using AiStatus.Ui;
using AiStatus.Ui.Controls;

namespace AiStatus.Tests.Ui;

public sealed class UiConstructionSmokeTests
{
    [Fact]
    public void SharedCardAndWindows_ConstructOnStaThread()
    {
        // Break caught: compiled XAML or a default constructor cannot create the three shared UI surfaces.
        Exception? failure = null;
        bool? popupShowActivated = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new ProviderCard();
                var popup = new PopupWindow();
                var overlay = new OverlayWindow();
                popupShowActivated = popup.ShowActivated;
                popup.Close();
                overlay.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The STA construction thread did not finish.");

        Assert.Null(failure);
        Assert.True(popupShowActivated);
    }
}
