using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuotaGlass.Model;
using QuotaGlass.Ui;
using QuotaGlass.Ui.Controls;

namespace QuotaGlass.Tests.Ui;

[Collection(WpfStaCollection.Name)]
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

    [Fact]
    public void ProviderCard_CompanySeatSnapshotRendersMemberBudgetText()
    {
        // Catches a valid provider snapshot never reaching the existing card's visible text.
        Exception? failure = null;
        string[] visibleText = [];
        var thread = new Thread(() =>
        {
            try
            {
                var card = new ProviderCard
                {
                    Snapshot = new ProviderSnapshot(
                        "opencode-company-seat",
                        "OpenCode",
                        HealthState.Ok,
                        "Company Seat",
                        [new UsageWindow(
                            "monthly budget",
                            25,
                            new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
                            Severity.Normal)],
                        [
                            new InfoLine("Spend", "USD 2.50"),
                            new InfoLine("Budget", "USD 10.00"),
                        ],
                        null,
                        DateTimeOffset.UtcNow,
                        0),
                };
                card.Measure(new Size(360, double.PositiveInfinity));
                card.Arrange(new Rect(0, 0, 360, card.DesiredSize.Height));
                card.UpdateLayout();
                visibleText = Descendants<TextBlock>(card)
                    .Select(textBlock => textBlock.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The STA rendering thread did not finish.");

        Assert.Null(failure);
        Assert.Contains("OpenCode", visibleText);
        Assert.Contains("Company Seat", visibleText);
        Assert.Contains("monthly budget", visibleText);
        Assert.Contains("Spend", visibleText);
        Assert.Contains("USD 2.50", visibleText);
        Assert.Contains("Budget", visibleText);
        Assert.Contains("USD 10.00", visibleText);
        Assert.DoesNotContain(visibleText, text => text.Contains("organization", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
