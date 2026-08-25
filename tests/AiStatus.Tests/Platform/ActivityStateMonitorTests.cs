using AiStatus.Platform;

namespace AiStatus.Tests.Platform;

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

    private sealed class FakeActivitySampler : IActivitySampler
    {
        private Action? _sampleRequested;

        public int SubscriberCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public event Action? SampleRequested
        {
            add { _sampleRequested += value; SubscriberCount++; }
            remove { _sampleRequested -= value; SubscriberCount--; }
        }

        public void RaiseSampleRequested() => _sampleRequested?.Invoke();
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeActivityState : IActivityState
    {
        public bool IsOnBattery { get; set; }
        public TimeSpan IdleDuration { get; set; }
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
