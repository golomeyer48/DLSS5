using System.Text.Json;
using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Library;

public interface ILibraryScanner
{
    string Name { get; }
    IEnumerable<GameInfo> Scan();
}

/// <summary>Liest Steam-Bibliotheken über libraryfolders.vdf und appmanifest_*.acf.</summary>
public sealed class SteamLibraryScanner(string steamRoot) : ILibraryScanner
{
    // Werkzeuge, Laufzeitumgebungen und Redistributables – keine Spiele.
    private static readonly HashSet<string> IgnoredAppIds =
    [
        "228980",  // Steamworks Common Redistributables
        "250820",  // SteamVR
        "1070560", // Steam Linux Runtime
        "1391110", // Steam Linux Runtime - Soldier
        "1628350", // Steam Linux Runtime - Sniper
        "1493710", // Proton Experimental
        "2180100", // Proton Hotfix
    ];

    public string Name => "Steam";

    public IEnumerable<GameInfo> Scan()
    {
        foreach (var library in LibraryPaths())
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps))
                continue;

            foreach (var manifest in SafeEnumerate(steamapps, "appmanifest_*.acf"))
            {
                GameInfo? game = null;
                try
                {
                    var root = VdfParser.Parse(File.ReadAllText(manifest));
                    var state = root.Child("AppState");
                    var appId = state?["appid"];
                    var name = state?["name"];
                    var installDir = state?["installdir"];
                    if (appId is null || name is null || installDir is null || IgnoredAppIds.Contains(appId))
                        continue;
                    if (name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var dir = Path.Combine(steamapps, "common", installDir);
                    if (Directory.Exists(dir))
                        game = new GameInfo(name, dir, GameSource.Steam, appId);
                }
                catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
                {
                    // Kaputtes Manifest: überspringen, der Rest der Bibliothek zählt.
                }
                if (game is not null)
                    yield return game;
            }
        }
    }

    public IEnumerable<string> LibraryPaths()
    {
        var paths = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                var root = VdfParser.Parse(File.ReadAllText(vdf));
                var folders = root.Child("libraryfolders") ?? root.Child("LibraryFolders");
                if (folders is not null)
                {
                    // Neues Format: "0" { "path" "..." }, altes Format: "1" "D:\\SteamLibrary".
                    paths.AddRange(folders.Children.Values.Select(c => c["path"]).OfType<string>());
                    paths.AddRange(folders.Values.Where(kv => int.TryParse(kv.Key, out _)).Select(kv => kv.Value));
                }
            }
            catch (Exception e) when (e is IOException or FormatException)
            {
            }
        }
        return paths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string p) => Path.GetFullPath(p.Replace('\\', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar);

    internal static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Liest die Epic-Games-Launcher-Manifeste (*.item, JSON).</summary>
public sealed class EpicLibraryScanner(string manifestsDir) : ILibraryScanner
{
    public string Name => "Epic Games";

    public IEnumerable<GameInfo> Scan()
    {
        if (!Directory.Exists(manifestsDir))
            yield break;

        foreach (var item in SteamLibraryScanner.SafeEnumerate(manifestsDir, "*.item"))
        {
            GameInfo? game = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var root = doc.RootElement;
                string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool incomplete = root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True;
                var name = Str("DisplayName");
                var dir = Str("InstallLocation");
                if (!incomplete && name is not null && dir is not null && Directory.Exists(dir))
                {
                    var exe = Str("LaunchExecutable");
                    game = new GameInfo(name, dir, GameSource.Epic, Str("AppName"),
                        exe is null ? null : Path.Combine(dir, exe));
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
            }
            if (game is not null)
                yield return game;
        }
    }
}

/// <summary>
/// Manuell hinzugefügte Ordner. Ein Ordner mit EXE-Dateien ist ein Spiel; sonst ist jeder
/// Unterordner ein Spiel (typisch für "D:\Games" oder "C:\XboxGames").
/// </summary>
public sealed class FolderLibraryScanner(IEnumerable<string> folders, GameSource source = GameSource.Manual) : ILibraryScanner
{
    public string Name => source == GameSource.Xbox ? "Xbox" : "Manuelle Ordner";

    public IEnumerable<GameInfo> Scan()
    {
        foreach (var folder in folders.Where(Directory.Exists))
        {
            // EXE direkt im Ordner: Das ist selbst ein Spiel. Sonst ist es ein Sammelordner.
            if (ExecutableLocator.ContainsExecutables(folder, maxDepth: 0))
            {
                yield return new GameInfo(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), folder, source, folder);
                continue;
            }

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(folder).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var sub in subDirs)
            {
                // Xbox-Spiele liegen unter <Spiel>\Content.
                var content = Path.Combine(sub, "Content");
                var dir = Directory.Exists(content) ? content : sub;
                if (ExecutableLocator.ContainsExecutables(dir, maxDepth: 4))
                    yield return new GameInfo(Path.GetFileName(sub), dir, source, dir);
            }
        }
    }
}
