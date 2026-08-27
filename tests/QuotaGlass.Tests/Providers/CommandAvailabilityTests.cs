using QuotaGlass.Providers;

namespace QuotaGlass.Tests.Providers;

public sealed class CommandAvailabilityTests
{
    [Fact]
    public void IsAvailable_FindsWindowsCommandShimThroughPathExt()
    {
        // Catches command discovery that checks only extensionless executables and misses .cmd shims.
        bool available = CommandAvailability.IsAvailable(
            "opencode",
            @";C:\first;;C:\tools;",
            ".EXE;.CMD",
            path => path == @"C:\tools\opencode.CMD");

        Assert.True(available);
    }

    [Fact]
    public void IsAvailable_FindsExtensionlessCommandOnPath()
    {
        // Catches command discovery that checks only PATHEXT-derived candidates.
        bool available = CommandAvailability.IsAvailable(
            "opencode",
            @"C:\tools",
            ".EXE;.CMD",
            path => path == @"C:\tools\opencode");

        Assert.True(available);
    }

    [Fact]
    public void IsAvailable_RejectsCommandNamesContainingPaths()
    {
        // Catches a caller-supplied relative path escaping PATH-only discovery.
        bool available = CommandAvailability.IsAvailable(
            @"..\opencode",
            @"C:\tools",
            ".CMD",
            _ => true);

        Assert.False(available);
    }
}
