using ReservePane.Providers;

namespace ReservePane.Tests.Providers;

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

    [Fact]
    public void IsAvailable_BecomesDiscoverableAfterUserEnvironmentRefresh()
    {
        // Catches per-poll discovery reusing the stale process PATH and PATHEXT.
        bool installed = false;

        string? ReadVariable(string name, EnvironmentVariableTarget target) => (name, target) switch
        {
            ("PATH", EnvironmentVariableTarget.Process) => @"C:\existing-tools",
            ("PATHEXT", EnvironmentVariableTarget.Process) => ".EXE",
            ("PATH", EnvironmentVariableTarget.User) when installed => @"C:\new-user-tools",
            ("PATHEXT", EnvironmentVariableTarget.User) when installed => ".CMD",
            _ => null,
        };

        bool unavailable = CommandAvailability.IsAvailable(
            "opencode",
            ReadVariable,
            path => path == @"C:\new-user-tools\opencode.CMD");

        installed = true;
        bool available = CommandAvailability.IsAvailable(
            "opencode",
            ReadVariable,
            path => path == @"C:\new-user-tools\opencode.CMD");

        Assert.False(unavailable);
        Assert.True(available);
    }

    [Fact]
    public void IsAvailable_EnvironmentReadFailuresRetainUsableValuesFromOtherTargets()
    {
        // Catches one failed environment target discarding the safe process fallback.
        string? ReadVariable(string name, EnvironmentVariableTarget target) => target switch
        {
            EnvironmentVariableTarget.Process => name == "PATH" ? @"C:\process-tools" : ".CMD",
            EnvironmentVariableTarget.User => throw new System.Security.SecurityException(
                "environment-value-must-not-be-reported"),
            EnvironmentVariableTarget.Machine => throw new IOException(
                "environment-value-must-not-be-reported"),
            _ => null,
        };

        bool available = CommandAvailability.IsAvailable(
            "opencode",
            ReadVariable,
            path => path == @"C:\process-tools\opencode.CMD");

        Assert.True(available);
    }

    [Fact]
    public void IsAvailable_ProcessReadFailureUsesRefreshedUserEnvironment()
    {
        // Catches a process-environment read failure preventing registry-backed discovery.
        string? ReadVariable(string name, EnvironmentVariableTarget target) => target switch
        {
            EnvironmentVariableTarget.Process => throw new System.Security.SecurityException(
                "environment-value-must-not-be-reported"),
            EnvironmentVariableTarget.User => name == "PATH" ? @"C:\user-tools" : ".CMD",
            _ => null,
        };

        bool available = CommandAvailability.IsAvailable(
            "opencode",
            ReadVariable,
            path => path == @"C:\user-tools\opencode.CMD");

        Assert.True(available);
    }
}
