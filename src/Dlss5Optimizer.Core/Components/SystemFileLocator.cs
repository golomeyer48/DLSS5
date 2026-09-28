using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Models;
using RouteIds = Dlss5Optimizer.Core.Decision.RouteCatalog.Ids;

namespace Dlss5Optimizer.Core.Components;

/// <summary>
/// Findet Dateien, die nicht heruntergeladen werden müssen, weil sie schon auf dem PC liegen:
/// das DLSS-5-Modell im NVIDIA-Treiber und die DLSS-Laufzeit in installierten Spielen.
/// </summary>
public static class SystemFileLocator
{
    public const string DlssNrFile = "nvngx_dlssnr.dll";
    public const string DlssFile = "nvngx_dlss.dll";

    /// <summary>Standardpfad des Windows-Treiberspeichers.</summary>
    public static string DefaultDriverStore =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");

    /// <summary>
    /// Sucht nvngx_dlssnr.dll in den NVIDIA-Treiberpaketen (nv*.inf_amd64_*). Bei mehreren
    /// installierten Treibern gewinnt die höchste Dateiversion, dann das neueste Datum.
    /// Stand 617.14 liefert NVIDIA das Modell nicht mit – die Suche bleibt für spätere Treiber;
    /// ein Fund zählt nur, wenn er zur hinterlegten Prüfsumme passt (siehe AppServices).
    /// </summary>
    public static string? FindDlssNrModel(string? driverStoreRoot = null)
    {
        var root = driverStoreRoot ?? DefaultDriverStore;
        if (!Directory.Exists(root))
            return null;
        try
        {
            return Directory.EnumerateDirectories(root, "nv*")
                .Select(d => Path.Combine(d, DlssNrFile))
                .Where(File.Exists)
                .Select(p => (Path: p, Version: PeFile.TryRead(p)?.FileVersion, Time: File.GetLastWriteTimeUtc(p)))
                .OrderByDescending(x => x.Version)
                .ThenByDescending(x => x.Time)
                .Select(x => x.Path)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Neueste nvngx_dlss.dll aus den analysierten Spielen.</summary>
    public static (string Path, Version? Version)? FindNewestDlssRuntime(IEnumerable<GameAnalysis> games) =>
        games.Where(g => g.Upscalers.DlssPath is not null && File.Exists(g.Upscalers.DlssPath))
            .Select(g => (Path: g.Upscalers.DlssPath!, Version: g.Upscalers.DlssVersion))
            .OrderByDescending(x => x.Version)
            .Cast<(string, Version?)?>()
            .FirstOrDefault();
}

/// <summary>Beantwortet für die Entscheidungs-Engine: Liegt eine Komponente vor, kann sie geladen werden?</summary>
public sealed class ComponentAvailability(ComponentCatalog catalog, ComponentStore store, Func<string?> dlssNrModel, Func<string?> dlssRuntime)
{
    public bool IsAvailable(string id)
    {
        var def = catalog.Get(id);
        if (def is null)
            return false;
        // Schon auf dem PC vorhanden (Treiber, Spiel) – sonst aus dem Speicher.
        if (id.Equals(RouteIds.DlssNrModel, StringComparison.OrdinalIgnoreCase) && dlssNrModel() is not null)
            return true;
        if (def.Source.Type == ComponentSourceType.GameLibrary && dlssRuntime() is not null)
            return true;
        return store.IsAvailable(id);
    }

    public bool CanAutoDownload(string id) => catalog.Get(id)?.CanAutoDownload == true;

    /// <summary>Pfad zur Datei einer Komponente, egal ob aus Speicher, Treiber oder Spiel.</summary>
    public string? ResolveFile(string id, string fileName, bool prefer32Bit = false)
    {
        var def = catalog.Get(id);
        if (id.Equals(RouteIds.DlssNrModel, StringComparison.OrdinalIgnoreCase) && dlssNrModel() is { } nr && Path.GetFileName(nr).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            return nr;
        if (def?.Source.Type == ComponentSourceType.GameLibrary && dlssRuntime() is { } rt && Path.GetFileName(rt).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            return rt;
        return store.FindFile(id, fileName, prefer32Bit);
    }
}
