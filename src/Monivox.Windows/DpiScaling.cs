using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Monivox.Windows;

/// <summary>
/// Liest und setzt die Windows-Skalierung ("Größe von Text, Apps und anderen Elementen") je Monitor.
/// Neue virtuelle Monitore bekommen von Windows eine eigene Empfehlung, die oft viel zu groß ist
/// (z. B. 150–200 % auf einem 57"-Monitor) – deshalb übernehmen sie standardmäßig die Skalierung des
/// aufgeteilten Monitors. Nutzt dieselbe Schnittstelle wie die Windows-Einstellungen (DisplayConfig).
/// </summary>
[SupportedOSPlatform("windows")]
public static class DpiScaling
{
    private static readonly int[] Steps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>Aktuelle Skalierung in Prozent, oder null, falls unbekannt.</summary>
    public static int? Get(string gdiDeviceName)
    {
        if (!Find(gdiDeviceName, out var adapter, out uint source)) return null;
        var info = Query(adapter, source);
        if (info is null) return null;
        int recommended = -info.Value.Min;
        int idx = Math.Clamp(recommended + info.Value.Cur, 0, Steps.Length - 1);
        return Steps[idx];
    }

    /// <summary>Setzt die Skalierung (auf die nächste zulässige Stufe gerundet). Gibt die gesetzte Stufe zurück.</summary>
    public static int? Set(string gdiDeviceName, int percent)
    {
        if (!Find(gdiDeviceName, out var adapter, out uint source)) return null;
        var info = Query(adapter, source);
        if (info is null) return null;
        int recommended = -info.Value.Min;
        int maxIdx = recommended + info.Value.Max;
        int target = 0;
        for (int i = 0; i < Steps.Length; i++)
            if (Math.Abs(Steps[i] - percent) < Math.Abs(Steps[target] - percent)) target = i;
        target = Math.Clamp(target, 0, Math.Min(maxIdx, Steps.Length - 1));
        int rel = target - recommended;
        if (rel == info.Value.Cur) return Steps[target];

        var set = new DPI_SET
        {
            header = new HEADER { type = SET_DPI_SCALE, size = (uint)Marshal.SizeOf<DPI_SET>(), adapterId = adapter, id = source },
            scaleRel = rel,
        };
        return DisplayConfigSetDeviceInfo(ref set) == 0 ? Steps[target] : null;
    }

    private static (int Min, int Cur, int Max)? Query(LUID adapter, uint source)
    {
        var get = new DPI_GET { header = new HEADER { type = GET_DPI_SCALE, size = (uint)Marshal.SizeOf<DPI_GET>(), adapterId = adapter, id = source } };
        if (DisplayConfigGetDeviceInfo(ref get) != 0) return null;
        return (get.minScaleRel, get.curScaleRel, get.maxScaleRel);
    }

    private static bool Find(string gdiDeviceName, out LUID adapter, out uint source)
    {
        adapter = default;
        source = 0;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) return false;
        var paths = new PATH_INFO[pathCount];
        var modes = new byte[Math.Max(1, modeCount) * 64];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return false;
        for (int i = 0; i < pathCount; i++)
        {
            var name = new SOURCE_NAME
            {
                header = new HEADER { type = GET_SOURCE_NAME, size = (uint)Marshal.SizeOf<SOURCE_NAME>(), adapterId = paths[i].sourceAdapterId, id = paths[i].sourceId },
            };
            if (DisplayConfigGetDeviceInfo(ref name) != 0) continue;
            if (!string.Equals(name.viewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
            adapter = paths[i].sourceAdapterId;
            source = paths[i].sourceId;
            return true;
        }
        return false;
    }

    // ---- Win32 (DisplayConfig; DPI-Typen -3/-4 werden von den Windows-Einstellungen selbst verwendet) ----
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const int GET_SOURCE_NAME = 1, GET_DPI_SCALE = -3, SET_DPI_SCALE = -4;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HEADER { public int type; public uint size; public LUID adapterId; public uint id; }

    // DISPLAYCONFIG_PATH_INFO: Quelle (20 Byte) + Ziel (48 Byte) + Flags (4 Byte) = 72 Byte
    [StructLayout(LayoutKind.Sequential, Size = 72)]
    private struct PATH_INFO { public LUID sourceAdapterId; public uint sourceId; public uint sourceModeInfoIdx; public uint sourceStatusFlags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_NAME { public HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_GET { public HEADER header; public int minScaleRel; public int curScaleRel; public int maxScaleRel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_SET { public HEADER header; public int scaleRel; }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PATH_INFO[] paths, ref uint modeCount, [Out] byte[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME packet);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DPI_GET packet);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DPI_SET packet);
}
