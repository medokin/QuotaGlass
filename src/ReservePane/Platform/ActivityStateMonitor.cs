using System.Runtime.InteropServices;
using ReservePane.Core;
using Microsoft.Win32;

namespace ReservePane.Platform;

internal interface IActivityState
{
    bool IsOnBattery { get; }
    TimeSpan IdleDuration { get; }
}

internal interface IActivityEventSource : IDisposable
{
    event Action<bool>? SessionLockChanged;
    event Action? PowerModeChanged;
}

internal interface IActivitySampler : IDisposable
{
    event Action? SampleRequested;
}

internal sealed class PeriodicActivitySampler : IActivitySampler
{
    internal static readonly TimeSpan SamplingInterval = TimeSpan.FromSeconds(30);
    private readonly System.Threading.Timer _timer;

    public PeriodicActivitySampler()
    {
        _timer = new System.Threading.Timer(
            static state => ((PeriodicActivitySampler)state!).SampleRequested?.Invoke(),
            this,
            SamplingInterval,
            SamplingInterval);
    }

    public event Action? SampleRequested;

    public void Dispose() => _timer.Dispose();
}

internal sealed class WindowsActivityState : IActivityState
{
    public bool IsOnBattery =>
        System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
        System.Windows.Forms.PowerLineStatus.Offline;

    public TimeSpan IdleDuration
    {
        get
        {
            var input = new LastInputInfo
            {
                Size = checked((uint)Marshal.SizeOf<LastInputInfo>()),
            };

            if (!NativeMethods.GetLastInputInfo(ref input))
            {
                return TimeSpan.Zero;
            }

            uint elapsedMilliseconds = unchecked(NativeMethods.GetTickCount() - input.Time);
            return TimeSpan.FromMilliseconds(elapsedMilliseconds);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLastInputInfo(ref LastInputInfo input);

        [DllImport("kernel32.dll")]
        internal static extern uint GetTickCount();
    }
}

internal sealed class SystemActivityEventSource : IActivityEventSource
{
    private bool _disposed;

    public SystemActivityEventSource()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public event Action<bool>? SessionLockChanged;
    public event Action? PowerModeChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason == SessionSwitchReason.SessionLock)
        {
            SessionLockChanged?.Invoke(true);
        }
        else if (args.Reason == SessionSwitchReason.SessionUnlock)
        {
            SessionLockChanged?.Invoke(false);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args) =>
        PowerModeChanged?.Invoke();
}

public sealed class ActivityStateMonitor : IDisposable, IActivityCadenceSource
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly object _notificationDrainGate = new();
    private readonly TaskCompletionSource _sourcesDisposed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IActivityEventSource _events;
    private readonly IActivityState _state;
    private readonly IActivitySampler _sampler;
    private bool _isLocked;
    private bool _isReducedCadence;
    private long _cadenceVersion;
    private bool _disposed;

    public ActivityStateMonitor()
        : this(
            new SystemActivityEventSource(),
            new WindowsActivityState(),
            new PeriodicActivitySampler())
    {
    }

    internal ActivityStateMonitor(
        IActivityEventSource events,
        IActivityState state,
        IActivitySampler sampler)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sampler);

        _events = events;
        _state = state;
        _sampler = sampler;
        _isReducedCadence = ComputeReducedCadence(isLocked: false);
        _events.SessionLockChanged += OnSessionLockChanged;
        _events.PowerModeChanged += OnPowerModeChanged;
        _sampler.SampleRequested += OnSampleRequested;
    }

    public event EventHandler? Changed;

    public bool IsReducedCadence
    {
        get
        {
            lock (_gate)
            {
                return _isReducedCadence;
            }
        }
    }

    ActivityCadenceSnapshot IActivityCadenceSource.Current
    {
        get
        {
            lock (_gate)
            {
                return new ActivityCadenceSnapshot(_cadenceVersion, _isReducedCadence);
            }
        }
    }

    ActivityCadenceSnapshot IActivityCadenceSource.Subscribe(EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Changed += handler;
            return new ActivityCadenceSnapshot(_cadenceVersion, _isReducedCadence);
        }
    }

    void IActivityCadenceSource.Unsubscribe(EventHandler handler)
    {
        lock (_gate)
        {
            Changed -= handler;
        }
    }

    public void Dispose()
    {
        bool ownsDisposal;
        lock (_gate)
        {
            ownsDisposal = !_disposed;
            _disposed = true;
        }

        if (ownsDisposal)
        {
            try
            {
                _events.SessionLockChanged -= OnSessionLockChanged;
                _events.PowerModeChanged -= OnPowerModeChanged;
                _sampler.SampleRequested -= OnSampleRequested;
                _sampler.Dispose();
                _events.Dispose();
            }
            finally
            {
                _sourcesDisposed.TrySetResult();
                DrainNotifications();
            }
        }
        else
        {
            _sourcesDisposed.Task.GetAwaiter().GetResult();
            DrainNotifications();
        }
    }

    private void OnSessionLockChanged(bool locked)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _isLocked = locked;
        }

        EvaluateAndNotify();
    }

    private void OnPowerModeChanged() => EvaluateAndNotify();

    private void OnSampleRequested() => EvaluateAndNotify();

    private void EvaluateAndNotify()
    {
        lock (_notificationDrainGate)
        {
            bool isLocked;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                isLocked = _isLocked;
            }

            bool reducedCadence = ComputeReducedCadence(isLocked);
            EventHandler? handlers;
            lock (_gate)
            {
                if (_disposed || reducedCadence == _isReducedCadence)
                {
                    return;
                }

                _isReducedCadence = reducedCadence;
                _cadenceVersion++;
                handlers = Changed;
            }

            if (handlers is null)
            {
                return;
            }

            foreach (Delegate subscriber in handlers.GetInvocationList())
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }

                ((EventHandler)subscriber)(this, EventArgs.Empty);
            }
        }
    }

    private void DrainNotifications()
    {
        // Monitor locks are reentrant. Dispose called by Changed returns safely while
        // the outer notification still owns this gate, preventing any later handler.
        lock (_notificationDrainGate)
        {
        }
    }

    private bool ComputeReducedCadence(bool isLocked) =>
        isLocked || (_state.IsOnBattery && _state.IdleDuration >= IdleThreshold);
}
