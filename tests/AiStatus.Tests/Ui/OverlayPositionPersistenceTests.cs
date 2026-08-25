using System.Windows;
using AiStatus.Core;
using AiStatus.Ui;

namespace AiStatus.Tests.Ui;

public sealed class OverlayPositionPersistenceTests
{
    [Fact]
    public async Task QueueAsync_SerializesWritesAndPersistsNewestPendingPosition()
    {
        // Break caught: concurrent drag saves collide or allow an older delayed write to finish last.
        AppSettings current = AppSettings.Default with
        {
            Hotkey = "Ctrl+Shift+Q",
            OverlayVisible = true,
            WarningPercent = 72,
        };
        int updateCount = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = new OverlayPositionPersistence(async (update, _) =>
        {
            if (Interlocked.Increment(ref updateCount) == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }

            current = update(current);
            return current;
        });

        Task first = persistence.QueueAsync(new CustomOverlayPosition(new Point(1100, 40), "SECONDARY"));
        await firstStarted.Task;
        Task second = persistence.QueueAsync(new CustomOverlayPosition(new Point(1200, 50), "SECONDARY"));
        Task newest = persistence.QueueAsync(new CustomOverlayPosition(new Point(1300, 60), "SECONDARY"));
        releaseFirst.SetResult();
        await Task.WhenAll(first, second, newest);

        Assert.Equal(2, updateCount);
        Assert.Equal(new OverlayPosition(1300, 60), current.OverlayPosition);
        Assert.Equal("SECONDARY", current.OverlayMonitorId);
        Assert.Equal(OverlayCorner.Custom, current.OverlayCorner);
        Assert.Equal("Ctrl+Shift+Q", current.Hotkey);
        Assert.True(current.OverlayVisible);
        Assert.Equal(72, current.WarningPercent);
    }

    [Fact]
    public async Task QueueAsync_ReportsExpectedFailureWithoutFaultingDispatcherCaller()
    {
        // Break caught: an expected file failure escapes an async-void mouse handler or is silently discarded.
        Exception? reported = null;
        var persistence = new OverlayPositionPersistence((_, _) => throw new IOException("disk unavailable"));
        persistence.Failed += (_, exception) => reported = exception;

        await persistence.QueueAsync(new CustomOverlayPosition(new Point(100, 80), "PRIMARY"));

        Assert.IsType<IOException>(reported);
        Assert.IsType<IOException>(persistence.LastFailure);
    }

    [Fact]
    public void ClampCustomPosition_UsesTargetDpiBeforePersistence()
    {
        // Break caught: a failed save leaves the visible overlay outside the physical working area.
        var monitor = new MonitorWorkArea(
            "SECONDARY",
            new Rect(1920, 0, 2560, 1440),
            new Rect(1920, 0, 2560, 1400),
            false,
            1.5);

        CustomOverlayPosition result = WindowPlacementService.ClampCustomPosition(
            new Point(4400, -20),
            new Size(300, 200),
            monitor);

        Assert.Equal(new Point(4030, 0), result.Position);
        Assert.Equal("SECONDARY", result.MonitorId);
    }
}
