using System.Formats.Tar;
using System.IO.Compression;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Models;
using Ids = Dlss5Optimizer.Core.Decision.RouteCatalog.Ids;

namespace Dlss5Optimizer.Core.Tests;

/// <summary>
/// Ende-zu-Ende-Szenarien für alte 32-Bit-DirectX-9-Spiele am Beispiel Fallout: New Vegas und
/// Fallout 3 – mit dem getesteten Aufbau DXVK → ReShade-Vulkan-Layer → Feeder → host64.
/// </summary>
public class LegacyGameTests
{
    private static readonly SystemInfo System5070Ti = new(
        new GpuInfo("NVIDIA GeForce RTX 5070 Ti", new Version(616, 64), 16L << 30),
        new DisplayInfo(2560, 1440, 144),
        HardwareSchedulingEnabled: true);

    /// <summary>Steam-Ordner von New Vegas mit Launcher, NVSE und ENB, ohne 4GB-Patch.</summary>
    private static string NewVegasFolder(TempDir t, bool withEnb = true, bool largeAddressAware = false)
    {
        var dir = t.Dir("Fallout New Vegas");
        TestPe.Write(t.Combine("Fallout New Vegas/FalloutNV.exe"), TestPe.I386, ["kernel32.dll", "d3d9.dll", "dinput8.dll", "d3dx9_38.dll"],
            trailer: new byte[400_000], largeAddressAware: largeAddressAware);
        TestPe.Write(t.Combine("Fallout New Vegas/FalloutNVLauncher.exe"), TestPe.I386, ["kernel32.dll", "d3d9.dll"], trailer: new byte[50_000]);
        TestPe.Write(t.Combine("Fallout New Vegas/nvse_loader.exe"), TestPe.I386, ["kernel32.dll"]);
        t.Dir("Fallout New Vegas/Data");
        if (withEnb)
        {
            TestPe.Write(t.Combine("Fallout New Vegas/d3d9.dll"), TestPe.I386, ["kernel32.dll"], trailer: "ENBSeries v0.4"u8.ToArray());
            t.File("Fallout New Vegas/enbseries.ini", "[GLOBAL]\n");
        }
        return dir;
    }

    private static (ComponentStore Store, ComponentAvailability Avail) AllComponents(TempDir t)
    {
        var catalog = ComponentCatalog.LoadEmbedded();
        var store = new ComponentStore(t.Combine("store"), catalog);
        void Import(string id, params string[] files) =>
            store.Import(id, files.Select(f => t.File(Path.Combine("src", id, f), $"{id}:{f}")).ToList());

        Import(Ids.ReShade, "ReShade64.dll", "ReShade32.dll", "ReShade64.json", "ReShade32.json");
        Import(Ids.Feeder, "dlss5-feed.addon64", "dlss5-feed.addon32", "DLSS5_Feed.fx", "dlss5-feed-host64.exe");
        Import(Ids.LumeniteFx, "lumenite_Kernel.fx", "lumenite_bluenoise256.png");
        Import(Ids.ReShadeHeaders, "ReShade.fxh", "ReShadeUI.fxh", "DrawText.fxh");
        Import(Ids.RenoDx, "renodx-dlss5.addon64");
        // DXVK hat zwei d3d9.dll (x32/x64) – als Ordner importieren, wie es das Archiv liefert.
        t.File("src/dxvk/dxvk-3.0.2/x32/d3d9.dll", "DXVK-x32");
        t.File("src/dxvk/dxvk-3.0.2/x64/d3d9.dll", "DXVK-x64");
        store.Import(Ids.Dxvk, [t.Combine("src/dxvk/dxvk-3.0.2")]);

        var model = t.File("driver/nvngx_dlssnr.dll", "MODEL");
        var runtime = t.File("othergame/nvngx_dlss.dll", "DLSS");
        return (store, new ComponentAvailability(catalog, store, () => model, () => runtime));
    }

    [Fact]
    public void NewVegasIsAnalyzedCorrectly()
    {
        using var t = new TempDir();
        var dir = NewVegasFolder(t);

        var a = new GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Fallout: New Vegas", dir, GameSource.Steam, "22380"));

        Assert.EndsWith("FalloutNV.exe", a.MainExe);
        Assert.Equal("Fallout: New Vegas", a.DbEntry?.Name);
        Assert.Equal(Bitness.X86, a.Bitness);
        Assert.Equal(GraphicsApi.D3D9, a.Api.Primary);
        Assert.True(a.Mods.HasFlag(ExistingMod.Enb));
        Assert.Contains(a.Warnings, w => w.Contains("4GB Patcher"));
        Assert.Contains(a.Warnings, w => w.Contains("ENB"));
        Assert.False(a.AntiCheat.Detected);
    }

    [Fact]
    public void Fallout3GotyPicksGameExeNotLauncherOrLoader()
    {
        using var t = new TempDir();
        var dir = t.Dir("Fallout 3 goty");
        TestPe.Write(t.Combine("Fallout 3 goty/Fallout3.exe"), TestPe.I386, ["d3d9.dll"], trailer: new byte[400_000], largeAddressAware: true);
        TestPe.Write(t.Combine("Fallout 3 goty/FalloutLauncher.exe"), TestPe.I386, ["d3d9.dll"], trailer: new byte[60_000]);
        TestPe.Write(t.Combine("Fallout 3 goty/fose_loader.exe"), TestPe.I386, []);
        TestPe.Write(t.Combine("Fallout 3 goty/Fallout3_backup.exe"), TestPe.I386, ["d3d9.dll"], trailer: new byte[400_000]);

        var a = new GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Fallout 3 - Game of the Year Edition", dir, GameSource.Steam, "22370"));

        Assert.Equal("Fallout3.exe", Path.GetFileName(a.MainExe));
        Assert.Equal("Fallout 3", a.DbEntry?.Name);
        Assert.DoesNotContain(a.Warnings, w => w.Contains("2 GB"));
        Assert.Contains(a.Evidence, e => e.Contains("Large-Address-Aware"));
    }

    [Fact]
    public void NewVegasGetsDxvkRouteWithReducedModelToReach60()
    {
        // Gemessen (4K, 28.09.2026): 100 % ergab 44–48 fps – für 60 fps muss das Modell kleiner rechnen.
        using var t = new TempDir();
        var dir = NewVegasFolder(t);
        var a = new GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Fallout: New Vegas", dir, GameSource.Steam, "22380"));

        var rec = new DecisionEngine(_ => true, _ => true).Recommend(a, System5070Ti, new UserPreferences(OptimizationProfile.Balanced));

        Assert.False(rec.Blocked);
        Assert.Equal(60, rec.TargetFps);
        Assert.Equal(RouteId.LegacyDxvkFeeder, rec.Best?.Route.Id);
        Assert.True(rec.Best!.Config.NrScale < 1.0);
        Assert.Equal(FrameGenMode.Off, rec.Best.Config.FrameGen);
        Assert.Equal(60, rec.Best.Prediction.DisplayedFps);
        // dgVoodoo scheitert in Gamebryo – taucht auch nicht als Alternative auf.
        Assert.DoesNotContain(rec.Alternatives, c => c.Route.Id == RouteId.LegacyFeeder);
        Assert.Contains(rec.Notes, n => n.Contains("60 fps"));
    }

    [Fact]
    public void StreetFighterIvAt4kGetsHalfModelFor60AndTheHostIniSaysSo()
    {
        // Gemessen (4K, 60 Hz, 28.09.2026): 100 % → 20–23 ms, ≈ 38 fps (Zeitlupe); 50 % → 6,3 ms, stabile 60 fps.
        using var t = new TempDir();
        TestPe.Write(t.Combine("Ultra Street Fighter IV/SSFIV.exe"), TestPe.I386, ["kernel32.dll", "d3d9.dll"], trailer: new byte[200_000]);
        var (store, avail) = AllComponents(t);
        var system = new SystemInfo(new GpuInfo("NVIDIA GeForce RTX 5070 Ti", new Version(617, 14), 16L << 30), new DisplayInfo(3840, 2160, 60), HardwareSchedulingEnabled: true);
        var a = new GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Ultra Street Fighter IV", t.Combine("Ultra Street Fighter IV"), GameSource.Manual));

        var rec = new DecisionEngine(avail.IsAvailable, avail.CanAutoDownload).Recommend(a, system, new UserPreferences());

        Assert.Equal("Ultra Street Fighter IV", a.DbEntry?.Name);
        Assert.Equal(RouteId.LegacyDxvkFeeder, rec.Best?.Route.Id);
        Assert.Equal(0.5, rec.Best!.Config.NrScale);
        Assert.True(rec.Best.Prediction.RenderedFps >= 60);
        var full = new FrameTimeModel(system).Predict(rec.Best.Route, rec.Best.Config with { NrScale = 1.0 }, Bitness.X86, 100);
        // Gemessen ≈ 38 fps; ohne Messung rechnet das Modell mit 100 fps Grundbildrate (SF4 schafft mehr) – daher tiefer.
        Assert.InRange(full.RenderedFps, 20, 45);

        var plan = new RoutePlanner(avail, store).Plan(a, rec.Best);
        Assert.Contains(plan.Steps.OfType<IniSetStep>(), i => i.Target == Path.Combine("host64", "ReShade.ini") && i.Key == "NRResolutionScale" && i.Value == "0.5");
    }

    [Theory]
    [InlineData(false, "nvse_loader.exe")]
    [InlineData(true, "FalloutNV.exe")]
    public void NvseLoaderOnlyWithoutThe4GbPatch(bool patched, string expected)
    {
        // Der „FNV 4GB Patcher“ lässt FalloutNV.exe NVSE selbst laden – danach nicht mehr über nvse_loader.exe starten.
        using var t = new TempDir();
        var dir = NewVegasFolder(t, withEnb: false, largeAddressAware: patched);
        var a = new GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Fallout: New Vegas", dir, GameSource.Steam, "22380"));
        var manifest = new InstallManifest(1, DateTimeOffset.Now,
            new Configuration(RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9, SrMode.Native, 1, NrPlacement.PostUpscale, FrameGenMode.Off), [], [], [], []);

        Assert.EndsWith(expected, LaunchChooser.Choose(a, manifest).Exe);
    }

    [Fact]
    public void FullAddressSpaceIsNamedInsteadOfTranslatorCrash()
    {
        // New Vegas mit Mods, ohne 4GB-Patch (29.09.2026): DXVK findet keinen Adressraum mehr, danach Absturz in d3d9.dll.
        using var t = new TempDir();
        var dir = t.Dir("fnv");
        t.File("fnv/FalloutNV_d3d9.log", "info:  DXVK: v3.0.2\nerr:   D3D9: InitTexture: map failed\nerr:   DxvkMemoryAllocator: Memory allocation failed\n");
        var fnv = GameDatabase.LoadEmbedded().Entries.Single(e => e.Name == "Fallout: New Vegas");
        var manifest = new InstallManifest(1, DateTimeOffset.Now.AddMinutes(-5),
            new Configuration(RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9, SrMode.Native, 1, NrPlacement.PostUpscale, FrameGenMode.Off), [], [], [], []);

        var report = InstallDiagnostics.Evaluate(dir, manifest, "FalloutNV.exe", [new CrashInfo("d3d9.dll", "0xc0000005", DateTime.Now)], fnv);

        Assert.Equal(DiagnosticVerdict.NeedsAttention, report.Verdict);
        Assert.Null(report.SwitchTo);
        Assert.Contains("4GB", report.Summary);
        Assert.Contains(report.Checks, c => c.Title == InstallDiagnostics.AddressSpaceTitle && c.Status == DiagnosticStatus.Failed);
    }

    [Fact]
    public void ForeignReShadeAddonsAreSetAsideButOwnStay()
    {
        // Arkham Asylum: ein altes AutoHDR.addon32 im Binaries-Ordner ließ das Spiel mit ReShade abstürzen.
        using var t = new TempDir();
        TestPe.Write(t.Combine("Batman/Binaries/ShippingPC-BmGame.exe"), TestPe.I386, ["kernel32.dll", "d3d9.dll"], trailer: new byte[200_000]);
        t.File("Batman/Binaries/AutoHDR.addon32", "fremd");
        t.File("Batman/Binaries/dlss5-feed.addon32", "vom Tool (Reparieren)");
        t.File("Batman/Binaries/DisplayCommander.ini", "nur Einstellungen");
        var (store, avail) = AllComponents(t);
        var a = new GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Batman: Arkham Asylum GOTY Edition", t.Combine("Batman"), GameSource.Manual));
        var best = new DecisionEngine(avail.IsAvailable, avail.CanAutoDownload).Recommend(a, System5070Ti, new UserPreferences()).Best!;

        var plan = new RoutePlanner(avail, store).Plan(a, best);

        var removed = plan.Steps.OfType<RemoveFileStep>().Select(r => r.Target).ToList();
        Assert.Contains("AutoHDR.addon32", removed);
        Assert.DoesNotContain("dlss5-feed.addon32", removed);
        Assert.DoesNotContain("DisplayCommander.ini", removed);
        Assert.Contains(plan.Hints, h => h.Contains("AutoHDR.addon32") && h.Contains("Rückgängig"));
    }

    [Fact]
    public void CommandLineIsMergedAndOwnSwitchesWin()
    {
        var merged = CommandLineFile.Merge("-norestrictions -width 1920 -height 1080\r\n-fullspecaudio\r\n",
            CommandLineFile.Resolve(["-availablevidmem 3072.0", "-width {width}", "-height {height}", "-refreshrate {refresh}"], new DisplayInfo(3840, 2160, 60)));

        Assert.Equal("-norestrictions\r\n-width 3840\r\n-height 2160\r\n-fullspecaudio\r\n-availablevidmem 3072.0\r\n-refreshrate 60\r\n", merged);
        Assert.Equal(["-nomemrestrict"], CommandLineFile.Resolve(["-nomemrestrict", "-width {width}"], null));
    }

    [Fact]
    public void GtaIvGetsCommandLineForTheDisplayAndStrongStrengthInTheHost()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("Grand Theft Auto IV Complete Edition/GTAIV/GTAIV.exe"), TestPe.I386, ["kernel32.dll", "d3d9.dll"], trailer: new byte[200_000]);
        t.File("Grand Theft Auto IV Complete Edition/GTAIV/commandline.txt", "-fullspecaudio\r\n");
        var (store, avail) = AllComponents(t);
        var system = new SystemInfo(new GpuInfo("NVIDIA GeForce RTX 5070 Ti", new Version(617, 14), 16L << 30), new DisplayInfo(3840, 2160, 60), HardwareSchedulingEnabled: true);
        var a = new GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Grand Theft Auto IV Complete Edition", t.Combine("Grand Theft Auto IV Complete Edition"), GameSource.Manual));
        var best = new DecisionEngine(avail.IsAvailable, avail.CanAutoDownload).Recommend(a, system, new UserPreferences()).Best!;

        var strong = new RoutePlanner(avail, store) { Display = () => system.Display, Strength = () => NrStrength.Strong }.Plan(a, best);
        var standard = new RoutePlanner(avail, store) { Display = () => system.Display }.Plan(a, best);

        Assert.Equal("Grand Theft Auto IV", a.DbEntry?.Name);
        Assert.Equal(RouteId.LegacyDxvkFeeder, best.Route.Id);
        var cmd = Assert.Single(strong.Steps.OfType<WriteTextStep>(), w => w.Target == "commandline.txt");
        Assert.StartsWith("-fullspecaudio\r\n", cmd.Content); // eigener Schalter des Nutzers bleibt
        Assert.Contains("-width 3840\r\n-height 2160\r\n-refreshrate 60", cmd.Content);
        var host = Path.Combine("host64", "ReShade.ini");
        Assert.Contains(strong.Steps.OfType<IniSetStep>(), i => i.Target == host && i.Key == "NRIntensity" && i.Value == "2");
        Assert.Contains(strong.Steps.OfType<IniSetStep>(), i => i.Target == host && i.Key == "NRSkinStructure" && i.Value == "1");
        Assert.DoesNotContain(strong.Steps.OfType<IniSetStep>(), i => i.Key == "NRStyle" && i.Value == "2");
        Assert.Contains(standard.Steps.OfType<IniSetStep>(), i => i.Target == host && i.Key == "NRIntensity" && i.Value == "1");
    }

    [Fact]
    public void NewVegasPlanMatchesTestedSetup()
    {
        using var t = new TempDir();
        var dir = NewVegasFolder(t);
        t.File("Fallout New Vegas/dxgi.dll", "old reshade proxy");
        var (store, avail) = AllComponents(t);
        var a = new GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Fallout: New Vegas", dir, GameSource.Steam, "22380"));
        var best = new DecisionEngine(avail.IsAvailable, avail.CanAutoDownload).Recommend(a, System5070Ti, new UserPreferences()).Best!;

        var plan = new RoutePlanner(avail, store).Plan(a, best);

        var copies = plan.Steps.OfType<CopyFileStep>().ToList();
        var ini = plan.Steps.OfType<IniSetStep>().ToList();
        string host(string f) => Path.Combine("host64", f);

        // DXVK 32 Bit ersetzt die ENB-d3d9.dll; ReShade kommt als 32-Bit-Vulkan-Layer, nicht als dxgi.dll.
        Assert.Contains(copies, c => c.Target == "d3d9.dll" && File.ReadAllText(c.Source) == "DXVK-x32");
        var layer = Assert.Single(plan.Steps.OfType<RegisterVulkanLayerStep>());
        Assert.True(layer.Is32Bit);
        Assert.EndsWith("ReShade32.json", layer.LayerJson);
        Assert.DoesNotContain(copies, c => c.Target == "dxgi.dll");
        Assert.Contains(plan.Steps.OfType<RemoveFileStep>(), r => r.Target == "dxgi.dll");
        // Über den Layer kein LoadFromDllMain (sonst „No add-on was registered“).
        Assert.Contains(ini, i => i.Target == "ReShade.ini" && i.Key == "LoadFromDllMain" && i.Value == "");
        Assert.Contains(ini, i => i.Target == "ReShade.ini" && i.Key == "PreprocessorDefinitions" && i.Value.Contains("RESHADE_DEPTH_INPUT_IS_REVERSED=1"));

        // 32-Bit-Seite + 64-Bit-Hilfsprozess mit RenoDX.
        Assert.Contains(copies, c => c.Target == "dlss5-feed.addon32");
        Assert.Contains(copies, c => c.Target == host("dlss5-feed-host64.exe"));
        Assert.Contains(copies, c => c.Target == host("dxgi.dll") && c.Source.EndsWith("ReShade64.dll"));
        Assert.Contains(copies, c => c.Target == host("renodx-dlss5.addon64"));
        Assert.Contains(copies, c => c.Target == host("nvngx_dlssnr.dll"));
        Assert.Contains(copies, c => c.Target == host("nvngx_dlss.dll"));
        Assert.Contains(ini, i => i.Target == host("ReShade.ini") && i.Key == "NRStyle" && i.Value == "0");
        Assert.Contains(ini, i => i.Target == host("ReShade.ini") && i.Key == "EnableHooks" && i.Value == "2");
        Assert.Contains(ini, i => i.Target == host("ReShade.ini") && i.Key == "LoadFromDllMain" && i.Value == "renodx-dlss5.addon64");

        // Shader, Header, Preset und Feeder-Konfiguration.
        Assert.Contains(copies, c => c.Target == Path.Combine("reshade-shaders", "Shaders", "ReShade.fxh"));
        Assert.Contains(copies, c => c.Target == Path.Combine("reshade-shaders", "Shaders", "DrawText.fxh"));
        Assert.Contains(ini, i => i.Key == "Techniques" && i.Value == "Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx");
        foreach (var (key, value) in new[] { ("enabled", "1"), ("mode", "2"), ("reset_every", "0"), ("warmup_rebuild", "0"), ("rebuild", "0"), ("host_window", "1") })
            Assert.Contains(ini, i => i.Target == "dlss5-feed.cfg" && i.Key == key && i.Value == value);

        // MSAA aus in den Nutzer-Einstellungen, aber nur wenn die Datei existiert.
        var prefs = Assert.Single(ini, i => i.Target.StartsWith("%DOCUMENTS%"));
        Assert.True(prefs.OnlyIfExists);
        Assert.Equal(("Display", "iMultiSample", "0"), (prefs.Section, prefs.Key, prefs.Value));
        Assert.Contains(plan.Hints, h => h.Contains("ENB"));
    }

    [Fact]
    public void NewVegasInstallAndUninstallRoundTrip()
    {
        using var t = new TempDir();
        var dir = NewVegasFolder(t);
        var enbBytes = File.ReadAllBytes(Path.Combine(dir, "d3d9.dll"));
        var docs = t.Dir("Documents");
        t.File("Documents/My Games/FalloutNV/FalloutPrefs.ini", "[Display]\r\niSize W=2560\r\niMultiSample=4\r\n");
        var (store, avail) = AllComponents(t);
        var a = new GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Fallout: New Vegas", dir, GameSource.Steam, "22380"));
        var best = new DecisionEngine(avail.IsAvailable, avail.CanAutoDownload).Recommend(a, System5070Ti, new UserPreferences()).Best!;
        var plan = new RoutePlanner(avail, store).Plan(a, best);
        var layers = new List<(string Json, bool Is32)>();
        var installer = new Installer((json, is32) => layers.Add((json, is32)), token => token == "%DOCUMENTS%" ? docs : null);

        installer.Install(plan);

        Assert.Equal("DXVK-x32", File.ReadAllText(Path.Combine(dir, "d3d9.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "host64", "dlss5-feed-host64.exe")));
        var prefsPath = Path.Combine(docs, "My Games", "FalloutNV", "FalloutPrefs.ini");
        Assert.Equal("0", IniFile.Load(prefsPath).Get("Display", "iMultiSample"));
        Assert.Equal("2560", IniFile.Load(prefsPath).Get("Display", "iSize W"));
        Assert.Equal(("mode", "2"), ("mode", IniFile.Load(Path.Combine(dir, "dlss5-feed.cfg")).Get("", "mode")));
        Assert.Single(layers);
        Assert.True(layers[0].Is32);
        Assert.Empty(installer.Verify(dir));

        installer.Uninstall(dir);

        Assert.Equal(enbBytes, File.ReadAllBytes(Path.Combine(dir, "d3d9.dll")));
        Assert.Equal("4", IniFile.Load(prefsPath).Get("Display", "iMultiSample"));
        Assert.False(Directory.Exists(Path.Combine(dir, "host64")));
        Assert.False(File.Exists(Path.Combine(dir, "dlss5-feed.cfg")));
        Assert.False(Directory.Exists(Path.Combine(dir, "reshade-shaders")));
    }

    [Fact]
    public void MissingPrefsFileIsNotCreated()
    {
        using var t = new TempDir();
        var dir = NewVegasFolder(t, withEnb: false);
        var docs = t.Dir("Documents");
        var (store, avail) = AllComponents(t);
        var a = new GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Fallout: New Vegas", dir, GameSource.Steam, "22380"));
        var best = new DecisionEngine(avail.IsAvailable, avail.CanAutoDownload).Recommend(a, System5070Ti, new UserPreferences()).Best!;
        var plan = new RoutePlanner(avail, store).Plan(a, best);

        new Installer((_, _) => { }, _ => docs).Install(plan);

        Assert.False(File.Exists(Path.Combine(docs, "My Games", "FalloutNV", "FalloutPrefs.ini")));
        Assert.Equal("DXVK-x32", File.ReadAllText(Path.Combine(dir, "d3d9.dll")));
    }

    [Fact]
    public void DxvkTarGzIsExtracted()
    {
        using var t = new TempDir();
        t.File("pkg/dxvk-3.0.2/x32/d3d9.dll", "x32");
        t.File("pkg/dxvk-3.0.2/x64/d3d9.dll", "x64");
        var tarGz = t.Combine("dxvk-3.0.2.tar.gz");
        using (var fs = File.Create(tarGz))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            TarFile.CreateFromDirectory(t.Combine("pkg"), gz, includeBaseDirectory: false);
        var store = new ComponentStore(t.Combine("store"), ComponentCatalog.LoadEmbedded());

        store.Import(Ids.Dxvk, [tarGz], "v3.0.2");

        Assert.Equal("x32", File.ReadAllText(store.FindFile(Ids.Dxvk, "d3d9.dll", prefer32Bit: true)!));
        Assert.Equal("x64", File.ReadAllText(store.FindFile(Ids.Dxvk, "d3d9.dll")!));
    }
}

public class DiagnosticsTests
{
    private static (string Dir, InstallManifest Manifest) Installed(TempDir t, RouteId route, bool host = true)
    {
        var dir = t.Dir("game");
        if (host)
            t.Dir("game/host64");
        var manifest = new InstallManifest(1, DateTimeOffset.Now.AddMinutes(-5),
            new Configuration(route, GraphicsApi.D3D9, SrMode.Native, 1, NrPlacement.PostUpscale, FrameGenMode.Off), [], [], [], []);
        return (dir, manifest);
    }

    [Fact]
    public void NothingRunYetIsReportedAsSuch()
    {
        using var t = new TempDir();
        var (dir, m) = Installed(t, RouteId.LegacyDxvkFeeder);

        var checks = InstallDiagnostics.Check(dir, m, "FalloutNV.exe");

        Assert.All(checks, c => Assert.Equal(DiagnosticStatus.NotRunYet, c.Status));
    }

    [Fact]
    public void WorkingChainIsAllOk()
    {
        using var t = new TempDir();
        var (dir, m) = Installed(t, RouteId.LegacyDxvkFeeder);
        t.File("game/FalloutNV_d3d9.log", "info:  DXVK: v3.0.2\ninfo:  D3D9DeviceEx::D3D9DeviceEx");
        t.File("game/ReShade.log", "INFO | Initializing crosire's ReShade version '6.8.0'\nINFO | Loaded add-on \"DLSS 5 Feed\"");
        t.File("game/dlss5-feed.log", "config: mode=2\nshared set ready (Vulkan): 2560x1440\nframe 600 delivered\n"
                                       + "MV probe (centre 64x64, frame 600): mean |mv| 1.8 px, max 9.5 px, 71% non-zero\n"
                                       + "Depth probe (4x 32x32, frame 600): min 0.02, max 0.97, mean 0.41, variance 0.0612, 100% finite");
        t.File("game/host64/dlss5-feed-host.log", "feature ready: 2560x1440 DLAA\nframe 600 evaluated");
        t.File("game/host64/ReShade.log", "feature 18 created via the signed snippet\ninline feature 18 evaluation succeeded (count=60)");

        var checks = InstallDiagnostics.Check(dir, m, "FalloutNV.exe");

        Assert.Equal(7, checks.Count);
        Assert.All(checks, c => Assert.Equal(DiagnosticStatus.Ok, c.Status));
    }

    [Fact]
    public void KnownFailuresAreRecognized()
    {
        using var t = new TempDir();
        var (dir, m) = Installed(t, RouteId.LegacyDxvkFeeder);
        t.File("game/ReShade.log", "ERROR | No add-on was registered by 'dlss5-feed.addon32'. Unloading again ...");
        t.File("game/dlss5-feed.log", "config: mode=1 (transport-only)");
        t.File("game/host64/dlss5-feed-host.log", "starting");
        t.File("game/host64/ReShade.log", "feature 18 create failed with 0xbad00001");

        var checks = InstallDiagnostics.Check(dir, m, "FalloutNV.exe");

        Assert.Contains(checks, c => c.Title == "ReShade-Add-ons" && c.Status == DiagnosticStatus.Failed);
        Assert.Contains(checks, c => c.Title == "DLSS5-Feeder" && c.Status == DiagnosticStatus.Failed);
        Assert.Contains(checks, c => c.Title == "64-Bit-Hilfsprozess" && c.Status == DiagnosticStatus.Failed);
        Assert.Contains(checks, c => c.Title.StartsWith("DLSS 5") && c.Status == DiagnosticStatus.Failed && c.Detail.Contains("RTX 50"));
    }

    [Fact]
    public void WrapperBypassIsDetected()
    {
        using var t = new TempDir();
        var (dir, m) = Installed(t, RouteId.LegacyDxvkFeeder);
        t.File("game/ReShade.log", "INFO | Redirecting IDirect3D9::CreateDevice\nINFO | IDirect3DDevice9 created");

        var checks = InstallDiagnostics.Check(dir, m, "FalloutNV.exe");

        Assert.Contains(checks, c => c.Title == "ReShade" && c.Status == DiagnosticStatus.Failed && c.Detail.Contains("System-d3d9.dll"));
    }

    [Fact]
    public void LogsFromBeforeTheInstallAreIgnored()
    {
        using var t = new TempDir();
        var (dir, m) = Installed(t, RouteId.LegacyDxvkFeeder);
        var old = t.File("game/dlss5-feed.log", "frame 600 delivered");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));

        var checks = InstallDiagnostics.Check(dir, m, "FalloutNV.exe");

        Assert.Contains(checks, c => c.Title == "DLSS5-Feeder" && c.Status == DiagnosticStatus.NotRunYet);
    }
}
