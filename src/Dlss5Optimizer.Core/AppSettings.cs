using System.Text.Json;
using System.Text.Json.Serialization;
using Dlss5Optimizer.Core.Decision;
using Dlss5Optimizer.Core.Models;

namespace Dlss5Optimizer.Core;

/// <summary>Persistente Einstellungen und Messwerte (JSON in %LOCALAPPDATA%\DLSS5Optimizer).</summary>
public sealed class AppSettings
{
    public List<string> ManualFolders { get; set; } = [];
    public UserPreferences Preferences { get; set; } = new();

    /// <summary>Pro Spiel (GameInfo.Key) und API die gemessene Basis.</summary>
    public Dictionary<string, Dictionary<GraphicsApi, Calibration>> Calibrations { get; set; } = [];

    /// <summary>Pro Spiel die im Testlauf ermittelte API.</summary>
    public Dictionary<string, GraphicsApi> ProbedApis { get; set; } = [];

    /// <summary>Optional: eigener Pfad zu PresentMon.exe.</summary>
    public string? PresentMonPath { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Beschädigte Einstellungen: mit Standardwerten weiter, die Datei wird beim Speichern ersetzt.
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
