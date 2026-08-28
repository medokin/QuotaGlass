using System.Diagnostics;
using QuotaGlass.Providers;
using QuotaGlass.Tests.Support;

namespace QuotaGlass.Tests.Providers;

public sealed class ClaudeCredentialRefresherTests
{
    [Fact]
    public void CreateStartInfo_RunsClaudeAuthStatusSilently()
    {
        // Catches token refresh launching an interactive window or invoking a mutable command shape.
        System.Diagnostics.ProcessStartInfo startInfo = ClaudeCredentialRefresher.CreateStartInfo();

        Assert.EndsWith("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(
            ["/d", "/c", "claude", "auth", "status", "--json"],
            startInfo.ArgumentList);
    }

    [Fact]
    public async Task RunAsync_NonzeroExit_ReturnsFalseAfterDrainingOutput()
    {
        // Catches redirected CLI output deadlocking the provider or a failed auth check being treated as refreshed.
        System.Diagnostics.ProcessStartInfo startInfo = CommandStartInfo(
            "for /L %i in (1,1,2000) do @echo authentication-output & exit /b 7");

        bool refreshed = await ClaudeCredentialRefresher.RunAsync(startInfo, CancellationToken.None);

        Assert.False(refreshed);
    }

    [Fact]
    public async Task RunAsync_Cancellation_StopsTheChildProcess()
    {
        // Catches Claude credential refresh outliving the provider timeout or application shutdown.
        using var directory = new TemporaryDirectory();
        string sentinelPath = Path.Combine(directory.Path, "child-survived.txt");
        HashSet<int> existingPingProcesses = GetProcessIds("ping");
        System.Diagnostics.ProcessStartInfo startInfo = CommandStartInfo(
            $"ping -n 3 127.0.0.1 >nul & echo survived > \"{sentinelPath}\"");
        using var cancellation = new CancellationTokenSource();

        Task<bool> refresh = ClaudeCredentialRefresher.RunAsync(startInfo, cancellation.Token);
        int childPid = 0;
        await WaitUntilAsync(
            () => TryFindNewProcessId("ping", existingPingProcesses, out childPid),
            TimeSpan.FromSeconds(3));
        Stopwatch stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Cancellation took {stopwatch.Elapsed}.");
        await WaitUntilAsync(() => !IsProcessRunning(childPid), TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.False(File.Exists(sentinelPath));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryFindNewProcessId(
        string processName,
        HashSet<int> existingProcessIds,
        out int processId)
    {
        processId = GetProcessIds(processName).FirstOrDefault(id => !existingProcessIds.Contains(id));
        return processId != 0;
    }

    private static HashSet<int> GetProcessIds(string processName)
    {
        var processIds = new HashSet<int>();
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                processIds.Add(process.Id);
            }
        }

        return processIds;
    }

    private static System.Diagnostics.ProcessStartInfo CommandStartInfo(string command)
    {
        string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = commandInterpreter,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }
}
