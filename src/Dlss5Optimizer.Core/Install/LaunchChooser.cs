using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Install;

/// <summary>Wie ein Spiel gestartet wird: direkt über eine EXE oder über seinen Store (Steam, Epic).</summary>
/// <param name="Exe">Direkt zu startende EXE, oder null für den Start über den Store.</param>
public sealed record LaunchChoice(string? Exe, string? Reason)
{
    public static LaunchChoice ViaStore { get; } = new(null, null);
}

public static class LaunchChooser
{
    /// <summary>
    /// Script Extender (fose_loader.exe …) starten immer direkt. Steam-Spiele, in denen ReShade als Vulkan-Layer läuft
    /// (DXVK-Routen), ebenfalls: Über Steam lädt das Spiel GameOverlayRenderer.dll und Steams Vulkan-Layer – auch mit
    /// ausgeschaltetem Overlay –, und Fallout 3 stürzte so viermal beim ersten Bild ab; direkt gestartet lief DLSS 5
    /// (28.09.2026). Steam darf dabei laufen.
    /// </summary>
    public static LaunchChoice Choose(GameAnalysis game, InstallManifest? installed)
    {
        if (game.GameDir is not { } dir)
            return LaunchChoice.ViaStore;
        var db = game.DbEntry;
        bool patched = db?.LoaderNotNeededWhenPatched == true && game.MainExe is { } main && Detection.PeFile.TryRead(main)?.LargeAddressAware == true;
        if (!patched && db?.LaunchExes.Select(l => Path.Combine(dir, l)).FirstOrDefault(File.Exists) is { } loader)
            return new(loader, "Script Extender");

        if (installed is null || game.Game.Source != GameSource.Steam || !UsesVulkanLayer(installed.Config))
            return LaunchChoice.ViaStore;
        var direct = db?.DirectExe is { } name && File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : game.MainExe;
        return direct is null
            ? LaunchChoice.ViaStore
            : new(direct, "direkt ohne Steam-Overlay und Steams Vulkan-Layer (sonst Absturz beim ersten Bild)");
    }

    /// <summary>Die EXE, deren Prozess das eigentliche Spiel ist (zum Messen).</summary>
    public static string? GameProcessExe(GameAnalysis game) =>
        game.GameDir is { } dir && game.DbEntry?.DirectExe is { } name && File.Exists(Path.Combine(dir, name))
            ? Path.Combine(dir, name)
            : game.MainExe;

    /// <summary>Wie im Routenplaner: DXVK macht aus DirectX 9 Vulkan, ReShade läuft dann als Vulkan-Layer.</summary>
    public static bool UsesVulkanLayer(Configuration config) => config.Api == GraphicsApi.Vulkan || config.Route.UsesDxvk();
}
