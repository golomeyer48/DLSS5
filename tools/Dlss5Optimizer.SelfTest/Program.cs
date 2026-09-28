using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.SelfTest;

// Aufruf: dlss5-selftest [--store <Ordner>] [--report <datei.md>] [--skip-download] [--synthetic]
//   --store          Komponentenspeicher (Standard: Temp-Ordner, wird danach gelöscht)
//   --skip-download  vorhandenen Speicher benutzen (z. B. nach einem ersten Lauf)
//   --synthetic      ohne Internet: nachgebaute Pakete statt echter Downloads
//   --report         Ergebnis zusätzlich als Markdown schreiben
string? storeDir = null, reportPath = null;
bool skipDownload = false, synthetic = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--store": storeDir = args[++i]; break;
        case "--report": reportPath = args[++i]; break;
        case "--skip-download": skipDownload = true; break;
        case "--synthetic": synthetic = true; break;
        default:
            Console.Error.WriteLine($"Unbekannter Parameter: {args[i]}");
            return 2;
    }
}

var work = Path.Combine(Path.GetTempPath(), "dlss5-selftest-" + Guid.NewGuid().ToString("N")[..8]);
bool ownStore = storeDir is null;
storeDir ??= Path.Combine(work, "components");
Directory.CreateDirectory(work);

var report = new Report();
var catalog = ComponentCatalog.LoadEmbedded();
var store = new ComponentStore(Path.GetFullPath(storeDir), catalog);
try
{
    if (synthetic)
    {
        Placeholders.FillSynthetic(store, report);
    }
    else if (!skipDownload)
    {
        using var http = ComponentCheck.CreateHttpClient();
        await ComponentCheck.RunAsync(report, catalog, store, http);
    }
    Action<string, bool>? registerLayer = null;
#if WINDOWS
    registerLayer = WindowsCheck.LayerRegistrar(work);
#endif
    InstallCheck.Run(report, catalog, store, Path.Combine(work, "games"), registerLayer);
#if WINDOWS
    WindowsCheck.Run(report, work, store);
#endif
}
finally
{
    var markdown = report.ToMarkdown();
    if (reportPath is not null)
        File.WriteAllText(reportPath, markdown);
    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
        File.AppendAllText(summary, markdown);
    try
    {
        if (ownStore)
            Directory.Delete(work, recursive: true);
        else if (Directory.Exists(Path.Combine(work, "games")))
            Directory.Delete(work, recursive: true);
    }
    catch (IOException)
    {
    }
}

Console.WriteLine();
Console.WriteLine(report.Failed ? "SELBSTTEST FEHLGESCHLAGEN" : "Selbsttest bestanden");
return report.Failed ? 1 : 0;
