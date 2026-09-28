using System.Text;

namespace Dlss5Optimizer.Core.Install;

/// <summary>
/// Minimaler INI-Editor, der Kommentare, Reihenfolge und unbekannte Einträge unverändert lässt –
/// wichtig, weil ReShade.ini und OptiScaler.ini auch Nutzereinstellungen enthalten.
/// </summary>
public sealed class IniFile
{
    private readonly List<string> _lines;

    private IniFile(List<string> lines) => _lines = lines;

    public static IniFile Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return new IniFile(lines);
    }

    public static IniFile Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : new IniFile([]);

    public string? Get(string section, string key)
    {
        int i = FindKey(section, key, out _);
        if (i < 0)
            return null;
        var line = _lines[i];
        return line[(line.IndexOf('=') + 1)..].Trim();
    }

    public void Set(string section, string key, string value)
    {
        int i = FindKey(section, key, out int sectionEnd);
        var entry = $"{key}={value}";
        if (i >= 0)
        {
            _lines[i] = entry;
            return;
        }
        if (sectionEnd >= 0)
        {
            // Hinter den letzten Eintrag der Sektion, vor Leerzeilen.
            int insert = sectionEnd;
            while (insert > 0 && string.IsNullOrWhiteSpace(_lines[insert - 1]) && !IsSection(_lines[insert - 1]))
                insert--;
            _lines.Insert(insert, entry);
            return;
        }
        if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[^1]))
            _lines.Add("");
        _lines.Add($"[{section}]");
        _lines.Add(entry);
    }

    public override string ToString() => string.Join("\r\n", _lines) + "\r\n";

    public void Save(string path) => File.WriteAllText(path, ToString(), new UTF8Encoding(false));

    private static bool IsSection(string line) => line.TrimStart().StartsWith('[');

    /// <param name="sectionEnd">Index hinter der Sektion (oder -1, wenn sie fehlt).</param>
    private int FindKey(string section, string key, out int sectionEnd)
    {
        sectionEnd = -1;
        bool inSection = section.Length == 0;
        int keyIndex = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            var t = _lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                // Globale Schlüssel (ohne Sektion) stehen vor der ersten Sektion.
                if (inSection)
                {
                    sectionEnd = i;
                    return keyIndex;
                }
                inSection = t[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || t.StartsWith(';') || t.StartsWith('#'))
                continue;
            int eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                keyIndex = i;
        }
        if (inSection)
            sectionEnd = _lines.Count;
        return keyIndex;
    }
}
