using System.Diagnostics;
using System.Globalization;
using Dlss5Optimizer.Core.Benchmark;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Models;
using Microsoft.Win32;

namespace Dlss5Optimizer.Platform;

/// <summary>Findet das DLSS-5-Modell des installierten NVIDIA-Treibers.</summary>
public static class NvidiaPaths
{
    /// <summary>
    /// Der Treiber trägt seinen NGX-Ordner unter NGXCore\FullPath ein (DriverStore\…\nv_dispi.inf_amd64_…).
    /// Fällt das aus, wird der ganze Treiberspeicher durchsucht.
    /// </summary>
    public static string? FindDlssNrModel()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\NVIDIA Corporation\Global\NGXCore");
            if (k?.GetValue("FullPath") is string dir)
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir), SystemFileLocator.DlssNrFile);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return SystemFileLocator.FindDlssNrModel();
    }
}

/// <summary>
/// ReShade als Vulkan-Layer – wie das offizielle ReShade-Setup: Dateien in C:\ProgramData\ReShade,
/// Eintrag unter HKLM (32-Bit-Layer im WOW6432Node-Zweig). Aktiv wird er nur in Spielen mit
/// ReShade.ini neben der EXE (so prüft es ReShade beim Laden).
/// </summary>
public static class VulkanLayer
{
    private const string Key = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";

    /// <summary>Letztes Ergebnis von <see cref="Register"/> – für Protokoll und Selbsttest.</summary>
    public static ReShadeLayer.DeployResult? LastDeploy { get; private set; }

    /// <param name="storeManifest">ReShade32/64.json aus dem Komponentenspeicher.</param>
    /// <param name="targetDir">Nur für Tests: anderer Ablageort als C:\ProgramData\ReShade.</param>
    public static void Register(string storeManifest, bool is32Bit) => Register(storeManifest, is32Bit, null);

    public static void Register(string storeManifest, bool is32Bit, string? targetDir)
    {
        var result = ReShadeLayer.Deploy(storeManifest, is32Bit, targetDir ?? ReShadeLayer.DefaultDirectory);
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, is32Bit ? RegistryView.Registry32 : RegistryView.Registry64);
        using var k = hklm.CreateSubKey(Key);
        k.SetValue(result.ManifestPath, 0, RegistryValueKind.DWord);
        // Einträge auf nicht mehr vorhandene ReShade-Layer (alte Setups, gelöschter Speicher) stören nur.
        foreach (var name in k.GetValueNames().Where(n => IsReShade(n) && !File.Exists(n)))
            k.DeleteValue(name, throwOnMissingValue: false);
        LastDeploy = result;
    }

    /// <summary>Entfernt genau einen Eintrag (Selbsttest räumt damit hinter sich auf).</summary>
    public static void Unregister(string manifestPath, bool is32Bit)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, is32Bit ? RegistryView.Registry32 : RegistryView.Registry64);
        using var k = hklm.OpenSubKey(Key, writable: true);
        k?.DeleteValue(manifestPath, throwOnMissingValue: false);
    }

    public static bool IsRegistered(string manifestPath, bool is32Bit)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, is32Bit ? RegistryView.Registry32 : RegistryView.Registry64);
        using var k = hklm.OpenSubKey(Key);
        return k?.GetValue(manifestPath) is int v && v == 0;
    }

    /// <summary>
    /// Weitere, aktive ReShade-Layer derselben Bitness außerhalb von C:\ProgramData\ReShade. Es lädt pro Spiel
    /// nur eine ReShade-Instanz – ist es die fremde (oft ohne Add-ons), lädt der Feeder nicht.
    /// </summary>
    public static IReadOnlyList<string> OtherReShadeLayers(bool is32Bit)
    {
        var ours = ReShadeLayer.DefaultDirectory;
        var result = new List<string>();
        foreach (var (hive, view, _) in Locations())
        {
            if (hive == RegistryHive.LocalMachine && view != (is32Bit ? RegistryView.Registry32 : RegistryView.Registry64))
                continue;
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(Key);
            foreach (var name in k?.GetValueNames() ?? [])
            {
                if (!IsReShade(name) || !File.Exists(name) || k!.GetValue(name) is not int enabled || enabled != 0)
                    continue;
                if (Path.GetDirectoryName(name) is { } dir && Path.GetFullPath(dir).Equals(Path.GetFullPath(ours), StringComparison.OrdinalIgnoreCase))
                    continue;
                // HKCU gilt für beide Bitness-Varianten – nur Layer mit passender DLL zählen.
                var dll = Path.Combine(Path.GetDirectoryName(name)!, Path.GetFileNameWithoutExtension(name) + ".dll");
                if (Dlss5Optimizer.Core.Detection.PeFile.TryRead(dll) is { } pe && pe.Bitness != (is32Bit ? Bitness.X86 : Bitness.X64))
                    continue;
                if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
                    result.Add(name);
            }
        }
        return result;
    }

    /// <summary>Meldet einzelne fremde Layer ab (nach Rückfrage in der Oberfläche).</summary>
    public static void UnregisterEverywhere(IEnumerable<string> manifestPaths)
    {
        var paths = manifestPaths.ToList();
        foreach (var (hive, view, _) in Locations())
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(Key, writable: true);
            foreach (var p in paths)
                k?.DeleteValue(p, throwOnMissingValue: false);
        }
    }

    public static IReadOnlyList<string> RegisteredReShadeLayers()
    {
        var result = new List<string>();
        foreach (var (hive, view, label) in Locations())
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(Key);
            result.AddRange(k?.GetValueNames().Where(IsReShade).Select(n => $"{n} ({label})") ?? []);
        }
        return result;
    }

    public static void UnregisterReShade()
    {
        foreach (var (hive, view, _) in Locations())
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(Key, writable: true);
            if (k is null)
                continue;
            foreach (var name in k.GetValueNames().Where(IsReShade))
                k.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static bool IsReShade(string name) => name.Contains("ReShade", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(RegistryHive, RegistryView, string)> Locations() =>
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, "64 Bit"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "32 Bit"),
        (RegistryHive.CurrentUser, RegistryView.Default, "Nutzer"),
    ];
}

/// <summary>Liest Absturz- und Hänger-Meldungen eines Spiels aus dem Windows-Ereignisprotokoll.</summary>
public static class CrashLog
{
    /// <summary>
    /// Ereignis 1000 (Anwendungsfehler) als <see cref="CrashInfo"/> und alle Meldungen (inkl. 1002,
    /// Anwendung hängt) als lesbare Zeilen, seit einem Zeitpunkt.
    /// </summary>
    public static (IReadOnlyList<CrashInfo> Crashes, IReadOnlyList<string> Lines) Recent(string exeName, DateTime sinceLocal, int max = 5)
    {
        var crashes = new List<CrashInfo>();
        var lines = new List<string>();
        try
        {
            using var log = new System.Diagnostics.EventLog("Application");
            var entries = log.Entries;
            for (int i = entries.Count - 1; i >= 0 && lines.Count < max; i--)
            {
                var e = entries[i];
                if (e.TimeGenerated < sinceLocal)
                    break;
                long id = e.InstanceId & 0xFFFF;
                if (id is not (1000 or 1002))
                    continue;
                var r = e.ReplacementStrings;
                if (r.Length == 0 || !r[0].Equals(exeName, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Ereignis 1000: [3] = fehlerhaftes Modul, [6] = Ausnahmecode (sprachunabhängig).
                if (id == 1000 && r.Length > 6)
                {
                    var code = "0x" + r[6].TrimStart('0', 'x');
                    crashes.Add(new CrashInfo(r[3], code, e.TimeGenerated));
                    lines.Add($"{e.TimeGenerated:dd.MM. HH:mm}: Absturz in {r[3]} (Code {code})");
                }
                else
                {
                    lines.Add($"{e.TimeGenerated:dd.MM. HH:mm}: Spiel reagierte nicht mehr (hängt)");
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or InvalidOperationException or UnauthorizedAccessException)
        {
            lines.Add("Ereignisprotokoll nicht lesbar: " + e.Message);
        }
        return (crashes, lines);
    }

    /// <summary>
    /// d3d8to9 braucht D3DX9 aus der alten DirectX-Laufzeit. 32-Bit-Spiele laden sie aus SysWOW64.
    /// </summary>
    public static bool HasD3dx9Runtime(bool is32Bit)
    {
        var dir = is32Bit && Environment.Is64BitOperatingSystem
            ? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
            : Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(dir, "d3dx9_43.dll"));
    }
}

/// <summary>Startet Spiele über ihren Launcher, damit DRM und Overlays wie gewohnt laufen.</summary>
public static class GameLauncher
{
    /// <param name="loader">Script-Extender-Starter (z. B. nvse_loader.exe) – startet das Spiel direkt.</param>
    public static void Launch(GameInfo game, string? exe, string? arguments = null, string? loader = null)
    {
        if (loader is not null && File.Exists(loader))
        {
            Process.Start(new ProcessStartInfo(loader, arguments ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(loader)! });
            return;
        }
        ProcessStartInfo psi = game.Source switch
        {
            // Steam reicht Startparameter über steam://run/<id>//<args>/ durch (DRM und Overlay bleiben aktiv).
            GameSource.Steam when game.SourceId is { } id =>
                new(arguments is null ? $"steam://rungameid/{id}" : $"steam://run/{id}//{Uri.EscapeDataString(arguments)}/") { UseShellExecute = true },
            GameSource.Epic when game.SourceId is { } app && arguments is null =>
                new($"com.epicgames.launcher://apps/{app}?action=launch&silent=true") { UseShellExecute = true },
            _ when exe is not null =>
                new(exe, arguments ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! },
            _ => throw new InvalidOperationException("Keine startbare EXE bekannt."),
        };
        Process.Start(psi);
    }
}

/// <summary>Misst Bildraten mit PresentMon (ETW; braucht Adminrechte).</summary>
public sealed class PresentMonRunner(string presentMonExe)
{
    public async Task<FrameStats> CaptureAsync(string processName, int seconds, string csvPath, CancellationToken ct)
    {
        if (File.Exists(csvPath))
            File.Delete(csvPath);
        var psi = new ProcessStartInfo(presentMonExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
                 {
                     "--process_name", processName,
                     "--timed", seconds.ToString(CultureInfo.InvariantCulture),
                     "--terminate_after_timed",
                     "--output_file", csvPath,
                     "--no_console_stats",
                     "--stop_existing_session",
                     "--track_frame_type",
                     "--track_pc_latency",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("PresentMon konnte nicht gestartet werden.");
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 45));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }

        if (!File.Exists(csvPath))
            throw new InvalidOperationException($"PresentMon hat keine Daten geliefert. {await stderr}".Trim());
        using var reader = new StreamReader(csvPath);
        var samples = PresentMonCsv.Parse(reader, processName);
        return FrameStats.From(samples)
               ?? throw new InvalidOperationException("Zu wenige Frames gemessen – läuft das Spiel im Vordergrund?");
    }
}
