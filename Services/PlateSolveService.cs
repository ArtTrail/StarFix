using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StarFix.Models;

namespace StarFix.Services;

/// <summary>Invokes the bundled solve.exe for a single FITS file. Req #3: the caller decides
/// the target path (original file for overwrite mode, a fresh auto-numbered copy for new-file
/// mode) — this service always solves whatever path it's given, for real (no --dry-run).</summary>
public static class PlateSolveService
{
    /// <summary>Req #3: decides where a solve actually writes — the original file for
    /// overwrite mode, or a fresh auto-numbered copy for new-file mode (copied here so the
    /// solver always gets a real, already-in-place target path to write into). Shared by the
    /// one-shot path below and BatchSolverSession, so overwrite/new-file semantics are never
    /// duplicated between them.</summary>
    public static string ResolveTargetPath(string sourcePath, bool overwriteExisting)
    {
        if (overwriteExisting)
            return sourcePath;

        var targetPath = OutputPathService.GetNextOutputPath(sourcePath);
        File.Copy(sourcePath, targetPath, overwrite: false);
        return targetPath;
    }

    /// <summary>After a successful overwrite-mode solve, renames the file to add a "_WCS"
    /// suffix — new-file mode already gets a visible "_solved_N" marker in its filename, but
    /// overwrite mode previously left the name completely unchanged, with no visible sign a
    /// file had been solved short of opening its header. Only applies in overwrite mode (a
    /// new-file-mode copy already has its own "_solved_N" marker and shouldn't get both) and
    /// is a no-op if the name already ends in "_WCS" (defensive — AlreadySolvedService should
    /// already prevent a file from reaching this point twice).</summary>
    public static string ApplyWcsSuffixIfNeeded(string targetPath, bool overwriteExisting)
    {
        if (!overwriteExisting) return targetPath;

        var dir  = Path.GetDirectoryName(targetPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var ext  = Path.GetExtension(targetPath);

        if (name.EndsWith("_WCS", StringComparison.OrdinalIgnoreCase))
            return targetPath;

        var newPath = Path.Combine(dir, $"{name}_WCS{ext}");
        File.Move(targetPath, newPath, overwrite: true);
        return newPath;
    }

    public static Task<SolveOutcome> SolveOneAsync(
        string sourcePath, double? ra, double? dec, double radiusDeg, bool overwriteExisting,
        string? catalogDir, CancellationToken ct) =>
        SolveOneAsync(sourcePath, ra, dec, radiusDeg, overwriteExisting, catalogDir, dryRun: false, applyWcsSuffix: true, ct);

    /// <summary>dryRun solves and reports the result without writing anything back into the
    /// file — used by astap-compat mode, which (matching ASTAP's own documented behavior)
    /// only writes the WCS into the FITS file when its own -update flag is given.
    ///
    /// applyWcsSuffix controls StarFix's own "_WCS" filename marker (see
    /// ApplyWcsSuffixIfNeeded) — astap-compat mode must pass false: real ASTAP's -update
    /// updates a file in place under its original name, and a caller that hardcodes ASTAP's
    /// CLI (the whole point of astap-compat mode) will look for that exact filename
    /// afterward. Renaming it out from under that caller would silently break the very
    /// compatibility this mode exists for.</summary>
    public static async Task<SolveOutcome> SolveOneAsync(
        string sourcePath, double? ra, double? dec, double radiusDeg, bool overwriteExisting,
        string? catalogDir, bool dryRun, bool applyWcsSuffix, CancellationToken ct)
    {
        var targetPath = ResolveTargetPath(sourcePath, overwriteExisting);

        if (!SolverRuntimeService.IsAvailable)
        {
            SessionLogService.Write($"[PlateSolve] ERROR — solver executable not found at {SolverRuntimeService.ExePath}");
            return new SolveOutcome
            {
                SourcePath = sourcePath, SolvedPath = targetPath, Success = false,
                ErrorMessage = "Solver executable not found. Reinstall StarFix or check PySolver\\solve\\.",
            };
        }

        var psi = new ProcessStartInfo
        {
            FileName               = SolverRuntimeService.ExePath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add(targetPath);
        if (ra.HasValue)
        {
            psi.ArgumentList.Add("--ra");
            psi.ArgumentList.Add(ra.Value.ToString("F6", CultureInfo.InvariantCulture));
        }
        if (dec.HasValue)
        {
            psi.ArgumentList.Add("--dec");
            psi.ArgumentList.Add(dec.Value.ToString("F6", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(radiusDeg.ToString("F4", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--json");
        if (dryRun)
            psi.ArgumentList.Add("--dry-run");

        if (!string.IsNullOrWhiteSpace(catalogDir))
            psi.EnvironmentVariables["STARFIX_GAIA_CATALOG_DIR"] = catalogDir;

        SessionLogService.Write($"[PlateSolve] Starting solve: {Path.GetFileName(targetPath)}");

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync(ct);

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        if (proc.ExitCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? $"exit code {proc.ExitCode}" : stderr.Trim();
            SessionLogService.Write($"[PlateSolve] FAILED — {Path.GetFileName(targetPath)} — {reason}");
            return new SolveOutcome
            {
                SourcePath = sourcePath, SolvedPath = targetPath, Success = false, ErrorMessage = reason,
            };
        }

        SolveResultJson? result;
        try
        {
            // solve.py --json prints exactly one JSON line to stdout.
            var jsonLine = stdout.Trim().Split('\n')[^1];
            result = JsonSerializer.Deserialize<SolveResultJson>(jsonLine);
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[PlateSolve] FAILED — could not parse solver output: {ex.Message}");
            return new SolveOutcome
            {
                SourcePath = sourcePath, SolvedPath = targetPath, Success = false,
                ErrorMessage = $"Could not parse solver output: {ex.Message}",
            };
        }

        if (!dryRun && applyWcsSuffix)
            targetPath = ApplyWcsSuffixIfNeeded(targetPath, overwriteExisting);

        SessionLogService.Write($"[PlateSolve] OK — {Path.GetFileName(targetPath)} — " +
            $"{result?.NumMatched}/{result?.NumDetected} matched, RMS {result?.RmsPixels:F2}px");

        return new SolveOutcome
        {
            SourcePath = sourcePath, SolvedPath = targetPath, Success = true, Result = result,
        };
    }
}
