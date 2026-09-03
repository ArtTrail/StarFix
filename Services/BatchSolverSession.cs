using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StarFix.Models;

namespace StarFix.Services;

/// <summary>Keeps one `solve.exe --server` process alive across an entire batch run instead of
/// relaunching the solver per file. This is what lets gaia_catalog_lookup.py's module-level
/// catalog cache actually pay off across a same-target batch — it would be wasted every file if
/// each one spawned a fresh process with no memory of the last one. Requests are strictly
/// sequential (one file solved at a time, matching the batch's existing serial-by-design
/// execution), so no concurrency beyond a simple in-flight guard is needed.</summary>
public class BatchSolverSession : IDisposable
{
    private Process? _process;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Start(string? catalogDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = SolverRuntimeService.ExePath,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("--server");
        if (!string.IsNullOrWhiteSpace(catalogDir))
            psi.EnvironmentVariables["STARFIX_GAIA_CATALOG_DIR"] = catalogDir;

        _process = new Process { StartInfo = psi };
        _process.Start();

        // Drain stderr in the background — otherwise a full pipe buffer can deadlock the
        // process, and any Python-side warning/traceback should still land in the diagnostics
        // log even though normal per-file errors travel back as {"ok": false, ...} on stdout.
        var proc = _process;
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    SessionLogService.Write($"[BatchSolverSession] stderr: {line}");
            }
            catch { /* process torn down, nothing left to drain */ }
        });

        SessionLogService.Write("[BatchSolverSession] Started persistent solver process.");
    }

    public async Task<SolveOutcome> SolveOneAsync(
        string sourcePath, double? ra, double? dec, double radiusDeg, bool overwriteExisting, CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
            throw new InvalidOperationException("BatchSolverSession is not running — call Start() first.");

        ct.ThrowIfCancellationRequested();
        var targetPath = PlateSolveService.ResolveTargetPath(sourcePath, overwriteExisting);

        await _lock.WaitAsync(ct);
        try
        {
            var request = new { file = targetPath, ra, dec, radius = radiusDeg };
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
            await _process.StandardInput.FlushAsync();

            // A solve can legitimately run for a while (solve.py's own internal budget allows
            // up to ~180s on a hard field), and there's no in-band way to ask the Python side
            // to abort just this one in-flight request — cooperative cancellation would need
            // protocol support that doesn't exist. Without observing `ct` here at all, clicking
            // Cancel while a file is mid-solve did nothing until that file happened to finish on
            // its own (confirmed — a real, reported bug). So: register a kill on the token: if
            // the user cancels mid-request, the whole process (and therefore the whole session)
            // is torn down immediately, same as a full-batch cancel is supposed to do.
            string? responseLine;
            using (ct.Register(static state => { try { ((Process)state!).Kill(entireProcessTree: true); } catch { } }, _process))
            {
                responseLine = await _process.StandardOutput.ReadLineAsync();
            }
            ct.ThrowIfCancellationRequested();

            if (responseLine is null)
            {
                var reason = "Solver process closed its output unexpectedly (it may have crashed).";
                SessionLogService.Write($"[BatchSolverSession] FAILED — {Path.GetFileName(targetPath)} — {reason}");
                return new SolveOutcome { SourcePath = sourcePath, SolvedPath = targetPath, Success = false, ErrorMessage = reason };
            }

            using var doc = JsonDocument.Parse(responseLine);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();

            if (!ok)
            {
                var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown error";
                SessionLogService.Write($"[BatchSolverSession] FAILED — {Path.GetFileName(targetPath)} — {error}");
                return new SolveOutcome { SourcePath = sourcePath, SolvedPath = targetPath, Success = false, ErrorMessage = error };
            }

            var result = JsonSerializer.Deserialize<SolveResultJson>(responseLine);
            targetPath = PlateSolveService.ApplyWcsSuffixIfNeeded(targetPath, overwriteExisting);
            SessionLogService.Write($"[BatchSolverSession] OK — {Path.GetFileName(targetPath)} — " +
                $"{result?.NumMatched}/{result?.NumDetected} matched, RMS {result?.RmsPixels:F2}px");
            return new SolveOutcome { SourcePath = sourcePath, SolvedPath = targetPath, Success = true, Result = result };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync()
    {
        var proc = _process;
        if (proc is null) return;
        _process = null;

        try
        {
            if (!proc.HasExited)
            {
                proc.StandardInput.Close(); // EOF on stdin — run_server()'s `for line in sys.stdin` exits cleanly
                var exitTask = proc.WaitForExitAsync();
                if (await Task.WhenAny(exitTask, Task.Delay(3000)) != exitTask)
                    proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* best-effort shutdown */ }
        finally
        {
            SessionLogService.Write("[BatchSolverSession] Stopped.");
            proc.Dispose();
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
