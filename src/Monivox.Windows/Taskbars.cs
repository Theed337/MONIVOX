using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Monivox.Windows;

/// <summary>
/// Stellt die Windows-Taskleisten wieder her. Der Compositor blendet beim Teilen Taskleisten aus und blendet sie
/// beim normalen Beenden wieder ein – wird er aber hart beendet (Teilen beenden, Absturz, Update), bliebe die
/// Taskleiste unsichtbar. Deshalb räumt die App danach selbst auf.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Taskbars
{
    /// <summary>Blendet alle versteckten Taskleisten wieder ein. Gibt die Anzahl zurück.</summary>
    public static int ShowAll()
    {
        int shown = 0;
        EnumWindows((hwnd, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            string c = cls.ToString();
            if (c is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && !IsWindowVisible(hwnd))
            {
                ShowWindow(hwnd, SW_SHOWNA);
                shown++;
            }
            return true;
        }, IntPtr.Zero);
        if (shown > 0)
        {
            // Explorer den Arbeitsbereich neu berechnen lassen (maximierte Fenster lassen wieder Platz für die Taskleiste)
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, (IntPtr)SPI_SETWORKAREA, "", SMTO_ABORTIFHUNG, 1000, out _);
        }
        return shown;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private const int SW_SHOWNA = 8;
    private const uint WM_SETTINGCHANGE = 0x001A, SPI_SETWORKAREA = 0x002F, SMTO_ABORTIFHUNG = 0x0002;
    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
