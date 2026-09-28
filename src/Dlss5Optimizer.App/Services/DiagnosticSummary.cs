using System.Globalization;
using System.Text;
using Dlss5Optimizer.App.ViewModels;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Models;
using Dlss5Optimizer.Platform;

namespace Dlss5Optimizer.App.Services;

/// <summary>Die „zusammenfassung.txt“ im Diagnose-Paket: alles, was nicht in einer Log-Datei steht.</summary>
public static class DiagnosticSummary
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Build(AppServices s, GameItemViewModel g)
    {
        var sb = new StringBuilder();
        var a = g.Analysis;
        void Line(string text = "") => sb.AppendLine(text);
        void Head(string text)
        {
            Line();
            Line($"== {text} ==");
        }

        Line("DLSS5 Optimizer – Diagnose-Paket");
        Line($"Erstellt: {DateTime.Now.ToString("dd.MM.yyyy HH:mm", De)}, Tool {typeof(AppServices).Assembly.GetName().Version}");

        Head("System");
        var sys = s.System;
        Line($"Windows: {Environment.OSVersion.VersionString}");
        Line($"Grafikkarte: {sys.Gpu.Name} ({sys.Gpu.VramBytes / (1024.0 * 1024 * 1024):0} GB), Treiber {sys.Gpu.DriverVersion?.ToString() ?? "unbekannt"}");
        Line($"Anzeige: {sys.Display.Width}×{sys.Display.Height} @ {sys.Display.RefreshHz} Hz, HAGS: {sys.HardwareSchedulingEnabled?.ToString() ?? "unbekannt"}");
        // Treiber oder Komponentenspeicher – wie beim Installieren.
        var model = s.Availability.ResolveFile(RouteCatalog.Ids.DlssNrModel, SystemFileLocator.DlssNrFile);
        Line($"DLSS-5-Modell: {model ?? "nicht gefunden"}{(model is not null && PeFile.TryRead(model)?.FileVersion is { } mv ? $" (v{mv})" : "")}");
        Line($"D3DX9-Laufzeit (32 Bit): {(CrashLog.HasD3dx9Runtime(is32Bit: true) ? "vorhanden" : "fehlt")}");
        var layers = VulkanLayer.RegisteredReShadeLayers();
        Line($"ReShade-Vulkan-Layer: {(layers.Count == 0 ? "keiner" : string.Join("; ", layers))}");

        Head("Spiel");
        Line($"{a.Game.Name} ({a.Game.Source}{(a.Game.SourceId is { } id ? " " + id : "")})");
        Line($"EXE: {a.MainExe ?? "nicht gefunden"}");
        Line($"Bitness: {a.Bitness}, Engine: {a.Engine}, Datenbank: {a.DbEntry?.Name ?? "kein Eintrag"}");
        Line($"API: {a.Api.Primary} (Sicherheit {a.Api.Confidence:P0}{(a.Api.FromProbe ? ", aus Testlauf" : "")}), möglich: {a.Api.Supported}");
        Line($"Upscaler: {a.Upscalers.Features}{(a.Upscalers.DlssVersion is { } dv ? $", DLSS {dv}" : "")}");
        Line($"Vorhandene Mods: {a.Mods}");
        foreach (var e in a.Evidence)
            Line("  · " + e);
        foreach (var w in a.Warnings)
            Line("  ⚠ " + w);

        Head("Empfehlung");
        if (g.Recommendation is { } rec)
        {
            if (rec.BlockedReason is { } blocked)
                Line("Gesperrt: " + blocked);
            if ((rec.Best ?? rec.BestWithImports) is { } best)
                Line($"{best.Route.Name}, {best.Config.Api}, {best.Config.SuperResolution}, Modell {best.Config.NrScale:P0}, {best.Config.Placement}, FG {best.Config.FrameGen} → ≈ {best.Prediction.DisplayedFps:0} fps{(best.Prediction.IsEstimate ? " (geschätzt)" : "")}");
            foreach (var n in rec.Notes)
                Line("  · " + n);
        }

        Head("Installation");
        if (g.Installed is { } m && a.GameDir is { } dir && a.MainExe is { } exe)
        {
            Line($"{RouteCatalog.Get(m.Config.Route).Name} ({m.Config.Route}), {m.Config.Api}, {m.Config.SuperResolution}, Modell {m.Config.NrScale:P0}, {m.Config.Placement}, FG {m.Config.FrameGen}");
            Line($"Installiert: {m.InstalledAt.ToString("dd.MM.yyyy HH:mm", De)}, {m.Files.Count} Dateien");
            foreach (var p in g.IntegrityProblems)
                Line("  ⚠ " + p);

            var (crashes, crashLines) = CrashLog.Recent(Path.GetFileName(exe), m.InstalledAt.LocalDateTime, max: 20);
            var report = InstallDiagnostics.Evaluate(dir, m, Path.GetFileName(exe), crashes, a.DbEntry);
            Head("Diagnose");
            Line($"Ergebnis: {report.Verdict} – {report.Summary}");
            foreach (var c in report.Checks)
                Line($"  [{c.Status}] {c.Title}: {c.Detail}");
            Head("Windows-Ereignisprotokoll seit der Installation");
            Line(crashLines.Count == 0 ? "keine Abstürze oder Hänger" : string.Join(Environment.NewLine, crashLines));
            if (m.Config.Route.UsesDxvk() || m.Config.Api == GraphicsApi.Vulkan)
            {
                var others = VulkanLayer.OtherReShadeLayers(a.Bitness == Bitness.X86);
                Line($"Weitere ReShade-Layer gleicher Bitness: {(others.Count == 0 ? "keine" : string.Join("; ", others))}");
            }
        }
        else
        {
            Line("nichts installiert");
        }

        Head("Gemerkt");
        var key = a.Game.Key;
        if (s.Settings.RouteOutcomes.GetValueOrDefault(key) is { Count: > 0 } outcomes)
            foreach (var (route, o) in outcomes)
                Line($"{route}: {(o.Worked ? "läuft" : "gescheitert")} ({o.Reason}, {o.When.ToString("dd.MM. HH:mm", De)})");
        if (s.Settings.DepthVariants.GetValueOrDefault(key) is { } depth)
            Line($"Tiefenpuffer-Variante: {depth.Label}");
        if (s.Settings.Calibrations.GetValueOrDefault(key) is { Count: > 0 } cal)
            Line($"Messungen: {string.Join(", ", cal.Keys)}");
        Line($"Einstellungen: {s.Settings.Preferences}");

        Head("Komponenten");
        foreach (var def in s.Catalog.Components)
        {
            var stored = s.Store.Get(def.Id);
            Line($"{def.Id}: {(stored is null ? "nicht im Speicher" : $"{stored.Version} ({stored.Origin}, {stored.StoredAt.ToString("dd.MM.yyyy", De)})")}");
        }
        return sb.ToString();
    }
}
