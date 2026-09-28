using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Detection;

/// <summary>
/// Statische Analyse eines Spiels ohne es zu starten: EXE-Header und Imports, mitgelieferte DLLs,
/// Engine, vorhandene Mods und Anti-Cheat. Das Ergebnis kann später durch einen Testlauf
/// (<see cref="ApplyProbe"/>) präzisiert werden.
/// </summary>
public sealed class GameAnalyzer(GameDatabase db)
{
    /// <summary>Schwelle, ab der eine API als "vom Spiel unterstützt" gilt.</summary>
    public const double SupportThreshold = 0.45;

    private static readonly (string Dll, GraphicsApi Api, double Weight)[] ImportRules =
    [
        ("d3d12.dll", GraphicsApi.D3D12, 0.9),
        ("d3d11.dll", GraphicsApi.D3D11, 0.8),
        ("d3d10.dll", GraphicsApi.D3D10, 0.8),
        ("d3d10_1.dll", GraphicsApi.D3D10, 0.8),
        ("d3d9.dll", GraphicsApi.D3D9, 0.9),
        ("d3d8.dll", GraphicsApi.D3D8, 0.9),
        ("ddraw.dll", GraphicsApi.DirectDraw, 0.8),
        ("vulkan-1.dll", GraphicsApi.Vulkan, 0.9),
        ("opengl32.dll", GraphicsApi.OpenGL, 0.7),
    ];

    // Strings, die in EXEs mit dynamisch geladener Grafik-API auftauchen (v. a. Unreal).
    private static readonly (string Needle, GraphicsApi Api, double Weight)[] StringRules =
    [
        ("d3d12.dll", GraphicsApi.D3D12, 0.45),
        ("D3D12RHI", GraphicsApi.D3D12, 0.5),
        // Streamline-Spiele (Alan Wake 2) importieren nur sl.interposer.dll und holen D3D12CreateDevice per Name.
        ("D3D12CreateDevice", GraphicsApi.D3D12, 0.45),
        ("d3d11.dll", GraphicsApi.D3D11, 0.35),
        ("D3D11RHI", GraphicsApi.D3D11, 0.5),
        ("vulkan-1.dll", GraphicsApi.Vulkan, 0.45),
        ("VulkanRHI", GraphicsApi.Vulkan, 0.5),
    ];

    private const string Ue5Marker = "++UE5+Release";
    private const string Ue4Marker = "++UE4+Release";

    public GameAnalysis Analyze(GameInfo game)
    {
        var evidence = new List<string>();
        var warnings = new List<string>();

        var dbEntry = db.Find(game, null);
        string? knownExe = null;
        if (dbEntry is null && db.FindByAnyExe(ExecutableLocator.EnumerateExecutables(game.InstallDir, maxDepth: 4)) is { } byExe)
            (dbEntry, knownExe) = byExe;
        var exe = ExecutableLocator.FindMainExecutable(game.InstallDir, dbEntry?.MainExe ?? knownExe ?? game.PreferredExe);
        dbEntry ??= db.Find(game, exe);
        if (exe is null)
        {
            warnings.Add("Keine Spiel-EXE gefunden.");
            return new GameAnalysis(game, null, Bitness.Unknown, GameEngine.Unknown, ApiDetection.Unknown,
                UpscalerInfo.None, ExistingMod.None, AntiCheatInfo.None, dbEntry, evidence, warnings);
        }
        evidence.Add($"Haupt-EXE: {Path.GetRelativePath(game.InstallDir, exe)}");

        var files = FileIndex.Build(game.InstallDir);
        var exeDir = Path.GetDirectoryName(exe)!;
        var pe = PeFile.TryRead(exe);
        var bitness = pe?.Bitness ?? Bitness.Unknown;
        if (pe is null)
            warnings.Add("EXE konnte nicht gelesen werden (Zugriff verweigert oder geschützt).");
        else
            evidence.Add($"Architektur: {(bitness == Bitness.X64 ? "64-Bit" : bitness == Bitness.X86 ? "32-Bit" : bitness.ToString())}");

        // Der String-Scan liest die ganze EXE – nur wenn die Imports nichts Eindeutiges sagen oder
        // es ein Unreal-Spiel ist (dort stehen Engine-Version und RHI-Module nur als Strings drin).
        bool strongImport = pe is not null && pe.AllImports.Any(i => ImportRules.Any(r => r.Dll == i && r.Weight >= 0.8));
        bool unrealExe = exe.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) || files.HasDirectory("Engine");
        var needles = StringRules.Select(r => r.Needle).Append(Ue5Marker).Append(Ue4Marker).ToArray();
        var strings = strongImport && !unrealExe ? [] : BinaryStringScanner.FindAny(exe, needles);

        var engine = DetectEngine(files, exe, strings, evidence);
        var scores = new Dictionary<GraphicsApi, double>();
        void Add(GraphicsApi api, double w) => scores[api] = Math.Min(1.0, scores.GetValueOrDefault(api) + w);

        if (pe is not null)
            ScoreImports(pe.AllImports.ToList(), 1.0, "EXE importiert", Add, evidence);

        // Engine-Module, die die Grafik-API statt der EXE laden.
        foreach (var dll in EngineModules(exeDir, engine))
        {
            var modPe = PeFile.TryRead(dll);
            if (modPe is not null)
                ScoreImports(modPe.AllImports.ToList(), 0.85, $"{Path.GetFileName(dll)} importiert", Add, evidence);
        }

        // DLL-Namen aus der Import-Tabelle stehen natürlich auch als String in der EXE – nicht doppelt zählen.
        var imported = pe?.AllImports.ToHashSet() ?? [];
        bool importsD3D12 = imported.Contains("d3d12.dll");
        foreach (var (needle, ruleApi, weight) in StringRules)
        {
            if (imported.Contains(needle.ToLowerInvariant()) || importsD3D12 && ruleApi == GraphicsApi.D3D11)
                continue;
            if (strings.Contains(needle))
            {
                Add(ruleApi, weight);
                evidence.Add($"EXE enthält \"{needle}\" → {ruleApi.DisplayName()}");
            }
        }

        if (files.Any(p => p.EndsWith(Path.Combine("D3D12", "D3D12Core.dll"), StringComparison.OrdinalIgnoreCase)))
        {
            Add(GraphicsApi.D3D12, 0.95);
            evidence.Add("DirectX 12 Agility SDK (D3D12\\D3D12Core.dll) gefunden → DirectX 12");
        }

        // dxgi.dll allein sagt nur "DX10/11/12".
        if (pe is not null && pe.AllImports.Contains("dxgi.dll") && !scores.ContainsKey(GraphicsApi.D3D11) && !scores.ContainsKey(GraphicsApi.D3D12))
        {
            Add(GraphicsApi.D3D11, 0.3);
            Add(GraphicsApi.D3D12, 0.3);
        }

        // Engine-Standard als schwacher Tiebreaker.
        switch (engine)
        {
            case GameEngine.Unreal5: Add(GraphicsApi.D3D12, 0.2); break;
            case GameEngine.Unreal4: Add(GraphicsApi.D3D11, 0.2); break;
            case GameEngine.Unity: Add(GraphicsApi.D3D11, 0.15); break;
        }

        var mods = DetectMods(files, exeDir, evidence, warnings);
        if (mods.HasFlag(ExistingMod.Dxvk))
        {
            bool legacy = scores.GetValueOrDefault(GraphicsApi.D3D9) >= 0.8 || scores.GetValueOrDefault(GraphicsApi.D3D8) >= 0.8;
            if (legacy)
            {
                // Für DX8/9 bringt die DXVK-Route ihre eigene, getestete DXVK-Version mit.
                warnings.Add("DXVK ist bereits installiert. Die DXVK-Route ersetzt es durch die geprüfte Version (gesichert, „Rückgängig“ stellt es wieder her).");
            }
            else
            {
                // DXVK übersetzt D3D10/11 nach Vulkan – für die Brücke zählt die tatsächliche API.
                Add(GraphicsApi.Vulkan, 0.6);
                warnings.Add("DXVK ist installiert: Das Spiel läuft effektiv über Vulkan. Für die DX12-/DX11-Routen DXVK entfernen.");
            }
        }

        // Bei Gleichstand (z. B. beide Imports gedeckelt auf 1,0) entscheidet die Spiel-Datenbank.
        var preferredApi = GraphicsApi.None;
        if (dbEntry is not null)
        {
            evidence.Add($"Spiel-Datenbank: {dbEntry.Name}");
            foreach (var dbApi in dbEntry.Apis.Each())
                Add(dbApi, 0.5);
            if (dbEntry.DefaultApi != GraphicsApi.None)
            {
                Add(dbEntry.DefaultApi, 0.6);
                preferredApi = dbEntry.DefaultApi;
            }
            foreach (var variant in dbEntry.ApiExeVariants)
            {
                if (Path.GetFullPath(Path.Combine(game.InstallDir, variant.Exe)).Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                {
                    Add(variant.Api, 1.0);
                    preferredApi = variant.Api;
                }
            }
        }

        var api = BuildApiDetection(scores, preferredApi);
        var upscalers = DetectUpscalers(files, evidence);
        var antiCheat = AntiCheatDetector.Detect(files, exe);
        if (antiCheat.Detected)
            evidence.Add($"Anti-Cheat: {string.Join(", ", antiCheat.Systems)}");
        if (bitness == Bitness.X86)
        {
            warnings.Add("32-Bit-Spiel: DLSS läuft nur als 64-Bit-Code, es wird ein Hilfsprozess gebraucht (etwas mehr Overhead).");
            if (pe is { LargeAddressAware: false })
                warnings.Add("Die EXE nutzt nur 2 GB Speicher (kein Large-Address-Aware). Mit ReShade, DXVK und dem Feeder drohen Speicherabstürze. "
                             + (dbEntry?.LargeAddressHint ?? "Einen 4GB-Patch für dieses Spiel verwenden."));
            else if (pe is { LargeAddressAware: true })
                evidence.Add("Large-Address-Aware: ja (4 GB Adressraum)");
        }
        if (dbEntry is not null)
            warnings.AddRange(dbEntry.Notes);

        return new GameAnalysis(game, exe, bitness, engine, api, upscalers, mods, antiCheat, dbEntry, evidence, warnings);
    }

    /// <summary>Übernimmt das Ergebnis eines Testlaufs; es schlägt die statische Analyse.</summary>
    public static GameAnalysis ApplyProbe(GameAnalysis analysis, GraphicsApi probedApi, IEnumerable<string> probeEvidence)
    {
        if (probedApi == GraphicsApi.None)
            return analysis;
        var scores = new Dictionary<GraphicsApi, double>(analysis.Api.Scores) { [probedApi] = 1.0 };
        var api = new ApiDetection(probedApi, analysis.Api.Supported | probedApi, 1.0, scores, FromProbe: true);
        return analysis with { Api = api, Evidence = analysis.Evidence.Concat(probeEvidence).ToList() };
    }

    internal static ApiDetection BuildApiDetection(Dictionary<GraphicsApi, double> scores, GraphicsApi preferred = GraphicsApi.None)
    {
        if (scores.Count == 0)
            return ApiDetection.Unknown;
        var ordered = scores
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => kv.Key == preferred)
            .ThenByDescending(kv => (int)kv.Key)
            .ToList();
        var primary = ordered[0];
        var supported = ordered.Where(kv => kv.Value >= SupportThreshold).Aggregate(GraphicsApi.None, (acc, kv) => acc | kv.Key);
        if (supported == GraphicsApi.None)
            supported = primary.Key;
        // Knapper Abstand zum Zweitplatzierten senkt die Sicherheit.
        double runnerUp = ordered.Count > 1 ? ordered[1].Value : 0;
        double confidence = Math.Clamp(primary.Value - runnerUp * 0.5, 0.05, 1.0);
        return new ApiDetection(primary.Key, supported, confidence, scores);
    }

    private static void ScoreImports(List<string> imports, double factor, string label, Action<GraphicsApi, double> add, List<string> evidence)
    {
        // Viele DX12-Spiele importieren zusätzlich d3d11.dll (D3D11On12, Videos, Overlays) –
        // das allein heißt nicht, dass es einen DX11-Modus gibt.
        bool hasD3D12 = imports.Contains("d3d12.dll");
        // DX10/11/12-Spiele holen oft nur die PIX-Marker (D3DPERF_*) aus d3d9.dll, etwa Devil May Cry 4 SE
        // (d3d10_1.dll + d3d9.dll). Ein DX9-Übersetzer wäre dort wirkungslos.
        bool hasNewerD3D = hasD3D12 || imports.Contains("d3d11.dll") || imports.Contains("d3d10.dll") || imports.Contains("d3d10_1.dll");
        foreach (var (dll, api, weight) in ImportRules)
        {
            if (imports.Contains(dll))
            {
                double w = hasD3D12 && api == GraphicsApi.D3D11 || hasNewerD3D && api == GraphicsApi.D3D9 ? weight * 0.5 : weight;
                add(api, w * factor);
                evidence.Add($"{label} {dll} → {api.DisplayName()}");
            }
        }
    }

    private static IEnumerable<string> EngineModules(string exeDir, GameEngine engine)
    {
        var candidates = new List<string>();
        if (engine == GameEngine.Unity)
            candidates.Add(Path.Combine(exeDir, "UnityPlayer.dll"));
        try
        {
            candidates.AddRange(Directory.EnumerateFiles(exeDir, "*.dll")
                .Where(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return n.Contains("render") || n.Contains("gfx") || n.Contains("graphics") || n is "engine" or "engine_x64" or "engine2"
                           || n.EndsWith("-d3d12rhi") || n.EndsWith("-d3d11rhi") || n.EndsWith("-vulkanrhi");
                }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(12);
    }

    private static GameEngine DetectEngine(FileIndex files, string exe, HashSet<string> strings, List<string> evidence)
    {
        GameEngine engine = GameEngine.Unknown;
        bool unrealLayout = exe.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
                            || files.HasDirectory("Engine") && files.Any(f => f.Contains($"{Path.DirectorySeparatorChar}Binaries{Path.DirectorySeparatorChar}Win64{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        if (strings.Contains(Ue5Marker))
            engine = GameEngine.Unreal5;
        else if (strings.Contains(Ue4Marker))
            engine = GameEngine.Unreal4;
        else if (unrealLayout)
            engine = GameEngine.Unreal;
        else if (files.HasFile("UnityPlayer.dll"))
            engine = GameEngine.Unity;
        else if (files.HasFile("re_chunk_000.pak"))
            engine = GameEngine.REEngine;
        else if (files.HasFile("engine2.dll"))
            engine = GameEngine.Source2;
        else if (files.HasFile("CrySystem.dll"))
            engine = GameEngine.CryEngine;

        if (engine != GameEngine.Unknown)
            evidence.Add($"Engine: {engine.DisplayName()}");
        return engine;
    }

    private static UpscalerInfo DetectUpscalers(FileIndex files, List<string> evidence)
    {
        var f = UpscalerFeature.None;
        Version? dlssVersion = null, fgVersion = null;
        string? dlssPath = null;

        var dlss = files.Find("nvngx_dlss.dll").Select(p => (Path: p, Pe: PeFile.TryRead(p))).OrderByDescending(x => x.Pe?.FileVersion).FirstOrDefault();
        if (dlss.Path is not null)
        {
            f |= UpscalerFeature.DlssSuperResolution;
            dlssPath = dlss.Path;
            dlssVersion = dlss.Pe?.FileVersion;
            evidence.Add($"DLSS Super Resolution {dlssVersion?.ToString() ?? ""} ({Path.GetFileName(Path.GetDirectoryName(dlss.Path))})".Replace("  ", " "));
        }
        var fg = files.Find("nvngx_dlssg.dll").FirstOrDefault();
        if (fg is not null)
        {
            f |= UpscalerFeature.DlssFrameGeneration;
            fgVersion = PeFile.TryRead(fg)?.FileVersion;
            evidence.Add($"DLSS Frame Generation {fgVersion}".TrimEnd());
        }
        if (files.HasFile("nvngx_dlssd.dll"))
            f |= UpscalerFeature.DlssRayReconstruction;
        if (files.HasFile("nvngx_dlssnr.dll"))
            f |= UpscalerFeature.DlssNeuralRenderingDll;
        if (files.HasFile("sl.interposer.dll"))
        {
            f |= UpscalerFeature.Streamline;
            evidence.Add("NVIDIA Streamline");
            if (files.HasFile("sl.dlss.dll"))
                f |= UpscalerFeature.DlssSuperResolution;
            if (files.HasFile("sl.dlss_g.dll"))
                f |= UpscalerFeature.DlssFrameGeneration;
        }
        if (files.HasFile("sl.reflex.dll") || files.HasFile("NvLowLatencyVk.dll"))
            f |= UpscalerFeature.Reflex;
        if (files.Names.Any(n => n.StartsWith("amd_fidelityfx", StringComparison.OrdinalIgnoreCase) || n.StartsWith("ffx_fsr", StringComparison.OrdinalIgnoreCase)))
        {
            f |= UpscalerFeature.Fsr;
            evidence.Add("AMD FSR");
        }
        if (files.Names.Any(n => n.StartsWith("libxess", StringComparison.OrdinalIgnoreCase)))
        {
            f |= UpscalerFeature.Xess;
            evidence.Add("Intel XeSS");
        }
        return new UpscalerInfo(f, dlssVersion, dlssPath, fgVersion);
    }

    private static ExistingMod DetectMods(FileIndex files, string exeDir, List<string> evidence, List<string> warnings)
    {
        var m = ExistingMod.None;
        if (files.HasFile("ReShade.ini") || files.HasFile("ReShade64.dll") || files.HasFile("ReShade32.dll") || files.HasDirectory("reshade-shaders"))
            m |= ExistingMod.ReShade;
        if (files.HasFile("OptiScaler.ini"))
            m |= ExistingMod.OptiScaler;
        if (files.HasFile("dgVoodoo.conf"))
            m |= ExistingMod.DgVoodoo;
        if (files.HasFile("dxvk.conf") || new[] { "d3d11.dll", "dxgi.dll", "d3d9.dll", "d3d10core.dll" }
                .Select(n => Path.Combine(exeDir, n)).Any(p => File.Exists(p) && BinaryStringScanner.FindAny(p, ["dxvk"], 64L * 1024 * 1024).Count > 0))
            m |= ExistingMod.Dxvk;
        if (files.Names.Any(n => n.StartsWith("SpecialK", StringComparison.OrdinalIgnoreCase)))
            m |= ExistingMod.SpecialK;
        if (files.HasDirectory("reframework"))
            m |= ExistingMod.REFramework;
        if (files.HasDirectory(".dlss5-optimizer"))
            m |= ExistingMod.Dlss5Optimizer;
        var d3d9 = Path.Combine(exeDir, "d3d9.dll");
        if (files.HasFile("enbseries.ini") || files.HasFile("enblocal.ini")
            || File.Exists(d3d9) && BinaryStringScanner.FindAny(d3d9, ["ENBSeries", "enbseries"], 64L * 1024 * 1024).Count > 0)
        {
            m |= ExistingMod.Enb;
            warnings.Add("ENBSeries gefunden. ENB und die DLSS-5-Wege für DirectX 9 brauchen beide die d3d9.dll – ENB wird bei der Installation gesichert und deaktiviert, „Rückgängig“ stellt es wieder her.");
        }

        foreach (var addon in files.Names.Where(n => n.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".addon32", StringComparison.OrdinalIgnoreCase)))
        {
            if (addon.StartsWith("renodx", StringComparison.OrdinalIgnoreCase))
                m |= ExistingMod.RenoDx;
            else if (addon.StartsWith("dlss5-bridge", StringComparison.OrdinalIgnoreCase))
                m |= ExistingMod.Dlss5Bridge;
            else if (addon.StartsWith("dlss5-feed", StringComparison.OrdinalIgnoreCase))
                m |= ExistingMod.Dlss5Feeder;
            else if (addon.StartsWith("deep-fried-chicken", StringComparison.OrdinalIgnoreCase))
                m |= ExistingMod.DeepFriedChicken;
        }

        var foreign = m & ~ExistingMod.Dlss5Optimizer;
        if (foreign != ExistingMod.None)
        {
            evidence.Add($"Vorhandene Mods: {foreign}");
            if (!m.HasFlag(ExistingMod.Dlss5Optimizer))
                warnings.Add("Es sind bereits Mods installiert, die nicht von diesem Tool stammen. Vor der Installation prüfen, ob sie sich vertragen.");
        }
        return m;
    }
}

/// <summary>Einmal eingelesene Dateiliste eines Spielordners (begrenzt, damit riesige Ordner nicht hängen).</summary>
public sealed class FileIndex
{
    private readonly List<string> _paths;
    private readonly Dictionary<string, List<string>> _byName;
    private readonly HashSet<string> _dirNames;

    private FileIndex(List<string> paths, HashSet<string> dirNames)
    {
        _paths = paths;
        _dirNames = dirNames;
        _byName = paths.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<string> Names => _byName.Keys;

    public static FileIndex Build(string root, int maxDepth = 7, int maxFiles = 60_000)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = maxDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var files = new List<string>();
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            files.AddRange(Directory.EnumerateFiles(root, "*", options).Take(maxFiles));
            foreach (var d in Directory.EnumerateDirectories(root, "*", options).Take(maxFiles))
                dirs.Add(Path.GetFileName(d));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return new FileIndex(files, dirs);
    }

    public bool Any(Func<string, bool> predicate) => _paths.Any(predicate);

    public bool HasFile(string name) => _byName.ContainsKey(name);

    public IEnumerable<string> Find(string name) => _byName.GetValueOrDefault(name) ?? [];

    public bool HasDirectory(string name) => _dirNames.Contains(name);
}
