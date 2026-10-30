namespace Monivox.Core;

/// <summary>Rechteck in normierten Koordinaten (0..1) relativ zum physischen Monitor.</summary>
public readonly record struct NormRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Overlaps(NormRect other, double epsilon = 1e-9) =>
        X < other.Right - epsilon && other.X < Right - epsilon &&
        Y < other.Bottom - epsilon && other.Y < Bottom - epsilon;
}

/// <summary>Rechteck in Pixeln.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

/// <summary>Auflösung und Bildwiederholrate eines Monitors.</summary>
public readonly record struct DisplayMode(int Width, int Height, int RefreshHz)
{
    public override string ToString() => $"{Width}x{Height}@{RefreshHz}";
}
