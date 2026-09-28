# DLSS5 Optimizer – Hinweise für Claude Code

Windows-Tool (WPF, .NET 10), das DLSS 5 (Neural Rendering) in Spiele bringt und pro Spiel die beste
Einbindung wählt. Nutzer: RTX 5070 Ti, Treiber 617.14, Hisense-TV 3840×2160 @ 60 Hz (einziger Bildschirm;
die Radeon 890M im Prozessor hat keinen), spricht Deutsch – **Oberfläche, Kommentare, Commit-Texte und
Antworten auf Deutsch**.

## Befehle

```powershell
dotnet test tests\Dlss5Optimizer.Core.Tests                     # Kern-Tests (auch unter Linux)
dotnet build DLSS5Optimizer.slnx -c Release                      # alles bauen
.\build.bat                                                     # Tests + Single-File-EXE nach publish\ (endet mit pause)
dotnet publish src\Dlss5Optimizer.App -c Release -o publish      # nur die EXE – geht nur, wenn das Tool geschlossen ist
dotnet run --project tools\Dlss5Optimizer.SelfTest -f net10.0-windows            # echte Downloads + Installieren/Rückgängig
dotnet run --project tools\Dlss5Optimizer.SelfTest -f net10.0 -- --synthetic      # ohne Internet
publish\DLSS5Optimizer.exe --screenshot .\screenshots --folder D:\Games           # alle Reiter als PNG, beendet sich
```

Die App verlangt Adminrechte (app.manifest). Daten und Log: `%LOCALAPPDATA%\DLSS5Optimizer`
(`dlss5optimizer.log`, `settings.json`, `components\`, `patcher-reste\`).

## Aufbau

- `src/Dlss5Optimizer.Core` – plattformunabhängig, voll getestet: Erkennung (PE-Parser, API-Bewertung,
  Spiel-Datenbank `Data/games.json`), Entscheidung (`RouteCatalog`, `DecisionEngine`, `FrameTimeModel`),
  Komponenten (`Data/components.json`, Speicher mit SHA-256, GitHub-Downloader), Installation
  (`RoutePlanner` → Schritte, `Installer` mit Sicherung/Rollback, `InstallDiagnostics`, `MiniDump`,
  `LaunchChooser`, `CommandLineFile`, `DiagnosticBundle`).
- `src/Dlss5Optimizer.Windows` – Windows ohne Oberfläche: Registry, Vulkan-Layer, Ereignisprotokoll, Module, Spielstart.
- `src/Dlss5Optimizer.App` – WPF/MVVM (CommunityToolkit.Mvvm). Farben/Stile in `App.xaml`.
- `tools/Dlss5Optimizer.SelfTest` – läuft in der CI auf windows-latest (siehe `.github/workflows/build.yml`),
  lädt dort auch das Modell samt Prüfsumme.

## Regeln

- **Keine fremden Binärdateien ins Repository.** Komponenten lädt das Tool beim Nutzer aus der
  Originalquelle; Closed-Source-Teile nur per Import oder nach ausdrücklicher Warnung. `DLSS5Patcher/` und
  `DLSS5-Diagnose-*.zip` sind per .gitignore ausgeschlossen – nie einchecken.
- **DLSS-5-Modell:** `nvngx_dlssnr.dll` liegt weder im Treiber noch im öffentlichen SDK. Angenommen wird nur
  NVIDIAs signierte Fassung 310.8.0 (SHA-256 e16bcf15… in `components.json` → `pinnedSha256`). Das Tool lädt sie
  aus `RankFTW/rhi-repo`, Release `dlssnr-310.8.0` (festes `source.tag`; Entscheidung des Nutzers). Alle anderen
  `dlssnr-*`-Releases dort (Lecram, SF, SF-v2, RTX40) und die Datei des „DLSS 5 Patchers“ (8270b350…,
  `knownModifiedSha256`) sind veränderte Builds und werden abgelehnt.
- Jede Route außer „nativ“ muss das Modell als Voraussetzung führen (Test `EveryDlss5RouteNeedsTheModel`).
- Neue Logik im Kern mit xUnit-Test; Windows-Teile im Selbsttest abdecken.
- Oberfläche: Kontrast mindestens 4,5:1. Keine globale TextBlock-Farbe – Schrift erbt vom Steuerelement.
  `MainWindow` nutzt den Stil `AppWindow` per Schlüssel (implizite Stile greifen nur beim exakten Typ).
- **Commit und Push nur nach ausdrücklichem Ja des Nutzers** (einmal ohne Frage gepusht – das soll nicht wieder
  passieren). Git hat keinen globalen Autor: wie bisher `git -c user.name=Claude -c user.email=noreply@anthropic.com
  commit -F <datei>` (Nachricht als Datei – PowerShell reicht Here-Strings nicht per stdin durch). Push nach
  `origin` (golomeyer48/DLSS5) funktioniert, die Anmeldung ist im Credential Manager gespeichert.

## Fachliches, das schon geklärt ist

- ReShade als Vulkan-Layer (DXVK-Route) lädt nur in Spielen mit `ReShade.ini` neben der EXE; abgelegt wie das
  offizielle Setup in `C:\ProgramData\ReShade`. Pro Prozess lädt nur eine ReShade-Instanz – eine Fassung ohne
  Add-on-Unterstützung wird ersetzt, fremde Layer werden auf Wunsch abgemeldet.
- **Steam-Spiele auf Vulkan-Layer-Routen nie über Steam starten.** Steam lädt `GameOverlayRenderer.dll` und seine
  Vulkan-Layer (`ENABLE_VK_LAYER_VALVE_*`) auch bei ausgeschaltetem Overlay; Fallout 3 stürzte so viermal beim
  ersten Bild ab. `LaunchChooser` startet direkt (games.json `directExe`, z. B. Fallout3ng.exe); Steam darf laufen.
- 32-Bit-Spiele: Feeder `addon32` + 64-Bit-Hilfsprozess in `host64\` (RenoDX dort mit `NRStyle=0` – 2 ergibt
  Schwarzbild –, `EnableHooks=2`, früh geladen). `dlss5-feed.cfg`: `mode=2`, `reset_every=0`, `rebuild=0`, `warmup_rebuild=0`.
- Ab Treiber 616.64 nur RenoDX ≥ 6.1 (neueste stabile: 6.5.3) oder Deep Fried Chicken (1.4.8-alpha, importiert).
- **RenoDX 6.5.3 stürzt direkt in 64-Bit-OpenGL-Spielen ab** (liest ReShades OpenGL-Gerätekennung 0x20000 als
  Zeiger; Wolfenstein: The Old Blood). Im 64-Bit-OpenGL-Weg also Deep Fried Chicken – Test läuft (siehe unten).
- Modellauflösung: RenoDX `NRResolutionScale` in `[RenoDX.DLSS5]` wirkt (0.5 → „ws1 1920x1080 -> 3840x2160“).
  Feeder-Wege kosten ≈ 2,2–2,4 ms/MP (4K bei 100 %: 17–23 ms) → `NrCostFactor` 2,7 in `RouteCatalog`. Deep Fried
  Chicken kennt keine Modellauflösung – bei < 100 % nimmt der Planer immer RenoDX.
- „DLSS-5-Stärke“ (Einstellung): „kräftig“ = NRPreset 3, NRIntensity/GlobalTone/LocalTone/LocalStructure 2,
  NRSkinStructure 1, NRUICorrection 1 (Satz aus dlss5-classic-games ohne NRStyle=2). Kostet keine Leistung.
- DXVK fest auf 3.0.2 (bestätigter Stand), dazu `dxvk.conf` mit `dxvk.enableDescriptorHeap = False`.
- Streamline-Spiele (Alan Wake 2) importieren nur `sl.interposer.dll` – API-Erkennung über den String `D3D12CreateDevice`.
- Fremde ReShade-Add-ons im Spielordner lädt ReShade mit (ein altes `AutoHDR.addon32` ließ Arkham Asylum abstürzen)
  → `SetAsideForeignAddons` legt sie beim Installieren gesichert beiseite.
- Viele Spielordner des Nutzers enthalten **Reste des „DLSS 5 Patchers“** (MRHRTZ, Ordner `.dlss5_backup`, oft mit
  dem veränderten Modell, ReShade als dxgi.dll, alten RenoDX-/Feeder-Add-ons). Vor dem Installieren prüfen und nach
  `%LOCALAPPDATA%\DLSS5Optimizer\patcher-reste\<Spiel>` **verschieben, nie löschen**. `preexisting.txt` im
  `.dlss5_backup` nennt, was vor dem Patcher da war.
- Referenzen: `jlrouzies-fr/DLSS5-Feeder` (README, Issues #95 Steam-Overlay, #135 New Vegas),
  `perseval-BLR/dlss5-classic-games` (bestätigte Klassiker, Skripte unter `scripts/`), `xdzleo/dlss5-launcher`,
  Gillian's GTA IV Guide (commandline.txt), DXVK-Issue #1831.

## Fehlersuche – so ging es bisher am schnellsten

- Der Nutzer spielt, Claude liest die Logs **selbst im Spielordner** (kein Diagnose-Paket nötig):
  `dlss5-feed.log` (Feeder, schreibt bei Absturz „EXCEPTION/CRASH RECORDED“ + `dlss5-feed-crash.dmp` + Stack nach
  Modulen), `ReShade.log`, `<exe>_d3d9.log` (DXVK), `host64\dlss5-feed-host.log` („frame N evaluated … DLSS GPU x ms“),
  `host64\ReShade.log` (RenoDX: „reference match“, „feature 18 created“, „evaluation succeeded“, `telemetry … fps=`),
  `deep-fried-chicken.log` („evaluate #N … Success“), `OptiScaler.log`. UE3-Spiele: `Documents\<Publisher>\…\Logs\Launch.log`
  (enthält den Absturz-Stack mit Modulnamen). Windows: Ereignis 1000/1001 im Anwendungsprotokoll.
- Eingrenzen durch Umbenennen (`*.test-aus`): erst `d3d9.dll` (ganze Kette aus), dann `ReShade.ini` (Layer aus),
  dann das Feeder-Add-on. Danach zurückbenennen.
- Kurze Auswertungsskripte als dateibasierte .NET-Programme im Scratchpad (`#:project …Core.csproj` und
  **`#:property PublishAot=false`**, sonst scheitert System.Text.Json).
- Handänderungen in Spielordnern überleben kein „Reparieren“, wenn sie dem gespeicherten Manifest widersprechen –
  dem Nutzer das jeweils sagen.

## Stand (28.09.2026, abends) – Übergabe

### Spiele beim Nutzer

| Spiel | Weg | Stand |
|---|---|---|
| Fallout 3 GOTY (Steam) | DXVK + Feeder + RenoDX, Direktstart Fallout3ng.exe | läuft, 223 200 Bilder, ~47 fps (Modell 100 %) |
| Fallout: New Vegas (Steam) | wie FO3, Direktstart FalloutNV.exe | läuft, ~44–48 fps (Modell 100 %) |
| Ultra Street Fighter IV | DXVK + Feeder + RenoDX | läuft mit 60 fps bei `NRResolutionScale=0.5` – **von Hand**, Manifest sagt noch 100 % |
| GTA IV Complete Edition | DXVK + Feeder + RenoDX, Modell 50 % | läuft, 4K 60 fps; commandline.txt und kräftige Regler **von Hand** gesetzt, Nutzer zufrieden |
| Batman: Arkham Asylum GOTY | DXVK + Feeder + RenoDX | Absturzursache (fremdes AutoHDR.addon32) beseitigt – **Test mit ganzer Kette steht aus** |
| Wolfenstein: The Old Blood | Feeder (64-Bit OpenGL) | stürzte in RenoDX ab; **von Hand auf Deep Fried Chicken getauscht, Test steht aus** |
| Assassin's Creed Black Flag Resynced | ReShade + DLSS-5-Add-on (DX12) mit Deep Fried Chicken | läuft: feature 18, > 6000 Auswertungen „Success“, echte Tiefe/Vektoren, DLSS Qualität |
| Alan Wake 2 | OptiScaler + DLSS 5 (DX12) empfohlen | Erkennung behoben, Patcher-Reste entfernt – **DLSS 5 noch nicht installiert** (Nutzer startete ohne Installation) |

### Handänderungen beim Nutzer (nicht im Tool-Zustand)

- USF4 `host64\ReShade.ini`: `NRResolutionScale=0.5` (Sicherung `.vor-aufloesungstest`). „Reparieren“ würde 1 schreiben
  → stattdessen „Rückgängig“ + „Installieren“ mit der neuen EXE (empfiehlt dann selbst 50 %).
- GTA IV: `GTAIV\commandline.txt` angelegt; `host64\ReShade.ini` kräftige Regler (Sicherung `.vor-staerketest`).
  Beides kann die neue EXE jetzt selbst (Stärke „kräftig“ wählen, dann Reparieren).
- Old Blood: `renodx-dlss5.addon64.test-aus`, Deep-Fried-Chicken-Dateien kopiert, `ReShade.ini` mit
  `LoadFromDllMain=deep-fried-chicken.addon64` (Sicherung `ReShade.ini.vor-dfc-test`).
- Verschoben nach `patcher-reste\`: Fallout New Vegas, Alan Wake 2, Batman Arkham Asylum GOTY.

### Offene Punkte (in dieser Reihenfolge)

1. Alles bis hier ist committet (Modellauflösung über RenoDX + `NrCostFactor`, USF4-/GTA-IV-/Alan-Wake-2-Einträge,
   `CommandLineFile`, „DLSS-5-Stärke“, Streamline-Erkennung, `SetAsideForeignAddons`); `publish\DLSS5Optimizer.exe`
   vom 28.09., 22:18 enthält es. Ob gepusht wurde: `git status -sb` prüfen.
2. Nutzer auf die neue Einstellung „DLSS-5-Stärke“ hinweisen (neben dem Optimierungsziel).
3. Old-Blood-Test auswerten. Wenn Deep Fried Chicken läuft: Planer im 64-Bit-OpenGL-Weg nie RenoDX, und dort
   keine Modellauflösung < 100 % anbieten (`RouteCatalog` Feeder `ModelScaleApis` ohne OpenGL) – mit Test.
4. Arkham-Asylum-Test mit ganzer Kette auswerten.
5. Alan Wake 2: Nutzer „DLSS 5 installieren“ klicken lassen, dann testen – zuerst ohne Pathtracing/Ray
   Reconstruction (unklar, ob OptiScaler-NR bei DLSS-RR greift). Danach Stärke über OptiScaler.ini `[DlssNr]` erhöhen
   (Nutzer wünscht mehr Intensität) – die Schlüssel dort vorher im installierten OptiScaler.ini nachlesen.
6. Stärke-Einstellung auch für Deep Fried Chicken (Regler in `deep-fried-chicken.cfg`) und OptiScaler anbieten.
7. Patcher-Reste automatisch erkennen (`.dlss5_backup`) und das Verschieben im Tool anbieten; laut Patcher-Log
   betroffen u. a. Cyberpunk, RDR2, Wolfenstein II, Skyrim SE, Watch Dogs, Resident Evil 2.
8. FO3/FNV laufen mit 100 % bei ~47 fps; das neue Kostenmodell empfiehlt bei Neuinstallation < 100 % für 60 fps.
   Nutzer fragen, ob ihm 60 fps oder volle Auflösung lieber ist.
