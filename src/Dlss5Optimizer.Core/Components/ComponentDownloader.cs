using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dlss5Optimizer.Core.Components;

public sealed record ReleaseAsset(string Tag, string Name, string Url, long Size, string? Sha256, bool Prerelease);

/// <summary>
/// Lädt Open-Source-Komponenten direkt beim Hersteller (GitHub-Releases bzw. offizielle URL).
/// Es wird nichts mitgeliefert: Jede Datei kommt aus der Originalquelle, auf dem PC des Nutzers.
/// </summary>
public sealed class ComponentDownloader(HttpClient http, ComponentStore store, ComponentCatalog catalog)
{
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DLSS5-Optimizer", "0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<ReleaseAsset> FindLatestAsync(ComponentDefinition def, CancellationToken ct = default)
    {
        var src = def.Source;
        if (src.Type != ComponentSourceType.GitHub && src.Url is not null)
        {
            var url = src.Url ?? throw new InvalidOperationException($"{def.Id}: URL fehlt");
            return new ReleaseAsset("fest", src.FileName ?? Path.GetFileName(new Uri(url).LocalPath), url, 0, null, false);
        }
        if (src.Type != ComponentSourceType.GitHub || src.Repo is null)
            throw new InvalidOperationException($"{def.Name} kann nicht automatisch geladen werden.");

        using var resp = await http.GetAsync($"https://api.github.com/repos/{src.Repo}/releases?per_page=100", ct);
        resp.EnsureSuccessStatusCode();
        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        var asset = SelectAsset(doc.RootElement, src.AssetPattern, src.IncludePrerelease, src.TagPattern);
        return asset ?? throw new InvalidOperationException($"{def.Name}: Kein passendes Release in {src.Repo} gefunden.");
    }

    /// <summary>Wählt das neueste passende Asset (Entwürfe nie, Vorabversionen nur auf Wunsch).</summary>
    public static ReleaseAsset? SelectAsset(JsonElement releases, string? assetPattern, bool includePrerelease, string? tagPattern = null)
    {
        var regex = new Regex(assetPattern ?? @"\.(zip|7z)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var tagRegex = tagPattern is null ? null : new Regex(tagPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var rel in releases.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean())
                continue;
            bool pre = rel.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            if (pre && !includePrerelease)
                continue;
            var tag = rel.GetProperty("tag_name").GetString() ?? "?";
            if (tagRegex is not null && !tagRegex.IsMatch(tag))
                continue;
            if (!rel.TryGetProperty("assets", out var assets))
                continue;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!regex.IsMatch(name))
                    continue;
                string? sha = null;
                if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest["sha256:".Length..];
                return new ReleaseAsset(tag, name, a.GetProperty("browser_download_url").GetString()!, a.TryGetProperty("size", out var s) ? s.GetInt64() : 0, sha, pre);
            }
        }
        return null;
    }

    public async Task<StoredComponent> DownloadAsync(string id, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var def = catalog.Get(id) ?? throw new ArgumentException($"Unbekannte Komponente: {id}");
        var asset = await FindLatestAsync(def, ct);

        var tmpDir = Path.Combine(store.Root, "_downloads");
        Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, asset.Name);
        try
        {
            using (var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? asset.Size;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0)
                        progress?.Report((double)done / total);
                }
            }

            if (asset.Sha256 is { } expected)
            {
                var actual = ComponentStore.Sha256Of(tmp);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException($"{def.Name}: Prüfsumme stimmt nicht (erwartet {expected}, erhalten {actual}).");
            }
            return store.Import(id, [tmp], asset.Tag, asset.Url);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}
