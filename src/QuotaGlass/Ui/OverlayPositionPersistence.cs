using System.Runtime.ExceptionServices;
using QuotaGlass.Core;

namespace QuotaGlass.Ui;

internal delegate Task<AppSettings> SettingsUpdate(
    Func<AppSettings, AppSettings> update,
    CancellationToken cancellationToken);

internal sealed class OverlayPositionPersistence
{
    private readonly object _gate = new();
    private readonly SettingsUpdate _updateSettings;
    private CustomOverlayPosition? _pending;
    private TaskCompletionSource? _batchCompletion;
    private Exception? _lastFailure;
    private bool _running;
    private bool _flushRequested;

    public OverlayPositionPersistence(SettingsStore store)
        : this(CreateUpdater(store))
    {
    }

    internal OverlayPositionPersistence(SettingsUpdate updateSettings)
    {
        _updateSettings = updateSettings ?? throw new ArgumentNullException(nameof(updateSettings));
    }

    public Exception? LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _lastFailure;
            }
        }
    }

    public event Action<object?, Exception>? Failed;

    public Task QueueAsync(CustomOverlayPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        bool startPump = false;
        Task batch;
        lock (_gate)
        {
            if (_flushRequested)
            {
                return _batchCompletion?.Task ?? Task.CompletedTask;
            }

            _pending = position;
            if (!_running)
            {
                _running = true;
                _batchCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                startPump = true;
            }

            batch = _batchCompletion!.Task;
        }

        if (startPump)
        {
            _ = RunPumpAsync();
        }

        return batch;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task batch;
        lock (_gate)
        {
            _flushRequested = true;
            batch = _batchCompletion?.Task ?? Task.CompletedTask;
        }

        await batch.WaitAsync(cancellationToken).ConfigureAwait(false);
        Exception? failure = LastFailure;
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task RunPumpAsync()
    {
        while (true)
        {
            CustomOverlayPosition position;
            lock (_gate)
            {
                position = _pending!;
                _pending = null;
            }

            try
            {
                await _updateSettings(
                    settings => settings with
                    {
                        OverlayCorner = OverlayCorner.Custom,
                        OverlayMonitorId = position.MonitorId,
                        OverlayPosition = new OverlayPosition(position.Position.X, position.Position.Y),
                    },
                    CancellationToken.None).ConfigureAwait(false);
                SetLastFailure(null);
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }

            lock (_gate)
            {
                if (_pending is not null)
                {
                    continue;
                }

                _running = false;
                _batchCompletion!.TrySetResult();
                _batchCompletion = null;
                return;
            }
        }
    }

    private void ReportFailure(Exception exception)
    {
        SetLastFailure(exception);
        Action<object?, Exception>? handlers = Failed;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<object?, Exception> handler in handlers.GetInvocationList().Cast<Action<object?, Exception>>())
        {
            try
            {
                handler(this, exception);
            }
            catch (Exception)
            {
            }
        }
    }

    private void SetLastFailure(Exception? failure)
    {
        lock (_gate)
        {
            _lastFailure = failure;
        }
    }

    private static SettingsUpdate CreateUpdater(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.UpdateAsync;
    }
}
