namespace Dlss5Optimizer.Core.Models;

/// <summary>Grafik-APIs, die ein Spiel verwenden kann. Flags, weil viele Spiele mehrere anbieten.</summary>
[Flags]
public enum GraphicsApi
{
    None = 0,
    DirectDraw = 1 << 0,
    D3D8 = 1 << 1,
    D3D9 = 1 << 2,
    D3D10 = 1 << 3,
    D3D11 = 1 << 4,
    D3D12 = 1 << 5,
    Vulkan = 1 << 6,
    OpenGL = 1 << 7,
}

public enum Bitness
{
    Unknown,
    X86,
    X64,
    Arm64,
}

public enum GameEngine
{
    Unknown,
    Unreal4,
    Unreal5,
    Unreal,
    Unity,
    REEngine,
    Source2,
    IdTech,
    CryEngine,
}

public static class GraphicsApiExtensions
{
    public static IEnumerable<GraphicsApi> Each(this GraphicsApi flags)
    {
        foreach (GraphicsApi api in Enum.GetValues<GraphicsApi>())
        {
            if (api != GraphicsApi.None && flags.HasFlag(api))
                yield return api;
        }
    }

    public static string DisplayName(this GraphicsApi api) => api switch
    {
        GraphicsApi.DirectDraw => "DirectDraw",
        GraphicsApi.D3D8 => "DirectX 8",
        GraphicsApi.D3D9 => "DirectX 9",
        GraphicsApi.D3D10 => "DirectX 10",
        GraphicsApi.D3D11 => "DirectX 11",
        GraphicsApi.D3D12 => "DirectX 12",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        GraphicsApi.None => "Unbekannt",
        _ => string.Join(" / ", api.Each().Select(DisplayName)),
    };

    /// <summary>APIs, die DirectX 9 oder älter sind und über dgVoodoo2 nach D3D11 übersetzt werden müssen.</summary>
    public static bool IsLegacy(this GraphicsApi api) =>
        api is GraphicsApi.DirectDraw or GraphicsApi.D3D8 or GraphicsApi.D3D9;

    public static string DisplayName(this GameEngine engine) => engine switch
    {
        GameEngine.Unreal4 => "Unreal Engine 4",
        GameEngine.Unreal5 => "Unreal Engine 5",
        GameEngine.Unreal => "Unreal Engine",
        GameEngine.Unity => "Unity",
        GameEngine.REEngine => "RE Engine",
        GameEngine.Source2 => "Source 2",
        GameEngine.IdTech => "id Tech",
        GameEngine.CryEngine => "CryEngine",
        _ => "Unbekannt",
    };
}
