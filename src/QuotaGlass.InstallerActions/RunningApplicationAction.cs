using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WixToolset.Dtf.WindowsInstaller;

namespace QuotaGlass.InstallerActions;

public static class RunningApplicationAction
{
    private const int MaximumPathLength = 32768;

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
                    string processPath = GetProcessPath(process);
                    if (!string.Equals(
                            processPath,
                            installedExecutable,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    session.Log("Closing the installed QuotaGlass process.");
                    _ = process.CloseMainWindow();
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

    private static string GetProcessPath(Process process)
    {
        var executablePath = new StringBuilder(MaximumPathLength);
        int pathLength = executablePath.Capacity;
        if (!QueryFullProcessImageName(process.Handle, 0, executablePath, ref pathLength))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return executablePath.ToString();
    }
}
