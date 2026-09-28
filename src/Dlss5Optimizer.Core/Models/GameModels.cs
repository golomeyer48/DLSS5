namespace Dlss5Optimizer.Core.Models;

public enum GameSource
{
    Steam,
    Epic,
    Gog,
    Xbox,
    Manual,
}

/// <summary>Ein installiertes Spiel, wie es ein Bibliotheks-Scanner findet.</summary>
public sealed record GameInfo(
    string Name,
    string InstallDir,
    GameSource Source,
    string? SourceId = null,
    string? PreferredExe = null)
{
    public string Key => $"{Source}:{SourceId ?? InstallDir}".ToLowerInvariant();
}

[Flags]
public enum UpscalerFeature
{
    None = 0,
    DlssSuperResolution = 1 << 0,
    DlssFrameGeneration = 1 << 1,
    DlssRayReconstruction = 1 << 2,
    DlssNeuralRenderingDll = 1 << 3,
    Streamline = 1 << 4,
    Fsr = 1 << 5,
    Xess = 1 << 6,
    Reflex = 1 << 7,
}

public sealed record UpscalerInfo(
    UpscalerFeature Features,
    Version? DlssVersion,
    string? DlssPath,
    Version? DlssFgVersion)
{
    public static UpscalerInfo None { get; } = new(UpscalerFeature.None, null, null, null);

    public bool Has(UpscalerFeature f) => (Features & f) == f;

    /// <summary>Das Spiel liefert echte Upscaler-Eingaben (Farbe, Tiefe, Bewegungsvektoren), die ein Mod abgreifen kann.</summary>
    public bool HasUpscalerInputs => (Features & (UpscalerFeature.DlssSuperResolution | UpscalerFeature.Fsr | UpscalerFeature.Xess)) != 0;
}

[Flags]
public enum ExistingMod
{
    None = 0,
    ReShade = 1 << 0,
    OptiScaler = 1 << 1,
    Dxvk = 1 << 2,
    DgVoodoo = 1 << 3,
    SpecialK = 1 << 4,
    RenoDx = 1 << 5,
    Dlss5Bridge = 1 << 6,
    Dlss5Feeder = 1 << 7,
    DeepFriedChicken = 1 << 8,
    REFramework = 1 << 9,
    Dlss5Optimizer = 1 << 10,
    Enb = 1 << 11,
}

public sealed record AntiCheatInfo(IReadOnlyList<string> Systems)
{
    public static AntiCheatInfo None { get; } = new([]);
    public bool Detected => Systems.Count > 0;
}

public sealed record ApiDetection(
    GraphicsApi Primary,
    GraphicsApi Supported,
    double Confidence,
    IReadOnlyDictionary<GraphicsApi, double> Scores,
    bool FromProbe = false)
{
    public static ApiDetection Unknown { get; } = new(GraphicsApi.None, GraphicsApi.None, 0, new Dictionary<GraphicsApi, double>());
}

/// <summary>Vollständiges Analyse-Ergebnis für ein Spiel.</summary>
public sealed record GameAnalysis(
    GameInfo Game,
    string? MainExe,
    Bitness Bitness,
    GameEngine Engine,
    ApiDetection Api,
    UpscalerInfo Upscalers,
    ExistingMod Mods,
    AntiCheatInfo AntiCheat,
    GameDbEntry? DbEntry,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Warnings)
{
    public string? GameDir => MainExe is null ? null : Path.GetDirectoryName(MainExe);
    public bool HasNativeDlss5 => DbEntry?.NativeDlss5 == true;
}
