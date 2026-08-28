using System.Diagnostics;
using System.IO;

namespace ReservePane.Providers;

internal static class ClaudeCredentialRefresher
{
    internal static Task<bool> RefreshAsync(CancellationToken cancellationToken) =>
        RunAsync(CreateStartInfo(), cancellationToken);

    internal static ProcessStartInfo CreateStartInfo()
    {
        string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("claude");
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--json");
        return startInfo;
    }

    internal static async Task<bool> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return false;
        }

        Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        Task exit = process.WaitForExitAsync(cancellationToken);

        try
        {
            await foreach (Task completed in Task.WhenEach(stdout, stderr, exit).ConfigureAwait(false))
            {
                await completed.ConfigureAwait(false);
            }

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            _ = ObserveAsync(stdout);
            _ = ObserveAsync(stderr);
            _ = ObserveAsync(exit);
            throw;
        }
        catch
        {
            TryKill(process);
            await ObserveCleanupAsync(process, stdout, stderr).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ObserveCleanupAsync(Process process, Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(
                    ObserveAsync(process.WaitForExitAsync(CancellationToken.None)),
                    ObserveAsync(stdout),
                    ObserveAsync(stderr))
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or
            IOException or
            InvalidOperationException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
