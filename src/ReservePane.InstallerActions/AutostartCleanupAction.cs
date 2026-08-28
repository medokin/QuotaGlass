using System;
using WixToolset.Dtf.WindowsInstaller;

namespace ReservePane.InstallerActions;

public static class AutostartCleanupAction
{
    [CustomAction]
    public static ActionResult AddReservePaneAutostartCleanup(Session session)
    {
        try
        {
            using (View view = session.Database.OpenView(
                       "INSERT INTO `Registry` " +
                       "(`Registry`, `Root`, `Key`, `Name`, `Value`, `Component_`) " +
                       "VALUES (?, ?, ?, ?, ?, ?) TEMPORARY"))
            using (var record = new Record(6))
            {
                record[1] = "ReservePaneLegacyAutostartCleanup";
                record[2] = 1;
                record[3] = @"Software\Microsoft\Windows\CurrentVersion\Run";
                record[4] = "ReservePane";
                record[5] = $"\"{session["INSTALLFOLDER"]}ReservePane.exe\"";
                record[6] = "ReservePaneApplication";
                view.Execute(record);
            }

            session.Log("Legacy autostart cleanup added to the uninstall transaction.");
        }
        catch (Exception exception)
        {
            session.Log($"Legacy autostart cleanup could not be added: {exception.Message}");
            return ActionResult.Failure;
        }

        return ActionResult.Success;
    }
}
