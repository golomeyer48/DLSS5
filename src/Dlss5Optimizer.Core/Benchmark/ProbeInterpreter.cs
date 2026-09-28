using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Benchmark;

public sealed record ProbeResult(GraphicsApi Api, bool DlssLoaded, bool FrameGenLoaded, IReadOnlyList<string> Evidence);

/// <summary>
/// Bestimmt die tatsächlich benutzte Grafik-API aus einem Testlauf: welche DLLs der Prozess
/// geladen hat und welche Present-Runtime PresentMon meldet.
///
/// Stolperfallen: Viele DX12-Spiele laden zusätzlich d3d11.dll (Videos, Overlays); DXVK-Spiele
/// laden d3d11.dll *und* vulkan-1.dll; Vulkan kann unter Windows über DXGI präsentieren.
/// Deshalb zählen vor allem die Laufzeit-Kerne (D3D12Core.dll) und die Treiber-Module.
/// </summary>
public static class ProbeInterpreter
{
    // NVIDIA-Treiber-Module (User-Mode-Treiber) je API.
    private const string NvD3D9 = "nvd3dumx.dll";
    private const string NvD3D9x86 = "nvd3dum.dll";
    private const string NvOpenGlVulkan = "nvoglv64.dll";
    private const string NvOpenGlVulkanX86 = "nvoglv32.dll";

    public static ProbeResult Interpret(IEnumerable<string> loadedModules, string? presentRuntime)
    {
        var mods = new HashSet<string>(loadedModules.Select(m => Path.GetFileName(m).ToLowerInvariant()));
        var evidence = new List<string>();
        bool Has(string m) => mods.Contains(m);

        GraphicsApi api = GraphicsApi.None;
        var runtime = presentRuntime?.Trim().ToUpperInvariant();
        bool nvGlVk = Has(NvOpenGlVulkan) || Has(NvOpenGlVulkanX86);

        if (Has("d3d12core.dll"))
        {
            api = GraphicsApi.D3D12;
            evidence.Add("Testlauf: D3D12Core.dll geladen → DirectX 12");
        }
        else if (Has("vulkan-1.dll") && (nvGlVk || runtime is "OTHER" or "VULKAN"))
        {
            api = GraphicsApi.Vulkan;
            evidence.Add(Has("d3d11.dll") || Has("d3d9.dll")
                ? "Testlauf: vulkan-1.dll + Direct3D-DLL → DXVK-Übersetzung, effektiv Vulkan"
                : "Testlauf: vulkan-1.dll + NVIDIA-Vulkan-Treiber → Vulkan");
        }
        else if (runtime == "D3D9" || Has("d3d9.dll") && (Has(NvD3D9) || Has(NvD3D9x86)))
        {
            api = GraphicsApi.D3D9;
            evidence.Add("Testlauf: Direct3D 9 präsentiert");
        }
        else if (Has("opengl32.dll") && nvGlVk)
        {
            api = GraphicsApi.OpenGL;
            evidence.Add("Testlauf: opengl32.dll + NVIDIA-OpenGL-Treiber → OpenGL");
        }
        else if (Has("d3d11.dll") && (runtime is null or "DXGI"))
        {
            api = GraphicsApi.D3D11;
            evidence.Add("Testlauf: d3d11.dll, kein D3D12Core → DirectX 11");
        }
        else if (Has("d3d10.dll") || Has("d3d10_1.dll"))
        {
            api = GraphicsApi.D3D10;
            evidence.Add("Testlauf: DirectX 10");
        }
        else if (Has("d3d8.dll"))
        {
            api = GraphicsApi.D3D8;
            evidence.Add("Testlauf: DirectX 8");
        }
        else if (Has("ddraw.dll"))
        {
            api = GraphicsApi.DirectDraw;
            evidence.Add("Testlauf: DirectDraw");
        }

        bool dlss = Has("nvngx_dlss.dll");
        bool fg = Has("nvngx_dlssg.dll") || Has("sl.dlss_g.dll");
        if (dlss)
            evidence.Add("Testlauf: DLSS Super Resolution ist aktiv geladen");
        if (fg)
            evidence.Add("Testlauf: DLSS Frame Generation ist geladen");
        return new ProbeResult(api, dlss, fg, evidence);
    }
}
