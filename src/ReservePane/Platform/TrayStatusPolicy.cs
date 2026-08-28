using ReservePane.Model;

namespace ReservePane.Platform;

public enum TrayState
{
    Green,
    Amber,
    Red,
    Grey,
}

public static class TrayStatusPolicy
{
    public static TrayState GetState(
        StatusReport report,
        double warningPercent,
        double criticalPercent)
    {
        ArgumentNullException.ThrowIfNull(report);
        _ = SeverityPolicy.FromPercent(null, warningPercent, criticalPercent);

        bool hasAvailableProvider = false;
        double worstPercent = 0;

        foreach (ProviderSnapshot provider in report.Providers)
        {
            if (provider.Health is HealthState.AuthExpired or HealthState.Degraded)
            {
                return TrayState.Red;
            }

            if (provider.Health == HealthState.Unreachable)
            {
                continue;
            }

            hasAvailableProvider = true;
            foreach (UsageWindow window in provider.Windows)
            {
                if (window.Percent is double percent)
                {
                    worstPercent = Math.Max(worstPercent, percent);
                }
            }
        }

        if (!hasAvailableProvider)
        {
            return TrayState.Grey;
        }

        if (worstPercent >= criticalPercent)
        {
            return TrayState.Red;
        }

        return worstPercent >= warningPercent ? TrayState.Amber : TrayState.Green;
    }
}
