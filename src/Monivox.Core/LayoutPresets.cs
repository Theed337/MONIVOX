namespace Monivox.Core;

/// <summary>Vordefinierte Aufteilungen.</summary>
public static class LayoutPresets
{
    public static Layout Full { get; } = new("Ganzer Monitor", [new(0, 0, 1, 1)]);

    public static Layout HalfVertical { get; } = Columns("2 nebeneinander (50/50)", 0.5);

    public static Layout HalfHorizontal { get; } = Rows("2 übereinander (50/50)", 0.5);

    public static Layout ThreeColumns { get; } = Columns("3 nebeneinander", 1.0 / 3, 2.0 / 3);

    public static Layout MainAndSide { get; } = Columns("Haupt + Seite (60/40)", 0.6);

    /// <summary>Links groß, rechts zwei übereinander (z. B. Spiel | Browser / Discord).</summary>
    public static Layout MainAndTwoStacked { get; } = new("Haupt + 2 gestapelt",
    [
        new(0, 0, 0.6, 1),
        new(0.6, 0, 0.4, 0.5),
        new(0.6, 0.5, 0.4, 0.5),
    ]);

    public static Layout Grid2x2 { get; } = Grid("4 Bereiche (2x2)", 2, 2);

    /// <summary>Alle Vorlagen mit stabilem Kurznamen (für CLI und Hotkeys).</summary>
    public static IReadOnlyDictionary<string, Layout> All { get; } = new Dictionary<string, Layout>(StringComparer.OrdinalIgnoreCase)
    {
        ["full"] = Full,
        ["half"] = HalfVertical,
        ["half-h"] = HalfHorizontal,
        ["thirds"] = ThreeColumns,
        ["main-side"] = MainAndSide,
        ["main-stack"] = MainAndTwoStacked,
        ["grid"] = Grid2x2,
    };

    /// <summary>Vertikale Trennlinien an den angegebenen Positionen (0..1, aufsteigend).</summary>
    public static Layout Columns(string name, params double[] splits)
    {
        var edges = Edges(splits);
        return new Layout(name, edges.Zip(edges.Skip(1), (a, b) => new NormRect(a, 0, b - a, 1)));
    }

    /// <summary>Horizontale Trennlinien an den angegebenen Positionen (0..1, aufsteigend).</summary>
    public static Layout Rows(string name, params double[] splits)
    {
        var edges = Edges(splits);
        return new Layout(name, edges.Zip(edges.Skip(1), (a, b) => new NormRect(0, a, 1, b - a)));
    }

    public static Layout Grid(string name, int columns, int rows)
    {
        if (columns < 1 || rows < 1 || columns * rows > Layout.MaxRegions)
            throw new ArgumentOutOfRangeException(nameof(columns), "Ungültige Rastergröße.");

        var regions = new List<NormRect>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
                regions.Add(new NormRect((double)c / columns, (double)r / rows, 1.0 / columns, 1.0 / rows));
        return new Layout(name, regions);
    }

    private static List<double> Edges(double[] splits)
    {
        foreach (var s in splits)
        {
            if (s <= 0 || s >= 1) throw new ArgumentOutOfRangeException(nameof(splits), "Trennlinien müssen zwischen 0 und 1 liegen.");
        }
        for (int i = 1; i < splits.Length; i++)
        {
            if (splits[i] <= splits[i - 1]) throw new ArgumentException("Trennlinien müssen aufsteigend sein.", nameof(splits));
        }
        return [0, .. splits, 1];
    }
}
