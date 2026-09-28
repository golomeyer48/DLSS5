#if WINDOWS
using System.Diagnostics;
using System.Security.Principal;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Models;
using Dlss5Optimizer.Platform;

namespace Dlss5Optimizer.SelfTest;

/// <summary>
/// Die Windows-Anbindung gegen ein echtes Windows: Vulkan-Layer in Registry und ProgramData,
/// Absturzmeldungen aus dem Ereignisprotokoll, Module eines laufenden 32-Bit-Prozesses, Systemabfragen.
/// Globale Änderungen (Registry, C:\ProgramData, Ereignisprotokoll) nur auf dem Build-Rechner (CI).
/// </summary>
public static class WindowsCheck
{
    private static readonly bool OnBuildMachine = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
    private static readonly List<(string Store, bool Is32Bit, string? Error)> Registrations = [];
    private static string? _layerDir;

    /// <summary>
    /// Vulkan-Layer wie in der App registrieren – auf dem Build-Rechner echt (C:\ProgramData\ReShade + HKLM),
    /// sonst nur die Dateiablage in einem Temp-Ordner.
    /// </summary>
    public static Action<string, bool> LayerRegistrar(string work) => (json, is32) =>
    {
        try
        {
            if (OnBuildMachine && IsAdmin())
            {
                VulkanLayer.Register(json, is32);
            }
            else
            {
                _layerDir ??= Path.Combine(work, "ProgramData-ReShade");
                ReShadeLayer.Deploy(json, is32, _layerDir);
            }
            Registrations.Add((json, is32, null));
        }
        catch (Exception e)
        {
            Registrations.Add((json, is32, e.Message));
            throw;
        }
    };

    public static void Run(Report report, string work, ComponentStore store)
    {
        report.Section("Windows-Anbindung");
        report.Info("Umgebung", $"{Environment.OSVersion.VersionString}, {(IsAdmin() ? "mit" : "ohne")} Adminrechte{(OnBuildMachine ? ", Build-Rechner" : "")}");
        CheckVulkanLayers(report);
        CheckCrashLog(report, work);
        CheckProcessModules(report);
        CheckSystemQueries(report);
    }

    private static void CheckVulkanLayers(Report report)
    {
        foreach (var bits in new[] { true, false })
        {
            var regs = Registrations.Where(r => r.Is32Bit == bits).ToList();
            var label = bits ? "Vulkan-Layer 32 Bit" : "Vulkan-Layer 64 Bit";
            if (regs.Count == 0)
            {
                report.Info(label, "in keinem Szenario gebraucht");
                continue;
            }
            if (regs.FirstOrDefault(r => r.Error is not null) is { Error: { } error })
            {
                report.Fail(label, error);
                continue;
            }
            var dir = OnBuildMachine && IsAdmin() ? ReShadeLayer.DefaultDirectory : _layerDir!;
            var name = bits ? "ReShade32" : "ReShade64";
            var json = Path.Combine(dir, name + ".json");
            var dll = Dlss5Optimizer.Core.Detection.PeFile.TryRead(Path.Combine(dir, name + ".dll"));
            var problems = new List<string>();
            if (!File.Exists(json))
                problems.Add($"{json} fehlt");
            if (dll is null)
                problems.Add($"{name}.dll fehlt in {dir}");
            else if (dll.Bitness != (bits ? Bitness.X86 : Bitness.X64))
                problems.Add($"{name}.dll hat die falsche Architektur");
            else if (!ReShadeLayer.HasAddonSupport(dll))
                problems.Add($"{name}.dll ohne Add-on-Unterstützung");
            if (OnBuildMachine && IsAdmin())
            {
                if (!VulkanLayer.IsRegistered(json, bits))
                    problems.Add($"nicht in HKLM ({(bits ? "WOW6432Node" : "64 Bit")}) eingetragen");
                if (VulkanLayer.OtherReShadeLayers(bits) is { Count: > 0 } others)
                    problems.Add("zusätzliche ReShade-Layer: " + string.Join(", ", others));
                VulkanLayer.Unregister(json, bits); // Build-Rechner aufräumen
            }
            report.Check(problems.Count == 0, label, string.Join("; ", problems),
                $"{dir}\\{name}.json{(OnBuildMachine ? ", in der Registry eingetragen" : " (nur Dateiablage geprüft)")}{(VulkanLayer.LastDeploy is { } d && OnBuildMachine ? " – " + d.Describe() : "")}");
        }
    }

    /// <summary>Ein Absturz in d3d9.dll muss als Übersetzer-Absturz erkannt werden und zum Wechsel auf dgVoodoo führen.</summary>
    private static void CheckCrashLog(Report report, string work)
    {
        if (!OnBuildMachine || !IsAdmin())
        {
            report.Info("Absturzprotokoll", "übersprungen (schreibt ins Ereignisprotokoll – nur auf dem Build-Rechner)");
            return;
        }
        try
        {
            const string exe = "dlss5-selftest-game.exe";
            var since = DateTime.Now.AddMinutes(-1);
            // Aufbau wie Ereignis 1000 „Anwendungsfehler“: [0] App, [3] Modul, [6] Ausnahmecode.
            EventLog.WriteEvent("Application Error", new EventInstance(1000, 100, EventLogEntryType.Error),
                exe, "1.0.0.0", "00000000", "d3d9.dll", "2.7.1.0", "00000000", "c0000005", "0000000000012345", "0x1a2b", "01dc0000", @"C:\Games\dlss5-selftest-game.exe", @"C:\Games\d3d9.dll", Guid.NewGuid().ToString(), "", "");

            IReadOnlyList<CrashInfo> crashes = [];
            for (int i = 0; i < 10 && crashes.Count == 0; i++)
            {
                crashes = CrashLog.Recent(exe, since).Crashes;
                if (crashes.Count == 0)
                    Thread.Sleep(500);
            }
            if (!report.Check(crashes.Count == 1 && crashes[0].Module == "d3d9.dll" && crashes[0].Code == "0xc0000005",
                    "Absturzprotokoll lesen", $"erwartet 1 Absturz in d3d9.dll (0xc0000005), gelesen: {string.Join(", ", crashes.Select(c => $"{c.Module} {c.Code}"))}",
                    "Ereignis 1000 erkannt: d3d9.dll, 0xc0000005"))
                return;

            var gameDir = Path.Combine(work, "crash-game");
            Directory.CreateDirectory(gameDir);
            var manifest = new InstallManifest(1, DateTimeOffset.Now.AddMinutes(-5),
                new Configuration(RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9, SrMode.Native, 1, NrPlacement.PostUpscale, FrameGenMode.Off), [], [], [], []);
            var verdict = InstallDiagnostics.Evaluate(gameDir, manifest, exe, crashes);
            report.Check(verdict.Verdict == DiagnosticVerdict.TranslatorFailed && verdict.SwitchTo == RouteId.LegacyFeeder,
                "Übersetzer-Wechsel nach Absturz", $"Diagnose: {verdict.Verdict}, Vorschlag {verdict.SwitchTo}", "DXVK abgestürzt → Vorschlag dgVoodoo2");
        }
        catch (Exception e)
        {
            report.Fail("Absturzprotokoll", $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Der Testlauf erkennt die API an den geladenen Modulen – auch in 32-Bit-Spielen.</summary>
    private static void CheckProcessModules(Report report)
    {
        var wowPing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "PING.EXE");
        if (!File.Exists(wowPing))
        {
            report.Warn("Module eines 32-Bit-Prozesses", "kein 32-Bit-ping.exe gefunden");
            return;
        }
        using var p = Process.Start(new ProcessStartInfo(wowPing, "-n 8 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        try
        {
            IReadOnlyList<string> modules = [];
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                modules = ProcessProbe.LoadedModules(p!.Id);
                if (modules.Any(m => Path.GetFileName(m).Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase)))
                    break;
            }
            bool has32 = modules.Any(m => Path.GetFileName(m).Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase));
            report.Check(has32, "Module eines 32-Bit-Prozesses",
                $"nur {modules.Count} Module, keine 32-Bit-DLLs: {string.Join(", ", modules.Select(Path.GetFileName).Take(8))}",
                $"{modules.Count} Module gelesen, z. B. {string.Join(", ", modules.Select(Path.GetFileName).Where(n => n is not null).Take(4))}");
        }
        catch (Exception e)
        {
            report.Fail("Module eines 32-Bit-Prozesses", $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try
            {
                p?.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static void CheckSystemQueries(Report report)
    {
        Try("Grafikkarte und Treiber", () =>
        {
            var sys = WindowsSystem.Read();
            return $"{(sys.Gpu.Name is { Length: > 0 } n ? n : "keine")}, Treiber {sys.Gpu.DriverVersion?.ToString() ?? "–"}, {sys.Display.Width}×{sys.Display.Height} @ {sys.Display.RefreshHz} Hz, HAGS {sys.HardwareSchedulingEnabled?.ToString() ?? "unbekannt"}";
        });
        Try("DLSS-5-Modell im Treiber", () => NvidiaPaths.FindDlssNrModel() ?? "nicht vorhanden (wird stattdessen geladen)");
        Try("DirectX-Laufzeit (D3DX9) für d3d8to9", () => CrashLog.HasD3dx9Runtime(is32Bit: true) ? "vorhanden" : "fehlt – die App warnt dann vor der Installation");
        Try("Spielbibliotheken lesen", () =>
        {
            var steam = WindowsSystem.SteamRoot();
            int gog = new GogLibraryScanner().Scan().Count();
            int xbox = WindowsSystem.XboxFolders().Count();
            return $"Steam {(steam is null ? "nicht installiert" : steam)}, GOG {gog} Spiele, Xbox-Ordner {xbox}";
        });
        Try("Weitere Systemabfragen", () =>
            $"Fenstermodus-Optimierung {WindowsSystem.ReadWindowedOptimizations()?.ToString() ?? "–"}, Spielmodus {WindowsSystem.ReadGameMode()?.ToString() ?? "–"}, ReShade-Layer registriert: {VulkanLayer.RegisteredReShadeLayers().Count}");

        void Try(string title, Func<string> query)
        {
            try
            {
                report.Ok(title, query());
            }
            catch (Exception e)
            {
                report.Fail(title, $"{e.GetType().Name}: {e.Message}");
            }
        }
    }

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
#endif
