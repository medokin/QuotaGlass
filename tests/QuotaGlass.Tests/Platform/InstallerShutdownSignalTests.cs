using QuotaGlass.Platform;

namespace QuotaGlass.Tests.Platform;

public sealed class InstallerShutdownSignalTests
{
    [Fact]
    public void FromExecutablePath_DifferentInstallPathsUseDifferentSignals()
    {
        string first = InstallerShutdownSignalName.FromExecutablePath(
            @"C:\Users\tester\AppData\Local\Programs\QuotaGlass\QuotaGlass.exe");
        string second = InstallerShutdownSignalName.FromExecutablePath(
            @"D:\Portable\QuotaGlass.exe");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Signal_RequestsShutdownForMatchingExecutablePath()
    {
        string executablePath = Path.Combine(
            Path.GetTempPath(),
            $"QuotaGlass-{Guid.NewGuid():N}",
            "QuotaGlass.exe");
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
