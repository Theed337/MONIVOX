using System.Globalization;
using System.Text;

namespace Monivox.Core;

/// <summary>Erzeugt die Konfigurationsdatei für MonivoxCompositor.exe (siehe native/src/compositor.cpp).</summary>
public static class CompositorConfig
{
    public sealed record View(string Device, PixelRect Dest, string Name);

    /// <param name="physicalDevice">GDI-Gerätename des physischen Monitors, z. B. \\.\DISPLAY1.</param>
    /// <param name="views">Virtuelle Bereiche: Quellmonitor, Zielrechteck auf dem physischen Monitor, Name.</param>
    /// <param name="direct">Direktbereich relativ zum physischen Monitor (oder null).</param>
    /// <param name="frames">Farbige Rahmen anfangs einblenden.</param>
    /// <param name="panelOrigin">Desktop-Position, an der das Monitorbild "gedacht" liegt (für Mausübergänge zu Nachbarmonitoren).</param>
    public static string Build(string physicalDevice, IEnumerable<View> views, (PixelRect Rect, string Name)? direct = null,
                               bool frames = false, (int X, int Y)? panelOrigin = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Automatisch erzeugt von MONIVOX – Änderungen werden überschrieben");
        sb.AppendLine(CultureInfo.InvariantCulture, $"physical={physicalDevice}");
        foreach (var v in views)
        {
            var d = v.Dest;
            sb.AppendLine(CultureInfo.InvariantCulture, $"view={v.Device},{d.X},{d.Y},{d.Width},{d.Height},{Clean(v.Name)}");
        }
        if (direct is { } dr)
        {
            var d = dr.Rect;
            sb.AppendLine(CultureInfo.InvariantCulture, $"direct={d.X},{d.Y},{d.Width},{d.Height},{Clean(dr.Name)}");
        }
        if (panelOrigin is { } po) sb.AppendLine(CultureInfo.InvariantCulture, $"panel={po.X},{po.Y}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"frames={(frames ? 1 : 0)}");
        return sb.ToString();
    }

    private static string Clean(string name) => name.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
