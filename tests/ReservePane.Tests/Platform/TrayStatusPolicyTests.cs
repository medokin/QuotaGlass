using System.Collections.Immutable;
using ReservePane.Model;
using ReservePane.Platform;

namespace ReservePane.Tests.Platform;

public sealed class TrayStatusPolicyTests
{
    [Fact]
    public void GetState_UsesWorstPercentageAcrossProviders()
    {
        StatusReport report = Report(
            Provider("claude", HealthState.Ok, 79),
            Provider("codex", HealthState.Ok, 96));

        TrayState state = TrayStatusPolicy.GetState(report, 80, 95);

        Assert.Equal(TrayState.Red, state);
    }

    [Theory]
    [InlineData(79.99, TrayState.Green)]
    [InlineData(80, TrayState.Amber)]
    [InlineData(94.99, TrayState.Amber)]
    [InlineData(95, TrayState.Red)]
    public void GetState_UsesInclusiveConfiguredThresholds(double percent, TrayState expected)
    {
        TrayState state = TrayStatusPolicy.GetState(
            Report(Provider("claude", HealthState.Ok, percent)),
            80,
            95);

        Assert.Equal(expected, state);
    }

    [Fact]
    public void GetState_ReturnsGreyWhenEveryProviderIsUnreachable()
    {
        StatusReport report = Report(
            Provider("claude", HealthState.Unreachable),
            Provider("codex", HealthState.Unreachable));

        TrayState state = TrayStatusPolicy.GetState(report, 80, 95);

        Assert.Equal(TrayState.Grey, state);
    }

    [Fact]
    public void GetState_ReturnsGreenWhenHealthyProviderIsBelowWarningAlongsideUnreachableProvider()
    {
        StatusReport report = Report(
            Provider("ollama", HealthState.Unreachable),
            Provider("claude", HealthState.Ok, 42));

        TrayState state = TrayStatusPolicy.GetState(report, 80, 95);

        Assert.Equal(TrayState.Green, state);
    }

    [Theory]
    [InlineData(HealthState.AuthExpired)]
    [InlineData(HealthState.Degraded)]
    public void GetState_ReturnsRedForFailedProviderWithoutWindows(HealthState health)
    {
        TrayState state = TrayStatusPolicy.GetState(
            Report(Provider("claude", health)),
            80,
            95);

        Assert.Equal(TrayState.Red, state);
    }

    private static StatusReport Report(params ProviderSnapshot[] providers) =>
        new(
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            providers.ToImmutableArray());

    private static ProviderSnapshot Provider(
        string id,
        HealthState health,
        double? percent = null) =>
        new(
            id,
            id,
            health,
            null,
            percent is double value
                ? [new UsageWindow("cycle", value, null, Severity.Normal)]
                : ImmutableArray<UsageWindow>.Empty,
            ImmutableArray<InfoLine>.Empty,
            null,
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            0);
}
