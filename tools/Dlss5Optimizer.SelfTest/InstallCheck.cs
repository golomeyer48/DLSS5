using System.Text.Json;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Models;
using Dlss5Optimizer.Core.Tests;
using Ids = Dlss5Optimizer.Core.Decision.RouteCatalog.Ids;

namespace Dlss5Optimizer.SelfTest;

/// <summary>
/// Installiert jede Route in einen nachgebauten Spielordner, prüft das Ergebnis und nimmt alles
/// wieder zurück. Der Ordner muss danach Byte für Byte dem Ausgangszustand entsprechen.
/// </summary>
public static class InstallCheck
{
    private sealed record Scenario(
        string Name,
        GameInfo Game,
        Action<string> Build,
        RouteId Route,
        GraphicsApi Api,
        string[] MustExist,
        SrMode Sr = SrMode.Native,
        NrPlacement Placement = NrPlacement.PostUpscale,
        bool WithDeepFriedChicken = false,
        bool Reinstall = false);

    /// <summary>Registrierte Vulkan-Layer (JSON, 32 Bit?), damit der Aufrufer sie prüfen und entfernen kann.</summary>
    public static List<(string Json, bool Is32Bit)> RegisteredLayers { get; } = [];

    public static void Run(Report report, ComponentCatalog catalog, ComponentStore store, string gamesDir, Action<string, bool>? registerLayer = null)
    {
        report.Section("Installieren und Rückgängig in nachgebauten Spielen");
        Placeholders.EnsureDriverModel(store);

        var docs = Path.Combine(gamesDir, "_documents");
        Directory.CreateDirectory(docs);
        var installer = new Installer(
            (json, is32) =>
            {
                RegisteredLayers.Add((json, is32));
                registerLayer?.Invoke(json, is32);
            },
            token => token.Equals("%DOCUMENTS%", StringComparison.OrdinalIgnoreCase) ? docs : null);
        var availability = new ComponentAvailability(catalog, store, () => null, () => null);
        var planner = new RoutePlanner(availability, store);
        var analyzer = new GameAnalyzer(GameDatabase.LoadEmbedded());

        string G(string name) => Path.Combine(gamesDir, name);
        string[] feed32 = ["d3d9.dll", "dlss5-feed.addon32", "dlss5-feed.cfg", "ReShade.ini", "ReShadePreset.ini",
            "reshade-shaders/Shaders/DLSS5_Feed.fx", "reshade-shaders/Shaders/ReShade.fxh", "reshade-shaders/Shaders/lumenite_Kernel.fx",
            "host64/dlss5-feed-host64.exe", "host64/dxgi.dll", "host64/ReShade.ini", "host64/renodx-dlss5.addon64", "host64/nvngx_dlssnr.dll", "host64/nvngx_dlss.dll"];

        var scenarios = new List<Scenario>
        {
            new("Fallout: New Vegas (DX9, 32 Bit, ENB vorhanden) → DXVK",
                new GameInfo("Fallout: New Vegas", G("Fallout New Vegas"), GameSource.Steam, "22380"),
                dir =>
                {
                    TestPe.Write(Path.Combine(dir, "FalloutNV.exe"), TestPe.I386, ["kernel32.dll", "d3d9.dll"], largeAddressAware: true);
                    TestPe.Write(Path.Combine(dir, "d3d9.dll"), TestPe.I386, ["kernel32.dll"]); // ENB
                    File.WriteAllText(Path.Combine(dir, "enbseries.ini"), "[GLOBAL]\r\nUsePatchSpeedhackWithoutGraphics=false\r\n");
                    File.WriteAllText(Path.Combine(dir, "ReShade.ini"), "[GENERAL]\r\nPreprocessorDefinitions=MY_OWN_DEFINE=1\r\n[INPUT]\r\nKeyOverlay=36,0,0,0\r\n");
                    var prefs = Path.Combine(docs, "My Games", "FalloutNV", "FalloutPrefs.ini");
                    Directory.CreateDirectory(Path.GetDirectoryName(prefs)!);
                    File.WriteAllText(prefs, "[Display]\r\niMultiSample=4\r\niSize W=2560\r\n");
                },
                RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9, feed32, Reinstall: true),

            new("Dark Messiah (DX9, 32 Bit) → DXVK auch in bin\\",
                new GameInfo("Dark Messiah of Might and Magic", G("Dark Messiah"), GameSource.Steam, "2100"),
                dir =>
                {
                    TestPe.Write(Path.Combine(dir, "mm.exe"), TestPe.I386, ["kernel32.dll"], largeAddressAware: true);
                    TestPe.Write(Path.Combine(dir, "bin", "shaderapidx9.dll"), TestPe.I386, ["d3d9.dll"]);
                },
                RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9, [.. feed32, "bin/d3d9.dll"]),

            new("DX8-Spiel (32 Bit) → d3d8to9 + DXVK",
                new GameInfo("Altes DX8-Spiel", G("Old DX8"), GameSource.Manual),
                dir => TestPe.Write(Path.Combine(dir, "game.exe"), TestPe.I386, ["kernel32.dll", "d3d8.dll"], largeAddressAware: true),
                RouteId.LegacyD3D8Dxvk, GraphicsApi.D3D8, ["d3d8.dll", .. feed32]),

            new("DX9-Spiel (32 Bit) → dgVoodoo2",
                new GameInfo("Altes DX9-Spiel", G("Old DX9"), GameSource.Manual),
                dir => TestPe.Write(Path.Combine(dir, "game.exe"), TestPe.I386, ["kernel32.dll", "d3d9.dll"], largeAddressAware: true),
                RouteId.LegacyFeeder, GraphicsApi.D3D9, ["D3D9.dll", "dgVoodoo.conf", "dxgi.dll", "dlss5-feed.addon32", "host64/dlss5-feed-host64.exe", "host64/renodx-dlss5.addon64"]),

            new("DirectDraw-Spiel (32 Bit) → dgVoodoo2",
                new GameInfo("Altes DirectDraw-Spiel", G("Old DDraw"), GameSource.Manual),
                dir => TestPe.Write(Path.Combine(dir, "game.exe"), TestPe.I386, ["kernel32.dll", "ddraw.dll"], largeAddressAware: true),
                RouteId.LegacyFeeder, GraphicsApi.DirectDraw, ["DDraw.dll", "D3DImm.dll", "dgVoodoo.conf", "dxgi.dll"]),

            new("Deus Ex: Human Revolution (DX11, 32 Bit) → Feeder + Hilfsprozess",
                new GameInfo("Deus Ex: Human Revolution", G("Deus Ex HR"), GameSource.Steam, "28050"),
                dir => TestPe.Write(Path.Combine(dir, "dxhr.exe"), TestPe.I386, ["kernel32.dll", "d3d11.dll", "dxgi.dll"], largeAddressAware: true),
                RouteId.Feeder, GraphicsApi.D3D11, ["dxgi.dll", "dlss5-feed.addon32", "host64/dlss5-feed-host64.exe", "host64/renodx-dlss5.addon64"]),

            new("DOOM (2016) (Vulkan, 64 Bit) → Feeder über den Vulkan-Layer",
                new GameInfo("DOOM", G("DOOM"), GameSource.Steam, "379720"),
                dir =>
                {
                    TestPe.Write(Path.Combine(dir, "DOOMx64vk.exe"), TestPe.Amd64, ["kernel32.dll", "vulkan-1.dll"]);
                    TestPe.Write(Path.Combine(dir, "DOOMx64.exe"), TestPe.Amd64, ["kernel32.dll", "opengl32.dll"]);
                },
                RouteId.Feeder, GraphicsApi.Vulkan, ["dlss5-feed.addon64", "ReShade.ini", "renodx-dlss5.addon64", "nvngx_dlssnr.dll", "nvngx_dlss.dll"]),

            new("OpenGL-Spiel (64 Bit) → Feeder als opengl32.dll",
                new GameInfo("OpenGL-Spiel", G("OpenGL"), GameSource.Manual),
                dir => TestPe.Write(Path.Combine(dir, "game.exe"), TestPe.Amd64, ["kernel32.dll", "opengl32.dll"]),
                RouteId.Feeder, GraphicsApi.OpenGL, ["opengl32.dll", "dlss5-feed.addon64", "renodx-dlss5.addon64"]),

            new("DX12 mit DLSS → OptiScaler DLSSNR",
                new GameInfo("DX12-Spiel mit DLSS", G("DX12 DLSS"), GameSource.Manual),
                dir =>
                {
                    TestPe.Write(Path.Combine(dir, "game.exe"), TestPe.Amd64, ["kernel32.dll", "d3d12.dll", "dxgi.dll"]);
                    TestPe.Write(Path.Combine(dir, "nvngx_dlss.dll"), TestPe.Amd64, ["kernel32.dll"], version: new Version(310, 4, 0, 0));
                },
                RouteId.OptiScalerNr, GraphicsApi.D3D12, ["dxgi.dll", "OptiScaler.ini", "nvngx_dlssnr.dll"], SrMode.Quality),

            new("DX12 mit DLSS → OptiScaler vor dem Hochskalieren (PreSR)",
                new GameInfo("DX12-Spiel PreSR", G("DX12 PreSR"), GameSource.Manual),
                dir =>
                {
                    TestPe.Write(Path.Combine(dir, "game.exe"), TestPe.Amd64, ["kernel32.dll", "d3d12.dll", "dxgi.dll"]);
                    TestPe.Write(Path.Combine(dir, "nvngx_dlss.dll"), TestPe.Amd64, ["kernel32.dll"], version: new Version(310, 4, 0, 0));
                },
                RouteId.OptiScalerNr, GraphicsApi.D3D12, ["dxgi.dll", "OptiScaler.ini"], SrMode.Performance, NrPlacement.PreUpscale),

            new("DX11 mit DLSS → ReShade + dlss5-bridge + RenoDX",
                new GameInfo("DX11-Spiel mit DLSS", G("DX11 DLSS"), GameSource.Manual),
                dir =>
                {
                    TestPe.Write(Path.Combine(dir, "game.exe"), TestPe.Amd64, ["kernel32.dll", "d3d11.dll", "dxgi.dll"]);
                    TestPe.Write(Path.Combine(dir, "nvngx_dlss.dll"), TestPe.Amd64, ["kernel32.dll"], version: new Version(310, 4, 0, 0));
                },
                RouteId.BridgeD3D11, GraphicsApi.D3D11, ["dxgi.dll", "renodx-dlss5.addon64", "nvngx_dlssnr.dll"], SrMode.Quality),

            new("Vulkan mit DLSS → Vulkan-Layer + dlss5-bridge",
                new GameInfo("Vulkan-Spiel mit DLSS", G("Vulkan DLSS"), GameSource.Manual),
                dir =>
                {
                    TestPe.Write(Path.Combine(dir, "game.exe"), TestPe.Amd64, ["kernel32.dll", "vulkan-1.dll"]);
                    TestPe.Write(Path.Combine(dir, "nvngx_dlss.dll"), TestPe.Amd64, ["kernel32.dll"], version: new Version(310, 4, 0, 0));
                },
                RouteId.BridgeVulkan, GraphicsApi.Vulkan, ["dlss5-bridge.addon64", "dlss5-bridge.cfg", "renodx-dlss5.addon64", "ReShade.ini"], SrMode.Quality),

            new("DX12 mit DLSS → ReShade + Deep Fried Chicken (Platzhalter)",
                new GameInfo("DX12-Spiel DFC", G("DX12 DFC"), GameSource.Manual),
                dir =>
                {
                    TestPe.Write(Path.Combine(dir, "game.exe"), TestPe.Amd64, ["kernel32.dll", "d3d12.dll", "dxgi.dll"]);
                    TestPe.Write(Path.Combine(dir, "nvngx_dlss.dll"), TestPe.Amd64, ["kernel32.dll"], version: new Version(310, 4, 0, 0));
                },
                RouteId.ReShadeNrAddon, GraphicsApi.D3D12, ["dxgi.dll", "deep-fried-chicken.addon64", "deep-fried-chicken-nvngx.dll"], SrMode.Quality, WithDeepFriedChicken: true),

            new("Deus Ex: HR mit Deep Fried Chicken (Platzhalter) im Hilfsprozess",
                new GameInfo("Deus Ex: Human Revolution", G("Deus Ex HR DFC"), GameSource.Steam, "28050"),
                dir => TestPe.Write(Path.Combine(dir, "dxhr.exe"), TestPe.I386, ["kernel32.dll", "d3d11.dll", "dxgi.dll"], largeAddressAware: true),
                RouteId.Feeder, GraphicsApi.D3D11, ["dxgi.dll", "host64/deep-fried-chicken.addon64", "host64/deep-fried-chicken-nvngx.dll"], WithDeepFriedChicken: true),
        };

        foreach (var s in scenarios)
        {
            if (s.WithDeepFriedChicken)
                Placeholders.EnsureDeepFriedChicken(store);
            else
                store.Remove(Ids.DeepFriedChicken);
            try
            {
                RunScenario(report, s, analyzer, planner, installer, docs);
            }
            catch (Exception e)
            {
                report.Fail(s.Name, $"Abbruch: {e.GetType().Name}: {e.Message}");
            }
        }
        store.Remove(Ids.DeepFriedChicken);
    }

    private static void RunScenario(Report report, Scenario s, GameAnalyzer analyzer, RoutePlanner planner, Installer installer, string docs)
    {
        var root = s.Game.InstallDir;
        Directory.CreateDirectory(root);
        s.Build(root);
        var analysis = analyzer.Analyze(s.Game);
        var gameDir = analysis.GameDir ?? throw new InvalidOperationException("keine Spiel-EXE erkannt");
        var exeName = Path.GetFileName(analysis.MainExe!);

        var before = Snapshot(root);
        var docsBefore = Snapshot(docs);

        var route = RouteCatalog.Get(s.Route);
        var config = new Configuration(s.Route, s.Api, s.Sr, 1.0, s.Placement, FrameGenMode.Off);
        var candidate = new Candidate(route, config, new Prediction(0, 0, 0, 0, true), [], [], []);

        InstallPlan plan;
        try
        {
            plan = planner.Plan(analysis, candidate);
        }
        catch (PlanException e)
        {
            report.Fail(s.Name, "Planung: " + e.Message);
            return;
        }

        var problems = new List<string>();
        int layersBefore = RegisteredLayers.Count;
        installer.Install(plan);
        var manifest = installer.ReadManifest(gameDir) ?? throw new InvalidOperationException("kein Manifest nach der Installation");

        problems.AddRange(installer.Verify(gameDir).Select(p => "Prüfsumme: " + p));
        problems.AddRange(s.MustExist.Where(f => !File.Exists(Path.Combine(gameDir, f.Replace('/', Path.DirectorySeparatorChar)))).Select(f => $"{f} fehlt"));
        problems.AddRange(CheckIniValues(plan, gameDir, docs));
        problems.AddRange(CheckArchitectures(manifest, gameDir, analysis.Bitness));
        problems.AddRange(CheckLayers(plan, RegisteredLayers.Skip(layersBefore).ToList()));
        problems.AddRange(CheckRouteDetails(s, plan, gameDir, analysis));

        // Diagnose direkt nach der Installation darf nicht abstürzen und noch nichts als Fehler melden.
        var checks = InstallDiagnostics.Check(gameDir, manifest, exeName);
        problems.AddRange(checks.Where(c => c.Status == DiagnosticStatus.Failed).Select(c => $"Diagnose meldet {c.Title}: {c.Detail}"));

        // Laufzeitdateien, wie sie die Mods dieser Route anlegen – „Rückgängig“ muss sie wegräumen.
        foreach (var f in RuntimeFiles(s, exeName, analysis.Bitness == Bitness.X86))
            File.WriteAllText(Path.Combine(gameDir, f.Replace('/', Path.DirectorySeparatorChar)), "laufzeit");

        if (s.Reinstall)
        {
            installer.Install(plan); // „Reparieren“ über eine bestehende Installation
            problems.AddRange(installer.Verify(gameDir).Select(p => "nach Reparatur: " + p));
        }

        installer.Uninstall(gameDir);
        problems.AddRange(Diff(before, Snapshot(root), "Spielordner"));
        problems.AddRange(Diff(docsBefore, Snapshot(docs), "Eigene Dateien"));

        int copies = plan.Steps.OfType<CopyFileStep>().Count(), inis = plan.Steps.OfType<IniSetStep>().Count();
        if (problems.Count == 0)
            report.Ok(s.Name, $"{copies} Dateien, {inis} INI-Werte{(plan.Steps.OfType<RegisterVulkanLayerStep>().Any() ? ", Vulkan-Layer" : "")}; Rückgängig stellt den Ursprungszustand exakt her");
        else
            report.Fail(s.Name, string.Join("; ", problems.Distinct().Take(12)));
    }

    private static IEnumerable<string> RuntimeFiles(Scenario s, string exeName, bool is32)
    {
        var stem = Path.GetFileNameWithoutExtension(exeName);
        if (s.Route == RouteId.OptiScalerNr)
        {
            yield return "OptiScaler.log";
            yield break;
        }
        yield return "ReShade.log";
        if (s.Route.UsesFeeder())
        {
            yield return "dlss5-feed.log";
            if (is32)
            {
                yield return "host64/dlss5-feed-host.log";
                yield return "host64/ReShade.log";
            }
        }
        if (s.Route.UsesDxvk())
        {
            yield return stem + "_d3d9.log";
            yield return stem + ".dxvk-cache";
        }
    }

    /// <summary>Jeder geplante INI-Wert muss nach der Installation genau so in der Datei stehen.</summary>
    private static IEnumerable<string> CheckIniValues(InstallPlan plan, string gameDir, string docs)
    {
        // Spätere Schritte überschreiben frühere – geprüft wird der letzte Wert je Schlüssel.
        var last = new Dictionary<(string, string, string), IniSetStep>();
        foreach (var step in plan.Steps.OfType<IniSetStep>())
            last[(step.Target.ToLowerInvariant(), step.Section.ToLowerInvariant(), step.Key.ToLowerInvariant())] = step;
        foreach (var step in last.Values)
        {
            var path = step.Target.StartsWith("%DOCUMENTS%", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(docs, step.Target["%DOCUMENTS%".Length..].TrimStart('\\', '/').Replace('\\', Path.DirectorySeparatorChar))
                : Path.Combine(gameDir, step.Target.Replace('\\', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                if (!step.OnlyIfExists)
                    yield return $"{step.Target} fehlt";
                continue;
            }
            var actual = IniFile.Load(path).Get(step.Section, step.Key);
            if (!string.Equals(actual ?? "", step.Value, StringComparison.Ordinal))
                yield return $"{step.Target} [{step.Section}] {step.Key} = „{actual}“ statt „{step.Value}“";
        }
    }

    /// <summary>32-Bit-Spiele laden nur 32-Bit-DLLs; alles in host64\ und jedes .addon64 muss 64 Bit sein.</summary>
    private static IEnumerable<string> CheckArchitectures(InstallManifest manifest, string gameDir, Bitness gameBits)
    {
        foreach (var entry in manifest.Files.Where(f => f.InstalledSha256 is not null))
        {
            var rel = entry.RelativePath.Replace('\\', '/');
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ext is not (".dll" or ".exe" or ".addon32" or ".addon64"))
                continue;
            var expected = rel.StartsWith("host64/", StringComparison.OrdinalIgnoreCase) || ext == ".addon64" ? Bitness.X64
                : ext == ".addon32" ? Bitness.X86
                : gameBits;
            var pe = PeFile.TryRead(Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (pe is null)
                yield return $"{rel} ist keine gültige DLL/EXE";
            else if (pe.Bitness != expected)
                yield return $"{rel} ist {ComponentCheck.Bits(pe.Bitness)}, gebraucht wird {ComponentCheck.Bits(expected)}";
        }
    }

    /// <summary>Layer-JSON und DLL müssen zur Bitness passen, sonst lädt Vulkan ReShade nicht.</summary>
    private static IEnumerable<string> CheckLayers(InstallPlan plan, List<(string Json, bool Is32Bit)> registered)
    {
        foreach (var step in plan.Steps.OfType<RegisterVulkanLayerStep>())
        {
            if (!registered.Any(r => r.Json == step.LayerJson && r.Is32Bit == step.Is32Bit))
            {
                yield return "Vulkan-Layer wurde nicht registriert";
                continue;
            }
            string? lib = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(step.LayerJson));
                lib = doc.RootElement.GetProperty("layer").GetProperty("library_path").GetString();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }
            if (lib is null)
            {
                yield return $"{Path.GetFileName(step.LayerJson)} ist kein Layer-Manifest";
                continue;
            }
            var dll = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(step.LayerJson)!, lib.Replace('\\', Path.DirectorySeparatorChar)));
            var pe = PeFile.TryRead(dll);
            var want = step.Is32Bit ? Bitness.X86 : Bitness.X64;
            if (pe is null)
                yield return $"Layer-DLL {lib} fehlt";
            else if (pe.Bitness != want)
                yield return $"Layer-DLL {lib} ist {ComponentCheck.Bits(pe.Bitness)} statt {ComponentCheck.Bits(want)}";
        }
    }

    /// <summary>Stellen, an denen es in echten Aufbauten schon gehakt hat.</summary>
    private static IEnumerable<string> CheckRouteDetails(Scenario s, InstallPlan plan, string gameDir, GameAnalysis analysis)
    {
        var reshadeIni = IniFile.Load(Path.Combine(gameDir, "ReShade.ini"));
        bool is32 = analysis.Bitness == Bitness.X86;
        if (s.Route.UsesFeeder())
        {
            var cfg = IniFile.Load(Path.Combine(gameDir, "dlss5-feed.cfg"));
            if (cfg.Get("", "mode") != "2")
                yield return "dlss5-feed.cfg: mode ist nicht 2 (nur Transporttest)";
            var preset = IniFile.Load(Path.Combine(gameDir, "ReShadePreset.ini")).Get("", "Techniques") ?? "";
            int lumenite = preset.IndexOf("Lumenite_Kernel", StringComparison.Ordinal), feed = preset.IndexOf("DLSS5_Feed", StringComparison.Ordinal);
            if (lumenite < 0 || feed < 0 || lumenite > feed)
                yield return $"ReShadePreset.ini: Lumenite_Kernel muss vor DLSS5_Feed stehen („{preset}“)";
            if (is32 && plan.Steps.OfType<RegisterVulkanLayerStep>().Any() && !string.IsNullOrEmpty(reshadeIni.Get("ADDON", "LoadFromDllMain")))
                yield return "ReShade.ini: LoadFromDllMain muss über den 32-Bit-Vulkan-Layer leer sein";
            if (is32 && !s.WithDeepFriedChicken)
            {
                var host = IniFile.Load(Path.Combine(gameDir, "host64", "ReShade.ini"));
                if (host.Get("RenoDX.DLSS5", "NRStyle") != "0")
                    yield return "host64\\ReShade.ini: NRStyle muss 0 sein (sonst schwarzes Bild)";
                if (host.Get("ADDON", "LoadFromDllMain") != "renodx-dlss5.addon64")
                    yield return "host64\\ReShade.ini: RenoDX wird nicht früh geladen";
            }
        }
        if (s.Name.StartsWith("Fallout", StringComparison.Ordinal))
        {
            if (!(reshadeIni.Get("GENERAL", "PreprocessorDefinitions") ?? "").Contains("MY_OWN_DEFINE=1", StringComparison.Ordinal))
                yield return "ReShade.ini: eigene Präprozessor-Werte des Nutzers gingen verloren";
            if (reshadeIni.Get("INPUT", "KeyOverlay") != "36,0,0,0")
                yield return "ReShade.ini: eigene Tastenbelegung ging verloren";
        }
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), ComponentStore.Sha256Of, StringComparer.OrdinalIgnoreCase)
            : [];

    private static IEnumerable<string> Diff(Dictionary<string, string> before, Dictionary<string, string> after, string label)
    {
        foreach (var (path, hash) in before)
        {
            if (!after.TryGetValue(path, out var now))
                yield return $"{label}: {path} fehlt nach Rückgängig";
            else if (now != hash)
                yield return $"{label}: {path} ist nach Rückgängig verändert";
        }
        foreach (var path in after.Keys.Where(p => !before.ContainsKey(p)))
            yield return $"{label}: {path} bleibt nach Rückgängig liegen";
    }
}
