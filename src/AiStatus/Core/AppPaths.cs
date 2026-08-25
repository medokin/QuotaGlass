using System.IO;

namespace AiStatus.Core;

public sealed record AppPaths(
    string ClaudeCredentialsPath,
    string CodexAuthPath,
    string SettingsPath,
    string LogPath)
{
    public static AppPaths FromEnvironment()
    {
        string userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetEnvironmentVariable("APPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string applicationDirectory = Path.Combine(appData, "ai-status");

        return new AppPaths(
            Path.Combine(userProfile, ".claude", ".credentials.json"),
            Path.Combine(userProfile, ".codex", "auth.json"),
            Path.Combine(applicationDirectory, "settings.json"),
            Path.Combine(applicationDirectory, "log.txt"));
    }
}
