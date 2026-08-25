using System.Collections.Immutable;

namespace AiStatus.Core;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Custom,
}

public sealed record ProviderSettings(bool Enabled);

public sealed record OverlayPosition(double X, double Y);

public sealed record AppSettings(
    TimeSpan PollInterval,
    TimeSpan IdleInterval,
    ImmutableDictionary<string, ProviderSettings> Providers,
    bool OverlayVisible,
    OverlayCorner OverlayCorner,
    string? OverlayMonitorId,
    OverlayPosition? OverlayPosition,
    string Hotkey,
    double WarningPercent,
    double CriticalPercent,
    bool Autostart)
{
    public static AppSettings Default { get; } = new(
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
        ImmutableDictionary<string, ProviderSettings>.Empty
            .Add("claude", new(true))
            .Add("codex", new(true))
            .Add("ollama", new(true)),
        false,
        OverlayCorner.BottomRight,
        null,
        null,
        "Ctrl+Alt+A",
        80,
        95,
        false);
}
