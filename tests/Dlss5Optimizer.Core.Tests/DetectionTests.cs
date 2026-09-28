using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Library;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Tests;

public class PeFileTests
{
    [Fact]
    public void Reads64BitImportsAndVersion()
    {
        var bytes = TestPe.Build(TestPe.Amd64, ["KERNEL32.dll", "d3d12.dll", "dxgi.dll"], ["vulkan-1.dll"], new Version(310, 4, 0, 0));
        var pe = PeFile.TryRead(new MemoryStream(bytes));

        Assert.NotNull(pe);
        Assert.Equal(Bitness.X64, pe.Bitness);
        Assert.Equal(["kernel32.dll", "d3d12.dll", "dxgi.dll"], pe.Imports);
        Assert.Equal(["vulkan-1.dll"], pe.DelayImports);
        Assert.Equal(new Version(310, 4, 0, 0), pe.FileVersion);
    }

    [Fact]
    public void ReadsExportNames()
    {
        var pe = PeFile.TryRead(new MemoryStream(TestPe.Build(TestPe.I386, ["kernel32.dll"], exports: ["ReShadeRegisterAddon", "ReShadeUnregisterAddon"])));

        Assert.NotNull(pe);
        Assert.Equal(["ReShadeRegisterAddon", "ReShadeUnregisterAddon"], pe.Exports);
        Assert.Empty(PeFile.TryRead(new MemoryStream(TestPe.Build(TestPe.Amd64, ["kernel32.dll"])))!.Exports);
    }

    [Fact]
    public void Reads32BitWithVaStyleDelayImports()
    {
        var bytes = TestPe.Build(TestPe.I386, ["d3d9.dll"], ["ddraw.dll"], delayUsesVa: true);
        var pe = PeFile.TryRead(new MemoryStream(bytes));

        Assert.NotNull(pe);
        Assert.Equal(Bitness.X86, pe.Bitness);
        Assert.Contains("d3d9.dll", pe.AllImports);
        Assert.Contains("ddraw.dll", pe.AllImports);
        Assert.Null(pe.FileVersion);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { (byte)'M', (byte)'Z' })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    public void GarbageReturnsNull(byte[] bytes) => Assert.Null(PeFile.TryRead(new MemoryStream(bytes)));

    [Fact]
    public void TruncatedFileReturnsNull()
    {
        var bytes = TestPe.Build(TestPe.Amd64, ["d3d11.dll"]);
        Assert.Null(PeFile.TryRead(new MemoryStream(bytes[..0x150])));
    }
}

public class BinaryStringScannerTests
{
    [Fact]
    public void FindsAsciiAndUtf16AcrossChunkBoundaries()
    {
        var data = new byte[9 * 1024 * 1024];
        var ascii = "D3D12RHI"u8.ToArray();
        // Genau über die 4-MiB-Chunkgrenze legen.
        ascii.CopyTo(data, 4 * 1024 * 1024 - 3);
        var utf16 = System.Text.Encoding.Unicode.GetBytes("++UE5+Release");
        utf16.CopyTo(data, 8 * 1024 * 1024 + 11);

        var found = BinaryStringScanner.FindAny(new MemoryStream(data), ["D3D12RHI", "++UE5+Release", "VulkanRHI"], long.MaxValue);

        Assert.Contains("D3D12RHI", found);
        Assert.Contains("++UE5+Release", found);
        Assert.DoesNotContain("VulkanRHI", found);
    }
}

public class ExecutableLocatorTests
{
    [Fact]
    public void PrefersUnrealShippingExeOverStubAndHelpers()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("MyGame.exe"), TestPe.Amd64, ["kernel32.dll"]);
        TestPe.Write(t.Combine("MyGame/Binaries/Win64/MyGame-Win64-Shipping.exe"), TestPe.Amd64, ["d3d12.dll"]);
        TestPe.Write(t.Combine("Engine/Extras/Redist/en-us/UEPrereqSetup_x64.exe"), TestPe.Amd64, []);
        TestPe.Write(t.Combine("unins000.exe"), TestPe.I386, []);

        var exe = ExecutableLocator.FindMainExecutable(t.Path);

        Assert.EndsWith("MyGame-Win64-Shipping.exe", exe);
    }

    [Fact]
    public void UsesHintWhenPresent()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("bin/bg3.exe"), TestPe.Amd64, ["vulkan-1.dll"]);
        TestPe.Write(t.Combine("bin/bg3_dx11.exe"), TestPe.Amd64, ["d3d11.dll"]);

        Assert.EndsWith("bg3_dx11.exe", ExecutableLocator.FindMainExecutable(t.Path, "bin/bg3_dx11.exe"));
    }

    [Fact]
    public void UnityExeWithDataFolderWins()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("Game.exe"), TestPe.Amd64, []);
        t.Dir("Game_Data");
        TestPe.Write(t.Combine("CrashHandler.exe"), TestPe.Amd64, []);
        TestPe.Write(t.Combine("Tool.exe"), TestPe.Amd64, [], trailer: new byte[200_000]);

        Assert.EndsWith("Game.exe", ExecutableLocator.FindMainExecutable(t.Path));
    }
}

public class GameAnalyzerTests
{
    private static GameAnalyzer Analyzer() => new(GameDatabase.LoadEmbedded());

    [Fact]
    public void Dx12GameWithDlssAndAgilitySdk()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("Game.exe"), TestPe.Amd64, ["kernel32.dll", "d3d12.dll", "dxgi.dll", "d3d11.dll"]);
        TestPe.Write(t.Combine("D3D12/D3D12Core.dll"), TestPe.Amd64, []);
        TestPe.Write(t.Combine("nvngx_dlss.dll"), TestPe.Amd64, [], version: new Version(310, 4, 0, 0));
        TestPe.Write(t.Combine("nvngx_dlssg.dll"), TestPe.Amd64, [], version: new Version(310, 4, 0, 0));
        TestPe.Write(t.Combine("libxess.dll"), TestPe.Amd64, []);

        var a = Analyzer().Analyze(new GameInfo("Game", t.Path, GameSource.Manual));

        Assert.Equal(GraphicsApi.D3D12, a.Api.Primary);
        Assert.False(a.Api.Supported.HasFlag(GraphicsApi.D3D11), "d3d11.dll neben d3d12.dll ist kein DX11-Modus");
        Assert.Equal(Bitness.X64, a.Bitness);
        Assert.True(a.Upscalers.Has(UpscalerFeature.DlssSuperResolution));
        Assert.True(a.Upscalers.Has(UpscalerFeature.DlssFrameGeneration));
        Assert.True(a.Upscalers.Has(UpscalerFeature.Xess));
        Assert.Equal(new Version(310, 4, 0, 0), a.Upscalers.DlssVersion);
        Assert.False(a.AntiCheat.Detected);
    }

    [Fact]
    public void D3d9ImportForPixMarkersDoesNotMakeADx10GameDx9()
    {
        // Devil May Cry 4 SE: d3d10_1.dll für das Rendern, d3d9.dll nur für D3DPERF_*.
        using var t = new TempDir();
        TestPe.Write(t.Combine("DevilMayCry4SpecialEdition.exe"), TestPe.I386, ["kernel32.dll", "d3d9.dll", "d3d10_1.dll", "dxgi.dll"], largeAddressAware: true);

        var a = Analyzer().Analyze(new GameInfo("DMC4SE", t.Path, GameSource.Manual));

        Assert.Equal(GraphicsApi.D3D10, a.Api.Primary);
    }

    [Fact]
    public void UnrealMonolithicExeDetectedViaStrings()
    {
        using var t = new TempDir();
        var marker = System.Text.Encoding.Unicode.GetBytes("++UE5+Release-5.4")
            .Concat("D3D12RHI\0VulkanRHI\0d3d12.dll\0"u8.ToArray()).ToArray();
        TestPe.Write(t.Combine("Proj/Binaries/Win64/Proj-Win64-Shipping.exe"), TestPe.Amd64, ["kernel32.dll"], trailer: marker);
        t.Dir("Engine/Binaries/ThirdParty");

        var a = Analyzer().Analyze(new GameInfo("Proj", t.Path, GameSource.Manual));

        Assert.Equal(GameEngine.Unreal5, a.Engine);
        Assert.Equal(GraphicsApi.D3D12, a.Api.Primary);
        Assert.True(a.Api.Supported.HasFlag(GraphicsApi.Vulkan));
    }

    [Fact]
    public void LegacyDx9With32Bit()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("old.exe"), TestPe.I386, ["d3d9.dll", "dinput8.dll"]);

        var a = Analyzer().Analyze(new GameInfo("Old", t.Path, GameSource.Manual));

        Assert.Equal(GraphicsApi.D3D9, a.Api.Primary);
        Assert.Equal(Bitness.X86, a.Bitness);
        Assert.Contains(a.Warnings, w => w.Contains("32-Bit"));
    }

    [Fact]
    public void DetectsAntiCheatAndExistingMods()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("Game.exe"), TestPe.Amd64, ["d3d11.dll"]);
        t.File("EasyAntiCheat/Settings.json", "{}");
        t.File("ReShade.ini");
        t.File("OptiScaler.ini");
        t.File("renodx-dlss5.addon64");

        var a = Analyzer().Analyze(new GameInfo("Game", t.Path, GameSource.Manual));

        Assert.Contains("Easy Anti-Cheat", a.AntiCheat.Systems);
        Assert.True(a.Mods.HasFlag(ExistingMod.ReShade));
        Assert.True(a.Mods.HasFlag(ExistingMod.OptiScaler));
        Assert.True(a.Mods.HasFlag(ExistingMod.RenoDx));
    }

    [Fact]
    public void DxvkMeansEffectivelyVulkan()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("Game.exe"), TestPe.Amd64, ["d3d11.dll"]);
        TestPe.Write(t.Combine("d3d11.dll"), TestPe.Amd64, ["vulkan-1.dll"], trailer: "dxvk-2.6"u8.ToArray());

        var a = Analyzer().Analyze(new GameInfo("Game", t.Path, GameSource.Manual));

        Assert.True(a.Mods.HasFlag(ExistingMod.Dxvk));
        Assert.True(a.Api.Supported.HasFlag(GraphicsApi.Vulkan));
        Assert.Contains(a.Warnings, w => w.Contains("DXVK"));
    }

    [Fact]
    public void DatabaseExeVariantDecidesApi()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("bin/bg3.exe"), TestPe.Amd64, ["vulkan-1.dll", "d3d11.dll"]);
        TestPe.Write(t.Combine("bin/bg3_dx11.exe"), TestPe.Amd64, ["d3d11.dll"]);

        var a = Analyzer().Analyze(new GameInfo("Baldur's Gate 3", t.Path, GameSource.Steam, "1086940"));

        Assert.Equal("Baldur's Gate 3", a.DbEntry?.Name);
        Assert.EndsWith("bg3.exe", a.MainExe);
        Assert.Equal(GraphicsApi.Vulkan, a.Api.Primary);
        Assert.True(a.Api.Supported.HasFlag(GraphicsApi.D3D11));
    }

    [Fact]
    public void ProbeOverridesStaticGuess()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("Game.exe"), TestPe.Amd64, ["dxgi.dll"]);
        var a = Analyzer().Analyze(new GameInfo("Game", t.Path, GameSource.Manual));

        var probed = GameAnalyzer.ApplyProbe(a, GraphicsApi.D3D12, ["Testlauf"]);

        Assert.Equal(GraphicsApi.D3D12, probed.Api.Primary);
        Assert.True(probed.Api.FromProbe);
        Assert.Equal(1.0, probed.Api.Confidence);
    }
}

public class LibraryTests
{
    [Fact]
    public void VdfParsesNestedAndEscapedValues()
    {
        var root = VdfParser.Parse("""
            "libraryfolders"
            {
                // Kommentar
                "0"
                {
                    "path"  "C:\\Program Files (x86)\\Steam"
                    "apps" { "1091500" "123" }
                }
            }
            """);

        var folder = root.Child("libraryfolders")?.Child("0");
        Assert.Equal(@"C:\Program Files (x86)\Steam", folder?["path"]);
        Assert.Equal("123", folder?.Child("apps")?["1091500"]);
    }

    [Fact]
    public void SteamScannerFindsGamesInAllLibraries()
    {
        using var steam = new TempDir();
        using var lib2 = new TempDir();
        steam.File("steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steam.Path.Replace("\\", "\\\\")}}" }
                "1" { "path" "{{lib2.Path.Replace("\\", "\\\\")}}" }
            }
            """);
        steam.File("steamapps/appmanifest_1091500.acf", """
            "AppState" { "appid" "1091500" "name" "Cyberpunk 2077" "installdir" "Cyberpunk 2077" }
            """);
        steam.Dir("steamapps/common/Cyberpunk 2077");
        lib2.File("steamapps/appmanifest_228980.acf", """
            "AppState" { "appid" "228980" "name" "Steamworks Common Redistributables" "installdir" "Steamworks Shared" }
            """);
        lib2.Dir("steamapps/common/Steamworks Shared");
        lib2.File("steamapps/appmanifest_1086940.acf", """
            "AppState" { "appid" "1086940" "name" "Baldur's Gate 3" "installdir" "Baldurs Gate 3" }
            """);
        lib2.Dir("steamapps/common/Baldurs Gate 3");

        var games = new SteamLibraryScanner(steam.Path).Scan().ToList();

        Assert.Equal(2, games.Count);
        Assert.Contains(games, g => g.Name == "Cyberpunk 2077" && g.SourceId == "1091500");
        Assert.Contains(games, g => g.Name == "Baldur's Gate 3");
    }

    [Fact]
    public void EpicScannerReadsItemManifests()
    {
        using var t = new TempDir();
        var install = t.Dir("Games/Foo");
        t.File("Manifests/abc.item", $$"""
            { "DisplayName": "Foo", "InstallLocation": "{{install.Replace("\\", "\\\\")}}", "LaunchExecutable": "Foo.exe", "AppName": "foo" }
            """);
        t.File("Manifests/broken.item", "{ not json");

        var games = new EpicLibraryScanner(t.Combine("Manifests")).Scan().ToList();

        var g = Assert.Single(games);
        Assert.Equal("Foo", g.Name);
        Assert.EndsWith("Foo.exe", g.PreferredExe);
    }

    [Fact]
    public void FolderScannerTreatsSubfoldersAsGames()
    {
        using var t = new TempDir();
        TestPe.Write(t.Combine("A/a.exe"), TestPe.Amd64, []);
        TestPe.Write(t.Combine("B/Content/b.exe"), TestPe.Amd64, []);
        t.Dir("Empty");

        var games = new FolderLibraryScanner([t.Path]).Scan().OrderBy(g => g.Name).ToList();

        Assert.Equal(["A", "B"], games.Select(g => g.Name));
        Assert.EndsWith("Content", games[1].InstallDir);
    }
}
