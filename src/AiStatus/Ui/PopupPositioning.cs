using System.Windows;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace AiStatus.Ui;

internal interface IPopupPositionOperation
{
    void Cancel();
}

internal interface IPopupPositionScheduler
{
    IPopupPositionOperation Schedule(Action action);
}

internal interface IPopupPlacementService
{
    IReadOnlyList<MonitorWorkArea> GetMonitorWorkAreas();
    (MonitorWorkArea Monitor, Rect Area, TaskbarEdge Edge)? GetNotificationArea();
    void PositionWindow(Window window, Point physicalPosition);
}

internal sealed class DispatcherPopupPositionScheduler(Dispatcher dispatcher) : IPopupPositionScheduler
{
    private readonly Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public IPopupPositionOperation Schedule(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new DispatcherPopupPositionOperation(
            _dispatcher.BeginInvoke(action, DispatcherPriority.Loaded));
    }
}

internal sealed class DispatcherPopupPositionOperation(DispatcherOperation operation) : IPopupPositionOperation
{
    private readonly DispatcherOperation _operation = operation ?? throw new ArgumentNullException(nameof(operation));

    public void Cancel() => _ = _operation.Abort();
}
