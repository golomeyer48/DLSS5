using Dlss5Optimizer.Core.Detection;

namespace Dlss5Optimizer.Core.Install;

/// <summary>
/// ReShade als Vulkan-Layer so ablegen wie das offizielle ReShade-Setup: ReShade32/64.dll und .json in
/// einem gemeinsamen Ordner (C:\ProgramData\ReShade). Es kann pro Prozess nur eine ReShade-Instanz
/// laden – liegt dort eine Fassung ohne Add-on-Unterstützung, lädt der Feeder nie. Deshalb:
/// eine vorhandene Add-on-Fassung gleicher oder neuerer Version bleibt, alles andere wird gesichert
/// (.bak) und ersetzt.
/// </summary>
public static class ReShadeLayer
{
    public enum DeployAction
    {
        Installed,
        KeptExisting,
        ReplacedWithoutAddons,
        ReplacedOlder,
    }

    public sealed record DeployResult(string ManifestPath, string DllPath, DeployAction Action, Version? ExistingVersion)
    {
        public string Describe() => Action switch
        {
            DeployAction.Installed => $"ReShade-Layer nach {Path.GetDirectoryName(DllPath)} kopiert",
            DeployAction.KeptExisting => $"vorhandener ReShade-Layer {ExistingVersion} mit Add-on-Unterstützung bleibt",
            DeployAction.ReplacedWithoutAddons => $"vorhandener ReShade-Layer {ExistingVersion} ohne Add-on-Unterstützung ersetzt (Sicherung: .bak)",
            _ => $"vorhandener ReShade-Layer {ExistingVersion} ist zu alt, ersetzt (Sicherung: .bak)",
        };
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReShade");

    public static bool HasAddonSupport(PeFile pe) =>
        pe.Exports.Contains("ReShadeRegisterAddon") && pe.Exports.Contains("ReShadeUnregisterAddon");

    /// <param name="storeManifest">ReShade32.json bzw. ReShade64.json aus dem Komponentenspeicher (DLL daneben).</param>
    public static DeployResult Deploy(string storeManifest, bool is32Bit, string targetDir)
    {
        var name = is32Bit ? "ReShade32" : "ReShade64";
        var srcDll = Path.Combine(Path.GetDirectoryName(storeManifest)!, name + ".dll");
        if (!File.Exists(storeManifest) || !File.Exists(srcDll))
            throw new FileNotFoundException($"{name}.json/.dll fehlen im Komponentenspeicher.", srcDll);

        Directory.CreateDirectory(targetDir);
        var dstDll = Path.Combine(targetDir, name + ".dll");
        var dstJson = Path.Combine(targetDir, name + ".json");

        var existing = File.Exists(dstDll) ? PeFile.TryRead(dstDll) : null;
        var ours = PeFile.TryRead(srcDll);
        DeployAction action;
        if (existing is null)
            action = DeployAction.Installed;
        else if (!HasAddonSupport(existing))
            action = DeployAction.ReplacedWithoutAddons;
        else if (existing.FileVersion is { } have && ours?.FileVersion is { } want && have < want)
            action = DeployAction.ReplacedOlder;
        else
            action = DeployAction.KeptExisting;

        if (action != DeployAction.KeptExisting)
        {
            if (File.Exists(dstDll))
                File.Copy(dstDll, dstDll + ".bak", overwrite: true);
            File.Copy(srcDll, dstDll, overwrite: true);
            File.Copy(storeManifest, dstJson, overwrite: true);
        }
        else if (!File.Exists(dstJson))
        {
            File.Copy(storeManifest, dstJson);
        }
        return new DeployResult(dstJson, dstDll, action, existing?.FileVersion);
    }
}
