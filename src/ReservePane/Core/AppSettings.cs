using System.Collections.Immutable;

namespace ReservePane.Core;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Custom,
}

public sealed record OpenCodeConsoleSettings(string? WorkspaceSelector);

public sealed record ProviderSettings
{
    public OpenCodeConsoleSettings? OpenCodeConsole { get; init; }
}

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
            .Add("claude", new ProviderSettings())
            .Add("codex", new ProviderSettings())
            .Add("grok", new ProviderSettings())
            .Add("opencode-go", new ProviderSettings())
            .Add("opencode-company-seat", new ProviderSettings())
            .Add("ollama", new ProviderSettings()),
        false,
        OverlayCorner.BottomRight,
        null,
        null,
        "Ctrl+Alt+A",
        80,
        95,
        false);
}
