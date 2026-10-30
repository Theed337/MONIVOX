using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Monivox.Core;

namespace Monivox.Windows;

/// <summary>
/// Platziert Fenster automatisch auf ihrem Bereich: "Discord öffnet sich → rechts unten".
/// Arbeitet ausschließlich mit normalen Fenster-APIs (wie Win+Shift+Pfeil oder DisplayFusion):
/// kein Injizieren in Prozesse, kein Speicherzugriff – unbedenklich für Anti-Cheat-Systeme.
/// Muss auf einem Thread mit Nachrichtenschleife gestartet werden (z. B. dem UI-Thread).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowRules : IDisposable
{
    private readonly Action<string> _log;
    private readonly Action<Action> _post;
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    private IReadOnlyList<AppAssignment> _rules = [];
    private IReadOnlyList<PixelRect> _regions = [];
    private readonly HashSet<IntPtr> _placed = [];

    /// <param name="post">Führt eine Aktion verzögert auf dem UI-Thread aus (für die kurze Wartezeit nach dem Öffnen).</param>
    public WindowRules(Action<string> log, Action<Action> post)
    {
        _log = log;
        _post = post;
        _callback = OnWinEvent; // Referenz halten, sonst sammelt der GC den Delegaten ein
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _callback, 0, 0,
                                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>Neue Regeln/Bereiche übernehmen und bereits offene Fenster einsortieren.</summary>
    /// <param name="gatherFrom">Bereiche (Desktop-Koordinaten), aus denen nicht zugewiesene Fenster auf den
    /// Hauptmonitor geholt werden – z. B. die virtuellen Monitore und der aufgeteilte Monitor. Leer = nichts holen.</param>
    public void Apply(IReadOnlyList<AppAssignment> rules, IReadOnlyList<PixelRect> regions, IReadOnlyList<PixelRect>? gatherFrom = null)
    {
        _rules = rules;
        _regions = regions;
        _placed.Clear();

        if (gatherFrom is { Count: > 0 }) GatherUnassigned(gatherFrom);
        if (rules.Count == 0 || regions.Count == 0) return;

        EnumWindows((hwnd, _) => { TryPlace(hwnd); return true; }, IntPtr.Zero);
        StartMissingApps();
    }

    /// <summary>
    /// Holt alle sichtbaren Fenster ohne Zuweisung, die auf einem der angegebenen Bereiche liegen, auf den
    /// Hauptmonitor (von dort kann man sie bequem weiterschieben). Größe bleibt erhalten, soweit sie passt;
    /// maximierte Fenster werden auf dem Hauptmonitor wieder maximiert.
    /// </summary>
    public void GatherUnassigned(IReadOnlyList<PixelRect> fromAreas)
    {
        var target = FindMainMonitorWorkArea(fromAreas);
        if (target is not { } work)
        {
            _log("Kein anderer Monitor vorhanden – Fenster bleiben, wo sie sind.");
            return;
        }

        int moved = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsGatherCandidate(hwnd)) return true;
            if (!GetWindowRect(hwnd, out var r)) return true;
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            if (!fromAreas.Any(a => cx >= a.X && cx < a.Right && cy >= a.Y && cy < a.Bottom)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            string? path = ProcessPath(pid);
            string title = WindowTitle(hwnd);
            if (path is not null && _rules.Any(rule => Matches(rule, path, title))) return true; // hat eigene Zuweisung

            MoveToArea(hwnd, work, moved);
            moved++;
            return true;
        }, IntPtr.Zero);

        if (moved > 0) _log($"{moved} Fenster ohne Zuweisung auf den Hauptmonitor geholt.");
    }

    private static bool IsGatherCandidate(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetAncestor(hwnd, GA_ROOT) != hwnd) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;
        // Unsichtbar gehaltene Fenster (z. B. ausgesetzte Store-Apps) nicht anfassen
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (WindowTitle(hwnd).Length == 0) return false;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        string c = cls.ToString();
        return c is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "MonivoxView" or "MonivoxBackdrop");
    }

    private static void MoveToArea(IntPtr hwnd, PixelRect work, int index)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return;
        bool maximized = wp.showCmd == SW_SHOWMAXIMIZED;
        bool minimized = wp.showCmd == SW_SHOWMINIMIZED;

        // Normale Größe beibehalten, aber auf den Hauptmonitor begrenzen; leicht versetzt (Kaskade)
        int w = Math.Min(wp.rcNormalPosition.Right - wp.rcNormalPosition.Left, work.Width);
        int h = Math.Min(wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top, work.Height);
        int step = (index % 10) * 32;
        int x = work.X + Math.Min(step + 40, Math.Max(0, work.Width - w));
        int y = work.Y + Math.Min(step + 40, Math.Max(0, work.Height - h));

        if (maximized || minimized)
        {
            // rcNormalPosition ist in Arbeitsbereich-Koordinaten des primären Monitors angegeben
            var off = PrimaryWorkAreaOffset();
            wp.rcNormalPosition = new RECT { Left = x - off.X, Top = y - off.Y, Right = x - off.X + w, Bottom = y - off.Y + h };
            if (minimized) wp.flags = 0;
            SetWindowPlacement(hwnd, ref wp);
            if (maximized)
            {
                // Maximieren erfolgt auf dem Monitor, auf dem die normale Position liegt
                ShowWindow(hwnd, SW_RESTORE);
                SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
                ShowWindow(hwnd, SW_MAXIMIZE);
            }
        }
        else
        {
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    /// <summary>Arbeitsbereich des Hauptmonitors: primärer Monitor, sonst erster Monitor außerhalb der aufgeteilten Bereiche.</summary>
    private static PixelRect? FindMainMonitorWorkArea(IReadOnlyList<PixelRect> splitAreas)
    {
        var monitors = new List<(PixelRect Rect, PixelRect Work, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
                monitors.Add((ToPixel(mi.rcMonitor), ToPixel(mi.rcWork), (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);

        bool IsSplit(PixelRect r) => splitAreas.Any(a => r.X < a.Right && a.X < r.Right && r.Y < a.Bottom && a.Y < r.Bottom);
        var candidates = monitors.Where(m => !IsSplit(m.Rect)).ToList();
        if (candidates.Count == 0) return null;
        return (candidates.FirstOrDefault(m => m.Primary) is { Rect.Width: > 0 } p ? p : candidates[0]).Work;
    }

    private static (int X, int Y) PrimaryWorkAreaOffset()
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var primary = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        if (!GetMonitorInfo(primary, ref mi)) return (0, 0);
        return (mi.rcWork.Left - mi.rcMonitor.Left, mi.rcWork.Top - mi.rcMonitor.Top);
    }

    private static PixelRect ToPixel(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public void Clear()
    {
        _rules = [];
        _regions = [];
        _placed.Clear();
    }

    private void StartMissingApps()
    {
        foreach (var rule in _rules.Where(r => r.StartWithProfile))
        {
            string name = Path.GetFileNameWithoutExtension(rule.Executable);
            if (Process.GetProcessesByName(name).Length > 0) continue;
            if (!File.Exists(rule.Executable))
            {
                _log($"{rule.Executable} nicht gefunden – kann nicht gestartet werden.");
                continue;
            }
            try
            {
                // Über den Explorer starten, damit das Programm NICHT mit Adminrechten läuft.
                var psi = string.IsNullOrWhiteSpace(rule.Arguments)
                    ? new ProcessStartInfo("explorer.exe", $"\"{rule.Executable}\"")
                    : new ProcessStartInfo(rule.Executable, rule.Arguments) { UseShellExecute = true };
                Process.Start(psi);
                _log($"{Path.GetFileName(rule.Executable)} gestartet.");
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                _log($"{rule.Executable}: {e.Message}");
            }
        }
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero || _rules.Count == 0) return;
        // Viele Programme setzen Größe/Position direkt nach dem Anzeigen selbst – kurz warten.
        _post(() => TryPlace(hwnd));
    }

    private void TryPlace(IntPtr hwnd)
    {
        if (_placed.Contains(hwnd) || !IsWindowVisible(hwnd) || GetAncestor(hwnd, GA_ROOT) != hwnd) return;
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return; // Dialoge bleiben beim Hauptfenster

        GetWindowThreadProcessId(hwnd, out uint pid);
        string? path = ProcessPath(pid);
        if (path is null) return;
        string title = WindowTitle(hwnd);

        foreach (var rule in _rules)
        {
            if (!Matches(rule, path, title)) continue;
            if (rule.RegionIndex < 0 || rule.RegionIndex >= _regions.Count) return;
            _placed.Add(hwnd);
            Place(hwnd, _regions[rule.RegionIndex], rule);
            _log($"{Path.GetFileName(path)} → Bereich {rule.RegionIndex + 1}");
            return;
        }
    }

    public static bool Matches(AppAssignment rule, string processPath, string title)
    {
        string wanted = rule.Executable.Trim();
        bool exeMatch = wanted.Contains('\\') || wanted.Contains('/')
            ? string.Equals(Path.GetFullPath(wanted), processPath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetFileName(processPath), wanted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? wanted : wanted + ".exe",
                            StringComparison.OrdinalIgnoreCase);
        if (!exeMatch) return false;
        if (string.IsNullOrWhiteSpace(rule.WindowTitlePattern)) return true;
        try { return Regex.IsMatch(title, rule.WindowTitlePattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { return false; }
    }

    private static void Place(IntPtr hwnd, PixelRect region, AppAssignment rule)
    {
        ShowWindow(hwnd, SW_RESTORE);
        switch (rule.State)
        {
            case Core.WindowState.Maximized:
                SetWindowPos(hwnd, IntPtr.Zero, region.X + 50, region.Y + 50, Math.Max(400, region.Width - 100), Math.Max(300, region.Height - 100),
                             SWP_NOZORDER | SWP_NOACTIVATE);
                ShowWindow(hwnd, SW_MAXIMIZE);
                break;
            case Core.WindowState.Minimized:
                SetWindowPos(hwnd, IntPtr.Zero, region.X + 50, region.Y + 50, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
                ShowWindow(hwnd, SW_MINIMIZE);
                break;
            case Core.WindowState.BorderlessFullscreen:
                long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                style &= ~(WS_CAPTION | WS_THICKFRAME);
                SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
                SetWindowPos(hwnd, IntPtr.Zero, region.X, region.Y, region.Width, region.Height,
                             SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                break;
            default:
                var b = rule.Bounds ?? new PixelRect(region.Width / 10, region.Height / 10, region.Width * 8 / 10, region.Height * 8 / 10);
                SetWindowPos(hwnd, IntPtr.Zero, region.X + b.X, region.Y + b.Y, b.Width, b.Height, SWP_NOZORDER | SWP_NOACTIVATE);
                break;
        }
    }

    private static string? ProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    // ---- Win32 ----
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT { public int length; public int flags; public int showCmd; public POINT ptMinPosition; public POINT ptMaxPosition; public RECT rcNormalPosition; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    private const int SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3;
    private const uint MONITORINFOF_PRIMARY = 1, MONITOR_DEFAULTTOPRIMARY = 1;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const uint GA_ROOT = 2, GW_OWNER = 4;
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_EX_TOOLWINDOW = 0x00000080;
    private const int SW_RESTORE = 9, SW_MAXIMIZE = 3, SW_MINIMIZE = 6;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate proc, uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref int size);
}
