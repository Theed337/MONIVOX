using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Monivox.Core;
using Monivox.Windows;

namespace Monivox.App;

public enum SplitState { Stopped, Starting, Running, Stopping }

/// <summary>Zentrale Steuerung: Einstellungen, Aufteilung, Fensterregeln, Updates.</summary>
public sealed class SplitController : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly WindowRules _rules;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public ProfileStore Store { get; } = new(ProfileStore.DefaultPath);
    public SettingsDocument Settings { get; private set; }
    public ObservableCollection<string> Log { get; } = [];
    public SplitState State { get; private set; } = SplitState.Stopped;
    public Profile? ActiveProfile { get; private set; }

    public event Action? StateChanged;
    public event Action<string, bool>? Notify; // Text, Fehler?

    public SplitController(Dispatcher ui)
    {
        _ui = ui;
        Settings = Store.Load();
        if (Settings.Profiles.Count == 0)
        {
            Settings = Settings with { Profiles = DefaultProfiles(), LastProfile = "Mein Monitor" };
            Store.Save(Settings);
        }
        _rules = new WindowRules(AddLog, action => _ui.BeginInvoke(DispatcherPriority.Background,
            () => DispatcherTimerOnce(TimeSpan.FromMilliseconds(400), action)));
        _rules.Start();
    }

    // Ein leeres Startprofil – Bereiche zieht man sich selbst auf
    private static IReadOnlyList<Profile> DefaultProfiles() =>
        [new Profile { Name = "Mein Monitor", Layout = new Layout("Mein Monitor", []) }];

    private void DispatcherTimerOnce(TimeSpan delay, Action action)
    {
        var t = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = delay };
        t.Tick += (_, _) => { t.Stop(); action(); };
        t.Start();
    }

    public void AddLog(string line)
    {
        void Add()
        {
            Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            while (Log.Count > 300) Log.RemoveAt(0);
        }
        if (_ui.CheckAccess()) Add(); else _ui.BeginInvoke(Add);
        try
        {
            Directory.CreateDirectory(SplitSession.RuntimeDirectory);
            File.AppendAllText(Path.Combine(SplitSession.RuntimeDirectory, "monivox.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    public void SaveSettings(SettingsDocument doc)
    {
        Settings = doc;
        Store.Save(doc);
        // Laufende Regeln sofort übernehmen
        if (State == SplitState.Running && ActiveProfile is not null)
        {
            var updated = doc.Profiles.FirstOrDefault(p => p.Name == ActiveProfile.Name);
            if (updated is not null && updated.Layout.Regions.SequenceEqual(ActiveProfile.Layout.Regions))
            {
                ActiveProfile = updated;
                _rules.Apply(updated.Apps, _lastRegions);
            }
        }
    }

    private IReadOnlyList<PixelRect> _lastRegions = [];

    /// <summary>Rahmen ein-/ausblenden – sofort im laufenden Compositor und für den nächsten Start gemerkt.</summary>
    public void ToggleFrames()
    {
        Settings = Settings with { ShowFrames = !Settings.ShowFrames };
        Store.Save(Settings);
        if (State == SplitState.Running)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(@"Global\MONIVOX-ToggleFrames");
                evt.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }
        StateChanged?.Invoke();
    }

    public Profile? FindProfile(string? name) => Settings.Profiles.FirstOrDefault(p => p.Name == name);

    /// <summary>Startet die Aufteilung mit dem angegebenen Profil (stoppt vorher eine laufende).</summary>
    public async Task StartAsync(Profile profile)
    {
        var errors = profile.Validate();
        if (errors.Count > 0)
        {
            Notify?.Invoke(string.Join(Environment.NewLine, errors), true);
            return;
        }
        if (State is SplitState.Running or SplitState.Starting) await StopAsync();

        ActiveProfile = profile;
        Settings = Settings with { LastProfile = profile.Name };
        Store.Save(Settings);
        SetState(SplitState.Starting);
        AddLog($"Starte Profil „{profile.Name}“ …");

        var options = new SplitOptions(profile.Layout,
            PhysicalDevice: string.IsNullOrWhiteSpace(profile.PhysicalMonitorId) ? null : NormalizeDevice(profile.PhysicalMonitorId),
            RefreshHz: profile.RefreshHz,
            CompositorPath: Path.Combine(AppContext.BaseDirectory, "MonivoxCompositor.exe"),
            ShowFrames: Settings.ShowFrames);

        var session = new SplitSession(options, AddLog);
        session.Ready += regions => _ui.BeginInvoke(() =>
        {
            _lastRegions = regions;
            SetState(SplitState.Running);
            _rules.Apply(profile.Apps, regions);
            Notify?.Invoke($"„{profile.Name}“ aktiv – {regions.Count} Bildschirme", false);
        });

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _runTask = Task.Run(() =>
        {
            try
            {
                int code = session.Run(token);
                if (!token.IsCancellationRequested) AddLog(code == 0 ? "Aufteilung beendet (Not-Aus)." : $"Aufteilung beendet (Code {code}).");
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                AddLog($"Fehler: {e.Message}");
                _ui.BeginInvoke(() => Notify?.Invoke(e.Message, true));
            }
            finally
            {
                try { SplitSession.Stop(AddLog); }
                catch (Exception e) when (e is InvalidOperationException or IOException) { AddLog($"Wiederherstellung: {e.Message}"); }
                _ui.BeginInvoke(() =>
                {
                    _rules.Clear();
                    _lastRegions = [];
                    SetState(SplitState.Stopped);
                    if (AvailableUpdate is not null && Settings.AutoUpdate) _ = InstallUpdateAsync();
                });
            }
        });
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null || _runTask is null) return;
        SetState(SplitState.Stopping);
        _cts.Cancel();
        await _runTask;
        _cts.Dispose();
        _cts = null;
        _runTask = null;
    }

    private static string NormalizeDevice(string value) =>
        value.StartsWith(@"\\.\", StringComparison.Ordinal) ? value : @"\\.\" + value.ToUpperInvariant();

    private void SetState(SplitState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    /// <summary>Gefundenes, noch nicht installiertes Update (oder null).</summary>
    public Updater.ReleaseInfo? AvailableUpdate { get; private set; }
    public bool UpdateBusy { get; private set; }
    public event Action? UpdateChanged;

    /// <summary>
    /// Sucht nach Updates. Automatisch installiert wird nur, wenn keine Aufteilung läuft (sonst würden mitten im
    /// Spiel die Bildschirme umgebaut). In jedem Fall erscheint in der App ein „Update installieren“-Knopf.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool interactive)
    {
        if (UpdateBusy) return;
        var updater = new Updater(AddLog);
        try
        {
            var release = await updater.CheckAsync();
            AvailableUpdate = release;
            UpdateChanged?.Invoke();
            if (release is null)
            {
                AddLog($"Update-Prüfung: MONIVOX {Updater.CurrentVersion} ist aktuell.");
                if (interactive) Notify?.Invoke($"MONIVOX {Updater.CurrentVersion} ist aktuell.", false);
                return;
            }
            AddLog($"Update {release.Tag} verfügbar (installiert: {Updater.CurrentVersion}).");
            if (!interactive && Settings.AutoUpdate && State == SplitState.Stopped)
            {
                await InstallUpdateAsync();
                return;
            }
            Notify?.Invoke($"Update {release.Tag} verfügbar – in MONIVOX auf „Update installieren“ klicken.", false);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddLog($"Update-Prüfung fehlgeschlagen: {e.Message}");
            if (interactive) Notify?.Invoke($"Update-Prüfung fehlgeschlagen: {e.Message}", true);
        }
    }

    /// <summary>Installiert das gefundene Update sofort (beendet dafür eine laufende Aufteilung) und startet neu.</summary>
    public async Task InstallUpdateAsync()
    {
        if (UpdateBusy) return;
        if (AvailableUpdate is null)
        {
            await CheckForUpdatesAsync(interactive: true);
            if (AvailableUpdate is null) return;
        }
        UpdateBusy = true;
        UpdateChanged?.Invoke();
        bool wasRunning = State != SplitState.Stopped;
        try
        {
            if (wasRunning) await StopAsync();
            var updater = new Updater(AddLog);
            var release = AvailableUpdate!;
            AddLog($"Installiere Update {release.Tag} …");
            bool driverChanged = await updater.InstallAsync(release);
            if (driverChanged) await Task.Run(() => VddControl.Install(AddLog));
            AddLog("Update installiert – MONIVOX startet neu.");
            Restart(release.Tag, resume: wasRunning);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddLog($"Update fehlgeschlagen: {e.Message}");
            Notify?.Invoke($"Update fehlgeschlagen: {e.Message}", true);
            UpdateBusy = false;
            UpdateChanged?.Invoke();
        }
    }

    public static void Restart(string? updatedTo = null, bool resume = false)
    {
        var exe = Environment.ProcessPath!;
        var visible = Application.Current.Windows.OfType<Window>().Any(w => w.IsVisible);
        string args = "--restarted" + (visible ? "" : " --tray") + (updatedTo is null ? "" : $" --updated {updatedTo}") + (resume ? " --resume" : "");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _rules.Dispose();
        _cts?.Dispose();
    }
}
