using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StarFix.Models;
using StarFix.Services;

namespace StarFix.ViewModels;

/// <summary>Req #7: two-phase "crawl (enumerate release assets) -> confirm -> download with
/// progress -> done" flow, matching TransitLab's LdtkLibraryDownloadViewModel UX exactly.</summary>
public partial class GaiaCatalogDownloadViewModel : ViewModelBase
{
    private readonly AppConfig _cfg;
    private List<CatalogAsset> _assets = new();
    private CancellationTokenSource? _cts;

    public GaiaCatalogDownloadViewModel(AppConfig cfg)
    {
        _cfg = cfg;
    }

    public Action? CloseCallback { get; set; }

    [ObservableProperty] private bool _isProbing = true;
    [ObservableProperty] private bool _isConfirmVisible;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private string _headlineText = "Checking the catalog release…";
    [ObservableProperty] private string _confirmText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 100;

    /// <summary>How long to wait for GitHub's release-metadata API before giving up and
    /// reporting a specific timeout — this is one small JSON request, not a large download, so
    /// it should never legitimately take long. Without this, a network problem (no internet,
    /// DNS failure, firewall) left the user stuck on "Checking the catalog release…" with no
    /// further feedback at all, since the HttpClient used for the big downloads has a 15-minute
    /// timeout that's completely wrong for this call.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    public async Task StartAsync()
    {
        IsProbing = true;
        HeadlineText = "Checking the catalog release on GitHub…";
        _cts = new CancellationTokenSource(ProbeTimeout);

        try
        {
            _assets = await GaiaCatalogService.EnumerateAssetsAsync(_cts.Token);

            if (_assets.Count == 0)
            {
                HeadlineText = "The catalog release exists but has no files listed — this looks like a server-side problem, not a network one. Try again later, or check github.com/ArtTrail/TransitLab/releases/tag/gaia-catalog-v1 directly.";
                IsProbing = false;
                IsError = true;
                return;
            }

            var totalBytes = 0L;
            foreach (var a in _assets) totalBytes += a.Size;
            var gb = totalBytes / 1_073_741_824.0;

            HeadlineText = "Gaia catalog release found";
            ConfirmText = $"{_assets.Count} files, ~{gb:F1} GB. Download now?";
            SessionLogService.Write($"[GaiaCatalog] Release check OK — {_assets.Count} files, {gb:F2} GB listed.");
            IsProbing = false;
            IsConfirmVisible = true;
        }
        catch (OperationCanceledException)
        {
            HeadlineText = $"Timed out after {ProbeTimeout.TotalSeconds:F0}s contacting GitHub — check your internet connection and try again.";
            IsProbing = false;
            IsError = true;
            SessionLogService.Write("[GaiaCatalog] Probe timed out contacting GitHub API.");
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            HeadlineText = $"Could not reach GitHub: {ex.Message}";
            IsProbing = false;
            IsError = true;
            SessionLogService.Write($"[GaiaCatalog] Probe failed (HTTP): {ex.Message}");
        }
        catch (Exception ex)
        {
            HeadlineText = $"Could not check the catalog release: {ex.GetType().Name} — {ex.Message}";
            IsProbing = false;
            IsError = true;
            SessionLogService.Write($"[GaiaCatalog] Probe failed: {ex}");
        }
    }

    [RelayCommand]
    private async Task Retry()
    {
        IsError = false;
        await StartAsync();
    }

    [RelayCommand]
    private async Task Confirm()
    {
        IsConfirmVisible = false;
        IsDownloading = true;
        HeadlineText = "Downloading Gaia catalog…";
        ProgressValue = 0;
        ProgressMax = _assets.Count;
        _cts = new CancellationTokenSource();

        var destDir = string.IsNullOrWhiteSpace(_cfg.GaiaCatalogPath)
            ? System.IO.Path.Combine(ConfigService.AppDataDir, "gaia_catalog")
            : _cfg.GaiaCatalogPath;

        var totalGb = 0L;
        foreach (var a in _assets) totalGb += a.Size;
        SessionLogService.Write(
            $"[GaiaCatalog] Starting download — {_assets.Count} files, " +
            $"{totalGb / 1_073_741_824.0:F2} GB, destination: {destDir}");

        var progress = new Progress<(int done, int total, string currentFile)>(t =>
        {
            ProgressValue = t.done;
            StatusText = $"{t.done}/{t.total} — {t.currentFile}";
        });

        try
        {
            var result = await GaiaCatalogService.DownloadAllAsync(_assets, destDir, progress, maxConcurrency: 8, _cts.Token);

            HeadlineText = "Installing…";
            StatusText = "Verifying downloaded files…";

            long totalBytesOnDisk = 0;
            foreach (var f in System.IO.Directory.GetFiles(destDir, "*.npz"))
                totalBytesOnDisk += new System.IO.FileInfo(f).Length;

            _cfg.GaiaCatalogPath = destDir;
            _cfg.GaiaCatalogInstalled = true;
            _cfg.GaiaCatalogBytesOnDisk = totalBytesOnDisk;
            ConfigService.Save(_cfg);

            HeadlineText = "Gaia catalog installed";
            StatusText = $"Done — {_assets.Count} files, {totalBytesOnDisk / 1_073_741_824.0:F1} GB.";

            string throughputClause;
            if (result.Downloaded == 0)
            {
                throughputClause = "nothing new to download, all files already present.";
            }
            else
            {
                var throughputMBps = (result.DownloadedBytes / 1_048_576.0) / result.Elapsed.TotalSeconds;
                throughputClause = $"took {result.Elapsed.TotalSeconds:F1}s for {result.Downloaded} new file(s) " +
                                    $"({result.DownloadedBytes / 1_048_576.0:F0} MB, ~{throughputMBps:F1} MB/s average).";
            }
            SessionLogService.Write(
                $"[GaiaCatalog] Download complete — {destDir} — {result.Downloaded} downloaded, " +
                $"{result.AlreadyPresent} already present, {totalBytesOnDisk} bytes on disk " +
                $"(expected {result.TotalBytes}), {throughputClause}");

            if (totalBytesOnDisk != result.TotalBytes)
                SessionLogService.Write(
                    $"[GaiaCatalog] WARNING — total bytes on disk ({totalBytesOnDisk}) does not match " +
                    $"the expected total from GitHub ({result.TotalBytes}) — the folder may have extra " +
                    "or stale files from a previous install.");
        }
        catch (OperationCanceledException)
        {
            HeadlineText = "Download cancelled";
            StatusText = "Cancelled.";
            SessionLogService.Write("[GaiaCatalog] Download cancelled by user.");
        }
        catch (Exception ex)
        {
            HeadlineText = "Download failed";
            StatusText = $"Failed: {ex.Message}";
            SessionLogService.Write($"[GaiaCatalog] Download failed: {ex.GetType().Name} — {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
            IsDone = true;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsDownloading) { _cts?.Cancel(); return; }
        CloseCallback?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseCallback?.Invoke();
}
