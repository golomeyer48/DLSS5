using System.Net.Http.Headers;
using System.Text.Json;
using Dlss5Optimizer.Core.Components;
using Dlss5Optimizer.Core.Detection;
using Dlss5Optimizer.Core.Models;
using Ids = Dlss5Optimizer.Core.Decision.RouteCatalog.Ids;

namespace Dlss5Optimizer.SelfTest;

/// <summary>
/// Lädt jede automatisch ladbare Komponente wirklich aus ihrer Quelle und prüft, ob darin die Dateien
/// liegen, die der Routen-Planer braucht – in der richtigen Architektur (32/64 Bit).
/// </summary>
public static class ComponentCheck
{
    /// <summary>Was die Routen aus jeder Komponente brauchen; Bitness null = keine PE-Datei.</summary>
    private static readonly Dictionary<string, (string Pattern, Bitness? Bits)[]> Needs = new()
    {
        [Ids.ReShade] = [("ReShade32.dll", Bitness.X86), ("ReShade64.dll", Bitness.X64), ("ReShade32.json", null), ("ReShade64.json", null)],
        [Ids.Feeder] = [("dlss5-feed.addon32", Bitness.X86), ("dlss5-feed.addon64", Bitness.X64), ("dlss5-feed-host64.exe", Bitness.X64), ("DLSS5_Feed.fx", null)],
        [Ids.LumeniteFx] = [("lumenite_Kernel.fx", null)],
        [Ids.ReShadeHeaders] = [("ReShade.fxh", null), ("ReShadeUI.fxh", null), ("DrawText.fxh", null)],
        [Ids.Dxvk] = [("x32/d3d9.dll", Bitness.X86), ("x64/d3d9.dll", Bitness.X64)],
        [Ids.D3D8To9] = [("d3d8.dll", Bitness.X86)],
        [Ids.DgVoodoo] = [("MS/x86/D3D9.dll", Bitness.X86), ("MS/x86/D3D8.dll", Bitness.X86), ("MS/x86/DDraw.dll", Bitness.X86), ("MS/x86/D3DImm.dll", Bitness.X86), ("MS/x64/D3D9.dll", Bitness.X64), ("dgVoodoo.conf", null)],
        [Ids.RenoDx] = [("renodx-dlss5*.addon64", Bitness.X64)],
        [Ids.OptiScalerNr] = [("OptiScaler.dll", Bitness.X64), ("OptiScaler.ini", null)],
        [Ids.OptiScalerPreUpscale] = [("OptiScaler.dll", Bitness.X64), ("OptiScaler.ini", null)],
        [Ids.Bridge] = [("dlss5-bridge.addon64", Bitness.X64)],
        [Ids.DlssRuntime] = [("nvngx_dlss.dll", Bitness.X64)],
        [Ids.DlssNrModel] = [("nvngx_dlssnr.dll", Bitness.X64)],
        [Ids.PresentMon] = [("PresentMon*.exe", Bitness.X64)],
    };

    /// <summary>HTTP-Client wie in der App; ein GitHub-Token (CI) geht nur an api.github.com.</summary>
    public static HttpClient CreateHttpClient()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var client = string.IsNullOrEmpty(token)
            ? ComponentDownloader.CreateHttpClient()
            : new HttpClient(new GitHubApiAuth(token) { InnerHandler = new HttpClientHandler() }) { Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DLSS5-Optimizer-SelfTest", "0.1"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
        return client;
    }

    public static async Task RunAsync(Report report, ComponentCatalog catalog, ComponentStore store, HttpClient http)
    {
        report.Section("Komponenten aus den Originalquellen");
        var downloader = new ComponentDownloader(http, store, catalog);
        foreach (var def in catalog.Components)
        {
            if (!def.CanAutoDownload)
            {
                report.Info(def.Name, def.Source.Type switch
                {
                    ComponentSourceType.DriverStore => "kommt aus dem NVIDIA-Treiber (hier Platzhalter)",
                    _ => "nur Import (hier Platzhalter)",
                });
                continue;
            }
            try
            {
                var started = DateTime.UtcNow;
                var stored = await downloader.DownloadAsync(def.Id);
                long bytes = stored.Files.Sum(f => f.Size);
                var problems = CheckNeeds(store, def.Id).ToList();
                var detail = $"{stored.Version} · {Path.GetFileName(new Uri(stored.Origin).LocalPath)} · {stored.Files.Count} Dateien, {bytes / 1024.0 / 1024:0.0} MB · {(DateTime.UtcNow - started).TotalSeconds:0} s";
                if (problems.Count == 0)
                    report.Ok(def.Name, detail);
                else
                    report.Fail(def.Name, detail + " · " + string.Join("; ", problems));
                if (def.Id == Ids.ReShade)
                    CheckVulkanLayerJson(report, store);
            }
            catch (Exception e)
            {
                report.Fail(def.Name, $"{e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>Fehlende oder falsch gebaute Dateien einer Komponente.</summary>
    public static IEnumerable<string> CheckNeeds(ComponentStore store, string id)
    {
        if (!Needs.TryGetValue(id, out var needs))
            yield break;
        var root = store.FilesDir(id);
        foreach (var (pattern, bits) in needs)
        {
            var dir = Path.GetDirectoryName(pattern) is { Length: > 0 } sub ? sub.Replace('\\', '/') : null;
            var name = Path.GetFileName(pattern);
            var matches = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, name, new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
                    .Where(p => dir is null || p.Replace('\\', '/').Contains($"/{dir}/", StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : [];
            if (matches.Count == 0)
            {
                yield return $"{pattern} fehlt";
                continue;
            }
            if (bits is null)
                continue;
            // Der Planer nimmt bei mehreren Treffern die zur Architektur passende Datei.
            var picked = dir is null ? store.FindFile(id, name, prefer32Bit: bits == Bitness.X86) : matches[0];
            var pe = picked is null ? null : PeFile.TryRead(picked);
            if (pe is null)
                yield return $"{pattern} ist keine gültige Windows-DLL/EXE";
            else if (pe.Bitness != bits)
                yield return $"{pattern}: {Bits(pe.Bitness)} statt {Bits(bits.Value)} ({Path.GetRelativePath(root, picked!)})";
        }
    }

    /// <summary>Der Vulkan-Layer-Eintrag zeigt auf die DLL neben der JSON – sonst lädt Vulkan ReShade nie.</summary>
    private static void CheckVulkanLayerJson(Report report, ComponentStore store)
    {
        foreach (var bits in new[] { "32", "64" })
        {
            var json = store.FindFile(Ids.ReShade, $"ReShade{bits}.json");
            if (json is null)
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var lib = doc.RootElement.GetProperty("layer").GetProperty("library_path").GetString() ?? "";
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(json)!, lib.Replace('\\', Path.DirectorySeparatorChar)));
                report.Check(File.Exists(resolved), $"ReShade{bits}.json (Vulkan-Layer)", $"library_path „{lib}“ zeigt ins Leere", $"library_path „{lib}“ gefunden");
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                report.Fail($"ReShade{bits}.json (Vulkan-Layer)", "kein gültiges Layer-Manifest: " + e.Message);
            }
        }
    }

    public static string Bits(Bitness b) => b switch { Bitness.X86 => "32 Bit", Bitness.X64 => "64 Bit", _ => "?" };

    private sealed class GitHubApiAuth(string token) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) == true)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, ct);
        }
    }
}
