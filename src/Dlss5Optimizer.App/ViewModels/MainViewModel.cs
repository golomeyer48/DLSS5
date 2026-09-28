using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dlss5Optimizer.Platform;
using Dlss5Optimizer.App.Services;
using Dlss5Optimizer.Core.Benchmark;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Models;
using Microsoft.Win32;

namespace Dlss5Optimizer.App.ViewModels;

public sealed record Choice<T>(T Value, string Label);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly IDialogs _dialogs;
    private bool _loadingPrefs;

    public MainViewModel(AppServices services, IDialogs dialogs)
    {
        _s = services;
        _dialogs = dialogs;
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = o => o is GameItemViewModel g && (string.IsNullOrWhiteSpace(SearchText) || g.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        GamesView.SortDescriptions.Add(new SortDescription(nameof(GameItemViewModel.Name), ListSortDirection.Ascending));

        foreach (var def in _s.Catalog.Components)
            Components.Add(new ComponentItemViewModel(def));

        _loadingPrefs = true;
        var p = _s.Settings.Preferences;
        Profile = p.Profile;
        TargetFps = p.TargetFps ?? 0;
        AllowExperimental = p.AllowExperimental;
        AllowFrameGeneration = p.AllowFrameGeneration;
        IgnoreAntiCheat = p.IgnoreAntiCheat;
        _loadingPrefs = false;

        RefreshSystemInfo();
        RefreshComponents();
    }

    public ObservableCollection<GameItemViewModel> Games { get; } = [];
    public ICollectionView GamesView { get; }
    public ObservableCollection<ComponentItemViewModel> Components { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    public IReadOnlyList<Choice<OptimizationProfile>> Profiles { get; } =
    [
        new(OptimizationProfile.Quality, "Qualität (Ziel 60 fps)"),
        new(OptimizationProfile.Balanced, "Ausgewogen (Ziel ¾ der Hz)"),
        new(OptimizationProfile.TargetFps, "Ziel-FPS"),
    ];

    public IReadOnlyList<Choice<SrMode>> SrModes { get; } =
    [
        new(SrMode.Native, "Nativ / aus"),
        new(SrMode.Dlaa, "DLAA"),
        new(SrMode.Quality, "Qualität"),
        new(SrMode.Balanced, "Ausgewogen"),
        new(SrMode.Performance, "Leistung"),
        new(SrMode.UltraPerformance, "Ultra-Leistung"),
    ];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private GameItemViewModel? _selectedGame;
    [ObservableProperty] private OptimizationProfile _profile;
    [ObservableProperty] private int _targetFps;
    [ObservableProperty] private bool _allowExperimental;
    [ObservableProperty] private bool _allowFrameGeneration;
    [ObservableProperty] private bool _ignoreAntiCheat;
    [ObservableProperty] private SrMode _measuredSrMode = SrMode.Quality;
    [ObservableProperty] private int _measureSeconds = 20;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _progress;

    [ObservableProperty] private string _gpuText = "";
    [ObservableProperty] private string _driverText = "";
    [ObservableProperty] private string _displayText = "";
    [ObservableProperty] private string _modelText = "";
    [ObservableProperty] private string _hagsText = "";
    [ObservableProperty] private string _windowedText = "";
    [ObservableProperty] private string _gameModeText = "";
    [ObservableProperty] private string _vulkanLayerText = "";
    [ObservableProperty] private string _systemVerdict = "";

    partial void OnSearchTextChanged(string value) => GamesView.Refresh();

    partial void OnProfileChanged(OptimizationProfile value) => PreferencesChanged();
    partial void OnTargetFpsChanged(int value) => PreferencesChanged();
    partial void OnAllowExperimentalChanged(bool value) => PreferencesChanged();
    partial void OnAllowFrameGenerationChanged(bool value) => PreferencesChanged();

    partial void OnIgnoreAntiCheatChanged(bool value)
    {
        if (value && !_loadingPrefs && !_dialogs.Confirm("Anti-Cheat ignorieren",
                "Injection-Mods in Spielen mit Anti-Cheat können zu Kick oder dauerhaftem Bann (auch Hardware-Bann) führen.\n\n"
                + "Nur für Spiele, die du ausschließlich offline spielst. Wirklich freischalten?", warning: true))
        {
            IgnoreAntiCheat = false;
            return;
        }
        PreferencesChanged();
    }

    private UserPreferences CurrentPreferences() =>
        new(Profile, TargetFps > 0 ? TargetFps : null, AllowExperimental, AllowFrameGeneration, IgnoreAntiCheat);

    private void PreferencesChanged()
    {
        if (_loadingPrefs)
            return;
        _s.Settings.Preferences = CurrentPreferences();
        _s.SaveSettings();
        RecomputeAll();
    }

    // ---------------------------------------------------------------- Bibliothek

    [RelayCommand]
    private async Task ScanAsync()
    {
        await RunBusy("Bibliotheken werden gelesen …", async () =>
        {
            var found = await Task.Run(() =>
            {
                var games = new List<GameInfo>();
                foreach (var scanner in _s.Scanners())
                {
                    try
                    {
                        var list = scanner.Scan().ToList();
                        games.AddRange(list);
                        Log($"{scanner.Name}: {list.Count} Spiele");
                    }
                    catch (Exception e)
                    {
                        Log($"{scanner.Name}: Fehler beim Lesen – {e.Message}");
                    }
                }
                return games.GroupBy(g => Path.GetFullPath(g.InstallDir).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            });

            BusyText = $"{found.Count} Spiele werden analysiert …";
            int done = 0;
            var analyses = new GameAnalysis[found.Count];
            await Task.Run(() => Parallel.For(0, found.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                analyses[i] = Analyze(found[i]);
                Progress = Interlocked.Increment(ref done) * 100.0 / Math.Max(1, found.Count);
            }));

            _s.SetAnalyses(analyses);
            Games.Clear();
            foreach (var a in analyses)
                Games.Add(new GameItemViewModel(a));
            RecomputeAll();
            RefreshComponents();
            Log($"Analyse fertig: {analyses.Length} Spiele, {analyses.Count(a => a.Upscalers.Has(UpscalerFeature.DlssSuperResolution))} mit DLSS, {analyses.Count(a => a.AntiCheat.Detected)} mit Anti-Cheat.");
        });
    }

    private GameAnalysis Analyze(GameInfo game)
    {
        var a = _s.Analyzer.Analyze(game);
        if (_s.Settings.ProbedApis.TryGetValue(game.Key, out var probed))
            a = GameAnalyzer.ApplyProbe(a, probed, ["Testlauf (gespeichert): " + probed.DisplayName()]);
        return a;
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Spielordner oder Sammelordner (z. B. D:\\Games) wählen", Multiselect = true };
        if (dlg.ShowDialog() != true)
            return;
        foreach (var f in dlg.FolderNames.Where(f => !_s.Settings.ManualFolders.Contains(f, StringComparer.OrdinalIgnoreCase)))
            _s.Settings.ManualFolders.Add(f);
        _s.SaveSettings();
        await ScanAsync();
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        if (SelectedGame?.Analysis.GameDir is { } dir)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ---------------------------------------------------------------- Empfehlung

    private void RecomputeAll()
    {
        foreach (var g in Games)
            Recompute(g);
        OnPropertyChanged(nameof(SelectedGame));
    }

    private void Recompute(GameItemViewModel g)
    {
        var key = g.Analysis.Game.Key;
        var cal = _s.Settings.Calibrations.GetValueOrDefault(key);
        var outcomes = _s.Settings.RouteOutcomes.GetValueOrDefault(key);
        var rec = _s.Engine.Recommend(g.Analysis, _s.System, CurrentPreferences(), cal, outcomes);
        g.SetRecommendation(rec);
        g.OutcomesText = outcomes is { Count: > 0 }
            ? "Bei dir getestet: " + string.Join(", ", outcomes.Select(o => $"{RouteCatalog.Get(o.Key).Name} {(o.Value.Worked ? "✓" : "✗")}"))
            : "";
        if (g.Analysis.GameDir is { } dir)
        {
            g.Installed = _s.Installer.ReadManifest(dir);
            g.IntegrityProblems = g.Installed is null ? [] : _s.Installer.Verify(dir);
        }
    }

    // ---------------------------------------------------------------- Installation

    [RelayCommand]
    private async Task InstallAsync()
    {
        var game = SelectedGame;
        var candidate = game?.SelectedCandidate?.Candidate;
        if (game is null || candidate is null)
            return;
        await InstallCandidateAsync(game, candidate);
    }

    private async Task InstallCandidateAsync(GameItemViewModel game, Candidate candidate)
    {
        if (game.Recommendation?.Blocked == true)
        {
            _dialogs.Info("Gesperrt", game.Recommendation.BlockedReason!);
            return;
        }
        if (candidate.Route.Id == RouteId.NativeDlss5)
        {
            _dialogs.Info("Natives DLSS 5", "Dieses Spiel hat DLSS 5 eingebaut – es muss nichts installiert werden.\n\n"
                                            + string.Join("\n", _s.Planner.Plan(game.Analysis, candidate).Hints.Prepend("• Im Spiel: DLSS 5 einschalten").Select(h => h.StartsWith('•') ? h : "• " + h)));
            return;
        }

        var manual = candidate.Missing.Where(m => !m.CanAutoDownload).ToList();
        if (manual.Count > 0)
        {
            _dialogs.Info("Komponente fehlt",
                $"Für diese Route fehlt: {string.Join(", ", manual.Select(m => m.Label))}.\n\nBitte im Reiter „Komponenten“ importieren.");
            return;
        }

        if (!await EnsureComponentsAsync(candidate.Missing.Select(m => m.Id).ToList()))
            return;

        if (candidate.Route.Id == RouteId.LegacyD3D8Dxvk && !CrashLog.HasD3dx9Runtime(game.Analysis.Bitness != Bitness.X64)
            && !_dialogs.Confirm("DirectX-Laufzeit fehlt",
                "d3d8to9 braucht D3DX9 aus der alten DirectX-Laufzeit (d3dx9_43.dll), die hier fehlt. Ohne sie startet das Spiel nicht.\n\n"
                + "Bitte die „DirectX End-User Runtime“ von Microsoft installieren (microsoft.com/download, ID 35).\n\nTrotzdem jetzt installieren?", warning: true))
            return;

        InstallPlan plan;
        try
        {
            plan = _s.Planner.Plan(game.Analysis, candidate);
        }
        catch (PlanException e)
        {
            _dialogs.Info("Installation nicht möglich", e.Message);
            return;
        }

        // Zwei ReShade-Layer blockieren sich: Nur einer lädt – ist es der fremde, fehlt das Add-on.
        foreach (var layer in plan.Steps.OfType<RegisterVulkanLayerStep>())
        {
            var others = VulkanLayer.OtherReShadeLayers(layer.Is32Bit);
            if (others.Count > 0 && _dialogs.Confirm("Weiterer ReShade-Vulkan-Layer",
                    "Es ist bereits ein anderer ReShade-Vulkan-Layer registriert:\n" + string.Join("\n", others)
                    + "\n\nEs lädt immer nur eine ReShade-Instanz. Ist es diese, startet das DLSS-5-Add-on nicht.\n\nDen anderen Layer abmelden? (Seine Dateien bleiben liegen.)", warning: true))
            {
                VulkanLayer.UnregisterEverywhere(others);
                Log("Fremde ReShade-Vulkan-Layer abgemeldet: " + string.Join(", ", others));
            }
        }

        var lines = plan.Steps.Where(st => st is not ManualStep).Select(st => "• " + st.Description)
            .Append("")
            .Append("Danach im Spiel:")
            .Concat(plan.ManualSteps.Select(m => "• " + m.Instruction))
            .Concat(plan.Hints.Select(h => "• " + h));
        if (!_dialogs.ConfirmList($"DLSS 5 installieren – {game.Name}",
                $"{candidate.Route.Name} · {candidate.Config.Api.DisplayName()}\nAlle geänderten Dateien werden gesichert und lassen sich mit „Rückgängig“ wiederherstellen.",
                lines))
            return;

        await RunBusy("Installiere …", () => Task.Run(() => _s.Installer.Install(plan)));
        Log($"{game.Name}: {candidate.Route.Name} installiert ({plan.Steps.Count} Schritte).");
        if (plan.Steps.OfType<RegisterVulkanLayerStep>().Any() && VulkanLayer.LastDeploy is { } deploy)
            Log($"{game.Name}: {deploy.Describe()}.");
        if (game.Recommendation?.ApiSwitch is { } sw && sw.To == candidate.Config.Api)
            _dialogs.Info("API umstellen", $"Wichtig: {sw.How}");
        Recompute(game);
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        var game = SelectedGame;
        if (game?.Analysis.GameDir is not { } dir || game.Installed is null)
            return;
        if (!_dialogs.Confirm("Rückgängig", $"Alle Änderungen dieses Tools an „{game.Name}“ entfernen und die Originaldateien wiederherstellen?"))
            return;
        await RunBusy("Stelle Originalzustand her …", () => Task.Run(() => _s.Installer.Uninstall(dir)));
        Log($"{game.Name}: Originalzustand wiederhergestellt.");
        Recompute(game);
    }

    [RelayCommand]
    private async Task RepairAsync()
    {
        var game = SelectedGame;
        if (game?.Installed is not { } manifest)
            return;
        var route = RouteCatalog.Get(manifest.Config.Route);
        var candidate = new Candidate(route, manifest.Config, new Prediction(0, 0, 0, 0, true), [], [], []);
        try
        {
            var plan = _s.Planner.Plan(game.Analysis, candidate);
            await RunBusy("Repariere …", () => Task.Run(() => _s.Installer.Install(plan)));
            Log($"{game.Name}: Installation erneuert (z. B. nach Spiel-Update).");
        }
        catch (PlanException e)
        {
            _dialogs.Info("Reparatur nicht möglich", e.Message);
        }
        Recompute(game);
    }

    /// <summary>Lädt fehlende Komponenten nach Rückfrage; Closed-Source-Teile mit eigener Warnung.</summary>
    private async Task<bool> EnsureComponentsAsync(IReadOnlyList<string> ids)
    {
        var defs = ids.Distinct().Select(_s.Catalog.Get).OfType<ComponentDefinition>().Where(d => !_s.Availability.IsAvailable(d.Id)).ToList();
        if (defs.Count == 0)
            return true;

        var lines = defs.Select(d => $"• {d.Name} – {d.License}\n   Quelle: {(d.Source.Repo is { } r ? "github.com/" + r : d.Source.Url)}");
        bool anyClosed = defs.Any(d => d.ClosedSource);
        var header = "Diese Komponenten werden direkt aus der Originalquelle geladen:"
                     + (anyClosed ? "\n\n⚠ Enthält Closed-Source-Add-ons aus der Community: Inhalt und Herkunft sind nicht überprüfbar. Nur laden, wenn du der Quelle vertraust." : "");
        if (!_dialogs.ConfirmList("Komponenten laden", header, lines))
            return false;

        foreach (var d in defs)
        {
            try
            {
                await RunBusy($"Lade {d.Name} …", async () =>
                {
                    var record = await _s.Downloader.DownloadAsync(d.Id, new Progress<double>(p => Progress = p * 100));
                    Log($"{d.Name} {record.Version} geladen ({record.Files.Count} Dateien).");
                });
            }
            catch (Exception e)
            {
                _dialogs.Info("Download fehlgeschlagen", $"{d.Name}: {e.Message}\n\nAlternativ die Datei manuell herunterladen und im Reiter „Komponenten“ importieren.");
                RefreshComponents();
                return false;
            }
        }
        RefreshComponents();
        return true;
    }

    // ---------------------------------------------------------------- Messen

    [RelayCommand]
    private void LaunchGame()
    {
        var g = SelectedGame;
        if (g is null)
            return;
        // Spiele mit mehreren APIs nach der Installation immer mit der installierten API starten.
        var installedApi = g.Installed?.Config.Api;
        var args = installedApi is { } api && g.Analysis.Api.Supported.Each().Count() > 1 ? DecisionEngine.LaunchArgument(g.Analysis.Engine, api) : null;
        // Script Extender (nvse_loader.exe, fose_loader.exe …) starten, wenn vorhanden – sonst fehlen Mods.
        var loader = g.Analysis.GameDir is { } gameDir
            ? g.Analysis.DbEntry?.LaunchExes.Select(l => Path.Combine(gameDir, l)).FirstOrDefault(File.Exists)
            : null;
        try
        {
            GameLauncher.Launch(g.Analysis.Game, g.Analysis.MainExe, args, loader);
            Log($"{g.Name} gestartet{(args is null ? "" : " mit " + args)}. In eine typische Spielszene gehen, dann „Messen“.");
        }
        catch (Exception e)
        {
            _dialogs.Info("Start fehlgeschlagen", e.Message);
        }
    }

    /// <summary>
    /// Misst das laufende Spiel: geladene DLLs (→ tatsächliche API) und Bildrate (PresentMon).
    /// Ohne installiertes DLSS 5 ist das die Basis, mit DLSS 5 werden daraus die echten Kosten berechnet.
    /// </summary>
    [RelayCommand]
    private async Task MeasureAsync()
    {
        var g = SelectedGame;
        if (g?.Analysis.MainExe is not { } exe)
            return;
        var procName = Path.GetFileName(exe);

        if (_s.PresentMonExe() is null && !await EnsureComponentsAsync([RouteCatalog.Ids.PresentMon]))
            return;
        var presentMon = _s.PresentMonExe();

        await RunBusy($"Warte auf {procName} …", async () =>
        {
            using var proc = await ProcessProbe.WaitForProcessAsync(exe, TimeSpan.FromSeconds(10), CancellationToken.None);
            if (proc is null)
            {
                _dialogs.Info("Spiel läuft nicht", $"{procName} wurde nicht gefunden. Erst das Spiel starten und in eine Spielszene gehen.");
                return;
            }

            // 1) Welche API läuft wirklich?
            var modules = ProcessProbe.LoadedModules(proc.Id);
            FrameStats? stats = null;
            if (presentMon is not null)
            {
                BusyText = $"Messe {MeasureSeconds} s – bitte normal weiterspielen …";
                var csv = Path.Combine(_s.DataDir, "last-capture.csv");
                stats = await new PresentMonRunner(presentMon).CaptureAsync(procName, MeasureSeconds, csv, CancellationToken.None);
            }
            var probe = ProbeInterpreter.Interpret(modules, stats?.DominantRuntime);
            if (probe.Api != GraphicsApi.None)
            {
                _s.Settings.ProbedApis[g.Analysis.Game.Key] = probe.Api;
                g.Analysis = GameAnalyzer.ApplyProbe(g.Analysis, probe.Api, probe.Evidence);
            }
            foreach (var e in probe.Evidence)
                Log($"{g.Name}: {e}");

            // 2) Kalibrieren.
            if (stats is not null)
            {
                var key = g.Analysis.Game.Key;
                var api = probe.Api != GraphicsApi.None ? probe.Api : g.Analysis.Api.Primary;
                var cals = _s.Settings.Calibrations.TryGetValue(key, out var existing) ? existing : _s.Settings.Calibrations[key] = [];
                double fps = stats.RenderedFps ?? stats.AverageFps;
                Log($"{g.Name}: {stats.AverageFps:0.0} fps im Schnitt, 1 % Low {stats.OnePercentLowFps:0.0} fps"
                    + (stats.RenderedFps is { } r ? $", gerendert {r:0.0} fps" : "")
                    + (stats.AveragePcLatencyMs is { } l ? $", PC-Latenz {l:0.0} ms" : ""));

                if (g.Installed is { } m && m.Config.Route != RouteId.NativeDlss5 && cals.TryGetValue(api, out var baseCal))
                {
                    if (m.Config.FrameGen != FrameGenMode.Off && stats.RenderedFps is null)
                    {
                        Log($"{g.Name}: Frame Generation ist aktiv, echte Bilder lassen sich nicht trennen – für die Kalibrierung FG kurz ausschalten und erneut messen.");
                    }
                    else
                    {
                        var route = RouteCatalog.Get(m.Config.Route);
                        var model = new FrameTimeModel(_s.System, baseCal);
                        double k = model.DeriveMsPerMegapixel(route, m.Config, g.Analysis.Bitness, fps);
                        cals[api] = baseCal with { NrMsPerMegapixel = k };
                        Log($"{g.Name}: DLSS 5 kostet hier gemessen {k:0.00} ms pro Megapixel – Vorhersagen sind jetzt kalibriert.");
                    }
                }
                else if (g.Installed is null)
                {
                    cals[api] = new Calibration(fps, MeasuredSrMode, api, cals.GetValueOrDefault(api)?.NrMsPerMegapixel);
                    Log($"{g.Name}: Basis gespeichert ({fps:0} fps bei {MeasuredSrMode.DisplayName()}, {api.DisplayName()}).");
                }
                else
                {
                    Log($"{g.Name}: Für die Kalibrierung zuerst ohne DLSS 5 messen (Rückgängig → Messen → Installieren → Messen).");
                }
            }
            _s.SaveSettings();
        });
        Recompute(g);
    }

    /// <summary>
    /// Prüft nach einem Spielstart, ob die ganze Kette greift (Logs + Windows-Absturzprotokoll),
    /// merkt sich das Ergebnis und bietet bei einem abgestürzten Übersetzer den anderen an.
    /// </summary>
    [RelayCommand]
    private async Task DiagnoseAsync()
    {
        var g = SelectedGame;
        if (g?.Analysis.GameDir is not { } dir || g.Analysis.MainExe is not { } exe)
            return;
        if (g.Installed is not { } manifest)
        {
            _dialogs.Info("Diagnose", "Für dieses Spiel ist nichts installiert.");
            return;
        }

        var (crashes, crashLines) = CrashLog.Recent(Path.GetFileName(exe), manifest.InstalledAt.LocalDateTime);
        var report = InstallDiagnostics.Evaluate(dir, manifest, Path.GetFileName(exe), crashes, g.Analysis.DbEntry);
        var lines = report.Checks.Select(c => $"{Symbol(c.Status)} {c.Title}\n   {c.Detail}").ToList();
        if (crashLines.Count > 0)
        {
            lines.Add("");
            lines.Add("✗ Windows-Ereignisprotokoll seit der Installation:");
            lines.AddRange(crashLines.Select(c => "   " + c));
        }
        foreach (var problem in g.IntegrityProblems)
            lines.Add($"⚠ {problem} – „Reparieren“ installiert neu.");
        if (manifest.Config.Route.UsesDxvk() || manifest.Config.Api == GraphicsApi.Vulkan)
        {
            var others = VulkanLayer.OtherReShadeLayers(g.Analysis.Bitness == Bitness.X86);
            if (others.Count > 0)
                lines.Add($"⚠ Weiterer ReShade-Vulkan-Layer registriert ({string.Join(", ", others)}) – er kann das DLSS-5-Add-on verdrängen. „DLSS 5 installieren“ bietet an, ihn abzumelden.");
        }
        foreach (var c in report.Checks)
            Log($"{g.Name}: Diagnose {c.Title}: {c.Status}");

        var route = manifest.Config.Route;
        if (report.Verdict == DiagnosticVerdict.Working)
            RememberOutcome(g, route, worked: true, "Diagnose: alles grün");
        else if (report.Verdict == DiagnosticVerdict.TranslatorFailed)
            RememberOutcome(g, route, worked: false, "Übersetzer abgestürzt");
        else if (report.Verdict == DiagnosticVerdict.WrapperBypassed)
            RememberOutcome(g, route, worked: false, "Spiel lädt die System-d3d9.dll");

        if (report.SwitchTo is { } alternative)
        {
            lines.Add("");
            lines.Add($"→ Vorschlag: auf „{RouteCatalog.Get(alternative).Name}“ umstellen.");
            _dialogs.ShowList($"Diagnose – {g.Name}", report.Summary, lines);
            if (_dialogs.Confirm("Übersetzer wechseln",
                    $"{report.Summary}\n\nJetzt auf „{RouteCatalog.Get(alternative).Name}“ umstellen? Die bisherige Installation wird dabei vollständig zurückgenommen."))
                await SwitchRouteAsync(g, alternative);
            else
                Recompute(g);
            return;
        }
        _dialogs.ShowList($"Diagnose – {g.Name}", report.Summary, lines);
        Recompute(g);

        static string Symbol(DiagnosticStatus s) => s switch
        {
            DiagnosticStatus.Ok => "✓",
            DiagnosticStatus.Warning => "⚠",
            DiagnosticStatus.Failed => "✗",
            _ => "…",
        };
    }

    /// <summary>
    /// Packt Logs, Einstellungen, Dateiliste und Systemdaten in eine ZIP-Datei auf dem Desktop – zum
    /// Weitergeben, wenn etwas nicht läuft. Benutzernamen in Pfaden werden ersetzt, Spielstände nicht eingepackt.
    /// </summary>
    [RelayCommand]
    private async Task DiagnosticPackageAsync()
    {
        var g = SelectedGame;
        if (g?.Analysis.GameDir is not { } dir)
        {
            _dialogs.Info("Diagnose-Paket", "Für dieses Spiel wurde keine EXE gefunden.");
            return;
        }
        var safeName = string.Concat(g.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '-' : ch));
        var zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"DLSS5-Diagnose-{safeName}-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        // Von uns geänderte Einstellungsdateien außerhalb des Spielordners (z. B. FalloutPrefs.ini).
        var external = (g.Installed?.Files ?? [])
            .Where(f => f.RelativePath.StartsWith("%DOCUMENTS%", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.Combine(documents, f.RelativePath["%DOCUMENTS%".Length..].TrimStart('\\', '/')))
            .ToList();
        var options = new DiagnosticBundle.Options(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, external, _s.LogPath);
        try
        {
            var summary = DiagnosticSummary.Build(_s, g);
            await RunBusy("Packe Diagnose …", () => Task.Run(() => DiagnosticBundle.Create(zip, dir, summary, options)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _dialogs.Info("Diagnose-Paket", "Konnte nicht gespeichert werden: " + e.Message);
            return;
        }
        Log($"{g.Name}: Diagnose-Paket gespeichert: {zip}");
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
        _dialogs.Info("Diagnose-Paket", $"Gespeichert auf dem Desktop:\n{Path.GetFileName(zip)}\n\n"
            + "Enthält die Logs und Einstellungen der Mods, eine Dateiliste, System- und Absturzdaten – keine Spielstände. "
            + "Dein Benutzername ist in allen Pfaden ersetzt.");
    }

    private void RememberOutcome(GameItemViewModel g, RouteId route, bool worked, string reason)
    {
        var key = g.Analysis.Game.Key;
        if (!_s.Settings.RouteOutcomes.TryGetValue(key, out var map))
            _s.Settings.RouteOutcomes[key] = map = [];
        map[route] = new RouteOutcome(worked, reason, DateTimeOffset.Now);
        _s.SaveSettings();
        Log($"{g.Name}: „{RouteCatalog.Get(route).Name}“ gemerkt als {(worked ? "läuft" : "gescheitert")} ({reason}).");
    }

    /// <summary>Nimmt die Installation zurück und installiert die beste Konfiguration der anderen Route.</summary>
    private async Task SwitchRouteAsync(GameItemViewModel g, RouteId target)
    {
        Recompute(g); // die gescheiterte Route ist jetzt ausgeschlossen
        var candidate = g.Candidates.Select(c => c.Candidate).FirstOrDefault(c => c.Route.Id == target);
        if (candidate is null)
        {
            _dialogs.Info("Wechsel nicht möglich", $"Für „{RouteCatalog.Get(target).Name}“ gibt es in diesem Spiel keine passende Konfiguration.");
            return;
        }
        await InstallCandidateAsync(g, candidate);
        if (g.Installed?.Config.Route == target)
            _dialogs.Info("Umgestellt", "Spiel erneut starten, eine Minute spielen und dann wieder „Diagnose“ klicken.");
    }

    [RelayCommand]
    private void ForgetOutcomes()
    {
        var g = SelectedGame;
        if (g is null || !_s.Settings.RouteOutcomes.Remove(g.Analysis.Game.Key))
            return;
        _s.SaveSettings();
        Log($"{g.Name}: Testergebnisse vergessen – alle Routen werden wieder berücksichtigt.");
        Recompute(g);
    }

    /// <summary>
    /// Stellt die nächste Tiefenpuffer-Variante ein (ReShade.ini im Spielordner) und merkt sie sich,
    /// damit „Reparieren“ sie beibehält. „Rückgängig“ stellt die ursprüngliche ReShade.ini wieder her.
    /// </summary>
    [RelayCommand]
    private void DepthAssistant()
    {
        var g = SelectedGame;
        if (g?.Analysis.GameDir is not { } dir || g.Installed is not { } manifest || !manifest.Config.Route.UsesFeeder())
        {
            _dialogs.Info("Tiefenpuffer-Assistent", "Nur für installierte Feeder-Routen (Spiele ohne eigenes DLSS).");
            return;
        }
        var iniPath = Path.Combine(dir, "ReShade.ini");
        var ini = IniFile.Load(iniPath);
        var next = Core.Install.DepthAssistant.Next(Core.Install.DepthAssistant.Read(ini));
        Core.Install.DepthAssistant.Apply(ini, next);
        ini.Save(iniPath);
        _s.Settings.DepthVariants[g.Analysis.Game.Key] = next;
        _s.SaveSettings();

        var (index, count) = Core.Install.DepthAssistant.Position(next);
        Log($"{g.Name}: Tiefenpuffer-Variante {index}/{count}: {next.Label}.");
        _dialogs.Info("Tiefenpuffer-Assistent",
            $"Variante {index} von {count}: {next.Label}.\n\nSpiel neu starten, eine Minute spielen (mit Bewegung) und dann „Diagnose“. "
            + "Meldet sie beim Tiefenpuffer weiter ✗, noch einmal klicken – nach der letzten Variante geht es von vorn los.");
    }

    // ---------------------------------------------------------------- Komponenten

    [RelayCommand]
    private async Task DownloadComponentAsync(ComponentItemViewModel? item)
    {
        if (item is null)
            return;
        if (_s.Store.IsAvailable(item.Definition.Id))
            _s.Store.Remove(item.Definition.Id); // erneut laden = aktualisieren
        await EnsureComponentsAsync([item.Definition.Id]);
        RecomputeAll();
    }

    [RelayCommand]
    private void ImportComponent(ComponentItemViewModel? item)
    {
        if (item is null)
            return;
        var dlg = new OpenFileDialog
        {
            Title = $"{item.Name} importieren – Dateien oder Archiv wählen",
            Multiselect = true,
            Filter = "Alle unterstützten|*.zip;*.7z;*.rar;*.dll;*.addon64;*.addon32;*.cfg;*.exe;*.fx;*.png;*.json|Alle Dateien|*.*",
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            var record = _s.Store.Import(item.Definition.Id, dlg.FileNames, "importiert", "Import: " + string.Join(", ", dlg.FileNames.Select(Path.GetFileName)));
            Log($"{item.Name} importiert ({record.Files.Count} Dateien, SHA-256 gespeichert).");
        }
        catch (Exception e)
        {
            _dialogs.Info("Import fehlgeschlagen", e.Message);
        }
        RefreshComponents();
        RecomputeAll();
    }

    [RelayCommand]
    private void RemoveComponent(ComponentItemViewModel? item)
    {
        if (item is null || !_s.Store.IsAvailable(item.Definition.Id))
            return;
        if (!_dialogs.Confirm("Komponente entfernen", $"{item.Name} aus dem lokalen Speicher löschen? Installierte Spiele bleiben unverändert."))
            return;
        _s.Store.Remove(item.Definition.Id);
        RefreshComponents();
        RecomputeAll();
    }

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void RefreshComponents()
    {
        foreach (var c in Components)
            c.Refresh(_s.Store, _s.Availability);
    }

    // ---------------------------------------------------------------- System

    [RelayCommand]
    private void RefreshSystemInfo()
    {
        _s.RefreshSystem();
        var sys = _s.System;
        var gpu = sys.Gpu;
        GpuText = string.IsNullOrEmpty(gpu.Name) ? "Keine Grafikkarte erkannt" : $"{gpu.Name} · {gpu.VramBytes / (1024.0 * 1024 * 1024):0} GB";
        DriverText = gpu.DriverVersion is { } d
            ? $"{d}" + (d < SystemInfo.MinDlss5Driver ? $" – zu alt, mindestens {SystemInfo.MinDlss5Driver}" : " ✓")
            : "unbekannt";
        DisplayText = $"{sys.Display.Width}×{sys.Display.Height} @ {sys.Display.RefreshHz} Hz";
        ModelText = _s.DlssNrModel() ?? "nicht gefunden – Treiber ≥ 616.56 installieren oder Datei importieren";
        HagsText = sys.HardwareSchedulingEnabled switch
        {
            true => "an ✓",
            false => "aus – für Frame Generation in Windows einschalten (Einstellungen → System → Anzeige → Grafik)",
            null => "unbekannt",
        };
        WindowedText = WindowsSystem.ReadWindowedOptimizations() switch
        {
            true => "an ✓",
            _ => "aus – verringert Latenz in DX10/11-Spielen im Fenstermodus",
        };
        GameModeText = WindowsSystem.ReadGameMode() switch { false => "aus", _ => "an ✓" };
        var layers = VulkanLayer.RegisteredReShadeLayers();
        VulkanLayerText = layers.Count == 0 ? "nicht registriert" : string.Join("\n", layers);
        SystemVerdict = gpu.Family switch
        {
            GpuFamily.Rtx50 when gpu.DriverVersion is { } dv && dv < SystemInfo.MinDlss5Driver => "Treiber aktualisieren, dann ist DLSS 5 möglich.",
            GpuFamily.Rtx50 => "DLSS 5 wird unterstützt.",
            GpuFamily.Rtx40 => "RTX 40: DLSS 5 ist noch nicht freigegeben (angekündigt). Smooth Motion und DLSS 4.5 funktionieren.",
            _ => "Diese Grafikkarte unterstützt DLSS 5 nicht.",
        };
    }

    [RelayCommand]
    private void EnableWindowedOptimizations()
    {
        WindowsSystem.EnableWindowedOptimizations();
        Log("Optimierungen für Spiele im Fenstermodus eingeschaltet.");
        RefreshSystemInfo();
    }

    [RelayCommand]
    private void RemoveVulkanLayer()
    {
        if (!_dialogs.Confirm("Vulkan-Layer entfernen", "ReShade als Vulkan-Layer abmelden? Vulkan-Spiele mit DLSS-5-Mod laufen danach ohne DLSS 5."))
            return;
        VulkanLayer.UnregisterReShade();
        RefreshSystemInfo();
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_s.DataDir}\"") { UseShellExecute = true });

    // ---------------------------------------------------------------- Hilfen

    private async Task RunBusy(string text, Func<Task> action)
    {
        IsBusy = true;
        BusyText = text;
        Progress = 0;
        try
        {
            await action();
        }
        catch (Exception e)
        {
            Log("Fehler: " + e.Message);
            _s.Log(e.ToString());
            _dialogs.Info("Fehler", e.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private void Log(string message)
    {
        _s.Log(message);
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(() => LogLines.Insert(0, line));
        else
            LogLines.Insert(0, line);
    }
}
