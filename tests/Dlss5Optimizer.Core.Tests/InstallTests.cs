using Dlss5Optimizer.Core.Detection;
using System.IO.Compression;
using System.Text.Json;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Models;
using Ids = Dlss5Optimizer.Core.Decision.RouteCatalog.Ids;

namespace Dlss5Optimizer.Core.Tests;

public class IniFileTests
{
    [Fact]
    public void SetKeepsCommentsAndOtherKeys()
    {
        var ini = IniFile.Parse("; Kommentar\n[GENERAL]\nFoo=1\n\n[ADDON]\nBar=2\n");
        ini.Set("GENERAL", "EffectSearchPaths", @".\reshade-shaders\Shaders\**");
        ini.Set("ADDON", "Bar", "3");
        ini.Set("NEW", "Key", "v");

        var text = ini.ToString();
        Assert.StartsWith("; Kommentar", text);
        Assert.Equal("1", ini.Get("GENERAL", "Foo"));
        Assert.Equal(@".\reshade-shaders\Shaders\**", ini.Get("general", "effectsearchpaths"));
        Assert.Equal("3", ini.Get("ADDON", "Bar"));
        Assert.Equal("v", ini.Get("NEW", "Key"));
        // Neuer Schlüssel steht in seiner Sektion, nicht hinter der nächsten.
        Assert.True(text.IndexOf("EffectSearchPaths", StringComparison.Ordinal) < text.IndexOf("[ADDON]", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalKeysGoBeforeFirstSection()
    {
        var ini = IniFile.Parse("[DLSS5_Feed.fx]\nMV_SCALE=1\n");
        ini.Set("", "Techniques", "A@a.fx,B@b.fx");

        Assert.StartsWith("Techniques=A@a.fx,B@b.fx", ini.ToString());
        Assert.Equal("A@a.fx,B@b.fx", ini.Get("", "Techniques"));
        Assert.Equal("1", ini.Get("DLSS5_Feed.fx", "MV_SCALE"));
    }

    [Fact]
    public void HandlesSpacesAroundEquals()
    {
        var ini = IniFile.Parse("[General]\nOutputAPI            = bestavailable\n");
        Assert.Equal("bestavailable", ini.Get("General", "OutputAPI"));
        ini.Set("General", "OutputAPI", "d3d11_fl11_0");
        Assert.Equal("d3d11_fl11_0", ini.Get("General", "OutputAPI"));
        Assert.DoesNotContain("bestavailable", ini.ToString());
    }
}

public class InstallerTests
{
    [Fact]
    public void InstallBacksUpAndUninstallRestores()
    {
        using var game = new TempDir();
        using var src = new TempDir();
        game.File("dxgi.dll", "ORIGINAL");
        game.File("ReShade.ini", "[GENERAL]\nKeep=1\n");
        var newDll = src.File("OptiScaler.dll", "OPTI");
        var plan = new InstallPlan(
            new Configuration(RouteId.OptiScalerNr, GraphicsApi.D3D12, SrMode.Quality, 1, NrPlacement.PostUpscale, FrameGenMode.Off),
            game.Path,
            [
                new CopyFileStep(newDll, "dxgi.dll", "OptiScaler"),
                new CopyFileStep(newDll, "OptiScaler/sub/x.dll", "Unterordner"),
                new IniSetStep("ReShade.ini", "ADDON", "AddonPath", @".\", "ini"),
                new WriteTextStep("dlss5-bridge.cfg", "# dlss5-bridge keep\n", "cfg"),
                new ManualStep("Im Spiel einschalten"),
            ],
            [], ["OptiScaler.log"]);

        var installer = new Installer();
        var manifest = installer.Install(plan);

        Assert.Equal("OPTI", File.ReadAllText(game.Combine("dxgi.dll")));
        Assert.True(File.Exists(game.Combine("OptiScaler/sub/x.dll")));
        Assert.Contains("AddonPath", File.ReadAllText(game.Combine("ReShade.ini")));
        Assert.Contains(manifest.Files, f => f.RelativePath == "dxgi.dll" && f.Existed);
        Assert.Empty(installer.Verify(game.Path));

        game.File("OptiScaler.log", "log");   // Laufzeitdatei des Mods
        installer.Uninstall(game.Path);

        Assert.Equal("ORIGINAL", File.ReadAllText(game.Combine("dxgi.dll")));
        Assert.Equal("[GENERAL]\nKeep=1\n", File.ReadAllText(game.Combine("ReShade.ini")));
        Assert.False(File.Exists(game.Combine("dlss5-bridge.cfg")));
        Assert.False(Directory.Exists(game.Combine("OptiScaler")));
        Assert.False(File.Exists(game.Combine("OptiScaler.log")));
        Assert.False(Directory.Exists(Installer.StateDir(game.Path)));
    }

    [Fact]
    public void FailedInstallRollsBack()
    {
        using var game = new TempDir();
        using var src = new TempDir();
        game.File("dxgi.dll", "ORIGINAL");
        var plan = new InstallPlan(
            new Configuration(RouteId.OptiScalerNr, GraphicsApi.D3D12, SrMode.Quality, 1, NrPlacement.PostUpscale, FrameGenMode.Off),
            game.Path,
            [
                new CopyFileStep(src.File("a.dll", "NEW"), "dxgi.dll", "ok"),
                new CopyFileStep(src.Combine("fehlt.dll"), "b.dll", "schlägt fehl"),
            ],
            [], []);

        Assert.ThrowsAny<IOException>(() => new Installer().Install(plan));

        Assert.Equal("ORIGINAL", File.ReadAllText(game.Combine("dxgi.dll")));
        Assert.False(Directory.Exists(Installer.StateDir(game.Path)));
    }

    [Fact]
    public void VerifyDetectsGameUpdateOverwritingFiles()
    {
        using var game = new TempDir();
        using var src = new TempDir();
        var plan = new InstallPlan(
            new Configuration(RouteId.OptiScalerNr, GraphicsApi.D3D12, SrMode.Quality, 1, NrPlacement.PostUpscale, FrameGenMode.Off),
            game.Path, [new CopyFileStep(src.File("a.dll", "MOD"), "dxgi.dll", "mod")], [], []);
        var installer = new Installer();
        installer.Install(plan);

        game.File("dxgi.dll", "SPIEL-UPDATE");

        Assert.Contains(installer.Verify(game.Path), p => p.Contains("dxgi.dll"));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("sub/../../evil.dll")]
    public void RejectsTargetsOutsideGameFolder(string target)
    {
        using var game = new TempDir();
        Assert.Throws<InvalidOperationException>(() => Installer.SafePath(game.Path, target));
    }
}

public class ComponentTests
{
    private static ComponentCatalog Catalog() => ComponentCatalog.LoadEmbedded();

    [Fact]
    public void EmbeddedCatalogCoversAllRouteComponents()
    {
        var catalog = Catalog();
        var ids = RouteCatalog.All.SelectMany(r => r.Components.SelectMany(c => c.AnyOf))
            .Concat(RouteCatalog.All.Select(r => r.PreUpscaleComponent).OfType<string>())
            .Append(Ids.PresentMon)
            .Distinct();
        Assert.All(ids, id => Assert.NotNull(catalog.Get(id)));
        Assert.False(catalog.Get(Ids.DeepFriedChicken)!.CanAutoDownload, "DFC darf laut Lizenz nicht automatisch geladen werden");
        Assert.True(catalog.Get(Ids.DlssRuntime)!.CanAutoDownload);
    }

    [Fact]
    public void ImportExtractsZipAndValidatesExpectedFiles()
    {
        using var t = new TempDir();
        var zipPath = t.Combine("OptiScaler-DLSSNR-v0.2.0.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntry("OptiScaler-DLSSNR/OptiScaler.dll").Open().Dispose();
            using (var w = new StreamWriter(zip.CreateEntry("OptiScaler-DLSSNR/OptiScaler.ini").Open()))
                w.Write("[DlssNr]\nEnabled=auto\n");
        }
        var store = new ComponentStore(t.Combine("store"), Catalog());

        var record = store.Import(Ids.OptiScalerNr, [zipPath], "v0.2.0");

        Assert.Equal("v0.2.0", record.Version);
        Assert.True(store.IsAvailable(Ids.OptiScalerNr));
        Assert.EndsWith("OptiScaler.ini", store.FindFile(Ids.OptiScalerNr, "optiscaler.ini"));
        Assert.Empty(store.VerifyIntegrity(Ids.OptiScalerNr));
    }

    [Fact]
    public void ImportRejectsWrongFilesAndKeepsPreviousVersion()
    {
        using var t = new TempDir();
        var store = new ComponentStore(t.Combine("store"), Catalog());
        store.Import(Ids.DeepFriedChicken, [t.File("deep-fried-chicken.addon64", "a"), t.File("deep-fried-chicken-nvngx.dll", "b")]);

        var ex = Assert.Throws<InvalidDataException>(() => store.Import(Ids.DeepFriedChicken, [t.File("random.txt", "x")]));

        Assert.Contains("deep-fried-chicken.addon64", ex.Message);
        Assert.NotNull(store.FindFile(Ids.DeepFriedChicken, "deep-fried-chicken.addon64"));
    }

    [Fact]
    public void ZipSlipIsBlocked()
    {
        using var t = new TempDir();
        var zipPath = t.Combine("evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            zip.CreateEntry("../../evil.dll").Open().Dispose();

        Assert.Throws<InvalidDataException>(() => ComponentStore.ExtractArchive(zipPath, t.Dir("out")));
        Assert.False(File.Exists(t.Combine("evil.dll")));
    }

    [Fact]
    public void ExtractsDllsFromInstallerWithAppendedZip()
    {
        using var t = new TempDir();
        var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("ReShade64.dll").Open()))
                w.Write("RS64");
            zip.CreateEntry("ReShade32.dll").Open().Dispose();
        }
        var exe = t.Combine("ReShade_Setup_6.8.0_Addon.exe");
        File.WriteAllBytes(exe, [.. TestPe.Build(TestPe.Amd64, ["kernel32.dll"]), .. new byte[5000], .. zipBytes.ToArray()]);

        var files = ComponentStore.ExtractFromSelfExtractingZip(exe, t.Dir("out"), ["ReShade64.dll"]);

        var f = Assert.Single(files);
        Assert.Equal("RS64", File.ReadAllText(f));
    }

    [Fact]
    public void SelectsNewestMatchingReleaseAsset()
    {
        var json = JsonDocument.Parse("""
            [
              { "tag_name": "dlssnr-310.8", "draft": false, "prerelease": false, "assets": [ { "name": "nvngx_dlssnr.zip", "browser_download_url": "u0", "size": 1 } ] },
              { "tag_name": "renodx-dlss5-8.5.0-rc10", "draft": false, "prerelease": true, "assets": [ { "name": "renodx-dlss5_8.5.0-rc10.zip", "browser_download_url": "u1", "size": 1, "digest": "sha256:ABC" } ] },
              { "tag_name": "renodx-dlss5-6.5.3", "draft": false, "prerelease": false, "assets": [ { "name": "renodx-dlss5_6.5.3.zip", "browser_download_url": "u2", "size": 1 } ] }
            ]
            """);

        var pre = ComponentDownloader.SelectAsset(json.RootElement, "^renodx-dlss5.*\\.zip$", includePrerelease: true, tagPattern: "^renodx-dlss5-\\d");
        var stable = ComponentDownloader.SelectAsset(json.RootElement, "^renodx-dlss5.*\\.zip$", includePrerelease: false, tagPattern: "^renodx-dlss5-\\d");

        Assert.Equal("u1", pre?.Url);
        Assert.Equal("ABC", pre?.Sha256);
        Assert.Equal("u2", stable?.Url);
    }

    [Fact]
    public void FindsNewestModelInDriverStore()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("nv_dispi.inf_amd64_aaa/nvngx_dlssnr.dll"), TestPe.Amd64, [], version: new Version(310, 7, 0, 0));
        TestPe.Write(t.Combine("nv_dispi.inf_amd64_bbb/nvngx_dlssnr.dll"), TestPe.Amd64, [], version: new Version(310, 8, 0, 0));
        TestPe.Write(t.Combine("iigd_dch.inf_amd64_ccc/nvngx_dlssnr.dll"), TestPe.Amd64, [], version: new Version(999, 0, 0, 0));

        var found = SystemFileLocator.FindDlssNrModel(t.Path);

        Assert.Contains("bbb", found);
    }
}

public class RoutePlannerTests
{
    private static GameAnalysis Game(string exeDir, GraphicsApi api, UpscalerFeature up, Bitness bitness = Bitness.X64) =>
        new(new GameInfo("Test", exeDir, GameSource.Manual), Path.Combine(exeDir, "game.exe"), bitness, GameEngine.Unknown,
            new ApiDetection(api, api, 1, new Dictionary<GraphicsApi, double>()),
            new UpscalerInfo(up, null, null, null), ExistingMod.None, AntiCheatInfo.None, null, [], []);

    private static (ComponentStore Store, ComponentAvailability Avail) Setup(TempDir t, params (string Id, string[] Files)[] comps)
    {
        var catalog = ComponentCatalog.LoadEmbedded();
        var store = new ComponentStore(t.Combine("store"), catalog);
        foreach (var (id, files) in comps)
            store.Import(id, files.Select(f => t.File(Path.Combine("src", id, f), f)).ToList());
        var model = t.File("driver/nvngx_dlssnr.dll", "MODEL");
        var runtime = t.File("game2/nvngx_dlss.dll", "DLSS");
        return (store, new ComponentAvailability(catalog, store, () => model, () => runtime));
    }

    private static Candidate Candidate(RouteId route, GraphicsApi api, NrPlacement placement = NrPlacement.PostUpscale, double scale = 1.0) =>
        new(RouteCatalog.Get(route), new Configuration(route, api, SrMode.Quality, scale, placement, FrameGenMode.Off),
            new Prediction(100, 100, 5, 90, true), [], [], []);

    [Fact]
    public void OptiScalerPlanRenamesProxyAndEnablesPass()
    {
        using var t = new TempDir();
        var (store, avail) = Setup(t, (Ids.OptiScalerNr, ["OptiScaler.dll", "OptiScaler.ini", "nvngx.dll_dlssnr.dll", "setup_windows.bat"]));
        var gameDir = t.Dir("game");

        var plan = new RoutePlanner(avail, store).Plan(Game(gameDir, GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution), Candidate(RouteId.OptiScalerNr, GraphicsApi.D3D12, scale: 0.75));

        var copies = plan.Steps.OfType<CopyFileStep>().ToList();
        Assert.Contains(copies, c => c.Target == "dxgi.dll" && c.Source.EndsWith("OptiScaler.dll"));
        Assert.Contains(copies, c => c.Target == "nvngx.dll_dlssnr.dll");
        Assert.Contains(copies, c => c.Target == "nvngx_dlssnr.dll");
        Assert.DoesNotContain(copies, c => c.Target.Contains("setup_windows"));
        var ini = plan.Steps.OfType<IniSetStep>().ToList();
        Assert.Contains(ini, i => i.Section == "DlssNr" && i.Key == "Enabled" && i.Value == "true");
        Assert.Contains(ini, i => i.Key == "WorkingScale" && i.Value == "0.75");
        Assert.Contains(ini, i => i.Key == "Dx12Upscaler" && i.Value == "dlss");
    }

    [Fact]
    public void OptiScalerUsesWinmmNextToForeignDxgi()
    {
        using var t = new TempDir();
        var (store, avail) = Setup(t, (Ids.OptiScalerNr, ["OptiScaler.dll", "OptiScaler.ini"]));
        var gameDir = t.Dir("game");
        t.File("game/dxgi.dll", "reshade");

        var plan = new RoutePlanner(avail, store).Plan(Game(gameDir, GraphicsApi.D3D11, UpscalerFeature.Fsr), Candidate(RouteId.OptiScalerNr, GraphicsApi.D3D11));

        Assert.Contains(plan.Steps.OfType<CopyFileStep>(), c => c.Target == "winmm.dll");
        Assert.Contains(plan.Steps.OfType<IniSetStep>(), i => i.Key == "Dx11Upscaler" && i.Value == "dlss_12");
        Assert.Contains(plan.Hints, h => h.Contains("FSR"));
    }

    [Fact]
    public void FeederPlanWritesPresetInCorrectOrderAndKeepsUserTechniques()
    {
        using var t = new TempDir();
        var (store, avail) = Setup(t,
            (Ids.ReShade, ["ReShade64.dll", "ReShade32.dll"]),
            (Ids.Feeder, ["dlss5-feed.addon64", "DLSS5_Feed.fx"]),
            (Ids.LumeniteFx, ["lumenite_Kernel.fx", "lumenite_bluenoise256.png"]),
            (Ids.ReShadeHeaders, ["ReShade.fxh", "ReShadeUI.fxh", "DrawText.fxh"]),
            (Ids.DeepFriedChicken, ["deep-fried-chicken.addon64", "deep-fried-chicken-nvngx.dll"]));
        var gameDir = t.Dir("game");
        t.File("game/ReShadePreset.ini", "Techniques=Clarity@Clarity.fx,DLSS5_Feed@DLSS5_Feed.fx\n");

        var plan = new RoutePlanner(avail, store).Plan(Game(gameDir, GraphicsApi.D3D11, UpscalerFeature.None), Candidate(RouteId.Feeder, GraphicsApi.D3D11, scale: 0.75));

        var ini = plan.Steps.OfType<IniSetStep>().ToList();
        Assert.Contains(ini, i => i.Target == "ReShadePreset.ini" && i.Key == "Techniques"
                                  && i.Value == "Clarity@Clarity.fx,Lumenite_Kernel@lumenite_Kernel.fx,DLSS5_Feed@DLSS5_Feed.fx");
        Assert.Contains(ini, i => i.Section == "DLSS5_Feed.fx" && i.Value == "DLSS5_MV_PROVIDER=3");
        Assert.Contains(ini, i => i.Target == "dlss5-feed.cfg" && i.Key == "work_resolution" && i.Value == "75");
        Assert.Contains(ini, i => i.Key == "LoadFromDllMain");
        var copies = plan.Steps.OfType<CopyFileStep>().ToList();
        Assert.Contains(copies, c => c.Target == "dxgi.dll" && c.Source.EndsWith("ReShade64.dll"));
        Assert.Contains(copies, c => c.Target == Path.Combine("reshade-shaders", "Shaders", "DLSS5_Feed.fx"));
        Assert.Contains(copies, c => c.Target == "nvngx_dlss.dll");
        Assert.Contains(copies, c => c.Target == Path.Combine("reshade-shaders", "Shaders", "ReShade.fxh"));
        Assert.Contains(ini, i => i.Target == "dlss5-feed.cfg" && i.Key == "mode" && i.Value == "2");
        Assert.Contains(ini, i => i.Target == "dlss5-feed.cfg" && i.Key == "reset_every" && i.Value == "0");
    }

    [Fact]
    public void Feeder32BitUsesHost64Folder()
    {
        using var t = new TempDir();
        var (store, avail) = Setup(t,
            (Ids.ReShade, ["ReShade64.dll", "ReShade32.dll"]),
            (Ids.Feeder, ["dlss5-feed.addon64", "dlss5-feed.addon32", "DLSS5_Feed.fx", "dlss5-feed-host64.exe"]),
            (Ids.LumeniteFx, ["lumenite_Kernel.fx"]),
            (Ids.ReShadeHeaders, ["ReShade.fxh", "ReShadeUI.fxh"]),
            (Ids.RenoDx, ["renodx-dlss5.addon64"]));
        var gameDir = t.Dir("game");

        var plan = new RoutePlanner(avail, store).Plan(Game(gameDir, GraphicsApi.D3D11, UpscalerFeature.None, Bitness.X86), Candidate(RouteId.Feeder, GraphicsApi.D3D11));

        var copies = plan.Steps.OfType<CopyFileStep>().ToList();
        Assert.Contains(copies, c => c.Target == "dxgi.dll" && c.Source.EndsWith("ReShade32.dll"));
        Assert.Contains(copies, c => c.Target == "dlss5-feed.addon32");
        Assert.Contains(copies, c => c.Target == Path.Combine("host64", "dxgi.dll") && c.Source.EndsWith("ReShade64.dll"));
        Assert.Contains(copies, c => c.Target == Path.Combine("host64", "renodx-dlss5.addon64"));
        Assert.Contains(copies, c => c.Target == Path.Combine("host64", "nvngx_dlssnr.dll"));
        var ini = plan.Steps.OfType<IniSetStep>().ToList();
        Assert.Contains(ini, i => i.Target == Path.Combine("host64", "ReShade.ini") && i.Key == "NRStyle" && i.Value == "0");
    }

    [Fact]
    public void MissingComponentsAreReported()
    {
        using var t = new TempDir();
        var (store, avail) = Setup(t);
        var gameDir = t.Dir("game");

        var ex = Assert.Throws<PlanException>(() => new RoutePlanner(avail, store).Plan(
            Game(gameDir, GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution), Candidate(RouteId.OptiScalerNr, GraphicsApi.D3D12)));

        Assert.Contains(Ids.OptiScalerNr, ex.MissingComponents);
    }

    [Fact]
    public void PlanCanBeInstalledAndRolledBack()
    {
        using var t = new TempDir();
        var (store, avail) = Setup(t, (Ids.OptiScalerNr, ["OptiScaler.dll", "OptiScaler.ini"]));
        var gameDir = t.Dir("game");
        var plan = new RoutePlanner(avail, store).Plan(Game(gameDir, GraphicsApi.D3D12, UpscalerFeature.DlssSuperResolution), Candidate(RouteId.OptiScalerNr, GraphicsApi.D3D12));

        var installer = new Installer();
        installer.Install(plan);
        var optiIni = IniFile.Load(Path.Combine(gameDir, "OptiScaler.ini"));
        Assert.Equal("true", optiIni.Get("DlssNr", "Enabled"));
        Assert.Equal("MODEL", File.ReadAllText(Path.Combine(gameDir, "nvngx_dlssnr.dll")));

        installer.Uninstall(gameDir);
        Assert.Empty(Directory.EnumerateFileSystemEntries(gameDir));
    }
}

/// <summary>ReShade-Vulkan-Layer wie das offizielle Setup ablegen – ohne eine gute Fassung zu zerstören.</summary>
public class ReShadeLayerTests
{
    private static readonly string[] AddonExports = ["ReShadeRegisterAddon", "ReShadeUnregisterAddon"];

    private static string StoreLayer(TempDir t, Version version)
    {
        TestPe.Write(t.Combine("store/ReShade32.dll"), TestPe.I386, ["kernel32.dll"], version: version, exports: AddonExports);
        return t.File("store/ReShade32.json", "{\"layer\":{\"library_path\":\".\\\\ReShade32.dll\"}}");
    }

    [Fact]
    public void FreshInstallCopiesDllAndManifest()
    {
        using var t = new TempDir();
        var json = StoreLayer(t, new Version(6, 8, 0, 0));

        var r = ReShadeLayer.Deploy(json, is32Bit: true, t.Combine("pd"));

        Assert.Equal(ReShadeLayer.DeployAction.Installed, r.Action);
        Assert.Equal(t.Combine("pd/ReShade32.json"), r.ManifestPath);
        Assert.True(File.Exists(t.Combine("pd/ReShade32.dll")));
    }

    [Fact]
    public void LayerWithoutAddonSupportIsBackedUpAndReplaced()
    {
        using var t = new TempDir();
        var json = StoreLayer(t, new Version(6, 8, 0, 0));
        TestPe.Write(t.Combine("pd/ReShade32.dll"), TestPe.I386, ["kernel32.dll"], version: new Version(6, 9, 0, 0)); // neuer, aber ohne Add-ons
        var original = File.ReadAllBytes(t.Combine("pd/ReShade32.dll"));

        var r = ReShadeLayer.Deploy(json, is32Bit: true, t.Combine("pd"));

        Assert.Equal(ReShadeLayer.DeployAction.ReplacedWithoutAddons, r.Action);
        Assert.Equal(original, File.ReadAllBytes(t.Combine("pd/ReShade32.dll.bak")));
        Assert.True(ReShadeLayer.HasAddonSupport(PeFile.TryRead(t.Combine("pd/ReShade32.dll"))!));
    }

    [Fact]
    public void AddonLayerOfSameOrNewerVersionIsKept()
    {
        using var t = new TempDir();
        var json = StoreLayer(t, new Version(6, 8, 0, 0));
        TestPe.Write(t.Combine("pd/ReShade32.dll"), TestPe.I386, ["kernel32.dll"], version: new Version(6, 9, 0, 0), exports: AddonExports);
        var theirs = File.ReadAllBytes(t.Combine("pd/ReShade32.dll"));

        var r = ReShadeLayer.Deploy(json, is32Bit: true, t.Combine("pd"));

        Assert.Equal(ReShadeLayer.DeployAction.KeptExisting, r.Action);
        Assert.Equal(theirs, File.ReadAllBytes(t.Combine("pd/ReShade32.dll")));
        Assert.True(File.Exists(t.Combine("pd/ReShade32.json")), "fehlendes Manifest wird ergänzt");
    }

    [Fact]
    public void OlderAddonLayerIsReplaced()
    {
        using var t = new TempDir();
        var json = StoreLayer(t, new Version(6, 8, 0, 0));
        TestPe.Write(t.Combine("pd/ReShade32.dll"), TestPe.I386, ["kernel32.dll"], version: new Version(6, 1, 0, 0), exports: AddonExports);

        Assert.Equal(ReShadeLayer.DeployAction.ReplacedOlder, ReShadeLayer.Deploy(json, is32Bit: true, t.Combine("pd")).Action);
    }
}

public class RenoDxVersionTests
{
    [Theory]
    [InlineData("renodx-dlss5-8.0.1", 8)]
    [InlineData("renodx-dlss5-6.5.3", 6)]
    [InlineData("renodx-dlss5_7.0.0-rc8.zip", 7)]
    [InlineData("v10.2", 10)]
    [InlineData("importiert", null)]
    [InlineData(null, null)]
    public void MajorVersionIgnoresTheFiveInDlss5(string? version, int? major) =>
        Assert.Equal(major, RoutePlanner.RenoDxMajorVersion(version));
}
