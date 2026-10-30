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
            Set("Bg", "#1C1C1C"); Set("Surface", "#272727"); Set("SurfaceAlt", "#333333"); Set("SurfaceHover", "#3D3D3D");
            Set("Stroke", "#474747"); Set("Text", "#FFFFFF"); Set("TextDim", "#C8C8C8"); Set("Danger", "#FF6B6B"); Set("Ok", "#3DD68C");
        }
        else
        {
            Set("Bg", "#F3F3F3"); Set("Surface", "#FFFFFF"); Set("SurfaceAlt", "#F5F5F5"); Set("SurfaceHover", "#EAEAEA");
            Set("Stroke", "#D6D6D6"); Set("Text", "#141414"); Set("TextDim", "#4A4A4A"); Set("Danger", "#C42B1C"); Set("Ok", "#0F7B0F");
            Set("Accent", "#6A4CE6");
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
