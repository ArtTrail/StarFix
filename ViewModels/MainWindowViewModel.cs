using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarFix;
using StarFix.Models;
using StarFix.Services;

namespace StarFix.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppConfig _cfg;

    public AppConfig Config => _cfg;

    public SolveViewModel Solve { get; }
    public ResultsViewModel Results { get; }

    public MainWindowViewModel()
    {
        _cfg = ConfigService.Load();
        if (string.IsNullOrWhiteSpace(_cfg.GaiaCatalogPath))
        {
            _cfg.GaiaCatalogPath = System.IO.Path.Combine(ConfigService.AppDataDir, "gaia_catalog");
            ConfigService.Save(_cfg);
        }

        Results = new ResultsViewModel();
        Solve = new SolveViewModel(_cfg) { OnSolved = Results.Add, OnJobStarting = Results.Clear };
    }

    // ── Update checker ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isUpdateDownloading;
    [ObservableProperty] private bool _isUpdateMessageVisible;
    [ObservableProperty] private string _updateVersionText = "";
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private double _updateProgress;

    private UpdateInfo? _pendingUpdate;

    /// <summary>Set by MainWindow — starts the downloaded installer and shuts the app down so
    /// the installer can overwrite files StarFix currently has open/locked. Inno Setup's own
    /// upgrade handling (fixed AppId) takes it from there — no separate uninstall needed.</summary>
    public Action<string>? LaunchInstallerAndExit { get; set; }

    public async Task RunStartupUpdateCheckAsync()
    {
        var info = await UpdateService.CheckAsync(AppVersion.Version);
        if (info is null) return;
        _pendingUpdate = info;
        UpdateVersionText = $"StarFix v{info.Version} is available";
        IsUpdateAvailable = true;
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        SessionLogService.Write("[Update] User clicked Check for Updates");
        var info = await UpdateService.CheckAsync(AppVersion.Version);
        if (info is null)
        {
            UpdateStatusText = "StarFix is up to date.";
            IsUpdateMessageVisible = true;
            return;
        }
        _pendingUpdate = info;
        UpdateVersionText = $"StarFix v{info.Version} is available";
        IsUpdateMessageVisible = false;
        IsUpdateAvailable = true;
    }

    [RelayCommand]
    private void SkipUpdate()
    {
        SessionLogService.Write("[Update] User clicked Later");
        IsUpdateAvailable = false;
    }

    [RelayCommand]
    private void DismissUpdateMessage() => IsUpdateMessageVisible = false;

    [RelayCommand]
    private async Task DownloadAndInstallUpdate()
    {
        if (_pendingUpdate is null) return;
        SessionLogService.Write($"[Update] Downloading {_pendingUpdate.AssetName}");

        IsUpdateAvailable = false;
        IsUpdateDownloading = true;
        UpdateStatusText = "Downloading…";
        UpdateProgress = 0;

        var destPath = Path.Combine(Path.GetTempPath(), _pendingUpdate.AssetName);
        try
        {
            var progress = new Progress<(long done, long total)>(t =>
            {
                if (t.total > 0)
                {
                    UpdateProgress   = (double)t.done / t.total * 100;
                    UpdateStatusText = $"Downloading… {t.done / 1_048_576.0:F1} MB / {t.total / 1_048_576.0:F1} MB";
                }
            });
            await UpdateService.DownloadInstallerAsync(_pendingUpdate.DownloadUrl, destPath, progress, default);

            SessionLogService.Write("[Update] Download complete, launching installer and exiting.");
            UpdateStatusText = "Download complete — restarting to install…";
            LaunchInstallerAndExit?.Invoke(destPath);
        }
        catch (Exception ex)
        {
            IsUpdateDownloading = false;
            UpdateStatusText = $"Update download failed: {ex.Message}";
            IsUpdateMessageVisible = true;
            SessionLogService.Write($"[Update] Download failed: {ex}");
        }
    }
}
