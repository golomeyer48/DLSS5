using System.Text;
using Dlss5Optimizer.Core.Decision;

namespace Dlss5Optimizer.Core.Install;

public enum DiagnosticStatus
{
    Ok,
    Warning,
    Failed,
    NotRunYet,
}

public sealed record DiagnosticCheck(string Title, DiagnosticStatus Status, string Detail);

/// <summary>
/// Prüft nach einem Spielstart anhand der Logs, ob die ganze Kette greift – Übersetzer (DXVK),
/// ReShade, Feeder, Hilfsprozess und der eigentliche DLSS-5-Pass. Die Erkennungsmerkmale stammen
/// aus den Handbüchern und Fehlerberichten der Projekte (siehe README).
/// </summary>
public static class InstallDiagnostics
{
    private const int TailBytes = 2 * 1024 * 1024;

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

        if (route == RouteId.LegacyDxvkFeeder)
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
            else if (route is RouteId.LegacyDxvkFeeder or RouteId.LegacyFeeder && Contains(reshade, "IDirect3DDevice9"))
                checks.Add(new("ReShade", DiagnosticStatus.Failed,
                    "ReShade hängt direkt an DirectX 9 – der Übersetzer (DXVK/dgVoodoo) ist nicht aktiv. Das Spiel lädt vermutlich die System-d3d9.dll."));
            else
                checks.Add(new("ReShade", DiagnosticStatus.Ok, "ReShade ist geladen."));
        }

        if (route is RouteId.Feeder or RouteId.LegacyFeeder or RouteId.LegacyDxvkFeeder)
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
