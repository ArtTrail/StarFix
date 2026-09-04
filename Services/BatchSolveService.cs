using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StarFix.Models;

namespace StarFix.Services;

/// <summary>Runs a batch through one persistent BatchSolverSession instead of spawning
/// solve.exe fresh per file — keeps the Gaia catalog cache warm across a same-target batch.
/// Still one file at a time, no parallelism (plate-solving is CPU-heavy per solve via
/// astroalign's matching, and the underlying process protocol is strictly sequential anyway).
///
/// NOTE: this originally also (a) narrowed the catalog search radius after the first
/// successful solve, and (b) carried the previous file's winning FWHM/match-cap forward as a
/// hint for the next. Both were rolled back after real-world testing — see solve.py's
/// run_server() docstring for (b), and git history / plate_solver.md for (a). Both failure
/// modes share the same root cause: the test data used to validate them was identical copies
/// of one file, which by construction can never expose "a guess from file N is actively wrong
/// for file N+1" — the exact scenario that made real batches on real (non-identical) frames
/// slower, not faster. Only the persistent-process change remains, since it has no equivalent
/// downside: it can only save time (skipping process/catalog reload) or be neutral, never cost
/// more than the one-shot-per-file approach did.</summary>
public record BatchSolveSummary(int Solved, int Failed, int Skipped, double? MeanRmsPixels);

public static class BatchSolveService
{
    public static async Task<BatchSolveSummary> RunAsync(
        IReadOnlyList<string> filePaths, double radiusDeg, bool overwriteExisting,
        string? catalogDir, IProgress<string> progress, Action<SolveOutcome>? onResult, CancellationToken ct)
    {
        using var session = new BatchSolverSession();
        session.Start(catalogDir);

        int solved = 0, failed = 0, skipped = 0;
        double rmsSum = 0;

        try
        {
            for (int i = 0; i < filePaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var path = filePaths[i];
                var name = Path.GetFileName(path);

                // Covers both overwrite mode (source's own PLTSOLVD header) and new-file mode
                // (a "_solved_N" copy already exists alongside the untouched source) — checking
                // only the header missed new-file mode entirely, confirmed against a real batch
                // that kept re-solving the same sources into new numbered copies every run.
                if (AlreadySolvedService.IsAlreadySolved(path))
                {
                    skipped++;
                    progress.Report($"[{i + 1}/{filePaths.Count}] {name} — already solved, skipping");
                    continue;
                }

                FitsHeaderService.FitsHeader? header = null;
                try
                {
                    header = FitsHeaderService.Read(path);
                }
                catch (Exception ex)
                {
                    progress.Report($"  ⚠  Could not read header from {name}: {ex.Message}");
                }

                progress.Report($"[{i + 1}/{filePaths.Count}] Solving {name}…");
                double? ra = header?.GetDouble("RA");
                double? dec = header?.GetDouble("DEC");

                var outcome = await session.SolveOneAsync(path, ra, dec, radiusDeg, overwriteExisting, ct);

                if (outcome.Success)
                {
                    solved++;
                    rmsSum += outcome.Result?.RmsPixels ?? 0;
                    progress.Report($"  ✓  {name} — {outcome.Result?.NumMatched}/{outcome.Result?.NumDetected} matched, " +
                                     $"RMS {outcome.Result?.RmsPixels:F2}px");
                }
                else
                {
                    failed++;
                    progress.Report($"  ✗  {name} — {outcome.ErrorMessage}");
                }

                onResult?.Invoke(outcome);
            }
        }
        finally
        {
            await session.StopAsync();
        }

        double? meanRms = solved > 0 ? rmsSum / solved : null;
        var meanRmsText = meanRms.HasValue ? $", mean RMS {meanRms:F2}px" : "";
        progress.Report($"Batch complete — {solved} solved, {failed} failed, {skipped} already solved (skipped){meanRmsText}.");
        return new BatchSolveSummary(solved, failed, skipped, meanRms);
    }
}
