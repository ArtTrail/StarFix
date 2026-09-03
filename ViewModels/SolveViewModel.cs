using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarFix.Models;
using StarFix.Services;

namespace StarFix.ViewModels;

public partial class SolveViewModel : ViewModelBase
{
    private readonly AppConfig _cfg;

    public SolveViewModel(AppConfig cfg)
    {
        _cfg = cfg;
        RadiusDeg = cfg.DefaultSearchRadiusDeg;
        RefreshCatalogStatus();
    }

    /// <summary>Re-checks whether the Gaia catalog folder actually has files in it — a real
    /// directory check, not just trusting the cached AppConfig.GaiaCatalogInstalled flag, so a
    /// manually deleted/moved folder is caught too. Call after the catalog download window
    /// closes (it may have just installed the catalog).</summary>
    public void RefreshCatalogStatus()
    {
        var installed = !string.IsNullOrWhiteSpace(_cfg.GaiaCatalogPath)
            && System.IO.Directory.Exists(_cfg.GaiaCatalogPath)
            && System.IO.Directory.GetFiles(_cfg.GaiaCatalogPath, "*.npz").Length > 0;

        if (installed != _cfg.GaiaCatalogInstalled)
        {
            _cfg.GaiaCatalogInstalled = installed;
            ConfigService.Save(_cfg);
        }
        IsCatalogInstalled = installed;
    }

    /// <summary>Set by MainWindowViewModel; called with the outcome of every completed solve.</summary>
    public Action<SolveOutcome>? OnSolved { get; set; }

    /// <summary>Set by MainWindowViewModel to Results.Clear — fired right before a solve
    /// starts, per the user's explicit request that a new job clears the panel rather than
    /// mixing with whatever an earlier job left behind.</summary>
    public Action? OnJobStarting { get; set; }

    /// <summary>Set by MainWindow (code-behind, not the ViewModel layer — opening a Window
    /// needs an owner reference) to open the Batch Solve window. The button lives here in
    /// the Solve pane's header instead of under Tools, per the user's explicit request.</summary>
    public Action? OnBatchSolveRequested { get; set; }

    [RelayCommand]
    private void BatchSolve() => OnBatchSolveRequested?.Invoke();

    /// <summary>Set by MainWindow to show the Cancel/Continue popup — same dialog and same
    /// already-solved definition (AlreadySolvedService) Batch Solve uses. Returns true to
    /// proceed anyway, false to cancel without solving.</summary>
    public Func<Task<bool>>? ConfirmAlreadySolvedFunc { get; set; }

    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _raText = "";
    [ObservableProperty] private string _decText = "";
    [ObservableProperty] private double _radiusDeg;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isCatalogInstalled;

    partial void OnFilePathChanged(string value)
    {
        SolveCommand.NotifyCanExecuteChanged();

        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            var header = FitsHeaderService.Read(value);
            var ra  = header.GetDouble("RA");
            var dec = header.GetDouble("DEC");
            if (ra.HasValue)  RaText  = ra.Value.ToString("F6");
            if (dec.HasValue) DecText = dec.Value.ToString("F6");
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Solve] Could not read header from {value}: {ex.Message}");
        }
    }

    partial void OnIsBusyChanged(bool value) => SolveCommand.NotifyCanExecuteChanged();
    partial void OnIsCatalogInstalledChanged(bool value) => SolveCommand.NotifyCanExecuteChanged();

    private bool CanSolve => !IsBusy && !string.IsNullOrWhiteSpace(FilePath) && IsCatalogInstalled;

    [RelayCommand(CanExecute = nameof(CanSolve))]
    private async Task SolveAsync()
    {
        if (AlreadySolvedService.IsAlreadySolved(FilePath) && ConfirmAlreadySolvedFunc is not null)
        {
            var proceed = await ConfirmAlreadySolvedFunc();
            if (!proceed)
            {
                StatusIsError = false;
                StatusText = "Cancelled — this file already appears solved.";
                return;
            }
        }

        OnJobStarting?.Invoke();
        IsBusy = true;
        StatusIsError = false;
        StatusText = "Solving…";
        try
        {
            double? ra  = NumericParseService.TryParse(RaText, out var raV)  ? raV  : null;
            double? dec = NumericParseService.TryParse(DecText, out var decV) ? decV : null;

            var catalogDir = string.IsNullOrWhiteSpace(_cfg.GaiaCatalogPath) ? null : _cfg.GaiaCatalogPath;
            var outcome = await PlateSolveService.SolveOneAsync(
                FilePath, ra, dec, RadiusDeg, _cfg.OverwriteExisting, catalogDir, default);

            StatusIsError = !outcome.Success;
            StatusText = outcome.Success
                ? $"Solved — {outcome.Result?.NumMatched}/{outcome.Result?.NumDetected} matched"
                : $"Failed — {outcome.ErrorMessage}";

            OnSolved?.Invoke(outcome);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
