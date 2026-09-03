using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StarFix.Services;

public record CatalogAsset(string Name, string Url, long Size);

/// <summary>Result of a DownloadAllAsync run — every field logged by the caller so the
/// diagnostics log carries the same level of detail a manual post-hoc check would show,
/// without needing one.</summary>
public record CatalogDownloadResult(int Downloaded, int AlreadyPresent, long DownloadedBytes, long TotalBytes, TimeSpan Elapsed);

/// <summary>Req #7: enumerates and downloads the 192 gaia-catalog-v1 release assets from the
/// existing ArtTrail/TransitLab repo. GitHub-API-enumeration pattern from TransitLab's own
/// UpdateService (self-update check); streaming-download-with-progress from
/// ExoticInstallService.DownloadFileAsync (used there for the ldtk library and Python
/// installer). No SDK dependency — raw HttpClient + System.Text.Json.Nodes.</summary>
public static class GaiaCatalogService
{
    private const string ReleaseApiUrl =
        "https://api.github.com/repos/ArtTrail/TransitLab/releases/tags/gaia-catalog-v1";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "StarFix-CatalogDownloader" } },
    };

    public static async Task<List<CatalogAsset>> EnumerateAssetsAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync(ReleaseApiUrl, ct);
        var root = JsonNode.Parse(json);
        var assets = root?["assets"]?.AsArray() ?? new JsonArray();

        return assets
            .Select(a => new CatalogAsset(
                a?["name"]?.GetValue<string>() ?? "",
                a?["browser_download_url"]?.GetValue<string>() ?? "",
                a?["size"]?.GetValue<long>() ?? 0))
            .Where(a => a.Name.EndsWith(".npz", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Downloads every asset not already present (by name + matching size) into
    /// destDir, with SemaphoreSlim-gated concurrency — same shape as ExoticInstallService's
    /// DownloadLdtkFilesAsync. Every freshly-downloaded file's actual size is checked against
    /// the size GitHub reported for it BEFORE it's moved into place — a truncated/interrupted
    /// transfer is deleted and reported as a failure rather than silently left in place, the
    /// same "never trust a transfer without checking its size against what the server actually
    /// promised" rule this session's build_catalog.py already learned the hard way against the
    /// ESA Gaia archive's own silent-truncation behavior (see plate_solver.md).</summary>
    public static async Task<CatalogDownloadResult> DownloadAllAsync(
        IReadOnlyList<CatalogAsset> assets, string destDir,
        IProgress<(int done, int total, string currentFile)> progress,
        int maxConcurrency, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(destDir);
        var done = 0;
        var downloaded = 0;
        var alreadyPresent = 0;
        long downloadedBytes = 0;
        var sem = new SemaphoreSlim(maxConcurrency);

        var tasks = assets.Select(async asset =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var dest = Path.Combine(destDir, asset.Name);
                if (File.Exists(dest) && new FileInfo(dest).Length == asset.Size)
                {
                    Interlocked.Increment(ref alreadyPresent);
                    Interlocked.Increment(ref done);
                    progress.Report((done, assets.Count, asset.Name));
                    return;
                }

                var tmp = dest + ".part";
                await DownloadFileAsync(asset.Url, tmp, null, ct);

                var actualSize = new FileInfo(tmp).Length;
                if (actualSize != asset.Size)
                {
                    try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
                    SessionLogService.Write(
                        $"[GaiaCatalog] MISMATCH — {asset.Name}: downloaded {actualSize} bytes, " +
                        $"GitHub reports {asset.Size} bytes — treating as a failed/truncated transfer.");
                    throw new IOException(
                        $"{asset.Name}: downloaded {actualSize} bytes but expected {asset.Size} — " +
                        "likely a truncated or interrupted transfer.");
                }

                File.Move(tmp, dest, overwrite: true);

                Interlocked.Increment(ref downloaded);
                Interlocked.Add(ref downloadedBytes, actualSize);
                Interlocked.Increment(ref done);
                progress.Report((done, assets.Count, asset.Name));
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        sw.Stop();

        long totalBytes = 0;
        foreach (var asset in assets) totalBytes += asset.Size;

        return new CatalogDownloadResult(downloaded, alreadyPresent, downloadedBytes, totalBytes, sw.Elapsed);
    }

    private static async Task DownloadFileAsync(
        string url, string dest, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src  = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(dest, FileMode.Create, FileAccess.Write);
        var buf = new byte[81920];
        long doneBytes = 0;
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await file.WriteAsync(buf.AsMemory(0, read), ct);
            doneBytes += read;
            progress?.Report((doneBytes, total));
        }
    }
}
