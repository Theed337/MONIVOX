using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Monivox.App;

/// <summary>Hell/Dunkel nach Windows-Einstellung, passende Titelleiste und abgerundete Ecken (Windows 11).</summary>
public static class Theme
{
    public static bool IsDark
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
    }

    public static void Apply(ResourceDictionary res)
    {
        bool dark = IsDark;
        void Set(string key, string hex) => res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        if (dark)
        {
            Set("Bg", "#202020"); Set("Surface", "#2B2B2B"); Set("SurfaceAlt", "#363636"); Set("Stroke", "#404040");
            Set("Text", "#F3F3F3"); Set("TextDim", "#A8A8A8");
        }
        else
        {
            Set("Bg", "#F3F3F3"); Set("Surface", "#FFFFFF"); Set("SurfaceAlt", "#F0F0F0"); Set("Stroke", "#E0E0E0");
            Set("Text", "#1B1B1B"); Set("TextDim", "#5F5F5F");
        }
    }

    public static void ApplyWindowChrome(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
