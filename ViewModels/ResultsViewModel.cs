using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using StarFix.Models;
using StarFix.Services;

namespace StarFix.ViewModels;

/// <summary>Req #4: one card per completed solve (single-file or batch — same shared list),
/// newest first. This is the live "window" the summary appears in, instead of a popup per
/// solve, so a batch run doesn't spawn a pile of stacked windows.</summary>
public class SolveResultEntry
{
    public required string FileName { get; init; }
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public SolveResultJson? Result { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.Now;

    public string TimestampText => CompletedAt.ToString("HH:mm:ss");
}

public partial class ResultsViewModel : ViewModelBase
{
    public ObservableCollection<SolveResultEntry> Entries { get; }

    /// <summary>Set by the view's code-behind to the platform save-file picker (needs a
    /// TopLevel, which a plain ViewModel doesn't have access to) — same delegate pattern
    /// Diagnostics uses for its own Save Log button.</summary>
    public Func<Task<string?>>? SaveFileFunc { get; set; }

    public ResultsViewModel()
    {
        Entries = new ObservableCollection<SolveResultEntry>(ResultsHistoryService.Load());
    }

    public void Add(SolveOutcome outcome)
    {
        Entries.Insert(0, new SolveResultEntry
        {
            FileName = Path.GetFileName(outcome.SolvedPath),
            Success = outcome.Success,
            ErrorMessage = outcome.ErrorMessage,
            Result = outcome.Result,
            CompletedAt = outcome.CompletedAt,
        });

        ResultsHistoryService.Save(Entries);
    }

    /// <summary>Called both by the Clear button and automatically whenever a new single-file
    /// or batch job starts, per the user's explicit request — the persisted history still
    /// covers a plain app restart, but starting a new job now means a fresh panel rather than
    /// old and new results mixing together.</summary>
    [RelayCommand]
    public void Clear()
    {
        Entries.Clear();
        ResultsHistoryService.Save(Entries);
    }

    [RelayCommand]
    private async Task Save()
    {
        if (SaveFileFunc is null) return;
        var path = await SaveFileFunc();
        if (path is null) return;

        try
        {
            using var writer = new StreamWriter(path);
            foreach (var entry in Entries)
            {
                writer.WriteLine($"{entry.FileName}  ({entry.TimestampText})");
                writer.WriteLine(entry.Success ? entry.Result?.Text ?? "" : $"FAILED — {entry.ErrorMessage}");
                writer.WriteLine();
            }
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Results] Save failed: {ex.Message}");
        }
    }
}
