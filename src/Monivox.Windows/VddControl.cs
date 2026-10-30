using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using Monivox.Core;

namespace Monivox.Windows;

/// <summary>
/// Steuert den mitgelieferten, offiziell signierten "Virtual Display Driver" (VDD, MIT-Lizenz,
/// github.com/VirtualDrivers/Virtual-Display-Driver, signiert über die SignPath Foundation).
/// Kein Testsignatur-Modus, kein selbst ausgestelltes Zertifikat, Secure Boot bleibt an.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VddControl
{
    public const string HardwareId = @"Root\MttVDD";
    public const string PipeName = "MTTVirtualDisplayPipe";
    private const string VddRegistryKey = @"SOFTWARE\MikeTheTech\VirtualDisplayDriver";
    private const string MonivoxRegistryKey = @"SOFTWARE\Monivox";

    /// <summary>Ordner mit dem Treiberpaket innerhalb der MONIVOX-Installation.</summary>
    public static string PackageDirectory => Path.Combine(AppContext.BaseDirectory, "vdd");

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Ordner, aus dem der Treiber seine vdd_settings.xml liest.</summary>
    public static string ConfigDirectory
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(VddRegistryKey);
            return key?.GetValue("VDDPATH") is string p && !string.IsNullOrWhiteSpace(p) ? p : @"C:\VirtualDisplayDriver";
        }
    }

    public static string SettingsPath => Path.Combine(ConfigDirectory, "vdd_settings.xml");

    /// <summary>Geräteinstanzen des VDD (von MONIVOX oder bereits vorher installiert).</summary>
    public static IReadOnlyList<string> FindDeviceInstances()
    {
        var result = new List<string>();
        using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        if (enumKey is null) return result;
        foreach (var bus in new[] { "ROOT", "SWD" })
        {
            using var busKey = enumKey.OpenSubKey(bus);
            if (busKey is null) continue;
            foreach (var deviceName in busKey.GetSubKeyNames())
            {
                using var deviceKey = busKey.OpenSubKey(deviceName);
                if (deviceKey is null) continue;
                foreach (var instance in deviceKey.GetSubKeyNames())
                {
                    using var instKey = deviceKey.OpenSubKey(instance);
                    if (instKey?.GetValue("HardwareID") is string[] ids &&
                        ids.Any(id => id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add($@"{bus}\{deviceName}\{instance}");
                    }
                }
            }
        }
        return result;
    }

    public static bool IsInstalled => FindDeviceInstances().Count > 0;

    /// <summary>
    /// Installiert den Treiber aus dem mitgelieferten Paket: Zertifikate aus dem Katalog als
    /// vertrauenswürdige Herausgeber eintragen (wie das offizielle Installationsskript), Treiber in den
    /// Driver Store aufnehmen, Gerät anlegen. Benötigt Adminrechte.
    /// </summary>
    public static void Install(Action<string> log)
    {
        if (!IsAdministrator()) throw new InvalidOperationException("Für die Treiberinstallation werden Administratorrechte benötigt.");

        string inf = Path.Combine(PackageDirectory, "MttVDD.inf");
        string cat = Path.Combine(PackageDirectory, "mttvdd.cat");
        if (!File.Exists(inf) || !File.Exists(cat))
            throw new InvalidOperationException($"Treiberpaket fehlt in {PackageDirectory}.");

        // Signaturzertifikate aus dem Katalog als "Vertrauenswürdige Herausgeber" eintragen,
        // damit Windows ohne Rückfrage installiert. Die Kette führt zu einer öffentlichen Zertifizierungsstelle.
        var certs = new X509Certificate2Collection();
        certs.Import(File.ReadAllBytes(cat));
        using (var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var cert in certs) store.Add(cert);
        }
        log("Treiberzertifikat eingetragen.");

        EnsureSettingsFile();

        Run("pnputil.exe", $"/add-driver \"{inf}\" /install", log, allowed: [0, 259, 3010]);
        if (FindDeviceInstances().Count == 0)
        {
            Run(Path.Combine(AppContext.BaseDirectory, "MonivoxDevice.exe"), "create", log, allowed: [0]);
            using var key = Registry.LocalMachine.CreateSubKey(MonivoxRegistryKey);
            key.SetValue("InstalledVdd", 1, RegistryValueKind.DWord);
        }
        log("Anzeigetreiber installiert.");
    }

    /// <summary>Entfernt den Treiber – aber nur, wenn MONIVOX ihn selbst installiert hat.</summary>
    public static void Uninstall(Action<string> log)
    {
        using var key = Registry.LocalMachine.OpenSubKey(MonivoxRegistryKey);
        if (key?.GetValue("InstalledVdd") is not int installed || installed != 1)
        {
            log("Anzeigetreiber war schon vor MONIVOX installiert – bleibt erhalten.");
            return;
        }
        Run(Path.Combine(AppContext.BaseDirectory, "MonivoxDevice.exe"), "remove", log, allowed: [0, 1]);
        Registry.LocalMachine.DeleteSubKeyTree(MonivoxRegistryKey, throwOnMissingSubKey: false);
        log("Anzeigetreiber entfernt.");
    }

    private static void EnsureSettingsFile()
    {
        if (File.Exists(SettingsPath)) return;
        Directory.CreateDirectory(ConfigDirectory);
        string bundled = Path.Combine(PackageDirectory, "vdd_settings.xml");
        if (File.Exists(bundled)) File.Copy(bundled, SettingsPath);
        else VddSettings.CreateDefault().Save(SettingsPath);
    }

    /// <summary>
    /// Stellt sicher, dass genug virtuelle Monitore mit den benötigten Modi existieren, und lädt den Treiber
    /// bei Bedarf neu. Gibt true zurück, wenn ein Neuladen ausgelöst wurde.
    /// </summary>
    public static bool Configure(int monitorCount, IReadOnlyCollection<DisplayMode> modes, Action<string> log)
    {
        EnsureSettingsFile();
        XDocument doc;
        try { doc = XDocument.Load(SettingsPath, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException)
        {
            File.Copy(SettingsPath, SettingsPath + ".defekt", overwrite: true);
            doc = VddSettings.CreateDefault();
        }

        if (!VddSettings.Apply(doc, monitorCount, modes)) return false;
        doc.Save(SettingsPath);
        log($"Anzeigetreiber: {monitorCount} virtuelle Monitore, Modi {string.Join(", ", modes.Distinct())}.");
        Reload(log);
        return true;
    }

    /// <summary>Lädt die Treiberkonfiguration neu (Pipe-Befehl), sonst Geräteneustart.</summary>
    public static void Reload(Action<string> log)
    {
        if (TrySendPipeCommand("RELOAD_DRIVER")) return;
        log("Treiber-Pipe nicht erreichbar – starte Gerät neu.");
        RestartDevice(log);
    }

    public static void RestartDevice(Action<string> log)
    {
        foreach (var instance in FindDeviceInstances())
            Run("pnputil.exe", $"/restart-device \"{instance}\"", log, allowed: [0, 3010]);
    }

    public static bool TrySendPipeCommand(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(2000);
            byte[] data = Encoding.Unicode.GetBytes(command);
            pipe.Write(data, 0, data.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Run(string file, string args, Action<string> log, int[] allowed)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} konnte nicht gestartet werden.");
        string output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        if (!allowed.Contains(p.ExitCode))
            throw new InvalidOperationException($"{Path.GetFileName(file)} {args} ist fehlgeschlagen ({p.ExitCode}): {output}");
    }
}
