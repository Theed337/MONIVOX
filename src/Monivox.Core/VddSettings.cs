using System.Globalization;
using System.Xml.Linq;

namespace Monivox.Core;

/// <summary>
/// Pflegt die vdd_settings.xml des mitgelieferten, signierten "Virtual Display Driver" (MIT-Lizenz,
/// github.com/VirtualDrivers/Virtual-Display-Driver). MONIVOX ändert nur Monitoranzahl, Auflösungen und
/// Bildwiederholraten; alle anderen Einstellungen bleiben unangetastet.
/// </summary>
public static class VddSettings
{
    /// <summary>Gültige Grenzen laut Treiber-Dokumentation.</summary>
    public const int MinWidth = 640, MaxWidth = 7680, MinHeight = 480, MaxHeight = 4320, MaxMonitors = 24;

    public static XDocument CreateDefault() => new(
        new XDeclaration("1.0", "utf-8", null),
        new XElement("vdd_settings",
            new XElement("monitors", new XElement("count", 1)),
            new XElement("gpu", new XElement("friendlyname", "default")),
            new XElement("global", new XElement("g_refresh_rate", 60)),
            new XElement("resolutions"),
            new XElement("options", new XElement("HardwareCursor", "true"))));

    /// <summary>
    /// Setzt die Monitoranzahl und stellt sicher, dass jeder benötigte Modus angeboten wird.
    /// Bereits vorhandene Auflösungen bleiben erhalten (andere Programme könnten sie nutzen).
    /// </summary>
    /// <returns>true, wenn sich etwas geändert hat (Treiber muss neu geladen werden).</returns>
    public static bool Apply(XDocument doc, int monitorCount, IEnumerable<DisplayMode> modes)
    {
        if (monitorCount < 0 || monitorCount > MaxMonitors)
            throw new ArgumentOutOfRangeException(nameof(monitorCount), $"Maximal {MaxMonitors} virtuelle Monitore.");

        var root = doc.Root ?? throw new InvalidDataException("vdd_settings.xml ohne Wurzelelement.");
        bool changed = false;

        var monitors = GetOrAdd(root, "monitors");
        var count = GetOrAdd(monitors, "count");
        if (count.Value != monitorCount.ToString(CultureInfo.InvariantCulture))
        {
            count.Value = monitorCount.ToString(CultureInfo.InvariantCulture);
            changed = true;
        }

        var global = GetOrAdd(root, "global");
        var rates = global.Elements("g_refresh_rate").Select(e => (int?)TryInt(e.Value)).Where(v => v is not null).Select(v => v!.Value).ToHashSet();
        var resolutions = GetOrAdd(root, "resolutions");
        var existing = resolutions.Elements("resolution")
            .Select(r => (W: TryInt(r.Element("width")?.Value), H: TryInt(r.Element("height")?.Value)))
            .ToHashSet();

        foreach (var mode in modes.Distinct())
        {
            if (mode.Width < MinWidth || mode.Width > MaxWidth || mode.Height < MinHeight || mode.Height > MaxHeight)
                throw new ArgumentOutOfRangeException(nameof(modes),
                    $"{mode.Width}x{mode.Height} liegt außerhalb dessen, was der Anzeigetreiber unterstützt " +
                    $"({MinWidth}x{MinHeight} bis {MaxWidth}x{MaxHeight}).");

            if (!existing.Contains((mode.Width, mode.Height)))
            {
                resolutions.Add(new XElement("resolution",
                    new XElement("width", mode.Width),
                    new XElement("height", mode.Height),
                    new XElement("refresh_rate", mode.RefreshHz)));
                existing.Add((mode.Width, mode.Height));
                changed = true;
            }
            if (rates.Add(mode.RefreshHz))
            {
                // Globale Raten gelten für alle Auflösungen
                global.Add(new XElement("g_refresh_rate", mode.RefreshHz));
                changed = true;
            }
        }
        return changed;
    }

    public static int GetMonitorCount(XDocument doc) =>
        TryInt(doc.Root?.Element("monitors")?.Element("count")?.Value);

    private static XElement GetOrAdd(XElement parent, string name)
    {
        var e = parent.Element(name);
        if (e is null)
        {
            e = new XElement(name);
            parent.Add(e);
        }
        return e;
    }

    private static int TryInt(string? s) =>
        int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
}
