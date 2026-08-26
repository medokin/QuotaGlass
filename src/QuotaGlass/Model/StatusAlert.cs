namespace QuotaGlass.Model;

public sealed record StatusAlert(
    string ProviderId,
    string ProviderLabel,
    string? WindowLabel,
    AlertKind Kind,
    double? Percent,
    DateTimeOffset? CycleResetsAt,
    string Message);
