# DLSS5 Optimizer

Windows-Tool, das DLSS 5 (Neural Rendering) in möglichst viele Spiele bringt – und dabei pro Spiel
automatisch die Variante mit der besten Kombination aus Bildqualität und Leistung wählt.

- erkennt, welche Grafik-API ein Spiel wirklich nutzt (DirectX 8–12, Vulkan, OpenGL)
- wählt die passende Einbindung (nativ, OptiScaler, ReShade-Add-on, dlss5-bridge, Feeder, dgVoodoo2)
- rechnet für jede Kombination aus Upscaling, DLSS-5-Modellauflösung und Frame Generation die zu
  erwartende Bildrate aus und nimmt die beste, die dein Ziel erreicht
- installiert mit Sicherung und stellt auf Knopfdruck den Originalzustand wieder her

> **Status:** Erste Version. Kern und Installer sind mit 86 Unit-Tests abgedeckt; die Oberfläche und
> die Windows-Teile (Registry, PresentMon, Spielstart) sind gebaut, aber noch nicht auf echter
> Hardware getestet. Bitte zuerst mit einem Einzelspieler-Spiel ausprobieren.

## Schnellstart

1. **Holen:** Unter *Actions → DLSS5 Optimizer → neuester Lauf → Artifacts* die Datei
   `DLSS5Optimizer-win-x64` herunterladen (eine einzelne EXE, kein .NET nötig).
   Oder selbst bauen: `build.bat` (braucht das .NET 10 SDK).
2. **Starten:** `DLSS5Optimizer.exe` fragt nach Adminrechten. Die werden für Spielordner unter
   *Programme* und für PresentMon-Messungen gebraucht.
3. Das Tool liest Steam, Epic, GOG und Xbox-Spiele ein. Weitere Ordner über „Ordner hinzufügen“.
4. **Spiel wählen → Empfehlung prüfen → „DLSS 5 installieren“.** Fehlende Open-Source-Teile lädt
   das Tool nach Rückfrage direkt aus der Originalquelle.
5. **Messen (empfohlen):**
   1. Das Spiel ohne DLSS 5 starten, in eine typische Szene gehen und „Messen“ klicken.
   2. DLSS 5 installieren.
   3. Erneut messen.

   Danach rechnet das Tool mit den echten Kosten deiner Karte in genau diesem Spiel.

Voraussetzungen:
- GeForce RTX 50
- NVIDIA-Treiber **≥ 616.56**; ab dieser Version liegt das DLSS-5-Modell `nvngx_dlssnr.dll` im Treiber
- Windows 10/11 x64

Für DLSS Frame Generation muss die hardwarebeschleunigte GPU-Planung eingeschaltet sein.

## So entscheidet das Tool

### 1. Erkennung der Grafik-API (drei Stufen)

| Stufe | Was geprüft wird | Grenzen |
|---|---|---|
| Dateien | PE-Header (32/64 Bit), statische und verzögerte Imports der EXE (`d3d12.dll`, `vulkan-1.dll` …). Außerdem Engine-DLLs, `D3D12\D3D12Core.dll` (Agility SDK), Strings wie `D3D12RHI`/`VulkanRHI` und die Unreal-Version (`++UE5+Release`) | Viele DX12-Spiele laden zusätzlich `d3d11.dll` – das wird herausgerechnet |
| Wissen | Spiel-Datenbank (`Data/games.json`), z. B. BG3 mit getrennten EXEs für Vulkan und DX11 | wird mit `%LOCALAPPDATA%\DLSS5Optimizer\games.json` erweitert |
| Testlauf | Geladene Module des laufenden Spiels plus PresentMon-Runtime: `D3D12Core.dll` ⇒ DX12; `vulkan-1.dll` + NVIDIA-Vulkan-Treiber ⇒ Vulkan (auch DXVK) usw. | schlägt immer die statische Analyse |

Außerdem werden erkannt:
- DLSS SR/FG/RR (mit Version), Streamline, FSR, XeSS und Reflex
- bereits vorhandene Mods: ReShade, OptiScaler, DXVK, dgVoodoo, Special K, RenoDX, REFramework
- Anti-Cheat: EAC, BattlEye, Javelin, GameGuard, Tencent ACE, XIGNCODE, Vanguard und andere

### 2. Routen

| Situation | Route | Bewegungsvektoren | Aufwand |
|---|---|---|---|
| Spiel hat DLSS 5 eingebaut | **Nativ** – nur im Menü einschalten | aus dem Spiel | – |
| DX12 mit DLSS/FSR/XeSS | **OptiScaler DLSSNR** (Pass direkt am Upscaler, gleiches D3D12-Gerät) | aus dem Spiel | ~0,1 ms |
| DX11 mit DLSS/FSR/XeSS | **OptiScaler DLSSNR** über dx11on12 | aus dem Spiel | ~0,9 ms |
| DX12 mit DLSS | ReShade + RenoDX / Deep Fried Chicken | aus dem Spiel | ~0,2 ms |
| DX11 mit DLSS | ReShade + dlss5-bridge + Add-on (RenoDX 8.x braucht keine Brücke) | aus dem Spiel | ~0,9 ms |
| Vulkan mit DLSS | ReShade (Vulkan-Layer) + dlss5-bridge + Add-on | aus dem Spiel | ~1,5 ms, zwei DLSS-Sitzungen |
| kein Upscaler (DX10–12, Vulkan, OpenGL) | ReShade + **DLSS5-Feeder** + LumeniteFX + Add-on | geschätzt (Schlieren möglich) | ~1,2 ms |
| DX9, DX8, DirectDraw | **dgVoodoo2** → DX11 → Feeder | geschätzt | ~1,6 ms |
| Anti-Cheat | **gesperrt** (Bann-Risiko). Freischalten nur ausdrücklich und nur für offline | – | – |

32-Bit-Spiele bekommen einen 64-Bit-Hilfsprozess (`host64\`), weil DLSS nur als 64-Bit-Code existiert.

### 3. Leistungsmodell und Auswahl

```
Frame-Zeit = Render-Zeit(Upscaling-Modus) + DLSS-5-Kosten × verarbeitete Megapixel + Routen-Aufwand
```

Das Modell trifft die veröffentlichten Messungen:
- RTX 5090, 4K, NBA 2K27: 128 → 63 fps
- RTX 5070 Ti, 4K, Mod: 180 → 80 fps
- dieselbe Karte mit Pre-Upscale: 122 fps

Ohne Messung schätzt es aus der Tensor-Leistung der Karte, nach „Messen“ rechnet es mit echten Werten.

Für jede API, die das Spiel kann, bewertet die Engine alle Kombinationen aus Route, Upscaling
(DLAA … Ultra-Leistung, mit DLSS-4.5-Preset K/M/L), DLSS-5-Modellauflösung (100/75/50 %),
Position (nach/vor dem Hochskalieren) und Frame Generation (aus, 2×–6×, Smooth Motion).
Gewählt wird die Kombination mit der **höchsten Qualität, die das Ziel erreicht**:

| Profil | Ziel |
|---|---|
| Qualität | 60 fps |
| Ausgewogen | ¾ der Bildwiederholrate |
| Ziel-FPS | frei wählbar |

Dabei gelten zwei Regeln:
- Frame Generation wird unter ~55 echten fps nie empfohlen.
- Kann ein Spiel mehrere APIs, empfiehlt das Tool den API-Wechsel samt Startparameter (`-dx12`, `-force-d3d12` …).

## Was die Leistung wirklich bringt (Recherche, Stand September 2026)

DLSS 5 macht Spiele **langsamer**: Es kostet je nach Karte und Auflösung 35–70 % der Bildrate.
Die Hebel, sortiert nach Wirkung:

| Hebel | Messwert | Preis |
|---|---|---|
| DLSS 5 **vor** dem Hochskalieren (Pre-Upscale-Fork) | RTX 5070 Ti 4K: 80 → 122 fps (Verlust 56 % → 25–30 %) | laut Tester kaum sichtbar, experimentell |
| Modellauflösung 75 % | 64 → 77 fps, mit FG 96–98 fps | bei 75 % kaum sichtbar, bei 50 % weicher |
| Upscaling-Modus + DLSS-4.5-Preset (M bei Leistung, L bei Ultra-Leistung) | senkt die Render-Zeit | Preset M hält „Leistung“ nahe an „Qualität“ |
| Frame Generation (MFG bis 6× auf RTX 50, Smooth Motion ab RTX 40) | vervielfacht die angezeigte Bildrate | mehr Latenz: Basis ≥ 60 fps, Reflex an |
| DX12 statt DX11/Vulkan für Mod-Routen | spart Kopien und die zweite DLSS-Sitzung | – |
| Treiber 616.56 | stabil | 616.64/616.86: bekannte Abstürze mancher Add-ons |

## Komponenten und Rechtliches

**Das Tool liefert keine fremden Dateien aus.**
- Open-Source-Teile lädt es auf deinem PC direkt aus der Originalquelle, mit SHA-256-Prüfung, wo das Release einen Hash angibt.
- Das DLSS-5-Modell kommt aus deinem NVIDIA-Treiber.
- Geschlossene Add-ons importierst du selbst.

| Komponente | Lizenz | Beschaffung |
|---|---|---|
| ReShade 6.8 (Add-on) | BSD-3 | reshade.me, DLLs werden aus dem Setup entpackt |
| OptiScaler DLSSNR (Dagherbou) / PreSR-Fork (wilsjo2) | GPL-3.0 | GitHub-Releases |
| dlss5-bridge, DLSS5-Feeder | MIT | GitHub-Releases |
| LumeniteFX | AGNYA (Rechte beim Autor) | direkt vom Repo des Autors |
| dgVoodoo2 | Freeware | GitHub-Releases |
| PresentMon | MIT | GitHub-Releases |
| `nvngx_dlssnr.dll` | NVIDIA | aus dem installierten Treiber |
| `nvngx_dlss.dll` | NVIDIA DLSS SDK | aus einem installierten Spiel, sonst von github.com/NVIDIA/DLSS |
| **RenoDX DLSS 5** | Closed Source | Download aus dem Community-Spiegel (RankFTW/rhi-repo) nach extra Warnung, oder Import |
| **Deep Fried Chicken** | Closed Source, Weitergabe untersagt | nur **Import** (Discord des Autors) |

Bewusst **nicht** unterstützt: veränderte oder geleakte DLSS-Modelle, etwa die „RTX 20–40“-Builds.
Auf einer RTX 50 wird das offizielle Modell aus dem Treiber verwendet.

## Sicherheit

- **Backups:** Jede Datei, die das Tool überschreibt oder ändert, landet vorher in
  `<Spiel>\.dlss5-optimizer\backup`. Schlägt ein Schritt fehl, wird alles zurückgerollt.
- **Rückgängig:** stellt den Originalzustand wieder her und entfernt Logs und Caches der Mods.
- **Spiel-Updates:** „Reparieren“ erkennt ersetzte Dateien per Prüfsumme und installiert neu.
- **Archive:** werden mit Schutz gegen Pfade außerhalb des Zielordners („Zip Slip“) entpackt.
- **Anti-Cheat:** Installationen werden gesperrt, außer du schaltest das ausdrücklich frei.

## Aufbau

```
src/Dlss5Optimizer.Core     plattformunabhängig, voll getestet
  Detection/                PE-Parser, String-Scanner, Spielanalyse, Anti-Cheat
  Library/                  Steam (VDF), Epic, Ordner
  Decision/                 Routenkatalog, Frametime-Modell, Entscheidungs-Engine
  Components/               Katalog, Speicher mit SHA-256, GitHub-Downloader, Treiber-Suche
  Install/                  Routen-Planer, Installer mit Backup/Rollback, INI-Editor
  Benchmark/                PresentMon-CSV (1.x/2.x), Testlauf-Auswertung
  Data/                     games.json, components.json (ohne Neuübersetzung erweiterbar)
src/Dlss5Optimizer.App      WPF-Oberfläche (MVVM) + Windows-Anbindung
tests/Dlss5Optimizer.Core.Tests
```

```
dotnet test tests/Dlss5Optimizer.Core.Tests                    # läuft auch unter Linux
dotnet publish src/Dlss5Optimizer.App -c Release -o publish    # Single-File-EXE für Windows
```

## Bekannte Grenzen

- **Bisher nur unter Linux gebaut und getestet.** Die Windows-Teile (Registry, Modul-Liste,
  PresentMon, Vulkan-Layer) sind nach Dokumentation geschrieben, aber noch nicht auf einem echten
  Windows-PC gelaufen.
- **Messwerte:** Die Leistungswerte stammen aus wenigen Spielen und zum Teil aus Zweitquellen;
  die Vorhersagen sind vor dem ersten „Messen“ Schätzungen.
- **Community-Mods:** Sie ändern sich wöchentlich. Dateinamen und INI-Schlüssel stehen deshalb in
  `components.json` bzw. im Routen-Planer und müssen bei neuen Versionen ggf. nachgezogen werden.
- **Treiber-Presets:** DLSS-Preset und Frame-Generation-Stufe stellt das Tool (noch) nicht selbst
  ein. Es sagt dir, was du in der NVIDIA App bzw. im Spiel wählen sollst.

## Quellen

- NVIDIA: [DLSS 5](https://www.nvidia.com/en-us/geforce/news/dlss5-breakthrough-in-visual-fidelity-for-games/),
  [DLSS 4.5](https://www.nvidia.com/en-us/geforce/news/dlss-4-5-dynamic-multi-frame-gen-6x-2nd-gen-transformer-super-res/)
- Leistung:
  - [TechSpot](https://www.techspot.com/article/3170-real-dlss-5-performance/)
  - [Notebookcheck](https://www.notebookcheck.net/First-DLSS-5-game-tested-DLSS-5-humbles-RTX-5090-with-over-50-performance-loss.1391978.0.html)
  - [Wccftech (Pre-Upscale)](https://wccftech.com/dlss-5-performance-cost-cut-in-half-with-experimental-optiscaler-mod/)
  - [GameGPU (Modell-Skalierung)](https://en.gamegpu.com/news/igry/modifikatsiya-dlss-5-optiscaler-izbavilas-ot-utechek-vram-i-poluchila-gibkoe-masshtabirovanie)
- Eingaben von DLSS 5: [TechPowerUp](https://www.techpowerup.com/347585/nvidia-dlss-5-takes-2d-frame-and-motion-vectors-as-input)
- Projekte:
  - [OptiScaler DLSSNR](https://github.com/Dagherbou/OptiScaler_DLSSNR)
  - [PreSR-Fork](https://github.com/wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass)
  - [dlss5-bridge](https://github.com/NIGos/dlss5-bridge)
  - [DLSS5-Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder)
  - [LumeniteFX](https://github.com/umar-afzaal/LumeniteFX)
  - [dgVoodoo2](https://github.com/dege-diosg/dgVoodoo2)
  - [PresentMon](https://github.com/GameTechDev/PresentMon)
- Vergleichbare Tools: [DLSS5-Swapper](https://github.com/rakanki911/DLSS5-Swapper), [DLSS5oneclick](https://github.com/faisalkindi/DLSS5oneclick)
