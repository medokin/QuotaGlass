namespace QuotaGlass.Model;

public sealed record UsageWindow(
    string Label, double? Percent, DateTimeOffset? ResetsAt, Severity Severity);
