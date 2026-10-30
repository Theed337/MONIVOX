using System.Globalization;
using System.Text.RegularExpressions;

namespace Monivox.Core;

/// <summary>
/// Format des Registry-Werts HKLM\SOFTWARE\Monivox\Monitors, den der Treiber liest:
/// "1920x2160@60;1920x2160@60". Muss mit ParseMonitorConfig in driver/Driver.cpp übereinstimmen.
/// </summary>
public static partial class DriverConfig
{
    public const string RegistryKey = @"SOFTWARE\Monivox";
    public const string RegistryValue = "Monitors";

    public static string Format(IEnumerable<DisplayMode> modes) =>
        string.Join(";", modes.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Width}x{m.Height}@{m.RefreshHz}")));

    public static IReadOnlyList<DisplayMode> Parse(string value)
    {
        var result = new List<DisplayMode>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = ModeRegex().Match(part);
            if (!m.Success) continue;
            int w = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int h = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int hz = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 60;
            // Gleiche Grenzen wie im Treiber
            if (w < 320 || h < 240 || w > 7680 || h > 4320) continue;
            if (hz < 24 || hz > 500) hz = 60;
            result.Add(new DisplayMode(w, h, hz));
            if (result.Count == Layout.MaxRegions) break;
        }
        return result;
    }

    [GeneratedRegex(@"^(\d+)x(\d+)(?:@(\d+))?$")]
    private static partial Regex ModeRegex();
}
