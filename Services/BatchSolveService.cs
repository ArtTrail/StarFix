using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StarFix.Models;

namespace StarFix.Services;

/// <summary>Runs a batch across a pool of persistent BatchSolverSession processes (issue #13),
/// each kept alive for the whole run so gaia_catalog_lookup.py's module-level catalog cache
/// stays warm. The pool size and each session's internal candidate-race width are both derived
/// from one "Max workers" budget so that files-in-flight × per-file race width never exceeds it
/// — outer and inner parallelism sharing a single CPU budget, which is the whole point: letting
/// them both run unbounded oversubscribes the CPU and is slower, not faster (see solve.py's
/// _run_one_race_attempt and _apply_race_cap_env). With budget 1 (or a single pending file) this
/// degrades to exactly the previous one-session, one-file-at-a-time behaviour.
///
/// NOTE: an earlier version of the batch runner also (a) narrowed the catalog search radius
/// after the first successful solve, and (b) carried the previous file's winning FWHM/match-cap
/// forward as a hint. Both were rolled back after real-world testing — see solve.py's
/// run_server() docstring for (b), and git history / plate_solver.md for (a). Both shared one
/// root cause: the validating test data was identical copies of one file, which can never expose
/// "a guess from file N is actively wrong for file N+1". Neither is reintroduced here.</summary>
public record BatchSolveSummary(int Solved, int Failed, int Skipped, double? MeanRmsPixels);

public static class BatchSolveService
{
    public static async Task<BatchSolveSummary> RunAsync(
        IReadOnlyList<string> filePaths, double radiusDeg, bool overwriteExisting,
        string? catalogDir, int maxWorkers, IProgress<string> progress,
        Action<SolveOutcome>? onResult, CancellationToken ct)
    {
        // Pre-scan: already-solved files are reported and dropped up front, so the worker pool
        // only ever sees files that actually need solving. Covers both overwrite mode (source's
        // own PLTSOLVD header) and new-file mode (a "_solved_N" copy already exists alongside the
        // untouched source).
        var pending = new List<string>();
        int skipped = 0;
        foreach (var path in filePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (AlreadySolvedService.IsAlreadySolved(path))
            {
                skipped++;
                progress.Report($"{Path.GetFileName(path)} — already solved, skipping");
            }
            else pending.Add(path);
        }

        if (pending.Count == 0)
        {
            progress.Report($"Batch complete — 0 solved, 0 failed, {skipped} already solved (skipped).");
            return new BatchSolveSummary(0, 0, skipped, null);
        }

        // One CPU budget, split between parallel sessions and each session's internal race.
        int budget = maxWorkers > 0 ? maxWorkers : Math.Min(Environment.ProcessorCount, 8);
        int sessionCount = Math.Max(1, Math.Min(budget, pending.Count));
        int racePerSession = Math.Max(1, budget / sessionCount);

        progress.Report($"Solving {pending.Count} file(s) across {sessionCount} parallel worker(s)" +
                        (racePerSession > 1 ? $", {racePerSession} race slots each" : "") + "…");

        int solved = 0, failed = 0, completed = 0;
        double rmsSum = 0;
        var accLock = new object();
        int nextIndex = -1; // Interlocked.Increment → first worker gets 0.

        var sessions = new List<BatchSolverSession>();
        try
        {
            for (int s = 0; s < sessionCount; s++)
            {
                var session = new BatchSolverSession();
                session.Start(catalogDir, racePerSession);
                sessions.Add(session);
            }

            // Each session pulls the next file off the shared index until the list is exhausted.
            // The real solving runs in the Python subprocesses (truly parallel); the C# side just
            // awaits each process's JSON reply. All await continuations marshal back to the UI
            // sync-context captured by the caller, so onResult (which mutates the results
            // ObservableCollection) and the accounting below stay single-threaded in practice —
            // the lock/Interlocked are belt-and-suspenders in case that ever changes.
            async Task RunSession(BatchSolverSession session)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int i = Interlocked.Increment(ref nextIndex);
                    if (i >= pending.Count) return;

                    var path = pending[i];
                    var name = Path.GetFileName(path);

                    double? ra = null, dec = null;
                    try
                    {
                        var header = FitsHeaderService.Read(path);
                        ra = header?.GetDouble("RA");
                        dec = header?.GetDouble("DEC");
                    }
                    catch (Exception ex)
                    {
                        progress.Report($"  ⚠  Could not read header from {name}: {ex.Message}");
                    }

                    var outcome = await session.SolveOneAsync(path, ra, dec, radiusDeg, overwriteExisting, ct);

                    int done;
                    lock (accLock)
                    {
                        completed++;
                        done = completed;
                        if (outcome.Success) { solved++; rmsSum += outcome.Result?.RmsPixels ?? 0; }
                        else failed++;
                    }

                    progress.Report(outcome.Success
                        ? $"[{done}/{pending.Count}] ✓  {name} — {outcome.Result?.NumMatched}/{outcome.Result?.NumDetected} matched, RMS {outcome.Result?.RmsPixels:F2}px"
                        : $"[{done}/{pending.Count}] ✗  {name} — {outcome.ErrorMessage}");

                    onResult?.Invoke(outcome);
                }
            }

            // A shared cancellation token means a Cancel fires every active session's own
            // kill-the-process registration at once, so all in-flight solves abort promptly.
            await Task.WhenAll(sessions.Select(RunSession));
        }
        finally
        {
            foreach (var session in sessions)
                await session.StopAsync();
        }

        double? meanRms = solved > 0 ? rmsSum / solved : null;
        var meanRmsText = meanRms.HasValue ? $", mean RMS {meanRms:F2}px" : "";
        progress.Report($"Batch complete — {solved} solved, {failed} failed, {skipped} already solved (skipped){meanRmsText}.");
        return new BatchSolveSummary(solved, failed, skipped, meanRms);
    }
}
