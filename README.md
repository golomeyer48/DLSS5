# DLSS5 Optimizer

Windows-Tool, das DLSS 5 (Neural Rendering) in möglichst viele Spiele bringt – und dabei pro Spiel
automatisch die Variante mit der besten Kombination aus Bildqualität und Leistung wählt.

- erkennt, welche Grafik-API ein Spiel wirklich nutzt (DirectX 8–12, Vulkan, OpenGL)
- wählt die passende Einbindung (nativ, OptiScaler, ReShade-Add-on, dlss5-bridge, Feeder, DXVK, d3d8to9, dgVoodoo2)
- rechnet für jede Kombination aus Upscaling, DLSS-5-Modellauflösung und Frame Generation die zu
  erwartende Bildrate aus und nimmt die beste, die dein Ziel erreicht
- installiert mit Sicherung und stellt auf Knopfdruck den Originalzustand wieder her
- prüft nach dem ersten Start, ob alles greift, und stellt bei einem Absturz selbst auf den anderen Übersetzer um

> **Status:** Erste Version. Kern und Installer sind mit 131 Unit-Tests abgedeckt. Bei jedem Build läuft
> außerdem ein [Selbsttest](#selbsttest) auf einem Windows-Rechner: Er lädt alle Komponenten echt herunter,
> installiert jede Route in nachgebaute Spiele und nimmt sie wieder zurück. Was dort nicht geht, ist das
> eigentliche Rendern: DLSS 5 im laufenden Spiel ist nur auf einer echten RTX 50 prüfbar. Bitte zuerst mit
> einem Einzelspieler-Spiel ausprobieren.

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
| DX9 (z. B. Fallout 3/New Vegas) | **DXVK** → Vulkan → ReShade-Vulkan-Layer → Feeder | geschätzt | ~1,4 ms |
| DX8 | **d3d8to9** → DXVK → Vulkan → Feeder | geschätzt | ~1,5 ms |
| DirectDraw/DX7 (und DX8/9, wenn der erste Übersetzer scheitert) | **dgVoodoo2** → DX11 → Feeder | geschätzt | ~1,6 ms |
| Anti-Cheat | **gesperrt** (Bann-Risiko). Freischalten nur ausdrücklich und nur für offline | – | – |

32-Bit-Spiele bekommen einen 64-Bit-Hilfsprozess (`host64\`), weil DLSS nur als 64-Bit-Code existiert.

### Alte Spiele: Fallout 3 und Fallout: New Vegas

Für 32-Bit-DirectX-9-Spiele ist **DXVK der Standard**, nicht dgVoodoo. Die Gründe:
- dgVoodoo beendet Fallout 3 nach etwa einer Sekunde.
- In New Vegas blockiert dgVoodoo die Bildübergabe an den Feeder.
- Unter Vulkan kann ReShade Compute-Shader ausführen, die LumeniteFX für die Bewegungsvektoren braucht.

Der Aufbau folgt der getesteten Referenz aus
[dlss5-classic-games](https://github.com/perseval-BLR/dlss5-classic-games) (Fallout 3, dort bestätigt) und dem
Community-Mod FNV-DLSS5 (gleiche DXVK-Kette für New Vegas):

- `d3d9.dll` = DXVK (32 Bit). Eine vorhandene ENB- oder DXVK-`d3d9.dll` wird gesichert und ersetzt.
- ReShade als **32-Bit-Vulkan-Layer**, dazu die ReShade.ini **ohne** `LoadFromDllMain`. Sonst meldet das Add-on „No add-on was registered“ und wird entladen.
- `dlss5-feed.addon32` neben der EXE. `dlss5-feed.cfg` mit `mode=2`, weil `mode=1` nur ein Transporttest ist, und `reset_every=0`, `rebuild=0`, `warmup_rebuild=0`.
- `host64\`: Hilfsprozess, ReShade 64 Bit, RenoDX mit `NRStyle=0` (mit 2 bleibt das Bild schwarz) und `EnableHooks=2`, dazu die NVIDIA-DLLs.
- Getestete Tiefenpuffer-Werte (`RESHADE_DEPTH_INPUT_IS_REVERSED=1` …) und Kantenglättung aus in `FalloutPrefs.ini`. Die Datei wird nur geändert, wenn sie existiert, und gesichert.
- Ziel 60 fps, weil die Gamebryo-Engine darauf ausgelegt ist. Die Auswahl nimmt deshalb volle DLSS-5-Qualität statt unnötiger Einsparungen.
- Warnung, wenn die EXE kein Large-Address-Aware-Flag hat (4GB-Patch).
- „Spiel starten“ nimmt `nvse_loader.exe` bzw. `fose_loader.exe`, wenn vorhanden.

**Nach dem ersten Start „Diagnose“ klicken.** Das Tool liest die Logs von DXVK, ReShade, Feeder,
Hilfsprozess und DLSS 5 sowie das Windows-Absturzprotokoll. Es zeigt, an welcher Stelle die Kette
hängt, zum Beispiel:
- „Das Spiel lädt die System-d3d9.dll“
- „nur Transporttest“
- „0xbad00001 – Modell passt nicht“
- „renodx-dlss5.addon64 verschwunden“: Windows Defender entfernt DLSS-Add-ons manchmal, weil sie sich in NGX einhängen

**„Diagnose-Paket“** packt alles für eine Fehlersuche aus der Ferne in eine ZIP-Datei auf dem Desktop:
- Logs und Einstellungen der Mods, auch aus `host64\`
- eine Dateiliste mit Version, 32/64 Bit und Prüfsumme
- System, Diagnose, Absturzprotokoll und die geladenen Komponenten-Versionen

Spielstände sind nicht dabei, und der Benutzername wird in allen Pfaden ersetzt.

**Der ReShade-Vulkan-Layer** wird wie beim offiziellen ReShade-Setup nach `C:\ProgramData\ReShade` gelegt und
unter HKLM eingetragen. Es lädt immer nur eine ReShade-Instanz pro Spiel. Daraus folgt:
- Liegt dort eine Fassung ohne Add-on-Unterstützung, wird sie gesichert (`.bak`) und ersetzt.
- Ist zusätzlich ein fremder ReShade-Layer registriert, bietet das Tool an, ihn abzumelden.

Aktiv wird der Layer nur in Spielen mit `ReShade.ini` neben der EXE. So prüft es ReShade selbst beim Laden.

### Weitere Klassiker, DirectX 8 und Selbsthilfe

**Spiel-Datenbank.** Neben Fallout 3 und New Vegas kennt das Tool diese Spiele mit der Konfiguration,
die in [dlss5-classic-games](https://github.com/perseval-BLR/dlss5-classic-games) als laufend
bestätigt ist:

| Spiel | API | Route | Besonderheit |
|---|---|---|---|
| Black Mesa | DX9, 32 Bit | DXVK | `d3d9.dll` auch in `bin\`, dgVoodoo ausgeschlossen |
| Dark Messiah of Might and Magic | DX9, 32 Bit | DXVK | `d3d9.dll` auch in `bin\` |
| Far Cry (2004) | DX9 (auch OpenGL) | DXVK | Start über `Bin32\FarCry.exe`, nicht den größeren Editor |
| Need for Speed Underground / Most Wanted (2005) / Underground 2 | DX9, 32 Bit | DXVK | – |
| BloodRayne 2: Terminal Cut | DX8 mit eigener DX9-Brücke | DXVK | d3d8to9 und dgVoodoo ausgeschlossen |
| Deus Ex: Human Revolution | DX11, 32 Bit | Feeder | ohne Übersetzer |
| DOOM (2016) | Vulkan (auch OpenGL) | Feeder | nimmt die Vulkan-EXE |

Die Datenbank-Empfehlung schlägt eine nur geschätzt schnellere Route; eine Route, die bei dir schon
lief, schlägt beides.

**DirectX 8.** Reine DX8-Spiele laufen jetzt über [d3d8to9](https://github.com/crosire/d3d8to9)
(von crosire, dem ReShade-Autor) und dann über dieselbe DXVK-Kette wie DX9. dgVoodoo bleibt als
Ausweichroute. d3d8to9 braucht die alte **DirectX End-User Runtime** (`d3dx9_43.dll`); fehlt sie,
warnt das Tool vor der Installation.

**Automatischer Übersetzer-Wechsel.** „Diagnose“ liest das Windows-Absturzprotokoll mit. Stürzt das
Spiel in `d3d9.dll`, `d3d8.dll` oder `ddraw.dll` ab, ist der Übersetzer schuld. Das Tool bietet dann an,
auf den anderen umzustellen: DXVK ↔ dgVoodoo bzw. d3d8to9 ↔ dgVoodoo. Es nimmt die alte Installation
vollständig zurück und installiert die neue. Das Ergebnis wird pro Spiel gemerkt:
- Gescheiterte Routen werden nicht mehr vorgeschlagen.
- Eine Route, bei der die Diagnose alles grün meldet, wird bevorzugt.

„Testergebnisse vergessen“ setzt das zurück. Abstürze in anderen Modulen werden **nicht** dem
Übersetzer angelastet.

**Bewegungsvektoren und Tiefe prüfen.** Der Feeder schreibt nach 600 Bildern Stichproben ins Log.
Die Diagnose wertet sie aus:
- *Bewegungsvektoren:* Bleiben sie in zwei Proben bei (fast) 0 %, bekommt DLSS keine Bewegung. Meist ist
  Lumenite nicht aktiv oder steht nicht über `DLSS5_Feed`.
- *Tiefe:* Eine flache oder leere Tiefe heißt, ReShade liest den falschen Puffer.

**Tiefenpuffer-Assistent.** Stellt nacheinander die sechs üblichen Kombinationen ein:
- `DepthCopyBeforeClears` an/aus
- Tiefe umgekehrt ja/nein
- Bild auf dem Kopf ja/nein

Nach jedem Klick: Spiel neu starten, eine Minute spielen, „Diagnose“. Die gefundene Variante bleibt
auch nach „Reparieren“ erhalten. Andere Einträge in der ReShade.ini werden nicht angefasst.

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
| DXVK | zlib | GitHub-Releases |
| d3d8to9 | BSD-2 | GitHub-Releases (crosire/d3d8to9) |
| ReShade-Shader-Header (`ReShade.fxh` …) | BSD-3 | crosire/reshade-shaders (Zweig „slim“) |
| **RenoDX DLSS 5** | Closed Source | neueste **stabile** Version aus dem Community-Spiegel (RankFTW/rhi-repo) nach extra Warnung, oder Import |
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

## Selbsttest

`tools/Dlss5Optimizer.SelfTest` läuft bei jedem Build als eigener Job auf `windows-latest`. Das Ergebnis
steht in der Job-Zusammenfassung und im Artefakt `selftest-report`. Geprüft wird:

1. **Downloads:** Jede Komponente wird aus ihrer Originalquelle geladen und entpackt. Danach wird
   geprüft, ob die Dateien darin liegen, die der Routen-Planer braucht, in der richtigen Architektur.
   Ein Beispiel: `dlss5-feed.addon32` muss 32 Bit sein, `host64\dlss5-feed-host64.exe` 64 Bit.
2. **Installieren und Rückgängig:** Das läuft für 14 nachgebaute Spiele, von Fallout: New Vegas mit ENB
   über DX8, DirectDraw, 32-Bit-DX11 und OpenGL bis zu DX12 mit DLSS und Deep Fried Chicken. Geprüft wird:
   - jede Datei mit Prüfsumme und Architektur
   - jeder INI-Wert
   - die Pflichtwerte aus den Referenz-Setups, z. B. `mode=2`, `NRStyle=0`, `LoadFromDllMain` und die Reihenfolge Lumenite vor Feeder
   - dass eigene Einstellungen des Nutzers erhalten bleiben
   - nach „Rückgängig“ und nach „Reparieren“: der Spielordner muss **Byte für Byte** dem Ausgangszustand entsprechen
3. **Windows:**
   - Vulkan-Layer in `C:\ProgramData\ReShade` und in der Registry (32 und 64 Bit)
   - ein simulierter Absturz in `d3d9.dll` im Ereignisprotokoll: wird erkannt und führt zum Vorschlag dgVoodoo2
   - die Module eines laufenden 32-Bit-Prozesses werden gelesen
   - GPU, Anzeige und Spielbibliotheken werden abgefragt, ohne abzustürzen

Heruntergeladene Dateien werden danach gelöscht, nichts davon landet in Artefakten. Deep Fried Chicken
und das DLSS-5-Modell aus dem Treiber sind dabei Platzhalter. Ohne Internet: `--synthetic`.

Stand des letzten Laufs (28.09.2026): alle Prüfungen bestanden. Diese Versionen wurden geladen:

| Komponente | Version |
|---|---|
| ReShade | 6.8.0 |
| OptiScaler DLSSNR | 0.2.0 |
| PreSR-Fork | 0.8.91 |
| dlss5-bridge | 1.4.12 |
| DLSS5-Feeder | 1.17.0 |
| RenoDX | 6.5.3 (neueste stabile) |
| DXVK | 3.1.1 |
| d3d8to9 | 1.16.0 |
| dgVoodoo2 | 2.87.5 |
| PresentMon | 2.6.0 |

```
dotnet run --project tools/Dlss5Optimizer.SelfTest -f net10.0-windows            # echte Downloads
dotnet run --project tools/Dlss5Optimizer.SelfTest -f net10.0 -- --synthetic       # ohne Internet, auch Linux
```

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
src/Dlss5Optimizer.Windows  Windows-Anbindung ohne Oberfläche (Registry, Vulkan-Layer, Ereignisprotokoll, Module)
src/Dlss5Optimizer.App      WPF-Oberfläche (MVVM)
tools/Dlss5Optimizer.SelfTest  Selbsttest gegen echte Quellen und echtes Windows
tests/Dlss5Optimizer.Core.Tests
```

```
dotnet test tests/Dlss5Optimizer.Core.Tests                    # läuft auch unter Linux
dotnet publish src/Dlss5Optimizer.App -c Release -o publish    # Single-File-EXE für Windows
```

## Bekannte Grenzen

- **Noch nicht mit Grafikkarte getestet.** Die Windows-Teile laufen im Selbsttest auf einem echten
  Windows ohne GPU: Registry, Vulkan-Layer, Ereignisprotokoll und Modul-Liste. Die Oberfläche,
  PresentMon-Messungen und DLSS 5 im Spiel sind erst auf einer RTX 50 prüfbar.
- **Fallout 3/New Vegas:** Die Konfiguration stammt aus bestätigt laufenden Community-Aufbauten.
  Für New Vegas sind die Tiefenpuffer-Werte von Fallout 3 übernommen (gleiche Engine). Die
  Bewegungsvektoren sind geschätzt, deshalb sind Schlieren bei schnellen Drehungen möglich; HUD und
  Pip-Boy werden mitbearbeitet.
- **Weitere Klassiker:** Aus der Referenzliste fehlen bewusst:
  - *OpenGL-Spiele* (OpenMW, Quake III, Serious Sam, Jedi Academy, Riddick, DOOM 3 BFG): Sie brauchen
    die VORT-Shader für Bewegungsvektoren (Lumenite liefert unter OpenGL 0 %) und RenoDX 4.60. Beides
    lädt das Tool nicht; die Feeder-Route warnt davor.
  - *GRID* braucht laut Referenz eine Sonder-DXVK (2.7.1 „addon_fix“) für 4K.
  - *Half-Life 2, Mass Effect LE, Split/Second, NFS Shift/ProStreet* laufen über die allgemeine
    Erkennung (DX9 → DXVK bzw. DX11 → Feeder), haben aber keinen eigenen Eintrag mit geprüften
    Tiefenwerten.
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
- Alte Spiele:
  - [dlss5-classic-games](https://github.com/perseval-BLR/dlss5-classic-games) (u. a. `OLD-GAMES-GOTCHAS.en.md`)
  - [FNV-DLSS5](https://www.nexusmods.com/newvegas/mods/99411)
  - [DXVK](https://github.com/doitsujin/dxvk)
  - [d3d8to9](https://github.com/crosire/d3d8to9)
  - [reshade-shaders (slim)](https://github.com/crosire/reshade-shaders/tree/slim)
- Vergleichbare Tools:
  - [DLSS5-Swapper](https://github.com/rakanki911/DLSS5-Swapper)
  - [DLSS5oneclick](https://github.com/faisalkindi/DLSS5oneclick)
  - [dlss5-launcher](https://github.com/xdzleo/dlss5-launcher)
