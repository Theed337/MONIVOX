using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Monivox.Core;
using static Monivox.Windows.Native;

namespace Monivox.Windows;

/// <summary>Ein Windows-Display-Ausgang mit (falls verbunden) angeschlossenem Monitor.</summary>
public sealed record DisplayInfo(
    string DeviceName,          // \\.\DISPLAY3
    string AdapterName,         // z. B. "NVIDIA GeForce RTX 4070" oder "Virtual Display Driver"
    string AdapterId,           // PnP-Hardware-ID des Adapters, z. B. Root\MttVDD
    string MonitorId,
    bool IsAttached,
    bool IsPrimary,
    PixelRect Bounds,
    DisplayMode Mode)
{
    public string ShortName => DeviceName.Replace(@"\\.\", "");
}

[SupportedOSPlatform("windows")]
public static class Displays
{
    /// <summary>Merkmale des mitgelieferten Virtual Display Driver (Hardware-ID bzw. Adaptername).</summary>
    public static readonly string[] DefaultVirtualAdapterMarkers = ["MttVDD", "Virtual Display"];

    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var result = new List<DisplayInfo>();
        for (uint i = 0; ; i++)
        {
            var adapter = DISPLAY_DEVICE.Create();
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;

            var monitor = DISPLAY_DEVICE.Create();
            string monitorId = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) ? monitor.DeviceID : "";
            if (string.IsNullOrEmpty(monitorId)) continue; // kein Monitor angeschlossen

            bool attached = (adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            var dm = DEVMODE.Create();
            PixelRect bounds = default;
            DisplayMode mode = default;
            if (attached && EnumDisplaySettings(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
            {
                bounds = new PixelRect(dm.dmPositionX, dm.dmPositionY, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
                mode = new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);
            }

            result.Add(new DisplayInfo(adapter.DeviceName, adapter.DeviceString, adapter.DeviceID ?? "", monitorId, attached,
                (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0, bounds, mode));
        }
        return result;
    }

    public static bool IsVirtual(DisplayInfo d, IEnumerable<string>? markers = null) =>
        (markers ?? DefaultVirtualAdapterMarkers).Any(m =>
            d.AdapterName.Contains(m, StringComparison.OrdinalIgnoreCase) ||
            d.AdapterId.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Virtuelle Monitore in stabiler Reihenfolge. Welcher virtuelle Monitor welchen Bereich bekommt, ist egal:
    /// MONIVOX setzt Auflösung und Position jedes virtuellen Monitors selbst.
    /// </summary>
    public static IReadOnlyList<DisplayInfo> VirtualDisplaysInOrder(IReadOnlyList<DisplayInfo> all, IEnumerable<string>? markers = null) =>
        all.Where(d => IsVirtual(d, markers)).OrderBy(d => DeviceNumber(d.DeviceName)).ToList();

    private static int DeviceNumber(string deviceName)
    {
        var m = Regex.Match(deviceName, @"(\d+)$");
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : int.MaxValue;
    }
}
