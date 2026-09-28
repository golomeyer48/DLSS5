using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Decision;

/// <summary>
/// Erzeugt alle sinnvollen Kombinationen aus API × Route × Upscaling × DLSS-5-Auflösung × Frame Generation,
/// sagt ihre Bildrate mit dem <see cref="FrameTimeModel"/> voraus und wählt nach Profil die beste.
/// </summary>
public sealed class DecisionEngine
{
    private static readonly double[] ModelScales = [1.0, 0.75, 0.5];

    private readonly Func<string, bool> _isAvailable;
    private readonly Func<string, bool> _canAutoDownload;

    /// <param name="isAvailable">Komponente liegt bereits lokal vor.</param>
    /// <param name="canAutoDownload">Komponente kann das Tool selbst beschaffen (Open Source, Treiber).</param>
    public DecisionEngine(Func<string, bool> isAvailable, Func<string, bool> canAutoDownload)
    {
        _isAvailable = isAvailable;
        _canAutoDownload = canAutoDownload;
    }

    public Recommendation Recommend(GameAnalysis game, SystemInfo system, UserPreferences prefs,
        IReadOnlyDictionary<GraphicsApi, Calibration>? calibrations = null,
        IReadOnlyDictionary<RouteId, RouteOutcome>? outcomes = null)
    {
        var notes = new List<string>();
        int target = prefs.EffectiveTargetFps(system.Display);
        int? frameCap = game.DbEntry?.FrameCapFps;
        if (frameCap is { } cap && cap < target)
        {
            // Mehr als die Engine erlaubt geht nicht – sonst würde die Auswahl Qualität für unerreichbare fps opfern.
            target = cap;
            notes.Add($"Die Engine begrenzt auf {cap} fps – das Ziel ist deshalb {cap} fps bei bestmöglicher Qualität.");
        }
        var currentApi = game.Api.Primary;
        Calibration? Cal(GraphicsApi api) => calibrations?.GetValueOrDefault(api);

        var assumedMode = game.Upscalers.Has(UpscalerFeature.DlssSuperResolution) ? SrMode.Quality : SrMode.Native;
        var baselineModel = new FrameTimeModel(system, Cal(currentApi), assumedMode);
        var baseline = baselineModel.PredictBaseline(Cal(currentApi)?.BaselineSrMode ?? assumedMode, currentApi);
        if (game.DbEntry?.FrameCapFps is { } baseCap && baseline.DisplayedFps > baseCap)
            baseline = baseline with { RenderedFps = baseCap, DisplayedFps = baseCap };

        if (game.MainExe is null)
            return Blocked("Keine Spiel-EXE gefunden – Ordner manuell prüfen.");

        var blockReason = CheckSystem(system, game, prefs, notes);
        if (game.HasNativeDlss5)
        {
            // Offizieller Weg geht immer, auch mit Anti-Cheat.
            blockReason = CheckGpuOnly(system);
        }
        if (blockReason is not null)
            return Blocked(blockReason);

        var candidates = new List<Candidate>();
        var apis = game.Api.Supported == GraphicsApi.None ? currentApi : game.Api.Supported;
        foreach (var api in apis.Each())
        {
            var model = new FrameTimeModel(system, Cal(api) ?? RebaseCalibration(Cal(currentApi), api), assumedMode);
            foreach (var route in RouteCatalog.All)
            {
                if (!IsEligible(route, api, game, prefs))
                    continue;
                if (outcomes?.GetValueOrDefault(route.Id) is { Worked: false })
                    continue; // bei diesem Nutzer schon gescheitert
                var expanded = Expand(route, api, game, system, prefs, model);
                if (outcomes?.GetValueOrDefault(route.Id) is { Worked: true })
                    expanded = expanded.Select(c => c with
                    {
                        Proven = true,
                        Reasons = c.Reasons.Append("Bei dir getestet: läuft").ToList(),
                    });
                candidates.AddRange(expanded);
            }
        }

        foreach (var (id, outcome) in outcomes ?? new Dictionary<RouteId, RouteOutcome>())
        {
            if (!outcome.Worked)
                notes.Add($"„{RouteCatalog.Get(id).Name}“ ist bei dir gescheitert ({outcome.Reason}) und wird übersprungen.");
        }

        if (candidates.Count == 0)
        {
            notes.Add("Für diese Kombination aus API und Spiel gibt es keine DLSS-5-Route.");
            return new Recommendation(null, null, [], null, null, notes, baseline, target);
        }

        var ranked = Rank(candidates, target).ToList();
        var best = ranked.FirstOrDefault(c => c.Installable);
        var bestAny = ranked[0];
        var bestWithImports = best is null || !ReferenceEquals(bestAny, best) && Better(bestAny, best, target) ? bestAny : null;
        if (bestWithImports is not null && !bestWithImports.Installable)
        {
            var missing = string.Join(", ", bestWithImports.Missing.Where(m => !m.CanAutoDownload).Select(m => m.Label));
            notes.Add($"Mit {missing} wäre „{bestWithImports.Route.Name}“ möglich (bessere Bewertung). Die Datei(en) unter „Komponenten“ importieren.");
        }

        var chosen = best ?? bestAny;
        ApiSwitchAdvice? apiSwitch = null;
        if (chosen.Config.Api != currentApi && currentApi != GraphicsApi.None)
            apiSwitch = new ApiSwitchAdvice(currentApi, chosen.Config.Api, ApiSwitchHint(game, chosen.Config.Api));

        if (chosen.Prediction.IsEstimate)
            notes.Add("Vorhersage basiert auf Schätzwerten. Ein Benchmark macht sie genau.");
        if (game.DbEntry?.Dlss5Announced == true)
            notes.Add("Natives DLSS 5 ist für dieses Spiel angekündigt – nach dem Patch den Mod entfernen.");

        var alternatives = ranked
            .Where(c => !ReferenceEquals(c, chosen))
            .GroupBy(c => (c.Route.Id, c.Config.Api))
            .Select(g => g.First())
            .Take(6)
            .ToList();

        return new Recommendation(best, bestWithImports, alternatives, apiSwitch, null, notes, baseline, target);

        Recommendation Blocked(string reason) => new(null, null, [], null, reason, notes, baseline, target);
    }

    /// <summary>
    /// Sortierung: Erst die, die das Ziel erreichen (nach Qualität), dann der Rest (nach Bildrate).
    /// Innerhalb jeder Gruppe geht eine bei diesem Nutzer erprobte Route vor, danach die von der
    /// Spiel-Datenbank empfohlene – echte Erfahrung schlägt ein, zwei geschätzte fps Unterschied.
    /// Bei Gleichstand gewinnt das Stabilere (weniger experimentell, ohne Frame Generation).
    /// </summary>
    public static IEnumerable<Candidate> Rank(IEnumerable<Candidate> candidates, int targetFps) =>
        candidates
            .OrderByDescending(c => c.Prediction.DisplayedFps >= targetFps)
            .ThenByDescending(c => c.Proven)
            .ThenByDescending(c => c.Recommended)
            .ThenByDescending(c => c.Prediction.DisplayedFps >= targetFps ? c.Prediction.Quality : c.Prediction.DisplayedFps)
            .ThenBy(c => c.Route.Experimental)
            .ThenBy(c => c.Config.FrameGen != FrameGenMode.Off)
            .ThenByDescending(c => c.Prediction.DisplayedFps);

    private static bool Better(Candidate a, Candidate b, int target)
    {
        bool aHit = a.Prediction.DisplayedFps >= target, bHit = b.Prediction.DisplayedFps >= target;
        if (aHit != bHit)
            return aHit;
        if (a.Proven != b.Proven)
            return a.Proven;
        if (a.Recommended != b.Recommended)
            return a.Recommended;
        return aHit
            ? a.Prediction.Quality > b.Prediction.Quality + 2
            : a.Prediction.DisplayedFps > b.Prediction.DisplayedFps * 1.05;
    }

    private static string? CheckGpuOnly(SystemInfo system) => system.Gpu.Family switch
    {
        GpuFamily.Rtx50 => null,
        GpuFamily.Rtx40 => "DLSS 5 läuft offiziell nur auf RTX 50. RTX-40-Unterstützung ist angekündigt, aber noch nicht erschienen.",
        GpuFamily.Rtx20 or GpuFamily.Rtx30 => "DLSS 5 läuft offiziell nur auf RTX 50.",
        GpuFamily.NonNvidia => "DLSS 5 braucht eine NVIDIA-RTX-Grafikkarte.",
        GpuFamily.NvidiaNoTensor => "Diese NVIDIA-Karte hat keine Tensor-Kerne – DLSS ist nicht möglich.",
        _ => null,
    };

    private static string? CheckSystem(SystemInfo system, GameAnalysis game, UserPreferences prefs, List<string> notes)
    {
        if (CheckGpuOnly(system) is { } gpu)
            return gpu;
        if (system.Gpu.DriverVersion is { } drv && drv < SystemInfo.MinDlss5Driver)
            return $"Treiber {drv} ist zu alt. DLSS 5 braucht mindestens {SystemInfo.MinDlss5Driver} (nvngx_dlssnr.dll ist erst ab dieser Version im Treiber).";
        if (system.Gpu.DriverVersion is { Major: 616, Minor: 64 or 86 })
            notes.Add($"Treiber {system.Gpu.DriverVersion}: Mit dem DLSS-5-Modell 310.8 sind Abstürze einzelner Add-ons bekannt. Bei Problemen Treiber 616.56 verwenden.");
        if (system.HardwareSchedulingEnabled == false)
            notes.Add("Hardwarebeschleunigte GPU-Planung (HAGS) ist aus – ohne sie gibt es keine DLSS Frame Generation.");
        if (game.AntiCheat.Detected)
        {
            if (!prefs.IgnoreAntiCheat)
                return $"Anti-Cheat erkannt ({string.Join(", ", game.AntiCheat.Systems)}). Injection-Mods können zu Kick oder Bann führen – Installation gesperrt.";
            notes.Add("Anti-Cheat wird auf eigenes Risiko ignoriert. Nur offline spielen!");
        }
        return null;
    }

    private static bool IsEligible(RouteDefinition route, GraphicsApi api, GameAnalysis game, UserPreferences prefs)
    {
        if (route.Id == RouteId.NativeDlss5)
            return game.HasNativeDlss5;
        if (game.HasNativeDlss5)
            return false; // nie einen Mod über natives DLSS 5 legen
        if (game.DbEntry?.Excludes(route.Id.ToString()) == true)
            return false; // in diesem Spiel nachweislich gescheitert
        if (!route.Apis.HasFlag(api))
            return false;
        if (route.Experimental && !prefs.AllowExperimental)
            return false;
        if (route.Requires64Bit && game.Bitness == Bitness.X86)
            return false;
        if (route.RequiresGameDlss)
        {
            bool dlss = game.Upscalers.Has(UpscalerFeature.DlssSuperResolution);
            bool alt = route.AcceptsFsrOrXess && game.Upscalers.HasUpscalerInputs;
            if (!dlss && !(alt && prefs.AllowExperimental))
                return false;
        }
        // Spiele mit echten Upscaler-Daten nie über geschätzte Vektoren führen, wenn es besser geht –
        // der Feeder bleibt trotzdem als Alternative in der Liste.
        return true;
    }

    private IEnumerable<Candidate> Expand(RouteDefinition route, GraphicsApi api, GameAnalysis game, SystemInfo system, UserPreferences prefs, FrameTimeModel model)
    {
        var missing = MissingComponents(route);
        bool gameHasDlss = game.Upscalers.Has(UpscalerFeature.DlssSuperResolution);
        bool usesAltInputs = route.RequiresGameDlss && !gameHasDlss;

        IEnumerable<SrMode> srModes = route.SupportsSuperResolution && (gameHasDlss || route.Id == RouteId.OptiScalerNr)
            ? [SrMode.Dlaa, SrMode.Quality, SrMode.Balanced, SrMode.Performance, SrMode.UltraPerformance]
            : [SrMode.Native];

        bool preAvailable = route.PreUpscaleComponent is { } pre && prefs.AllowExperimental && (_isAvailable(pre) || _canAutoDownload(pre));
        IEnumerable<NrPlacement> placements = preAvailable ? [NrPlacement.PostUpscale, NrPlacement.PreUpscale] : [NrPlacement.PostUpscale];

        bool scaleAvailable = route.ModelScaleComponent is { } sc && route.ModelScaleApis.HasFlag(api) && (_isAvailable(sc) || _canAutoDownload(sc));
        IEnumerable<double> scales = scaleAvailable ? ModelScales : [1.0];

        var frameGens = FrameGenOptions(route, api, game, system, prefs).ToList();

        foreach (var sr in srModes)
        foreach (var placement in placements)
        foreach (var scale in scales)
        {
            if (placement == NrPlacement.PreUpscale && sr is SrMode.Native or SrMode.Dlaa)
                continue; // ohne Hochskalierung gibt es kein "davor"
            if (placement == NrPlacement.PreUpscale && scale < 1.0)
                continue; // Pre-Upscale-Fork flackert mit reduzierter Modellauflösung (bis v0.8.91)
            foreach (var fg in frameGens)
            {
                var config = new Configuration(route.Id, api, sr, scale, placement, fg);
                var reasons = new List<string>();
                var warnings = new List<string>(route.Caveats);
                double quality = Quality(route, config, usesAltInputs, reasons, warnings);
                bool recommended = string.Equals(game.DbEntry?.PreferredRoute, route.Id.ToString(), StringComparison.OrdinalIgnoreCase);
                if (recommended)
                    reasons.Add("Für dieses Spiel empfohlen (Spiel-Datenbank)");
                var prediction = model.Predict(route, config, game.Bitness, quality);
                if (game.DbEntry?.FrameCapFps is { } fpsCap && prediction.RenderedFps > fpsCap)
                {
                    double shown = config.FrameGen == FrameGenMode.Off ? fpsCap : Math.Min(prediction.DisplayedFps, fpsCap * FrameTimeModel.FrameGenMultiplier(config.FrameGen));
                    prediction = prediction with { RenderedFps = fpsCap, DisplayedFps = shown };
                }

                if (fg != FrameGenMode.Off && prediction.RenderedFps < FrameTimeModel.MinBaseFpsForFrameGen)
                    continue; // Frame Generation auf zu niedriger Basis: spürbare Verzögerung

                var routeMissing = missing.ToList();
                if (placement == NrPlacement.PreUpscale && route.PreUpscaleComponent is { } preId && !_isAvailable(preId)
                    && routeMissing.All(m => m.Id != preId))
                    routeMissing.Add(new MissingComponent(preId, route.PreUpscaleLabel ?? preId, _canAutoDownload(preId)));

                yield return new Candidate(route, config, prediction, routeMissing, reasons, warnings) { Recommended = recommended };
            }
        }
    }

    private static IEnumerable<FrameGenMode> FrameGenOptions(RouteDefinition route, GraphicsApi api, GameAnalysis game, SystemInfo system, UserPreferences prefs)
    {
        yield return FrameGenMode.Off;
        if (!prefs.AllowFrameGeneration || system.HardwareSchedulingEnabled == false)
            yield break;
        if (game.Upscalers.Has(UpscalerFeature.DlssFrameGeneration))
        {
            yield return FrameGenMode.DlssFg2x;
            if (system.Gpu.Family == GpuFamily.Rtx50)
            {
                yield return FrameGenMode.DlssMfg3x;
                yield return FrameGenMode.DlssMfg4x;
                yield return FrameGenMode.DlssMfg6x;
            }
            yield break;
        }
        bool smoothMotionApi = api is GraphicsApi.D3D11 or GraphicsApi.D3D12 or GraphicsApi.Vulkan;
        if (smoothMotionApi && !route.SmoothMotionIncompatibleApis.HasFlag(api) && system.Gpu.Family is GpuFamily.Rtx40 or GpuFamily.Rtx50)
            yield return FrameGenMode.SmoothMotion;
    }

    /// <summary>Qualitätspunkte 0–100. Die Abzüge sind grobe, dokumentierte Erfahrungswerte.</summary>
    internal static double Quality(RouteDefinition route, Configuration c, bool usesAltInputs, List<string> reasons, List<string> warnings)
    {
        double q = route.MotionVectors == MotionVectorSource.Engine ? 100 : 72;
        reasons.Add(route.MotionVectors == MotionVectorSource.Engine
            ? "Echte Bewegungsvektoren aus dem Spiel"
            : "Geschätzte Bewegungsvektoren (weniger sauber)");

        if (usesAltInputs)
        {
            q -= 4;
            warnings.Add("Nutzt die FSR-/XeSS-Eingaben des Spiels – mit DLSS 5 noch wenig getestet.");
        }

        q -= c.SuperResolution switch
        {
            SrMode.Native or SrMode.Dlaa => 0,
            SrMode.Quality => 2,
            SrMode.Balanced => 4,
            SrMode.Performance => 6,
            SrMode.UltraPerformance => 12,
            _ => 0,
        };
        if (c.SuperResolution is not (SrMode.Native or SrMode.Dlaa))
            reasons.Add($"{c.SuperResolution.DisplayName()} mit Preset {c.SuperResolution.RecommendedPreset()}");

        if (c.Placement == NrPlacement.PreUpscale)
        {
            q -= 8;
            reasons.Add("DLSS 5 vor dem Hochskalieren: ca. halbe Kosten");
            warnings.Add("Pre-Upscale ist ein experimenteller Community-Fork.");
        }

        q -= c.NrScale switch
        {
            >= 1.0 => 0,
            >= 0.75 => 3,
            _ => 12,
        };
        if (c.NrScale < 1.0)
            reasons.Add($"DLSS-5-Modell auf {c.NrScale:P0} Auflösung");

        q -= c.FrameGen switch
        {
            FrameGenMode.DlssFg2x => 2,
            FrameGenMode.DlssMfg3x => 4,
            FrameGenMode.DlssMfg4x => 5,
            FrameGenMode.DlssMfg6x => 8,
            FrameGenMode.SmoothMotion => 5,
            _ => 0,
        };
        if (c.FrameGen != FrameGenMode.Off)
            reasons.Add($"{c.FrameGen.DisplayName()} + Reflex");

        return Math.Max(0, q);
    }

    private List<MissingComponent> MissingComponents(RouteDefinition route)
    {
        var result = new List<MissingComponent>();
        foreach (var req in route.Components)
        {
            if (req.AnyOf.Any(_isAvailable))
                continue;
            var auto = req.AnyOf.FirstOrDefault(_canAutoDownload);
            result.Add(new MissingComponent(auto ?? req.AnyOf[0], req.Label, auto is not null));
        }
        return result;
    }

    /// <summary>Ohne eigene Messung für eine andere API wird die Basis der aktuellen API übernommen.</summary>
    private static Calibration? RebaseCalibration(Calibration? current, GraphicsApi api) =>
        current is null ? null : current with { Api = api, NrMsPerMegapixel = null };

    public static string ApiSwitchHint(GameAnalysis game, GraphicsApi to)
    {
        if (game.DbEntry?.ApiExeVariants.FirstOrDefault(v => v.Api == to) is { } variant)
            return $"Spiel über „{variant.Exe}“ starten.";
        var arg = LaunchArgument(game.Engine, to);
        return arg is not null
            ? $"Startparameter „{arg}“ setzen (Steam: Eigenschaften → Startoptionen)."
            : $"Im Grafikmenü des Spiels auf {to.DisplayName()} umstellen.";
    }

    public static string? LaunchArgument(GameEngine engine, GraphicsApi api) => (engine, api) switch
    {
        (GameEngine.Unreal or GameEngine.Unreal4 or GameEngine.Unreal5, GraphicsApi.D3D12) => "-dx12",
        (GameEngine.Unreal or GameEngine.Unreal4 or GameEngine.Unreal5, GraphicsApi.D3D11) => "-dx11",
        (GameEngine.Unreal or GameEngine.Unreal4 or GameEngine.Unreal5, GraphicsApi.Vulkan) => "-vulkan",
        (GameEngine.Unity, GraphicsApi.D3D12) => "-force-d3d12",
        (GameEngine.Unity, GraphicsApi.D3D11) => "-force-d3d11",
        (GameEngine.Unity, GraphicsApi.Vulkan) => "-force-vulkan",
        (GameEngine.Unity, GraphicsApi.OpenGL) => "-force-glcore",
        (GameEngine.Source2, GraphicsApi.Vulkan) => "-vulkan",
        (GameEngine.Source2, GraphicsApi.D3D11) => "-dx11",
        _ => null,
    };
}
