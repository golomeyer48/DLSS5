using Dlss5Optimizer.Core.Benchmark;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Tests;

public class SystemInfoTests
{
    [Theory]
    [InlineData("32.0.16.1664", 616, 64)]
    [InlineData("32.0.16.1656", 616, 56)]
    [InlineData("32.0.15.6094", 560, 94)]
    [InlineData("31.0.15.3623", 536, 23)]
    public void ConvertsWindowsDriverVersion(string win, int major, int minor) =>
        Assert.Equal(new Version(major, minor), NvidiaDriverVersion.FromWindowsVersion(win));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2")]
    public void RejectsInvalidDriverVersion(string win) => Assert.Null(NvidiaDriverVersion.FromWindowsVersion(win));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", GpuFamily.Rtx50, 1406)]
    [InlineData("NVIDIA GeForce RTX 5070", GpuFamily.Rtx50, 988)]
    [InlineData("NVIDIA GeForce RTX 5090", GpuFamily.Rtx50, 3352)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti Laptop GPU", GpuFamily.Rtx50, 992)]
    [InlineData("NVIDIA GeForce RTX 4090", GpuFamily.Rtx40, 988)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", GpuFamily.NvidiaNoTensor, 988)]
    [InlineData("AMD Radeon RX 9070 XT", GpuFamily.NonNvidia, 988)]
    public void ClassifiesGpus(string name, GpuFamily family, double tops)
    {
        var gpu = new GpuInfo(name, null, 0);
        Assert.Equal(family, gpu.Family);
        if (family == GpuFamily.Rtx50)
            Assert.Equal(tops, gpu.TensorTops);
    }
}

public class FrameTimeModelTests
{
    private static SystemInfo Rtx5070Ti4K() => new(new GpuInfo("NVIDIA GeForce RTX 5070 Ti", new Version(616, 64), 16L << 30), new DisplayInfo(3840, 2160, 144));

    [Fact]
    public void ReproducesPublishedModMeasurement()
    {
        // Veröffentlicht: RTX 5070 Ti, 4K, 180 fps → 80 fps mit DLSS 5 (Post-Upscale).
        var model = new FrameTimeModel(Rtx5070Ti4K(), new Calibration(180, SrMode.Performance, GraphicsApi.D3D12));
        var route = RouteCatalog.Get(RouteId.OptiScalerNr);
        var p = model.Predict(route, new Configuration(route.Id, GraphicsApi.D3D12, SrMode.Performance, 1.0, NrPlacement.PostUpscale, FrameGenMode.Off), Bitness.X64, 100);

        Assert.InRange(p.RenderedFps, 72, 88);
    }

    [Fact]
    public void PreUpscaleIsMuchCheaper()
    {
        // Veröffentlicht: Pre-Upscale-Fork 122 fps im selben Test.
        var model = new FrameTimeModel(Rtx5070Ti4K(), new Calibration(180, SrMode.Performance, GraphicsApi.D3D12));
        var route = RouteCatalog.Get(RouteId.OptiScalerNr);
        var p = model.Predict(route, new Configuration(route.Id, GraphicsApi.D3D12, SrMode.Performance, 1.0, NrPlacement.PreUpscale, FrameGenMode.Off), Bitness.X64, 92);

        Assert.InRange(p.RenderedFps, 110, 135);
    }

    [Fact]
    public void FrameGenIsCappedAtRefreshRate()
    {
        var model = new FrameTimeModel(Rtx5070Ti4K(), new Calibration(180, SrMode.Performance, GraphicsApi.D3D12));
        var route = RouteCatalog.Get(RouteId.OptiScalerNr);
        var p = model.Predict(route, new Configuration(route.Id, GraphicsApi.D3D12, SrMode.Performance, 1.0, NrPlacement.PostUpscale, FrameGenMode.DlssMfg6x), Bitness.X64, 90);

        Assert.Equal(144, p.DisplayedFps);
    }

    [Fact]
    public void DerivedCostRoundTripsThroughPrediction()
    {
        // Basis bei DLSS Qualität gemessen, DLSS 5 danach mit DLSS Leistung installiert.
        var model = new FrameTimeModel(Rtx5070Ti4K(), new Calibration(90, SrMode.Quality, GraphicsApi.D3D12));
        var route = RouteCatalog.Get(RouteId.OptiScalerNr);
        var config = new Configuration(route.Id, GraphicsApi.D3D12, SrMode.Performance, 0.75, NrPlacement.PostUpscale, FrameGenMode.Off);

        double k = model.DeriveMsPerMegapixel(route, config, Bitness.X64, measuredRenderedFps: 70);
        var calibrated = new FrameTimeModel(Rtx5070Ti4K(), new Calibration(90, SrMode.Quality, GraphicsApi.D3D12, k));

        Assert.Equal(70, calibrated.Predict(route, config, Bitness.X64, 100).RenderedFps, 3);
    }

    [Fact]
    public void DerivesCostFromTwoMeasurements()
    {
        double k = FrameTimeModel.DeriveMsPerMegapixel(180, 80, 3840 * 2160 / 1e6, 0.1);
        Assert.InRange(k, 0.8, 0.85);
    }
}

public class DecisionEngineTests
{
    private static readonly SystemInfo System5070Ti = new(
        new GpuInfo("NVIDIA GeForce RTX 5070 Ti", new Version(616, 64), 16L << 30),
        new DisplayInfo(2560, 1440, 144),
        HardwareSchedulingEnabled: true);

    private static GameAnalysis Game(GraphicsApi primary, GraphicsApi supported, UpscalerFeature upscalers, Bitness bitness = Bitness.X64,
        AntiCheatInfo? antiCheat = null, GameDbEntry? db = null, GameEngine engine = GameEngine.Unknown) =>
        new(new GameInfo("Test", "/games/test", GameSource.Manual), "/games/test/game.exe", bitness, engine,
            new ApiDetection(primary, supported, 0.9, new Dictionary<GraphicsApi, double>()),
            new UpscalerInfo(upscalers, null, null, null), ExistingMod.None, antiCheat ?? AntiCheatInfo.None, db, [], []);

    private static DecisionEngine Engine(params string[] available) =>
        new(id => available.Contains(id), id => id is not (RouteCatalog.Ids.RenoDx or RouteCatalog.Ids.DeepFriedChicken or RouteCatalog.Ids.LumeniteFx or RouteCatalog.Ids.OptiScalerPreUpscale));

    [Fact]
    public void Dx12WithDlssPrefersOptiScaler()
    {
        var game = Game(GraphicsApi.D3D12, GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution);
        var rec = Engine().Recommend(game, System5070Ti, new UserPreferences(OptimizationProfile.Quality));

        Assert.False(rec.Blocked);
        Assert.NotNull(rec.Best);
        Assert.Equal(RouteId.OptiScalerNr, rec.Best.Route.Id);
        Assert.True(rec.Best.Installable);
        Assert.True(rec.Best.Prediction.DisplayedFps >= rec.TargetFps);
    }

    [Fact]
    public void AntiCheatBlocksMods()
    {
        var game = Game(GraphicsApi.D3D12, GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution, antiCheat: new AntiCheatInfo(["BattlEye"]));
        var rec = Engine().Recommend(game, System5070Ti, new UserPreferences());

        Assert.True(rec.Blocked);
        Assert.Contains("BattlEye", rec.BlockedReason);
    }

    [Fact]
    public void NativeDlss5IsAlwaysTheAnswer()
    {
        var db = new GameDbEntry { Name = "NBA 2K27", NativeDlss5 = true };
        var game = Game(GraphicsApi.D3D12, GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution, antiCheat: new AntiCheatInfo(["Easy Anti-Cheat"]), db: db);
        var rec = Engine().Recommend(game, System5070Ti, new UserPreferences());

        Assert.False(rec.Blocked);
        Assert.Equal(RouteId.NativeDlss5, rec.Best?.Route.Id);
        Assert.All(rec.Alternatives, a => Assert.Equal(RouteId.NativeDlss5, a.Route.Id));
    }

    [Fact]
    public void Rtx40IsBlocked()
    {
        var sys = System5070Ti with { Gpu = new GpuInfo("NVIDIA GeForce RTX 4080", new Version(616, 64), 16L << 30) };
        var rec = Engine().Recommend(Game(GraphicsApi.D3D12, GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution), sys, new UserPreferences());

        Assert.True(rec.Blocked);
    }

    [Fact]
    public void OldDriverIsBlocked()
    {
        var sys = System5070Ti with { Gpu = System5070Ti.Gpu with { DriverVersion = new Version(591, 44) } };
        var rec = Engine().Recommend(Game(GraphicsApi.D3D12, GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution), sys, new UserPreferences());

        Assert.True(rec.Blocked);
        Assert.Contains("616.56", rec.BlockedReason);
    }

    [Fact]
    public void Dx11WithDlssUsesOptiScalerViaDx11On12()
    {
        var game = Game(GraphicsApi.D3D11, GraphicsApi.D3D11, UpscalerFeature.DlssSuperResolution);
        var rec = Engine().Recommend(game, System5070Ti, new UserPreferences());

        Assert.Equal(RouteId.OptiScalerNr, rec.Best?.Route.Id);
        Assert.Equal(GraphicsApi.D3D11, rec.Best?.Config.Api);
    }

    [Fact]
    public void VulkanOnlyGameNeedsClosedAddon()
    {
        var game = Game(GraphicsApi.Vulkan, GraphicsApi.Vulkan, UpscalerFeature.DlssSuperResolution);
        var engine = new DecisionEngine(_ => false, id => id is not RouteCatalog.Ids.DeepFriedChicken and not RouteCatalog.Ids.RenoDx and not RouteCatalog.Ids.LumeniteFx);
        var rec = engine.Recommend(game, System5070Ti, new UserPreferences());

        // Ohne importiertes Add-on ist nichts installierbar – die Empfehlung nennt, was fehlt.
        Assert.Null(rec.Best);
        Assert.NotNull(rec.BestWithImports);
        Assert.Equal(RouteId.BridgeVulkan, rec.BestWithImports.Route.Id);
        Assert.Contains(rec.BestWithImports.Missing, m => !m.CanAutoDownload);
        Assert.Contains(rec.Notes, n => n.Contains("importieren"));

        var withAddon = Engine(RouteCatalog.Ids.DeepFriedChicken).Recommend(game, System5070Ti, new UserPreferences());
        Assert.Equal(RouteId.BridgeVulkan, withAddon.Best?.Route.Id);
        Assert.All(new[] { withAddon.Best! }.Concat(withAddon.Alternatives).Where(c => c.Config.Api == GraphicsApi.Vulkan),
            c => Assert.NotEqual(FrameGenMode.SmoothMotion, c.Config.FrameGen));
    }

    [Fact]
    public void PreUpscaleNeverCombinedWithReducedModelScale()
    {
        var game = Game(GraphicsApi.D3D12, GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution);
        var engine = new DecisionEngine(_ => true, _ => true);
        var rec = engine.Recommend(game, System5070Ti, new UserPreferences(OptimizationProfile.TargetFps, 1000));

        var all = new[] { rec.Best! }.Concat(rec.Alternatives);
        Assert.DoesNotContain(all, c => c.Config.Placement == NrPlacement.PreUpscale && c.Config.NrScale < 1.0);
    }

    [Fact]
    public void MultiApiGameIsSwitchedToDx12()
    {
        var game = Game(GraphicsApi.Vulkan, GraphicsApi.Vulkan | GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution, engine: GameEngine.Unreal5);
        var rec = Engine(RouteCatalog.Ids.DeepFriedChicken).Recommend(game, System5070Ti, new UserPreferences(OptimizationProfile.Quality));

        Assert.Equal(GraphicsApi.D3D12, rec.Best?.Config.Api);
        Assert.NotNull(rec.ApiSwitch);
        Assert.Equal(GraphicsApi.Vulkan, rec.ApiSwitch.From);
        Assert.Contains("-dx12", rec.ApiSwitch.How);
    }

    [Fact]
    public void FrameGenIsNotRecommendedOnLowBase()
    {
        var game = Game(GraphicsApi.D3D12, GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution | UpscalerFeature.DlssFrameGeneration);
        var cal = new Dictionary<GraphicsApi, Calibration> { [GraphicsApi.D3D12] = new(30, SrMode.Dlaa, GraphicsApi.D3D12) };
        var rec = Engine().Recommend(game, System5070Ti, new UserPreferences(OptimizationProfile.TargetFps, 240), cal);

        Assert.NotNull(rec.Best);
        var all = new[] { rec.Best }.Concat(rec.Alternatives);
        Assert.All(all.Where(c => c.Config.FrameGen != FrameGenMode.Off),
            c => Assert.True(c.Prediction.RenderedFps >= FrameTimeModel.MinBaseFpsForFrameGen));
    }

    [Fact]
    public void GameWithoutUpscalerFallsBackToFeeder()
    {
        var game = Game(GraphicsApi.D3D11, GraphicsApi.D3D11, UpscalerFeature.None);
        var rec = Engine(RouteCatalog.Ids.LumeniteFx, RouteCatalog.Ids.DeepFriedChicken).Recommend(game, System5070Ti, new UserPreferences());

        Assert.Equal(RouteId.Feeder, rec.Best?.Route.Id);
        Assert.Equal(MotionVectorSource.Estimated, rec.Best?.Route.MotionVectors);
    }

    [Fact]
    public void Legacy32BitDx9PrefersDxvkOverDgVoodoo()
    {
        var game = Game(GraphicsApi.D3D9, GraphicsApi.D3D9, UpscalerFeature.None, Bitness.X86);
        var rec = Engine(RouteCatalog.Ids.LumeniteFx, RouteCatalog.Ids.RenoDx).Recommend(game, System5070Ti, new UserPreferences());

        Assert.Equal(RouteId.LegacyDxvkFeeder, rec.Best?.Route.Id);
        Assert.Contains(rec.Alternatives, a => a.Route.Id == RouteId.LegacyFeeder);
    }

    [Fact]
    public void Dx8GameUsesDgVoodoo()
    {
        var game = Game(GraphicsApi.D3D8, GraphicsApi.D3D8, UpscalerFeature.None, Bitness.X86);
        var rec = Engine(RouteCatalog.Ids.LumeniteFx, RouteCatalog.Ids.RenoDx).Recommend(game, System5070Ti, new UserPreferences());

        Assert.Equal(RouteId.LegacyFeeder, rec.Best?.Route.Id);
    }

    [Fact]
    public void LaunchArguments()
    {
        Assert.Equal("-dx12", DecisionEngine.LaunchArgument(GameEngine.Unreal4, GraphicsApi.D3D12));
        Assert.Equal("-force-vulkan", DecisionEngine.LaunchArgument(GameEngine.Unity, GraphicsApi.Vulkan));
        Assert.Null(DecisionEngine.LaunchArgument(GameEngine.Unknown, GraphicsApi.D3D12));
    }
}

public class BenchmarkTests
{
    [Fact]
    public void ParsesPresentMon2Csv()
    {
        var csv = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,MsBetweenPresents,MsBetweenDisplayChange,MsPCLatency\n"
                  + string.Concat(Enumerable.Range(0, 400).Select(i =>
                      $"game.exe,1,0x1,DXGI,0,0,1,Hardware: Independent Flip,{(i % 2 == 0 ? "Application" : "NvidiaDLSSFG")},{(i == 399 ? 50 : 10)},10,25\n"))
                  + "other.exe,2,0x2,DXGI,0,0,1,Composed: Flip,Application,5,5,0\n";

        var samples = PresentMonCsv.Parse(new StringReader(csv), "game.exe");
        var stats = FrameStats.From(samples, warmupSeconds: 0);

        Assert.Equal(400, samples.Count);
        Assert.NotNull(stats);
        Assert.InRange(stats.AverageFps, 98, 100);
        Assert.InRange(stats.RenderedFps!.Value, 49, 50);
        Assert.Equal(25, stats.AveragePcLatencyMs);
        Assert.Equal("DXGI", stats.DominantRuntime);
        Assert.True(stats.PointOnePercentLowFps < stats.AverageFps);
    }

    [Fact]
    public void ParsesPresentMon1CsvWithLowercaseColumns()
    {
        var csv = "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents\n"
                  + string.Concat(Enumerable.Range(0, 100).Select(_ => "g.exe,1,0,D3D9,0,0,0,Hardware: Legacy Flip,0,1.0,0.1,16.667\n"));

        var stats = FrameStats.From(PresentMonCsv.Parse(new StringReader(csv)), warmupSeconds: 0);

        Assert.NotNull(stats);
        Assert.InRange(stats.AverageFps, 59.9, 60.1);
        Assert.Null(stats.RenderedFps);
        Assert.Equal("D3D9", stats.DominantRuntime);
    }

    [Fact]
    public void CsvSplitHandlesQuotes() =>
        Assert.Equal(["a", "b,c", "d\"e"], PresentMonCsv.SplitCsv("a,\"b,c\",\"d\"\"e\""));

    [Fact]
    public void RejectsNonPresentMonFiles() =>
        Assert.Throws<InvalidDataException>(() => PresentMonCsv.Parse(new StringReader("foo,bar\n1,2\n")));

    [Theory]
    [InlineData(new[] { "C:\\x\\game.exe", "d3d12.dll", "D3D12Core.dll", "d3d11.dll", "dxgi.dll" }, "DXGI", GraphicsApi.D3D12)]
    [InlineData(new[] { "d3d11.dll", "dxgi.dll", "nvwgf2umx.dll" }, "DXGI", GraphicsApi.D3D11)]
    [InlineData(new[] { "d3d11.dll", "dxgi.dll", "vulkan-1.dll", "nvoglv64.dll" }, "Other", GraphicsApi.Vulkan)]
    [InlineData(new[] { "opengl32.dll", "nvoglv64.dll" }, "Other", GraphicsApi.OpenGL)]
    [InlineData(new[] { "d3d9.dll", "nvd3dumx.dll" }, "D3D9", GraphicsApi.D3D9)]
    [InlineData(new[] { "kernel32.dll" }, null, GraphicsApi.None)]
    public void ProbeInterpretsModules(string[] modules, string? runtime, GraphicsApi expected) =>
        Assert.Equal(expected, ProbeInterpreter.Interpret(modules, runtime).Api);

    [Fact]
    public void ProbeSeesActiveDlss()
    {
        var r = ProbeInterpreter.Interpret(["d3d12core.dll", "nvngx_dlss.dll", "sl.dlss_g.dll"], "DXGI");
        Assert.True(r.DlssLoaded);
        Assert.True(r.FrameGenLoaded);
    }
}
