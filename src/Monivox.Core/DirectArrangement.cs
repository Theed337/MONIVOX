namespace Monivox.Core;

/// <summary>
/// Anordnung im Direktbereich-Modus: Der physische Monitor bleibt ein ganz normaler Windows-Monitor an seiner
/// Position (sein Bild ist im Direktbereich sichtbar). Die übrigen Bereiche werden virtuelle Monitore, die als
/// zusammenhängender Block rechts neben dem gesamten Desktop liegen – in derselben Anordnung wie auf dem Monitor.
/// Die Maus-Brücke im Compositor sorgt dafür, dass sich die Maus trotzdem so bewegt, als lägen alle Bereiche
/// nebeneinander auf dem echten Monitor.
/// </summary>
public static class DirectArrangement
{
    public sealed record Plan(
        IReadOnlyList<DesktopArrangement.VirtualPlacement> Virtuals,
        IReadOnlyList<DesktopArrangement.OtherDisplay> Others,
        PixelRect Physical,
        bool PhysicalPrimary,
        PixelRect WorkArea); // Arbeitsbereich des physischen Monitors = Direktbereich (Desktop-Koordinaten)

    /// <param name="virtualSpecs">Nur die virtuellen Bereiche (ohne Direktbereich).</param>
    /// <param name="direct">Direktbereich relativ zum physischen Monitor.</param>
    public static Plan Create(
        IReadOnlyList<VirtualMonitorSpec> virtualSpecs,
        PixelRect direct,
        PixelRect physicalBounds,
        bool physicalIsPrimary,
        IReadOnlyList<DesktopArrangement.OtherDisplay> others)
    {
        // Block rechts neben allem, oben bündig mit dem am weitesten rechts liegenden Monitor
        var all = others.Select(o => o.Bounds).Append(physicalBounds).ToList();
        var rightmost = all.OrderByDescending(r => r.Right).ThenBy(r => r.Y).First();

        IReadOnlyList<DesktopArrangement.VirtualPlacement> virtuals = [];
        if (virtualSpecs.Count > 0)
        {
            int originX = rightmost.Right, originY = rightmost.Y;
            var modes = virtualSpecs.ToDictionary(s => s.Index, s => s.Mode);
            virtuals = VirtualBlock.Offsets(virtualSpecs)
                .Select(o => new DesktopArrangement.VirtualPlacement(o.Index, originX + o.X, originY + o.Y, modes[o.Index], false))
                .ToList();
        }

        var workArea = new PixelRect(physicalBounds.X + direct.X, physicalBounds.Y + direct.Y, direct.Width, direct.Height);
        return new Plan(virtuals, others, physicalBounds, physicalIsPrimary, workArea);
    }
}
