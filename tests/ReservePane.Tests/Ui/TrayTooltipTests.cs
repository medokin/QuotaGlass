using System.Collections.Immutable;
using System.Text;
using ReservePane.Model;
using ReservePane.Ui;

namespace ReservePane.Tests.Ui;

public sealed class TrayTooltipTests
{
    [Fact]
    public void Format_UsesOneProviderLineWithOnlyLabelAndWorstPercentage()
    {
        DateTimeOffset reset = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
        StatusReport report = Report(
            Snapshot("claude", "Claude", "team_standard", Window("session", 2, reset), Window("weekly", 95, reset)),
            Snapshot("codex", "Codex", "prolite", Window("7d", 2, reset)),
            Snapshot("ollama", "Ollama", null));

        string tooltip = TrayTooltip.Format(report);

        Assert.Equal("Claude 95%\nCodex 2%\nOllama", tooltip);
        Assert.DoesNotContain("team_standard", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("prolite", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("weekly", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_CapsUtf16LengthWithoutSplittingNonBmpLabels()
    {
        string longLabel = string.Concat(Enumerable.Repeat("quota-\U0001F680", 24));
        StatusReport report = Report(
            Snapshot("claude", longLabel, null, Window("weekly", 95, null)),
            Snapshot("codex", longLabel, null, Window("7d", 81.25, null)),
            Snapshot("ollama", longLabel, null));

        string tooltip = TrayTooltip.Format(report);
        string[] lines = tooltip.Split('\n');

        Assert.True(tooltip.Length <= 127, $"Tooltip used {tooltip.Length} UTF-16 code units.");
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"\.\.\. 95%$", lines[0]);
        Assert.Matches(@"\.\.\. 81\.25%$", lines[1]);
        Assert.EndsWith("...", lines[2], StringComparison.Ordinal);
        Assert.DoesNotContain(tooltip.EnumerateRunes(), rune => rune == Rune.ReplacementChar);
    }

    [Fact]
    public void Format_ReplacesEmbeddedLineBreaksSoProviderCountStaysStable()
    {
        StatusReport report = Report(
            Snapshot("claude", "Claude\r\nInjected", null),
            Snapshot("codex", "Codex", null),
            Snapshot("ollama", "Ollama", null));

        string[] lines = TrayTooltip.Format(report).Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Equal("Claude  Injected", lines[0]);
    }

    [Fact]
    public void Format_CapsTooltipWhenVendorPercentageHasExtremeMagnitude()
    {
        StatusReport report = Report(
            Snapshot("claude", "Claude", null, Window("weekly", double.MaxValue, null)),
            Snapshot("codex", "Codex", null),
            Snapshot("ollama", "Ollama", null));

        string tooltip = TrayTooltip.Format(report);

        Assert.True(tooltip.Length <= 127, $"Tooltip used {tooltip.Length} UTF-16 code units.");
        Assert.Equal(3, tooltip.Split('\n').Length);
    }

    private static StatusReport Report(params ProviderSnapshot[] providers) =>
        new(DateTimeOffset.Parse("2026-08-25T12:00:00Z"), providers.ToImmutableArray());

    private static ProviderSnapshot Snapshot(
        string id,
        string label,
        string? plan,
        params UsageWindow[] windows) =>
        new(
            id,
            label,
            HealthState.Ok,
            plan,
            windows.ToImmutableArray(),
            [],
            null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            0);

    private static UsageWindow Window(string label, double percent, DateTimeOffset? reset) =>
        new(label, percent, reset, Severity.Normal);
}
