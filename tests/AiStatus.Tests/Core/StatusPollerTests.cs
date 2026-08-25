using System.Collections.Concurrent;
using System.Collections.Immutable;
using AiStatus.Core;
using AiStatus.Model;
using AiStatus.Providers;
using AiStatus.Tests.Support;

namespace AiStatus.Tests.Core;

public sealed class StatusPollerTests : IDisposable
{
    private readonly List<TemporaryDirectory> _directories = [];

    [Fact]
    public async Task PollOnceAsync_StartsProvidersBeforeEitherCompletes()
    {
        // Break caught: awaiting each fetch while starting it serializes provider polling.
        FakeStatusProvider first = FakeStatusProvider.Blocking("first");
        FakeStatusProvider second = FakeStatusProvider.Blocking("second");
        StatusPoller poller = CreatePoller([first, second]);

        Task<StatusReport> poll = poller.PollOnceAsync(CancellationToken.None);
        await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(TimeSpan.FromSeconds(1));
        first.CompleteOk();
        second.CompleteOk();

        Assert.Equal(["first", "second"], (await poll).Providers.Select(provider => provider.Id));
    }

    [Fact]
    public async Task PollOnceAsync_DefaultTimeoutIsTenSeconds()
    {
        // Break caught: the production timeout drifts from ten seconds.
        var time = new RecordingTimeProvider();
        FakeStatusProvider provider = FakeStatusProvider.Blocking("slow");
        StatusPoller poller = CreatePoller([provider], timeProvider: time);

        Task<StatusReport> poll = poller.PollOnceAsync(CancellationToken.None);
        RecordingTimer timeout = await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        timeout.Fire();

        ProviderSnapshot snapshot = Assert.Single((await poll).Providers);
        Assert.Equal(HealthState.Unreachable, snapshot.Health);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task PollOnceAsync_UsesInjectedShortProviderTimeout()
    {
        // Break caught: provider calls can block past an injected timeout.
        FakeStatusProvider provider = FakeStatusProvider.Blocking("slow");
        StatusPoller poller = CreatePoller([provider], providerTimeout: TimeSpan.FromMilliseconds(20));

        ProviderSnapshot snapshot = Assert.Single(
            (await poller.PollOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1))).Providers);

        Assert.Equal(HealthState.Unreachable, snapshot.Health);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task PollOnceAsync_IncludesDisabledProviderWithoutInvokingIt()
    {
        // Break caught: disabling a provider removes its stable slot or still fetches it.
        FakeStatusProvider disabled = FakeStatusProvider.Blocking("claude", "Claude");
        AppSettings settings = Settings() with
        {
            Providers = AppSettings.Default.Providers.SetItem("claude", new(false)),
        };
        StatusPoller poller = CreatePoller([disabled], () => settings);

        ProviderSnapshot snapshot = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Equal("claude", snapshot.Id);
        Assert.Equal("Claude", snapshot.Label);
        Assert.Equal(HealthState.Disabled, snapshot.Health);
        Assert.Empty(snapshot.Windows);
        Assert.Empty(snapshot.Info);
        Assert.Equal(0, disabled.InvocationCount);
    }

    [Fact]
    public async Task PollOnceAsync_SuccessReplacesSnapshotAndResetsFailureCount()
    {
        // Break caught: recovery keeps retained data or a nonzero failure count.
        ProviderSnapshot old = FakeStatusProvider.Snapshot(
            "claude",
            planLabel: "old",
            info: [new InfoLine("model", "old")]);
        ProviderSnapshot replacement = FakeStatusProvider.Snapshot(
            "claude",
            planLabel: "new",
            info: [new InfoLine("model", "new")],
            consecutiveFailures: 19);
        FakeStatusProvider provider = FakeStatusProvider.Sequence(
            "claude",
            [
                _ => Task.FromResult(old),
                _ => Task.FromException<ProviderSnapshot>(new HttpRequestException()),
                _ => Task.FromResult(replacement),
            ]);
        StatusPoller poller = CreatePoller([provider]);
        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);

        ProviderSnapshot recovered = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Equal("new", recovered.PlanLabel);
        Assert.Equal("new", Assert.Single(recovered.Info).Value);
        Assert.Equal(0, recovered.ConsecutiveFailures);
    }

    [Fact]
    public async Task PollOnceAsync_FirstTwoFailuresRetainDataAndHealth()
    {
        // Break caught: a transient failure clears data or degrades health too early.
        ProviderSnapshot success = FakeStatusProvider.Snapshot(
            "claude",
            planLabel: "Pro",
            windows: [new UsageWindow("weekly", 42, null, Severity.Normal)],
            fetchedAt: DateTimeOffset.Parse("2026-08-25T11:00:00Z"));
        FakeStatusProvider provider = FakeStatusProvider.Sequence(
            "claude",
            [
                _ => Task.FromResult(success),
                _ => Task.FromException<ProviderSnapshot>(new HttpRequestException()),
                _ => Task.FromException<ProviderSnapshot>(new IOException()),
            ]);
        StatusPoller poller = CreatePoller([provider]);
        await poller.PollOnceAsync(CancellationToken.None);

        ProviderSnapshot firstFailure = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);
        ProviderSnapshot secondFailure = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(success, firstFailure, HealthState.Ok, 1);
        AssertRetained(success, secondFailure, HealthState.Ok, 2);
    }

    [Fact]
    public async Task PollOnceAsync_ThirdFailureDegradesRetainedSnapshot()
    {
        // Break caught: three consecutive failures do not change retained health to degraded.
        ProviderSnapshot success = FakeStatusProvider.Snapshot(
            "claude",
            health: HealthState.AuthExpired,
            planLabel: "Pro",
            info: [new InfoLine("account", "retained")],
            fetchedAt: DateTimeOffset.Parse("2026-08-25T11:00:00Z"));
        FakeStatusProvider provider = FakeStatusProvider.Sequence(
            "claude",
            [
                _ => Task.FromResult(success),
                _ => Task.FromException<ProviderSnapshot>(new IOException()),
                _ => Task.FromException<ProviderSnapshot>(new IOException()),
                _ => Task.FromException<ProviderSnapshot>(new IOException()),
            ]);
        StatusPoller poller = CreatePoller([provider]);
        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);

        ProviderSnapshot thirdFailure = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        AssertRetained(success, thirdFailure, HealthState.Degraded, 3);
    }

    [Theory]
    [InlineData(HealthState.Degraded)]
    [InlineData(HealthState.Unreachable)]
    public async Task PollOnceAsync_ProviderHealthIsSuccessfulFetchResult(HealthState health)
    {
        // Break caught: provider-reported degraded or unreachable health is mistaken for a thrown fetch failure.
        ProviderSnapshot returned = FakeStatusProvider.Snapshot(
            "ollama",
            health: health,
            error: "provider detail",
            consecutiveFailures: 8);
        StatusPoller poller = CreatePoller([FakeStatusProvider.Returning("ollama", returned)]);

        ProviderSnapshot snapshot = Assert.Single((await poller.PollOnceAsync(CancellationToken.None)).Providers);

        Assert.Equal(health, snapshot.Health);
        Assert.Equal("provider detail", snapshot.Error);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task PollOnceAsync_PropagatesCallerCancellation()
    {
        // Break caught: caller cancellation is converted into a retained provider failure.
        FakeStatusProvider provider = FakeStatusProvider.Blocking("claude");
        StatusPoller poller = CreatePoller([provider], providerTimeout: TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();

        Task<StatusReport> poll = poller.PollOnceAsync(cancellation.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Empty(poller.Current.Providers);
    }

    [Fact]
    public async Task RequestRefresh_WakesRunLoopBeforeTimerTick()
    {
        // Break caught: refresh requests wait for the scheduled cadence.
        var time = new RecordingTimeProvider();
        FakeStatusProvider provider = FakeStatusProvider.Returning("claude", FakeStatusProvider.Snapshot("claude"));
        StatusPoller poller = CreatePoller([provider], timeProvider: time);
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        poller.RequestRefresh();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, provider.InvocationCount);
    }

    [Fact]
    public async Task RequestRefresh_PerformsOnePoll()
    {
        // Break caught: a consumed auto-reset signal remains completed and drives a second poll.
        var time = new RecordingTimeProvider();
        var firstResult = new TaskCompletionSource<ProviderSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeStatusProvider provider = new(
            "claude",
            (invocation, cancellationToken) => invocation == 1
                ? firstResult.Task.WaitAsync(cancellationToken)
                : Task.FromException<ProviderSnapshot>(new InvalidOperationException("Unexpected second poll.")));
        StatusPoller poller = CreatePoller([provider], timeProvider: time);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        poller.ReportUpdated += (_, _) => updated.TrySetResult();
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        poller.RequestRefresh();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        firstResult.SetResult(FakeStatusProvider.Snapshot("claude"));
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, provider.InvocationCount);
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SetReducedCadence_RecreatesTimerUsingCurrentSettings()
    {
        // Break caught: cadence mode changes leave the old timer active or use stale settings.
        var time = new RecordingTimeProvider();
        AppSettings currentSettings = Settings();
        StatusPoller poller = CreatePoller([], () => currentSettings, time);
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        RecordingTimer original = await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        poller.SetReducedCadence(true);
        RecordingTimer reduced = await time.WaitForTimerAsync(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        Assert.True(original.IsDisposed);

        currentSettings = currentSettings with { PollInterval = TimeSpan.FromSeconds(30) };
        poller.SetReducedCadence(false);
        RecordingTimer restored = await time.WaitForTimerAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        Assert.True(reduced.IsDisposed);

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(restored.IsDisposed);
    }

    [Fact]
    public async Task RunAsync_RecreatesTimerWhenSettingsChangeAfterTick()
    {
        // Break caught: a settings update is ignored after the current timer wakes the loop.
        var time = new RecordingTimeProvider();
        AppSettings currentSettings = Settings();
        StatusPoller poller = CreatePoller([], () => currentSettings, time);
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        RecordingTimer original = await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        currentSettings = currentSettings with { PollInterval = TimeSpan.FromSeconds(20) };
        original.Fire();
        RecordingTimer changed = await time.WaitForTimerAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

        Assert.True(original.IsDisposed);
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(changed.IsDisposed);
    }

    [Fact]
    public async Task PollOnceAsync_DispatchesReportUpdatedOnCapturedSynchronizationContext()
    {
        // Break caught: report events run inline on a worker instead of the constructor's UI context.
        var context = new QueuedSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        StatusPoller poller;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            poller = CreatePoller([
                FakeStatusProvider.Returning("claude", FakeStatusProvider.Snapshot("claude")),
            ]);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        StatusReport? observed = null;
        poller.ReportUpdated += (_, report) => observed = report;
        StatusReport result = await Task.Run(() => poller.PollOnceAsync(CancellationToken.None));

        Assert.Null(observed);
        Assert.Same(result, poller.Current);
        Assert.Equal(1, context.PendingCount);
        context.RunOne();
        Assert.Same(result, observed);
    }

    [Fact]
    public async Task RunAsync_CancellationStopsActivePollAndDisposesTimer()
    {
        // Break caught: shutdown leaves a cadence timer or provider wait abandoned.
        var time = new RecordingTimeProvider();
        FakeStatusProvider provider = FakeStatusProvider.Blocking("claude");
        StatusPoller poller = CreatePoller([provider], timeProvider: time);
        using var cancellation = new CancellationTokenSource();
        Task run = poller.RunAsync(cancellation.Token);
        RecordingTimer cadence = await time.WaitForTimerAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        poller.RequestRefresh();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(cadence.IsDisposed);
        Assert.False(time.HasActiveTimer(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan));
    }

    private StatusPoller CreatePoller(
        IReadOnlyList<IStatusProvider> providers,
        Func<AppSettings>? settings = null,
        TimeProvider? timeProvider = null,
        TimeSpan? providerTimeout = null)
    {
        var directory = new TemporaryDirectory();
        _directories.Add(directory);
        return new StatusPoller(
            providers,
            settings ?? Settings,
            new RollingFileLog(Path.Combine(directory.Path, "poller.log")),
            timeProvider,
            providerTimeout);
    }

    private static AppSettings Settings() => AppSettings.Default;

    private static void AssertRetained(
        ProviderSnapshot expected,
        ProviderSnapshot actual,
        HealthState expectedHealth,
        int expectedFailures)
    {
        Assert.Equal(expected.PlanLabel, actual.PlanLabel);
        Assert.Equal(expected.Windows, actual.Windows);
        Assert.Equal(expected.Info, actual.Info);
        Assert.Equal(expected.FetchedAt, actual.FetchedAt);
        Assert.Equal(expectedHealth, actual.Health);
        Assert.Equal(expectedFailures, actual.ConsecutiveFailures);
    }

    public void Dispose()
    {
        foreach (TemporaryDirectory directory in _directories)
        {
            directory.Dispose();
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public int PendingCount => _callbacks.Count;

        public override void Post(SendOrPostCallback d, object? state) => _callbacks.Enqueue((d, state));

        public void RunOne()
        {
            Assert.True(_callbacks.TryDequeue(out var callback));
            callback.Callback(callback.State);
        }
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<RecordingTimer> _timers = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new RecordingTimer(callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public async Task<RecordingTimer> WaitForTimerAsync(TimeSpan dueTime, TimeSpan period)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            while (true)
            {
                lock (_gate)
                {
                    RecordingTimer? timer = _timers.LastOrDefault(candidate =>
                        !candidate.IsDisposed && candidate.DueTime == dueTime && candidate.Period == period);
                    if (timer is not null)
                    {
                        return timer;
                    }
                }

                await Task.Delay(1, timeout.Token);
            }
        }

        public bool HasActiveTimer(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                return _timers.Any(timer =>
                    !timer.IsDisposed && timer.DueTime == dueTime && timer.Period == period);
            }
        }
    }

    private sealed class RecordingTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private int _disposed;

        public TimeSpan DueTime { get; private set; } = dueTime;

        public TimeSpan Period { get; private set; } = period;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (IsDisposed)
            {
                return false;
            }

            DueTime = dueTime;
            Period = period;
            return true;
        }

        public void Fire()
        {
            if (!IsDisposed)
            {
                callback(state);
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
