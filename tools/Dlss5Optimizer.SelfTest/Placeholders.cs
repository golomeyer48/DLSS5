using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Tests;
using Ids = Dlss5Optimizer.Core.Decision.RouteCatalog.Ids;

namespace Dlss5Optimizer.SelfTest;

/// <summary>
/// Platzhalter für das, was nicht geladen werden darf oder kann (Deep Fried Chicken, DLSS-5-Modell aus
/// dem Treiber), und – mit --synthetic – nachgebaute Pakete für alle Komponenten (ohne Internet).
/// Die Platzhalter sind gültige PE-Dateien der richtigen Architektur, aber ohne Funktion.
/// </summary>
public static class Placeholders
{
    private static readonly string[] AddonExports = ["ReShadeRegisterAddon", "ReShadeUnregisterAddon"];

    public static void EnsureDriverModel(ComponentStore store)
    {
        if (!store.IsAvailable(Ids.DlssNrModel))
            Import(store, Ids.DlssNrModel, "Platzhalter", dir => Pe64(dir, "nvngx_dlssnr.dll"));
    }

    public static void EnsureDeepFriedChicken(ComponentStore store)
    {
        if (!store.IsAvailable(Ids.DeepFriedChicken))
            Import(store, Ids.DeepFriedChicken, "Platzhalter", dir =>
            {
                Pe64(dir, "deep-fried-chicken.addon64");
                Pe64(dir, "deep-fried-chicken-nvngx.dll");
            });
    }

    /// <summary>Baut für jede fehlende Komponente ein Paket im Aufbau des echten Archivs nach.</summary>
    public static void FillSynthetic(ComponentStore store, Report report)
    {
        report.Section("Komponenten (nachgebaut, ohne Internet)");
        var builders = new Dictionary<string, Action<string>>
        {
            [Ids.ReShade] = dir =>
            {
                Pe32(dir, "ReShade32.dll", AddonExports, new Version(6, 8, 0, 0));
                Pe64(dir, "ReShade64.dll", AddonExports, new Version(6, 8, 0, 0));
                foreach (var bits in new[] { "32", "64" })
                    Text(dir, $"ReShade{bits}.json",
                        "{\"file_format_version\":\"1.0.0\",\"layer\":{\"name\":\"VK_LAYER_reshade\",\"type\":\"GLOBAL\",\"library_path\":\".\\\\ReShade" + bits + ".dll\",\"api_version\":\"1.3.0\",\"implementation_version\":\"1\"}}");
            },
            [Ids.OptiScalerNr] = dir => OptiScaler(Path.Combine(dir, "OptiScaler-DLSSNR")),
            [Ids.OptiScalerPreUpscale] = dir => OptiScaler(Path.Combine(dir, "OptiScaler-NR")),
            [Ids.Bridge] = dir => Pe64(dir, "dlss5-bridge.addon64"),
            [Ids.Feeder] = dir =>
            {
                Pe64(dir, "dlss5-feed.addon64");
                Pe32(dir, "dlss5-feed.addon32");
                Pe64(dir, "host64/dlss5-feed-host64.exe");
                Text(dir, "shaders/DLSS5_Feed.fx", "#include \"ReShade.fxh\"\n");
            },
            [Ids.LumeniteFx] = dir =>
            {
                Text(dir, "LumeniteFX-mainline/Shaders/lumenite_Kernel.fx", "#include \"ReShade.fxh\"\n");
                Text(dir, "LumeniteFX-mainline/Shaders/include/lumenite_Helpers.fxh", "// helpers\n");
                Text(dir, "LumeniteFX-mainline/Textures/lumenite_bluenoise256.png", "png");
                Text(dir, "LumeniteFX-mainline/README.md", "readme");
            },
            [Ids.RenoDx] = dir => Pe64(dir, "renodx-dlss5.addon64"),
            [Ids.Dxvk] = dir =>
            {
                foreach (var dll in new[] { "d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll" })
                {
                    Pe32(dir, $"dxvk-2.7.1/x32/{dll}");
                    Pe64(dir, $"dxvk-2.7.1/x64/{dll}");
                }
            },
            [Ids.D3D8To9] = dir => Pe32(dir, "d3d8.dll"),
            [Ids.ReShadeHeaders] = dir =>
            {
                foreach (var h in new[] { "ReShade.fxh", "ReShadeUI.fxh", "DrawText.fxh" })
                    Text(dir, $"reshade-shaders-slim/Shaders/{h}", "// header\n");
            },
            [Ids.DgVoodoo] = dir =>
            {
                foreach (var dll in new[] { "D3D9.dll", "D3D8.dll", "DDraw.dll", "D3DImm.dll" })
                {
                    Pe32(dir, $"MS/x86/{dll}");
                    Pe64(dir, $"MS/x64/{dll}");
                }
                Pe32(dir, "dgVoodooCpl.exe");
                Text(dir, "dgVoodoo.conf", "Version = 0x286\r\n\r\n[General]\r\n\r\nOutputAPI = bestavailable\r\nAdapters = all\r\n\r\n[DirectX]\r\n\r\nDisableAndPassThru = false\r\nVideoCard = internal3D\r\nVRAM = 256\r\ndgVoodooWatermark = true\r\n");
            },
            [Ids.DlssRuntime] = dir => Pe64(dir, "nvngx_dlss.dll", version: new Version(310, 9, 1, 0)),
            [Ids.PresentMon] = dir => Pe64(dir, "PresentMon-2.3.1-x64.exe"),
        };
        foreach (var (id, build) in builders)
        {
            if (store.IsAvailable(id))
                continue;
            Import(store, id, "nachgebaut", build);
            var problems = ComponentCheck.CheckNeeds(store, id).ToList();
            report.Check(problems.Count == 0, id, string.Join("; ", problems), "nachgebautes Paket");
        }
    }

    private static void OptiScaler(string dir)
    {
        Pe64(dir, "OptiScaler.dll");
        Pe64(dir, "libxess.dll");
        Pe64(dir, "amd_fidelityfx_dx12.dll");
        Text(dir, "OptiScaler.ini", "[Upscalers]\r\nDx12Upscaler=auto\r\n\r\n[DlssNr]\r\nEnabled=false\r\n");
        Text(dir, "setup_windows.bat", "@echo off\r\n");
        Text(dir, "!! EXTRACT ALL FILES TO GAME FOLDER !!.md", "readme");
    }

    private static void Import(ComponentStore store, string id, string version, Action<string> build)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "dlss5-placeholder-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            build(tmp);
            store.Import(id, Directory.EnumerateFileSystemEntries(tmp).ToList(), version, "Selbsttest");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    private static void Pe32(string dir, string rel, string[]? exports = null, Version? version = null) =>
        TestPe.Write(Path.Combine(dir, rel), TestPe.I386, ["kernel32.dll"], version: version, exports: exports);

    private static void Pe64(string dir, string rel, string[]? exports = null, Version? version = null) =>
        TestPe.Write(Path.Combine(dir, rel), TestPe.Amd64, ["kernel32.dll"], version: version, exports: exports);

    private static void Text(string dir, string rel, string content)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }
}
