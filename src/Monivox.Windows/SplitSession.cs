using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Monivox.Core;

namespace Monivox.Windows;

public sealed record SplitOptions(
    Layout Layout,
    string? PhysicalDevice = null,
    int RefreshHz = 0,
    string CompositorPath = "MonivoxCompositor.exe",
    bool ShowFrames = false);

/// <summary>Gesicherte Anordnung vor dem Split – wird von <see cref="SplitSession.Stop"/> wiederhergestellt.</summary>
public sealed record RestoreState(string PhysicalDevice, PixelRect PhysicalBounds, bool PhysicalWasPrimary,
                                  IReadOnlyList<RestoreState.Entry> Displays)
{
    public sealed record Entry(string DeviceName, int X, int Y, bool Primary);
}

/// <summary>
/// Teilt GENAU EINEN gewählten physischen Monitor auf. Alle anderen Monitore bleiben, wo sie sind.
/// Ablauf: Layout auflösen → Treiber konfigurieren → Monitore anordnen → Compositor starten und überwachen.
/// Rückgängig machen mit <see cref="Stop"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SplitSession(SplitOptions options, Action<string> log)
{
    public static string RuntimeDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Monivox");

    public static string CompositorConfigPath => Path.Combine(RuntimeDirectory, "compositor.cfg");

    /// <summary>
    /// Wird ausgelöst, sobald die virtuellen Monitore angeordnet sind (auch nach jedem Neuaufbau).
    /// Liefert pro Layout-Bereich die Desktop-Koordinaten des zugehörigen virtuellen Monitors.
    /// </summary>
    /// <remarks>Zweiter Wert: aktuelle Desktop-Position des aufgeteilten (physischen) Monitors.</remarks>
    public event Action<IReadOnlyList<PixelRect>, PixelRect>? Ready;
    public static string RestorePath => Path.Combine(RuntimeDirectory, "restore.json");

    /// <summary>Wählt den aufzuteilenden Monitor. Bei mehreren echten Monitoren muss er angegeben werden.</summary>
    public static DisplayInfo SelectPhysical(IReadOnlyList<DisplayInfo> all, string? wanted)
    {
        var candidates = all.Where(d => !Displays.IsVirtual(d) && d.IsAttached).ToList();
        if (wanted is not null)
        {
            return candidates.FirstOrDefault(d => d.DeviceName.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                                                  || d.DeviceName.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Monitor '{wanted}' nicht gefunden oder nicht aktiv. Siehe: monivox list");
        }
        return candidates.Count switch
        {
            0 => throw new InvalidOperationException("Kein aktiver physischer Monitor gefunden."),
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                "Mehrere Monitore gefunden – bitte den aufzuteilenden wählen, z. B. --monitor " +
                string.Join(" oder --monitor ", candidates.Select(c => c.DeviceName.Replace(@"\\.\", "")))),
        };
    }

    /// <summary>Startet den Split und blockiert, bis er beendet wird (Not-Aus-Hotkey oder Abbruch).</summary>
    public int Run(CancellationToken cancel)
    {
        for (int attempt = 0; ; attempt++)
        {
            Prepare();
            int code = RunCompositor(cancel);
            if (cancel.IsCancellationRequested || code == 0) return code;
            if (code == 2 && attempt >= 2) return code; // Konfiguration dauerhaft fehlerhaft
            if (attempt >= 5)
            {
                log("Compositor ist mehrfach abgebrochen – gebe auf.");
                return code;
            }
            // 3 = Displayänderung, 4 = Grafikfehler/Absturz -> neu aufbauen
            log($"Compositor beendet mit Code {code}, baue neu auf …");
            Thread.Sleep(1000);
        }
    }

    private void Prepare()
    {
        var all = Displays.Enumerate();

        // Ausgangszustand nur sichern, wenn noch keiner gesichert ist. Nach einem Absturz liegt
        // der Monitor schon geparkt vor – dann gilt weiterhin die ursprüngliche Sicherung.
        var restore = LoadRestore();
        if (restore is null)
        {
            var physical = SelectPhysical(all, options.PhysicalDevice);
            restore = new RestoreState(physical.DeviceName, physical.Bounds, physical.IsPrimary,
                all.Where(d => !Displays.IsVirtual(d) && d.IsAttached)
                   .Select(d => new RestoreState.Entry(d.DeviceName, d.Bounds.X, d.Bounds.Y, d.IsPrimary)).ToList());
            SaveRestore(restore);
        }
        else if (options.PhysicalDevice is { } wanted && !restore.PhysicalDevice.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Es läuft noch eine Aufteilung von {restore.PhysicalDevice}. Bitte die Aufteilung zuerst beenden.");
        }

        var current = all.FirstOrDefault(d => d.DeviceName == restore.PhysicalDevice)
            ?? throw new InvalidOperationException($"Monitor {restore.PhysicalDevice} ist nicht mehr angeschlossen.");
        var physicalMode = current.Mode;

        var errors = options.Layout.Validate(physicalMode.Width, physicalMode.Height);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        int hz = options.RefreshHz > 0 ? options.RefreshHz : physicalMode.RefreshHz;
        var layout = options.Layout;
        var specs = LayoutResolver.Resolve(layout, physicalMode with { RefreshHz = hz });
        var virtualSpecs = specs.Where(s => !layout.HasDirect || s.Index != layout.DirectIndex).ToList();
        log($"Aufgeteilt wird: {restore.PhysicalDevice} ({physicalMode}) – andere Monitore bleiben unverändert.");
        foreach (var s in specs)
        {
            log(layout.HasDirect && s.Index == layout.DirectIndex
                ? $"  Bereich {s.Index + 1} „{layout.RegionName(s.Index)}“: {s.Dest} -> DIREKT (keine Latenz, DRM)"
                : $"  Bereich {s.Index + 1} „{layout.RegionName(s.Index)}“: {s.Dest} -> virtueller Monitor {s.Mode}");
        }

        if (!VddControl.IsInstalled)
            throw new InvalidOperationException("Der Anzeigetreiber ist nicht installiert (Status-Seite → „Treiber installieren“).");

        // Virtueller Monitor k gehört zu virtualSpecs[k]
        IReadOnlyList<DisplayInfo> virtuals = [];
        if (virtualSpecs.Count > 0)
        {
            if (VddControl.Configure(virtualSpecs.Count, virtualSpecs.Select(s => s.Mode).ToList(), log))
                log("Warte auf virtuelle Monitore …");

            try
            {
                virtuals = WaitForVirtualDisplays(virtualSpecs.Count, TimeSpan.FromSeconds(8 + 2 * virtualSpecs.Count));
            }
            catch (InvalidOperationException)
            {
                // Manche Treiberversionen übernehmen die Monitoranzahl erst nach einem Geräteneustart.
                VddControl.RestartDevice(log);
                virtuals = WaitForVirtualDisplays(virtualSpecs.Count, TimeSpan.FromSeconds(20));
            }
        }
        var deviceOf = virtualSpecs.Select((spec, k) => (spec.Index, virtuals[k].DeviceName)).ToDictionary(x => x.Index, x => x.DeviceName);

        // Andere Monitore an ihrer ursprünglichen Position (aus der Sicherung, nicht der aktuellen –
        // nach einem Neustart könnte sich der Ursprung verschoben haben).
        var others = restore.Displays
            .Where(e => e.DeviceName != restore.PhysicalDevice)
            .Select(e => (Entry: e, Info: all.FirstOrDefault(d => d.DeviceName == e.DeviceName && d.IsAttached)))
            .Where(x => x.Info is not null)
            .Select(x => new DesktopArrangement.OtherDisplay(x.Entry.DeviceName,
                new PixelRect(x.Entry.X, x.Entry.Y, x.Info!.Bounds.Width, x.Info.Bounds.Height), x.Entry.Primary))
            .ToList();
        var physicalBounds = restore.PhysicalBounds with { Width = physicalMode.Width, Height = physicalMode.Height };

        var placements = new List<DisplayArranger.Placement>();
        (int X, int Y)? panelOrigin = null;
        if (layout.HasDirect)
        {
            // Direktbereich: physischer Monitor bleibt, wo er ist; virtuelle Monitore als Block daneben
            var plan = DirectArrangement.Create(virtualSpecs, specs[layout.DirectIndex].Dest, physicalBounds, restore.PhysicalWasPrimary, others);
            foreach (var v in plan.Virtuals) placements.Add(new(deviceOf[v.Index], v.X, v.Y, v.Mode, false));
            foreach (var o in plan.Others) placements.Add(new(o.DeviceName, o.Bounds.X, o.Bounds.Y, null, o.IsPrimary));
            placements.Add(new(restore.PhysicalDevice, plan.Physical.X, plan.Physical.Y, null, plan.PhysicalPrimary));
        }
        else
        {
            var plan = DesktopArrangement.Create(specs, physicalBounds, restore.PhysicalWasPrimary, others);
            if (VirtualBlock.CanMirror(specs))
            {
                var v0 = plan.Virtuals[0];
                panelOrigin = (v0.X - specs[v0.Index].Dest.X, v0.Y - specs[v0.Index].Dest.Y);
            }
            foreach (var v in plan.Virtuals) placements.Add(new(deviceOf[v.Index], v.X, v.Y, v.Mode, v.Primary));
            foreach (var o in plan.Others) placements.Add(new(o.DeviceName, o.Bounds.X, o.Bounds.Y, null, o.IsPrimary));
            placements.Add(new(restore.PhysicalDevice, plan.ParkedPhysical.X, plan.ParkedPhysical.Y, null, false));
        }
        DisplayArranger.Apply(placements);
        WaitUntilArranged(placements, TimeSpan.FromSeconds(5));
        log("Monitore angeordnet.");

        // Nach dem Umschalten tatsächliche Modi prüfen
        var after = Displays.Enumerate();
        foreach (var spec in virtualSpecs)
        {
            var actual = after.First(d => d.DeviceName == deviceOf[spec.Index]);
            if (actual.Mode.Width != spec.Mode.Width || actual.Mode.Height != spec.Mode.Height)
                log($"  Hinweis: Bereich {spec.Index + 1} läuft mit {actual.Mode} statt {spec.Mode} – Bild wird skaliert.");
        }

        Directory.CreateDirectory(RuntimeDirectory);
        File.WriteAllText(CompositorConfigPath, CompositorConfig.Build(restore.PhysicalDevice,
            virtualSpecs.Select(s => new CompositorConfig.View(deviceOf[s.Index], s.Dest, layout.RegionName(s.Index), layout.HiddenTaskbars.Contains(s.Index))),
            layout.HasDirect ? (specs[layout.DirectIndex].Dest, layout.RegionName(layout.DirectIndex)) : null,
            options.ShowFrames, panelOrigin));

        // Desktop-Koordinaten je Bereich (für die Programmregeln) – der Direktbereich liegt auf dem echten Monitor
        var physicalNow = after.First(d => d.DeviceName == restore.PhysicalDevice).Bounds;
        Ready?.Invoke(specs.Select(s => layout.HasDirect && s.Index == layout.DirectIndex
            ? s.Dest.Offset(physicalNow.X, physicalNow.Y)
            : after.First(d => d.DeviceName == deviceOf[s.Index]).Bounds).ToList(), physicalNow);
    }

    /// <summary>Wartet, bis Windows die neue Anordnung wirklich übernommen hat (sonst findet der Compositor die Monitore nicht).</summary>
    private static void WaitUntilArranged(IReadOnlyList<DisplayArranger.Placement> placements, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var now = Displays.Enumerate();
            bool ok = placements.All(p => now.Any(d => d.DeviceName == p.DeviceName && d.IsAttached && d.Bounds.X == p.X && d.Bounds.Y == p.Y));
            if (ok) return;
            Thread.Sleep(200);
        }
    }

    private IReadOnlyList<DisplayInfo> WaitForVirtualDisplays(int count, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var virtuals = Displays.VirtualDisplaysInOrder(Displays.Enumerate());
            if (virtuals.Count >= count) return virtuals.Take(count).ToList();
            if (sw.Elapsed > timeout)
            {
                throw new InvalidOperationException(
                    $"Es wurden nur {virtuals.Count} von {count} virtuellen Monitoren gefunden.");
            }
            Thread.Sleep(500);
        }
    }

    private int RunCompositor(CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(options.CompositorPath, $"\"{CompositorConfigPath}\"") { UseShellExecute = false };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Compositor konnte nicht gestartet werden.");
        log($"Compositor läuft (PID {process.Id}). Not-Aus: Strg+Alt+Shift+Q");

        using var reg = cancel.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        });
        process.WaitForExit();
        DisplayArranger.ReleaseCursor();
        return cancel.IsCancellationRequested ? 0 : process.ExitCode;
    }

    public static bool IsRunning => Process.GetProcessesByName("MonivoxCompositor").Length > 0;

    /// <summary>Beendet laufende Compositoren und stellt die ursprüngliche Anordnung wieder her.</summary>
    public static void Stop(Action<string> log)
    {
        foreach (var p in Process.GetProcessesByName("MonivoxCompositor"))
        {
            try { p.Kill(); p.WaitForExit(3000); log($"Compositor {p.Id} beendet."); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        DisplayArranger.ReleaseCursor();

        var all = Displays.Enumerate();
        var virtuals = all.Where(d => Displays.IsVirtual(d) && d.IsAttached).ToList();
        // Virtuelle Monitore im selben Schritt vom Desktop lösen
        List<string> detach = virtuals.Select(v => v.DeviceName).ToList();

        var restore = LoadRestore();
        var placements = new List<DisplayArranger.Placement>();
        if (restore is not null)
        {
            foreach (var e in restore.Displays.Where(e => all.Any(d => d.DeviceName == e.DeviceName)))
                placements.Add(new(e.DeviceName, e.X, e.Y, null, e.Primary));
        }
        else
        {
            // Keine Sicherung: echte Monitore nebeneinander, erster wird primär
            int x = 0;
            var physical = all.Where(d => !Displays.IsVirtual(d) && d.IsAttached).OrderBy(d => d.Bounds.X).ToList();
            for (int i = 0; i < physical.Count; i++)
            {
                placements.Add(new(physical[i].DeviceName, x, 0, null, i == 0));
                x += physical[i].Bounds.Width;
            }
        }

        if (placements.Count > 0)
        {
            DisplayArranger.Apply(placements, detach);
            if (detach.Count > 0) log($"{detach.Count} virtuelle Monitore deaktiviert.");
        }
        try { File.Delete(RestorePath); } catch (IOException) { }
        // Treibergeräte abschalten: ohne Aufteilung erzeugt der Anzeigetreiber so keinerlei Last
        try { VddControl.DisableAll(log); }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { log($"Treiber abschalten: {e.Message}"); }
        log("Ursprüngliche Anzeige wiederhergestellt.");
    }

    private static RestoreState? LoadRestore()
    {
        try
        {
            return File.Exists(RestorePath)
                ? JsonSerializer.Deserialize<RestoreState>(File.ReadAllText(RestorePath), ProfileStore.JsonOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void SaveRestore(RestoreState state)
    {
        Directory.CreateDirectory(RuntimeDirectory);
        File.WriteAllText(RestorePath, JsonSerializer.Serialize(state, ProfileStore.JsonOptions));
    }
}
