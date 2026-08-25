using AiStatus.Core;

namespace AiStatus.Ui;

internal delegate Task<AppSettings> SettingsUpdate(
    Func<AppSettings, AppSettings> update,
    CancellationToken cancellationToken);

internal sealed class OverlayPositionPersistence
{
    private readonly object _gate = new();
    private readonly SettingsUpdate _updateSettings;
    private CustomOverlayPosition? _pending;
    private TaskCompletionSource? _batchCompletion;
    private bool _running;

    public OverlayPositionPersistence(SettingsStore store)
        : this(CreateUpdater(store))
    {
    }

    internal OverlayPositionPersistence(SettingsUpdate updateSettings)
    {
        _updateSettings = updateSettings ?? throw new ArgumentNullException(nameof(updateSettings));
    }

    public Exception? LastFailure { get; private set; }

    public event Action<object?, Exception>? Failed;

    public Task QueueAsync(CustomOverlayPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        bool startPump = false;
        Task batch;
        lock (_gate)
        {
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
                LastFailure = null;
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
        LastFailure = exception;
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

    private static SettingsUpdate CreateUpdater(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.UpdateAsync;
    }
}
