using System.Collections.Immutable;
using AiStatus.Model;

namespace AiStatus.Tests.Support;

internal static class SnapshotFactory
{
    public static StatusReport Report(
        double? percent,
        DateTimeOffset resetsAt,
        HealthState health = HealthState.Ok,
        string providerId = "claude",
        string windowLabel = "five-hour") =>
        Report(
            providerId,
            providerId == "claude" ? "Claude" : providerId,
            health,
            [new UsageWindow(windowLabel, percent, resetsAt, Severity.Normal)]);

    public static StatusReport Report(
        string providerId,
        string providerLabel,
        HealthState health,
        ImmutableArray<UsageWindow> windows,
        string? error = null) =>
        new(
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            [new ProviderSnapshot(
                providerId,
                providerLabel,
                health,
                null,
                windows,
                ImmutableArray<InfoLine>.Empty,
                error ?? (health == HealthState.AuthExpired && providerId == "claude"
                    ? "re-auth: run claude login"
                    : null),
                DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
                0)]);
}
