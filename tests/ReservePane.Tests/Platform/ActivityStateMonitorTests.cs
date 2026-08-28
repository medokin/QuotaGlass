using ReservePane.Core;
using ReservePane.Platform;

namespace ReservePane.Tests.Platform;

public sealed class ActivityStateMonitorTests
{
    [Fact]
    public void IsReducedCadence_IsTrueWhileSessionIsLocked()
    {
        var events = new FakeActivityEventSource();
        var state = new FakeActivityState { IsOnBattery = false, IdleDuration = TimeSpan.Zero };
        using var monitor = new ActivityStateMonitor(events, state, new FakeActivitySampler());

        events.RaiseSessionLockChanged(true);

        Assert.True(monitor.IsReducedCadence);
    }

    [Theory]
    [InlineData(true, 299, false)]
    [InlineData(true, 300, true)]
    [InlineData(false, 300, false)]
    public void IsReducedCadence_RequiresOfflineBatteryAndAtLeastFiveMinutesIdle(
        bool isOnBattery,
        int idleSeconds,
        bool expected)
    {
        var state = new FakeActivityState
        {
            IsOnBattery = isOnBattery,
            IdleDuration = TimeSpan.FromSeconds(idleSeconds),
        };
        using var monitor = new ActivityStateMonitor(
            new FakeActivityEventSource(),
            state,
            new FakeActivitySampler());

        Assert.Equal(expected, monitor.IsReducedCadence);
    }

    [Fact]
    public void SessionUnlock_ReevaluatesBatteryAndIdleState()
    {
        var events = new FakeActivityEventSource();
        var state = new FakeActivityState { IsOnBattery = false, IdleDuration = TimeSpan.Zero };
        using var monitor = new ActivityStateMonitor(events, state, new FakeActivitySampler());
        events.RaiseSessionLockChanged(true);

        events.RaiseSessionLockChanged(false);

        Assert.False(monitor.IsReducedCadence);
    }

    [Fact]
    public void Sampler_UnpluggedActiveToIdleThresholdRaisesChangedOnce()
    {
        var events = new FakeActivityEventSource();
        var state = new FakeActivityState
        {
            IsOnBattery = true,
            IdleDuration = TimeSpan.FromMinutes(4),
        };
        var sampler = new FakeActivitySampler();
        using var monitor = new ActivityStateMonitor(events, state, sampler);
        int changes = 0;
        monitor.Changed += (_, _) => changes++;

        state.IdleDuration = TimeSpan.FromMinutes(5);
        sampler.RaiseSampleRequested();
        sampler.RaiseSampleRequested();

        Assert.Equal(1, changes);
        Assert.True(monitor.IsReducedCadence);
    }

    [Fact]
    public void Sampler_UserInputEndsReducedCadenceAndRaisesChangedOnce()
    {
        var state = new FakeActivityState
        {
            IsOnBattery = true,
            IdleDuration = TimeSpan.FromMinutes(5),
        };
        var sampler = new FakeActivitySampler();
        using var monitor = new ActivityStateMonitor(
            new FakeActivityEventSource(),
            state,
            sampler);
        int changes = 0;
        monitor.Changed += (_, _) => changes++;

        state.IdleDuration = TimeSpan.Zero;
        sampler.RaiseSampleRequested();
        sampler.RaiseSampleRequested();

        Assert.Equal(1, changes);
        Assert.False(monitor.IsReducedCadence);
    }

    [Fact]
    public void LockAndUnlock_RaiseChangedOnlyOnStateTransitions()
    {
        var events = new FakeActivityEventSource();
        using var monitor = new ActivityStateMonitor(
            events,
            new FakeActivityState(),
            new FakeActivitySampler());
        int changes = 0;
        monitor.Changed += (_, _) => changes++;

        events.RaiseSessionLockChanged(true);
        events.RaiseSessionLockChanged(true);
        events.RaiseSessionLockChanged(false);
        events.RaiseSessionLockChanged(false);

        Assert.Equal(2, changes);
        Assert.False(monitor.IsReducedCadence);
    }

    [Fact]
    public void PowerModeChange_RaisesChangedOnlyWhenCadenceStateChanges()
    {
        var events = new FakeActivityEventSource();
        var state = new FakeActivityState
        {
            IsOnBattery = false,
            IdleDuration = TimeSpan.FromMinutes(5),
        };
        using var monitor = new ActivityStateMonitor(
            events,
            state,
            new FakeActivitySampler());
        int changes = 0;
        monitor.Changed += (_, _) => changes++;

        events.RaisePowerModeChanged();
        state.IsOnBattery = true;
        events.RaisePowerModeChanged();
        events.RaisePowerModeChanged();

        Assert.Equal(1, changes);
        Assert.True(monitor.IsReducedCadence);
    }

    [Fact]
    public void ProductionSamplingInterval_IsNoLongerThanThirtySeconds()
    {
        Assert.True(PeriodicActivitySampler.SamplingInterval <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void CadenceSubscription_ReturnsCurrentVersionAndPublishesOnlyNewerSnapshots()
    {
        // Break caught: startup subscribes after reading state and misses a lock transition in that gap.
        var events = new FakeActivityEventSource();
        using var monitor = new ActivityStateMonitor(
            events,
            new FakeActivityState(),
            new FakeActivitySampler());
        events.RaiseSessionLockChanged(true);
        var source = (IActivityCadenceSource)monitor;
        var observed = new List<ActivityCadenceSnapshot>();
        EventHandler handler = (_, _) => observed.Add(source.Current);

        ActivityCadenceSnapshot initial = source.Subscribe(handler);
        events.RaiseSessionLockChanged(false);
        source.Unsubscribe(handler);
        events.RaiseSessionLockChanged(true);

        Assert.Equal(new ActivityCadenceSnapshot(1, true), initial);
        Assert.Equal([new ActivityCadenceSnapshot(2, false)], observed);
    }

    [Fact]
    public void Dispose_UnsubscribesEventsAndDisposesSource()
    {
        var events = new FakeActivityEventSource();
        var sampler = new FakeActivitySampler();
        var monitor = new ActivityStateMonitor(events, new FakeActivityState(), sampler);

        monitor.Dispose();

        Assert.Equal(0, events.SessionSubscriberCount);
        Assert.Equal(0, events.PowerSubscriberCount);
        Assert.Equal(0, sampler.SubscriberCount);
        Assert.True(events.IsDisposed);
        Assert.True(sampler.IsDisposed);
    }

    [Fact]
    public async Task Dispose_WaitsForInFlightChangedHandlerAndPreventsLaterNotification()
    {
        var events = new FakeActivityEventSource();
        var state = new FakeActivityState
        {
            IsOnBattery = true,
            IdleDuration = TimeSpan.FromMinutes(4),
        };
        var sampler = new FakeActivitySampler();
        var monitor = new ActivityStateMonitor(events, state, sampler);
        using var handlerEntered = new ManualResetEventSlim();
        using var releaseHandler = new ManualResetEventSlim();
        int notifications = 0;
        monitor.Changed += (_, _) =>
        {
            Interlocked.Increment(ref notifications);
            handlerEntered.Set();
            releaseHandler.Wait();
        };

        state.IdleDuration = TimeSpan.FromMinutes(5);
        Task sample = Task.Run(sampler.RaiseSampleRequested);
        Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(2)));

        Task dispose = Task.Factory.StartNew(
            monitor.Dispose,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(sampler.DisposedSignal.Wait(TimeSpan.FromSeconds(2)));
        bool disposeReturnedBeforeHandlerCompleted = dispose.IsCompleted;

        releaseHandler.Set();
        await Task.WhenAll(sample, dispose).WaitAsync(TimeSpan.FromSeconds(2));
        state.IdleDuration = TimeSpan.Zero;
        sampler.RaiseSampleRequested();

        Assert.False(disposeReturnedBeforeHandlerCompleted);
        Assert.Equal(1, Volatile.Read(ref notifications));
    }

    [Fact]
    public async Task Sample_TransitionThatLosesRaceToDisposeDoesNotInvokeChanged()
    {
        var events = new FakeActivityEventSource();
        var state = new BlockingActivityState
        {
            IsOnBattery = true,
            IdleDuration = TimeSpan.FromMinutes(4),
        };
        var sampler = new FakeActivitySampler();
        var monitor = new ActivityStateMonitor(events, state, sampler);
        int notifications = 0;
        monitor.Changed += (_, _) => Interlocked.Increment(ref notifications);

        state.IdleDuration = TimeSpan.FromMinutes(5);
        state.BlockNextIdleRead();
        Task sample = Task.Run(sampler.RaiseSampleRequested);
        Assert.True(state.ReadStarted.Wait(TimeSpan.FromSeconds(2)));

        Task dispose = Task.Factory.StartNew(
            monitor.Dispose,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        bool disposalWonRace = sampler.DisposedSignal.Wait(TimeSpan.FromSeconds(2));
        state.ReleaseRead.Set();

        await Task.WhenAll(sample, dispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(disposalWonRace);
        Assert.Equal(0, Volatile.Read(ref notifications));
    }

    [Fact]
    public async Task ChangedHandler_CanDisposeWithoutDeadlockOrLaterNotification()
    {
        var events = new FakeActivityEventSource();
        var state = new FakeActivityState
        {
            IsOnBattery = true,
            IdleDuration = TimeSpan.FromMinutes(4),
        };
        var sampler = new FakeActivitySampler();
        var monitor = new ActivityStateMonitor(events, state, sampler);
        int notifications = 0;
        monitor.Changed += (_, _) =>
        {
            Interlocked.Increment(ref notifications);
            monitor.Dispose();
        };

        state.IdleDuration = TimeSpan.FromMinutes(5);
        await Task.Run(sampler.RaiseSampleRequested).WaitAsync(TimeSpan.FromSeconds(2));
        state.IdleDuration = TimeSpan.Zero;
        sampler.RaiseSampleRequested();

        Assert.Equal(1, Volatile.Read(ref notifications));
        Assert.True(events.IsDisposed);
        Assert.True(sampler.IsDisposed);
    }

    [Fact]
    public async Task ChangedHandler_DisposeStopsRemainingSubscribersAndLaterNotifications()
    {
        var events = new FakeActivityEventSource();
        var state = new FakeActivityState
        {
            IsOnBattery = true,
            IdleDuration = TimeSpan.FromMinutes(4),
        };
        var sampler = new FakeActivitySampler();
        var monitor = new ActivityStateMonitor(events, state, sampler);
        int firstSubscriberCalls = 0;
        int secondSubscriberCalls = 0;
        monitor.Changed += (_, _) =>
        {
            Interlocked.Increment(ref firstSubscriberCalls);
            monitor.Dispose();
        };
        monitor.Changed += (_, _) => Interlocked.Increment(ref secondSubscriberCalls);

        state.IdleDuration = TimeSpan.FromMinutes(5);
        await Task.Run(sampler.RaiseSampleRequested).WaitAsync(TimeSpan.FromSeconds(2));
        state.IdleDuration = TimeSpan.Zero;
        sampler.RaiseSampleRequested();

        Assert.Equal(1, Volatile.Read(ref firstSubscriberCalls));
        Assert.Equal(0, Volatile.Read(ref secondSubscriberCalls));
        Assert.True(events.IsDisposed);
        Assert.True(sampler.IsDisposed);
    }

    private sealed class FakeActivitySampler : IActivitySampler
    {
        private Action? _sampleRequested;

        public int SubscriberCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public ManualResetEventSlim DisposedSignal { get; } = new();

        public event Action? SampleRequested
        {
            add { _sampleRequested += value; SubscriberCount++; }
            remove { _sampleRequested -= value; SubscriberCount--; }
        }

        public void RaiseSampleRequested() => _sampleRequested?.Invoke();
        public void Dispose()
        {
            IsDisposed = true;
            DisposedSignal.Set();
        }
    }

    private sealed class FakeActivityState : IActivityState
    {
        public bool IsOnBattery { get; set; }
        public TimeSpan IdleDuration { get; set; }
    }

    private sealed class BlockingActivityState : IActivityState
    {
        private int _blockNextRead;
        private TimeSpan _idleDuration;

        public bool IsOnBattery { get; set; }
        public TimeSpan IdleDuration
        {
            get
            {
                if (Interlocked.Exchange(ref _blockNextRead, 0) == 1)
                {
                    ReadStarted.Set();
                    ReleaseRead.Wait();
                }

                return _idleDuration;
            }
            set => _idleDuration = value;
        }

        public ManualResetEventSlim ReadStarted { get; } = new();
        public ManualResetEventSlim ReleaseRead { get; } = new();

        public void BlockNextIdleRead() => Volatile.Write(ref _blockNextRead, 1);
    }

    private sealed class FakeActivityEventSource : IActivityEventSource
    {
        private Action<bool>? _sessionLockChanged;
        private Action? _powerModeChanged;

        public int SessionSubscriberCount { get; private set; }
        public int PowerSubscriberCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public event Action<bool>? SessionLockChanged
        {
            add { _sessionLockChanged += value; SessionSubscriberCount++; }
            remove { _sessionLockChanged -= value; SessionSubscriberCount--; }
        }

        public event Action? PowerModeChanged
        {
            add { _powerModeChanged += value; PowerSubscriberCount++; }
            remove { _powerModeChanged -= value; PowerSubscriberCount--; }
        }

        public void RaiseSessionLockChanged(bool locked) => _sessionLockChanged?.Invoke(locked);
        public void RaisePowerModeChanged() => _powerModeChanged?.Invoke();
        public void Dispose() => IsDisposed = true;
    }
}
