using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dlss5Optimizer.Core.Models;

/// <summary>Eine API-Variante eines Spiels mit eigener EXE (z. B. Baldur's Gate 3: bg3.exe = Vulkan, bg3_dx11.exe = DX11).</summary>
public sealed record ApiExeVariant(GraphicsApi Api, string Exe);

/// <summary>
/// Änderung an einer Einstellungsdatei außerhalb des Spielordners. <see cref="File"/> beginnt mit
/// einem Platzhalter wie <c>%DOCUMENTS%</c>. Wird nur geändert, wenn die Datei schon existiert.
/// </summary>
public sealed record UserIniTweak(string File, string Section, string Key, string Value, string Reason);

/// <summary>
/// Wissen über einzelne Spiele, das sich nicht zuverlässig aus Dateien ablesen lässt.
/// Wird als JSON ausgeliefert und kann ohne neuen Build aktualisiert werden.
/// </summary>
public sealed record GameDbEntry
{
    public required string Name { get; init; }
    public string[] SteamAppIds { get; init; } = [];
    public string[] ExeNames { get; init; } = [];
    public string? NamePattern { get; init; }
    public bool NativeDlss5 { get; init; }
    public bool Dlss5Announced { get; init; }
    public GraphicsApi Apis { get; init; }
    public GraphicsApi DefaultApi { get; init; }
    public ApiExeVariant[] ApiExeVariants { get; init; } = [];
    public string? MainExe { get; init; }
    public string? PreferredRoute { get; init; }

    /// <summary>Routen, die in diesem Spiel nachweislich scheitern (z. B. dgVoodoo in Fallout 3/NV).</summary>
    public string[] ExcludedRoutes { get; init; } = [];

    /// <summary>Feste Bildratenbegrenzung der Engine (Gamebryo-Physik läuft nur bis 60 fps sauber).</summary>
    public int? FrameCapFps { get; init; }

    /// <summary>ReShade-Präprozessor-Definitionen (Tiefenpuffer), getestet für dieses Spiel.</summary>
    public string[] ReShadeDefines { get; init; } = [];

    /// <summary>Zusätzliche Schlüssel für dlss5-feed.cfg aus einer getesteten Konfiguration.</summary>
    public Dictionary<string, string> FeedConfig { get; init; } = [];

    public UserIniTweak[] UserIniTweaks { get; init; } = [];

    /// <summary>Starter, die bevorzugt werden, wenn vorhanden (Script Extender wie nvse_loader.exe).</summary>
    public string[] LaunchExes { get; init; } = [];

    /// <summary>
    /// Die eigentliche Spiel-EXE hinter einem Launcher (Fallout 3: Fallout3.exe → Fallout3ng.exe). Sie wird
    /// gestartet, wenn das Spiel ohne Steam laufen muss, und beim Messen gesucht.
    /// </summary>
    public string? DirectExe { get; init; }

    /// <summary>
    /// Weitere Ordner (relativ zur EXE), in die der Übersetzer zusätzlich muss – Source-Spiele laden
    /// shaderapidx9.dll aus bin\ und damit auch die d3d9.dll von dort.
    /// </summary>
    public string[] WrapperExtraDirs { get; init; } = [];

    /// <summary>Hinweis für 32-Bit-Spiele ohne Large-Address-Aware-Flag (welcher 4GB-Patch passt).</summary>
    public string? LargeAddressHint { get; init; }

    public string[] Notes { get; init; } = [];

    public bool Excludes(string routeId) => ExcludedRoutes.Any(r => r.Equals(routeId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Anti-Cheat nur im Online-Modus; Offline-Start ist bekannt und dokumentiert.</summary>
    public bool AntiCheatOfflineBypass { get; init; }
}

public sealed class GameDatabase
{
    private readonly List<GameDbEntry> _entries;

    public GameDatabase(IEnumerable<GameDbEntry> entries) => _entries = entries.ToList();

    public IReadOnlyList<GameDbEntry> Entries => _entries;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static GameDatabase LoadEmbedded() =>
        Load(EmbeddedData.Open("games.json"));

    public static GameDatabase Load(Stream json)
    {
        var doc = JsonSerializer.Deserialize<GameDbFile>(json, JsonOptions)
                  ?? throw new InvalidDataException("games.json ist leer");
        return new GameDatabase(doc.Games);
    }

    /// <summary>Lädt die mitgelieferte DB und überlagert sie mit einer lokalen Datei (neuere Einträge gewinnen).</summary>
    public static GameDatabase LoadWithOverride(string? overridePath)
    {
        var db = LoadEmbedded();
        if (overridePath is null || !File.Exists(overridePath))
            return db;
        using var fs = File.OpenRead(overridePath);
        var extra = Load(fs);
        var merged = extra.Entries.ToList();
        merged.AddRange(db.Entries.Where(e => !extra.Entries.Any(x => x.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase))));
        return new GameDatabase(merged);
    }

    public GameDbEntry? Find(GameInfo game, string? mainExe)
    {
        if (game.Source == GameSource.Steam && game.SourceId is { } appId)
        {
            var bySteam = _entries.FirstOrDefault(e => e.SteamAppIds.Contains(appId));
            if (bySteam is not null)
                return bySteam;
        }

        var exeName = mainExe is null ? null : Path.GetFileName(mainExe);
        if (exeName is not null)
        {
            var byExe = _entries.FirstOrDefault(e => e.ExeNames.Any(x => x.Equals(exeName, StringComparison.OrdinalIgnoreCase)));
            if (byExe is not null)
                return byExe;
        }

        return _entries.FirstOrDefault(e =>
            e.NamePattern is { } p && Regex.IsMatch(game.Name, p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    /// <summary>
    /// Sucht unter allen EXE-Dateien eines Spielordners eine, die die Datenbank kennt – für Spiele,
    /// deren eigentliche EXE klein ist (Far Cry: 32-KB-Starter neben dem großen Editor).
    /// </summary>
    public (GameDbEntry Entry, string Exe)? FindByAnyExe(IEnumerable<string> exePaths)
    {
        foreach (var exe in exePaths)
        {
            var name = Path.GetFileName(exe);
            var entry = _entries.FirstOrDefault(e => e.ExeNames.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)));
            if (entry is not null)
                return (entry, exe);
        }
        return null;
    }

    private sealed record GameDbFile(List<GameDbEntry> Games);
}

internal static class EmbeddedData
{
    public static Stream Open(string fileName) =>
        typeof(EmbeddedData).Assembly.GetManifestResourceStream($"Dlss5Optimizer.Core.Data.{fileName}")
        ?? throw new FileNotFoundException($"Eingebettete Ressource fehlt: {fileName}");
}
