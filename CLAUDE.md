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
  (Lecram, SF, RTX40) werden abgelehnt. Derzeit nur Import – ob das Tool sie selbst lädt, entscheidet der Nutzer.
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
- **Offen:** Ergebnis des nächsten Tests mit Fallout 3 (erst Modell importieren, dann installieren, spielen,
  „Diagnose“; bei Problemen „Diagnose-Paket“). Entscheidung A (Tool lädt das signierte Modell selbst) oder
  B (nur Import, aktueller Stand).
