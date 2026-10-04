using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using StarFix.Models;
using StarFix.Services;

namespace StarFix.ViewModels;

/// <summary>Issue #9: the batch progress log is a collection of coloured lines rather than one
/// plain text block, so a solved file shows a green ✓ and a failed one a red ✗ at a glance.</summary>
public enum BatchLogKind { Info, Success, Failure, Warning, Skipped }

public class BatchLogLine
{
    public required string Text { get; init; }
    public required BatchLogKind Kind { get; init; }

    public IBrush Brush => Kind switch
    {
        BatchLogKind.Success => SuccessBrush,
        BatchLogKind.Failure => FailureBrush,
        BatchLogKind.Warning => WarningBrush,
        BatchLogKind.Skipped => SkippedBrush,
        _ => InfoBrush,
    };

    // Nord aurora/frost palette, matching the rest of the app's styling.
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#A3BE8C")); // nord14 green
    private static readonly IBrush FailureBrush = new SolidColorBrush(Color.Parse("#BF616A")); // nord11 red
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#EBCB8B")); // nord13 yellow
    private static readonly IBrush SkippedBrush = new SolidColorBrush(Color.Parse("#81A1C1")); // nord9 frost
    private static readonly IBrush InfoBrush    = new SolidColorBrush(Color.Parse("#D8DEE9")); // nord4 text
}

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
    [ObservableProperty] private string _status = "Not run yet.";

    /// <summary>Issue #9: one coloured entry per progress line (green ✓ / red ✗).</summary>
    public ObservableCollection<BatchLogLine> LogLines { get; } = new();

    private CancellationTokenSource? _cts;

    public Func<Task<string?>>? FolderPickerFunc { get; set; }
    public Func<Task<string?>>? FilePickerFunc { get; set; }

    private static readonly string[] FitsExtensions = [".fits", ".fit", ".fts", ".fz"];

    // Excludes StarFix's own "_solved_N" output copies — see BrowseFolder for the rationale.
    private static readonly System.Text.RegularExpressions.Regex SolvedCopyPattern =
        new(@"_solved_\d+\.(fits|fit|fts|fz)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Issue #10: adds dropped (or otherwise supplied) paths to the file list. Folders
    /// are expanded to their FITS files; individual FITS files are added directly. StarFix's own
    /// "_solved_N" output copies are excluded, and the merge is de-duplicated (case-insensitive)
    /// against whatever is already listed, preserving order.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var collected = new System.Collections.Generic.List<string>();
        foreach (var p in paths)
        {
            if (System.IO.Directory.Exists(p))
            {
                collected.AddRange(FitsExtensions
                    .SelectMany(ext => System.IO.Directory.GetFiles(p, "*" + ext))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            else if (System.IO.File.Exists(p) &&
                     FitsExtensions.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant()))
            {
                collected.Add(p);
            }
        }

        var existing = FilePathsText
            .Split('\n')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();
        var seen = new System.Collections.Generic.HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        int added = 0;
        foreach (var f in collected)
        {
            if (SolvedCopyPattern.IsMatch(System.IO.Path.GetFileName(f))) continue;
            if (seen.Add(f)) { existing.Add(f); added++; }
        }

        FilePathsText = string.Join(Environment.NewLine, existing);
        if (added > 0)
        {
            var alreadySolvedCount = existing.Count(AlreadySolvedService.IsAlreadySolved);
            Status = alreadySolvedCount > 0
                ? $"Added {added} file(s) — {alreadySolvedCount} of {existing.Count} already appear solved."
                : $"Added {added} file(s) — {existing.Count} total.";
        }
        else
        {
            Status = "No new FITS files were added.";
        }
    }

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
        LogLines.Clear();
        IsRunning = true;
        Status = $"Running {paths.Count} file(s)…";
        _cts = new CancellationTokenSource();
        // Progress<T> marshals back onto the UI thread captured here (Start runs on it), so
        // mutating the ObservableCollection from the callback is safe.
        var progress = new Progress<string>(s => LogLines.Add(new BatchLogLine { Text = s, Kind = ClassifyLine(s) }));
        var catalogDir = string.IsNullOrWhiteSpace(_cfg.GaiaCatalogPath) ? null : _cfg.GaiaCatalogPath;

        try
        {
            var summary = await BatchSolveService.RunAsync(
                paths, RadiusDeg, _cfg.OverwriteExisting,
                catalogDir, _cfg.MaxWorkers, progress, OnResult, _cts.Token);
            var meanRmsText = summary.MeanRmsPixels.HasValue ? $", mean RMS {summary.MeanRmsPixels:F2}px" : "";
            Status = $"Batch finished — {summary.Solved} solved, {summary.Failed} failed, " +
                     $"{summary.Skipped} already solved (skipped){meanRmsText}.";
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

    /// <summary>Classifies a BatchSolveService progress line by the status glyph it already
    /// carries (✓ solved, ✗ failed, ⚠ header warning) so the view can colour it. Kept as
    /// content-sniffing rather than a structured progress channel to leave the plain-string
    /// IProgress contract (shared with the diagnostics log) untouched.</summary>
    private static BatchLogKind ClassifyLine(string s) =>
        s.Contains('✓') ? BatchLogKind.Success :
        s.Contains('✗') ? BatchLogKind.Failure :
        s.Contains('⚠') ? BatchLogKind.Warning :
        s.Contains("already solved, skipping") ? BatchLogKind.Skipped :
        BatchLogKind.Info;
}
