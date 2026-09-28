using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Install;

/// <summary>
/// Packt alles, was man zur Fehlersuche aus der Ferne braucht, in eine ZIP-Datei: Logs und
/// Einstellungen der Mods, eine Dateiliste (Größe, Version, 32/64 Bit, Prüfsumme), das Manifest und
/// eine Zusammenfassung. Benutzernamen in Pfaden werden ersetzt; große Logs werden auf ihr Ende gekürzt.
/// </summary>
public static class DiagnosticBundle
{
    /// <summary>Logs und Einstellungsdateien im Spielordner (und in host64\).</summary>
    private static readonly string[] Patterns =
    [
        "*.log", "*.log1", "*.log2", "ReShade.ini", "ReShadePreset.ini", "dlss5-feed.cfg", "dlss5-bridge.cfg", "OptiScaler.ini",
        "dgVoodoo.conf", "dxvk.conf", "deep-fried-chicken.cfg",
    ];

    public const long MaxFileBytes = 2 * 1024 * 1024;

    public sealed record Options(string? UserProfile = null, string? UserName = null, IReadOnlyList<string>? ExtraFiles = null, string? AppLog = null);

    /// <summary>Relative Pfade der Dateien, die ins Paket gehören.</summary>
    public static IReadOnlyList<string> CollectGameFiles(string gameDir)
    {
        var result = new List<string>();
        foreach (var sub in new[] { "", "host64", Path.Combine(Installer.StateDirName) })
        {
            var dir = Path.Combine(gameDir, sub);
            if (!Directory.Exists(dir))
                continue;
            var patterns = sub == Installer.StateDirName ? ["manifest.json"] : Patterns;
            foreach (var pattern in patterns)
            {
                foreach (var f in Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
                {
                    var rel = Path.GetRelativePath(gameDir, f);
                    if (!result.Contains(rel, StringComparer.OrdinalIgnoreCase))
                        result.Add(rel);
                }
            }
        }
        return result;
    }

    /// <summary>Dateiliste von Spielordner und host64\: Größe, Datum, Version, Architektur, Prüfsumme.</summary>
    public static string FileListing(string gameDir)
    {
        var sb = new StringBuilder();
        foreach (var sub in new[] { "", "host64", "bin", Path.Combine("reshade-shaders", "Shaders") })
        {
            var dir = Path.Combine(gameDir, sub);
            if (!Directory.Exists(dir))
                continue;
            sb.AppendLine($"[{(sub.Length == 0 ? "Spielordner" : sub)}]");
            foreach (var f in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(f);
                var line = $"  {info.Name,-40} {info.Length,12:N0}  {info.LastWriteTime:yyyy-MM-dd HH:mm}";
                var ext = info.Extension.ToLowerInvariant();
                if (ext is ".dll" or ".exe" or ".asi" or ".addon32" or ".addon64")
                {
                    var pe = PeFile.TryRead(f);
                    line += pe is null
                        ? "  keine gültige DLL/EXE"
                        : $"  {(pe.Bitness == Bitness.X86 ? "32 Bit" : pe.Bitness == Bitness.X64 ? "64 Bit" : "?")}"
                          + (pe.FileVersion is { } v ? $"  v{v}" : "")
                          + (pe.Exports.Contains("ReShadeRegisterAddon") ? "  ReShade mit Add-ons" : "");
                    if (info.Length < 256L * 1024 * 1024)
                        line += "  sha256 " + ComponentStore.Sha256Of(f)[..16];
                }
                sb.AppendLine(line);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Benutzerpfad und -name durch Platzhalter ersetzen.</summary>
    public static string Redact(string text, Options o)
    {
        if (!string.IsNullOrEmpty(o.UserProfile))
            text = text.Replace(o.UserProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                       .Replace(o.UserProfile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(o.UserName) && o.UserName.Length >= 3)
            text = Regex.Replace(text, $@"(?<=[\\/]){Regex.Escape(o.UserName)}(?=[\\/])", "<Nutzer>", RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>Erstellt das Paket und liefert den Pfad.</summary>
    public static string Create(string zipPath, string gameDir, string summary, Options options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        var tmp = zipPath + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            AddText(zip, "zusammenfassung.txt", Redact(summary, options));
            AddText(zip, "dateien.txt", Redact(FileListing(gameDir), options));
            foreach (var rel in CollectGameFiles(gameDir))
                AddFile(zip, Path.Combine("spiel", rel.Replace('\\', '/')), Path.Combine(gameDir, rel), options);
            foreach (var extra in options.ExtraFiles ?? [])
            {
                if (File.Exists(extra))
                    AddFile(zip, Path.Combine("extern", Path.GetFileName(extra)), extra, options);
            }
            if (options.AppLog is { } log && File.Exists(log))
                AddFile(zip, "dlss5optimizer.log", log, options);
        }
        File.Move(tmp, zipPath, overwrite: true);
        return zipPath;
    }

    private static void AddFile(ZipArchive zip, string entryName, string path, Options options)
    {
        string text;
        try
        {
            text = ReadTail(path, MaxFileBytes);
        }
        catch (IOException e)
        {
            text = $"(nicht lesbar: {e.Message})";
        }
        catch (UnauthorizedAccessException e)
        {
            text = $"(nicht lesbar: {e.Message})";
        }
        AddText(zip, entryName, Redact(text, options));
    }

    /// <summary>Liest eine (evtl. noch vom Spiel geöffnete) Datei; bei großen Dateien nur das Ende.</summary>
    public static string ReadTail(string path, long maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long skip = Math.Max(0, fs.Length - maxBytes);
        fs.Position = skip;
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return skip > 0 ? $"(… die ersten {skip:N0} Bytes ausgelassen …){Environment.NewLine}{text}" : text;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name.Replace('\\', '/'), CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }
}
