using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AiStatus.Core;
using AiStatus.Model;
using AiStatus.Tests.Support;
using AiStatus.Ui;
using AiStatus.Ui.Converters;

namespace AiStatus.Tests.Ui;

public sealed class DisplayConverterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void ResetTimeConverter_FormatsNearResetAsCompactCountdown()
    {
        // Break caught: a near reset is rendered as a calendar timestamp instead of a scan-friendly countdown.
        var converter = new ResetTimeConverter(new FixedTimeProvider(Now), TimeZoneInfo.Utc);

        object result = converter.Convert(Now.AddHours(2).AddMinutes(47), typeof(string), null, CultureInfo.GetCultureInfo("de-DE"));

        Assert.Equal("in 2h47", result);
    }

    [Fact]
    public void ResetTimeConverter_UsesInvariantLocalDayAtExactlyTwentyFourHours()
    {
        // Break caught: the 24-hour boundary remains a countdown or uses the current UI culture's day name.
        TimeZoneInfo localZone = TimeZoneInfo.CreateCustomTimeZone("Test +02", TimeSpan.FromHours(2), "Test +02", "Test +02");
        var converter = new ResetTimeConverter(new FixedTimeProvider(Now), localZone);

        object result = converter.Convert(Now.AddHours(24), typeof(string), null, CultureInfo.GetCultureInfo("de-DE"));

        Assert.Equal("Wed 14:00", result);
    }

    [Fact]
    public void ResetTimeConverter_ReturnsEmptyForNullAndNowForPastTime()
    {
        // Break caught: missing or expired reset values produce misleading future timestamps.
        var converter = new ResetTimeConverter(new FixedTimeProvider(Now), TimeZoneInfo.Utc);

        Assert.Equal(string.Empty, converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("now", converter.Convert(Now.AddSeconds(-1), typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PercentToGridLengthConverter_HidesNullAndClampsVisualWidthWithoutChangingModel()
    {
        // Break caught: invalid percentages overflow the runway, or display normalization mutates provider data.
        var converter = new PercentToGridLengthConverter();
        var below = new UsageWindow("hourly", -12, null, Severity.Normal);
        var above = new UsageWindow("weekly", 135, null, Severity.Critical);

        Assert.Equal(new GridLength(0, GridUnitType.Star), converter.Convert(null, typeof(GridLength), null, CultureInfo.InvariantCulture));
        Assert.Equal(new GridLength(0, GridUnitType.Star), converter.Convert(below.Percent, typeof(GridLength), null, CultureInfo.InvariantCulture));
        Assert.Equal(new GridLength(100, GridUnitType.Star), converter.Convert(above.Percent, typeof(GridLength), null, CultureInfo.InvariantCulture));
        Assert.Equal(-12, below.Percent);
        Assert.Equal(135, above.Percent);
    }

    [Fact]
    public void PercentToGridLengthConverter_ComputesTheUnfilledRunwayColumn()
    {
        // Break caught: both grid columns receive the filled percentage and the runway no longer represents a whole.
        var converter = new PercentToGridLengthConverter();

        object result = converter.Convert(72.5, typeof(GridLength), "Remaining", CultureInfo.InvariantCulture);

        Assert.Equal(new GridLength(27.5, GridUnitType.Star), result);
    }

    [Fact]
    public void SeverityToBrushConverter_UsesTheRuntimeStatusColors()
    {
        // Break caught: provider cards drift from the tray icon's exact operational colors.
        var converter = new SeverityToBrushConverter();

        Assert.Equal(Color.FromRgb(0x35, 0xC4, 0x6A), Assert.IsType<SolidColorBrush>(converter.Convert(Severity.Normal, typeof(Brush), null, CultureInfo.InvariantCulture)).Color);
        Assert.Equal(Color.FromRgb(0xF0, 0xA4, 0x3A), Assert.IsType<SolidColorBrush>(converter.Convert(Severity.Warning, typeof(Brush), null, CultureInfo.InvariantCulture)).Color);
        Assert.Equal(Color.FromRgb(0xE2, 0x4B, 0x4B), Assert.IsType<SolidColorBrush>(converter.Convert(Severity.Critical, typeof(Brush), null, CultureInfo.InvariantCulture)).Color);
    }

    [Fact]
    public void UpdatedText_AppearsForFailuresOrAgeBeyondTwiceTheActivePollInterval()
    {
        // Break caught: stale provider data looks current, or the exact freshness boundary is marked stale.
        var time = new FixedTimeProvider(Now);
        ProviderSnapshot boundary = Snapshot(Now.AddMinutes(-2), 0);
        ProviderSnapshot aged = Snapshot(Now.AddMinutes(-2).AddSeconds(-1), 0);
        ProviderSnapshot failed = Snapshot(Now.AddSeconds(-20), 1);

        Assert.Equal(string.Empty, ProviderDisplayText.GetUpdatedText(boundary, TimeSpan.FromMinutes(1), time));
        Assert.Equal("Updated 2m ago", ProviderDisplayText.GetUpdatedText(aged, TimeSpan.FromMinutes(1), time));
        Assert.Equal("Updated 20s ago", ProviderDisplayText.GetUpdatedText(failed, TimeSpan.FromMinutes(1), time));
    }

    [Fact]
    public void ClampToWorkArea_KeepsTheWholeWindowVisibleOnBothAxes()
    {
        // Break caught: popup or overlay placement leaks beyond a monitor's working area.
        Rect workArea = new(100, 50, 800, 600);

        Point result = WindowPlacementService.ClampToWorkArea(new Point(850, -20), new Size(240, 180), workArea);

        Assert.Equal(new Point(660, 50), result);
    }

    [Fact]
    public void OverlayPosition_UsesConfiguredMonitorCornerAndTwelveDipMargin()
    {
        // Break caught: corner placement ignores the configured monitor or the specified instrument margin.
        MonitorWorkArea[] monitors =
        [
            new("PRIMARY", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040), true, 1),
            new("SECONDARY", new Rect(1920, 0, 1280, 1024), new Rect(1920, 0, 1280, 984), false, 1.5),
        ];
        AppSettings settings = AppSettings.Default with
        {
            OverlayMonitorId = "SECONDARY",
            OverlayCorner = OverlayCorner.BottomRight,
        };

        Point result = WindowPlacementService.GetOverlayPosition(settings, monitors, new Size(300, 200));

        Assert.Equal(new Point(2888, 772), result);
    }

    [Theory]
    [InlineData(OverlayCorner.TopLeft, 112, 62)]
    [InlineData(OverlayCorner.TopRight, 648, 62)]
    [InlineData(OverlayCorner.BottomLeft, 112, 438)]
    [InlineData(OverlayCorner.BottomRight, 648, 438)]
    public void OverlayPosition_AppliesMarginAtEveryCorner(OverlayCorner corner, double expectedX, double expectedY)
    {
        // Break caught: one corner omits the margin or applies it on the wrong side.
        MonitorWorkArea[] monitors =
        [
            new("PRIMARY", new Rect(0, 0, 1000, 800), new Rect(100, 50, 800, 600), true, 1),
        ];
        AppSettings settings = AppSettings.Default with { OverlayCorner = corner };

        Point result = WindowPlacementService.GetOverlayPosition(settings, monitors, new Size(240, 200));

        Assert.Equal(new Point(expectedX, expectedY), result);
    }

    [Fact]
    public void OverlayPosition_FallsBackToPrimaryAndClampsCustomCoordinates()
    {
        // Break caught: a disconnected configured monitor strands a custom overlay off-screen.
        MonitorWorkArea[] monitors =
        [
            new("SECONDARY", new Rect(-1280, 0, 1280, 1024), new Rect(-1280, 0, 1280, 984), false, 1),
            new("PRIMARY", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040), true, 1),
        ];
        AppSettings settings = AppSettings.Default with
        {
            OverlayMonitorId = "DISCONNECTED",
            OverlayCorner = OverlayCorner.Custom,
            OverlayPosition = new OverlayPosition(4000, -100),
        };

        Point result = WindowPlacementService.GetOverlayPosition(settings, monitors, new Size(300, 200));

        Assert.Equal(new Point(1620, 0), result);
    }

    [Theory]
    [InlineData(TaskbarEdge.Bottom, 660, 550)]
    [InlineData(TaskbarEdge.Top, 660, 50)]
    [InlineData(TaskbarEdge.Left, 100, 550)]
    [InlineData(TaskbarEdge.Right, 660, 550)]
    public void PopupPosition_StaysInsideWorkAreaForEveryTaskbarEdge(TaskbarEdge edge, double expectedX, double expectedY)
    {
        // Break caught: popup anchoring assumes a bottom taskbar and clips on another edge.
        var monitor = new MonitorWorkArea("DISPLAY", new Rect(0, 0, 1000, 800), new Rect(100, 50, 800, 700), true, 1);
        Rect notificationArea = edge switch
        {
            TaskbarEdge.Bottom => new Rect(700, 750, 200, 50),
            TaskbarEdge.Top => new Rect(700, 0, 200, 50),
            TaskbarEdge.Left => new Rect(0, 600, 100, 200),
            TaskbarEdge.Right => new Rect(900, 600, 100, 200),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };

        Point result = WindowPlacementService.GetPopupPosition(monitor, notificationArea, new Size(240, 200), edge);

        Assert.Equal(new Point(expectedX, expectedY), result);
    }

    [Fact]
    public async Task SaveCustomPositionAsync_PersistsCoordinatesAndContainingMonitor()
    {
        // Break caught: drag placement is visually applied but lost, or saved against the wrong monitor.
        using var directory = new TemporaryDirectory();
        using var store = new SettingsStore(Path.Combine(directory.Path, "settings.json"));
        MonitorWorkArea[] monitors =
        [
            new("PRIMARY", new Rect(0, 0, 1000, 800), new Rect(0, 0, 1000, 760), true, 1),
            new("SECONDARY", new Rect(1000, 0, 1000, 800), new Rect(1000, 0, 1000, 760), false, 1),
        ];

        await WindowPlacementService.SaveCustomPositionAsync(
            store,
            AppSettings.Default,
            new Point(1200, 80),
            new Size(300, 200),
            monitors,
            CancellationToken.None);

        AppSettings saved = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(OverlayCorner.Custom, saved.OverlayCorner);
        Assert.Equal("SECONDARY", saved.OverlayMonitorId);
        Assert.Equal(new OverlayPosition(1200, 80), saved.OverlayPosition);
    }

    private static ProviderSnapshot Snapshot(DateTimeOffset fetchedAt, int consecutiveFailures) => new(
        "claude",
        "Claude",
        HealthState.Ok,
        null,
        ImmutableArray<UsageWindow>.Empty,
        ImmutableArray<InfoLine>.Empty,
        null,
        fetchedAt,
        consecutiveFailures);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
