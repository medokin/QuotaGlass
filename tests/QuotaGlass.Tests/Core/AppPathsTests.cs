using QuotaGlass.Core;

namespace QuotaGlass.Tests.Core;

public sealed class AppPathsTests
{
    [Fact]
    public void FromEnvironment_StoresApplicationStateUnderQuotaGlassDirectory()
    {
        // Break caught: a rename leaves settings and logs in the obsolete application directory.
        AppPaths paths = AppPaths.FromEnvironment();
        string? settingsDirectory = Path.GetDirectoryName(paths.SettingsPath);

        Assert.Equal("QuotaGlass", Path.GetFileName(settingsDirectory));
        Assert.Equal(settingsDirectory, Path.GetDirectoryName(paths.LogPath));
    }
}
