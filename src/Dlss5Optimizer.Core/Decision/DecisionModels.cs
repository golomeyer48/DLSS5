using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Decision;

public enum RouteId
{
    NativeDlss5,
    OptiScalerNr,
    ReShadeNrAddon,
    BridgeD3D11,
    BridgeVulkan,
    Feeder,
    LegacyFeeder,
}

/// <summary>Woher die Bewegungsvektoren kommen – der wichtigste Qualitätsfaktor für DLSS 5.</summary>
public enum MotionVectorSource
{
    /// <summary>Echte Vektoren aus der DLSS-/FSR-/XeSS-Integration des Spiels.</summary>
    Engine,
    /// <summary>Per Shader aus dem Bild geschätzt (LumeniteFX u. a.): Ghosting bei schneller Bewegung.</summary>
    Estimated,
}

public enum NrPlacement
{
    /// <summary>DLSS 5 läuft auf dem hochskalierten Bild (Standard, beste Qualität).</summary>
    PostUpscale,
    /// <summary>DLSS 5 läuft vor der Super Resolution auf dem kleinen Bild (experimentell, deutlich schneller).</summary>
    PreUpscale,
}

public enum SrMode
{
    Native,
    Dlaa,
    Quality,
    Balanced,
    Performance,
    UltraPerformance,
}

public enum FrameGenMode
{
    Off,
    DlssFg2x,
    DlssMfg3x,
    DlssMfg4x,
    DlssMfg6x,
    SmoothMotion,
}

public enum OptimizationProfile
{
    /// <summary>Beste Bildqualität, solange mindestens die Ziel-FPS erreicht werden.</summary>
    Quality,
    /// <summary>Gute Qualität bei hoher Bildrate (Ziel: ~75 % der Bildwiederholrate).</summary>
    Balanced,
    /// <summary>Feste Ziel-FPS mit bestmöglicher Qualität.</summary>
    TargetFps,
}

public sealed record UserPreferences(
    OptimizationProfile Profile = OptimizationProfile.Balanced,
    int? TargetFps = null,
    bool AllowExperimental = true,
    bool AllowFrameGeneration = true,
    bool IgnoreAntiCheat = false)
{
    public int EffectiveTargetFps(DisplayInfo display) => Profile switch
    {
        OptimizationProfile.Quality => TargetFps ?? 60,
        OptimizationProfile.TargetFps => TargetFps ?? 90,
        _ => TargetFps ?? Math.Max(60, (int)Math.Round(display.RefreshHz * 0.75)),
    };
}

/// <summary>Bedingung "mindestens eine dieser Komponenten".</summary>
public sealed record ComponentRequirement(string Label, IReadOnlyList<string> AnyOf)
{
    public static ComponentRequirement One(string id, string label) => new(label, [id]);
}

public sealed record RouteDefinition(
    RouteId Id,
    string Name,
    string Description,
    GraphicsApi Apis,
    bool Requires64Bit,
    bool RequiresGameDlss,
    bool AcceptsFsrOrXess,
    MotionVectorSource MotionVectors,
    double OverheadMs,
    bool SupportsSuperResolution,
    IReadOnlyList<ComponentRequirement> Components,
    IReadOnlyList<string> Caveats,
    bool Experimental = false,
    bool SemiAutomatic = false,
    string? PreUpscaleComponent = null,
    string? ModelScaleComponent = null,
    GraphicsApi ModelScaleApis = GraphicsApi.None,
    GraphicsApi SmoothMotionIncompatibleApis = GraphicsApi.None,
    bool Official = false);

/// <summary>Eine konkrete, bewertbare Einstellung: Route + API + Parameter.</summary>
public sealed record Configuration(
    RouteId Route,
    GraphicsApi Api,
    SrMode SuperResolution,
    double NrScale,
    NrPlacement Placement,
    FrameGenMode FrameGen);

public sealed record Prediction(
    double RenderedFps,
    double DisplayedFps,
    double NrCostMs,
    double Quality,
    bool IsEstimate);

public sealed record MissingComponent(string Id, string Label, bool CanAutoDownload);

public sealed record Candidate(
    RouteDefinition Route,
    Configuration Config,
    Prediction Prediction,
    IReadOnlyList<MissingComponent> Missing,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Installierbar, wenn höchstens automatisch ladbare Komponenten fehlen.</summary>
    public bool Installable => Missing.All(m => m.CanAutoDownload);
}

public sealed record ApiSwitchAdvice(GraphicsApi From, GraphicsApi To, string How);

public sealed record Recommendation(
    Candidate? Best,
    Candidate? BestWithImports,
    IReadOnlyList<Candidate> Alternatives,
    ApiSwitchAdvice? ApiSwitch,
    string? BlockedReason,
    IReadOnlyList<string> Notes,
    Prediction? Baseline,
    int TargetFps)
{
    public bool Blocked => BlockedReason is not null;
}

/// <summary>Messwerte eines Spiels, mit denen die Vorhersage kalibriert wird.</summary>
public sealed record Calibration(
    double BaselineFps,
    SrMode BaselineSrMode,
    GraphicsApi Api,
    double? NrMsPerMegapixel = null);

public static class DecisionText
{
    public static string DisplayName(this SrMode m) => m switch
    {
        SrMode.Native => "Nativ (kein Upscaling)",
        SrMode.Dlaa => "DLAA",
        SrMode.Quality => "DLSS Qualität",
        SrMode.Balanced => "DLSS Ausgewogen",
        SrMode.Performance => "DLSS Leistung",
        SrMode.UltraPerformance => "DLSS Ultra-Leistung",
        _ => m.ToString(),
    };

    public static string DisplayName(this FrameGenMode m) => m switch
    {
        FrameGenMode.Off => "Aus",
        FrameGenMode.DlssFg2x => "DLSS Frame Generation 2×",
        FrameGenMode.DlssMfg3x => "DLSS Multi Frame Generation 3×",
        FrameGenMode.DlssMfg4x => "DLSS Multi Frame Generation 4×",
        FrameGenMode.DlssMfg6x => "DLSS Multi Frame Generation 6×",
        FrameGenMode.SmoothMotion => "NVIDIA Smooth Motion (Treiber)",
        _ => m.ToString(),
    };

    public static string DisplayName(this NrPlacement p) => p switch
    {
        NrPlacement.PreUpscale => "vor dem Hochskalieren",
        _ => "nach dem Hochskalieren",
    };

    /// <summary>DLSS-4.5-Preset laut NVIDIA-Empfehlung.</summary>
    public static string RecommendedPreset(this SrMode m) => m switch
    {
        SrMode.Performance => "M",
        SrMode.UltraPerformance => "L",
        SrMode.Native => "–",
        _ => "K",
    };
}
