using System.Collections.Immutable;
using System.Threading.Channels;
using AiStatus.Model;
using AiStatus.Providers;

namespace AiStatus.Core;

public sealed class StatusPoller
{
    private static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(10);
    private readonly IReadOnlyList<IStatusProvider> _providers;
    private readonly Func<AppSettings> _settings;
    private readonly RollingFileLog _log;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _providerTimeout;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly Channel<bool> _refreshRequests = Channel.CreateUnbounded<bool>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private StatusReport _current;
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
        await _pollGate.WaitAsync(cancellationToken);
        try
        {
            AppSettings settings = _settings();
            StatusReport previous = Current;
            Dictionary<string, ProviderSnapshot> previousById = previous.Providers
                .GroupBy(snapshot => snapshot.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            Task<ProviderSnapshot>[] fetches = _providers
                .Select(provider => IsEnabled(settings, provider.Id)
                    ? FetchProviderAsync(provider, previousById.GetValueOrDefault(provider.Id), cancellationToken)
                    : Task.FromResult(CreateDisabledSnapshot(provider)))
                .ToArray();

            ProviderSnapshot[] providers = await Task.WhenAll(fetches);
            cancellationToken.ThrowIfCancellationRequested();

            var report = new StatusReport(_timeProvider.GetUtcNow(), providers.ToImmutableArray());
            Volatile.Write(ref _current, report);
            DispatchReportUpdated(report);
            return report;
        }
        finally
        {
            _pollGate.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _runActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("The status poller is already running.");
        }

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
                Task completed = await Task.WhenAny(tick, refresh);

                waitCancellation.Cancel();
                await Task.WhenAll(
                    ObserveCancellationAsync(tick),
                    ObserveCancellationAsync(refresh));
                cancellationToken.ThrowIfCancellationRequested();

                if (completed == tick && !await tick)
                {
                    break;
                }

                await PollOnceAsync(cancellationToken);

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
            Volatile.Write(ref _runActive, 0);
        }
    }

    public void RequestRefresh()
    {
        if (!_refreshRequests.Writer.TryWrite(true))
        {
            throw new InvalidOperationException("The refresh queue is unavailable.");
        }
    }

    public void SetReducedCadence(bool reduced)
    {
        int value = reduced ? 1 : 0;
        if (Interlocked.Exchange(ref _reducedCadence, value) != value)
        {
            RequestRefresh();
        }
    }

    private static bool IsEnabled(AppSettings settings, string providerId) =>
        !settings.Providers.TryGetValue(providerId, out ProviderSettings? providerSettings)
        || providerSettings.Enabled;

    private ProviderSnapshot CreateDisabledSnapshot(IStatusProvider provider)
    {
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

    private async Task<ProviderSnapshot> FetchProviderAsync(
        IStatusProvider provider,
        ProviderSnapshot? previous,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_providerTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            ProviderSnapshot snapshot = await provider.FetchAsync(linked.Token);
            _log.Write(LogArea.Provider, OutcomeFor(snapshot.Health));
            return snapshot with { ConsecutiveFailures = 0 };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            _log.Write(LogArea.Provider, LogOutcome.TimedOut, exception: exception);
            return RetainFailure(provider, previous);
        }
        catch (Exception exception)
        {
            _log.Write(LogArea.Provider, LogOutcome.Failed, exception: exception);
            return RetainFailure(provider, previous);
        }
    }

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

    private static LogOutcome OutcomeFor(HealthState health) => health switch
    {
        HealthState.Ok => LogOutcome.Succeeded,
        HealthState.Degraded => LogOutcome.Degraded,
        HealthState.AuthExpired => LogOutcome.AuthExpired,
        HealthState.Unreachable => LogOutcome.Unreachable,
        HealthState.Disabled => LogOutcome.Disabled,
        _ => throw new InvalidOperationException("Validated health state was not mapped."),
    };

    private TimeSpan GetCadence()
    {
        AppSettings settings = _settings();
        return Volatile.Read(ref _reducedCadence) != 0
            ? settings.IdleInterval
            : settings.PollInterval;
    }

    private async Task WaitForRefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshRequests.Reader.ReadAsync(cancellationToken);
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void DispatchReportUpdated(StatusReport report)
    {
        if (_synchronizationContext is null)
        {
            ReportUpdated?.Invoke(this, report);
            return;
        }

        _synchronizationContext.Post(
            static state =>
            {
                var dispatch = ((StatusPoller Poller, StatusReport Report))state!;
                dispatch.Poller.ReportUpdated?.Invoke(dispatch.Poller, dispatch.Report);
            },
            (this, report));
    }
}
