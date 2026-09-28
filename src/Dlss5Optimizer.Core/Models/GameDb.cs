using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dlss5Optimizer.Core.Models;

/// <summary>Eine API-Variante eines Spiels mit eigener EXE (z. B. Baldur's Gate 3: bg3.exe = Vulkan, bg3_dx11.exe = DX11).</summary>
public sealed record ApiExeVariant(GraphicsApi Api, string Exe);

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
    public string[] Notes { get; init; } = [];

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

    private sealed record GameDbFile(List<GameDbEntry> Games);
}

internal static class EmbeddedData
{
    public static Stream Open(string fileName) =>
        typeof(EmbeddedData).Assembly.GetManifestResourceStream($"Dlss5Optimizer.Core.Data.{fileName}")
        ?? throw new FileNotFoundException($"Eingebettete Ressource fehlt: {fileName}");
}
