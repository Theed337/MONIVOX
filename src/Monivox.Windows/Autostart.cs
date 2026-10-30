using System.Diagnostics;
using System.Runtime.Versioning;

namespace Monivox.Windows;

/// <summary>
/// Start mit Windows über eine geplante Aufgabe "bei Anmeldung, höchste Rechte".
/// Vorteil gegenüber dem Autostart-Ordner: keine UAC-Abfrage bei jedem Systemstart.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Autostart
{
    public const string TaskName = "MONIVOX";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static void Enable(string exePath)
    {
        // Per XML, weil schtasks-Parameter zwei wichtige Standardwerte nicht ändern können:
        // "nur im Netzbetrieb starten" (Laptops!) und "nach 3 Tagen beenden".
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><Delay>PT10S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{System.Security.SecurityElement.Escape(exePath)}</Command><Arguments>--tray</Arguments></Exec>
              </Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), "monivox-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            int rc = RunSchtasks($"/Create /F /TN \"{TaskName}\" /XML \"{file}\"");
            if (rc != 0) throw new InvalidOperationException($"Autostart konnte nicht eingerichtet werden (schtasks {rc}).");
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void Disable()
    {
        if (IsEnabled()) RunSchtasks($"/Delete /F /TN \"{TaskName}\"");
    }

    private static int RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
