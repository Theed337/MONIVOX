using System.Runtime.Versioning;
using Monivox.Core;
using static Monivox.Windows.Native;

namespace Monivox.Windows;

/// <summary>
/// Setzt Positionen, Modi und primären Monitor. Alle Änderungen werden zuerst gesammelt
/// (CDS_NORESET) und dann in einem Schritt angewendet, damit Windows nur einmal umschaltet.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DisplayArranger
{
    public sealed record Placement(string DeviceName, int X, int Y, DisplayMode? Mode, bool Primary);

    /// <param name="detach">Monitore, die im selben Schritt vom Desktop gelöst werden.</param>
    public static void Apply(IEnumerable<Placement> placements, IEnumerable<string>? detach = null)
    {
        foreach (var name in detach ?? Enumerable.Empty<string>())
        {
            var off = DEVMODE.Create();
            off.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
            ChangeDisplaySettingsEx(name, ref off, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        }

        // Der primäre Monitor muss zuerst gesetzt werden, die anderen Positionen sind relativ zu ihm.
        foreach (var p in placements.OrderByDescending(p => p.Primary))
        {
            var dm = DEVMODE.Create();
            dm.dmFields = DM_POSITION;
            dm.dmPositionX = p.X;
            dm.dmPositionY = p.Y;
            if (p.Mode is { } mode)
            {
                dm.dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                dm.dmPelsWidth = (uint)mode.Width;
                dm.dmPelsHeight = (uint)mode.Height;
                dm.dmDisplayFrequency = (uint)mode.RefreshHz;
            }

            uint flags = CDS_UPDATEREGISTRY | CDS_NORESET | (p.Primary ? CDS_SET_PRIMARY : 0);
            int rc = ChangeDisplaySettingsEx(p.DeviceName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (rc != DISP_CHANGE_SUCCESSFUL)
                throw new InvalidOperationException($"Anzeige {p.DeviceName} konnte nicht gesetzt werden (Code {rc}).");
        }
        Commit();
    }

    /// <summary>Hebt eine Maus-Begrenzung auf (falls der Compositor hart beendet wurde).</summary>
    public static void ReleaseCursor() => ClipCursor(IntPtr.Zero);

    private static void Commit()
    {
        int rc = ChangeDisplaySettingsExApply(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (rc != DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException($"Anzeigekonfiguration konnte nicht übernommen werden (Code {rc}).");
    }
}
