using System.Text.Json.Serialization;

namespace Monivox.Core;

/// <summary>
/// Aufteilung eines physischen Monitors in Bereiche. Koordinaten sind normiert (0..1),
/// damit ein Layout unabhängig von der Auflösung gespeichert werden kann.
/// Jeder Bereich wird zu einem virtuellen Monitor.
/// </summary>
public sealed record Layout
{
    /// <summary>Maximale Anzahl Bereiche (entspricht der Treibergrenze).</summary>
    public const int MaxRegions = 8;

    /// <summary>Kleinste erlaubte Bereichsgröße in Pixeln (entspricht den Treibergrenzen).</summary>
    public const int MinRegionWidth = 320;
    public const int MinRegionHeight = 240;

    public string Name { get; init; } = "";
    public IReadOnlyList<NormRect> Regions { get; init; } = [];

    [JsonConstructor]
    public Layout() { }

    public Layout(string name, IEnumerable<NormRect> regions)
    {
        Name = name;
        Regions = regions.ToList();
    }

    /// <summary>Prüft das Layout. Gibt eine leere Liste zurück, wenn alles gültig ist.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        const double eps = 1e-6;

        if (Regions.Count == 0) errors.Add("Das Layout enthält keinen Bereich.");
        if (Regions.Count > MaxRegions) errors.Add($"Maximal {MaxRegions} Bereiche erlaubt.");

        for (int i = 0; i < Regions.Count; i++)
        {
            var r = Regions[i];
            if (r.Width <= 0 || r.Height <= 0)
                errors.Add($"Bereich {i + 1} hat keine Fläche.");
            if (r.X < -eps || r.Y < -eps || r.Right > 1 + eps || r.Bottom > 1 + eps)
                errors.Add($"Bereich {i + 1} liegt außerhalb des Monitors.");

            for (int j = i + 1; j < Regions.Count; j++)
            {
                if (r.Overlaps(Regions[j], eps))
                    errors.Add($"Bereich {i + 1} überlappt Bereich {j + 1}.");
            }
        }
        return errors;
    }

    /// <summary>Prüft zusätzlich die Mindestgröße für eine konkrete Monitorauflösung.</summary>
    public IReadOnlyList<string> Validate(int physicalWidth, int physicalHeight)
    {
        var errors = Validate().ToList();
        if (errors.Count > 0) return errors;

        var specs = LayoutResolver.Resolve(this, new DisplayMode(physicalWidth, physicalHeight, 60));
        foreach (var spec in specs)
        {
            if (spec.Dest.Width < MinRegionWidth || spec.Dest.Height < MinRegionHeight)
                errors.Add($"Bereich {spec.Index + 1} ist mit {spec.Dest.Width}x{spec.Dest.Height} zu klein.");
        }
        return errors;
    }
}
