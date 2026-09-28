using System.Diagnostics;
using System.Globalization;
using Dlss5Optimizer.Core.Benchmark;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Models;
using Microsoft.Win32;

namespace Dlss5Optimizer.App.Platform;

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

/// <summary>ReShade als Vulkan-Layer (nur aktueller Nutzer, keine Adminrechte nötig).</summary>
public static class VulkanLayer
{
    private const string Key = @"Software\Khronos\Vulkan\ImplicitLayers";

    public static void Register(string layerJson)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue(layerJson, 0, RegistryValueKind.DWord);
    }

    public static IReadOnlyList<string> RegisteredReShadeLayers()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValueNames().Where(n => n.Contains("ReShade", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
    }

    public static void UnregisterReShade()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (k is null)
            return;
        foreach (var name in k.GetValueNames().Where(n => n.Contains("ReShade", StringComparison.OrdinalIgnoreCase)))
            k.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>Startet Spiele über ihren Launcher, damit DRM und Overlays wie gewohnt laufen.</summary>
public static class GameLauncher
{
    public static void Launch(GameInfo game, string? exe, string? arguments = null)
    {
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
