using AiStatus.Ui;

namespace AiStatus.Tests.Ui;

public sealed class OverlayDragStateTests
{
    [Fact]
    public void LoseCapture_CancelsAnActiveDrag()
    {
        // Break caught: lost mouse capture leaves the overlay believing a drag is still active.
        var state = new OverlayDragState();
        state.Begin(captured: true);

        state.Cancel();

        Assert.False(state.IsDragging);
    }

    [Fact]
    public void Begin_DoesNotStartWhenMouseCaptureFails()
    {
        // Break caught: a failed mouse capture starts a drag that cannot end through normal capture events.
        var state = new OverlayDragState();

        state.Begin(captured: false);

        Assert.False(state.IsDragging);
    }

    [Fact]
    public void End_ReturnsTrueOnlyOnceForAnActiveDrag()
    {
        // Break caught: duplicate mouse-up or lost-capture paths persist the same drag more than once.
        var state = new OverlayDragState();
        state.Begin(captured: true);

        Assert.True(state.End());
        Assert.False(state.End());
        Assert.False(state.IsDragging);
    }
}
