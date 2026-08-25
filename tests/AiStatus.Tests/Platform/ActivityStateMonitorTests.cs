using AiStatus.Platform;

namespace AiStatus.Tests.Platform;

public sealed class ActivityStateMonitorTests
{
    [Fact]
    public void IsReducedCadence_IsTrueWhileSessionIsLocked()
    {
        var events = new FakeActivityEventSource();
        var state = new FakeActivityState { IsOnBattery = false, IdleDuration = TimeSpan.Zero };
        using var monitor = new ActivityStateMonitor(events, state);

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
        using var monitor = new ActivityStateMonitor(new FakeActivityEventSource(), state);

        Assert.Equal(expected, monitor.IsReducedCadence);
    }

    [Fact]
    public void SessionUnlock_ReevaluatesBatteryAndIdleState()
    {
        var events = new FakeActivityEventSource();
        var state = new FakeActivityState { IsOnBattery = false, IdleDuration = TimeSpan.Zero };
        using var monitor = new ActivityStateMonitor(events, state);
        events.RaiseSessionLockChanged(true);

        events.RaiseSessionLockChanged(false);

        Assert.False(monitor.IsReducedCadence);
    }

    [Fact]
    public void PowerModeChange_RaisesChangedForCadenceConsumer()
    {
        var events = new FakeActivityEventSource();
        using var monitor = new ActivityStateMonitor(events, new FakeActivityState());
        int changes = 0;
        monitor.Changed += (_, _) => changes++;

        events.RaisePowerModeChanged();

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Dispose_UnsubscribesEventsAndDisposesSource()
    {
        var events = new FakeActivityEventSource();
        var monitor = new ActivityStateMonitor(events, new FakeActivityState());

        monitor.Dispose();

        Assert.Equal(0, events.SessionSubscriberCount);
        Assert.Equal(0, events.PowerSubscriberCount);
        Assert.True(events.IsDisposed);
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
