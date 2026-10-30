namespace Monivox.Core;

/// <summary>Ein virtueller Monitor, wie er aus einem Layout-Bereich entsteht.</summary>
/// <param name="Index">Index des Bereichs bzw. virtuellen Monitors (0-basiert).</param>
/// <param name="Dest">Zielrechteck auf dem physischen Monitor in Pixeln.</param>
/// <param name="Mode">Modus, den der virtuelle Monitor bekommt (= Größe des Zielrechtecks).</param>
public sealed record VirtualMonitorSpec(int Index, PixelRect Dest, DisplayMode Mode);

/// <summary>Rechnet normierte Layouts in konkrete Pixelbereiche um.</summary>
public static class LayoutResolver
{
    /// <summary>
    /// Rundet Kanten statt Breiten, damit benachbarte Bereiche lückenlos und ohne Überlappung
    /// aneinanderstoßen (z. B. 3 Spalten auf 3840 px: 1280/1280/1280, auf 2560 px: 853/854/853).
    /// </summary>
    public static IReadOnlyList<VirtualMonitorSpec> Resolve(Layout layout, DisplayMode physical)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (physical.Width <= 0 || physical.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(physical), "Ungültige Auflösung des physischen Monitors.");

        var result = new List<VirtualMonitorSpec>(layout.Regions.Count);
        for (int i = 0; i < layout.Regions.Count; i++)
        {
            var r = layout.Regions[i];
            int left = Edge(r.X, physical.Width);
            int top = Edge(r.Y, physical.Height);
            int right = Edge(r.Right, physical.Width);
            int bottom = Edge(r.Bottom, physical.Height);
            var dest = new PixelRect(left, top, right - left, bottom - top);
            result.Add(new VirtualMonitorSpec(i, dest, new DisplayMode(dest.Width, dest.Height, physical.RefreshHz)));
        }
        return result;
    }

    private static int Edge(double normalized, int size) =>
        (int)Math.Clamp(Math.Round(normalized * size, MidpointRounding.AwayFromZero), 0, size);
}
