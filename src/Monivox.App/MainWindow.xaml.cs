using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Monivox.Core;
using Monivox.Windows;
using CoreWindowState = Monivox.Core.WindowState;

namespace Monivox.App;

public partial class MainWindow : Window
{
    private readonly SplitController _controller;
    private List<Profile> _profiles;
    private int _current;
    private bool _loading;
    private readonly List<RuleRow> _rules = [];

    private sealed class RuleRow
    {
        public string Executable = "";
        public string? Arguments;
        public string? TitlePattern;
        public string? TitleContains;
        public List<int> Regions = [];
        public CoreWindowState State = CoreWindowState.Maximized;
        public bool Start;
    }

    public MainWindow(SplitController controller)
    {
        _controller = controller;
        _profiles = controller.Settings.Profiles.ToList();
        InitializeComponent();
        SourceInitialized += (_, _) => Theme.ApplyWindowChrome(this);

        VersionText.Text = $"Version {Updater.CurrentVersion}";
        LogList.ItemsSource = controller.Log;
        controller.StateChanged += () => Dispatcher.BeginInvoke(RefreshState);

        Editor.Changed += OnEditorChanged;
        Editor.SelectionChanged += ShowSelection;

        LoadMonitors();
        _loading = true;
        foreach (var p in _profiles) ProfileBox.Items.Add(p.Name);
        _current = Math.Max(0, _profiles.FindIndex(p => p.Name == controller.Settings.LastProfile));
        ProfileBox.SelectedIndex = _profiles.Count > 0 ? _current : -1;
        _loading = false;
        LoadProfile();

        AutostartBox.IsChecked = SafeAutostartEnabled();
        SplitOnStartBox.IsChecked = controller.Settings.SplitOnStartup;
        AutoUpdateBox.IsChecked = controller.Settings.AutoUpdate;
        FramesBox.IsChecked = controller.Settings.ShowFrames;
        GatherBox.IsChecked = controller.Settings.GatherWindows;
        controller.StateChanged += () => Dispatcher.BeginInvoke(() => FramesBox.IsChecked = _controller.Settings.ShowFrames);

        RefreshDriver();
        RefreshAntiCheat();
        RefreshState();
        controller.UpdateChanged += () => Dispatcher.BeginInvoke(RefreshUpdate);
        RefreshUpdate();
    }

    private void RefreshUpdate()
    {
        var upd = _controller.AvailableUpdate;
        UpdateCard.Visibility = upd is null ? Visibility.Collapsed : Visibility.Visible;
        if (upd is null) return;
        UpdateText.Text = _controller.UpdateBusy ? $"Update {upd.Tag} wird installiert …" : $"Update {upd.Tag} verfügbar";
        UpdateButton.IsEnabled = !_controller.UpdateBusy;
        UpdateButton.Content = _controller.State == SplitState.Running ? "Teilen beenden & aktualisieren" : "Update installieren";
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await _controller.InstallUpdateAsync();

    // Schließen = in den Infobereich minimieren
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private Profile Current => _profiles[_current];

    // ================================================================ Navigation / Status

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        string page = (string)((RadioButton)sender).Tag;
        foreach (var p in new FrameworkElement[] { PageLayout, PageApps, PageSettings, PageStatus })
            p.Visibility = p.Name == page ? Visibility.Visible : Visibility.Collapsed;
        if (page == "PageApps") BuildRules();
        if (page == "PageStatus") { RefreshDriver(); RefreshAntiCheat(); }
    }

    private void RefreshState()
    {
        var s = _controller.State;
        StatusText.Text = s switch
        {
            SplitState.Running => $"Aktiv: {_controller.ActiveProfile?.Name}",
            SplitState.Starting => "Wird gestartet …",
            SplitState.Stopping => "Wird beendet …",
            _ => "Nicht aktiv",
        };
        StatusDot.Fill = (Brush)FindResource(s == SplitState.Running ? "Ok" : "TextDim");
        ToggleButton.Content = s == SplitState.Running ? "Teilen beenden" : "Teilen starten";
        ToggleButton.IsEnabled = s is SplitState.Running or SplitState.Stopped;
        if (_profiles.Count > 0) SyncAppsProfileBox();
        RefreshUpdate();
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.State == SplitState.Running)
        {
            await _controller.StopAsync();
            return;
        }
        if (!SaveAll()) return;
        if (!VddControl.IsInstalled)
        {
            MessageBox.Show(this, "Bitte zuerst unter „Status & Treiber“ den Anzeigetreiber installieren.", "MONIVOX");
            return;
        }
        await _controller.StartAsync(Current);
    }

    // ================================================================ Monitore & Profile

    private sealed record MonitorItem(string Device, DisplayMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    private void LoadMonitors()
    {
        MonitorBox.Items.Clear();
        foreach (var d in Displays.Enumerate().Where(d => !Displays.IsVirtual(d) && d.IsAttached))
        {
            string label = $"{d.ShortName} – {d.Mode.Width}×{d.Mode.Height} @ {d.Mode.RefreshHz} Hz{(d.IsPrimary ? " (Hauptmonitor)" : "")}";
            MonitorBox.Items.Add(new MonitorItem(d.ShortName, d.Mode, label));
        }
    }

    private void LoadProfile()
    {
        if (_profiles.Count == 0) return;
        _loading = true;
        var p = Current;
        var match = MonitorBox.Items.Cast<MonitorItem>().FirstOrDefault(m => m.Device.Equals(p.PhysicalMonitorId, StringComparison.OrdinalIgnoreCase));
        MonitorBox.SelectedItem = match ?? MonitorBox.Items.Cast<MonitorItem>().FirstOrDefault();
        ApplyMonitorSize();
        HzBox.Text = p.RefreshHz.ToString(CultureInfo.InvariantCulture);
        Editor.Load(p.Layout);
        _rules.Clear();
        _rules.AddRange(p.Apps.Select(a => new RuleRow
        {
            Executable = a.Executable, Arguments = a.Arguments, TitlePattern = a.WindowTitlePattern, TitleContains = a.TitleContains,
            Regions = a.TargetRegions().ToList(), State = a.State, Start = a.StartWithProfile,
        }));
        SyncAppsProfileBox();
        _loading = false;
        Validate();
        UpdateRegionCount();
    }

    private void ApplyMonitorSize()
    {
        if (MonitorBox.SelectedItem is MonitorItem m && m.Mode.Width > 0)
        {
            Editor.PhysicalWidth = m.Mode.Width;
            Editor.PhysicalHeight = m.Mode.Height;
            Editor.InvalidateVisual();
        }
    }

    /// <summary>Übernimmt Editor, Felder und Regeln in das aktuelle Profil (nur im Speicher).</summary>
    private void CaptureProfile()
    {
        if (_profiles.Count == 0) return;
        int.TryParse(HzBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hz);
        _profiles[_current] = Current with
        {
            PhysicalMonitorId = (MonitorBox.SelectedItem as MonitorItem)?.Device ?? "",
            RefreshHz = Math.Clamp(hz, 0, 500),
            Layout = Editor.ToLayout(Current.Name),
            Apps = CaptureRules(),
        };
    }

    private List<AppAssignment> CaptureRules() =>
        _rules.Where(r => !string.IsNullOrWhiteSpace(r.Executable)).Select(r => new AppAssignment
        {
            Executable = r.Executable, Arguments = r.Arguments, WindowTitlePattern = r.TitlePattern,
            TitleContains = string.IsNullOrWhiteSpace(r.TitleContains) ? null : r.TitleContains.Trim(),
            RegionIndex = r.Regions.Count > 0 ? r.Regions[0] : 0,
            Regions = r.Regions.Count > 1 ? r.Regions.ToList() : null,
            State = r.State, StartWithProfile = r.Start,
        }).ToList();

    /// <summary>
    /// Speichert nur die Programmzuweisungen des aktuellen Profils – sofort bei jeder Änderung, auch wenn das
    /// Layout gerade ungültig ist (vorher gingen gelöschte Einträge verloren, wenn man nicht „Speichern“ drückte).
    /// </summary>
    private void SaveRules()
    {
        if (_profiles.Count == 0 || _loading) return;
        var apps = CaptureRules();
        _profiles[_current] = Current with { Apps = apps };
        string name = Current.Name;
        var saved = _controller.Settings.Profiles.ToList();
        int idx = saved.FindIndex(p => p.Name == name);
        if (idx >= 0) saved[idx] = saved[idx] with { Apps = apps };
        else saved.Add(Current);
        _controller.SaveSettings(_controller.Settings with { Profiles = saved });
    }

    private void SyncAppsProfileBox()
    {
        bool was = _loading;
        _loading = true;
        AppsProfileBox.Items.Clear();
        foreach (var p in _profiles) AppsProfileBox.Items.Add(p.Name);
        AppsProfileBox.SelectedIndex = _profiles.Count > 0 ? _current : -1;
        _loading = was;
        AppsProfileHint.Text = _profiles.Count > 0 && _controller.State == SplitState.Running && _controller.ActiveProfile?.Name == Current.Name
            ? "● gerade aktiv"
            : "Gilt, sobald dieses Profil geteilt wird.";
    }

    private void AppsProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppsProfileBox.SelectedIndex < 0 || AppsProfileBox.SelectedIndex == _current) return;
        ProfileBox.SelectedIndex = AppsProfileBox.SelectedIndex; // löst den normalen Profilwechsel aus
    }

    private bool SaveAll()
    {
        CaptureProfile();
        if (!Validate()) return false;
        _controller.SaveSettings(_controller.Settings with { Profiles = _profiles.ToList() });
        return true;
    }

    private bool Validate()
    {
        var layout = Editor.ToLayout("x");
        var errors = layout.Validate(Editor.PhysicalWidth, Editor.PhysicalHeight);
        if (MonitorBox.SelectedItem is null) errors = [.. errors, "Kein Monitor gewählt."];
        ValidationText.Text = string.Join("  ", errors);
        return errors.Count == 0;
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedIndex < 0) return;
        CaptureProfile();
        _current = ProfileBox.SelectedIndex;
        LoadProfile();
        if (PageApps.Visibility == Visibility.Visible) BuildRules();
    }

    private void MonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ApplyMonitorSize();
        Validate();
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, "Neues Profil", "Name des Profils:", "Neues Profil");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_profiles.Any(p => p.Name == name)) { MessageBox.Show(this, "Diesen Namen gibt es schon.", "MONIVOX"); return; }
        CaptureProfile();
        _profiles.Add(new Profile { Name = name, Layout = new Layout(name, []), PhysicalMonitorId = (MonitorBox.SelectedItem as MonitorItem)?.Device ?? "" });
        ProfileBox.Items.Add(name);
        ProfileBox.SelectedIndex = _profiles.Count - 1;
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, "Profil umbenennen", "Neuer Name:", Current.Name);
        if (string.IsNullOrWhiteSpace(name) || _profiles.Any(p => p.Name == name)) return;
        CaptureProfile();
        _profiles[_current] = Current with { Name = name };
        _loading = true;
        ProfileBox.Items[_current] = name;
        ProfileBox.SelectedIndex = _current;
        _loading = false;
        SyncAppsProfileBox();
        SaveAll();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count <= 1) return;
        if (MessageBox.Show(this, $"Profil „{Current.Name}“ löschen?", "MONIVOX", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _profiles.RemoveAt(_current);
        _loading = true;
        ProfileBox.Items.RemoveAt(_current);
        _current = 0;
        ProfileBox.SelectedIndex = 0;
        _loading = false;
        LoadProfile();
        _controller.SaveSettings(_controller.Settings with { Profiles = _profiles.ToList() });
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        CaptureProfile();
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Current.Name + ".monivox.json", Filter = "MONIVOX-Profil|*.monivox.json" };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, ProfileStore.Export(Current));
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "MONIVOX-Profil|*.monivox.json;*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var p = ProfileStore.Import(File.ReadAllText(dlg.FileName));
            string name = p.Name;
            for (int i = 2; _profiles.Any(x => x.Name == name); i++) name = $"{p.Name} ({i})";
            CaptureProfile();
            _profiles.Add(p with { Name = name });
            ProfileBox.Items.Add(name);
            ProfileBox.SelectedIndex = _profiles.Count - 1;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            MessageBox.Show(this, $"Profil konnte nicht gelesen werden: {ex.Message}", "MONIVOX");
        }
    }

    // ================================================================ Editor

    private void AddRegion_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.Regions.Count >= Layout.MaxRegions) return;
        // Neuer Bereich mittig, ein Viertel der Fläche – danach frei verschieben/ziehen
        Editor.AddRegion(new NormRect(0.375, 0.375, 0.25, 0.25));
    }

    private void ClearRegions_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.Regions.Count == 0) return;
        if (MessageBox.Show(this, "Alle Bereiche dieses Profils löschen?", "MONIVOX", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Editor.Clear();
    }

    private void TaskbarBox_Click(object sender, RoutedEventArgs e)
    {
        int i = Editor.SelectedIndex;
        if (i >= 0) Editor.SetTaskbarHidden(i, TaskbarBox.IsChecked == true);
    }

    private void OnEditorChanged()
    {
        if (_loading) return;
        ShowSelection();
        Validate();
        UpdateRegionCount();
    }

    private void UpdateRegionCount()
    {
        int n = Editor.Regions.Count;
        RegionCountText.Text = n == 0
            ? "Ziehe mit der Maus auf dem Monitor Bereiche auf. Kanten und Ecken lassen sich verschieben."
            : $"{n} von max. {Layout.MaxRegions} Bereichen" + (n > 16 ? " – mehr als 16 virtuelle Monitore sind noch nicht ausgiebig getestet." : "") +
              " Neue Bereiche einfach auf freier Fläche aufziehen.";
    }

    private void ShowSelection()
    {
        int i = Editor.SelectedIndex;
        bool has = i >= 0 && i < Editor.Regions.Count;
        SelectionPanel.IsEnabled = has;
        if (!has)
        {
            SelectionTitle.Text = "Kein Bereich gewählt – klicke einen Bereich an oder ziehe einen neuen auf.";
            NameBox.Text = XBox.Text = YBox.Text = WBox.Text = HBox.Text = "";
            DirectBox.IsChecked = false;
            TaskbarBox.IsChecked = false;
            return;
        }
        var r = Editor.Regions[i];
        int w = Editor.PhysicalWidth, h = Editor.PhysicalHeight;
        SelectionTitle.Text = $"Bereich {i + 1}";
        _loading = true;
        NameBox.Text = Editor.Names[i];
        XBox.Text = ((int)Math.Round(r.X * w)).ToString(CultureInfo.InvariantCulture);
        YBox.Text = ((int)Math.Round(r.Y * h)).ToString(CultureInfo.InvariantCulture);
        WBox.Text = ((int)Math.Round(r.Width * w)).ToString(CultureInfo.InvariantCulture);
        HBox.Text = ((int)Math.Round(r.Height * h)).ToString(CultureInfo.InvariantCulture);
        DirectBox.IsChecked = Editor.DirectIndex == i;
        TaskbarBox.IsChecked = Editor.IsTaskbarHidden(i);
        TaskbarBox.IsEnabled = Editor.DirectIndex != i;
        _loading = false;
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        int i = Editor.SelectedIndex;
        if (sender == HzBox || i < 0) { Validate(); return; }
        if (sender == NameBox) { Editor.SetName(i, NameBox.Text.Trim()); return; }

        int w = Editor.PhysicalWidth, h = Editor.PhysicalHeight;
        static int Parse(TextBox b) => int.TryParse(b.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
        int x = Parse(XBox), y = Parse(YBox), rw = Parse(WBox), rh = Parse(HBox);
        if (x < 0 || y < 0 || rw <= 0 || rh <= 0) { ShowSelection(); return; }
        Editor.SetRegion(i, new NormRect((double)x / w, (double)y / h, (double)rw / w, (double)rh / h));
    }

    private void DirectBox_Click(object sender, RoutedEventArgs e)
    {
        int i = Editor.SelectedIndex;
        if (i < 0) return;
        Editor.DirectIndex = DirectBox.IsChecked == true ? i : (Editor.DirectIndex == i ? -1 : Editor.DirectIndex);
    }

    private void Frames_Click(object sender, RoutedEventArgs e)
    {
        if ((FramesBox.IsChecked == true) != _controller.Settings.ShowFrames) _controller.ToggleFrames();
    }

    private void SplitV_Click(object sender, RoutedEventArgs e) => Editor.SplitSelected(vertical: true);
    private void SplitH_Click(object sender, RoutedEventArgs e) => Editor.SplitSelected(vertical: false);
    private void Remove_Click(object sender, RoutedEventArgs e) => Editor.RemoveSelected();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SaveAll()) _controller.AddLog($"Profil „{Current.Name}“ gespeichert.");
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveAll()) return;
        if (!VddControl.IsInstalled)
        {
            MessageBox.Show(this, "Bitte zuerst unter „Status & Treiber“ den Anzeigetreiber installieren.", "MONIVOX");
            return;
        }
        await _controller.StartAsync(Current);
    }

    // ================================================================ Programme

    private void BuildRules()
    {
        RulesPanel.Children.Clear();
        int regionCount = Editor.Regions.Count;
        if (_rules.Count == 0)
        {
            RulesPanel.Children.Add(new TextBlock { Text = "Noch keine Programme zugewiesen.", Foreground = (Brush)FindResource("TextDim") });
            return;
        }

        foreach (var rule in _rules.ToList())
        {
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

            // Zeile 1: Programm, Fensterzustand, Mit Profil starten, Entfernen
            var row = new Grid();
            foreach (var w in new[] { 2.6, 1.6, 1.4, 0.0 })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? GridLength.Auto : new GridLength(w, GridUnitType.Star) });

            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = Path.GetFileName(rule.Executable), FontWeight = FontWeights.SemiBold });
            name.Children.Add(new TextBlock { Text = rule.Executable, Style = (Style)FindResource("Sub"), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(name);

            var states = new[] { ("Maximiert", CoreWindowState.Maximized), ("Fenster", CoreWindowState.Normal), ("Randlos Vollbild", CoreWindowState.BorderlessFullscreen), ("Minimiert", CoreWindowState.Minimized) };
            var state = new ComboBox { ItemsSource = states.Select(s => s.Item1).ToList(), SelectedIndex = Array.FindIndex(states, s => s.Item2 == rule.State), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            state.SelectionChanged += (_, _) => { rule.State = states[Math.Max(0, state.SelectedIndex)].Item2; SaveRules(); };
            Grid.SetColumn(state, 1);
            row.Children.Add(state);

            var start = new CheckBox { Content = "Mit Profil starten", IsChecked = rule.Start, VerticalAlignment = VerticalAlignment.Center };
            start.Click += (_, _) => { rule.Start = start.IsChecked == true; SaveRules(); };
            Grid.SetColumn(start, 2);
            row.Children.Add(start);

            var remove = new Button { Content = "Entfernen", Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (_, _) => { _rules.Remove(rule); SaveRules(); BuildRules(); };
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            card.Children.Add(row);

            // Zeile 2: Bildschirme (mehrere = Fenster werden verteilt) und Titelfilter
            var line = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            line.Children.Add(new TextBlock { Text = "Bildschirme:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            for (int i = 0; i < regionCount; i++)
            {
                int idx = i;
                var box = new CheckBox
                {
                    Content = $"{i + 1}", IsChecked = rule.Regions.Contains(i), Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = Editor.Names[i],
                };
                box.Click += (_, _) =>
                {
                    if (box.IsChecked == true) { if (!rule.Regions.Contains(idx)) rule.Regions.Add(idx); }
                    else if (rule.Regions.Count > 1) rule.Regions.Remove(idx);
                    else box.IsChecked = true; // mindestens ein Bildschirm
                    SaveRules();
                };
                line.Children.Add(box);
            }
            if (rule.Regions.Any(r => r >= regionCount))
                line.Children.Add(new TextBlock { Text = "(Bereich existiert im Layout nicht mehr)", Foreground = (Brush)FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center });

            line.Children.Add(new TextBlock { Text = "Titel enthält:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) });
            var title = new TextBox { Text = rule.TitleContains ?? "", Width = 200, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Optional. Nur Fenster, deren Titel diesen Text enthält (mehrere mit ; trennen). Leer = alle Fenster des Programms." };
            title.LostFocus += (_, _) => { if ((rule.TitleContains ?? "") != title.Text) { rule.TitleContains = title.Text; SaveRules(); } };
            line.Children.Add(title);
            card.Children.Add(line);

            RulesPanel.Children.Add(card);
        }
    }

    private void AddExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programme|*.exe", Title = "Programm auswählen" };
        if (dlg.ShowDialog(this) != true) return;
        AddRule(dlg.FileName);
    }

    private void AddRunning_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<(string Label, string Path)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                string? path = p.MainModule?.FileName;
                if (path is null || items.Any(i => i.Path == path)) continue;
                items.Add(($"{p.ProcessName}  –  {p.MainWindowTitle}", path));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        var chosen = InputDialog.Choose(this, "Laufendes Programm", "Welches Programm soll einen festen Platz bekommen?", items.OrderBy(i => i.Label).ToList());
        if (chosen is not null) AddRule(chosen);
    }

    private void AddRule(string exe)
    {
        // Dasselbe Programm darf mehrfach vorkommen (z. B. Browser: "YouTube" → Bereich 3, alle anderen → 1 und 2)
        _rules.Add(new RuleRow { Executable = exe, Regions = [Math.Max(0, Editor.SelectedIndex)] });
        SaveRules();
        BuildRules();
    }

    // ================================================================ Einstellungen

    private static bool SafeAutostartEnabled()
    {
        try { return Autostart.IsEnabled(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender == AutostartBox)
            {
                if (AutostartBox.IsChecked == true) Autostart.Enable(Environment.ProcessPath!);
                else Autostart.Disable();
            }
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "MONIVOX");
            AutostartBox.IsChecked = SafeAutostartEnabled();
        }
        _controller.SaveSettings(_controller.Settings with
        {
            StartWithWindows = AutostartBox.IsChecked == true,
            SplitOnStartup = SplitOnStartBox.IsChecked == true,
            AutoUpdate = AutoUpdateBox.IsChecked == true,
            GatherWindows = GatherBox.IsChecked == true,
        });
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await _controller.CheckForUpdatesAsync(interactive: true);

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SplitSession.RuntimeDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SplitSession.RuntimeDirectory}\"") { UseShellExecute = true });
    }

    // ================================================================ Status

    private void RefreshDriver()
    {
        bool installed = VddControl.IsInstalled;
        DriverText.Text = installed ? "Anzeigetreiber: installiert ✓" : "Anzeigetreiber: nicht installiert";
        DriverText.Foreground = (Brush)FindResource(installed ? "Ok" : "Danger");
        InstallDriverButton.Content = installed ? "Neu installieren" : "Treiber installieren";
    }

    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        InstallDriverButton.IsEnabled = false;
        try
        {
            await System.Threading.Tasks.Task.Run(() => VddControl.Install(_controller.AddLog));
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "MONIVOX");
        }
        finally
        {
            InstallDriverButton.IsEnabled = true;
            RefreshDriver();
        }
    }

    private void CheckAntiCheat_Click(object sender, RoutedEventArgs e) => RefreshAntiCheat();

    private void RefreshAntiCheat()
    {
        AntiCheatPanel.Children.Clear();
        var findings = AntiCheatCheck.Run();
        if (findings.Count == 0)
        {
            AntiCheatPanel.Children.Add(new TextBlock { Text = "✓ Keine Auffälligkeiten (kein Testmodus, kein Anti-Cheat aktiv).", Foreground = (Brush)FindResource("Ok"), TextWrapping = TextWrapping.Wrap });
        }
        foreach (var f in findings)
        {
            AntiCheatPanel.Children.Add(new TextBlock
            {
                Text = (f.Critical ? "⚠ " : "ℹ ") + f.Message,
                Foreground = (Brush)FindResource(f.Critical ? "Danger" : "TextDim"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }
        AntiCheatPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Sub"),
            Margin = new Thickness(0, 6, 0, 0),
            Text = "Kopiergeschützte Videos (Netflix, Disney+ …) werden von Windows auf aufgeteilten Bereichen schwarz dargestellt – " +
                   "das ist ein Schutz von Windows/Streamingdiensten. Filme daher auf einem nicht aufgeteilten Monitor oder im " +
                   "Browser mit deaktivierter Hardwarebeschleunigung abspielen.",
        });
    }
}
