using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using AiStatus.Model;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace AiStatus.Ui;

public partial class PopupWindow : Window
{
    private readonly WindowPlacementService _placementService;

    public PopupWindow()
        : this(new WindowPlacementService())
    {
    }

    internal PopupWindow(WindowPlacementService placementService)
    {
        _placementService = placementService ?? throw new ArgumentNullException(nameof(placementService));
        InitializeComponent();
        Deactivated += OnDeactivated;
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
        Deactivated -= OnDeactivated;
        IsVisibleChanged -= OnIsVisibleChanged;
        base.OnClosed(args);
    }

    private void OnDeactivated(object? sender, EventArgs args) => Hide();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (IsVisible)
        {
            _ = Dispatcher.BeginInvoke(PositionNearNotificationArea, DispatcherPriority.Loaded);
        }
    }

    private void PositionNearNotificationArea()
    {
        Size size = new(Math.Max(ActualWidth, MinWidth), Math.Max(ActualHeight, 1));
        (MonitorWorkArea Monitor, Rect Area, TaskbarEdge Edge)? notification = _placementService.GetNotificationArea();
        if (notification is not null)
        {
            Point position = WindowPlacementService.GetPopupPosition(
                notification.Value.Monitor,
                notification.Value.Area,
                size,
                notification.Value.Edge);
            _placementService.PositionWindow(this, position);
            return;
        }

        IReadOnlyList<MonitorWorkArea> monitors = _placementService.GetMonitorWorkAreas();
        if (monitors.Count == 0)
        {
            return;
        }

        MonitorWorkArea primary = WindowPlacementService.ResolveConfiguredMonitor(null, monitors);
        Rect fallbackAnchor = new(primary.WorkingArea.Right, primary.WorkingArea.Bottom, 0, 0);
        Point fallback = WindowPlacementService.GetPopupPosition(primary, fallbackAnchor, size, TaskbarEdge.Bottom);
        _placementService.PositionWindow(this, fallback);
    }
}
