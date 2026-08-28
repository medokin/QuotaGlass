using QuotaGlass.Model;

namespace QuotaGlass.Ui;

public static class ProviderDisplayText
{
    public static string GetHealthText(ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            return snapshot.Error;
        }

        return snapshot.Health switch
        {
            HealthState.Ok => string.Empty,
            HealthState.Degraded => "Provider data is degraded",
            HealthState.AuthExpired => "Authentication expired",
            HealthState.Unreachable => "Provider is unreachable",
            _ => string.Empty,
        };
    }

    public static string GetUpdatedText(
        ProviderSnapshot snapshot,
        TimeSpan activePollInterval,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (activePollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(activePollInterval));
        }

        TimeSpan age = (timeProvider ?? TimeProvider.System).GetUtcNow() - snapshot.FetchedAt;
        if (snapshot.ConsecutiveFailures <= 0 && age <= activePollInterval * 2)
        {
            return string.Empty;
        }

        return $"Updated {FormatAge(age)}";
    }

    public static string FormatPercent(double? percent) => percent is double value
        ? FormattableString.Invariant($"{value:0.#}%")
        : string.Empty;

    public static string FormatResetPrefix(DateTimeOffset? resetsAt) => resetsAt is null ? string.Empty : "resets";

    private static string FormatAge(TimeSpan age)
    {
        if (age <= TimeSpan.Zero)
        {
            return "now";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return FormattableString.Invariant($"{Math.Max(1, (int)age.TotalSeconds)}s ago");
        }

        if (age < TimeSpan.FromHours(1))
        {
            return FormattableString.Invariant($"{(int)age.TotalMinutes}m ago");
        }

        if (age < TimeSpan.FromDays(1))
        {
            return FormattableString.Invariant($"{(int)age.TotalHours}h ago");
        }

        return FormattableString.Invariant($"{(int)age.TotalDays}d ago");
    }
}
