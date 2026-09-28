using System.Text;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Tests;

/// <summary>Absturzabbilder des Feeders lesen und in der Diagnose die Ursache nennen (Fallout 3, 28.09.2026).</summary>
public class CrashDumpTests
{
    // Wie im echten Fallout-3-Abbild: Sprung an eine Adresse ohne Modul, ganz oben ruft das Steam-Overlay.
    private const ulong Game = 0x00E40000, Overlay = 0x64CB0000, Feeder = 0x71840000, Stack = 0x0FDBF000;

    private static byte[] X86Dump(ulong faultAddress, params uint[] stackWords)
    {
        var modules = new (ulong Base, uint Size, string Path)[]
        {
            (Game, 0x111E000, @"C:\Games\Fallout 3 goty\Fallout3ng.exe"),
            (Overlay, 0x175000, @"C:\Program Files (x86)\Steam\GameOverlayRenderer.dll"),
            (Feeder, 0x32000, @"C:\Games\Fallout 3 goty\dlss5-feed.addon32"),
        };
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        const int streams = 4, headerSize = 32, dirSize = streams * 12;
        w.Write(new byte[headerSize + dirSize]);

        uint sysRva = (uint)ms.Position;
        w.Write((ushort)0); // PROCESSOR_ARCHITECTURE_INTEL
        w.Write(new byte[54]);

        var nameRvas = new List<uint>();
        foreach (var m in modules)
        {
            nameRvas.Add((uint)ms.Position);
            var bytes = Encoding.Unicode.GetBytes(m.Path);
            w.Write((uint)bytes.Length);
            w.Write(bytes);
        }

        uint modRva = (uint)ms.Position;
        w.Write((uint)modules.Length);
        for (int i = 0; i < modules.Length; i++)
        {
            var start = ms.Position;
            w.Write(modules[i].Base);
            w.Write(modules[i].Size);
            w.Write(0u); // Prüfsumme
            w.Write(0u); // Zeitstempel
            w.Write(nameRvas[i]);
            w.Write(new byte[108 - (ms.Position - start)]);
        }

        uint stackRva = (uint)ms.Position;
        foreach (var word in stackWords)
            w.Write(word);
        uint stackSize = (uint)ms.Position - stackRva;

        uint memRva = (uint)ms.Position;
        w.Write(1u);
        w.Write(Stack);
        w.Write(stackSize);
        w.Write(stackRva);

        uint ctxRva = (uint)ms.Position;
        var context = new byte[0x2CC];
        BitConverter.GetBytes((uint)faultAddress).CopyTo(context, 0xB8); // Eip
        BitConverter.GetBytes((uint)Stack).CopyTo(context, 0xC4);        // Esp
        w.Write(context);

        uint exRva = (uint)ms.Position;
        w.Write(16716u); // Thread
        w.Write(0u);
        var record = new byte[152];
        BitConverter.GetBytes(0xC0000005u).CopyTo(record, 0);
        BitConverter.GetBytes(faultAddress).CopyTo(record, 16);
        w.Write(record);
        w.Write((uint)context.Length);
        w.Write(ctxRva);
        uint exSize = (uint)ms.Position - exRva;

        ms.Position = 0;
        w.Write(0x504D444Du);
        w.Write(0xA793u);
        w.Write((uint)streams);
        w.Write((uint)headerSize);
        ms.Position = headerSize;
        foreach (var (type, size, rva) in new[] { (7u, 56u, sysRva), (4u, stackRva - modRva, modRva), (5u, ctxRva - memRva, memRva), (6u, exSize, exRva) })
        {
            w.Write(type);
            w.Write(size);
            w.Write(rva);
        }
        return ms.ToArray();
    }

    private static readonly uint[] OverlayOnTop =
    [
        (uint)(Overlay + 0x8E3E4), 0, 0, (uint)(Overlay + 0xA44F4), 0x12345678,
        (uint)(Game + 0xF94F5A), (uint)(Feeder + 0x6A7B), (uint)(Feeder + 0x71AD),
    ];

    private static InstallManifest Manifest() =>
        new(1, DateTimeOffset.Now.AddMinutes(-5), new Configuration(RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9, SrMode.Native, 1, NrPlacement.PostUpscale, FrameGenMode.Off), [], [], [], []);

    private const string FeedLogWithException =
        "dlss5-feed32 1.17.0 commit 03710dd attached.\n"
        + "[feed32] effects: technique MISSING, DLSS5_MV MISSING\n"
        + "### EXCEPTION RECORDED ###  exception 0xC0000005 (executing address 65480650) at 65480650 in unknown; "
        + "this add-on was last doing: nothing yet -- no feed work has run in this process\n";

    [Fact]
    public void ReadsFaultAndCallersFromAnX86Dump()
    {
        var info = MiniDump.Read(X86Dump(0x65480650, OverlayOnTop))!;

        Assert.Equal("0xC0000005", info.CodeHex);
        Assert.Equal(0x65480650ul, info.Address);
        Assert.Null(info.FaultModule);
        Assert.Equal(["GameOverlayRenderer.dll", "Fallout3ng.exe", "dlss5-feed.addon32"], info.StackModules);
    }

    [Fact]
    public void NotADumpGivesNull()
    {
        Assert.Null(MiniDump.Read(Encoding.ASCII.GetBytes("kein Abbild, nur Text – aber lang genug für den Kopf")));
    }

    [Fact]
    public void SteamOverlayCrashIsNamedInsteadOfDepthAdvice()
    {
        using var t = new TempDir();
        var dir = t.Dir("game");
        t.File("game/ReShade.log", "INFO | Initializing");
        t.File("game/dlss5-feed.log", FeedLogWithException);
        File.WriteAllBytes(Path.Combine(dir, "dlss5-feed-crash.dmp"), X86Dump(0x65480650, OverlayOnTop));

        var report = InstallDiagnostics.Evaluate(dir, Manifest(), "Fallout3.exe", []);

        Assert.Equal(DiagnosticVerdict.NeedsAttention, report.Verdict);
        Assert.Contains("Steam-Overlay", report.Summary);
        Assert.Contains("bevor der Feeder", report.Summary);
        Assert.DoesNotContain(report.Checks, c => c.Detail.Contains("Tiefenpuffer prüfen"));
    }

    [Fact]
    public void CrashWithoutOverlayNamesTheCallers()
    {
        using var t = new TempDir();
        var dir = t.Dir("game");
        t.File("game/dlss5-feed.log", FeedLogWithException);
        File.WriteAllBytes(Path.Combine(dir, "dlss5-feed-crash.dmp"), X86Dump(Game + 0x100, (uint)(Game + 0x200), (uint)(Feeder + 0x10)));

        var check = InstallDiagnostics.Check(dir, Manifest(), "Fallout3.exe").Single(c => c.Title == InstallDiagnostics.CrashTitle);

        Assert.Equal(DiagnosticStatus.Failed, check.Status);
        Assert.Contains("Fallout3ng.exe", check.Detail);
        Assert.DoesNotContain("Overlay", check.Detail);
    }

    [Fact]
    public void DxvkLogOfTheRealGameExeCounts()
    {
        // Fallout3.exe startet Fallout3ng.exe; das Launcher-Log legt kein Gerät an.
        using var t = new TempDir();
        var dir = t.Dir("game");
        t.File("game/Fallout3Launcher_d3d9.log", "info:  Game: Fallout3Launcher.exe\ninfo:  DXVK: v3.1.1");
        t.File("game/Fallout3ng_d3d9.log", "info:  Game: Fallout3ng.exe\ninfo:  D3D9DeviceEx::ResetSwapChain:");

        var check = InstallDiagnostics.Check(dir, Manifest(), "Fallout3.exe").Single(c => c.Title.StartsWith("DXVK"));

        Assert.Equal(DiagnosticStatus.Ok, check.Status);
    }

    // ------------------------------------------------------------------ Start ohne Steam

    private static GameAnalysis Fallout3(TempDir t, GameSource source, bool withFose = false)
    {
        TestPe.Write(t.Combine("FO3/Fallout3.exe"), TestPe.I386, ["d3d9.dll"], largeAddressAware: true);
        TestPe.Write(t.Combine("FO3/Fallout3ng.exe"), TestPe.I386, ["d3d9.dll"], largeAddressAware: true);
        if (withFose)
            t.File("FO3/fose_loader.exe", "FOSE");
        return new Detection.GameAnalyzer(GameDatabase.LoadEmbedded()).Analyze(new GameInfo("Fallout 3 - Game of the Year Edition", t.Combine("FO3"), source, "22370"));
    }

    [Fact]
    public void SteamGameOnDxvkRouteStartsDirectly()
    {
        using var t = new TempDir();
        var a = Fallout3(t, GameSource.Steam);

        var choice = LaunchChooser.Choose(a, Manifest());

        Assert.EndsWith("Fallout3ng.exe", choice.Exe);
        Assert.Contains("Steam", choice.Reason);
        Assert.EndsWith("Fallout3ng.exe", LaunchChooser.GameProcessExe(a));
    }

    [Fact]
    public void WithoutInstallOrOutsideSteamTheStoreStartsTheGame()
    {
        using var t = new TempDir();
        var steam = Fallout3(t, GameSource.Steam);
        Assert.Null(LaunchChooser.Choose(steam, null).Exe);

        var native = new InstallManifest(1, DateTimeOffset.Now, new Configuration(RouteId.NativeDlss5, GraphicsApi.D3D12, SrMode.Native, 1, NrPlacement.PostUpscale, FrameGenMode.Off), [], [], [], []);
        Assert.Null(LaunchChooser.Choose(steam, native).Exe);
    }

    [Fact]
    public void ScriptExtenderStillWins()
    {
        using var t = new TempDir();
        var a = Fallout3(t, GameSource.Steam, withFose: true);

        Assert.EndsWith("fose_loader.exe", LaunchChooser.Choose(a, Manifest()).Exe);
    }

    [Fact]
    public void DxvkIsPinnedToTheTestedRelease()
    {
        var dxvk = Components.ComponentCatalog.LoadEmbedded().Get(RouteCatalog.Ids.Dxvk)!;
        Assert.Equal("v3.0.2", dxvk.Source.Tag);
    }

    [Fact]
    public void LauncherLogAloneIsNotEnough()
    {
        using var t = new TempDir();
        var dir = t.Dir("game");
        t.File("game/Fallout3Launcher_d3d9.log", "info:  Game: Fallout3Launcher.exe\ninfo:  DXVK: v3.1.1");

        var check = InstallDiagnostics.Check(dir, Manifest(), "Fallout3.exe").Single(c => c.Title.StartsWith("DXVK"));

        Assert.Equal(DiagnosticStatus.NotRunYet, check.Status);
    }
}
