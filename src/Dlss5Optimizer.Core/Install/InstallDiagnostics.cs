using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Install;

public enum DiagnosticStatus
{
    Ok,
    Warning,
    Failed,
    NotRunYet,
}

public sealed record DiagnosticCheck(string Title, DiagnosticStatus Status, string Detail);

public enum DiagnosticVerdict
{
    /// <summary>Die ganze Kette läuft.</summary>
    Working,
    /// <summary>Noch nicht (lange genug) gespielt.</summary>
    NotRunYet,
    /// <summary>Der Übersetzer (DXVK/dgVoodoo/d3d8to9) ist abgestürzt – der andere kann helfen.</summary>
    TranslatorFailed,
    /// <summary>Das Spiel lädt die System-d3d9.dll selbst – kein Übersetzer greift.</summary>
    WrapperBypassed,
    /// <summary>Läuft, aber etwas ist falsch eingestellt (Tiefe, Bewegungsvektoren, Add-on …).</summary>
    NeedsAttention,
}

/// <summary>Ein Absturz aus dem Windows-Ereignisprotokoll (Ereignis 1000).</summary>
public sealed record CrashInfo(string Module, string Code, DateTime When);

public sealed record DiagnosticReport(
    IReadOnlyList<DiagnosticCheck> Checks,
    DiagnosticVerdict Verdict,
    string Summary,
    RouteId? SwitchTo);

/// <summary>
/// Prüft nach einem Spielstart anhand der Logs, ob die ganze Kette greift – Übersetzer (DXVK),
/// ReShade, Feeder, Hilfsprozess und der eigentliche DLSS-5-Pass. Die Erkennungsmerkmale stammen
/// aus den Handbüchern und Fehlerberichten der Projekte (siehe README).
/// </summary>
public static class InstallDiagnostics
{
    private const int TailBytes = 2 * 1024 * 1024;

    // Module der Übersetzer – stürzt das Spiel darin ab, hilft oft der jeweils andere Übersetzer.
    private static readonly HashSet<string> TranslatorModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "d3d9.dll", "d3d8.dll", "ddraw.dll", "d3dimm.dll",
    };

    // Zeilenformate aus dlss5-feed.cpp (DLSS5-Feeder).
    private static readonly Regex MvProbe = new(@"MV probe \([^)]*\):.*?(\d+)% non-zero", RegexOptions.CultureInvariant);
    private static readonly Regex DepthProbe = new(@"Depth probe \([^)]*\):.*?(\d+)% finite(.*)$", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// Fasst Log-Prüfung und Absturzprotokoll zu einem Urteil zusammen und schlägt – wenn der
    /// Übersetzer abstürzt – den anderen Übersetzer vor (sofern die Spiel-Datenbank ihn nicht ausschließt).
    /// </summary>
    public static DiagnosticReport Evaluate(string gameDir, InstallManifest manifest, string exeName, IReadOnlyList<CrashInfo> crashes, GameDbEntry? db = null)
    {
        var checks = Check(gameDir, manifest, exeName);
        var route = manifest.Config.Route;

        var translatorCrash = route.IsLegacy() ? crashes.FirstOrDefault(c => TranslatorModules.Contains(Path.GetFileName(c.Module))) : null;
        if (translatorCrash is not null)
        {
            var alt = RouteCatalog.AlternativeTranslator(route, manifest.Config.Api);
            if (alt is { } a && db?.Excludes(a.ToString()) == true)
                alt = null;
            var summary = $"Das Spiel ist im Übersetzer abgestürzt ({translatorCrash.Module}, Code {translatorCrash.Code}).";
            summary += alt is { } to
                ? $" Der andere Übersetzer ({RouteCatalog.Get(to).Name}) läuft in solchen Fällen oft."
                : " Ein anderer Übersetzer ist für dieses Spiel nicht bekannt oder als nicht funktionierend markiert.";
            return new DiagnosticReport(checks, DiagnosticVerdict.TranslatorFailed, summary, alt);
        }

        if (checks.Any(c => c.Title == "ReShade" && c.Status == DiagnosticStatus.Failed && c.Detail.Contains("System-d3d9.dll")))
            return new DiagnosticReport(checks, DiagnosticVerdict.WrapperBypassed,
                "Das Spiel lädt die d3d9.dll aus Windows selbst (z. B. für einen DirectX-Test beim Start). Dann greift weder DXVK noch dgVoodoo – mit diesen Werkzeugen nicht lösbar.", null);

        if (crashes.Count > 0)
            return new DiagnosticReport(checks, DiagnosticVerdict.NeedsAttention,
                $"Das Spiel ist abgestürzt ({crashes[0].Module}, Code {crashes[0].Code}).", null);

        if (checks.Any(c => c.Status == DiagnosticStatus.Failed))
            return new DiagnosticReport(checks, DiagnosticVerdict.NeedsAttention, "Es gibt Probleme – die Hinweise sagen, woran es liegt.", null);
        if (checks.Any(c => c.Status == DiagnosticStatus.NotRunYet))
            return new DiagnosticReport(checks, DiagnosticVerdict.NotRunYet,
                "Noch nicht vollständig geprüft – Spiel starten, eine Minute spielen (die Feeder-Prüfungen laufen alle 600 Bilder), dann erneut „Diagnose“.", null);
        if (checks.Any(c => c.Status == DiagnosticStatus.Warning))
            return new DiagnosticReport(checks, DiagnosticVerdict.NeedsAttention, "Läuft, aber nicht alles ist optimal – siehe Hinweise.", null);
        return new DiagnosticReport(checks, DiagnosticVerdict.Working, "Alles greift: DLSS 5 läuft in diesem Spiel.", null);
    }

    /// <summary>
    /// Bewegungsvektoren: Eine einzelne 0-%-Messung ist bei stehender Kamera normal. Erst wenn
    /// mehrere Prüfungen nichts finden, bekommt DLSS wirklich keine Vektoren.
    /// </summary>
    internal static DiagnosticCheck? MotionVectorCheck(string feedLog)
    {
        var values = MvProbe.Matches(feedLog).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        if (values.Count == 0)
            return new("Bewegungsvektoren", DiagnosticStatus.NotRunYet, "Noch keine Prüfung – die erste kommt nach 600 Bildern (etwa 10 Sekunden).");
        int best = values.Max();
        if (best >= 2)
            return new("Bewegungsvektoren", DiagnosticStatus.Ok, $"Kommen an (bis {best} % der geprüften Bildpunkte bewegen sich).");
        return values.Count >= 2
            ? new("Bewegungsvektoren", DiagnosticStatus.Failed,
                "DLSS bekommt keine Bewegungsvektoren. Im ReShade-Menü (Pos1) muss Lumenite_Kernel aktiv sein und über DLSS5_Feed stehen; kompiliert es nicht, fehlen die ReShade-Header („Reparieren“).")
            : new("Bewegungsvektoren", DiagnosticStatus.Warning, "Bisher 0 % – beim nächsten Mal während der Prüfung die Kamera bewegen.");
    }

    /// <summary>Tiefenpuffer: flache oder fehlende Tiefe heißt, ReShade liest den falschen Puffer.</summary>
    internal static DiagnosticCheck? DepthCheck(string feedLog)
    {
        var last = DepthProbe.Matches(feedLog).LastOrDefault();
        if (last is null)
            return new("Tiefenpuffer", DiagnosticStatus.NotRunYet, "Noch keine Prüfung – die erste kommt nach 600 Bildern.");
        int finite = int.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture);
        bool flat = last.Groups[2].Value.Contains("flat", StringComparison.OrdinalIgnoreCase);
        if (flat || finite == 0)
            return new("Tiefenpuffer", DiagnosticStatus.Failed,
                "Die Tiefe ist flach – ReShade liest den falschen Puffer. Kantenglättung (MSAA) im Spiel aus und den „Tiefenpuffer-Assistenten“ nutzen.");
        return new("Tiefenpuffer", DiagnosticStatus.Ok, "Tiefe kommt an.");
    }

    public static IReadOnlyList<DiagnosticCheck> Check(string gameDir, InstallManifest manifest, string exeName)
    {
        var checks = new List<DiagnosticCheck>();
        var route = manifest.Config.Route;
        var since = manifest.InstalledAt.UtcDateTime;
        string? Log(params string[] names) => ReadFreshLog(gameDir, since, names);
        bool host = Directory.Exists(Path.Combine(gameDir, "host64"));

        if (route == RouteId.NativeDlss5)
        {
            checks.Add(new("Natives DLSS 5", DiagnosticStatus.Ok, "Keine Mods installiert – DLSS 5 im Spielmenü einschalten."));
            return checks;
        }

        if (route.UsesDxvk())
        {
            var exeBase = Path.GetFileNameWithoutExtension(exeName);
            var dxvk = Log($"{exeBase}_d3d9.log");
            checks.Add(dxvk is null
                ? new("DXVK (DirectX 9 → Vulkan)", DiagnosticStatus.NotRunYet,
                    "Kein DXVK-Log seit der Installation. Spiel starten. Fehlt es danach weiter, lädt das Spiel die System-d3d9.dll (z. B. durch einen DirectX-Test beim Start) – dann die dgVoodoo-Route versuchen.")
                : new("DXVK (DirectX 9 → Vulkan)", DiagnosticStatus.Ok, "DXVK läuft."));
        }

        if (route is RouteId.OptiScalerNr)
        {
            var opti = Log("OptiScaler.log");
            checks.Add(opti is null
                ? new("OptiScaler", DiagnosticStatus.NotRunYet, "Noch kein OptiScaler-Log – Spiel starten und DLSS/FSR/XeSS im Spiel einschalten.")
                : Contains(opti, "DlssNr") || Contains(opti, "dlssnr")
                    ? new("OptiScaler", DiagnosticStatus.Ok, "OptiScaler läuft und hat den DLSS-5-Pass geladen.")
                    : new("OptiScaler", DiagnosticStatus.Warning, "OptiScaler läuft, erwähnt DLSS 5 aber nicht. Im Spiel DLSS (oder FSR/XeSS) einschalten; Menü mit Einfg öffnen."));
            return checks;
        }

        // ReShade (Spielseite).
        var reshade = Log("ReShade.log", "dxgi.log", "opengl32.log", "d3d9.log");
        if (reshade is null)
        {
            checks.Add(new("ReShade", DiagnosticStatus.NotRunYet, "Noch kein ReShade-Log seit der Installation – Spiel starten."));
        }
        else
        {
            if (Contains(reshade, "No add-on was registered"))
                checks.Add(new("ReShade-Add-ons", DiagnosticStatus.Failed,
                    "Ein Add-on hat sich nicht registriert („No add-on was registered“). Über den Vulkan-Layer darf es nicht unter LoadFromDllMain stehen – „Reparieren“ setzt das richtig."));
            else if (route.IsLegacy() && Contains(reshade, "IDirect3DDevice9"))
                checks.Add(new("ReShade", DiagnosticStatus.Failed,
                    "ReShade hängt direkt an DirectX 9 – der Übersetzer (DXVK/dgVoodoo) ist nicht aktiv. Das Spiel lädt vermutlich die System-d3d9.dll."));
            else
                checks.Add(new("ReShade", DiagnosticStatus.Ok, "ReShade ist geladen."));
        }

        if (route.UsesFeeder())
        {
            var feed = Log("dlss5-feed.log");
            if (feed is null)
                checks.Add(new("DLSS5-Feeder", DiagnosticStatus.NotRunYet, "Noch kein Feeder-Log – im Spiel die Techniken Lumenite_Kernel und DLSS5_Feed aktivieren (Pos1)."));
            else if (Contains(feed, "transport-only") || Contains(feed, "mode=1"))
                checks.Add(new("DLSS5-Feeder", DiagnosticStatus.Failed, "Nur Transporttest aktiv (mode=1) – DLSS 5 läuft nicht. „Reparieren“ setzt mode=2."));
            else if (Contains(feed, "delivered"))
                checks.Add(new("DLSS5-Feeder", DiagnosticStatus.Ok, "Bilder werden übergeben (\"frame … delivered\")."));
            else if (Contains(feed, "shared set ready"))
                checks.Add(new("DLSS5-Feeder", DiagnosticStatus.Warning, "Übergabe ist bereit, aber noch keine Bilder gesendet – ein paar Sekunden im Spiel bewegen."));
            else
                checks.Add(new("DLSS5-Feeder", DiagnosticStatus.Warning, "Feeder geladen, aber keine Übergabe. Tiefenpuffer prüfen (Kantenglättung/MSAA im Spiel aus)."));

            if (feed is not null)
            {
                checks.Add(MotionVectorCheck(feed)!);
                checks.Add(DepthCheck(feed)!);
            }

            if (host)
            {
                var hostLog = Log(Path.Combine("host64", "dlss5-feed-host.log"));
                if (hostLog is null)
                    checks.Add(new("64-Bit-Hilfsprozess", DiagnosticStatus.NotRunYet, "Hilfsprozess noch nicht gestartet – er startet mit dem ersten übergebenen Bild."));
                else if (Contains(hostLog, "evaluated"))
                    checks.Add(new("64-Bit-Hilfsprozess", DiagnosticStatus.Ok, "DLAA läuft im Hilfsprozess (\"frame … evaluated\")."));
                else if (Contains(hostLog, "feature ready"))
                    checks.Add(new("64-Bit-Hilfsprozess", DiagnosticStatus.Warning, "DLAA ist bereit, hat aber noch nichts berechnet."));
                else
                    checks.Add(new("64-Bit-Hilfsprozess", DiagnosticStatus.Failed, "Hilfsprozess läuft, aber DLAA startet nicht. Stammen Add-on und Hilfsprozess aus demselben Feeder-Release? („Reparieren“)"));
            }
        }

        // Der eigentliche DLSS-5-Pass (RenoDX/Deep Fried Chicken): im Hilfsprozess oder im Spiel.
        var nrLog = host ? Log(Path.Combine("host64", "ReShade.log")) : reshade;
        if (nrLog is not null)
        {
            if (Contains(nrLog, "0xbad00001"))
                checks.Add(new("DLSS 5 (Neural Rendering)", DiagnosticStatus.Failed,
                    "Das DLSS-5-Modell lässt sich nicht anlegen (0xbad00001) – nur RTX 50, Treiber ≥ 616.56 und das Modell aus dem Treiber verwenden."));
            else if (Contains(nrLog, "workset pool exhausted"))
                checks.Add(new("DLSS 5 (Neural Rendering)", DiagnosticStatus.Failed,
                    "RenoDX gibt nach wenigen Bildern auf („workset pool exhausted“) – bekannt bei OpenGL mit RenoDX 4.7; eine andere RenoDX-Version importieren."));
            else if (Contains(nrLog, "evaluation succeeded"))
                checks.Add(new("DLSS 5 (Neural Rendering)", DiagnosticStatus.Ok, "DLSS 5 rechnet (\"evaluation succeeded\")."));
            else if (Contains(nrLog, "feature 18 created"))
                checks.Add(new("DLSS 5 (Neural Rendering)", DiagnosticStatus.Warning, "DLSS 5 ist angelegt, hat aber noch nicht gerechnet. Im Add-on-Menü Neural Rendering einschalten."));
            else
                checks.Add(new("DLSS 5 (Neural Rendering)", DiagnosticStatus.Warning, "Kein Hinweis auf DLSS 5 im Log. Im ReShade-Menü (Pos1) das DLSS-5-Panel öffnen und einschalten."));
        }
        return checks;
    }

    /// <summary>Liest das Ende der ersten vorhandenen Logdatei, die seit der Installation geschrieben wurde.</summary>
    private static string? ReadFreshLog(string gameDir, DateTime sinceUtc, params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(gameDir, name);
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < sinceUtc)
                continue;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > TailBytes)
                    fs.Seek(-TailBytes, SeekOrigin.End);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
            }
        }
        return null;
    }

    private static bool Contains(string text, string marker) => text.Contains(marker, StringComparison.OrdinalIgnoreCase);
}
