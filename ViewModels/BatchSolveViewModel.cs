using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StarFix.Models;
using StarFix.Services;

namespace StarFix.ViewModels;

/// <summary>Batch Solve window (Tools menu) — runs PlateSolveService across a list of files
/// via BatchSolveService, one at a time, adapted from VariLab's BatchViewModel (targets ->
/// file paths).</summary>
public partial class BatchSolveViewModel : ViewModelBase
{
    private readonly AppConfig _cfg;

    public BatchSolveViewModel(AppConfig cfg)
    {
        _cfg = cfg;
        RadiusDeg = cfg.DefaultSearchRadiusDeg;
    }

    /// <summary>Set by MainWindowViewModel; called with the outcome of every completed solve.</summary>
    public Action<SolveOutcome>? OnResult { get; set; }

    /// <summary>Set by MainWindowViewModel to Results.Clear — fired right before the batch
    /// starts, same as SolveViewModel's OnJobStarting.</summary>
    public Action? OnJobStarting { get; set; }

    [ObservableProperty] private string _filePathsText = "";
    [ObservableProperty] private double _radiusDeg;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private string _status = "Not run yet.";

    private CancellationTokenSource? _cts;

    public Func<Task<string?>>? FolderPickerFunc { get; set; }
    public Func<Task<string?>>? FilePickerFunc { get; set; }

    /// <summary>Set by BatchSolveWindow to show the Cancel/Continue popup. Args are
    /// (already-solved count, total count); returns true to proceed with the batch as listed
    /// (re-solving the already-solved files too), false to cancel and not run anything.</summary>
    public Func<int, int, Task<bool>>? ConfirmAlreadySolvedFunc { get; set; }

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (FolderPickerFunc is null) return;
        var dir = await FolderPickerFunc();
        if (string.IsNullOrEmpty(dir)) return;

        // Exclude StarFix's own previous "new file" mode OUTPUT copies (e.g.
        // "target_solved_1.fits") — these are results, not sources to solve. Without this,
        // re-running "Add folder" against a folder that already has earlier solved copies in
        // it re-ingests and re-solves them, compounding the suffix into
        // "target_solved_1_solved_1.fits" and so on, confirmed happening in real batch output.
        //
        // Already-solved SOURCE files are deliberately still included here (not silently
        // filtered) — Start's own already-solved check is the one place that warns about
        // them, and it can only do that if the list actually contains them. Silently emptying
        // the list here (a folder that's entirely already solved) meant Start's "Add at least
        // one file first" fired before its already-solved popup ever got a chance to.
        var alreadySolvedPattern = new System.Text.RegularExpressions.Regex(
            @"_solved_\d+\.(fits|fit|fts|fz)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var files = System.IO.Directory.GetFiles(dir, "*.fits")
            .Concat(System.IO.Directory.GetFiles(dir, "*.fit"))
            .Concat(System.IO.Directory.GetFiles(dir, "*.fts"))
            .Concat(System.IO.Directory.GetFiles(dir, "*.fz"))
            .Where(f => !alreadySolvedPattern.IsMatch(System.IO.Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        FilePathsText = string.Join(Environment.NewLine, files);

        var alreadySolvedCount = files.Count(AlreadySolvedService.IsAlreadySolved);
        Status = alreadySolvedCount > 0
            ? $"Added {files.Count} file(s) from {dir} — {alreadySolvedCount} already appear solved."
            : $"Added {files.Count} file(s) from {dir}.";
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        var paths = FilePathsText
            .Split('\n')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (paths.Count == 0)
        {
            Status = "Add at least one file first.";
            return;
        }

        // "Add folder" already excludes these, but the list can also be typed/pasted by hand
        // (which bypasses that filter entirely) or hand-edited after an Add folder — so this
        // is the one place that reliably catches every path regardless of how it got in.
        var alreadySolvedCount = paths.Count(AlreadySolvedService.IsAlreadySolved);
        if (alreadySolvedCount > 0 && ConfirmAlreadySolvedFunc is not null)
        {
            var proceed = await ConfirmAlreadySolvedFunc(alreadySolvedCount, paths.Count);
            if (!proceed)
            {
                Status = "Cancelled — already-solved files were in the list.";
                return;
            }
        }

        OnJobStarting?.Invoke();
        Log = "";
        IsRunning = true;
        Status = $"Running {paths.Count} file(s)…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(s => Log += s + "\n");
        var catalogDir = string.IsNullOrWhiteSpace(_cfg.GaiaCatalogPath) ? null : _cfg.GaiaCatalogPath;

        try
        {
            await BatchSolveService.RunAsync(
                paths, RadiusDeg, _cfg.OverwriteExisting,
                catalogDir, progress, OnResult, _cts.Token);
            Status = $"Batch finished — {paths.Count} file(s) processed.";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Batch failed: {ex.Message}";
            SessionLogService.Write($"[Batch] Run failed: {ex}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    partial void OnIsRunningChanged(bool value) => StartCommand.NotifyCanExecuteChanged();
}
