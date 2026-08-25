using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AiStatus.Platform;

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

public sealed class ActivityStateMonitor : IDisposable
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);
    private readonly IActivityEventSource _events;
    private readonly IActivityState _state;
    private bool _isLocked;
    private bool _disposed;

    public ActivityStateMonitor()
        : this(new SystemActivityEventSource(), new WindowsActivityState())
    {
    }

    internal ActivityStateMonitor(IActivityEventSource events, IActivityState state)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(state);

        _events = events;
        _state = state;
        _events.SessionLockChanged += OnSessionLockChanged;
        _events.PowerModeChanged += OnPowerModeChanged;
    }

    public event EventHandler? Changed;

    public bool IsReducedCadence =>
        _isLocked || (_state.IsOnBattery && _state.IdleDuration >= IdleThreshold);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _events.SessionLockChanged -= OnSessionLockChanged;
        _events.PowerModeChanged -= OnPowerModeChanged;
        _events.Dispose();
    }

    private void OnSessionLockChanged(bool locked)
    {
        _isLocked = locked;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPowerModeChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
