using ReservePane.Platform;

namespace ReservePane.Tests.Platform;

public sealed class InstallerShutdownSignalTests
{
    [Fact]
    public void FromExecutablePath_UsesReservePaneSignalPrefix()
    {
        string signal = InstallerShutdownSignalName.FromExecutablePath(
            @"C:\Users\tester\AppData\Local\Programs\ReservePane\ReservePane.exe");

        Assert.StartsWith(
            @"Local\ReservePane.InstallerShutdown.",
            signal,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FromExecutablePath_DifferentInstallPathsUseDifferentSignals()
    {
        string first = InstallerShutdownSignalName.FromExecutablePath(
            @"C:\Users\tester\AppData\Local\Programs\ReservePane\ReservePane.exe");
        string second = InstallerShutdownSignalName.FromExecutablePath(
            @"D:\Portable\ReservePane.exe");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Signal_RequestsShutdownForMatchingExecutablePath()
    {
        string executablePath = Path.Combine(
            Path.GetTempPath(),
            $"ReservePane-{Guid.NewGuid():N}",
            "ReservePane.exe");
        var requested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var signal = new InstallerShutdownSignal(
            executablePath,
            () =>
            {
                requested.SetResult();
                return Task.CompletedTask;
            });
        using EventWaitHandle sender = EventWaitHandle.OpenExisting(
            InstallerShutdownSignalName.FromExecutablePath(executablePath));

        sender.Set();

        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
