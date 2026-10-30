using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Monivox.Core;

namespace Monivox.Windows;

/// <summary>Steuert den eigenen Monivox-IDD (Backend A).</summary>
[SupportedOSPlatform("windows")]
public static class DriverControl
{
    /// <summary>Geräteinstanz, die MonivoxDevice.exe anlegt.</summary>
    public const string DeviceInstanceId = @"SWD\Monivox\MonivoxIdd";

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static IReadOnlyList<DisplayMode> ReadConfiguredModes()
    {
        using var key = Registry.LocalMachine.OpenSubKey(DriverConfig.RegistryKey);
        return key?.GetValue(DriverConfig.RegistryValue) is string s ? DriverConfig.Parse(s) : Array.Empty<DisplayMode>();
    }

    /// <summary>Schreibt die gewünschten Modi und startet das Gerät neu, falls sie sich geändert haben.</summary>
    /// <returns>true, wenn ein Neustart des Geräts ausgelöst wurde.</returns>
    public static bool Configure(IReadOnlyList<DisplayMode> modes)
    {
        if (ReadConfiguredModes().SequenceEqual(modes)) return false;

        using (var key = Registry.LocalMachine.CreateSubKey(DriverConfig.RegistryKey))
        {
            key.SetValue(DriverConfig.RegistryValue, DriverConfig.Format(modes), RegistryValueKind.String);
        }
        RestartDevice();
        return true;
    }

    /// <summary>Startet das Treibergerät neu (pnputil, ab Windows 10 2004).</summary>
    public static void RestartDevice()
    {
        var psi = new ProcessStartInfo("pnputil.exe", $"/restart-device \"{DeviceInstanceId}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("pnputil konnte nicht gestartet werden.");
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Treibergerät konnte nicht neu gestartet werden (pnputil {p.ExitCode}): {output.Trim()}");
    }
}
