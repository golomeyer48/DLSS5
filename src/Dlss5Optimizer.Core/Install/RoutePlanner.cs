using System.Globalization;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Models;
using Ids = Dlss5Optimizer.Core.Decision.RouteCatalog.Ids;

namespace Dlss5Optimizer.Core.Install;

public sealed class PlanException(string message, IReadOnlyList<string> missingComponents) : Exception(message)
{
    public IReadOnlyList<string> MissingComponents { get; } = missingComponents;
}

/// <summary>
/// Übersetzt eine gewählte Konfiguration in konkrete Dateioperationen. Die Layouts folgen den
/// Anleitungen und Installern der jeweiligen Projekte (siehe README, Abschnitt Quellen).
/// </summary>
public sealed class RoutePlanner(ComponentAvailability components, ComponentStore store)
{
    private const string LumeniteTechnique = "Lumenite_Kernel@lumenite_Kernel.fx";
    private const string FeedTechnique = "DLSS5_Feed@DLSS5_Feed.fx";
    private const string FeedDebugTechnique = "DLSS5_Feed_Debug@DLSS5_Feed.fx";
    private const int LumeniteMvProvider = 3;

    public InstallPlan Plan(GameAnalysis game, Candidate candidate)
    {
        var gameDir = game.GameDir ?? throw new PlanException("Spiel-EXE unbekannt.", []);
        var ctx = new Context(game, candidate, gameDir, components, store);

        switch (candidate.Route.Id)
        {
            case RouteId.NativeDlss5:
                PlanNative(ctx);
                break;
            case RouteId.OptiScalerNr:
                PlanOptiScaler(ctx);
                break;
            case RouteId.ReShadeNrAddon:
            case RouteId.BridgeD3D11:
            case RouteId.BridgeVulkan:
                PlanReShadeConsumer(ctx);
                break;
            case RouteId.Feeder:
            case RouteId.LegacyFeeder:
            case RouteId.LegacyDxvkFeeder:
                PlanFeeder(ctx);
                break;
        }

        AddSettingHints(ctx);
        if (ctx.Missing.Count > 0)
            throw new PlanException($"Es fehlen Komponenten: {string.Join(", ", ctx.Missing.Distinct())}", ctx.Missing.Distinct().ToList());
        return new InstallPlan(candidate.Config, gameDir, ctx.Steps, ctx.Hints, ctx.Cleanup);
    }

    private static void PlanNative(Context c)
    {
        c.Steps.Add(new ManualStep("Im Spiel: Grafikeinstellungen → DLSS 5 (Neural Rendering) einschalten."));
    }

    private void PlanOptiScaler(Context c)
    {
        bool pre = c.Config.Placement == NrPlacement.PreUpscale;
        var package = pre ? Ids.OptiScalerPreUpscale : Ids.OptiScalerNr;
        var root = PackageRoot(c, package, "OptiScaler.dll");
        if (root is null)
            return;

        // Neben ReShade oder DXVK (dxgi.dll) weicht OptiScaler auf winmm.dll aus.
        var proxy = File.Exists(Path.Combine(c.GameDir, "dxgi.dll")) && !c.Game.Mods.HasFlag(ExistingMod.Dlss5Optimizer)
            ? "winmm.dll"
            : c.Config.Api == GraphicsApi.Vulkan ? "winmm.dll" : "dxgi.dll";

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var name = Path.GetFileName(rel);
            if (name.StartsWith("setup_", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = rel.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? proxy : rel;
            c.Steps.Add(new CopyFileStep(file, target, $"OptiScaler: {rel} → {target}"));
        }

        CopyModel(c, "");
        // DX11 läuft über dx11on12 – laut OptiScaler die einzige DX11-Variante, die DLSS 5 ausführen kann.
        if (c.Config.Api == GraphicsApi.D3D11)
            c.Ini("OptiScaler.ini", "Upscalers", "Dx11Upscaler", "dlss_12", "DirectX 11 über dx11on12 mit DLSS");
        else
            c.Ini("OptiScaler.ini", "Upscalers", "Dx12Upscaler", "dlss", "DLSS als Upscaler");
        c.Ini("OptiScaler.ini", "DlssNr", "Enabled", "true", "DLSS 5 in OptiScaler einschalten");
        c.Ini("OptiScaler.ini", "DlssNr", "WorkingScale", Num(c.Config.NrScale), $"Modellauflösung {c.Config.NrScale:P0}");
        if (pre)
            c.Ini("OptiScaler.ini", "DlssNr", "RunBeforeSR", "true", "DLSS 5 vor der Super Resolution ausführen");

        c.Cleanup.Add("OptiScaler.log");
        c.Hints.Add("OptiScaler-Menü im Spiel: Einfg-Taste. Dort unter „DlssNr“ Stärke und Intensität feinjustieren.");
        if (!c.Game.Upscalers.Has(UpscalerFeature.DlssSuperResolution))
            c.Hints.Add("Das Spiel hat kein DLSS: Im Spiel FSR oder XeSS einschalten – OptiScaler ersetzt es durch DLSS.");
    }

    private void PlanReShadeConsumer(Context c)
    {
        var route = c.Candidate.Route.Id;
        var consumer = ChooseConsumer();

        if (route == RouteId.BridgeVulkan)
            PlanReShadeVulkan(c, is32: false);
        else
            PlanReShadeDll(c, c.Game.Bitness == Bitness.X86 ? "ReShade32.dll" : "ReShade64.dll", "dxgi.dll");

        PlanConsumer(c, consumer, "", hostProcess: false);
        CopyModel(c, "");

        bool renoDxBridgesItself = consumer == Ids.RenoDx && RenoDxMajor() is null or >= 8;
        if (route == RouteId.BridgeVulkan || route == RouteId.BridgeD3D11 && !renoDxBridgesItself)
        {
            c.Copy(Ids.Bridge, "dlss5-bridge.addon64", "dlss5-bridge.addon64", "dlss5-bridge (ReShade-Add-on)");
            var cfg = route == RouteId.BridgeD3D11
                ? "# dlss5-bridge keep\r\nskip_game=1\r\n"
                : "# dlss5-bridge keep\r\nvk_sync=0\r\n";
            c.Steps.Add(new WriteTextStep("dlss5-bridge.cfg", cfg, "dlss5-bridge.cfg schreiben"));
            if (File.Exists(Path.Combine(c.GameDir, "dlss5-dx11-bridge.addon64")))
                c.Steps.Add(new RemoveFileStep("dlss5-dx11-bridge.addon64", "Alte dlss5-dx11-bridge entfernen (Konflikt)"));
        }
        if (route == RouteId.BridgeD3D11 && renoDxBridgesItself)
            c.Hints.Add("RenoDX 8.x überbrückt DirectX 11 selbst – dlss5-bridge wird deshalb nicht installiert.");
    }

    private void PlanFeeder(Context c)
    {
        bool is32 = c.Game.Bitness == Bitness.X86;
        var route = c.Candidate.Route.Id;
        // Über DXVK wird aus DX9 Vulkan: ReShade kommt dann als Vulkan-Layer, nicht als DLL im Spielordner.
        bool viaLayer = c.Config.Api == GraphicsApi.Vulkan || route == RouteId.LegacyDxvkFeeder;
        // Deep Fried Chicken ist auf 32-Bit-Vulkan ungetestet – dort nur RenoDX (so auch in allen Referenzen).
        var consumer = route == RouteId.LegacyDxvkFeeder || viaLayer && is32 ? Ids.RenoDx : ChooseConsumer();

        if (route == RouteId.LegacyFeeder)
            PlanDgVoodoo(c, is32);
        if (route == RouteId.LegacyDxvkFeeder)
            PlanDxvk(c, is32);

        if (viaLayer)
        {
            PlanReShadeVulkan(c, is32);
            // Ein ReShade-Proxy (dxgi.dll) würde ReShade ein zweites Mal laden.
            if (File.Exists(Path.Combine(c.GameDir, "dxgi.dll")))
                c.Steps.Add(new RemoveFileStep("dxgi.dll", "ReShade-Proxy dxgi.dll entfernen (ReShade läuft jetzt als Vulkan-Layer)"));
            // Über den Layer lädt ReShade früh geladene 32-Bit-Add-ons, bevor seine Laufzeit steht –
            // das Add-on meldet dann „No add-on was registered“ und wird entladen (Fallout 3).
            if (is32)
                c.Ini("ReShade.ini", "ADDON", "LoadFromDllMain", "", "Kein frühes Laden über den Vulkan-Layer (sonst registriert sich das Feeder-Add-on nicht)");
        }
        else
        {
            PlanReShadeDll(c, is32 ? "ReShade32.dll" : "ReShade64.dll", c.Config.Api == GraphicsApi.OpenGL ? "opengl32.dll" : "dxgi.dll");
        }

        var feedAddon = is32 ? "dlss5-feed.addon32" : "dlss5-feed.addon64";
        c.Copy(Ids.Feeder, feedAddon, feedAddon, "DLSS5-Feeder (ReShade-Add-on)", prefer32Bit: is32);
        c.Copy(Ids.Feeder, "DLSS5_Feed.fx", Path.Combine("reshade-shaders", "Shaders", "DLSS5_Feed.fx"), "Feeder-Shader");

        // DLSS5_Feed.fx und LumeniteFX binden ReShade.fxh ein – ohne die Header kompiliert nichts.
        foreach (var header in new[] { "ReShade.fxh", "ReShadeUI.fxh" })
            c.Copy(Ids.ReShadeHeaders, header, Path.Combine("reshade-shaders", "Shaders", header), $"ReShade-Header {header}");
        if (store.FindFile(Ids.ReShadeHeaders, "DrawText.fxh") is { } drawText)
            c.Steps.Add(new CopyFileStep(drawText, Path.Combine("reshade-shaders", "Shaders", "DrawText.fxh"), "ReShade-Header DrawText.fxh"));

        // LumeniteFX: alle Shader inkl. include\ und die Blue-Noise-Textur.
        var lumeniteShaders = PackageRoot(c, Ids.LumeniteFx, "lumenite_Kernel.fx");
        if (lumeniteShaders is not null)
        {
            foreach (var f in Directory.EnumerateFiles(lumeniteShaders, "*", SearchOption.AllDirectories))
                c.Steps.Add(new CopyFileStep(f, Path.Combine("reshade-shaders", "Shaders", Path.GetRelativePath(lumeniteShaders, f)), $"LumeniteFX: {Path.GetFileName(f)}"));
            var texture = store.FindFile(Ids.LumeniteFx, "lumenite_bluenoise256.png");
            if (texture is not null)
                c.Steps.Add(new CopyFileStep(texture, Path.Combine("reshade-shaders", "Textures", "lumenite_bluenoise256.png"), "LumeniteFX-Textur"));
        }

        WritePreset(c);
        WriteReShadeDefines(c);
        WriteFeedConfig(c);

        // DLSS/NGX gibt es nur als 64-Bit-Code: 32-Bit-Spiele bekommen einen 64-Bit-Hilfsprozess in host64\.
        var consumerDir = is32 ? "host64" : "";
        if (is32)
        {
            c.Copy(Ids.Feeder, "dlss5-feed-host64.exe", Path.Combine("host64", "dlss5-feed-host64.exe"), "Feeder-Hilfsprozess (64 Bit)");
            c.Copy(Ids.ReShade, "ReShade64.dll", Path.Combine("host64", "dxgi.dll"), "ReShade 64 Bit für den Hilfsprozess");
            c.Steps.Add(new WriteTextStep(Path.Combine("host64", "ReShade.ini"), "[GENERAL]\r\nEffectSearchPaths=.\\\r\nTextureSearchPaths=.\\\r\n", "ReShade.ini für den Hilfsprozess"));
        }
        PlanConsumer(c, consumer, consumerDir, hostProcess: is32);
        CopyModel(c, consumerDir);
        c.CopyResolved(Ids.DlssRuntime, "nvngx_dlss.dll", Path.Combine(consumerDir, "nvngx_dlss.dll"), "DLSS-Laufzeit (DLAA)");

        ApplyUserIniTweaks(c);
        if (c.Game.Mods.HasFlag(ExistingMod.Enb) && route is RouteId.LegacyDxvkFeeder or RouteId.LegacyFeeder)
            c.Hints.Add("ENB wurde deaktiviert (seine d3d9.dll ist gesichert). „Rückgängig“ stellt ENB wieder her.");

        c.Hints.Add("ReShade-Menü: Pos1-Taste. Lumenite_Kernel muss aktiv sein und über DLSS5_Feed stehen.");
        if (is32)
            c.Hints.Add("Der DLSS-5-Regler liegt im Hilfsprozess: ReShade-Menü → Add-ons → DLSS 5 Feed → „Show the DLSS 5 panel in-game“.");
        if (viaLayer)
            c.Hints.Add("NVIDIA Smooth Motion für dieses Spiel ausschalten – mit dem Feeder unter Vulkan unverträglich.");
    }

    /// <summary>DXVK als d3d9.dll (32 oder 64 Bit) – ersetzt dabei ENB, ein älteres DXVK oder dgVoodoo (gesichert).</summary>
    private void PlanDxvk(Context c, bool is32)
    {
        var arch = is32 ? "x32" : "x64";
        var dir = store.FilesDir(Ids.Dxvk);
        var src = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "d3d9.dll", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
                .FirstOrDefault(p => p.Replace('\\', '/').Contains($"/{arch}/", StringComparison.OrdinalIgnoreCase))
            : null;
        if (src is null)
        {
            c.Missing.Add(Ids.Dxvk);
            return;
        }
        c.Steps.Add(new CopyFileStep(src, "d3d9.dll", $"DXVK ({arch}) als d3d9.dll – DirectX 9 → Vulkan"));
        if (File.Exists(Path.Combine(c.GameDir, "dgVoodoo.conf")))
            c.Hints.Add("dgVoodoo war installiert – seine D3D9.dll wurde durch DXVK ersetzt (gesichert).");
        // DXVK schreibt Logs und Shader-Caches neben die EXE.
        c.Cleanup.AddRange(["*_d3d9.log", "*_dxgi.log", "*.dxvk-cache"]);
    }

    /// <summary>
    /// Pflichtwerte für dlss5-feed.cfg (ohne mode=2 läuft nur ein Transporttest, reset/rebuild-Werte
    /// ungleich 0 beenden das Neural Rendering) plus getestete Werte aus der Spiel-Datenbank.
    /// </summary>
    private static void WriteFeedConfig(Context c)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = "1",
            ["mode"] = "2",
            ["reset_every"] = "0",
            ["warmup_rebuild"] = "0",
            ["rebuild"] = "0",
        };
        foreach (var (key, value) in c.Game.DbEntry?.FeedConfig ?? [])
            values[key] = value;
        if (c.Config.NrScale < 1.0 && c.Config.Api == GraphicsApi.D3D11)
        {
            values["work_resolution"] = ((int)Math.Round(c.Config.NrScale * 100)).ToString(CultureInfo.InvariantCulture);
            values["work_upscale"] = "1";
        }
        foreach (var (key, value) in values)
            c.Ini("dlss5-feed.cfg", "", key, value, $"{key}={value}");
    }

    /// <summary>Getestete Tiefenpuffer-Einstellungen des Spiels in ReShade.ini [GENERAL] einmischen.</summary>
    private static void WriteReShadeDefines(Context c)
    {
        var defines = c.Game.DbEntry?.ReShadeDefines ?? [];
        if (defines.Length == 0)
            return;
        var existing = IniFile.Load(Path.Combine(c.GameDir, "ReShade.ini")).Get("GENERAL", "PreprocessorDefinitions") ?? "";
        string Key(string d) => d.Split('=')[0].Trim();
        var merged = existing.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(d => !defines.Any(n => Key(n).Equals(Key(d), StringComparison.OrdinalIgnoreCase)))
            .Concat(defines);
        c.Ini("ReShade.ini", "GENERAL", "PreprocessorDefinitions", string.Join(",", merged), "Tiefenpuffer-Einstellungen für dieses Spiel");
    }

    /// <summary>Einstellungsdateien des Spiels (z. B. MSAA aus); nur wenn die Datei schon existiert.</summary>
    private static void ApplyUserIniTweaks(Context c)
    {
        foreach (var t in c.Game.DbEntry?.UserIniTweaks ?? [])
        {
            c.Steps.Add(new IniSetStep(t.File, t.Section, t.Key, t.Value, $"{Path.GetFileName(t.File.Replace('\\', '/'))}: {t.Reason}", OnlyIfExists: true));
            c.Hints.Add($"{t.Reason}. Falls die Einstellung nicht übernommen wurde (Datei fehlte): Spiel einmal über den Launcher starten und „Reparieren“ klicken.");
        }
    }

    private void PlanDgVoodoo(Context c, bool is32)
    {
        var arch = is32 ? "x86" : "x64";
        var dlls = c.Config.Api switch
        {
            GraphicsApi.D3D8 => new[] { "D3D8.dll" },
            GraphicsApi.DirectDraw => ["DDraw.dll", "D3DImm.dll"],
            _ => ["D3D9.dll"],
        };
        foreach (var dll in dlls)
        {
            // Das Archiv enthält MS\x86 und MS\x64 – die passende Architektur nehmen.
            var dir = store.FilesDir(Ids.DgVoodoo);
            var src = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, dll, new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
                    .FirstOrDefault(p => p.Replace('\\', '/').Contains($"/MS/{arch}/", StringComparison.OrdinalIgnoreCase))
                : null;
            if (src is null)
                c.Missing.Add(Ids.DgVoodoo);
            else
                c.Steps.Add(new CopyFileStep(src, dll, $"dgVoodoo2: {dll} ({arch})"));
        }
        c.Copy(Ids.DgVoodoo, "dgVoodoo.conf", "dgVoodoo.conf", "dgVoodoo2-Konfiguration");
        c.Ini("dgVoodoo.conf", "General", "OutputAPI", "d3d11_fl11_0", "Ausgabe über DirectX 11 (dort greift ReShade)");
        c.Ini("dgVoodoo.conf", "DirectX", "DisableAndPassThru", "false", "dgVoodoo aktiv lassen");
        c.Ini("dgVoodoo.conf", "DirectX", "dgVoodooWatermark", "false", "Wasserzeichen aus (DLSS 5 würde es mitbearbeiten)");
        c.Ini("dgVoodoo.conf", "DirectX", "VideoCard", "internal3D", "Virtuelle Grafikkarte");
        // 1 GB wie im Feeder-Handbuch: mehr verwirrt manche alten Engines, weniger (Standard 256 MB) führt zu Abstürzen.
        c.Ini("dgVoodoo.conf", "DirectX", "VRAM", "1024", "Videospeicher für alte Spiele");
    }

    private void PlanReShadeDll(Context c, string sourceDll, string targetName)
    {
        c.Copy(Ids.ReShade, sourceDll, targetName, $"ReShade als {targetName}", prefer32Bit: sourceDll.Contains("32"));
        ReShadeIni(c);
    }

    private void PlanReShadeVulkan(Context c, bool is32)
    {
        var bits = is32 ? "32" : "64";
        var json = store.FindFile(Ids.ReShade, $"ReShade{bits}.json");
        if (json is null || store.FindFile(Ids.ReShade, $"ReShade{bits}.dll") is null)
        {
            c.Missing.Add($"ReShade (Vulkan-Layer, {bits} Bit)");
            return;
        }
        c.Steps.Add(new RegisterVulkanLayerStep(json, $"ReShade als Vulkan-Layer registrieren ({bits} Bit)", is32));
        ReShadeIni(c);
        c.Hints.Add("Der ReShade-Vulkan-Layer greift nur in Spielen mit ReShade.ini neben der EXE.");
    }

    private static void ReShadeIni(Context c)
    {
        c.Ini("ReShade.ini", "ADDON", "AddonPath", @".\", "Add-ons neben der EXE laden");
        c.Ini("ReShade.ini", "GENERAL", "EffectSearchPaths", @".\reshade-shaders\Shaders\**", "Shader-Pfad");
        c.Ini("ReShade.ini", "GENERAL", "TextureSearchPaths", @".\reshade-shaders\Textures\**", "Textur-Pfad");
        c.Ini("ReShade.ini", "GENERAL", "PresetPath", @".\ReShadePreset.ini", "Preset-Datei");
        c.Cleanup.AddRange(["ReShade.log", "ReShade64.log", "ReShade32.log", "ReShadePreset.ini", "reshade-shaders"]);
    }

    /// <param name="hostProcess">Add-on läuft im 64-Bit-Hilfsprozess eines 32-Bit-Spiels.</param>
    private void PlanConsumer(Context c, string consumer, string dir, bool hostProcess)
    {
        if (consumer == Ids.RenoDx)
        {
            var addon = store.FindFile(Ids.RenoDx, "renodx-dlss5*.addon64");
            if (addon is null)
            {
                c.Missing.Add("RenoDX DLSS 5 Add-on");
                return;
            }
            c.Steps.Add(new CopyFileStep(addon, Path.Combine(dir, "renodx-dlss5.addon64"), "RenoDX DLSS 5 Add-on"));
            var ini = Path.Combine(dir, "ReShade.ini");
            c.Ini(ini, "RenoDX.DLSS5", "NeuralUplift", "1", "Neural Rendering an");
            c.Ini(ini, "RenoDX.DLSS5", "NREnableUpscaling", "0", "Upscaling bleibt beim Spiel");
            if (hostProcess)
            {
                // Werte der getesteten 32-Bit-Aufbauten: nur NGX-Hooks, NRStyle=0 (NRStyle=2 ergibt ein schwarzes Bild).
                c.Ini(ini, "RenoDX.DLSS5", "EnableHooks", "2", "Nur NGX-Hooks (der Feeder liefert die DLSS-Aufrufe)");
                c.Ini(ini, "RenoDX.DLSS5", "NRStyle", "0", "NRStyle=0 – mit 2 bleibt das Bild bei 32-Bit-Spielen schwarz");
                c.Ini(ini, "ADDON", "LoadFromDllMain", "renodx-dlss5.addon64", "Add-on im Hilfsprozess früh laden");
            }
        }
        else
        {
            c.Copy(Ids.DeepFriedChicken, "deep-fried-chicken.addon64", Path.Combine(dir, "deep-fried-chicken.addon64"), "Deep Fried Chicken (Add-on)");
            c.Copy(Ids.DeepFriedChicken, "deep-fried-chicken-nvngx.dll", Path.Combine(dir, "deep-fried-chicken-nvngx.dll"), "Deep Fried Chicken (NGX-Brücke)");
            var cfg = store.FindFile(Ids.DeepFriedChicken, "deep-fried-chicken.cfg");
            if (cfg is not null)
                c.Steps.Add(new CopyFileStep(cfg, Path.Combine(dir, "deep-fried-chicken.cfg"), "Deep Fried Chicken (Konfiguration)"));
            c.Ini(Path.Combine(dir, "ReShade.ini"), "ADDON", "LoadFromDllMain", "deep-fried-chicken.addon64", "Add-on früh laden (spart den Neustart)");
        }
    }

    private void WritePreset(Context c)
    {
        // Vorhandenes Preset behalten, Lumenite und Feed in der richtigen Reihenfolge ans Ende setzen.
        var presetPath = Path.Combine(c.GameDir, "ReShadePreset.ini");
        var preset = IniFile.Load(presetPath);
        List<string> Merge(string key, params string[] tail)
        {
            var list = (preset.Get("", key) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !t.Equals(LumeniteTechnique, StringComparison.OrdinalIgnoreCase) && !t.StartsWith("DLSS5_Feed", StringComparison.OrdinalIgnoreCase))
                .ToList();
            list.AddRange(tail);
            return list;
        }
        c.Ini("ReShadePreset.ini", "", "Techniques", string.Join(",", Merge("Techniques", LumeniteTechnique, FeedTechnique)), "Techniken: Lumenite_Kernel vor DLSS5_Feed");
        c.Ini("ReShadePreset.ini", "", "TechniqueSorting", string.Join(",", Merge("TechniqueSorting", LumeniteTechnique, FeedTechnique, FeedDebugTechnique)), "Reihenfolge der Techniken");

        var defs = (preset.Get("DLSS5_Feed.fx", "PreprocessorDefinitions") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(d => !d.StartsWith("DLSS5_MV_PROVIDER", StringComparison.OrdinalIgnoreCase))
            .Append($"DLSS5_MV_PROVIDER={LumeniteMvProvider}");
        c.Ini("ReShadePreset.ini", "DLSS5_Feed.fx", "PreprocessorDefinitions", string.Join(",", defs), "Bewegungsvektoren von LumeniteFX");
    }

    private static void AddSettingHints(Context c)
    {
        var cfg = c.Config;
        if (cfg.SuperResolution is not (SrMode.Native))
            c.Hints.Add($"Im Spiel: {cfg.SuperResolution.DisplayName()} – in der NVIDIA App unter „DLSS-Override – Modellvoreinstellung“ Preset {cfg.SuperResolution.RecommendedPreset()} wählen.");
        switch (cfg.FrameGen)
        {
            case FrameGenMode.DlssFg2x:
                c.Hints.Add("Im Spiel: DLSS Frame Generation einschalten, NVIDIA Reflex auf „Ein + Boost“.");
                break;
            case FrameGenMode.DlssMfg3x or FrameGenMode.DlssMfg4x or FrameGenMode.DlssMfg6x:
                c.Hints.Add($"Im Spiel Frame Generation einschalten; in der NVIDIA App „DLSS-Override – Frame Generation“ auf {cfg.FrameGen.DisplayName().Split(' ').Last()} (oder „Dynamisch“) stellen. Reflex an.");
                break;
            case FrameGenMode.SmoothMotion:
                c.Hints.Add("NVIDIA App → Grafik → Programmeinstellungen → dieses Spiel → „Smooth Motion“ einschalten.");
                break;
        }
        c.Hints.Add("G-Sync/VRR an und V-Sync im Treiber erzwingen; Reflex begrenzt die Bildrate dann automatisch knapp unter der Bildwiederholrate.");
    }

    private void CopyModel(Context c, string dir) =>
        c.CopyResolved(Ids.DlssNrModel, SystemFileLocator.DlssNrFile, Path.Combine(dir, SystemFileLocator.DlssNrFile), "DLSS-5-Modell (aus dem NVIDIA-Treiber)");

    /// <summary>
    /// Deep Fried Chicken, wenn der Nutzer es importiert hat (vom Feeder-Autor empfohlen), sonst RenoDX.
    /// </summary>
    private string ChooseConsumer()
    {
        if (components.IsAvailable(Ids.DeepFriedChicken))
            return Ids.DeepFriedChicken;
        return Ids.RenoDx;
    }

    private int? RenoDxMajor()
    {
        var version = store.Get(Ids.RenoDx)?.Version;
        if (version is null)
            return null;
        var digits = new string(version.SkipWhile(ch => !char.IsDigit(ch)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : null;
    }

    /// <summary>Ordner innerhalb eines Pakets, der die Leitdatei enthält (Archive haben oft einen Oberordner).</summary>
    private string? PackageRoot(Context c, string id, string leadFile)
    {
        var lead = store.FindFile(id, leadFile);
        if (lead is null)
        {
            c.Missing.Add(id);
            return null;
        }
        return Path.GetDirectoryName(lead);
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class Context(GameAnalysis game, Candidate candidate, string gameDir, ComponentAvailability components, ComponentStore store)
    {
        public GameAnalysis Game { get; } = game;
        public Candidate Candidate { get; } = candidate;
        public Configuration Config => Candidate.Config;
        public string GameDir { get; } = gameDir;
        public List<InstallStep> Steps { get; } = [];
        public List<string> Hints { get; } = [];
        public List<string> Cleanup { get; } = [];
        public List<string> Missing { get; } = [];

        public void Ini(string file, string section, string key, string value, string description) =>
            Steps.Add(new IniSetStep(file, section, key, value, $"{file}: {description}"));

        /// <summary>Datei aus dem Komponentenspeicher kopieren.</summary>
        public void Copy(string id, string fileName, string target, string description, bool prefer32Bit = false)
        {
            var src = store.FindFile(id, fileName, prefer32Bit);
            if (src is null)
                Missing.Add(id);
            else
                Steps.Add(new CopyFileStep(src, target, description));
        }

        /// <summary>Datei aus Treiber, Spielbibliothek oder Speicher kopieren.</summary>
        public void CopyResolved(string id, string fileName, string target, string description)
        {
            var src = components.ResolveFile(id, fileName);
            if (src is null)
                Missing.Add(id);
            else
                Steps.Add(new CopyFileStep(src, target, description));
        }
    }
}
