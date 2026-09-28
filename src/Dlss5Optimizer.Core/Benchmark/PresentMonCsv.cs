using System.Globalization;

namespace Dlss5Optimizer.Core.Benchmark;

public sealed record FrameSample(
    double MsBetweenPresents,
    double? MsBetweenDisplayChange,
    string? Runtime,
    string? FrameType,
    double? MsPcLatency);

/// <summary>
/// Liest PresentMon-CSV-Dateien der Versionen 1.x und 2.x. Die Spaltennamen haben sich zwischen
/// den Versionen geändert (z. B. Runtime → PresentRuntime, msBetweenPresents → MsBetweenPresents),
/// deshalb wird jede Spalte über mehrere Kandidaten ohne Beachtung der Groß-/Kleinschreibung gesucht.
/// </summary>
public static class PresentMonCsv
{
    private static readonly string[] ProcessCols = ["Application", "ProcessName"];
    private static readonly string[] BetweenPresentsCols = ["MsBetweenPresents", "msBetweenPresents"];
    private static readonly string[] BetweenDisplayCols = ["MsBetweenDisplayChange", "msBetweenDisplayChange"];
    private static readonly string[] RuntimeCols = ["PresentRuntime", "Runtime"];
    private static readonly string[] FrameTypeCols = ["FrameType"];
    private static readonly string[] PcLatencyCols = ["MsPCLatency", "MsClickToPhotonLatency", "msClickToPhotonLatency"];

    public static List<FrameSample> Parse(TextReader reader, string? processName = null)
    {
        var header = reader.ReadLine() ?? throw new InvalidDataException("Leere PresentMon-Datei");
        var cols = SplitCsv(header);
        int Col(string[] names) => names.Select(n => cols.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);

        int iProc = Col(ProcessCols), iBetween = Col(BetweenPresentsCols), iDisplay = Col(BetweenDisplayCols);
        int iRuntime = Col(RuntimeCols), iType = Col(FrameTypeCols), iLatency = Col(PcLatencyCols);
        if (iBetween < 0)
            throw new InvalidDataException("Spalte MsBetweenPresents fehlt – ist das eine PresentMon-CSV?");

        var result = new List<FrameSample>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            var f = SplitCsv(line);
            if (processName is not null && iProc >= 0 && iProc < f.Count
                && !f[iProc].Equals(processName, StringComparison.OrdinalIgnoreCase))
                continue;
            var between = Num(f, iBetween);
            if (between is null or <= 0)
                continue;
            result.Add(new FrameSample(between.Value, Num(f, iDisplay), Str(f, iRuntime), Str(f, iType), Num(f, iLatency)));
        }
        return result;
    }

    private static string? Str(List<string> f, int i) => i >= 0 && i < f.Count && f[i].Length > 0 ? f[i] : null;

    private static double? Num(List<string> f, int i)
    {
        if (i < 0 || i >= f.Count)
            return null;
        return double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cur.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(cur.ToString().Trim());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }
        fields.Add(cur.ToString().Trim());
        return fields;
    }
}

public sealed record FrameStats(
    int Frames,
    double DurationSeconds,
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double? RenderedFps,
    double? AveragePcLatencyMs,
    string? DominantRuntime)
{
    public static FrameStats? From(IReadOnlyList<FrameSample> samples, double warmupSeconds = 3)
    {
        // Die ersten Sekunden (Laden, Shader-Kompilierung) verfälschen das Ergebnis.
        var list = SkipWarmup(samples, warmupSeconds);
        if (list.Count < 30)
            return null;

        var times = list.Select(s => s.MsBetweenPresents).ToArray();
        double totalMs = times.Sum();
        double avgFps = list.Count * 1000.0 / totalMs;
        var sorted = times.OrderBy(t => t).ToArray();
        double p99 = Percentile(sorted, 0.99), p999 = Percentile(sorted, 0.999);

        // Mit Frame Generation markiert PresentMon 2.x erzeugte Frames; "Application" = echt gerendert.
        double? rendered = null;
        if (list.Any(s => s.FrameType is not null))
        {
            int real = list.Count(s => s.FrameType is null || s.FrameType.Equals("Application", StringComparison.OrdinalIgnoreCase) || s.FrameType == "NotSet");
            rendered = real * 1000.0 / totalMs;
        }

        var latencies = list.Select(s => s.MsPcLatency).OfType<double>().Where(v => v > 0).ToArray();
        var runtime = list.Select(s => s.Runtime).OfType<string>().GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        return new FrameStats(list.Count, totalMs / 1000.0, avgFps, 1000.0 / p99, 1000.0 / p999, rendered,
            latencies.Length > 0 ? latencies.Average() : null, runtime);
    }

    private static List<FrameSample> SkipWarmup(IReadOnlyList<FrameSample> samples, double warmupSeconds)
    {
        double elapsed = 0;
        int start = 0;
        while (start < samples.Count && elapsed < warmupSeconds * 1000)
            elapsed += samples[start++].MsBetweenPresents;
        // Bei sehr kurzen Aufnahmen lieber alles nehmen als nichts.
        return start >= samples.Count / 2 ? samples.ToList() : samples.Skip(start).ToList();
    }

    internal static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
            return double.NaN;
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
