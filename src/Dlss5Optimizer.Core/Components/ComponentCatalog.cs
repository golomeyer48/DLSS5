using System.Text.Json;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Components;

public enum ComponentSourceType
{
    /// <summary>Neueste Version aus den GitHub-Releases eines Repos.</summary>
    GitHub,
    /// <summary>Feste Download-URL.</summary>
    Url,
    /// <summary>Nicht öffentlich verfügbar: Der Nutzer importiert die Datei(en) selbst.</summary>
    Import,
    /// <summary>Liegt im installierten NVIDIA-Treiber (DriverStore).</summary>
    DriverStore,
    /// <summary>Wird aus einem installierten Spiel übernommen (z. B. nvngx_dlss.dll).</summary>
    GameLibrary,
}

public sealed record ComponentSource
{
    public ComponentSourceType Type { get; init; }
    public string? Repo { get; init; }
    public string? AssetPattern { get; init; }
    /// <summary>Nur Releases, deren Tag passt (Repos mit mehreren Produkten, z. B. rhi-repo).</summary>
    public string? TagPattern { get; init; }
    public bool IncludePrerelease { get; init; }
    public string? Url { get; init; }
    public string? FileName { get; init; }
    /// <summary>Wo Nutzer die Datei bekommen (nur Anzeige), z. B. "RenoDX-Discord, Kanal #DLSS5".</summary>
    public string? Distribution { get; init; }
}

public sealed record ComponentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string License { get; init; } = "unbekannt";
    public bool ClosedSource { get; init; }
    public string? Homepage { get; init; }
    public required ComponentSource Source { get; init; }

    /// <summary>
    /// Dateien, die nach dem Import vorhanden sein müssen (Dateiname oder Muster mit *).
    /// Jede Zeile ist eine Alternative-Liste, getrennt durch "|".
    /// </summary>
    public string[] ExpectedFiles { get; init; } = [];

    /// <summary>Dateien, die aus einem Installer mit angehängtem ZIP geholt werden (ReShade-Setup).</summary>
    public string[] ExtractFromInstaller { get; init; } = [];
    public string[] Notes { get; init; } = [];

    /// <summary>
    /// Automatisch ladbar: GitHub/URL, oder Treiber-/Spieldateien mit hinterlegter Ersatz-URL.
    /// </summary>
    public bool CanAutoDownload => Source.Type is ComponentSourceType.GitHub or ComponentSourceType.Url
                                   || Source.Url is not null;
}

public sealed class ComponentCatalog
{
    public IReadOnlyList<ComponentDefinition> Components { get; }

    public ComponentCatalog(IEnumerable<ComponentDefinition> components) => Components = components.ToList();

    public ComponentDefinition? Get(string id) => Components.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static ComponentCatalog LoadEmbedded()
    {
        using var s = EmbeddedData.Open("components.json");
        return Load(s);
    }

    public static ComponentCatalog Load(Stream json)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(json, GameDatabase.JsonOptions)
                   ?? throw new InvalidDataException("components.json ist leer");
        return new ComponentCatalog(file.Components);
    }

    private sealed record CatalogFile(List<ComponentDefinition> Components);
}
