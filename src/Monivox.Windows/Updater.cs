using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Monivox.Windows;

/// <summary>
/// Automatische Updates über GitHub Releases.
/// Jeder Push auf main erzeugt per CI ein Release (Tag vX.Y.Z) mit
///   Monivox-win-x64.zip  und  Monivox-win-x64.zip.sha256
/// Der Updater lädt das ZIP, prüft die SHA-256-Prüfsumme und tauscht die Programmdateien aus.
/// Laufende .exe/.dll-Dateien kann Windows nicht überschreiben, wohl aber umbenennen – daher
/// werden alte Dateien zu *.old umbenannt und beim nächsten Start gelöscht.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Updater(Action<string> log)
{
    public const string Owner = "Theed337";
    public const string Repository = "MONIVOX";
    public const string AssetName = "Monivox-win-x64.zip";

    public sealed record ReleaseInfo(Version Version, string Tag, Uri ZipUrl, Uri ShaUrl);

    public static string InstallDirectory => AppContext.BaseDirectory;

    public static Version CurrentVersion
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? "0.0.0";
            return ParseVersion(info) ?? new Version(0, 0, 0);
        }
    }

    public static Version? ParseVersion(string text)
    {
        var core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out var v) ? v : null;
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MONIVOX", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Fragt das neueste Release ab. Gibt null zurück, wenn keins erreichbar oder nicht neuer ist.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken cancel = default)
    {
        using var http = CreateClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest", cancel);
        if (!response.IsSuccessStatusCode)
        {
            log(response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "Update-Prüfung: kein öffentliches Release gefunden (Repository privat oder noch kein Release)."
                : $"Update-Prüfung fehlgeschlagen: HTTP {(int)response.StatusCode}");
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version is null || version <= CurrentVersion) return null;

        Uri? zip = null, sha = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (name == AssetName) zip = url;
            else if (name == AssetName + ".sha256") sha = url;
        }
        if (zip is null || sha is null)
        {
            log($"Release {tag} enthält kein vollständiges Paket – übersprungen.");
            return null;
        }
        return new ReleaseInfo(version, tag, zip, sha);
    }

    /// <summary>Lädt, prüft und installiert ein Release. Gibt true zurück, wenn der Treiber neu installiert werden muss.</summary>
    public async Task<bool> InstallAsync(ReleaseInfo release, CancellationToken cancel = default)
    {
        using var http = CreateClient();
        string temp = Path.Combine(Path.GetTempPath(), "monivox-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string zipPath = Path.Combine(temp, AssetName);
            log($"Lade MONIVOX {release.Tag} …");
            await using (var fs = File.Create(zipPath))
            {
                await using var stream = await http.GetStreamAsync(release.ZipUrl, cancel);
                await stream.CopyToAsync(fs, cancel);
            }

            string expected = (await http.GetStringAsync(release.ShaUrl, cancel)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
            string actual;
            await using (var fs = File.OpenRead(zipPath))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, cancel)).ToLowerInvariant();
            }
            if (expected.Length != 64 || actual != expected)
                throw new InvalidOperationException("Prüfsumme des Updates stimmt nicht – Update abgebrochen.");

            string staging = Path.Combine(temp, "neu");
            ZipFile.ExtractToDirectory(zipPath, staging);

            // Sicherheitsnetz: Pfade dürfen nicht aus dem Installationsordner herausführen.
            string installRoot = Path.GetFullPath(InstallDirectory);
            bool driverChanged = DriverPackageChanged(staging, installRoot);

            foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(staging, source);
                string target = Path.GetFullPath(Path.Combine(installRoot, relative));
                if (!target.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Ungültiger Pfad im Update: {relative}");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    string old = target + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(target, old);
                }
                File.Move(source, target);
            }
            log($"MONIVOX {release.Tag} installiert.");
            return driverChanged;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool DriverPackageChanged(string staging, string installRoot)
    {
        string newCat = Path.Combine(staging, "driver-package", "MonivoxIdd.cat");
        string oldCat = Path.Combine(installRoot, "driver-package", "MonivoxIdd.cat");
        if (!File.Exists(newCat)) return false;
        if (!File.Exists(oldCat)) return true;
        return !File.ReadAllBytes(newCat).AsSpan().SequenceEqual(File.ReadAllBytes(oldCat));
    }

    /// <summary>Entfernt *.old-Dateien früherer Updates.</summary>
    public static void CleanupOldFiles()
    {
        foreach (var file in Directory.EnumerateFiles(InstallDirectory, "*.old", SearchOption.AllDirectories))
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Installiert den Treiber aus dem (aktualisierten) Paket neu. Benötigt Adminrechte.</summary>
    public static int ReinstallDriver()
    {
        string script = Path.Combine(InstallDirectory, "scripts", "install-driver.ps1");
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
