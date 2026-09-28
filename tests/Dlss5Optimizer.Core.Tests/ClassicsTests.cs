using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Models;
using Ids = Dlss5Optimizer.Core.Decision.RouteCatalog.Ids;

namespace Dlss5Optimizer.Core.Tests;

/// <summary>DirectX 8 über d3d8to9, Übersetzer-Wechsel, Tiefen-/Bewegungsprüfung und die Klassiker der Datenbank.</summary>
public class ClassicsTests
{
    private static readonly SystemInfo System5070Ti = new(
        new GpuInfo("NVIDIA GeForce RTX 5070 Ti", new Version(616, 64), 16L << 30),
        new DisplayInfo(2560, 1440, 144),
        HardwareSchedulingEnabled: true);

    private static GameAnalyzer Analyzer() => new(GameDatabase.LoadEmbedded());
    private static DecisionEngine AllAvailable() => new(_ => true, _ => true);

    private static (ComponentStore Store, ComponentAvailability Avail) AllComponents(TempDir t)
    {
        var catalog = ComponentCatalog.LoadEmbedded();
        var store = new ComponentStore(t.Combine("store"), catalog);
        void Import(string id, params string[] files) =>
            store.Import(id, files.Select(f => t.File(Path.Combine("src", id, f), $"{id}:{f}")).ToList());
        Import(Ids.ReShade, "ReShade64.dll", "ReShade32.dll", "ReShade64.json", "ReShade32.json");
        Import(Ids.Feeder, "dlss5-feed.addon64", "dlss5-feed.addon32", "DLSS5_Feed.fx", "dlss5-feed-host64.exe");
        Import(Ids.LumeniteFx, "lumenite_Kernel.fx");
        Import(Ids.ReShadeHeaders, "ReShade.fxh", "ReShadeUI.fxh");
        Import(Ids.RenoDx, "renodx-dlss5.addon64");
        Import(Ids.D3D8To9, "d3d8.dll");
        t.File("src/dxvk/dxvk-3.0.2/x32/d3d9.dll", "DXVK-x32");
        t.File("src/dxvk/dxvk-3.0.2/x64/d3d9.dll", "DXVK-x64");
        store.Import(Ids.Dxvk, [t.Combine("src/dxvk/dxvk-3.0.2")]);
        var model = t.File("driver/nvngx_dlssnr.dll", "MODEL");
        var runtime = t.File("other/nvngx_dlss.dll", "DLSS");
        return (store, new ComponentAvailability(catalog, store, () => model, () => runtime));
    }

    // ------------------------------------------------------------------ DirectX 8

    [Fact]
    public void Dx8GamePrefersD3D8To9WithDxvk()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("Old/old.exe"), TestPe.I386, ["d3d8.dll"], largeAddressAware: true);
        var a = Analyzer().Analyze(new GameInfo("Old", t.Combine("Old"), GameSource.Manual));

        var rec = AllAvailable().Recommend(a, System5070Ti, new UserPreferences());

        Assert.Equal(GraphicsApi.D3D8, a.Api.Primary);
        Assert.Equal(RouteId.LegacyD3D8Dxvk, rec.Best?.Route.Id);
        Assert.Contains(rec.Alternatives, c => c.Route.Id == RouteId.LegacyFeeder);
    }

    [Fact]
    public void Dx8PlanInstallsD3D8To9InFrontOfDxvk()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("Old/old.exe"), TestPe.I386, ["d3d8.dll"], largeAddressAware: true);
        var (store, avail) = AllComponents(t);
        var a = Analyzer().Analyze(new GameInfo("Old", t.Combine("Old"), GameSource.Manual));
        var best = new DecisionEngine(avail.IsAvailable, avail.CanAutoDownload).Recommend(a, System5070Ti, new UserPreferences()).Best!;

        var plan = new RoutePlanner(avail, store).Plan(a, best);

        var copies = plan.Steps.OfType<CopyFileStep>().ToList();
        Assert.Contains(copies, c => c.Target == "d3d8.dll" && c.Source.EndsWith("d3d8.dll") && File.ReadAllText(c.Source) == "d3d8to9:d3d8.dll");
        Assert.Contains(copies, c => c.Target == "d3d9.dll" && File.ReadAllText(c.Source) == "DXVK-x32");
        Assert.True(Assert.Single(plan.Steps.OfType<RegisterVulkanLayerStep>()).Is32Bit);
        Assert.Contains(plan.Hints, h => h.Contains("DirectX End-User Runtime"));
    }

    // ------------------------------------------------------------------ Merkliste + Wechsel

    [Fact]
    public void FailedRouteIsSkippedAndWorkingRouteIsPreferred()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("G/g.exe"), TestPe.I386, ["d3d9.dll"], largeAddressAware: true);
        var a = Analyzer().Analyze(new GameInfo("G", t.Combine("G"), GameSource.Manual));
        var failedDxvk = new Dictionary<RouteId, RouteOutcome> { [RouteId.LegacyDxvkFeeder] = new(false, "Übersetzer abgestürzt", DateTimeOffset.Now) };

        var rec = AllAvailable().Recommend(a, System5070Ti, new UserPreferences(), outcomes: failedDxvk);

        Assert.Equal(RouteId.LegacyFeeder, rec.Best?.Route.Id);
        Assert.DoesNotContain(rec.Alternatives, c => c.Route.Id == RouteId.LegacyDxvkFeeder);
        Assert.Contains(rec.Notes, n => n.Contains("gescheitert"));

        var workedDgVoodoo = new Dictionary<RouteId, RouteOutcome> { [RouteId.LegacyFeeder] = new(true, "ok", DateTimeOffset.Now) };
        var rec2 = AllAvailable().Recommend(a, System5070Ti, new UserPreferences(), outcomes: workedDgVoodoo);
        Assert.Equal(RouteId.LegacyFeeder, rec2.Best?.Route.Id);
        Assert.Contains(rec2.Best!.Reasons, r => r.Contains("Bei dir getestet"));
    }

    private static InstallManifest Manifest(RouteId route, GraphicsApi api) =>
        new(1, DateTimeOffset.Now.AddMinutes(-5), new Configuration(route, api, SrMode.Native, 1, NrPlacement.PostUpscale, FrameGenMode.Off), [], [], [], []);

    [Fact]
    public void CrashInTranslatorSuggestsTheOtherOne()
    {
        using var t = new TempDir();
        var dir = t.Dir("game");
        var crashes = new[] { new CrashInfo("d3d9.dll", "0xc0000005", DateTime.Now) };

        var dxvk = InstallDiagnostics.Evaluate(dir, Manifest(RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9), "g.exe", crashes);
        var dgv = InstallDiagnostics.Evaluate(dir, Manifest(RouteId.LegacyFeeder, GraphicsApi.D3D9), "g.exe", crashes);
        var dx8 = InstallDiagnostics.Evaluate(dir, Manifest(RouteId.LegacyD3D8Dxvk, GraphicsApi.D3D8), "g.exe", [new CrashInfo("d3d8.dll", "0xc0000005", DateTime.Now)]);

        Assert.Equal(DiagnosticVerdict.TranslatorFailed, dxvk.Verdict);
        Assert.Equal(RouteId.LegacyFeeder, dxvk.SwitchTo);
        Assert.Equal(RouteId.LegacyDxvkFeeder, dgv.SwitchTo);
        Assert.Equal(RouteId.LegacyFeeder, dx8.SwitchTo);
    }

    [Fact]
    public void NoSwitchToARouteTheDatabaseExcludes()
    {
        using var t = new TempDir();
        var fnv = GameDatabase.LoadEmbedded().Entries.Single(e => e.Name == "Fallout: New Vegas");

        var report = InstallDiagnostics.Evaluate(t.Dir("game"), Manifest(RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9), "FalloutNV.exe",
            [new CrashInfo("d3d9.dll", "0xc0000005", DateTime.Now)], fnv);

        Assert.Equal(DiagnosticVerdict.TranslatorFailed, report.Verdict);
        Assert.Null(report.SwitchTo);
    }

    [Fact]
    public void CrashElsewhereIsNotBlamedOnTheTranslator()
    {
        using var t = new TempDir();
        var report = InstallDiagnostics.Evaluate(t.Dir("game"), Manifest(RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9), "g.exe",
            [new CrashInfo("nvse_1_4.dll", "0xc0000005", DateTime.Now)]);

        Assert.Equal(DiagnosticVerdict.NeedsAttention, report.Verdict);
        Assert.Null(report.SwitchTo);
    }

    [Fact]
    public void WorkingChainGivesWorkingVerdict()
    {
        using var t = new TempDir();
        var dir = t.Dir("game");
        t.Dir("game/host64");
        t.File("game/g_d3d9.log", "info: DXVK");
        t.File("game/ReShade.log", "INFO | Initializing");
        t.File("game/dlss5-feed.log",
            "[feed] shared set ready (Vulkan): 2560x1440\n[feed] frame 600 delivered\n"
            + "[feed] MV probe (centre 64x64, frame 600): mean |mv| 1.234 px, max 9.00 px, 37% non-zero\n"
            + "[feed] Depth probe (4x 32x32, frame 600): min 0.1, max 0.9, mean 0.5, variance 0.01, 100% finite\n");
        t.File("game/host64/dlss5-feed-host.log", "feature ready: 2560x1440 DLAA\nframe 600 evaluated");
        t.File("game/host64/ReShade.log", "inline feature 18 evaluation succeeded");

        var report = InstallDiagnostics.Evaluate(dir, Manifest(RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9), "g.exe", []);

        Assert.Equal(DiagnosticVerdict.Working, report.Verdict);
        Assert.Contains(report.Checks, c => c.Title == "Bewegungsvektoren" && c.Detail.Contains("37 %"));
    }

    // ------------------------------------------------------------------ Tiefe + Bewegungsvektoren

    [Fact]
    public void MotionVectorProbe()
    {
        Assert.Equal(DiagnosticStatus.NotRunYet, InstallDiagnostics.MotionVectorCheck("nothing yet")!.Status);
        var once = "MV probe (centre 64x64, frame 600): mean |mv| 0.000 px, max 0.00 px, 0% non-zero  <-- DLSS is getting (almost) no motion vectors";
        Assert.Equal(DiagnosticStatus.Warning, InstallDiagnostics.MotionVectorCheck(once)!.Status);
        var twice = once + "\n" + once.Replace("600", "1200");
        Assert.Equal(DiagnosticStatus.Failed, InstallDiagnostics.MotionVectorCheck(twice)!.Status);
        var moving = twice + "\nMV probe (centre 64x64, frame 1800): mean |mv| 2.1 px, max 12.0 px, 64% non-zero";
        Assert.Equal(DiagnosticStatus.Ok, InstallDiagnostics.MotionVectorCheck(moving)!.Status);
    }

    [Fact]
    public void DepthProbe()
    {
        Assert.Equal(DiagnosticStatus.NotRunYet, InstallDiagnostics.DepthCheck("no depth probe yet (first one after 600 frames)")!.Status);
        var flat = "Depth probe (4x 32x32, frame 600): min 1, max 1, mean 1, variance 0, 100% finite  <-- sampled depth is flat; inspect the depth debug view / Generic Depth settings";
        Assert.Equal(DiagnosticStatus.Failed, InstallDiagnostics.DepthCheck(flat)!.Status);
        var none = "Depth probe (4x 32x32, frame 600): min 0, max 0, mean 0, variance 0, 0% finite";
        Assert.Equal(DiagnosticStatus.Failed, InstallDiagnostics.DepthCheck(none)!.Status);
        var good = flat + "\nDepth probe (4x 32x32, frame 1200): min 0.01, max 0.97, mean 0.4, variance 0.02, 100% finite";
        Assert.Equal(DiagnosticStatus.Ok, InstallDiagnostics.DepthCheck(good)!.Status);
    }

    [Fact]
    public void DepthAssistantCyclesThroughAllVariantsAndKeepsOtherDefines()
    {
        var ini = IniFile.Parse("[GENERAL]\nPreprocessorDefinitions=RESHADE_DEPTH_LINEARIZATION_FAR_PLANE=1000.0,RESHADE_DEPTH_INPUT_IS_REVERSED=1,RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=0,DLSS5_MV_PROVIDER=3\n");
        var start = DepthAssistant.Read(ini);
        Assert.Equal(DepthAssistant.Variants[0], start);

        var seen = new HashSet<DepthVariant> { start };
        var v = start;
        for (int i = 0; i < DepthAssistant.Variants.Count - 1; i++)
        {
            v = DepthAssistant.Next(v);
            DepthAssistant.Apply(ini, v);
            Assert.Equal(v, DepthAssistant.Read(ini));
            seen.Add(v);
        }
        Assert.Equal(DepthAssistant.Variants.Count, seen.Count);
        Assert.Equal(start, DepthAssistant.Next(v));

        var defs = ini.Get("GENERAL", "PreprocessorDefinitions")!;
        Assert.Contains("RESHADE_DEPTH_LINEARIZATION_FAR_PLANE=1000.0", defs);
        Assert.Contains("DLSS5_MV_PROVIDER=3", defs);
        Assert.Single(defs.Split(','), d => d.StartsWith("RESHADE_DEPTH_INPUT_IS_REVERSED"));
    }

    [Fact]
    public void PlannerKeepsDepthAssistantChoiceOnRepair()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("G/g.exe"), TestPe.I386, ["d3d9.dll"], largeAddressAware: true);
        var (store, avail) = AllComponents(t);
        var a = Analyzer().Analyze(new GameInfo("G", t.Combine("G"), GameSource.Manual));
        var best = new DecisionEngine(avail.IsAvailable, avail.CanAutoDownload).Recommend(a, System5070Ti, new UserPreferences()).Best!;
        var chosen = new DepthVariant(CopyBeforeClears: true, Reversed: false, UpsideDown: false);

        var plan = new RoutePlanner(avail, store) { DepthOverride = _ => chosen }.Plan(a, best);

        var ini = plan.Steps.OfType<IniSetStep>().ToList();
        Assert.Contains(ini, i => i.Target == "ReShade.ini" && i.Section == "DEPTH" && i.Key == "DepthCopyBeforeClears" && i.Value == "1");
        Assert.Contains(ini, i => i.Target == "ReShade.ini" && i.Key == "PreprocessorDefinitions" && i.Value.Contains("RESHADE_DEPTH_INPUT_IS_REVERSED=0"));
    }

    // ------------------------------------------------------------------ Klassiker in der Datenbank

    [Fact]
    public void DarkMessiahGetsDxvkAlsoInBin()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("Dark Messiah/mm.exe"), TestPe.I386, ["kernel32.dll"], largeAddressAware: true);
        TestPe.Write(t.Combine("Dark Messiah/bin/shaderapidx9.dll"), TestPe.I386, ["d3d9.dll"]);
        var (store, avail) = AllComponents(t);
        var a = Analyzer().Analyze(new GameInfo("Dark Messiah of Might & Magic", t.Combine("Dark Messiah"), GameSource.Steam, "2100"));
        var best = new DecisionEngine(avail.IsAvailable, avail.CanAutoDownload).Recommend(a, System5070Ti, new UserPreferences()).Best!;

        var plan = new RoutePlanner(avail, store).Plan(a, best);

        Assert.Equal("Dark Messiah of Might and Magic", a.DbEntry?.Name);
        Assert.Equal(RouteId.LegacyDxvkFeeder, best.Route.Id);
        var copies = plan.Steps.OfType<CopyFileStep>().ToList();
        Assert.Contains(copies, c => c.Target == "d3d9.dll");
        Assert.Contains(copies, c => c.Target == Path.Combine("bin", "d3d9.dll"));
    }

    [Fact]
    public void FarCryIsFoundByItsSmallStarterNextToABiggerEditor()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("Far Cry/Bin32/FarCry.exe"), TestPe.I386, ["kernel32.dll"], largeAddressAware: true);
        TestPe.Write(t.Combine("Far Cry/Bin32/Editor.exe"), TestPe.I386, ["kernel32.dll"], trailer: new byte[500_000]);
        TestPe.Write(t.Combine("Far Cry/Bin32/XRenderD3D9.dll"), TestPe.I386, ["d3d9.dll"]);
        TestPe.Write(t.Combine("Far Cry/Bin32/XRenderOGL.dll"), TestPe.I386, ["opengl32.dll"]);

        var a = Analyzer().Analyze(new GameInfo("Far Cry", t.Combine("Far Cry"), GameSource.Gog, "1207658719"));

        Assert.Equal("Far Cry", a.DbEntry?.Name);
        Assert.Equal("FarCry.exe", Path.GetFileName(a.MainExe));
        Assert.Equal(GraphicsApi.D3D9, a.Api.Primary);
        Assert.Equal(RouteId.LegacyDxvkFeeder, AllAvailable().Recommend(a, System5070Ti, new UserPreferences()).Best?.Route.Id);
    }

    [Fact]
    public void BloodRayne2KeepsItsOwnD3D8Bridge()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("BR2/rayne2.exe"), TestPe.I386, ["d3d8.dll"], largeAddressAware: true);
        TestPe.Write(t.Combine("BR2/d3d8.dll"), TestPe.I386, ["d3d9.dll"]);
        var a = Analyzer().Analyze(new GameInfo("BloodRayne 2: Terminal Cut", t.Combine("BR2"), GameSource.Gog, "br2"));

        var rec = AllAvailable().Recommend(a, System5070Ti, new UserPreferences());

        Assert.Equal(GraphicsApi.D3D9, a.Api.Primary);
        Assert.Equal(RouteId.LegacyDxvkFeeder, rec.Best?.Route.Id);
        Assert.DoesNotContain(rec.Alternatives, c => c.Route.Id is RouteId.LegacyD3D8Dxvk or RouteId.LegacyFeeder);
    }

    [Fact]
    public void DeusExHumanRevolutionUsesFeederWithoutTranslator()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("DXHR/dxhr.exe"), TestPe.I386, ["kernel32.dll"], largeAddressAware: true);
        var a = Analyzer().Analyze(new GameInfo("Deus Ex: Human Revolution", t.Combine("DXHR"), GameSource.Steam, "28050"));

        var rec = AllAvailable().Recommend(a, System5070Ti, new UserPreferences());

        Assert.Equal(GraphicsApi.D3D11, a.Api.Primary);
        Assert.Equal(RouteId.Feeder, rec.Best?.Route.Id);
    }

    [Fact]
    public void Doom2016PicksVulkanExe()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("DOOM/DOOMx64vk.exe"), TestPe.Amd64, ["vulkan-1.dll"]);
        TestPe.Write(t.Combine("DOOM/DOOMx64.exe"), TestPe.Amd64, ["opengl32.dll"], trailer: new byte[100_000]);
        var a = Analyzer().Analyze(new GameInfo("DOOM", t.Combine("DOOM"), GameSource.Steam, "379720"));

        var rec = AllAvailable().Recommend(a, System5070Ti, new UserPreferences());

        Assert.Equal("DOOMx64vk.exe", Path.GetFileName(a.MainExe));
        Assert.Equal(GraphicsApi.Vulkan, rec.Best?.Config.Api);
        Assert.Equal(RouteId.Feeder, rec.Best?.Route.Id);
    }

    [Fact]
    public void EveryDatabaseRouteNameIsValid()
    {
        var names = Enum.GetNames<RouteId>();
        foreach (var e in GameDatabase.LoadEmbedded().Entries)
        {
            if (e.PreferredRoute is { } p)
                Assert.Contains(p, names);
            Assert.All(e.ExcludedRoutes, r => Assert.Contains(r, names));
        }
    }
}
