using System;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StarFix.Services;

public record UpdateInfo(string Version, string AssetName, string DownloadUrl);

/// <summary>Checks GitHub Releases for a newer StarFix version — same GitHub-API-enumeration
/// pattern as GaiaCatalogService / TransitLab's own UpdateService. Looks specifically for the
/// "StarFix-Setup-*.exe" installer asset (not the portable zip), since the point is a
/// self-installing update: download it, launch it, and let Inno Setup's own upgrade handling
/// (fixed AppId) replace the existing per-user install in place — no separate uninstall step.</summary>
public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/ArtTrail/StarFix/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "StarFix-UpdateChecker" } },
    };

    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(ApiUrl, ct);
            var root = JsonNode.Parse(json);

            var tag = root?["tag_name"]?.GetValue<string>();
            if (tag is null) return null;

            var latestVersion = tag.TrimStart('v');
            if (!IsNewer(latestVersion, currentVersion)) return null;

            var assets = root?["assets"]?.AsArray();
            if (assets is null) return null;

            foreach (var asset in assets)
            {
                var name = asset?["name"]?.GetValue<string>() ?? "";
                var url  = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                if (name.StartsWith("StarFix-Setup-", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return new UpdateInfo(latestVersion, name, url);
            }
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Update] Check failed: {ex.Message}");
        }
        return null;
    }

    public static async Task DownloadInstallerAsync(
        string url, string destPath, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var src  = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write);
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

    private static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(latest, out var l)) return false;
        if (!Version.TryParse(current, out var c)) return false;
        return l > c;
    }
}
