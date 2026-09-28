using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Dlss5Optimizer.Core.Components;

public sealed record StoredFile(string RelativePath, string Sha256, long Size);

public sealed record StoredComponent(
    string Id,
    string Version,
    string Origin,
    DateTimeOffset StoredAt,
    IReadOnlyList<StoredFile> Files);

/// <summary>
/// Lokaler Speicher für heruntergeladene und importierte Komponenten:
/// &lt;root&gt;\&lt;id&gt;\files\… plus component.json mit SHA-256 jeder Datei.
/// </summary>
public sealed class ComponentStore(string root, ComponentCatalog catalog)
{
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2"];

    // Komprimierte Tar-Archive (dxvk-x.y.tar.gz) liest der Stream-Leser; alles andere das Archiv-API.
    private static readonly string[] StreamArchiveExtensions = [".gz", ".tgz", ".xz", ".bz2"];

    public string Root => root;

    public string FilesDir(string id) => Path.Combine(root, id, "files");

    private string RecordPath(string id) => Path.Combine(root, id, "component.json");

    public StoredComponent? Get(string id)
    {
        var p = RecordPath(id);
        if (!File.Exists(p))
            return null;
        try
        {
            return JsonSerializer.Deserialize<StoredComponent>(File.ReadAllText(p));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool IsAvailable(string id) => Get(id) is not null;

    /// <summary>
    /// Übernimmt Dateien, Ordner oder Archive (zip/7z/rar) in den Speicher und prüft, ob die
    /// erwarteten Dateien dabei sind. Bei Fehlern bleibt der vorherige Stand erhalten.
    /// </summary>
    public StoredComponent Import(string id, IEnumerable<string> sources, string version = "importiert", string origin = "Import")
    {
        var def = catalog.Get(id) ?? throw new ArgumentException($"Unbekannte Komponente: {id}");
        var staging = Path.Combine(root, id, "staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var src in sources)
            {
                if (Directory.Exists(src))
                    CopyDirectory(src, Path.Combine(staging, Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar))));
                else if (IsArchive(src))
                    ExtractArchive(src, staging);
                else if (def.ExtractFromInstaller.Length > 0 && src.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (ExtractFromSelfExtractingZip(src, staging, def.ExtractFromInstaller).Count == 0)
                        throw new InvalidDataException($"{def.Name}: Im Installer wurden keine passenden Dateien gefunden.");
                }
                else if (File.Exists(src))
                    File.Copy(src, Path.Combine(staging, Path.GetFileName(src)), overwrite: true);
                else
                    throw new FileNotFoundException("Datei nicht gefunden", src);
            }

            var missing = MissingExpected(def, staging).ToList();
            if (missing.Count > 0)
                throw new InvalidDataException($"{def.Name}: Es fehlen erwartete Dateien: {string.Join(", ", missing)}");

            var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Select(f => new StoredFile(Path.GetRelativePath(staging, f), Sha256Of(f), new FileInfo(f).Length))
                .ToList();

            var target = FilesDir(id);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);

            var record = new StoredComponent(id, version, origin, DateTimeOffset.Now, files);
            File.WriteAllText(RecordPath(id), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            return record;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    public void Remove(string id)
    {
        var dir = Path.Combine(root, id);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Sucht eine Datei der Komponente. Bei mehreren Treffern werden 64-Bit-Varianten bevorzugt
    /// (Ordner mit "x64", "64", "win64"), außer <paramref name="prefer32Bit"/> ist gesetzt.
    /// </summary>
    public string? FindFile(string id, string fileNameOrPattern, bool prefer32Bit = false)
    {
        var dir = FilesDir(id);
        if (!Directory.Exists(dir))
            return null;
        var matches = Directory.EnumerateFiles(dir, fileNameOrPattern, new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }).ToList();
        if (matches.Count <= 1)
            return matches.FirstOrDefault();

        int Rank(string p)
        {
            var rel = Path.GetRelativePath(dir, p).ToLowerInvariant();
            bool is64 = rel.Contains("x64") || rel.Contains("win64") || rel.Contains("64bit") || rel.Contains("amd64");
            bool is32 = rel.Contains("x86") || rel.Contains("x32") || rel.Contains("win32") || rel.Contains("32bit");
            if (prefer32Bit)
                return is32 ? 0 : is64 ? 2 : 1;
            return is64 ? 0 : is32 ? 2 : 1;
        }
        return matches.OrderBy(Rank).ThenBy(p => p.Length).First();
    }

    /// <summary>Prüft, ob die gespeicherten Dateien noch unverändert sind.</summary>
    public IEnumerable<string> VerifyIntegrity(string id)
    {
        var record = Get(id);
        if (record is null)
            yield break;
        foreach (var f in record.Files)
        {
            var p = Path.Combine(FilesDir(id), f.RelativePath);
            if (!File.Exists(p))
                yield return $"{f.RelativePath} fehlt";
            else if (!Sha256Of(p).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                yield return $"{f.RelativePath} wurde verändert";
        }
    }

    public static IEnumerable<string> MissingExpected(ComponentDefinition def, string dir)
    {
        foreach (var line in def.ExpectedFiles)
        {
            var alternatives = line.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool found = alternatives.Any(pattern => Directory.EnumerateFiles(dir, pattern,
                new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }).Any());
            if (!found)
                yield return line.Replace("|", " oder ");
        }
    }

    public static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static bool IsArchive(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Entpackt zip/7z/rar mit Schutz gegen "Zip Slip" (Pfade außerhalb des Ziels).</summary>
    public static void ExtractArchive(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination);
        if (StreamArchiveExtensions.Contains(Path.GetExtension(archivePath), StringComparer.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(archivePath);
            using var reader = ReaderFactory.OpenReader(fs, new ReaderOptions());
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory || reader.Entry.Key is null)
                    continue;
                var target = SafeTarget(root, reader.Entry.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var outFs = File.Create(target);
                reader.WriteEntryTo(outFs);
            }
            return;
        }

        using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key is not null))
        {
            var target = SafeTarget(root, entry.Key!);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.WriteToFile(target, new ExtractionOptions { Overwrite = true, ExtractFullPath = true });
        }
    }

    /// <summary>
    /// Holt Dateien aus einem Installer, an den ein ZIP angehängt ist (z. B. ReShade_Setup_*.exe).
    /// Wie das ReShade-Setup selbst: den ersten lokalen ZIP-Header ("PK\x03\x04") suchen und ab dort
    /// als Archiv lesen. Liefert die entpackten Pfade der gesuchten Dateien.
    /// </summary>
    public static IReadOnlyList<string> ExtractFromSelfExtractingZip(string exePath, string destination, IEnumerable<string> wanted)
    {
        var wantedSet = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(destination);
        var bytes = File.ReadAllBytes(exePath);

        foreach (var offset in ZipHeaderOffsets(bytes))
        {
            var result = new List<string>();
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes, offset, bytes.Length - offset, writable: false), ZipArchiveMode.Read);
                foreach (var entry in zip.Entries.Where(e => wantedSet.Contains(e.Name)))
                {
                    var target = SafeTarget(root, entry.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    result.Add(target);
                }
            }
            catch (InvalidDataException)
            {
                continue; // zufällige Bytefolge im Programmcode, nächster Kandidat
            }
            if (result.Count > 0)
                return result;
        }
        return [];
    }

    private static readonly byte[] ZipLocalHeader = [0x50, 0x4B, 0x03, 0x04];

    private static List<int> ZipHeaderOffsets(byte[] bytes)
    {
        var offsets = new List<int>();
        for (int i = 0; i + 30 <= bytes.Length && offsets.Count < 32; i++)
        {
            int hit = bytes.AsSpan(i).IndexOf(ZipLocalHeader);
            if (hit < 0)
                break;
            i += hit;
            // Wie ReShade: Ein echter Header hat hinter der Signatur nicht nur Nullen.
            if (i + 30 <= bytes.Length && bytes.AsSpan(i + 4, 26).ContainsAnyExcept((byte)0))
                offsets.Add(i);
        }
        return offsets;
    }

    private static string SafeTarget(string root, string entryKey)
    {
        var target = Path.GetFullPath(Path.Combine(root, entryKey.Replace('\\', '/').TrimStart('/')));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Archiv-Eintrag zeigt außerhalb des Zielordners: {entryKey}");
        return target;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, overwrite: true);
        }
    }
}
