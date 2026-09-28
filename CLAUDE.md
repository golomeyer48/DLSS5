# DLSS5 Optimizer – Hinweise für Claude Code

Windows-Tool (WPF, .NET 10), das DLSS 5 (Neural Rendering) in Spiele bringt und pro Spiel die beste
Einbindung wählt. Nutzer: RTX 5070 Ti, Treiber 617.14, 4K/60 Hz, spricht Deutsch – **Oberfläche,
Kommentare, Commit-Texte und Antworten auf Deutsch**.

## Befehle

```powershell
dotnet test tests\Dlss5Optimizer.Core.Tests                     # Kern-Tests (auch unter Linux)
dotnet build DLSS5Optimizer.slnx -c Release                      # alles bauen
.\build.bat                                                     # Tests + Single-File-EXE nach publish\
dotnet run --project tools\Dlss5Optimizer.SelfTest -f net10.0-windows            # echte Downloads + Installieren/Rückgängig
dotnet run --project tools\Dlss5Optimizer.SelfTest -f net10.0 -- --synthetic      # ohne Internet
publish\DLSS5Optimizer.exe --screenshot .\screenshots --folder D:\Games           # alle Reiter als PNG, beendet sich
```

Die App verlangt Adminrechte (app.manifest). Daten und Log: `%LOCALAPPDATA%\DLSS5Optimizer`
(`dlss5optimizer.log`, `settings.json`, `components\`).

## Aufbau

- `src/Dlss5Optimizer.Core` – plattformunabhängig, voll getestet: Erkennung (PE-Parser, API-Bewertung,
  Spiel-Datenbank `Data/games.json`), Entscheidung (`RouteCatalog`, `DecisionEngine`, `FrameTimeModel`),
  Komponenten (`Data/components.json`, Speicher mit SHA-256, GitHub-Downloader), Installation
  (`RoutePlanner` → Schritte, `Installer` mit Sicherung/Rollback, `InstallDiagnostics`, `DiagnosticBundle`).
- `src/Dlss5Optimizer.Windows` – Windows ohne Oberfläche: Registry, Vulkan-Layer, Ereignisprotokoll, Module.
- `src/Dlss5Optimizer.App` – WPF/MVVM (CommunityToolkit.Mvvm). Farben/Stile in `App.xaml`.
- `tools/Dlss5Optimizer.SelfTest` – läuft in der CI auf windows-latest (siehe `.github/workflows/build.yml`).

## Regeln

- **Keine fremden Binärdateien ins Repository.** Komponenten lädt das Tool beim Nutzer aus der
  Originalquelle; Closed-Source-Teile nur per Import oder nach ausdrücklicher Warnung.
- **DLSS-5-Modell:** `nvngx_dlssnr.dll` liegt weder im Treiber noch im öffentlichen SDK. Angenommen wird nur
  NVIDIAs signierte Fassung 310.8.0 (SHA-256 in `components.json` → `pinnedSha256`); veränderte Builds
  (Lecram, SF, RTX40) werden abgelehnt. Das Tool lädt sie selbst aus `RankFTW/rhi-repo`, Release `dlssnr-310.8.0`
  (Entscheidung des Nutzers, 28.09.2026); Import geht weiterhin.
- Jede Route außer „nativ“ muss das Modell als Voraussetzung führen (Test `EveryDlss5RouteNeedsTheModel`).
- Neue Logik im Kern mit xUnit-Test; Windows-Teile im Selbsttest abdecken.
- Oberfläche: Kontrast mindestens 4,5:1. Keine globale TextBlock-Farbe – Schrift erbt vom Steuerelement.
  `MainWindow` nutzt den Stil `AppWindow` per Schlüssel (implizite Stile greifen nur beim exakten Typ).

## Fachliches, das schon geklärt ist

- ReShade als Vulkan-Layer (DXVK-Route) lädt nur in Spielen mit `ReShade.ini` neben der EXE; abgelegt wie das
  offizielle Setup in `C:\ProgramData\ReShade`. Pro Prozess lädt nur eine ReShade-Instanz – eine Fassung ohne
  Add-on-Unterstützung wird ersetzt, fremde Layer werden auf Wunsch abgemeldet.
- 32-Bit-Spiele: Feeder `addon32` + 64-Bit-Hilfsprozess in `host64\` (RenoDX dort mit `NRStyle=0`,
  `EnableHooks=2`, früh geladen). `dlss5-feed.cfg`: `mode=2`, `reset_every=0`, `rebuild=0`, `warmup_rebuild=0`.
- Ab Treiber 616.64 nur RenoDX ≥ 6.1 (neueste stabile: 6.5.3) oder Deep Fried Chicken.
- Referenzen: `jlrouzies-fr/DLSS5-Feeder` (v. a. `tools/Install-DLSS5Feeder.ps1` und README),
  `perseval-BLR/dlss5-classic-games` (bestätigte Klassiker), `xdzleo/dlss5-launcher`.

## Stand (28.09.2026)

- Erster echter Test (Fallout 3 GOTY, Treiber 617.14) scheiterte an „Es fehlen Komponenten: nvngx-dlssnr“ –
  behoben: Das Modell ist jetzt Voraussetzung jeder Route und wird importiert (siehe oben).
- Oberfläche war kaum lesbar (weißes Fenster, hellgraue Schrift) – behoben; die CI speichert Bildschirmfotos.
- Ordner `DLSS5Patcher/` (MRHRTZ „DLSS 5 Patcher“ 1.2.1, nur lokal, per .gitignore ausgeschlossen) geprüft:
  Sein `nvngx_dlssnr.dll` (SHA-256 8270b350…) ist NVIDIA-signiert, aber verändert (Authenticode: HashMismatch) –
  wird abgelehnt und in `components.json` unter `knownModifiedSha256` mit Herkunft benannt. Brauchbar daraus:
  Deep Fried Chicken 1.4.8-alpha (Import), `nvngx_dlss.dll` 310.8.0 (gültig signiert); der Rest kommt aus den Originalquellen. Beide sind beim Nutzer in den Komponentenspeicher importiert (28.09.2026) –
  damit nimmt `ChooseConsumer` jetzt Deep Fried Chicken statt RenoDX 6.5.3.
- Quelle der signierten Fassung gefunden: GitHub `RankFTW/rhi-repo`, Release `dlssnr-310.8.0`,
  `nvngx_dlssnr_310.8.0.zip` (ZIP-SHA-256 388c0a79…; DLL = e16bcf15…, Authenticode gültig, NVIDIA). Beim Nutzer
  importiert (28.09.2026). Die übrigen `dlssnr-*`-Releases dort (Lecram, SF, SF-v2, RTX40) sind veränderte Builds.
  Nicht im Treiber 617.14 enthalten (DriverStore/NGXCore geprüft).
- Zweiter Test Fallout 3 (28.09.2026, 17:05, alles installiert inkl. signiertem Modell, RenoDX 6.5.3): Absturz
  0xC0000005 beim ersten Bild, Sprung an eine Adresse ohne Modul, oberster Aufrufer laut `dlss5-feed-crash.dmp`
  `GameOverlayRenderer.dll` (Steam-Overlay) – wie Feeder-Issue #95; der Feeder hatte noch nichts getan.
  Umgesetzt: `MiniDump` (liest Feeder-Abbilder), Diagnose nennt Overlay-Abstürze statt „Tiefenpuffer prüfen“,
  DXVK-Log auch von `Fallout3ng.exe`, Installationshinweis „Steam-Overlay aus“ für Vulkan-Layer-Routen.
- Dritter Test (17:19, Steam-Overlay aus): gleicher Absturz (EIP 0x9, kein Aufrufer im Modul) direkt nach dem Start
  der ReShade-Laufzeit. Das Overlay war also nur Beteiligter. `GameOverlayRenderer.dll` sowie die impliziten
  Vulkan-Layer `VK_LAYER_VALVE_steam_overlay`/`_fossilize` laden trotzdem (Steam setzt `ENABLE_VK_LAYER_VALVE_*`).
  Abweichungen zum bestätigten Aufbau (perseval: „Fallout 3 ReBuild“ ohne Steam, DXVK 3.0.2, Feeder ≤ 0.13.1-beta.1,
  lokale `vulkan-1.dll` x86). Umgesetzt: DXVK fest auf 3.0.2, `dxvk.conf` mit `dxvk.enableDescriptorHeap = False`.
- Vierter Test (17:43, DXVK 3.0.2 + dxvk.conf, Feeder 0.13.1-beta.1 von Hand): gleicher Absturz (0x65470650) –
  Feeder- und DXVK-Fassung sind es nicht.
- **Durchbruch (17:50):** `Fallout3ng.exe` per Doppelklick (Steam läuft, startet das Spiel aber nicht) → DLSS 5 läuft:
  RenoDX 6.5.3 „signed runtime … reference match“, „feature 18 created“, „inline feature 18 evaluation succeeded“,
  Hilfsprozess 21 600 Bilder „evaluated“ (DLSS-GPU 17,4 ms), „copy home“ ins Backbuffer, ~48 fps in 4K nativ.
  Ursache also: Start über Steam (GameOverlayRenderer.dll + Steams Vulkan-Layer, auch mit Overlay aus).
  Umgesetzt: `LaunchChooser` (Steam + Vulkan-Layer-Route → Direktstart; `directExe` in games.json, Fallout 3:
  Fallout3ng.exe; auch fürs Messen und die Diagnose), Hinweise/Diagnose-Texte „nicht über Steam starten“.
- **Fallout 3 läuft (bestätigt 28.09.2026, 18:16–19:35):** nach „Reparieren“ mit Feeder 1.17.0, RenoDX 6.5.3,
  DXVK 3.0.2 und „Spiel starten“ im Tool (Direktstart Fallout3ng.exe) – 223 200 Bilder in 4K mit DLSS 5, kein Absturz,
  ~47 fps (DLSS-GPU 17,4 ms/Bild). Der Feeder bleibt also auf der neuesten Fassung.
- **Fallout: New Vegas läuft (28.09.2026, 19:49–20:05):** vorher Patcher-Reste aus dem Spielordner nach
  `%LOCALAPPDATA%\DLSS5Optimizer\patcher-reste\Fallout New Vegas` verschoben (dgVoodoo-d3d9.dll, ReShade32 als dxgi.dll,
  fremde ReShade.ini, host64 mit dem veränderten Modell 8270b350…). Dann Installation mit dem Tool, Direktstart
  FalloutNV.exe: 39 600 Bilder mit DLSS 5, ~44–48 fps, kein Absturz. Das Spiel schrieb beim Beenden iMultiSample=8
  zurück → `Installer.ReapplyIniTweaks` setzt die Spieleinstellungen vor jedem „Spiel starten“ neu.
- **Idee:** Reste des DLSS 5 Patchers (Ordner `.dlss5_backup`) erkennen und wegräumen – laut Patcher-Log u. a. in
  Cyberpunk, RDR2, Wolfenstein II, Skyrim SE, Watch Dogs, Resident Evil 2.
- **Entschieden: A.** Das Tool lädt das Modell selbst aus rhi-repo, Release `dlssnr-310.8.0` (festes Tag, direkt
  abgefragt – `source.tag` in components.json, ebenso DXVK `v3.0.2`); angenommen nur mit der hinterlegten Prüfsumme.
  Import geht weiterhin.
