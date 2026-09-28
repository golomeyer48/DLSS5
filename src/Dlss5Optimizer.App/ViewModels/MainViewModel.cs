using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dlss5Optimizer.App.Platform;
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
        var cal = _s.Settings.Calibrations.GetValueOrDefault(g.Analysis.Game.Key);
        var rec = _s.Engine.Recommend(g.Analysis, _s.System, CurrentPreferences(), cal);
        g.SetRecommendation(rec);
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

    /// <summary>Prüft nach einem Spielstart, ob die ganze Kette greift (Logs + Windows-Absturzprotokoll).</summary>
    [RelayCommand]
    private void Diagnose()
    {
        var g = SelectedGame;
        if (g?.Analysis.GameDir is not { } dir || g.Analysis.MainExe is not { } exe)
            return;
        if (g.Installed is not { } manifest)
        {
            _dialogs.Info("Diagnose", "Für dieses Spiel ist nichts installiert.");
            return;
        }

        var checks = InstallDiagnostics.Check(dir, manifest, Path.GetFileName(exe));
        var lines = checks.Select(c => $"{Symbol(c.Status)} {c.Title}\n   {c.Detail}").ToList();
        var crashes = CrashLog.RecentCrashes(Path.GetFileName(exe), manifest.InstalledAt.LocalDateTime);
        if (crashes.Count > 0)
        {
            lines.Add("");
            lines.Add("✗ Windows-Ereignisprotokoll seit der Installation:");
            lines.AddRange(crashes.Select(c => "   " + c));
        }
        foreach (var problem in g.IntegrityProblems)
            lines.Add($"⚠ {problem} – „Reparieren“ installiert neu.");

        bool allOk = checks.All(c => c.Status == DiagnosticStatus.Ok) && crashes.Count == 0 && g.IntegrityProblems.Count == 0;
        var header = allOk
            ? "Alles greift: DLSS 5 läuft in diesem Spiel."
            : checks.Any(c => c.Status == DiagnosticStatus.NotRunYet)
                ? "Noch nicht vollständig geprüft – Spiel starten, ein paar Sekunden spielen, dann erneut „Diagnose“."
                : "Es gibt Probleme. Die Hinweise unten sagen, woran es liegt.";
        _dialogs.ShowList($"Diagnose – {g.Name}", header, lines);
        foreach (var c in checks)
            Log($"{g.Name}: Diagnose {c.Title}: {c.Status}");

        static string Symbol(DiagnosticStatus s) => s switch
        {
            DiagnosticStatus.Ok => "✓",
            DiagnosticStatus.Warning => "⚠",
            DiagnosticStatus.Failed => "✗",
            _ => "…",
        };
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
