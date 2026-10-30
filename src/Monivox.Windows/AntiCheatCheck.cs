using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Monivox.Windows;

/// <summary>
/// Prüft Dinge, die mit Anti-Cheat-Systemen kollidieren könnten.
/// MONIVOX selbst greift nie in Spiele ein: kein Injizieren, kein Lesen von Spielspeicher, kein Kernel-Treiber
/// (der Anzeigetreiber läuft im User-Mode, die Kernel-Teile stammen von Microsoft). Die Bildschirmaufnahme
/// läuft über die offizielle Windows-API (wie bei OBS, Discord-Streaming, Parsec).
/// </summary>
[SupportedOSPlatform("windows")]
public static class AntiCheatCheck
{
    public sealed record Finding(bool Critical, string Message);

    // Prozessname (ohne .exe) -> Produkt
    private static readonly Dictionary<string, string> KnownAntiCheats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vgc"] = "Riot Vanguard",
        ["vgtray"] = "Riot Vanguard",
        ["EasyAntiCheat"] = "Easy Anti-Cheat",
        ["EasyAntiCheat_EOS"] = "Easy Anti-Cheat",
        ["BEService"] = "BattlEye",
        ["BEService_x64"] = "BattlEye",
        ["faceit"] = "FACEIT",
        ["faceitservice"] = "FACEIT",
        ["FACEITService"] = "FACEIT",
        ["ESEADriver2"] = "ESEA",
        ["mhyprot2"] = "miHoYo Anti-Cheat",
        ["GameGuard"] = "nProtect GameGuard",
        ["xigncode"] = "XIGNCODE",
        ["PnkBstrA"] = "PunkBuster",
        ["RicochetAntiCheat"] = "Ricochet",
    };

    public static IReadOnlyList<Finding> Run()
    {
        var findings = new List<Finding>();

        if (IsTestSigningEnabled())
        {
            findings.Add(new Finding(true,
                "Windows läuft im Testsignatur-Modus. MONIVOX braucht ihn NICHT. Riot Vanguard, FACEIT und andere " +
                "verweigern damit den Start. Abschalten: 'bcdedit /set testsigning off' (Admin) und neu starten."));
        }

        var running = Process.GetProcesses()
            .Select(p => { try { return p.ProcessName; } catch (InvalidOperationException) { return ""; } })
            .Where(KnownAntiCheats.ContainsKey)
            .Select(n => KnownAntiCheats[n])
            .Distinct()
            .ToList();

        if (running.Count > 0)
        {
            findings.Add(new Finding(false,
                $"Anti-Cheat aktiv: {string.Join(", ", running)}. MONIVOX greift nicht in Spiele ein; virtuelle Monitore " +
                "und Bildschirmaufnahme werden genauso von OBS, Parsec oder Sunshine genutzt und sind uns als Bann-Grund " +
                "nicht bekannt. Eine Garantie kann es trotzdem nicht geben – wenn ein Spiel meckert oder nicht startet, " +
                "die Aufteilung vorher beenden (Rechtsklick aufs Tray-Symbol → Teilen beenden)."));
        }
        return findings;
    }

    // --- Testsignatur-Erkennung über die Code-Integritäts-Info des Kernels (kein Admin nötig) ---

    private const int SystemCodeIntegrityInformation = 103;
    private const uint CODEINTEGRITY_OPTION_TESTSIGN = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_CODEINTEGRITY_INFORMATION
    {
        public uint Length;
        public uint CodeIntegrityOptions;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, ref SYSTEM_CODEINTEGRITY_INFORMATION info, int length, out int returnLength);

    public static bool IsTestSigningEnabled()
    {
        var info = new SYSTEM_CODEINTEGRITY_INFORMATION { Length = (uint)Marshal.SizeOf<SYSTEM_CODEINTEGRITY_INFORMATION>() };
        int status = NtQuerySystemInformation(SystemCodeIntegrityInformation, ref info, (int)info.Length, out _);
        return status == 0 && (info.CodeIntegrityOptions & CODEINTEGRITY_OPTION_TESTSIGN) != 0;
    }
}
