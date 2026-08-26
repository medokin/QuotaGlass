using System.Collections.Immutable;

namespace QuotaGlass.Model;

public sealed record InfoLine(string Label, string Value);

public sealed record ProviderSnapshot(
    string Id,
    string Label,
    HealthState Health,
    string? PlanLabel,
    ImmutableArray<UsageWindow> Windows,
    ImmutableArray<InfoLine> Info,
    string? Error,
    DateTimeOffset FetchedAt,
    int ConsecutiveFailures);
