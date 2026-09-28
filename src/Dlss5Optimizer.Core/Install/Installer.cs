using System.Text.Json;
using System.Text.Json.Serialization;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;

namespace Dlss5Optimizer.Core.Install;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CopyFileStep), "copy")]
[JsonDerivedType(typeof(IniSetStep), "ini")]
[JsonDerivedType(typeof(WriteTextStep), "text")]
[JsonDerivedType(typeof(ManualStep), "manual")]
[JsonDerivedType(typeof(RemoveFileStep), "remove")]
[JsonDerivedType(typeof(RegisterVulkanLayerStep), "vulkan-layer")]
public abstract record InstallStep(string Description);

/// <summary>Kopiert eine Datei; <see cref="Target"/> ist relativ zum Ordner der Spiel-EXE.</summary>
public sealed record CopyFileStep(string Source, string Target, string Description) : InstallStep(Description);

/// <summary>
/// Setzt einen INI-Schlüssel. <see cref="Target"/> ist relativ zum Spielordner oder beginnt mit einem
/// Platzhalter wie <c>%DOCUMENTS%</c> (Einstellungsdateien des Spiels unter „Eigene Dateien“).
/// Mit <see cref="OnlyIfExists"/> wird eine fehlende Datei nicht angelegt.
/// </summary>
public sealed record IniSetStep(string Target, string Section, string Key, string Value, string Description, bool OnlyIfExists = false) : InstallStep(Description);

public sealed record WriteTextStep(string Target, string Content, string Description) : InstallStep(Description);

/// <summary>Schritt, den der Nutzer selbst ausführen muss (wird nur angezeigt).</summary>
public sealed record ManualStep(string Instruction) : InstallStep(Instruction);

/// <summary>Entfernt eine störende Datei (wird gesichert und bei "Rückgängig" wiederhergestellt).</summary>
public sealed record RemoveFileStep(string Target, string Description) : InstallStep(Description);

/// <summary>
/// Registriert ReShade als Vulkan-Layer (32- oder 64-Bit-Registry-Zweig). Der Layer ist global, greift
/// aber nur in Spielen, neben deren EXE eine ReShade.ini liegt – er bleibt daher beim Rückgängigmachen bestehen.
/// </summary>
public sealed record RegisterVulkanLayerStep(string LayerJson, string Description, bool Is32Bit = false) : InstallStep(Description);

public sealed record InstallPlan(
    Configuration Config,
    string GameDir,
    IReadOnlyList<InstallStep> Steps,
    IReadOnlyList<string> Hints,
    IReadOnlyList<string> CleanupPatterns)
{
    public IEnumerable<ManualStep> ManualSteps => Steps.OfType<ManualStep>();
}

public sealed record ManifestEntry(string RelativePath, bool Existed, string? InstalledSha256, long Size = -1, DateTime LastWriteUtc = default);

public sealed record InstallManifest(
    int FormatVersion,
    DateTimeOffset InstalledAt,
    Configuration Config,
    List<ManifestEntry> Files,
    List<string> CreatedDirectories,
    List<string> CleanupPatterns,
    List<string> PreexistingFiles);

/// <summary>
/// Führt Installationspläne transaktional aus: Jede Datei, die überschrieben oder geändert wird,
/// landet vorher in &lt;Spiel&gt;\.dlss5-optimizer\backup. Schlägt ein Schritt fehl, wird alles
/// zurückgerollt; "Rückgängig" stellt den Originalzustand wieder her.
/// </summary>
/// <param name="registerVulkanLayer">Registriert einen Layer (JSON-Pfad, 32 Bit?) – nur unter Windows.</param>
/// <param name="resolveToken">Löst Platzhalter wie %DOCUMENTS% in echte Ordner auf.</param>
public sealed class Installer(Action<string, bool>? registerVulkanLayer = null, Func<string, string?>? resolveToken = null)
{
    public const string StateDirName = ".dlss5-optimizer";
    private const string ManifestName = "manifest.json";
    private const string BackupDirName = "backup";
    private const string ExternalBackupDir = "_external";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string StateDir(string gameDir) => Path.Combine(gameDir, StateDirName);

    public InstallManifest? ReadManifest(string gameDir)
    {
        var p = Path.Combine(StateDir(gameDir), ManifestName);
        if (!File.Exists(p))
            return null;
        try
        {
            return JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(p), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool IsInstalled(string gameDir) => ReadManifest(gameDir) is not null;

    public InstallManifest Install(InstallPlan plan)
    {
        var gameDir = Path.GetFullPath(plan.GameDir);
        if (IsInstalled(gameDir))
            Uninstall(gameDir);

        var state = StateDir(gameDir);
        var backup = Path.Combine(state, BackupDirName);
        Directory.CreateDirectory(backup);

        var touched = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // rel → existierte vorher
        var createdDirs = new List<string>();
        var preexisting = MatchCleanup(gameDir, plan.CleanupPatterns).ToList();

        try
        {
            foreach (var step in plan.Steps)
            {
                switch (step)
                {
                    case CopyFileStep c:
                    {
                        var target = Prepare(c.Target);
                        File.Copy(c.Source, target, overwrite: true);
                        break;
                    }
                    case IniSetStep i:
                    {
                        if (i.OnlyIfExists && !File.Exists(ResolvePath(gameDir, i.Target)))
                            break;
                        var target = Prepare(i.Target);
                        var ini = IniFile.Load(target);
                        ini.Set(i.Section, i.Key, i.Value);
                        ini.Save(target);
                        break;
                    }
                    case WriteTextStep w:
                    {
                        var target = Prepare(w.Target);
                        File.WriteAllText(target, w.Content);
                        break;
                    }
                    case RemoveFileStep r:
                    {
                        var target = Prepare(r.Target);
                        if (File.Exists(target))
                            File.Delete(target);
                        break;
                    }
                    case RegisterVulkanLayerStep v:
                        if (registerVulkanLayer is null)
                            throw new PlatformNotSupportedException("Vulkan-Layer können nur unter Windows registriert werden.");
                        registerVulkanLayer(v.LayerJson, v.Is32Bit);
                        break;
                    case ManualStep:
                        break;
                }
            }

            var manifest = new InstallManifest(
                FormatVersion: 1,
                InstalledAt: DateTimeOffset.Now,
                Config: plan.Config,
                Files: touched.Select(kv => Entry(ResolvePath(gameDir, kv.Key), kv.Key, kv.Value)).ToList(),
                CreatedDirectories: createdDirs,
                CleanupPatterns: plan.CleanupPatterns.ToList(),
                PreexistingFiles: preexisting);
            File.WriteAllText(Path.Combine(state, ManifestName), JsonSerializer.Serialize(manifest, Json));
            return manifest;
        }
        catch
        {
            Restore(gameDir, touched.Select(kv => new ManifestEntry(kv.Key, kv.Value, null)), createdDirs);
            TryDeleteDirectory(state);
            throw;
        }

        string Prepare(string relative)
        {
            var target = ResolvePath(gameDir, relative);
            bool external = IsExternal(relative);
            var key = external ? relative : Path.GetRelativePath(gameDir, target);
            if (!touched.ContainsKey(key))
            {
                bool existed = File.Exists(target);
                if (existed)
                {
                    var bak = BackupPath(gameDir, key);
                    Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                    File.Copy(target, bak, overwrite: true);
                }
                else if (external)
                {
                    throw new InvalidOperationException($"Einstellungsdatei außerhalb des Spielordners fehlt: {relative}");
                }
                touched[key] = existed;
            }
            if (external)
                return target;
            // Fehlende Ordner anlegen und merken, damit "Rückgängig" sie wieder entfernt.
            var dir = Path.GetDirectoryName(target)!;
            var missing = new Stack<string>();
            for (var d = dir; !Directory.Exists(d) && d.Length > gameDir.Length; d = Path.GetDirectoryName(d)!)
                missing.Push(d);
            foreach (var d in missing)
            {
                Directory.CreateDirectory(d);
                createdDirs.Add(Path.GetRelativePath(gameDir, d));
            }
            return target;
        }
    }

    /// <summary>Stellt den Zustand vor der Installation wieder her.</summary>
    public void Uninstall(string gameDir)
    {
        gameDir = Path.GetFullPath(gameDir);
        var manifest = ReadManifest(gameDir) ?? throw new InvalidOperationException("Keine Installation dieses Tools gefunden.");
        Restore(gameDir, manifest.Files, manifest.CreatedDirectories);

        // Laufzeitdateien der Mods (Logs, Caches), die vorher nicht da waren.
        foreach (var rel in MatchCleanup(gameDir, manifest.CleanupPatterns).Except(manifest.PreexistingFiles, StringComparer.OrdinalIgnoreCase))
        {
            var p = Path.Combine(gameDir, rel);
            if (File.Exists(p))
                File.Delete(p);
            else if (Directory.Exists(p))
                TryDeleteDirectory(p);
        }
        // Erst jetzt sind z. B. host64\ oder reshade-shaders\ leer (die Logs darin sind weg).
        RemoveCreatedDirectories(gameDir, manifest.CreatedDirectories);
        TryDeleteDirectory(StateDir(gameDir));
    }

    /// <summary>
    /// Findet installierte Dateien, die fehlen oder verändert wurden – typischerweise nach einem
    /// Spiel-Update, das DLLs zurückgesetzt hat. INI-Dateien werden ausgelassen, weil Mods dort
    /// zur Laufzeit selbst schreiben.
    /// </summary>
    public IReadOnlyList<string> Verify(string gameDir)
    {
        var manifest = ReadManifest(gameDir);
        if (manifest is null)
            return [];
        var problems = new List<string>();
        foreach (var f in manifest.Files.Where(f => !f.RelativePath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && !IsExternal(f.RelativePath)))
        {
            if (f.InstalledSha256 is null)
                continue; // bewusst entfernte Datei
            var p = Path.Combine(gameDir, f.RelativePath);
            if (!File.Exists(p))
            {
                problems.Add($"{f.RelativePath} fehlt");
                continue;
            }
            // Schneller Weg: Größe und Zeitstempel unverändert ⇒ Datei unverändert (spart das Hashen großer DLLs).
            var info = new FileInfo(p);
            if (info.Length == f.Size && info.LastWriteTimeUtc == f.LastWriteUtc)
                continue;
            if (!ComponentStore.Sha256Of(p).Equals(f.InstalledSha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{f.RelativePath} wurde ersetzt (Spiel-Update?)");
        }
        return problems;
    }

    private void Restore(string gameDir, IEnumerable<ManifestEntry> files, IEnumerable<string> createdDirs)
    {
        foreach (var f in files)
        {
            var target = ResolvePath(gameDir, f.RelativePath);
            if (f.Existed)
            {
                var bak = BackupPath(gameDir, f.RelativePath);
                if (File.Exists(bak))
                    File.Copy(bak, target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        RemoveCreatedDirectories(gameDir, createdDirs);
    }

    /// <summary>Selbst angelegte Ordner entfernen, sofern leer (Dateien des Nutzers bleiben unangetastet).</summary>
    private static void RemoveCreatedDirectories(string gameDir, IEnumerable<string> createdDirs)
    {
        foreach (var d in createdDirs.OrderByDescending(d => d.Length))
        {
            var full = Path.Combine(gameDir, d);
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                Directory.Delete(full);
        }
    }

    private static IEnumerable<string> MatchCleanup(string gameDir, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var dir = Path.GetDirectoryName(pattern.Replace('/', Path.DirectorySeparatorChar)) ?? "";
            var baseDir = Path.Combine(gameDir, dir);
            if (!Directory.Exists(baseDir))
                continue;
            var name = Path.GetFileName(pattern);
            foreach (var e in Directory.EnumerateFileSystemEntries(baseDir, name))
                yield return Path.GetRelativePath(gameDir, e);
        }
    }

    private static ManifestEntry Entry(string path, string rel, bool existed)
    {
        if (!File.Exists(path))
            return new ManifestEntry(rel, existed, null);
        var info = new FileInfo(path);
        return new ManifestEntry(rel, existed, ComponentStore.Sha256Of(path), info.Length, info.LastWriteTimeUtc);
    }

    private static bool IsExternal(string target) => target.StartsWith('%');

    /// <summary>Ziel im Spielordner oder – mit Platzhalter – in einem freigegebenen Ordner außerhalb.</summary>
    internal string ResolvePath(string gameDir, string target)
    {
        if (!IsExternal(target))
            return SafePath(gameDir, target);
        int end = target.IndexOf('%', 1);
        if (end < 0)
            throw new InvalidOperationException($"Ungültiger Platzhalter: {target}");
        var token = target[..(end + 1)];
        var root = resolveToken?.Invoke(token) ?? throw new InvalidOperationException($"Platzhalter {token} ist hier nicht verfügbar.");
        return SafePath(Path.GetFullPath(root), target[(end + 1)..].TrimStart('\\', '/'));
    }

    private static string BackupPath(string gameDir, string key)
    {
        var backup = Path.Combine(StateDir(gameDir), BackupDirName);
        if (!IsExternal(key))
            return Path.Combine(backup, key);
        var safeName = string.Concat(key.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_'));
        return Path.Combine(backup, ExternalBackupDir, safeName);
    }

    internal static string SafePath(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, normalized));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Ziel liegt außerhalb des Spielordners: {relative}");
        return full;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
