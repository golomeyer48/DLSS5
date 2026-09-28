using System.Text;

namespace Dlss5Optimizer.Core.Detection;

/// <summary>
/// Sucht Zeichenketten (ASCII und UTF-16LE) in Binärdateien. Wird gebraucht, weil große
/// Unreal-EXEs Direct3D/Vulkan erst zur Laufzeit laden: dann steht nichts in der Import-Tabelle,
/// aber die DLL- und RHI-Namen stehen als Strings in der Datei.
/// </summary>
public static class BinaryStringScanner
{
    private const int ChunkSize = 4 * 1024 * 1024;

    public static long DefaultMaxBytes { get; set; } = 768L * 1024 * 1024;

    /// <returns>Die Teilmenge von <paramref name="needles"/>, die in der Datei vorkommt.</returns>
    public static HashSet<string> FindAny(string path, IEnumerable<string> needles, long? maxBytes = null)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return FindAny(fs, needles, maxBytes ?? DefaultMaxBytes);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static HashSet<string> FindAny(Stream stream, IEnumerable<string> needles, long maxBytes)
    {
        var patterns = needles
            .Distinct(StringComparer.Ordinal)
            .SelectMany(n => new[]
            {
                (Needle: n, Bytes: Encoding.ASCII.GetBytes(n)),
                (Needle: n, Bytes: Encoding.Unicode.GetBytes(n)),
            })
            .ToList();
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (patterns.Count == 0)
            return found;

        int needleCount = patterns.Count / 2;
        int overlap = patterns.Max(p => p.Bytes.Length) - 1;
        var buffer = new byte[ChunkSize + overlap];
        int carried = 0;
        long total = 0;

        while (total < maxBytes && found.Count < needleCount)
        {
            int toRead = (int)Math.Min(ChunkSize, maxBytes - total);
            int read = stream.ReadAtLeast(buffer.AsSpan(carried, toRead), toRead, throwOnEndOfStream: false);
            if (read == 0)
                break;
            total += read;
            int valid = carried + read;
            var window = buffer.AsSpan(0, valid);

            foreach (var (needle, bytes) in patterns)
            {
                if (!found.Contains(needle) && window.IndexOf(bytes) >= 0)
                    found.Add(needle);
            }

            carried = Math.Min(overlap, valid);
            window[(valid - carried)..].CopyTo(buffer);
            if (read < toRead)
                break;
        }
        return found;
    }
}
