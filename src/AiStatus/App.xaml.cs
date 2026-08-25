using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using AiStatus.Core;
using AiStatus.Platform;
using AiStatus.Providers;
using AiStatus.Ui;

namespace AiStatus;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _applicationCancellation = new();
    private SettingsStore? _settingsStore;
    private RollingFileLog? _log;
    private AppSettingsState? _settingsState;
    private ProviderRegistry? _providerRegistry;
    private ThresholdWatcher? _thresholdWatcher;
    private StatusPoller? _poller;
    private PopupWindow? _popup;
    private OverlayWindow? _overlay;
    private ToastNotifier? _toast;
    private IHotkeyRegistration? _hotkey;
    private ActivityStateMonitor? _activity;
    private TrayIconHost? _tray;
    private ApplicationSettingsCoordinator? _settingsCoordinator;
    private ApplicationShutdownCoordinator? _shutdownCoordinator;
    private Task? _pollLoop;
    private Task? _startupTask;
    private int _ownedResourcesDisposed;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _startupTask = StartAsync();
    }

    protected override void OnExit(ExitEventArgs args)
    {
        if (_shutdownCoordinator is not null)
        {
            _shutdownCoordinator.ShutdownFallback();
        }
        else
        {
            CancelApplication();
            DisposeOwnedResources();
        }

        base.OnExit(args);
    }

    private async Task StartAsync()
    {
        try
        {
            AppPaths paths = AppPaths.FromEnvironment();
            _settingsStore = new SettingsStore(paths.SettingsPath);
            AppSettings settings = await _settingsStore.LoadAsync(_applicationCancellation.Token);
            _settingsState = new AppSettingsState(settings);

            _log = new RollingFileLog(paths.LogPath);
            _log.Write(LogArea.Application, LogOutcome.Started);

            _providerRegistry = ProviderRegistry.Create(() => _settingsState.Current, paths);
            _thresholdWatcher = new ThresholdWatcher(settings.WarningPercent, settings.CriticalPercent);
            _poller = new StatusPoller(
                _providerRegistry.Providers,
                () => _settingsState.Current,
                _log);
            _popup = new PopupWindow();
            _overlay = new OverlayWindow(_settingsStore);
            _toast = new ToastNotifier();
            _hotkey = CreateHotkeyRegistration(settings.Hotkey);
            _activity = new ActivityStateMonitor();

            var dispatcher = new WpfUiDispatcher(Dispatcher);
            _shutdownCoordinator = new ApplicationShutdownCoordinator(
                _applicationCancellation,
                () => _pollLoop,
                dispatcher,
                DisposeOwnedResources,
                Shutdown,
                _log);

            string executablePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("The executable path is unavailable.");
            var autostart = new AutostartService(executablePath);
            _tray = new TrayIconHost(
                Dispatcher,
                _popup,
                _overlay,
                _poller,
                _thresholdWatcher,
                () => _settingsState.Current,
                GetActivePollInterval,
                _settingsStore,
                autostart,
                _toast,
                paths.SettingsPath,
                _shutdownCoordinator.ShutdownAsync,
                _log);

            _settingsCoordinator = new ApplicationSettingsCoordinator(
                _settingsState,
                _thresholdWatcher,
                _hotkey,
                CreateHotkeyRegistration,
                ApplyOverlaySettings,
                _poller.RequestRefresh,
                _tray.ToggleOverlayAsync,
                _log);

            ApplyOverlaySettings(settings);
            _settingsStore.Changed += OnSettingsChanged;
            _activity.Changed += OnActivityChanged;
            _poller.SetReducedCadence(_activity.IsReducedCadence);

            _pollLoop = _poller.RunAsync(_applicationCancellation.Token);
            _poller.RequestRefresh();
        }
        catch (OperationCanceledException) when (_applicationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            TryLogTopLevelFailure(exception);
            if (_shutdownCoordinator is not null)
            {
                await _shutdownCoordinator.ShutdownAsync();
            }
            else
            {
                CancelApplication();
                DisposeOwnedResources();
                Shutdown();
            }
        }
    }

    private IHotkeyRegistration CreateHotkeyRegistration(string chord) =>
        new OwnedHotkeyRegistration(chord, _log ?? throw new InvalidOperationException("Logging is unavailable."));

    private TimeSpan GetActivePollInterval()
    {
        AppSettings settings = _settingsState?.Current ?? AppSettings.Default;
        return _activity?.IsReducedCadence == true
            ? settings.IdleInterval
            : settings.PollInterval;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        try
        {
            _ = Dispatcher.BeginInvoke(
                () => ApplySettingsReloadSafely(settings),
                System.Windows.Threading.DispatcherPriority.DataBind);
        }
        catch (Exception exception)
        {
            TryLogTopLevelFailure(exception);
        }
    }

    private void ApplySettingsReloadSafely(AppSettings settings)
    {
        if (Volatile.Read(ref _ownedResourcesDisposed) != 0)
        {
            return;
        }

        try
        {
            _settingsCoordinator?.Apply(settings);
        }
        catch (Exception exception)
        {
            TryLogTopLevelFailure(exception);
        }
    }

    private void OnActivityChanged(object? sender, EventArgs args)
    {
        try
        {
            if (Volatile.Read(ref _ownedResourcesDisposed) == 0 && _activity is not null)
            {
                _poller?.SetReducedCadence(_activity.IsReducedCadence);
            }
        }
        catch (Exception exception)
        {
            TryLogTopLevelFailure(exception);
        }
    }

    private void ApplyOverlaySettings(AppSettings settings)
    {
        if (_overlay is null)
        {
            return;
        }

        if (settings.OverlayVisible)
        {
            if (!_overlay.IsVisible)
            {
                _overlay.Show();
            }

            _overlay.ApplySettings(settings);
        }
        else if (_overlay.IsVisible)
        {
            _overlay.Hide();
        }
    }

    private void DisposeOwnedResources()
    {
        if (Interlocked.Exchange(ref _ownedResourcesDisposed, 1) != 0)
        {
            return;
        }

        if (_settingsStore is not null)
        {
            _settingsStore.Changed -= OnSettingsChanged;
        }

        if (_activity is not null)
        {
            _activity.Changed -= OnActivityChanged;
        }

        DisposeSafely(() => _tray?.Dispose());
        DisposeSafely(() =>
        {
            if (_settingsCoordinator is not null)
            {
                _settingsCoordinator.Dispose();
            }
            else
            {
                _hotkey?.Dispose();
            }
        });
        DisposeSafely(() => _activity?.Dispose());
        DisposeSafely(() => _providerRegistry?.Dispose());
        DisposeSafely(() => _settingsStore?.Dispose());
        DisposeSafely(() => _popup?.Close());
        DisposeSafely(() => _overlay?.Close());
        DisposeSafely(_applicationCancellation.Dispose);
    }

    private void DisposeSafely(Action? dispose)
    {
        if (dispose is null)
        {
            return;
        }

        try
        {
            dispose();
        }
        catch (Exception exception)
        {
            TryLogTopLevelFailure(exception);
        }
    }

    private void CancelApplication()
    {
        try
        {
            _applicationCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void TryLogTopLevelFailure(Exception exception)
    {
        try
        {
            _log?.Write(LogArea.Application, LogOutcome.Failed, exception: exception);
        }
        catch (Exception)
        {
        }
    }
}

internal sealed class OwnedHotkeyRegistration : IHotkeyRegistration
{
    private const int PopupWindowStyle = unchecked((int)0x80000000);
    private readonly HwndSource _source;
    private readonly GlobalHotkey _hotkey;
    private bool _disposed;

    public OwnedHotkeyRegistration(string chord, RollingFileLog log)
    {
        _source = new HwndSource(new HwndSourceParameters("AI Status Hotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = PopupWindowStyle,
        });

        try
        {
            _hotkey = new GlobalHotkey(_source, chord, log);
        }
        catch
        {
            _source.Dispose();
            throw;
        }
    }

    public event EventHandler? Pressed
    {
        add => _hotkey.Pressed += value;
        remove => _hotkey.Pressed -= value;
    }

    public bool IsRegistered => _hotkey.IsRegistered;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkey.Dispose();
        _source.Dispose();
    }
}
