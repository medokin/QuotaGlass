using System.Collections.Immutable;
using AiStatus.Model;

namespace AiStatus.Core;

public sealed class ThresholdWatcher
{
    private readonly HashSet<FiredKey> _fired = [];
    private double _warningPercent;
    private double _criticalPercent;

    public ThresholdWatcher(double warningPercent, double criticalPercent)
    {
        ValidateThresholds(warningPercent, criticalPercent);
        _warningPercent = warningPercent;
        _criticalPercent = criticalPercent;
    }

    public void UpdateThresholds(double warningPercent, double criticalPercent)
    {
        ValidateThresholds(warningPercent, criticalPercent);
        _warningPercent = warningPercent;
        _criticalPercent = criticalPercent;
    }

    public ImmutableArray<StatusAlert> Evaluate(StatusReport? previous, StatusReport next)
    {
        ArgumentNullException.ThrowIfNull(next);

        RemoveStaleKeys(next);
        var alerts = ImmutableArray.CreateBuilder<StatusAlert>();

        foreach (ProviderSnapshot provider in next.Providers)
        {
            ProviderSnapshot? previousProvider = previous?.Providers
                .FirstOrDefault(candidate => candidate.Id == provider.Id);

            if (provider.Health == HealthState.AuthExpired)
            {
                if (previous is null || previousProvider?.Health != HealthState.AuthExpired)
                {
                    AddAuthExpiredAlert(provider, alerts);
                }

                continue;
            }

            _fired.Remove(new FiredKey(provider.Id, null, AlertKind.AuthExpired, null));

            if (provider.Id == "ollama" && provider.Health == HealthState.Unreachable)
            {
                continue;
            }

            foreach (UsageWindow window in provider.Windows)
            {
                if (window.Percent is not double percent)
                {
                    continue;
                }

                double previousPercent = PreviousPercent(previousProvider, window.Label);
                AlertKind? kind = CrossedKind(previousPercent, percent);
                if (kind is null)
                {
                    continue;
                }

                var key = new FiredKey(provider.Id, window.Label, kind.Value, window.ResetsAt);
                if (_fired.Add(key))
                {
                    alerts.Add(new StatusAlert(
                        provider.Id,
                        provider.Label,
                        window.Label,
                        kind.Value,
                        percent,
                        window.ResetsAt,
                        ThresholdMessage(window.Label, percent, kind.Value)));
                }
            }
        }

        return alerts.ToImmutable();
    }

    private void AddAuthExpiredAlert(
        ProviderSnapshot provider,
        ImmutableArray<StatusAlert>.Builder alerts)
    {
        var key = new FiredKey(provider.Id, null, AlertKind.AuthExpired, null);
        if (_fired.Add(key))
        {
            alerts.Add(new StatusAlert(
                provider.Id,
                provider.Label,
                null,
                AlertKind.AuthExpired,
                null,
                null,
                provider.Error ?? "re-authentication required"));
        }
    }

    private AlertKind? CrossedKind(double previousPercent, double percent)
    {
        if (percent >= 100 && previousPercent < 100)
        {
            return AlertKind.LimitReached;
        }

        if (percent >= _criticalPercent && previousPercent < _criticalPercent)
        {
            return AlertKind.Critical;
        }

        if (percent >= _warningPercent && previousPercent < _warningPercent)
        {
            return AlertKind.Warning;
        }

        return null;
    }

    private static double PreviousPercent(ProviderSnapshot? provider, string windowLabel)
    {
        UsageWindow? window = provider?.Windows
            .FirstOrDefault(candidate => candidate.Label == windowLabel);

        return window?.Percent ?? 0;
    }

    private void RemoveStaleKeys(StatusReport next)
    {
        _fired.RemoveWhere(key => IsStale(key, next));
    }

    private static bool IsStale(FiredKey key, StatusReport next)
    {
        ProviderSnapshot? provider = next.Providers
            .FirstOrDefault(candidate => candidate.Id == key.ProviderId);
        if (provider is null)
        {
            return true;
        }

        if (key.Kind == AlertKind.AuthExpired)
        {
            return provider.Health != HealthState.AuthExpired;
        }

        UsageWindow? window = provider.Windows
            .FirstOrDefault(candidate => candidate.Label == key.WindowLabel);
        if (window is null)
        {
            return true;
        }

        return key.CycleResetsAt is not null
            && window.ResetsAt is not null
            && key.CycleResetsAt < window.ResetsAt;
    }

    private static string ThresholdMessage(string windowLabel, double percent, AlertKind kind) => kind switch
    {
        AlertKind.Warning => $"{windowLabel} usage reached {percent:0.##}%.",
        AlertKind.Critical => $"{windowLabel} usage reached {percent:0.##}%.",
        AlertKind.LimitReached => $"{windowLabel} usage limit reached.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void ValidateThresholds(double warningPercent, double criticalPercent)
    {
        _ = SeverityPolicy.FromPercent(null, warningPercent, criticalPercent);
    }

    private readonly record struct FiredKey(
        string ProviderId,
        string? WindowLabel,
        AlertKind Kind,
        DateTimeOffset? CycleResetsAt);
}
