using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StarFix.ViewModels;

namespace StarFix.Services;

/// <summary>Persists the Results panel's entries to disk during a session (so the panel survives
/// transient view/window rebuilds within a run). Per issue #8 the history is cleared on normal
/// app exit (see App.axaml.cs), so a fresh launch starts with an empty panel rather than
/// reloading the previous session's results.</summary>
public static class ResultsHistoryService
{
    private static readonly string Path_ = System.IO.Path.Combine(ConfigService.AppDataDir, "results_history.json");

    // Caps unbounded growth over long-term use — high enough that no realistic single/batch
    // session would hit it, low enough to keep the file and startup load trivial.
    private const int MaxEntries = 500;

    public static List<SolveResultEntry> Load()
    {
        try
        {
            if (!File.Exists(Path_)) return new List<SolveResultEntry>();
            var json = File.ReadAllText(Path_);
            return JsonSerializer.Deserialize<List<SolveResultEntry>>(json) ?? new List<SolveResultEntry>();
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[ResultsHistory] Load failed: {ex.Message}");
            return new List<SolveResultEntry>();
        }
    }

    /// <summary>Removes the persisted history file (issue #8 — called on normal app exit so the
    /// next launch starts empty). A missing file is already treated as empty by Load().</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(Path_)) File.Delete(Path_);
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[ResultsHistory] Clear failed: {ex.Message}");
        }
    }

    /// <summary>Saves the full list (already newest-first), trimmed to MaxEntries.</summary>
    public static void Save(IReadOnlyList<SolveResultEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.AppDataDir);
            var trimmed = entries.Count > MaxEntries
                ? new List<SolveResultEntry>(entries)[..MaxEntries]
                : entries;
            File.WriteAllText(Path_, JsonSerializer.Serialize(trimmed, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[ResultsHistory] Save failed: {ex.Message}");
        }
    }
}
