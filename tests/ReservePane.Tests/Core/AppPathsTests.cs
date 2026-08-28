using ReservePane.Core;

namespace ReservePane.Tests.Core;

public sealed class AppPathsTests
{
    [Fact]
    public void FromEnvironment_StoresApplicationStateUnderReservePaneDirectory()
    {
        // Break caught: the clean rename leaves settings or logs under another product directory.
        AppPaths paths = AppPaths.FromEnvironment();
        string? settingsDirectory = Path.GetDirectoryName(paths.SettingsPath);

        Assert.Equal("ReservePane", Path.GetFileName(settingsDirectory));
        Assert.Equal(settingsDirectory, Path.GetDirectoryName(paths.LogPath));
        Assert.EndsWith(
            Path.Combine(".local", "share", "opencode", "auth.json"),
            paths.OpenCodeAuthPath,
            StringComparison.OrdinalIgnoreCase);
    }
}
