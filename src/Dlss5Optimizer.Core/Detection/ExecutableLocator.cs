using System.Text.RegularExpressions;

namespace Dlss5Optimizer.Core.Detection;

/// <summary>Findet die eigentliche Spiel-EXE in einem Installationsordner.</summary>
public static partial class ExecutableLocator
{
    // Hilfsprogramme, die nie das Spiel selbst sind.
    [GeneratedRegex(@"(unins\d*|uninstall|setup|install|redist|vc_?redist|dxsetup|dotnet|directx|crash|report|bugsplat|sentry|ue4prereq|ueprereq|prereq|easyanticheat|start_protected_game|beservice|battleye|eaanticheat|cefprocess|cefsharp|webhelper|quicksfv|touchup|cleanup|updater|patcher|activation|dxwebsetup|oalinst|physx|vcredist|register|config(urator|tool)?$|settings$|benchmark_tool)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HelperExe();

    [GeneratedRegex(@"(^|[\\/])(_commonredist|commonredist|redist|redistributables?|directx|dotnet|support|__installer|installers?|tools|sdk|prereqs?|easyanticheat|battleye|engine[\\/]extras)([\\/]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HelperDir();

    [GeneratedRegex(@"-Win64-Shipping\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnrealShipping();

    [GeneratedRegex(@"launcher", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Launcher();

    public static bool ContainsExecutables(string dir, int maxDepth) =>
        EnumerateExecutables(dir, maxDepth).Any(e => !IsHelper(dir, e));

    public static string? FindMainExecutable(string installDir, string? hint = null)
    {
        if (hint is not null)
        {
            var hinted = Path.IsPathRooted(hint) ? hint : Path.Combine(installDir, hint.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(hinted))
                return hinted;
        }

        return EnumerateExecutables(installDir, maxDepth: 6)
            .Where(e => !IsHelper(installDir, e))
            .Select(e => (Path: e, Score: Score(installDir, e)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    internal static double Score(string installDir, string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        var rel = Path.GetRelativePath(installDir, exe);
        int depth = rel.Count(c => c == Path.DirectorySeparatorChar || c == '/');
        double score = 0;

        long size = 0;
        try
        {
            size = new FileInfo(exe).Length;
        }
        catch (IOException)
        {
        }
        // Größe zählt logarithmisch: Spiele-EXEs sind meist deutlich größer als Hilfsprogramme.
        score += Math.Log2(Math.Max(size, 1024) / 1024.0) * 3;

        if (UnrealShipping().IsMatch(exe))
            score += 60;
        if (Directory.Exists(Path.Combine(Path.GetDirectoryName(exe)!, name + "_Data")))
            score += 40; // Unity: Spiel.exe + Spiel_Data
        if (Launcher().IsMatch(name))
            score -= 25;

        var folder = Normalize(Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar)));
        var exeName = Normalize(name);
        if (folder.Length >= 3 && exeName.Length >= 3 && (folder.Contains(exeName) || exeName.Contains(folder)))
            score += 15;

        // Unreal-Startstub im Hauptordner (klein) gegenüber der Shipping-EXE bevorzugt die Shipping-EXE,
        // alle anderen Spiele liegen eher weiter oben.
        score -= depth * 2;
        return score;
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsHelper(string root, string exe)
    {
        var rel = Path.GetRelativePath(root, exe);
        return HelperExe().IsMatch(Path.GetFileNameWithoutExtension(exe)) || HelperDir().IsMatch(Path.GetDirectoryName(rel) ?? "");
    }

    internal static IEnumerable<string> EnumerateExecutables(string dir, int maxDepth)
    {
        if (!Directory.Exists(dir))
            return [];
        try
        {
            return Directory.EnumerateFiles(dir, "*.exe", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = maxDepth,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            }).Take(2000).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
