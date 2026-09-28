using System.Net.Http;
using Dlss5Optimizer.App.Platform;
using Dlss5Optimizer.Core;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Install;
using Dlss5Optimizer.Core.Library;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.App.Services;

/// <summary>Verdrahtet alle Dienste. Daten liegen in %LOCALAPPDATA%\DLSS5Optimizer.</summary>
public sealed class AppServices
{
    private string? _dlssNrModel;
    private bool _dlssNrModelSearched;
    private readonly List<GameAnalysis> _analyses = [];

    public string DataDir { get; }
    public string SettingsPath => Path.Combine(DataDir, "settings.json");
    public string LogPath => Path.Combine(DataDir, "dlss5optimizer.log");
    public AppSettings Settings { get; }
    public ComponentCatalog Catalog { get; }
    public ComponentStore Store { get; }
    public ComponentDownloader Downloader { get; }
    public GameDatabase Database { get; }
    public GameAnalyzer Analyzer { get; }
    public Installer Installer { get; }
    public ComponentAvailability Availability { get; }
    public RoutePlanner Planner { get; }
    public SystemInfo System { get; private set; }

    public AppServices()
    {
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Optimizer");
        Directory.CreateDirectory(DataDir);
        Settings = AppSettings.Load(SettingsPath);
        Catalog = ComponentCatalog.LoadEmbedded();
        Store = new ComponentStore(Path.Combine(DataDir, "components"), Catalog);
        Downloader = new ComponentDownloader(ComponentDownloader.CreateHttpClient(), Store, Catalog);
        Database = GameDatabase.LoadWithOverride(Path.Combine(DataDir, "games.json"));
        Analyzer = new GameAnalyzer(Database);
        Installer = new Installer(VulkanLayer.Register);
        Availability = new ComponentAvailability(Catalog, Store, DlssNrModel, DlssRuntime);
        Planner = new RoutePlanner(Availability, Store);
        System = WindowsSystem.Read();
    }

    public DecisionEngine Engine => new(Availability.IsAvailable, Availability.CanAutoDownload);

    public void RefreshSystem()
    {
        System = WindowsSystem.Read();
        _dlssNrModelSearched = false;
    }

    public string? DlssNrModel()
    {
        if (!_dlssNrModelSearched)
        {
            _dlssNrModel = NvidiaPaths.FindDlssNrModel();
            _dlssNrModelSearched = true;
        }
        return _dlssNrModel;
    }

    private string? DlssRuntime()
    {
        lock (_analyses)
            return SystemFileLocator.FindNewestDlssRuntime(_analyses)?.Path;
    }

    public void SetAnalyses(IEnumerable<GameAnalysis> analyses)
    {
        lock (_analyses)
        {
            _analyses.Clear();
            _analyses.AddRange(analyses);
        }
    }

    public IEnumerable<ILibraryScanner> Scanners()
    {
        if (WindowsSystem.SteamRoot() is { } steam)
            yield return new SteamLibraryScanner(steam);
        yield return new EpicLibraryScanner(WindowsSystem.EpicManifests());
        yield return new GogLibraryScanner();
        var xbox = WindowsSystem.XboxFolders().ToList();
        if (xbox.Count > 0)
            yield return new FolderLibraryScanner(xbox, GameSource.Xbox);
        if (Settings.ManualFolders.Count > 0)
            yield return new FolderLibraryScanner(Settings.ManualFolders);
    }

    public string? PresentMonExe() =>
        Settings.PresentMonPath is { } custom && File.Exists(custom)
            ? custom
            : Store.FindFile(RouteCatalog.Ids.PresentMon, "PresentMon*.exe");

    public void SaveSettings() => Settings.Save(SettingsPath);

    public void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
