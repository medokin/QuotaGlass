using System.Collections.Immutable;
using AiStatus.Core;
using AiStatus.Model;
using AiStatus.Tests.Support;
using AiStatus.Ui;
using static AiStatus.Tests.Support.SnapshotFactory;

namespace AiStatus.Tests.Core;

public sealed class ApplicationCompositionTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public void SettingsReload_UpdatesStatefulServicesAndReregistersHotkeyTransactionally()
    {
        AppSettings initial = AppSettings.Default;
        var state = new AppSettingsState(initial);
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
        Assert.Single(watcher.Evaluate(Report(79, cycle), Report(80, cycle)));
        var originalHotkey = new FakeHotkeyRegistration(isRegistered: true);
        var rejectedHotkey = new FakeHotkeyRegistration(isRegistered: false);
        var acceptedHotkey = new FakeHotkeyRegistration(isRegistered: true);
        var created = new Queue<FakeHotkeyRegistration>([rejectedHotkey, acceptedHotkey]);
        var overlaySettings = new List<AppSettings>();
        int refreshes = 0;
        int toggles = 0;
        using var coordinator = new ApplicationSettingsCoordinator(
            state,
            watcher,
            originalHotkey,
            _ => created.Dequeue(),
            overlaySettings.Add,
            () => refreshes++,
            () =>
            {
                toggles++;
                return Task.CompletedTask;
            },
            CreateLog());

        AppSettings rejected = initial with { Hotkey = "Ctrl+Shift+Q" };
        coordinator.Apply(rejected);

        Assert.True(rejectedHotkey.Disposed);
        Assert.False(originalHotkey.Disposed);
        originalHotkey.RaisePressed();
        Assert.Equal(1, toggles);

        AppSettings accepted = rejected with
        {
            Hotkey = "Win+Shift+9",
            WarningPercent = 70,
            CriticalPercent = 90,
            PollInterval = TimeSpan.FromSeconds(45),
            Providers = rejected.Providers.SetItem("ollama", new ProviderSettings(false)),
            OverlayVisible = true,
            OverlayCorner = OverlayCorner.TopLeft,
        };
        coordinator.Apply(accepted);

        Assert.True(originalHotkey.Disposed);
        Assert.False(acceptedHotkey.Disposed);
        acceptedHotkey.RaisePressed();
        Assert.Equal(2, toggles);
        Assert.Equal(accepted, state.Current);
        Assert.Equal(1, refreshes);
        Assert.Equal(accepted, Assert.Single(overlaySettings));
        Assert.Equal(
            AlertKind.Critical,
            Assert.Single(watcher.Evaluate(Report(80, cycle), Report(91, cycle))).Kind);
    }

    [Fact]
    public void SettingsReload_EquivalentAppOwnedSaveDoesNotCreateFeedbackWork()
    {
        AppSettings initial = AppSettings.Default;
        var state = new AppSettingsState(initial);
        var hotkey = new FakeHotkeyRegistration(isRegistered: true);
        int factories = 0;
        int overlays = 0;
        int refreshes = 0;
        using var coordinator = new ApplicationSettingsCoordinator(
            state,
            new ThresholdWatcher(80, 95),
            hotkey,
            _ =>
            {
                factories++;
                return new FakeHotkeyRegistration(isRegistered: true);
            },
            _ => overlays++,
            () => refreshes++,
            () => Task.CompletedTask,
            CreateLog());
        AppSettings equivalentReload = initial with
        {
            Providers = initial.Providers.ToImmutableDictionary(),
        };

        coordinator.Apply(equivalentReload);

        Assert.Equal(0, factories);
        Assert.Equal(0, overlays);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public void SettingsReload_ThresholdOnlyChangeRequestsPromptRefresh()
    {
        AppSettings initial = AppSettings.Default;
        int refreshes = 0;
        using var coordinator = new ApplicationSettingsCoordinator(
            new AppSettingsState(initial),
            new ThresholdWatcher(80, 95),
            new FakeHotkeyRegistration(isRegistered: true),
            _ => new FakeHotkeyRegistration(isRegistered: true),
            _ => { },
            () => refreshes++,
            () => Task.CompletedTask,
            CreateLog());

        coordinator.Apply(initial with { WarningPercent = 75 });

        Assert.Equal(1, refreshes);
    }

    [Fact]
    public void SettingsReload_OverlayFailureDoesNotBlockCadenceRefresh()
    {
        AppSettings initial = AppSettings.Default;
        int refreshes = 0;
        using var coordinator = new ApplicationSettingsCoordinator(
            new AppSettingsState(initial),
            new ThresholdWatcher(80, 95),
            new FakeHotkeyRegistration(isRegistered: true),
            _ => new FakeHotkeyRegistration(isRegistered: true),
            _ => throw new InvalidOperationException("Synthetic placement failure."),
            () => refreshes++,
            () => Task.CompletedTask,
            CreateLog());

        coordinator.Apply(initial with
        {
            OverlayVisible = true,
            PollInterval = TimeSpan.FromSeconds(30),
        });

        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task Shutdown_CancelsAndAwaitsPollLoopBeforeUiDisposalAndApplicationShutdown()
    {
        using var cancellation = new CancellationTokenSource();
        var pollReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new QueuedDispatcher();
        var events = new List<string>();
        Task pollLoop = WaitForCancellationAndReleaseAsync();
        var coordinator = new ApplicationShutdownCoordinator(
            cancellation,
            () => pollLoop,
            dispatcher,
            () => events.Add("dispose"),
            () => events.Add("shutdown"),
            CreateLog());

        Task first = coordinator.ShutdownAsync();
        Task second = coordinator.ShutdownAsync();
        await Task.Delay(20);

        Assert.Same(first, second);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(events);
        Assert.Equal(0, dispatcher.PendingCount);

        pollReleased.TrySetResult();
        await dispatcher.WaitForPendingAsync();
        Assert.False(first.IsCompleted);
        dispatcher.RunNext();
        await first;

        Assert.Equal(["poll", "dispose", "shutdown"], events);

        async Task WaitForCancellationAndReleaseAsync()
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token)
                .ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await pollReleased.Task;
            events.Add("poll");
        }
    }

    [Fact]
    public async Task PollLoopFault_IsObservedAndTriggersExactlyOneOrderlyShutdown()
    {
        // Break caught: a later poll-loop fault remains unobserved while the tray silently goes stale.
        using var cancellation = new CancellationTokenSource();
        var pollCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new QueuedDispatcher();
        int disposals = 0;
        int shutdowns = 0;
        string logPath = Path.Combine(_directory.Path, $"poll-fault-{Guid.NewGuid():N}.log");
        var log = new RollingFileLog(logPath);
        var coordinator = new ApplicationShutdownCoordinator(
            cancellation,
            () => pollCompletion.Task,
            dispatcher,
            () => disposals++,
            () => shutdowns++,
            log);
        var observer = new PollLoopFaultObserver(coordinator.ShutdownAsync, log);

        Task observation = observer.ObserveAsync(pollCompletion.Task, cancellation.Token);
        pollCompletion.SetException(new InvalidOperationException("token=secret account=user@example.test"));

        await dispatcher.WaitForPendingAsync();
        dispatcher.RunNext();
        await observation;

        Assert.True(pollCompletion.Task.IsFaulted);
        Assert.True(observation.IsCompletedSuccessfully);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, disposals);
        Assert.Equal(1, shutdowns);
        string[] failureLines = File.ReadAllLines(logPath)
            .Where(line => line.Contains("application failed", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(failureLines);
        Assert.Contains("exception=InvalidOperationException", failureLines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failureLines[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", failureLines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollLoopCancellation_DoesNotLogOrRequestRecursiveShutdown()
    {
        // Break caught: normal application cancellation is treated as a poll failure and starts shutdown twice.
        using var cancellation = new CancellationTokenSource();
        var pollCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int shutdowns = 0;
        string logPath = Path.Combine(_directory.Path, $"poll-cancel-{Guid.NewGuid():N}.log");
        var observer = new PollLoopFaultObserver(
            () =>
            {
                shutdowns++;
                return Task.CompletedTask;
            },
            new RollingFileLog(logPath));

        Task observation = observer.ObserveAsync(pollCompletion.Task, cancellation.Token);
        cancellation.Cancel();
        pollCompletion.SetCanceled(cancellation.Token);
        await observation;

        Assert.True(observation.IsCompletedSuccessfully);
        Assert.Equal(0, shutdowns);
        Assert.False(File.Exists(logPath));
    }

    [Fact]
    public void ShutdownFallback_IsSynchronousIdempotentAndDoesNotRequestShutdownAgain()
    {
        using var cancellation = new CancellationTokenSource();
        int disposals = 0;
        int shutdowns = 0;
        var coordinator = new ApplicationShutdownCoordinator(
            cancellation,
            () => Task.CompletedTask,
            new QueuedDispatcher(),
            () => disposals++,
            () => shutdowns++,
            CreateLog());

        coordinator.ShutdownFallback();
        coordinator.ShutdownFallback();

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, disposals);
        Assert.Equal(0, shutdowns);
    }

    public void Dispose() => _directory.Dispose();

    private RollingFileLog CreateLog() =>
        new(Path.Combine(_directory.Path, $"composition-{Guid.NewGuid():N}.log"));

    private sealed class FakeHotkeyRegistration(bool isRegistered) : IHotkeyRegistration
    {
        public event EventHandler? Pressed;
        public bool IsRegistered { get; } = isRegistered;
        public bool Disposed { get; private set; }
        public void RaisePressed() => Pressed?.Invoke(this, EventArgs.Empty);
        public void Dispose() => Disposed = true;
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<(Action Action, TaskCompletionSource Completion)> _pending = [];
        private readonly SemaphoreSlim _available = new(0);
        public int PendingCount => _pending.Count;
        public void Post(Action action)
        {
            _pending.Enqueue((action, new TaskCompletionSource()));
            _available.Release();
        }

        public Task InvokeAsync(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((action, completion));
            _available.Release();
            return completion.Task;
        }

        public async Task WaitForPendingAsync() =>
            await _available.WaitAsync(TimeSpan.FromSeconds(2));

        public void RunNext()
        {
            (Action action, TaskCompletionSource completion) = _pending.Dequeue();
            action();
            completion.TrySetResult();
        }
    }
}
