using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AiStatus.Core;
using AiStatus.Model;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AiStatus.Ui;

public partial class OverlayWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const long NoActivateStyle = 0x08000000L;
    private const long ToolWindowStyle = 0x00000080L;

    private readonly SettingsStore? _settingsStore;
    private readonly WindowPlacementService _placementService;
    private AppSettings _settings = AppSettings.Default;
    private Point _dragOffset;
    private bool _dragging;

    public OverlayWindow()
        : this(null, new WindowPlacementService())
    {
    }

    public OverlayWindow(SettingsStore settingsStore)
        : this(settingsStore, new WindowPlacementService())
    {
    }

    internal OverlayWindow(SettingsStore? settingsStore, WindowPlacementService placementService)
    {
        _settingsStore = settingsStore;
        _placementService = placementService ?? throw new ArgumentNullException(nameof(placementService));
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public ObservableCollection<ProviderSnapshot> Providers { get; } = [];

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    public void SetProviders(IEnumerable<ProviderSnapshot> providers, TimeSpan activePollInterval)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (activePollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(activePollInterval));
        }

        PollInterval = activePollInterval;
        Providers.Clear();
        foreach (ProviderSnapshot provider in providers)
        {
            Providers.Add(provider);
        }
    }

    protected override void OnClosed(EventArgs args)
    {
        SourceInitialized -= OnSourceInitialized;
        IsVisibleChanged -= OnIsVisibleChanged;
        base.OnClosed(args);
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        long styles = NativeMethods.GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, ExtendedStyleIndex, new IntPtr(styles | NoActivateStyle | ToolWindowStyle));
    }

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!IsVisible)
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            if (_settingsStore is not null)
            {
                _settings = await _settingsStore.LoadAsync(CancellationToken.None);
            }

            ApplyConfiguredPlacement();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void ApplyConfiguredPlacement()
    {
        IReadOnlyList<MonitorWorkArea> monitors = _placementService.GetMonitorWorkAreas();
        if (monitors.Count == 0)
        {
            return;
        }

        Size size = new(Math.Max(ActualWidth, MinWidth), Math.Max(ActualHeight, 1));
        Point position = WindowPlacementService.GetOverlayPosition(_settings, monitors, size);
        Left = position.X;
        Top = position.Y;
    }

    private void OnDragStarted(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dragOffset = args.GetPosition(this);
        _dragging = CaptureMouse();
        args.Handled = _dragging;
    }

    private void OnDragMoved(object sender, MouseEventArgs args)
    {
        if (!_dragging || args.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point cursor = args.GetPosition(this);
        Left += cursor.X - _dragOffset.X;
        Top += cursor.Y - _dragOffset.Y;
        args.Handled = true;
    }

    private async void OnDragEnded(object sender, MouseButtonEventArgs args)
    {
        if (!_dragging || args.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        args.Handled = true;

        if (_settingsStore is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<MonitorWorkArea> monitors = _placementService.GetMonitorWorkAreas();
            if (monitors.Count == 0)
            {
                return;
            }

            Size size = new(Math.Max(ActualWidth, MinWidth), Math.Max(ActualHeight, 1));
            await WindowPlacementService.SaveCustomPositionAsync(
                _settingsStore,
                _settings,
                new Point(Left, Top),
                size,
                monitors,
                CancellationToken.None);
            MonitorWorkArea monitor = WindowPlacementService.FindNearestMonitor(
                new Point(Left + (size.Width / 2), Top + (size.Height / 2)),
                monitors);
            Point clamped = WindowPlacementService.ClampToWorkArea(new Point(Left, Top), size, monitor.WorkingArea);
            Left = clamped.X;
            Top = clamped.Y;
            _settings = _settings with
            {
                OverlayCorner = OverlayCorner.Custom,
                OverlayMonitorId = monitor.DeviceId,
                OverlayPosition = new OverlayPosition(clamped.X, clamped.Y),
            };
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue);
    }
}
