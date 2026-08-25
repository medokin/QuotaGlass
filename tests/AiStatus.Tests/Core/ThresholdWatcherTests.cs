using AiStatus.Core;
using AiStatus.Model;
using AiStatus.Tests.Support;
using static AiStatus.Tests.Support.SnapshotFactory;

namespace AiStatus.Tests.Core;

public sealed class ThresholdWatcherTests
{
    [Fact]
    public void Evaluate_FiresEachThresholdOncePerCycle()
    {
        // Break caught: repeated evaluations or falling usage reissue alerts within one reset cycle.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");

        Assert.Empty(watcher.Evaluate(Report(79, cycle), Report(79, cycle)));
        Assert.Equal(AlertKind.Warning, Assert.Single(watcher.Evaluate(Report(79, cycle), Report(80, cycle))).Kind);
        Assert.Empty(watcher.Evaluate(Report(80, cycle), Report(94, cycle)));
        Assert.Equal(AlertKind.Critical, Assert.Single(watcher.Evaluate(Report(94, cycle), Report(95, cycle))).Kind);
        Assert.Empty(watcher.Evaluate(Report(95, cycle), Report(96, cycle)));
        Assert.Empty(watcher.Evaluate(Report(96, cycle), Report(81, cycle)));
    }

    [Fact]
    public void Evaluate_NewResetTimestampRearmsThreshold()
    {
        // Break caught: a warning from a completed billing window suppresses the next window's warning.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset first = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
        DateTimeOffset second = first.AddDays(7);
        watcher.Evaluate(Report(79, first), Report(80, first));

        Assert.Equal(AlertKind.Warning,
            Assert.Single(watcher.Evaluate(Report(10, second), Report(80, second))).Kind);
    }

    [Fact]
    public void Evaluate_InitialCriticalPercentEmitsOnlyCritical()
    {
        // Break caught: treating the first snapshot as both warning and critical crossings.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");

        StatusAlert alert = Assert.Single(watcher.Evaluate(null, Report(95, cycle)));

        Assert.Equal(AlertKind.Critical, alert.Kind);
        Assert.Equal(95, alert.Percent);
    }

    [Theory]
    [InlineData(95, AlertKind.Critical)]
    [InlineData(100, AlertKind.LimitReached)]
    public void Evaluate_DirectJumpEmitsOnlyHighestReachedState(double percent, AlertKind expected)
    {
        // Break caught: one poll jumping through multiple thresholds produces a stack of stale alerts.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");

        StatusAlert alert = Assert.Single(watcher.Evaluate(Report(79, cycle), Report(percent, cycle)));

        Assert.Equal(expected, alert.Kind);
    }

    [Fact]
    public void Evaluate_LimitReachedFiresOnlyOncePerCycle()
    {
        // Break caught: repeated 100 percent snapshots continually announce an already exhausted window.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");

        Assert.Equal(AlertKind.LimitReached, Assert.Single(watcher.Evaluate(Report(99, cycle), Report(100, cycle))).Kind);
        Assert.Empty(watcher.Evaluate(Report(100, cycle), Report(100, cycle)));
    }

    [Fact]
    public void Evaluate_AuthExpiredTransitionEmitsExactReauthenticationMessage()
    {
        // Break caught: an auth-expiry transition loses the provider's actionable re-authentication instruction.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");

        StatusAlert alert = Assert.Single(watcher.Evaluate(
            Report(10, cycle),
            Report(10, cycle, HealthState.AuthExpired)));

        Assert.Equal(AlertKind.AuthExpired, alert.Kind);
        Assert.Equal("re-auth: run claude login", alert.Message);
        Assert.Null(alert.WindowLabel);
        Assert.Null(alert.CycleResetsAt);
    }

    [Fact]
    public void Evaluate_FreshWatcherWithPersistentlyExpiredProviderIsSilent()
    {
        // Break caught: a watcher created after expiry announces it without an initial snapshot or health transition.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
        StatusReport expired = Report(10, cycle, HealthState.AuthExpired);

        Assert.Empty(watcher.Evaluate(expired, expired));
    }

    [Fact]
    public void Evaluate_InitiallyAuthExpiredEmitsOnceThenRecoversAndReexpires()
    {
        // Break caught: initial expiry is silent or recovery fails to re-arm a later expiry notification.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
        StatusReport expired = Report(10, cycle, HealthState.AuthExpired);
        StatusReport recovered = Report(10, cycle);

        Assert.Equal(AlertKind.AuthExpired, Assert.Single(watcher.Evaluate(null, expired)).Kind);
        Assert.Empty(watcher.Evaluate(expired, expired));
        Assert.Empty(watcher.Evaluate(expired, recovered));
        Assert.Equal(AlertKind.AuthExpired, Assert.Single(watcher.Evaluate(recovered, expired)).Kind);
    }

    [Fact]
    public void Evaluate_UnreachableOllamaNeverEmitsAlert()
    {
        // Break caught: local Ollama availability failures are presented as quota or authentication alerts.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");

        Assert.Empty(watcher.Evaluate(null, Report(100, cycle, HealthState.Unreachable, "ollama")));
    }

    [Fact]
    public void Evaluate_DisappearedWindowClearsItsFiredCycleKey()
    {
        // Break caught: an alert key survives a removed provider/window and suppresses a later reappearance.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
        StatusReport warning = Report(80, cycle);
        StatusReport empty = StatusReport.Empty(DateTimeOffset.Parse("2026-08-25T12:05:00Z"));

        Assert.Equal(AlertKind.Warning, Assert.Single(watcher.Evaluate(Report(79, cycle), warning)).Kind);
        Assert.Empty(watcher.Evaluate(warning, empty));
        Assert.Equal(AlertKind.Warning, Assert.Single(watcher.Evaluate(null, warning)).Kind);
    }

    [Fact]
    public void Evaluate_MatchesPreviousWindowByExactLabel()
    {
        // Break caught: a prior percentage from a differently named window is used to infer a crossing.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
        StatusReport previous = Report(
            "claude",
            "Claude",
            HealthState.Ok,
            [new UsageWindow("weekly", 99, cycle, Severity.Critical)]);
        StatusReport next = Report(
            "claude",
            "Claude",
            HealthState.Ok,
            [new UsageWindow("five-hour", 80, cycle, Severity.Warning)]);

        Assert.Equal(AlertKind.Warning, Assert.Single(watcher.Evaluate(previous, next)).Kind);
    }

    [Fact]
    public void Evaluate_DifferentProviderHasItsOwnFiredCycleKey()
    {
        // Break caught: an alert from one provider suppresses the same window and threshold for another provider.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");

        watcher.Evaluate(Report(79, cycle), Report(80, cycle));

        StatusAlert alert = Assert.Single(watcher.Evaluate(
            Report(79, cycle, HealthState.Ok, "codex"),
            Report(80, cycle, HealthState.Ok, "codex")));

        Assert.Equal("codex", alert.ProviderId);
        Assert.Equal(AlertKind.Warning, alert.Kind);
    }

    [Fact]
    public void Evaluate_DifferentWindowLabelHasItsOwnFiredCycleKey()
    {
        // Break caught: an alert for one provider window suppresses another exact window label in the same cycle.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");

        watcher.Evaluate(Report(79, cycle), Report(80, cycle));

        StatusAlert alert = Assert.Single(watcher.Evaluate(
            Report(79, cycle, HealthState.Ok, "claude", "weekly"),
            Report(80, cycle, HealthState.Ok, "claude", "weekly")));

        Assert.Equal("weekly", alert.WindowLabel);
        Assert.Equal(AlertKind.Warning, alert.Kind);
    }

    [Fact]
    public void Evaluate_StaleWindowDoesNotClearFutureCycleKey()
    {
        // Break caught: an out-of-order earlier reset timestamp clears a key from a later active cycle.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset first = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
        DateTimeOffset second = first.AddDays(7);

        watcher.Evaluate(Report(79, second), Report(80, second));
        watcher.Evaluate(Report(80, first), Report(80, first));

        Assert.Empty(watcher.Evaluate(Report(79, second), Report(80, second)));
    }

    [Fact]
    public void UpdateThresholds_PreservesFiredKeysAndDoesNotDuplicateAlert()
    {
        // Break caught: recreating or resetting the watcher when settings change repeats an already-fired alert.
        var watcher = new ThresholdWatcher(80, 95);
        DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");

        Assert.Equal(AlertKind.Warning, Assert.Single(watcher.Evaluate(Report(79, cycle), Report(80, cycle))).Kind);
        watcher.UpdateThresholds(81, 96);

        Assert.Empty(watcher.Evaluate(Report(80, cycle), Report(81, cycle)));
    }
}
