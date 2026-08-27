using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Threading.Channels;
using QuotaGlass.Model;
using QuotaGlass.Providers;

namespace QuotaGlass.Core;

public sealed class StatusPoller : IActivityCadencePoller
{
    private static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(10);
    private readonly IReadOnlyList<IStatusProvider> _providers;
    private readonly Func<AppSettings> _settings;
    private readonly RollingFileLog _log;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _providerTimeout;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _cadenceGate = new();
    private readonly object _notificationGate = new();
    private readonly Queue<StatusReport> _notificationQueue = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.Ordinal);
    private readonly Channel<bool> _refreshRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private StatusReport _current;
    private Task _notificationPump = Task.CompletedTask;
    private bool _notificationPumpRunning;
    private int _reducedCadence;
    private int _runActive;

    public StatusPoller(
        IReadOnlyList<IStatusProvider> providers,
        Func<AppSettings> settings,
        RollingFileLog log,
        TimeProvider? timeProvider = null,
        TimeSpan? providerTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _providers = providers;
        _settings = settings;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _providerTimeout = providerTimeout ?? DefaultProviderTimeout;
        _synchronizationContext = SynchronizationContext.Current;
        _current = StatusReport.Empty(_timeProvider.GetUtcNow());
    }

    public StatusReport Current => Volatile.Read(ref _current);

    public event EventHandler<StatusReport>? ReportUpdated;

    public async Task<StatusReport> PollOnceAsync(CancellationToken cancellationToken)
    {
        StatusReport report;
        TaskCompletionSource? notificationPump;
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings settings = _settings();
            StatusReport previous = Current;
            Dictionary<string, ProviderSnapshot> previousById = previous.Providers
                .GroupBy(snapshot => snapshot.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            Task<ProviderSnapshot>[] fetches = _providers
                .Select(provider => IsEnabled(settings, provider.Id)
                    ? FetchOrRetainProviderAsync(provider, previousById.GetValueOrDefault(provider.Id), cancellationToken)
                    : Task.FromResult(CreateDisabledSnapshot(provider)))
                .ToArray();

            ProviderSnapshot[] providers = await Task.WhenAll(fetches).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            report = new StatusReport(_timeProvider.GetUtcNow(), providers.ToImmutableArray());
            Volatile.Write(ref _current, report);
            notificationPump = EnqueueReportUpdated(report);
        }
        finally
        {
            _pollGate.Release();
        }

        if (notificationPump is not null)
        {
            StartNotificationPump(notificationPump);
        }

        return report;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_cadenceGate)
        {
            if (_runActive != 0)
            {
                throw new InvalidOperationException("The status poller is already running.");
            }

            Volatile.Write(ref _runActive, 1);
        }

        try
        {
            // The loop owns cancellation so it always reaches notification drain and cleanup.
            await Task.Run(
                () => RunLoopAsync(cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_cadenceGate)
            {
                Volatile.Write(ref _runActive, 0);
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        PeriodicTimer? timer = null;
        try
        {
            TimeSpan cadence = GetCadence();
            timer = new PeriodicTimer(cadence, _timeProvider);

            while (true)
            {
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<bool> tick = timer.WaitForNextTickAsync(waitCancellation.Token).AsTask();
                Task refresh = WaitForRefreshAsync(waitCancellation.Token);
                Task completed = await Task.WhenAny(tick, refresh).ConfigureAwait(false);

                waitCancellation.Cancel();
                await Task.WhenAll(
                    ObserveCancellationAsync(tick),
                    ObserveCancellationAsync(refresh)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (completed == tick && !await tick.ConfigureAwait(false))
                {
                    break;
                }

                await PollOnceAsync(cancellationToken).ConfigureAwait(false);

                TimeSpan nextCadence = GetCadence();
                if (nextCadence != cadence)
                {
                    timer.Dispose();
                    cadence = nextCadence;
                    timer = new PeriodicTimer(cadence, _timeProvider);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            timer?.Dispose();
            await GetNotificationPump().ConfigureAwait(false);
        }
    }

    public void RequestRefresh()
    {
        _refreshRequests.Writer.TryWrite(true);
    }

    public void SetReducedCadence(bool reduced)
    {
        int value = reduced ? 1 : 0;
        lock (_cadenceGate)
        {
            if (Interlocked.Exchange(ref _reducedCadence, value) != value && _runActive != 0)
            {
                RequestRefresh();
            }
        }
    }

    private static bool IsEnabled(AppSettings settings, string providerId) =>
        !settings.Providers.TryGetValue(providerId, out ProviderSettings? providerSettings)
        || providerSettings.Enabled;

    private ProviderSnapshot CreateDisabledSnapshot(IStatusProvider provider)
    {
        _cooldowns.TryRemove(provider.Id, out _);
        _log.Write(LogArea.Provider, LogOutcome.Disabled);
        return new ProviderSnapshot(
            provider.Id,
            provider.Label,
            HealthState.Disabled,
            null,
            ImmutableArray<UsageWindow>.Empty,
            ImmutableArray<InfoLine>.Empty,
            null,
            _timeProvider.GetUtcNow(),
            0);
    }

    private Task<ProviderSnapshot> FetchOrRetainProviderAsync(
        IStatusProvider provider,
        ProviderSnapshot? previous,
        CancellationToken cancellationToken)
    {
        if (_cooldowns.TryGetValue(provider.Id, out DateTimeOffset cooldownUntil) &&
            cooldownUntil > _timeProvider.GetUtcNow())
        {
            return Task.FromResult(RetainCooldown(provider, previous));
        }

        return FetchProviderAsync(provider, previous, cancellationToken);
    }

    private async Task<ProviderSnapshot> FetchProviderAsync(
        IStatusProvider provider,
        ProviderSnapshot? previous,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_providerTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            ProviderFetchResult result = await provider.FetchAsync(linked.Token).ConfigureAwait(false);
            return ApplyResult(provider, previous, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            _cooldowns.TryRemove(provider.Id, out _);
            ProviderSnapshot retained = RetainFailure(provider, previous);
            _log.Write(
                LogArea.Provider,
                LogOutcome.TimedOut,
                exception: exception,
                providerId: provider.Id,
                providerOutcome: ProviderFetchOutcome.TransientFailure,
                consecutiveFailures: retained.ConsecutiveFailures);
            return retained;
        }
        catch (Exception exception)
        {
            _cooldowns.TryRemove(provider.Id, out _);
            ProviderSnapshot retained = RetainFailure(provider, previous);
            _log.Write(
                LogArea.Provider,
                LogOutcome.Failed,
                exception: exception,
                providerId: provider.Id,
                providerOutcome: ProviderFetchOutcome.TransientFailure,
                consecutiveFailures: retained.ConsecutiveFailures);
            return retained;
        }
    }

    private ProviderSnapshot ApplyResult(
        IStatusProvider provider,
        ProviderSnapshot? previous,
        ProviderFetchResult result)
    {
        ProviderSnapshot? snapshot = result.Snapshot;
        if (snapshot is not null && !string.Equals(snapshot.Id, provider.Id, StringComparison.Ordinal))
        {
            _cooldowns.TryRemove(provider.Id, out _);
            ProviderSnapshot retained = RetainFailure(provider, previous);
            LogProviderResult(
                provider,
                new ProviderFetchResult(ProviderFetchOutcome.InvalidResponse, statusCode: result.StatusCode),
                retained);
            return retained;
        }

        if (result.Outcome == ProviderFetchOutcome.RateLimited)
        {
            DateTimeOffset cooldownUntil = _timeProvider.GetUtcNow() + result.RetryAfter!.Value;
            _cooldowns[provider.Id] = cooldownUntil;
            ProviderSnapshot retained = RetainFailure(provider, previous);
            LogProviderResult(provider, result, retained);
            return retained;
        }

        _cooldowns.TryRemove(provider.Id, out _);
        switch (result.Outcome)
        {
            case ProviderFetchOutcome.Success:
            case ProviderFetchOutcome.PartialSuccess:
            case ProviderFetchOutcome.NotConfigured:
            case ProviderFetchOutcome.AuthenticationRequired:
                ProviderSnapshot published = snapshot! with { ConsecutiveFailures = 0 };
                LogProviderResult(provider, result, published);
                return published;
            case ProviderFetchOutcome.TransientFailure:
                ProviderSnapshot transient = RetainFailure(provider, previous);
                LogProviderResult(provider, result, transient);
                return transient;
            case ProviderFetchOutcome.InvalidResponse:
                ProviderSnapshot invalid = RetainFailure(provider, previous);
                LogProviderResult(provider, result, invalid);
                return invalid;
            default:
                throw new InvalidOperationException("Validated provider outcome was not mapped.");
        }
    }

    private void LogProviderResult(
        IStatusProvider provider,
        ProviderFetchResult result,
        ProviderSnapshot snapshot)
    {
        LogOutcome logOutcome = result.Outcome switch
        {
            ProviderFetchOutcome.Success => LogOutcome.Succeeded,
            ProviderFetchOutcome.PartialSuccess => LogOutcome.Degraded,
            ProviderFetchOutcome.NotConfigured => LogOutcome.Unreachable,
            ProviderFetchOutcome.AuthenticationRequired => LogOutcome.AuthExpired,
            ProviderFetchOutcome.TransientFailure => LogOutcome.Failed,
            ProviderFetchOutcome.RateLimited => LogOutcome.Failed,
            ProviderFetchOutcome.InvalidResponse => LogOutcome.Invalid,
            _ => throw new InvalidOperationException("Validated provider outcome was not mapped."),
        };
        int? cooldownSeconds = result.RetryAfter is TimeSpan retryAfter
            ? checked((int)Math.Ceiling(retryAfter.TotalSeconds))
            : null;
        _log.Write(
            LogArea.Provider,
            logOutcome,
            statusCode: (int?)result.StatusCode,
            providerId: provider.Id,
            providerOutcome: result.Outcome,
            cooldownSeconds: cooldownSeconds,
            consecutiveFailures: snapshot.ConsecutiveFailures);
    }

    private ProviderSnapshot RetainCooldown(IStatusProvider provider, ProviderSnapshot? previous) =>
        previous ?? new ProviderSnapshot(
            provider.Id,
            provider.Label,
            HealthState.Unreachable,
            null,
            ImmutableArray<UsageWindow>.Empty,
            ImmutableArray<InfoLine>.Empty,
            null,
            _timeProvider.GetUtcNow(),
            1);

    private ProviderSnapshot RetainFailure(IStatusProvider provider, ProviderSnapshot? previous)
    {
        int failures = checked((previous?.ConsecutiveFailures ?? 0) + 1);
        HealthState health = failures >= 3
            ? HealthState.Degraded
            : previous?.Health ?? HealthState.Unreachable;

        if (failures >= 3)
        {
            _log.Write(LogArea.Poller, LogOutcome.Degraded);
        }

        return previous is null
            ? new ProviderSnapshot(
                provider.Id,
                provider.Label,
                health,
                null,
                ImmutableArray<UsageWindow>.Empty,
                ImmutableArray<InfoLine>.Empty,
                null,
                _timeProvider.GetUtcNow(),
                failures)
            : previous with
            {
                Health = health,
                ConsecutiveFailures = failures,
            };
    }

    private TimeSpan GetCadence()
    {
        AppSettings settings = _settings();
        return Volatile.Read(ref _reducedCadence) != 0
            ? settings.IdleInterval
            : settings.PollInterval;
    }

    private async Task WaitForRefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshRequests.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private TaskCompletionSource? EnqueueReportUpdated(StatusReport report)
    {
        lock (_notificationGate)
        {
            _notificationQueue.Enqueue(report);
            if (_notificationPumpRunning)
            {
                return null;
            }

            _notificationPumpRunning = true;
            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _notificationPump = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                await DrainNotificationsAsync().ConfigureAwait(false);
            });
            return start;
        }
    }

    private static void StartNotificationPump(TaskCompletionSource start)
    {
        start.TrySetResult();
    }

    private async Task DrainNotificationsAsync()
    {
        try
        {
            while (TryDequeueNotification(out StatusReport report))
            {
                if (_synchronizationContext is null)
                {
                    InvokeReportUpdatedHandlers(report);
                }
                else
                {
                    await InvokeReportUpdatedHandlersOnContextAsync(report).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            LogHandlerFailure(exception);
        }
    }

    private bool TryDequeueNotification(out StatusReport report)
    {
        lock (_notificationGate)
        {
            if (_notificationQueue.Count > 0)
            {
                report = _notificationQueue.Dequeue();
                return true;
            }

            _notificationPumpRunning = false;
            report = null!;
            return false;
        }
    }

    private Task InvokeReportUpdatedHandlersOnContextAsync(StatusReport report)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _synchronizationContext!.Post(
                static state =>
                {
                    var dispatch = ((StatusPoller Poller, StatusReport Report, TaskCompletionSource Completion))state!;
                    try
                    {
                        dispatch.Poller.InvokeReportUpdatedHandlers(dispatch.Report);
                    }
                    catch (Exception exception)
                    {
                        dispatch.Poller.LogHandlerFailure(exception);
                    }
                    finally
                    {
                        dispatch.Completion.TrySetResult();
                    }
                },
                (this, report, completion));
        }
        catch (Exception exception)
        {
            LogHandlerFailure(exception);
            completion.TrySetResult();
        }

        return completion.Task;
    }

    private void InvokeReportUpdatedHandlers(StatusReport report)
    {
        EventHandler<StatusReport>? handlers = ReportUpdated;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<StatusReport> handler in handlers.GetInvocationList().Cast<EventHandler<StatusReport>>())
        {
            try
            {
                handler(this, report);
            }
            catch (Exception exception)
            {
                LogHandlerFailure(exception);
            }
        }
    }

    private void LogHandlerFailure(Exception exception)
    {
        try
        {
            _log.Write(LogArea.Ui, LogOutcome.Failed, exception: exception);
        }
        catch (Exception)
        {
        }
    }

    private Task GetNotificationPump()
    {
        lock (_notificationGate)
        {
            return _notificationPump;
        }
    }
}
