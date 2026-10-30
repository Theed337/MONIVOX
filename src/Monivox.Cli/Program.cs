using System.Diagnostics;
using System.Globalization;
using Monivox.Core;
using Monivox.Windows;

// monivox – Kommandozeile für den technischen Nachweis (Phase 2), bevor die Oberfläche existiert.

static void Log(string message) => Console.WriteLine(message);

static int Usage()
{
    Console.WriteLine($"""
        MONIVOX {Updater.CurrentVersion}

          monivox-cli list                          Monitore anzeigen
          monivox-cli presets                       Layout-Vorlagen anzeigen
          monivox-cli preview <vorlage> [WxH]       Bereiche für eine Auflösung berechnen (ohne Änderungen)
          monivox-cli apply <vorlage> [optionen]    gewählten Monitor aufteilen (läuft bis Strg+C / Strg+Alt+Shift+Q)
          monivox-cli stop                          Aufteilung beenden, ursprüngliche Anordnung wiederherstellen
          monivox-cli check                         Anti-Cheat-Verträglichkeit prüfen
          monivox-cli update                        nach Updates suchen und installieren
          monivox-cli install-driver                Anzeigetreiber installieren (Administrator)
          monivox-cli uninstall-driver              Anzeigetreiber entfernen (Administrator)

        Optionen für apply:
          --monitor DISPLAY2        welcher Monitor aufgeteilt wird (Pflicht bei mehreren Monitoren)
          --hz N                    Bildwiederholrate der virtuellen Monitore
          --columns 0.6,0.8         eigene Spaltenaufteilung (Vorlage "custom")
          --no-update               Update-Prüfung überspringen
        """);
    return 2;
}

if (args.Length == 0) return Usage();
Updater.CleanupOldFiles();

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "list":
        {
            foreach (var d in Displays.Enumerate())
            {
                string kind = Displays.IsVirtual(d) ? "virtuell" : "physisch";
                string state = d.IsAttached ? $"{d.Bounds} @{d.Mode.RefreshHz}Hz" : "nicht aktiv";
                Console.WriteLine($"{d.DeviceName.Replace(@"\\.\", ""),-10} {kind,-9} {(d.IsPrimary ? "primär " : "       ")} {state,-28} {d.AdapterName}");
            }
            return 0;
        }

        case "presets":
        {
            foreach (var (key, layout) in LayoutPresets.All)
                Console.WriteLine($"{key,-12} {layout.Name} ({layout.Regions.Count} Bereiche)");
            return 0;
        }

        case "preview":
        {
            if (args.Length < 2) return Usage();
            var layout = ResolveLayout(args[1], args);
            var mode = new DisplayMode(3840, 2160, 60);
            if (args.Length >= 3 && args[2].Contains('x'))
            {
                var parts = args[2].Split('x');
                mode = new DisplayMode(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), 60);
            }
            foreach (var e in layout.Validate(mode.Width, mode.Height)) Console.WriteLine($"FEHLER: {e}");
            foreach (var s in LayoutResolver.Resolve(layout, mode))
                Console.WriteLine($"Bereich {s.Index + 1}: {s.Dest}");
            return 0;
        }

        case "check":
        {
            var findings = AntiCheatCheck.Run();
            if (findings.Count == 0) Console.WriteLine("Keine Auffälligkeiten. Testsignatur ist aus, kein Anti-Cheat aktiv.");
            foreach (var f in findings) Console.WriteLine($"{(f.Critical ? "ACHTUNG" : "Hinweis")}: {f.Message}");
            return findings.Any(f => f.Critical) ? 1 : 0;
        }

        case "update":
        {
            if (SplitSession.IsRunning)
            {
                Console.Error.WriteLine("Bitte zuerst 'monivox-cli stop' ausführen.");
                return 1;
            }
            var updated = await TryUpdate(force: true);
            if (updated is null) Console.WriteLine($"MONIVOX {Updater.CurrentVersion} ist aktuell.");
            return 0;
        }

        case "apply":
        {
            if (args.Length < 2) return Usage();
            if (!VddControl.IsAdministrator())
            {
                Console.Error.WriteLine("Bitte als Administrator ausführen (Anzeigetreiber-Konfiguration benötigt Adminrechte).");
                return 1;
            }

            // Automatisches Update vor dem Start – danach mit derselben Befehlszeile neu starten.
            if (!args.Contains("--no-update") && Environment.GetEnvironmentVariable("MONIVOX_UPDATED") is null && !SplitSession.IsRunning)
            {
                if (await TryUpdate(force: false) is not null)
                {
                    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                    foreach (var a in args) psi.ArgumentList.Add(a);
                    psi.Environment["MONIVOX_UPDATED"] = "1";
                    using var restarted = Process.Start(psi)!;
                    restarted.WaitForExit();
                    return restarted.ExitCode;
                }
            }

            foreach (var f in AntiCheatCheck.Run()) Console.WriteLine($"{(f.Critical ? "ACHTUNG" : "Hinweis")}: {f.Message}");

            string compositor = Path.Combine(AppContext.BaseDirectory, "MonivoxCompositor.exe");
            if (!File.Exists(compositor))
            {
                Console.Error.WriteLine($"MonivoxCompositor.exe nicht gefunden in {AppContext.BaseDirectory}");
                return 1;
            }

            var options = new SplitOptions(
                ResolveLayout(args[1], args),
                PhysicalDevice: NormalizeMonitor(Option(args, "--monitor") ?? Option(args, "--physical")),
                RefreshHz: int.TryParse(Option(args, "--hz"), out int hz) ? hz : 0,
                CompositorPath: compositor);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            int code = new SplitSession(options, Log).Run(cts.Token);
            SplitSession.Stop(Log);
            return code;
        }

        case "install-driver":
        {
            VddControl.Install(Log);
            return 0;
        }

        case "uninstall-driver":
        {
            SplitSession.Stop(Log);
            VddControl.Uninstall(Log);
            return 0;
        }

        case "stop":
        {
            SplitSession.Stop(Log);
            return 0;
        }

        default:
            return Usage();
    }
}
catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or IOException or HttpRequestException)
{
    Console.Error.WriteLine($"Fehler: {e.Message}");
    return 1;
}

static async Task<Updater.ReleaseInfo?> TryUpdate(bool force)
{
    var updater = new Updater(Log);
    try
    {
        var release = await updater.CheckAsync();
        if (release is null) return null;
        Console.WriteLine($"Neue Version verfügbar: {release.Tag} (installiert: {Updater.CurrentVersion})");
        bool driverChanged = await updater.InstallAsync(release);
        if (driverChanged)
        {
            if (VddControl.IsAdministrator())
            {
                Console.WriteLine("Anzeigetreiber wurde aktualisiert – installiere neu …");
                VddControl.Install(Log);
            }
            else
            {
                Console.WriteLine("Anzeigetreiber wurde aktualisiert – bitte 'monivox-cli install-driver' als Administrator ausführen.");
            }
        }
        return release;
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        // Ein fehlgeschlagenes Update darf den Start nie verhindern.
        Console.Error.WriteLine($"Update übersprungen: {e.Message}");
        if (force) throw new InvalidOperationException(e.Message, e);
        return null;
    }
}

static string? NormalizeMonitor(string? value) =>
    value is null ? null : value.StartsWith(@"\\.\", StringComparison.Ordinal) ? value : @"\\.\" + value.ToUpperInvariant();

static string? Option(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static Layout ResolveLayout(string name, string[] args)
{
    if (name.Equals("custom", StringComparison.OrdinalIgnoreCase))
    {
        var cols = Option(args, "--columns") ?? throw new ArgumentException("custom benötigt --columns, z. B. --columns 0.6");
        var splits = cols.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        return LayoutPresets.Columns("Eigene Aufteilung", splits);
    }
    return LayoutPresets.All.TryGetValue(name, out var layout)
        ? layout
        : throw new ArgumentException($"Unbekannte Vorlage '{name}'. Siehe: monivox-cli presets");
}
