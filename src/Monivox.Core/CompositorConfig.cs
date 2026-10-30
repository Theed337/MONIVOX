using System.Globalization;
using System.Text;

namespace Monivox.Core;

/// <summary>Erzeugt die Konfigurationsdatei für MonivoxCompositor.exe (siehe native/src/compositor.cpp).</summary>
public static class CompositorConfig
{
    /// <param name="physicalDevice">GDI-Gerätename des physischen Monitors, z. B. \\.\DISPLAY1.</param>
    /// <param name="views">Quellmonitor (GDI-Name) und Zielrechteck auf dem physischen Monitor.</param>
    public static string Build(string physicalDevice, IEnumerable<(string Device, PixelRect Dest)> views, bool clipCursor = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Automatisch erzeugt von Monivox – Änderungen werden überschrieben");
        sb.AppendLine(CultureInfo.InvariantCulture, $"physical={physicalDevice}");
        foreach (var (device, d) in views)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"view={device},{d.X},{d.Y},{d.Width},{d.Height}");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"clipcursor={(clipCursor ? 1 : 0)}");
        return sb.ToString();
    }
}
