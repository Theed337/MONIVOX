namespace Monivox.Core;

/// <summary>
/// Plant die Windows-Desktop-Anordnung, wenn GENAU EIN gewählter physischer Monitor aufgeteilt wird.
/// <list type="bullet">
/// <item>Die virtuellen Monitore nehmen exakt den Platz des gewählten Monitors ein (gleiche Position,
/// gleiche Anordnung wie die Bereiche). Für die Maus und für andere Monitore ändert sich dadurch nichts.</item>
/// <item>Alle anderen Monitore behalten ihre Position (nur verschoben, falls sich der Ursprung ändert).</item>
/// <item>Der gewählte physische Monitor wird rechts neben dem gesamten Desktop „geparkt“. Weil er ganz
/// außen liegt, kann der Compositor die Maus mit einem einzigen Rechteck von ihm fernhalten.</item>
/// <item>War der gewählte Monitor primär, wird der Bereich oben links primär.</item>
/// </list>
/// </summary>
public static class DesktopArrangement
{
    /// <summary>Ein Monitor, der nicht aufgeteilt wird.</summary>
    public sealed record OtherDisplay(string DeviceName, PixelRect Bounds, bool IsPrimary);

    public sealed record VirtualPlacement(int Index, int X, int Y, DisplayMode Mode, bool Primary);

    public sealed record Plan(
        IReadOnlyList<VirtualPlacement> Virtuals,
        IReadOnlyList<OtherDisplay> Others,
        PixelRect ParkedPhysical,
        PixelRect CursorArea); // Bereich, in dem sich die Maus bewegen darf (alles außer dem geparkten Monitor)

    /// <param name="specs">Aufgelöste Bereiche des gewählten Monitors.</param>
    /// <param name="physicalBounds">Aktuelle Desktop-Position und Größe des gewählten Monitors.</param>
    /// <param name="physicalIsPrimary">Ob der gewählte Monitor aktuell primär ist.</param>
    /// <param name="others">Alle anderen aktiven Monitore (ohne virtuelle Monivox-Monitore).</param>
    public static Plan Create(
        IReadOnlyList<VirtualMonitorSpec> specs,
        PixelRect physicalBounds,
        bool physicalIsPrimary,
        IReadOnlyList<OtherDisplay> others)
    {
        if (specs.Count == 0) throw new ArgumentException("Keine virtuellen Monitore.", nameof(specs));

        // 1. Virtuelle Monitore genau dort, wo der physische Monitor war
        var modes = specs.ToDictionary(s => s.Index, s => s.Mode);
        var virtuals = VirtualBlock.Offsets(specs)
            .Select(o => new VirtualPlacement(o.Index, physicalBounds.X + o.X, physicalBounds.Y + o.Y, modes[o.Index], false))
            .ToList();
        var otherList = others.ToList();

        // 2. Primärer Monitor muss bei (0,0) liegen
        int dx = 0, dy = 0;
        if (physicalIsPrimary)
        {
            var primary = virtuals.OrderBy(v => v.Y).ThenBy(v => v.X).First();
            dx = -primary.X;
            dy = -primary.Y;
            virtuals = virtuals.Select(v => v with { X = v.X + dx, Y = v.Y + dy, Primary = v.Index == primary.Index }).ToList();
            otherList = otherList.Select(o => o with { Bounds = o.Bounds.Offset(dx, dy), IsPrimary = false }).ToList();
        }

        // 3. Physischen Monitor rechts außen parken, an den am weitesten rechts liegenden Monitor angrenzend
        var all = virtuals.Select(v => new PixelRect(v.X, v.Y, v.Mode.Width, v.Mode.Height))
            .Concat(otherList.Select(o => o.Bounds))
            .ToList();
        var rightmost = all.OrderByDescending(r => r.Right).ThenBy(r => r.Y).First();
        var parked = new PixelRect(rightmost.Right, rightmost.Y, physicalBounds.Width, physicalBounds.Height);

        int left = all.Min(r => r.X), top = all.Min(r => r.Y);
        int right = all.Max(r => r.Right), bottom = all.Max(r => r.Bottom);
        var cursorArea = new PixelRect(left, top, right - left, bottom - top);

        return new Plan(virtuals, otherList, parked, cursorArea);
    }
}
