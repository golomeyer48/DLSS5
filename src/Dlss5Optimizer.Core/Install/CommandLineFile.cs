using System.Globalization;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Install;

/// <summary>
/// Startparameter-Dateien wie GTA IVs commandline.txt: Schalter („-name“, optional mit Wert) durch Leerzeichen oder
/// Zeilen getrennt. Beim Zusammenführen bleiben fremde Schalter erhalten, eigene ersetzen gleichnamige.
/// </summary>
public static class CommandLineFile
{
    /// <summary>Ersetzt {width}, {height} und {refresh}; ohne bekannten Bildschirm entfallen solche Schalter.</summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string> args, DisplayInfo? display)
    {
        var result = new List<string>();
        foreach (var arg in args)
        {
            bool needsDisplay = arg.Contains("{width}") || arg.Contains("{height}") || arg.Contains("{refresh}");
            if (needsDisplay && display is null)
                continue;
            result.Add(display is null ? arg : arg
                .Replace("{width}", display.Width.ToString(CultureInfo.InvariantCulture))
                .Replace("{height}", display.Height.ToString(CultureInfo.InvariantCulture))
                .Replace("{refresh}", display.RefreshHz.ToString(CultureInfo.InvariantCulture)));
        }
        return result;
    }

    /// <summary>Vorhandenen Inhalt und neue Schalter zusammenführen – ein Schalter pro Zeile, Reihenfolge stabil.</summary>
    public static string Merge(string? existing, IEnumerable<string> args)
    {
        var switches = new List<(string Name, string Line)>();
        void Put(string line)
        {
            var name = line.Split(' ', 2)[0];
            int i = switches.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
                switches[i] = (name, line);
            else
                switches.Add((name, line));
        }
        foreach (var s in Split(existing ?? ""))
            Put(s);
        foreach (var s in args.SelectMany(Split))
            Put(s);
        return string.Concat(switches.Select(s => s.Line + "\r\n"));
    }

    /// <summary>Zerlegt Text in Schalter: ein Token mit „-“ plus die folgenden Werte ohne „-“.</summary>
    private static IEnumerable<string> Split(string text)
    {
        var tokens = text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string>();
        foreach (var t in tokens)
        {
            bool isSwitch = t.StartsWith('-') && !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            if (isSwitch && current.Count > 0)
            {
                yield return string.Join(' ', current);
                current.Clear();
            }
            current.Add(t);
        }
        if (current.Count > 0)
            yield return string.Join(' ', current);
    }
}
