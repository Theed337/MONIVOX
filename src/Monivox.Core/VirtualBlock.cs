namespace Monivox.Core;

/// <summary>
/// Relative Lage der virtuellen Monitore zueinander auf dem Windows-Desktop.
/// Normalfall: genau wie die Bereiche auf dem echten Monitor (die Maus wechselt dann natürlich zwischen ihnen).
/// Haben kleine Bereiche einen hochskalierten virtuellen Monitor, passt diese Spiegelung nicht mehr
/// (Überlappung) – dann werden sie nebeneinander in eine Reihe gelegt; die Maus-Brücke des Compositors
/// übernimmt die richtige Zuordnung.
/// </summary>
public static class VirtualBlock
{
    public static bool CanMirror(IReadOnlyList<VirtualMonitorSpec> specs) =>
        specs.All(s => s.Mode.Width == s.Dest.Width && s.Mode.Height == s.Dest.Height);

    /// <returns>Versatz jedes virtuellen Monitors relativ zur linken oberen Ecke des Blocks.</returns>
    public static IReadOnlyList<(int Index, int X, int Y)> Offsets(IReadOnlyList<VirtualMonitorSpec> specs)
    {
        if (specs.Count == 0) return [];
        if (CanMirror(specs))
        {
            int minX = specs.Min(s => s.Dest.X), minY = specs.Min(s => s.Dest.Y);
            return specs.Select(s => (s.Index, s.Dest.X - minX, s.Dest.Y - minY)).ToList();
        }
        var result = new List<(int, int, int)>();
        int x = 0;
        foreach (var s in specs)
        {
            result.Add((s.Index, x, 0));
            x += s.Mode.Width;
        }
        return result;
    }
}
