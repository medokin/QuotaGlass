using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using QuotaGlass.Platform;
using WixToolset.Dtf.WindowsInstaller;

namespace QuotaGlass.InstallerActions;

public static class RunningApplicationAction
{
    private const int MaximumPathLength = 32768;
    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        int desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        int flags,
        StringBuilder executablePath,
        ref int pathLength);

    [CustomAction]
    public static ActionResult CloseInstalledQuotaGlass(Session session)
    {
        string installedExecutable = Path.GetFullPath(
            Path.Combine(session["INSTALLFOLDER"], "QuotaGlass.exe"));

        foreach (Process process in Process.GetProcessesByName("QuotaGlass"))
        {
            using (process)
            {
                try
                {
                    string? processPath = TryGetProcessPath(process.Id);
                    if (processPath is null)
                    {
                        continue;
                    }
                    if (!string.Equals(
                            processPath,
                            installedExecutable,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    session.Log("Closing the installed QuotaGlass process.");
                    SignalGracefulShutdown(installedExecutable);
                    if (!process.WaitForExit(15000))
                    {
                        session.Log("QuotaGlass did not exit after 15 seconds; terminating it.");
                        process.Kill();
                        if (!process.WaitForExit(10000))
                        {
                            session.Log("QuotaGlass did not exit after termination.");
                            return ActionResult.Failure;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and inspection.
                }
                catch (Exception exception)
                {
                    session.Log($"The installed QuotaGlass process could not be closed: {exception.Message}");
                    return ActionResult.Failure;
                }
            }
        }

        return ActionResult.Success;
    }

    private static void SignalGracefulShutdown(string installedExecutable)
    {
        try
        {
            using (EventWaitHandle shutdownSignal = EventWaitHandle.OpenExisting(
                       InstallerShutdownSignalName.FromExecutablePath(installedExecutable)))
            {
                shutdownSignal.Set();
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Older or partially initialized versions do not expose the signal.
        }
    }

    private static string? TryGetProcessPath(int processId)
    {
        IntPtr processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var executablePath = new StringBuilder(MaximumPathLength);
            int pathLength = executablePath.Capacity;
            return QueryFullProcessImageName(processHandle, 0, executablePath, ref pathLength)
                ? executablePath.ToString()
                : null;
        }
        finally
        {
            _ = CloseHandle(processHandle);
        }
    }
}
