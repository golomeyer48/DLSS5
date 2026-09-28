using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.App.ViewModels;

public enum StatusKind
{
    Neutral,
    Good,
    Warning,
    Blocked,
    Installed,
}

/// <summary>Eine Zeile in der Liste der möglichen Konfigurationen.</summary>
public sealed class CandidateRow(Candidate candidate, bool isBest)
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public Candidate Candidate { get; } = candidate;
    public bool IsBest { get; } = isBest;
    public string RouteName => Candidate.Route.Name;
    public string ApiText => Candidate.Config.Api.DisplayName();
    public string SrText => Candidate.Config.SuperResolution.DisplayName()
                            + (Candidate.Config.SuperResolution is SrMode.Native ? "" : $" · Preset {Candidate.Config.SuperResolution.RecommendedPreset()}");
    public string NrText => $"{Candidate.Config.NrScale.ToString("P0", De)}, {Candidate.Config.Placement.DisplayName()}";
    public string FgText => Candidate.Config.FrameGen.DisplayName();
    public string FpsText
    {
        get
        {
            var p = Candidate.Prediction;
            var text = Candidate.Config.FrameGen == FrameGenMode.Off
                ? $"≈ {p.DisplayedFps:0} fps"
                : $"≈ {p.DisplayedFps:0} fps (gerendert {p.RenderedFps:0})";
            return p.IsEstimate ? text + " *" : text;
        }
    }
    public string CostText => $"DLSS 5: {Candidate.Prediction.NrCostMs.ToString("0.0", De)} ms/Frame";
    public string QualityText => $"{Candidate.Prediction.Quality:0}/100";
    public string StatusText => Candidate.Missing.Count == 0
        ? "bereit"
        : Candidate.Installable
            ? "lädt: " + string.Join(", ", Candidate.Missing.Select(m => m.Label))
            : "fehlt: " + string.Join(", ", Candidate.Missing.Where(m => !m.CanAutoDownload).Select(m => m.Label));
    public string Reasons => string.Join("\n", Candidate.Reasons);
    public string Warnings => string.Join("\n", Candidate.Warnings);
    public bool HasWarnings => Candidate.Warnings.Count > 0;
}

public sealed partial class GameItemViewModel(GameAnalysis analysis) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(SourceText), nameof(ApiText), nameof(DetailsText), nameof(UpscalerText), nameof(AntiCheatText), nameof(Evidence), nameof(Warnings))]
    private GameAnalysis _analysis = analysis;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusKind), nameof(Notes), nameof(BlockedReason), nameof(ApiSwitchText), nameof(BaselineText), nameof(TargetText))]
    private Recommendation? _recommendation;

    [ObservableProperty]
    private CandidateRow? _selectedCandidate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusKind), nameof(InstalledText))]
    private InstallManifest? _installed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusKind))]
    private IReadOnlyList<string> _integrityProblems = [];

    public ObservableCollection<CandidateRow> Candidates { get; } = [];

    public string Name => Analysis.Game.Name;
    public string SourceText => Analysis.Game.Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic",
        GameSource.Gog => "GOG",
        GameSource.Xbox => "Xbox",
        _ => "Ordner",
    };

    public string ApiText
    {
        get
        {
            var api = Analysis.Api;
            if (api.Primary == GraphicsApi.None)
                return "API unbekannt";
            var others = api.Supported & ~api.Primary;
            var text = api.Primary.DisplayName() + (others != GraphicsApi.None ? $" (+{others.DisplayName()})" : "");
            return api.FromProbe ? text + " ✓" : text;
        }
    }

    public string DetailsText
    {
        get
        {
            var parts = new List<string> { ApiText };
            if (Analysis.Engine != GameEngine.Unknown)
                parts.Add(Analysis.Engine.DisplayName());
            parts.Add(Analysis.Bitness switch { Bitness.X86 => "32 Bit", Bitness.X64 => "64 Bit", _ => "Bitness ?" });
            parts.Add($"Erkennung {Analysis.Api.Confidence:P0}");
            return string.Join(" · ", parts);
        }
    }

    public string UpscalerText
    {
        get
        {
            var u = Analysis.Upscalers;
            var parts = new List<string>();
            if (u.Has(UpscalerFeature.DlssSuperResolution))
                parts.Add("DLSS SR" + (u.DlssVersion is { } v ? $" {v.Major}.{v.Minor}.{v.Build}" : ""));
            if (u.Has(UpscalerFeature.DlssFrameGeneration))
                parts.Add("DLSS FG");
            if (u.Has(UpscalerFeature.DlssRayReconstruction))
                parts.Add("DLSS RR");
            if (u.Has(UpscalerFeature.Fsr))
                parts.Add("FSR");
            if (u.Has(UpscalerFeature.Xess))
                parts.Add("XeSS");
            if (u.Has(UpscalerFeature.Reflex))
                parts.Add("Reflex");
            if (Analysis.HasNativeDlss5)
                parts.Insert(0, "DLSS 5 nativ");
            return parts.Count == 0 ? "Kein Upscaler" : string.Join(" · ", parts);
        }
    }

    public string AntiCheatText => Analysis.AntiCheat.Detected ? "Anti-Cheat: " + string.Join(", ", Analysis.AntiCheat.Systems) : "";
    public IReadOnlyList<string> Evidence => Analysis.Evidence;
    public IReadOnlyList<string> Warnings => Analysis.Warnings;
    public IReadOnlyList<string> Notes => Recommendation?.Notes ?? [];
    public string? BlockedReason => Recommendation?.BlockedReason;

    public string? ApiSwitchText => Recommendation?.ApiSwitch is { } s
        ? $"API wechseln: {s.From.DisplayName()} → {s.To.DisplayName()}. {s.How}"
        : null;

    public string BaselineText => Recommendation?.Baseline is { } b
        ? $"Ohne DLSS 5: ≈ {b.DisplayedFps:0} fps" + (b.IsEstimate ? " (Annahme – bitte messen)" : " (gemessen)")
        : "";

    public string TargetText => Recommendation is { } r ? $"Ziel: {r.TargetFps} fps" : "";

    public string InstalledText => Installed is { } m
        ? $"Installiert: {RouteCatalog.Get(m.Config.Route).Name} ({m.Config.Api.DisplayName()}), {m.InstalledAt:dd.MM.yyyy HH:mm}"
        : "";

    public bool IsInstalled => Installed is not null;

    public StatusKind StatusKind =>
        Installed is not null ? (IntegrityProblems.Count > 0 ? StatusKind.Warning : StatusKind.Installed)
        : Recommendation is null ? StatusKind.Neutral
        : Recommendation.Blocked ? StatusKind.Blocked
        : Recommendation.Best is not null ? StatusKind.Good
        : StatusKind.Warning;

    public string StatusText =>
        Installed is not null ? (IntegrityProblems.Count > 0 ? "Reparatur nötig" : "DLSS 5 installiert")
        : Recommendation is null ? "…"
        : Recommendation.Blocked ? "Gesperrt"
        : Analysis.HasNativeDlss5 ? "Natives DLSS 5"
        : Recommendation.Best is not null ? "Bereit"
        : Recommendation.BestWithImports is not null ? "Add-on fehlt"
        : "Keine Route";

    public void SetRecommendation(Recommendation rec)
    {
        Recommendation = rec;
        Candidates.Clear();
        var best = rec.Best ?? rec.BestWithImports;
        if (best is not null)
            Candidates.Add(new CandidateRow(best, isBest: true));
        foreach (var alt in rec.Alternatives.Where(a => !ReferenceEquals(a, best)))
            Candidates.Add(new CandidateRow(alt, isBest: false));
        SelectedCandidate = Candidates.FirstOrDefault();
        OnPropertyChanged(nameof(IsInstalled));
    }

    partial void OnInstalledChanged(InstallManifest? value) => OnPropertyChanged(nameof(IsInstalled));
}

public sealed partial class ComponentItemViewModel(ComponentDefinition definition) : ObservableObject
{
    public ComponentDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string License => Definition.License;
    public bool ClosedSource => Definition.ClosedSource;
    public bool CanDownload => Definition.CanAutoDownload;
    public string? Homepage => Definition.Homepage;

    public string SourceText => Definition.Source.Type switch
    {
        ComponentSourceType.GitHub => $"GitHub: {Definition.Source.Repo}",
        ComponentSourceType.Url => Definition.Source.Url ?? "",
        ComponentSourceType.DriverStore => "NVIDIA-Treiber (automatisch)",
        ComponentSourceType.GameLibrary => "Installierte Spiele" + (Definition.Source.Url is not null ? " oder NVIDIA-Download" : ""),
        _ => Definition.Source.Distribution ?? "Import",
    };

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isAvailable;

    public void Refresh(ComponentStore store, ComponentAvailability availability)
    {
        IsAvailable = availability.IsAvailable(Definition.Id);
        var stored = store.Get(Definition.Id);
        Status = stored is not null
            ? $"✓ {stored.Version} ({stored.StoredAt:dd.MM.yyyy})"
            : IsAvailable
                ? "✓ vorhanden (Treiber/Spiel)"
                : Definition.Source.Type == ComponentSourceType.Import ? "fehlt – bitte importieren" : "fehlt – wird bei Bedarf geladen";
    }
}
