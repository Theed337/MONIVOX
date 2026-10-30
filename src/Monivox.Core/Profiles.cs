using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monivox.Core;

public enum WindowState
{
    Normal,
    Maximized,
    Minimized,
    /// <summary>Randloses Vollbild über den ganzen virtuellen Monitor (empfohlen für Spiele).</summary>
    BorderlessFullscreen,
}

/// <summary>Weist ein Programm einem virtuellen Monitor zu.</summary>
public sealed record AppAssignment
{
    /// <summary>Vollständiger Pfad zur .exe oder nur der Prozessname (z. B. "discord.exe").</summary>
    public string Executable { get; init; } = "";
    public string? Arguments { get; init; }
    /// <summary>Optionaler regulärer Ausdruck für den Fenstertitel (bei Programmen mit mehreren Fenstern).</summary>
    public string? WindowTitlePattern { get; init; }
    /// <summary>Index des Layout-Bereichs bzw. virtuellen Monitors.</summary>
    public int RegionIndex { get; init; }
    public WindowState State { get; init; } = WindowState.Maximized;
    /// <summary>Optionale Position/Größe relativ zum virtuellen Monitor (nur bei <see cref="WindowState.Normal"/>).</summary>
    public PixelRect? Bounds { get; init; }
    /// <summary>Beim Laden des Profils starten, falls nicht schon offen.</summary>
    public bool StartWithProfile { get; init; }
}

public sealed record Profile
{
    public string Name { get; init; } = "";
    /// <summary>Aufzuteilender Monitor (z. B. "DISPLAY2"), leer = einziger vorhandener Monitor.</summary>
    public string PhysicalMonitorId { get; init; } = "";
    public Layout Layout { get; init; } = LayoutPresets.HalfVertical;
    /// <summary>Bildwiederholrate der virtuellen Monitore, 0 = wie physischer Monitor.</summary>
    public int RefreshHz { get; init; }
    public IReadOnlyList<AppAssignment> Apps { get; init; } = [];
    /// <summary>Profil beim Start der App automatisch laden.</summary>
    public bool LoadOnStartup { get; init; }
    /// <summary>Tastenkombination, z. B. "Ctrl+Alt+1".</summary>
    public string? Hotkey { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Profilname fehlt.");
        errors.AddRange(Layout.Validate());
        foreach (var app in Apps)
        {
            if (string.IsNullOrWhiteSpace(app.Executable)) errors.Add("Programmzuweisung ohne Programm.");
            if (app.RegionIndex < 0 || app.RegionIndex >= Layout.Regions.Count)
                errors.Add($"{app.Executable}: Bereich {app.RegionIndex + 1} existiert im Layout nicht.");
        }
        return errors;
    }
}

/// <summary>Gesamte gespeicherte Konfiguration.</summary>
public sealed record SettingsDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<Profile> Profiles { get; init; } = [];
    public string? LastProfile { get; init; }
    public bool StartWithWindows { get; init; }
    /// <summary>Beim Start von MONIVOX das letzte Profil sofort aktivieren (virtuelle Monitore entstehen beim Start).</summary>
    public bool SplitOnStartup { get; init; } = true;
    /// <summary>Updates automatisch laden und installieren.</summary>
    public bool AutoUpdate { get; init; } = true;
}

/// <summary>Lädt und speichert Profile als JSON (atomar über eine temporäre Datei).</summary>
public sealed class ProfileStore(string path)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Monivox", "settings.json");

    public SettingsDocument Load()
    {
        if (!File.Exists(Path)) return new SettingsDocument();
        try
        {
            using var stream = File.OpenRead(Path);
            return JsonSerializer.Deserialize<SettingsDocument>(stream, JsonOptions) ?? new SettingsDocument();
        }
        catch (JsonException)
        {
            // Beschädigte Datei sichern statt verwerfen, dann mit leeren Einstellungen weiter.
            File.Copy(Path, Path + ".defekt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            return new SettingsDocument();
        }
    }

    public void Save(SettingsDocument doc)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
    }

    public static string Export(Profile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    public static Profile Import(string json) =>
        JsonSerializer.Deserialize<Profile>(json, JsonOptions) ?? throw new InvalidDataException("Leeres Profil.");
}
