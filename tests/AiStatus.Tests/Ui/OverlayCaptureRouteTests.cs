using System.Windows.Input;
using AiStatus.Ui;

namespace AiStatus.Tests.Ui;

[Collection(WpfStaCollection.Name)]
public sealed class OverlayCaptureRouteTests
{
    [Fact]
    public void WindowLostMouseCapture_CancelsInjectedActiveDragState()
    {
        // Break caught: cleanup is attached to a child while OverlayWindow is the element that owns capture.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dragState = new OverlayDragState();
                dragState.Begin(captured: true);
                var window = new OverlayWindow(null, new WindowPlacementService(), dragState);
                var captureLost = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                {
                    RoutedEvent = Mouse.LostMouseCaptureEvent,
                    Source = window,
                };

                window.RaiseEvent(captureLost);

                Assert.False(dragState.IsDragging);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The STA capture-route thread did not finish.");

        Assert.Null(failure);
    }
}
