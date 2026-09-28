using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core.Decision;

/// <summary>
/// Alle Wege, DLSS 5 in ein Spiel zu bringen. Die Overhead-Werte stammen aus den Messungen und
/// Beschreibungen der jeweiligen Projekte (siehe README) und werden durch Benchmarks ersetzt.
/// </summary>
public static class RouteCatalog
{
    public static class Ids
    {
        public const string ReShade = "reshade";
        public const string OptiScalerNr = "optiscaler-dlssnr";
        public const string OptiScalerPreUpscale = "optiscaler-dlssnr-preupscale";
        public const string Bridge = "dlss5-bridge";
        public const string Feeder = "dlss5-feeder";
        public const string LumeniteFx = "lumenitefx";
        public const string RenoDx = "renodx-dlss5";
        public const string DeepFriedChicken = "deep-fried-chicken";
        public const string DgVoodoo = "dgvoodoo2";
        public const string Dxvk = "dxvk";
        public const string D3D8To9 = "d3d8to9";
        public const string ReShadeHeaders = "reshade-headers";
        public const string DlssNrModel = "nvngx-dlssnr";
        public const string DlssRuntime = "nvngx-dlss";
        public const string PresentMon = "presentmon";
    }

    private static readonly ComponentRequirement NeuralConsumer =
        new("DLSS-5-Add-on (RenoDX oder Deep Fried Chicken)", [Ids.DeepFriedChicken, Ids.RenoDx]);

    private static readonly ComponentRequirement NrModel = ComponentRequirement.One(Ids.DlssNrModel, "DLSS-5-Modell (nvngx_dlssnr.dll)");

    public static IReadOnlyList<RouteDefinition> All { get; } =
    [
        new(RouteId.NativeDlss5, "Natives DLSS 5",
            "Das Spiel bringt DLSS 5 selbst mit. Nur im Grafikmenü einschalten – kein Mod, kein Risiko.",
            Apis: GraphicsApi.D3D12 | GraphicsApi.D3D11 | GraphicsApi.Vulkan,
            Requires64Bit: false, RequiresGameDlss: false, AcceptsFsrOrXess: false,
            MotionVectors: MotionVectorSource.Engine, OverheadMs: 0, SupportsSuperResolution: true,
            Components: [], Caveats: [], Official: true),

        new(RouteId.OptiScalerNr, "OptiScaler + DLSS 5 (DLSSNR)",
            "OptiScaler hängt den DLSS-5-Pass direkt an den Upscaler des Spiels – unter DX12 auf demselben Gerät ohne Kopien, unter DX11 über dx11on12.",
            Apis: GraphicsApi.D3D12 | GraphicsApi.D3D11,
            Requires64Bit: true, RequiresGameDlss: true, AcceptsFsrOrXess: true,
            MotionVectors: MotionVectorSource.Engine, OverheadMs: 0.1, SupportsSuperResolution: true,
            Components: [ComponentRequirement.One(Ids.OptiScalerNr, "OptiScaler DLSSNR"), NrModel],
            Caveats: ["Experimentell und nicht von NVIDIA unterstützt."],
            PreUpscaleComponent: Ids.OptiScalerPreUpscale,
            PreUpscaleLabel: "OptiScaler DLSSNR PreSR (wilsjo2)",
            ModelScaleComponent: Ids.OptiScalerNr,
            ModelScaleApis: GraphicsApi.D3D12 | GraphicsApi.D3D11,
            ApiOverheadMs: new Dictionary<GraphicsApi, double> { [GraphicsApi.D3D11] = 0.8 }),

        new(RouteId.ReShadeNrAddon, "ReShade + DLSS-5-Add-on",
            "Das DLSS-5-Add-on greift die DLSS-Auswertung des Spiels ab und rechnet den Neural-Pass dahinter.",
            Apis: GraphicsApi.D3D12,
            Requires64Bit: true, RequiresGameDlss: true, AcceptsFsrOrXess: false,
            MotionVectors: MotionVectorSource.Engine, OverheadMs: 0.2, SupportsSuperResolution: true,
            Components: [ComponentRequirement.One(Ids.ReShade, "ReShade (mit Add-on-Unterstützung)"), NeuralConsumer, NrModel],
            Caveats: ["Closed-Source-Add-on aus der Community; Herkunft ungeprüft."]),

        new(RouteId.BridgeD3D11, "dlss5-bridge (DirectX 11)",
            "Kopiert Farbe, Tiefe und Bewegungsvektoren der DLSS-Auswertung in ein privates D3D12-Gerät und zurück.",
            Apis: GraphicsApi.D3D11,
            Requires64Bit: true, RequiresGameDlss: true, AcceptsFsrOrXess: false,
            MotionVectors: MotionVectorSource.Engine, OverheadMs: 0.9, SupportsSuperResolution: true,
            Components: [ComponentRequirement.One(Ids.ReShade, "ReShade (mit Add-on-Unterstützung)"), ComponentRequirement.One(Ids.Bridge, "dlss5-bridge"), NeuralConsumer, NrModel],
            Caveats: ["Kopien + ca. 0,8 ms CPU pro Frame. Mit skip_game=1 entfällt die doppelte DLSS-Auswertung."]),

        new(RouteId.BridgeVulkan, "dlss5-bridge (Vulkan)",
            "Spiegelt die DLSS-Auswertung des Spiels über importierte D3D12-Texturen.",
            Apis: GraphicsApi.Vulkan,
            Requires64Bit: true, RequiresGameDlss: true, AcceptsFsrOrXess: false,
            MotionVectors: MotionVectorSource.Engine, OverheadMs: 1.5, SupportsSuperResolution: true,
            Components: [ComponentRequirement.One(Ids.ReShade, "ReShade (mit Add-on-Unterstützung)"), ComponentRequirement.One(Ids.Bridge, "dlss5-bridge"), NeuralConsumer, NrModel],
            Caveats: ["Zwei DLSS-Sitzungen gleichzeitig (Spiel + Spiegel).", "NVIDIA Smooth Motion muss für Vulkan aus sein.", "ReShade für Vulkan wird global als Vulkan-Layer registriert (Administratorrechte)."],
            SemiAutomatic: true,
            SmoothMotionIncompatibleApis: GraphicsApi.Vulkan),

        new(RouteId.Feeder, "DLSS5-Feeder (Spiele ohne DLSS)",
            "Baut aus ReShade-Tiefe und geschätzten Bewegungsvektoren eine DLAA-Eingabe und führt DLSS 5 in einem privaten D3D12-Gerät aus.",
            Apis: GraphicsApi.D3D12 | GraphicsApi.D3D11 | GraphicsApi.D3D10 | GraphicsApi.Vulkan | GraphicsApi.OpenGL,
            Requires64Bit: false, RequiresGameDlss: false, AcceptsFsrOrXess: false,
            MotionVectors: MotionVectorSource.Estimated, OverheadMs: 1.2, SupportsSuperResolution: false,
            Components:
            [
                ComponentRequirement.One(Ids.ReShade, "ReShade (mit Add-on-Unterstützung)"),
                ComponentRequirement.One(Ids.Feeder, "DLSS5-Feeder"),
                ComponentRequirement.One(Ids.LumeniteFx, "LumeniteFX (Bewegungsvektoren)"),
                ComponentRequirement.One(Ids.ReShadeHeaders, "ReShade-Shader-Header"),
                ComponentRequirement.One(Ids.DlssRuntime, "nvngx_dlss.dll"),
                NeuralConsumer,
                NrModel,
            ],
            Caveats:
            [
                "Geschätzte Bewegungsvektoren: Schlieren bei schneller Bewegung, dünne Objekte werden weicher.",
                "Das HUD wird mitbearbeitet.",
                "OpenGL: LumeniteFX lieferte in Referenztests keine Bewegungsvektoren – die Diagnose zeigt das („MV probe 0 %“).",
            ],
            ModelScaleComponent: Ids.Feeder,
            ModelScaleApis: GraphicsApi.D3D11,
            SmoothMotionIncompatibleApis: GraphicsApi.Vulkan),

        new(RouteId.LegacyFeeder, "dgVoodoo2 + DLSS5-Feeder (DirectX 9 und älter)",
            "dgVoodoo2 übersetzt DirectX 8/9/DirectDraw nach DirectX 11, danach übernimmt der Feeder.",
            Apis: GraphicsApi.D3D9 | GraphicsApi.D3D8 | GraphicsApi.DirectDraw,
            Requires64Bit: false, RequiresGameDlss: false, AcceptsFsrOrXess: false,
            MotionVectors: MotionVectorSource.Estimated, OverheadMs: 1.6, SupportsSuperResolution: false,
            Components:
            [
                ComponentRequirement.One(Ids.DgVoodoo, "dgVoodoo2"),
                ComponentRequirement.One(Ids.ReShade, "ReShade (mit Add-on-Unterstützung)"),
                ComponentRequirement.One(Ids.Feeder, "DLSS5-Feeder"),
                ComponentRequirement.One(Ids.LumeniteFx, "LumeniteFX (Bewegungsvektoren)"),
                ComponentRequirement.One(Ids.ReShadeHeaders, "ReShade-Shader-Header"),
                ComponentRequirement.One(Ids.DlssRuntime, "nvngx_dlss.dll"),
                NeuralConsumer,
                NrModel,
            ],
            Caveats: ["Alte Spiele laufen oft mit fester Bildrate – DLSS 5 kostet dann nur Leistungsreserve.", "dgVoodoo scheitert in manchen Engines (z. B. Gamebryo: Fallout 3/New Vegas) – dort die DXVK-Route nehmen."],
            Experimental: true),

        new(RouteId.LegacyDxvkFeeder, "DXVK + DLSS5-Feeder (DirectX 9)",
            "DXVK übersetzt DirectX 9 nach Vulkan, ReShade läuft als Vulkan-Layer, der Feeder schickt Bild und Tiefe an einen 64-Bit-Hilfsprozess mit DLSS 5.",
            Apis: GraphicsApi.D3D9,
            Requires64Bit: false, RequiresGameDlss: false, AcceptsFsrOrXess: false,
            MotionVectors: MotionVectorSource.Estimated, OverheadMs: 1.4, SupportsSuperResolution: false,
            Components:
            [
                ComponentRequirement.One(Ids.Dxvk, "DXVK"),
                ComponentRequirement.One(Ids.ReShade, "ReShade (mit Add-on-Unterstützung)"),
                ComponentRequirement.One(Ids.Feeder, "DLSS5-Feeder"),
                ComponentRequirement.One(Ids.LumeniteFx, "LumeniteFX (Bewegungsvektoren)"),
                ComponentRequirement.One(Ids.ReShadeHeaders, "ReShade-Shader-Header"),
                ComponentRequirement.One(Ids.DlssRuntime, "nvngx_dlss.dll"),
                // Deep Fried Chicken ist auf 32-Bit-Vulkan (DXVK) laut Feeder-Doku ungetestet.
                ComponentRequirement.One(Ids.RenoDx, "RenoDX DLSS 5 Add-on"),
                NrModel,
            ],
            Caveats:
            [
                "Geschätzte Bewegungsvektoren: Schlieren bei schneller Bewegung möglich; das HUD wird mitbearbeitet.",
                "ReShade wird als Vulkan-Layer registriert – aktiv nur in Spielen mit ReShade.ini neben der EXE.",
            ],
            SmoothMotionIncompatibleApis: GraphicsApi.D3D9),

        new(RouteId.LegacyD3D8Dxvk, "d3d8to9 + DXVK + DLSS5-Feeder (DirectX 8)",
            "d3d8to9 macht aus DirectX 8 ein DirectX 9, DXVK daraus Vulkan – danach wie bei DirectX-9-Spielen über den Feeder und den 64-Bit-Hilfsprozess.",
            Apis: GraphicsApi.D3D8,
            Requires64Bit: false, RequiresGameDlss: false, AcceptsFsrOrXess: false,
            MotionVectors: MotionVectorSource.Estimated, OverheadMs: 1.5, SupportsSuperResolution: false,
            Components:
            [
                ComponentRequirement.One(Ids.D3D8To9, "d3d8to9"),
                ComponentRequirement.One(Ids.Dxvk, "DXVK"),
                ComponentRequirement.One(Ids.ReShade, "ReShade (mit Add-on-Unterstützung)"),
                ComponentRequirement.One(Ids.Feeder, "DLSS5-Feeder"),
                ComponentRequirement.One(Ids.LumeniteFx, "LumeniteFX (Bewegungsvektoren)"),
                ComponentRequirement.One(Ids.ReShadeHeaders, "ReShade-Shader-Header"),
                ComponentRequirement.One(Ids.DlssRuntime, "nvngx_dlss.dll"),
                ComponentRequirement.One(Ids.RenoDx, "RenoDX DLSS 5 Add-on"),
                NrModel,
            ],
            Caveats:
            [
                "Geschätzte Bewegungsvektoren: Schlieren bei schneller Bewegung möglich; das HUD wird mitbearbeitet.",
                "d3d8to9 braucht die alte DirectX-Laufzeit (D3DX9) – fehlt sie, startet das Spiel nicht.",
            ],
            SmoothMotionIncompatibleApis: GraphicsApi.D3D8),
    ];

    /// <summary>
    /// Der andere Übersetzer für denselben Fall – DXVK und dgVoodoo scheitern jeweils in anderen Spielen.
    /// </summary>
    public static RouteId? AlternativeTranslator(RouteId route, GraphicsApi api) => (route, api) switch
    {
        (RouteId.LegacyDxvkFeeder, GraphicsApi.D3D9) => RouteId.LegacyFeeder,
        (RouteId.LegacyD3D8Dxvk, GraphicsApi.D3D8) => RouteId.LegacyFeeder,
        (RouteId.LegacyFeeder, GraphicsApi.D3D9) => RouteId.LegacyDxvkFeeder,
        (RouteId.LegacyFeeder, GraphicsApi.D3D8) => RouteId.LegacyD3D8Dxvk,
        _ => null,
    };

    public static RouteDefinition Get(RouteId id) => All.First(r => r.Id == id);
}
