using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Monivox.Windows;
using Forms = System.Windows.Forms;

namespace Monivox.App;

public partial class App : Application
{
    private const string MutexName = "Global\\MONIVOX-SingleInstance";
    private const string ShowEventName = "Global\\MONIVOX-Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private DispatcherTimer? _updateTimer;

    public SplitController Controller { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool startHidden = e.Args.Contains("--tray");

        // Nur eine Instanz. Ein zweiter Start holt das Fenster der laufenden Instanz nach vorn.
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first && e.Args.Contains("--restarted"))
        {
            // Nach einem Update: kurz warten, bis die alte Instanz beendet ist
            try { first = _mutex.WaitOne(TimeSpan.FromSeconds(15)); } catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var showThread = new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true };
        showThread.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            Controller?.AddLog($"Unerwarteter Fehler: {args.Exception.Message}");
            args.Handled = true;
        };

        Theme.Apply(Resources);
        Updater.CleanupOldFiles();
        Controller = new SplitController(Dispatcher);
        Controller.StateChanged += () => Dispatcher.BeginInvoke(RefreshTray);
        Controller.Notify += (text, error) => Dispatcher.BeginInvoke(() =>
            _tray?.ShowBalloonTip(4000, "MONIVOX", text, error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info));

        CreateTray();

        // Nach einem Absturz: Anordnung wiederherstellen, bevor irgendetwas anderes passiert
        if (System.IO.File.Exists(SplitSession.RestorePath) && !SplitSession.IsRunning && !Controller.Settings.SplitOnStartup)
        {
            await Task.Run(() => SplitSession.Stop(Controller.AddLog));
        }

        if (!startHidden || !VddControl.IsInstalled) ShowMainWindow();

        // "Die Bildschirme entstehen, sobald MONIVOX startet"
        if (Controller.Settings.SplitOnStartup && VddControl.IsInstalled &&
            Controller.FindProfile(Controller.Settings.LastProfile) is { } last)
        {
            await Controller.StartAsync(last);
        }

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) => await Controller.CheckForUpdatesAsync(interactive: false);
        _updateTimer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Dispatcher.BeginInvoke(async () => await Controller.CheckForUpdatesAsync(interactive: false)));
    }

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(Controller);
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == System.Windows.WindowState.Minimized) _window.WindowState = System.Windows.WindowState.Normal;
        _window.Activate();
    }

    // ------------------------------------------------------------------ Tray

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "MONIVOX",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();
        _tray.ContextMenuStrip.Opening += (_, _) => RefreshTray();
        RefreshTray();
    }

    private void RefreshTray()
    {
        if (_tray?.ContextMenuStrip is not { } menu) return;
        menu.Items.Clear();

        var state = Controller.State;
        string status = state switch
        {
            SplitState.Running => $"Aktiv: {Controller.ActiveProfile?.Name}",
            SplitState.Starting => "Wird gestartet …",
            SplitState.Stopping => "Wird beendet …",
            _ => "Nicht aktiv",
        };
        menu.Items.Add(new Forms.ToolStripMenuItem($"MONIVOX – {status}") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        if (state == SplitState.Running)
            menu.Items.Add("Teilen beenden", null, async (_, _) => await Controller.StopAsync());
        else if (state == SplitState.Stopped && Controller.FindProfile(Controller.Settings.LastProfile) is { } last)
            menu.Items.Add($"Teilen starten ({last.Name})", null, async (_, _) => await Controller.StartAsync(last));

        var profiles = new Forms.ToolStripMenuItem("Profil wechseln");
        foreach (var p in Controller.Settings.Profiles)
        {
            var item = new Forms.ToolStripMenuItem(p.Name)
            {
                Checked = state == SplitState.Running && Controller.ActiveProfile?.Name == p.Name,
            };
            var profile = p;
            item.Click += async (_, _) => await Controller.StartAsync(profile);
            profiles.DropDownItems.Add(item);
        }
        menu.Items.Add(profiles);
        menu.Items.Add(new Forms.ToolStripMenuItem("Rahmen anzeigen (Strg+Alt+Shift+F)", null, (_, _) => Controller.ToggleFrames())
        {
            Checked = Controller.Settings.ShowFrames,
        });

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Einstellungen öffnen", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Nach Updates suchen", null, async (_, _) => await Controller.CheckForUpdatesAsync(interactive: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("MONIVOX beenden", null, async (_, _) => await ExitAsync());

        _tray.Text = $"MONIVOX – {status}".Length > 63 ? "MONIVOX" : $"MONIVOX – {status}";
    }

    public async Task ExitAsync()
    {
        await Controller.StopAsync();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Controller.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        base.OnExit(e);
    }

    /// <summary>Zeichnet das Tray-Symbol: zwei Bildschirmhälften im Rahmen.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var frame = new SolidBrush(Color.FromArgb(124, 92, 255));
            using var left = new SolidBrush(Color.White);
            using var right = new SolidBrush(Color.FromArgb(220, 214, 255));
            g.FillRectangle(frame, 1, 4, 30, 21);
            g.FillRectangle(left, 4, 7, 11, 15);
            g.FillRectangle(right, 17, 7, 11, 15);
            g.FillRectangle(frame, 12, 25, 8, 3);
            g.FillRectangle(frame, 8, 28, 16, 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
