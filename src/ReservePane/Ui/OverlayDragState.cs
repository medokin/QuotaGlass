namespace QuotaGlass.Ui;

internal sealed class OverlayDragState
{
    public bool IsDragging { get; private set; }

    public void Begin(bool captured)
    {
        IsDragging = captured;
    }

    public bool End()
    {
        if (!IsDragging)
        {
            return false;
        }

        IsDragging = false;
        return true;
    }

    public void Cancel()
    {
        IsDragging = false;
    }
}
