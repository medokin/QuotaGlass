using System.Windows;
using QuotaGlass.Ui;

namespace QuotaGlass.Tests.Ui;

[Collection(WpfStaCollection.Name)]
public sealed class PopupWindowLifecycleTests
{
    [Fact]
    public void Close_CancelsDeferredPositioningBeforeItCanTouchClosedWindow()
    {
        // Break caught: a DispatcherPriority.Loaded positioning callback runs after popup close during shutdown.
        Exception? failure = null;
        int positions = -1;
        var thread = new Thread(() =>
        {
            try
            {
                var placement = new FakePopupPlacementService();
                var scheduler = new FakePopupPositionScheduler();
                var window = new PopupWindow(placement, scheduler);

                window.Show();
                window.Close();
                scheduler.RunPending();
                positions = placement.PositionCalls;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The popup lifecycle thread did not finish.");

        Assert.Null(failure);
        Assert.Equal(0, positions);
    }

    private sealed class FakePopupPositionScheduler : IPopupPositionScheduler
    {
        private readonly List<FakePopupPositionOperation> _pending = [];

        public IPopupPositionOperation Schedule(Action action)
        {
            var operation = new FakePopupPositionOperation(action);
            _pending.Add(operation);
            return operation;
        }

        public void RunPending()
        {
            foreach (FakePopupPositionOperation operation in _pending)
            {
                operation.Run();
            }
        }
    }

    private sealed class FakePopupPositionOperation(Action action) : IPopupPositionOperation
    {
        private bool _cancelled;

        public void Cancel() => _cancelled = true;

        public void Run()
        {
            if (!_cancelled)
            {
                action();
            }
        }
    }

    private sealed class FakePopupPlacementService : IPopupPlacementService
    {
        private static readonly MonitorWorkArea Monitor = new(
            "PRIMARY",
            new Rect(0, 0, 1920, 1080),
            new Rect(0, 0, 1920, 1040),
            true,
            1);

        public int PositionCalls { get; private set; }

        public IReadOnlyList<MonitorWorkArea> GetMonitorWorkAreas() => [Monitor];

        public (MonitorWorkArea Monitor, Rect Area, TaskbarEdge Edge)? GetNotificationArea() =>
            (Monitor, new Rect(1800, 1040, 120, 40), TaskbarEdge.Bottom);

        public void PositionWindow(Window window, Point physicalPosition) => PositionCalls++;
    }
}
