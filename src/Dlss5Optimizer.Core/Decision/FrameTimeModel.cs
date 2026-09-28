using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Decision;

/// <summary>
/// Einfaches Frametime-Modell: Frame-Zeit = Render-Zeit + DLSS-5-Aufschlag + Routen-Overhead.
/// Der DLSS-5-Aufschlag wächst ungefähr mit der Zahl der Pixel, die das Modell verarbeitet.
///
/// Abgleich mit veröffentlichten Messungen:
///   RTX 5090, 4K, NBA 2K27 (nativ): 128 → 63 fps  ⇒ 7,8 ms + 8,3 ms = 16,1 ms  ✔
///   RTX 5070 Ti, 4K, Mod (OptiScaler): 180 → 80 fps ⇒ ≈ 6,9 ms Aufschlag ⇒ ≈ 0,83 ms/MP
///   RTX 5070 Ti, 4K, Pre-Upscale-Fork (Performance, 1080p intern): 122 fps  ⇒ ≈ 2,6 ms
/// Die Kosten hängen stark vom Spiel und vom Weg (nativ/Mod) ab, deshalb wird nach dem ersten
/// Benchmark mit gemessenen Werten gerechnet (<see cref="Calibration"/>).
/// </summary>
public sealed class FrameTimeModel
{
    /// <summary>Referenz: RTX 5070 Ti (1406 AI TOPS) über den Mod-Weg.</summary>
    public const double ReferenceMsPerMegapixel = 0.83;
    public const double ReferenceTops = 1406;

    /// <summary>Der native Weg (NBA 2K27) war in den Tests rund 2,5× teurer als der Mod-Weg.</summary>
    public const double NativeCostFactor = 2.5;

    /// <summary>Anteil der Frame-Zeit, der mit der Renderauflösung skaliert (GPU-gebunden).</summary>
    public const double GpuBoundFraction = 0.85;

    /// <summary>Unter dieser Basis-Bildrate fühlt sich Frame Generation träge an.</summary>
    public const double MinBaseFpsForFrameGen = 55;

    /// <summary>Angenommene Basis-Bildrate, solange nichts gemessen wurde.</summary>
    public const double AssumedBaselineFps = 100;

    private readonly SystemInfo _system;
    private readonly Calibration? _calibration;

    public FrameTimeModel(SystemInfo system, Calibration? calibration = null)
    {
        _system = system;
        _calibration = calibration;
    }

    public bool IsEstimate => _calibration is null;

    public static double ScaleFactor(SrMode mode) => mode switch
    {
        SrMode.Quality => 2.0 / 3.0,
        SrMode.Balanced => 0.58,
        SrMode.Performance => 0.5,
        SrMode.UltraPerformance => 1.0 / 3.0,
        _ => 1.0,
    };

    public static double FrameGenMultiplier(FrameGenMode fg) => fg switch
    {
        // Effektive Vervielfachung inkl. Kosten der Zwischenbild-Erzeugung.
        FrameGenMode.DlssFg2x => 1.8,
        FrameGenMode.DlssMfg3x => 2.6,
        FrameGenMode.DlssMfg4x => 3.4,
        FrameGenMode.DlssMfg6x => 4.8,
        FrameGenMode.SmoothMotion => 1.7,
        _ => 1.0,
    };

    public double NrMsPerMegapixel(RouteDefinition route)
    {
        if (_calibration?.NrMsPerMegapixel is { } measured)
            return measured;
        double perMp = ReferenceMsPerMegapixel * ReferenceTops / Math.Max(_system.Gpu.TensorTops, 100);
        return route.Official ? perMp * NativeCostFactor : perMp;
    }

    public double BaselineFrameTimeMs(SrMode mode, GraphicsApi api)
    {
        double baseFps = _calibration?.BaselineFps ?? AssumedBaselineFps;
        var baseMode = _calibration?.BaselineSrMode ?? SrMode.Quality;
        double tBase = 1000.0 / baseFps;
        double pixelRatio = Math.Pow(ScaleFactor(mode), 2) / Math.Pow(ScaleFactor(baseMode), 2);
        return tBase * (1 - GpuBoundFraction + GpuBoundFraction * pixelRatio);
    }

    /// <summary>Megapixel, die das DLSS-5-Modell pro Frame verarbeitet.</summary>
    public double NrMegapixels(Configuration config)
    {
        double outputMp = _system.Display.Megapixels;
        double nrMp = config.Placement == NrPlacement.PreUpscale
            ? outputMp * Math.Pow(ScaleFactor(config.SuperResolution), 2)
            : outputMp;
        return nrMp * config.NrScale * config.NrScale;
    }

    /// <summary>Fester Aufwand der Route pro Frame (Kopien, Synchronisation, Hilfsprozess).</summary>
    public static double OverheadMs(RouteDefinition route, Configuration config, Bitness bitness)
    {
        double overhead = route.OverheadMs + (route.ApiOverheadMs?.GetValueOrDefault(config.Api) ?? 0);
        if (bitness == Bitness.X86 && !route.Official)
            overhead += 0.5; // 32-Bit: Hilfsprozess + gemeinsame Texturen
        if (config.Placement == NrPlacement.PreUpscale)
            overhead += 0.9; // gemessen: Pre-Upscale kostet mehr als das reine Pixelverhältnis
        return overhead;
    }

    public Prediction Predict(RouteDefinition route, Configuration config, Bitness bitness, double quality)
    {
        double tRender = BaselineFrameTimeMs(config.SuperResolution, config.Api);
        double tNr = NrMsPerMegapixel(route) * NrMegapixels(config) + OverheadMs(route, config, bitness);
        double rendered = 1000.0 / (tRender + tNr);
        double displayed = rendered * FrameGenMultiplier(config.FrameGen);
        if (config.FrameGen != FrameGenMode.Off)
            displayed = Math.Min(displayed, _system.Display.RefreshHz);
        return new Prediction(rendered, displayed, tNr, quality, IsEstimate);
    }

    /// <summary>Vorhersage ohne DLSS 5 – zum Vergleich in der Oberfläche.</summary>
    public Prediction PredictBaseline(SrMode mode, GraphicsApi api)
    {
        double fps = 1000.0 / BaselineFrameTimeMs(mode, api);
        return new Prediction(fps, fps, 0, 0, IsEstimate);
    }

    /// <summary>
    /// Kehrt <see cref="Predict"/> um: Aus der mit DLSS 5 gemessenen (echt gerenderten) Bildrate und der
    /// kalibrierten Basis ergeben sich die DLSS-5-Kosten pro Megapixel – auch wenn der Upscaling-Modus
    /// seit der Basismessung geändert wurde.
    /// </summary>
    public double DeriveMsPerMegapixel(RouteDefinition route, Configuration config, Bitness bitness, double measuredRenderedFps)
    {
        double tRender = BaselineFrameTimeMs(config.SuperResolution, config.Api);
        double delta = 1000.0 / measuredRenderedFps - tRender - OverheadMs(route, config, bitness);
        return Math.Max(0.05, delta / Math.Max(NrMegapixels(config), 0.1));
    }

    /// <summary>Leitet die DLSS-5-Kosten pro Megapixel aus zwei Messungen (ohne/mit DLSS 5) ab.</summary>
    public static double DeriveMsPerMegapixel(double fpsWithout, double fpsWith, double nrMegapixels, double overheadMs)
    {
        double delta = 1000.0 / fpsWith - 1000.0 / fpsWithout - overheadMs;
        return Math.Max(0.05, delta / Math.Max(nrMegapixels, 0.1));
    }
}
