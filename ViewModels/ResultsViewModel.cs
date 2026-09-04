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
            if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                SaveCsv(path);
            else
                SaveText(path);
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Results] Save failed: {ex.Message}");
        }
    }

    private void SaveText(string path)
    {
        using var writer = new StreamWriter(path);
        foreach (var entry in Entries)
        {
            writer.WriteLine($"{entry.FileName}  ({entry.TimestampText})");
            writer.WriteLine(entry.Success ? entry.Result?.Text ?? "" : $"FAILED — {entry.ErrorMessage}");
            writer.WriteLine();
        }
    }

    /// <summary>The numeric fields (RMS, pixel scale, rotation, ...) are exactly the kind of
    /// thing worth charting across a session — did tracking or focus drift over the night —
    /// which a wall of card text can't offer. One row per solve, newest first, same order as
    /// the panel itself.</summary>
    private void SaveCsv(string path)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine(string.Join(',', new[]
        {
            "FileName", "Timestamp", "Success", "ErrorMessage",
            "CenterRaDeg", "CenterDecDeg", "PixelScaleArcsec", "RotationDeg", "Parity",
            "FovWidthArcmin", "FovHeightArcmin",
            "NumDetected", "NumCatalog", "NumMatched", "RmsPixels", "RmsArcsec",
        }));

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var entry in Entries)
        {
            var s = entry.Result?.Summary;
            var fields = new[]
            {
                entry.FileName,
                entry.CompletedAt.ToString("yyyy-MM-dd HH:mm:ss", ic),
                entry.Success.ToString(),
                entry.ErrorMessage ?? "",
                s?.CenterRaDeg.ToString(ic) ?? "",
                s?.CenterDecDeg.ToString(ic) ?? "",
                s?.PixelScaleArcsec.ToString(ic) ?? "",
                s?.RotationDeg.ToString(ic) ?? "",
                s?.Parity ?? "",
                s?.FovWidthArcmin.ToString(ic) ?? "",
                s?.FovHeightArcmin.ToString(ic) ?? "",
                entry.Result?.NumDetected.ToString(ic) ?? "",
                entry.Result?.NumCatalog.ToString(ic) ?? "",
                entry.Result?.NumMatched.ToString(ic) ?? "",
                entry.Result?.RmsPixels.ToString(ic) ?? "",
                s?.RmsArcsec.ToString(ic) ?? "",
            };
            writer.WriteLine(string.Join(',', Array.ConvertAll(fields, CsvEscape)));
        }
    }

    private static string CsvEscape(string field) =>
        field.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
}
