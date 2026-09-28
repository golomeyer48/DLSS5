namespace Dlss5Optimizer.Core.Install;

/// <summary>
/// Eine Einstellung für ReShades Tiefenpuffer: welcher Puffer (vor oder nach dem Löschen kopiert)
/// und wie er zu lesen ist (umgekehrt, auf dem Kopf).
/// </summary>
public sealed record DepthVariant(bool CopyBeforeClears, bool Reversed, bool UpsideDown)
{
    public string Label =>
        $"Puffer {(CopyBeforeClears ? "vor dem Löschen" : "normal")}, {(Reversed ? "umgekehrt" : "nicht umgekehrt")}, {(UpsideDown ? "gespiegelt" : "nicht gespiegelt")}";
}

/// <summary>
/// Probiert Tiefenpuffer-Einstellungen der Reihe nach durch, wenn der Feeder „flache“ Tiefe meldet.
/// Reihenfolge nach Häufigkeit: Die getestete Standardeinstellung der Referenzaufbauten zuerst,
/// dann „vor dem Löschen kopieren“ (Engines, die den Tiefenpuffer vor HUD/Waffe leeren),
/// dann nicht umgekehrt, zuletzt gespiegelt.
/// </summary>
public static class DepthAssistant
{
    private const string Reversed = "RESHADE_DEPTH_INPUT_IS_REVERSED";
    private const string UpsideDown = "RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN";

    public static IReadOnlyList<DepthVariant> Variants { get; } =
    [
        new(false, true, false),
        new(true, true, false),
        new(false, false, false),
        new(true, false, false),
        new(false, true, true),
        new(false, false, true),
    ];

    /// <summary>Liest die aktuelle Variante aus ReShade.ini (fehlende Werte zählen als ReShade-Standard).</summary>
    public static DepthVariant Read(IniFile reShadeIni)
    {
        var defs = Parse(reShadeIni.Get("GENERAL", "PreprocessorDefinitions"));
        bool Flag(string key, bool fallback) => defs.TryGetValue(key, out var v) ? v.Trim() == "1" : fallback;
        return new DepthVariant(
            reShadeIni.Get("DEPTH", "DepthCopyBeforeClears")?.Trim() == "1",
            Flag(Reversed, false),
            Flag(UpsideDown, false));
    }

    /// <summary>Die nächste Variante der Liste (nach der letzten wieder die erste).</summary>
    public static DepthVariant Next(DepthVariant current)
    {
        int i = Variants.ToList().FindIndex(v => v == current);
        return Variants[(i + 1) % Variants.Count];
    }

    public static (int Index, int Count) Position(DepthVariant v) => (Variants.ToList().FindIndex(x => x == v) + 1, Variants.Count);

    /// <summary>Schreibt die Variante in ReShade.ini und lässt alle anderen Definitionen stehen.</summary>
    public static void Apply(IniFile reShadeIni, DepthVariant v)
    {
        reShadeIni.Set("GENERAL", "PreprocessorDefinitions", MergeDefines(reShadeIni.Get("GENERAL", "PreprocessorDefinitions"), v));
        reShadeIni.Set("DEPTH", "DepthCopyBeforeClears", v.CopyBeforeClears ? "1" : "0");
    }

    public static string MergeDefines(string? existing, DepthVariant v)
    {
        var parts = (existing ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(d => !Key(d).Equals(Reversed, StringComparison.OrdinalIgnoreCase) && !Key(d).Equals(UpsideDown, StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Add($"{UpsideDown}={(v.UpsideDown ? 1 : 0)}");
        parts.Add($"{Reversed}={(v.Reversed ? 1 : 0)}");
        return string.Join(",", parts);
    }

    private static string Key(string define) => define.Split('=')[0].Trim();

    private static Dictionary<string, string> Parse(string? defines) =>
        (defines ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split('=', 2))
            .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().ElementAtOrDefault(1) ?? "", StringComparer.OrdinalIgnoreCase);
}
