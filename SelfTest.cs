using System;
using System.Threading.Tasks;
using StarFix.Models;
using StarFix.Services;
using StarFix.ViewModels;

namespace StarFix;

/// <summary>Dev-only verification hook (Program.cs dispatches to this before Avalonia starts
/// when the first arg is --selftest). Exercises the real PlateSolveService/SolverRuntimeService
/// against the real bundled solve.exe without a GUI — useful since live-clicking through the
/// desktop app isn't something this environment does. Not part of the shipped feature set.
/// Usage: StarFix.exe --selftest &lt;fits_path&gt; &lt;ra&gt; &lt;dec&gt; &lt;radius_deg&gt; &lt;catalog_dir&gt; [--overwrite]</summary>
public static class SelfTest
{
    public static async Task Run(string[] args)
    {
        var path = args[1];
        var ra = double.Parse(args[2]);
        var dec = double.Parse(args[3]);
        var radius = double.Parse(args[4]);
        var catalogDir = args.Length > 5 ? args[5] : null;
        var overwrite = args.Length > 6 && args[6] == "--overwrite";

        Console.WriteLine($"Solver exe: {SolverRuntimeService.ExePath}");
        Console.WriteLine($"Available: {SolverRuntimeService.IsAvailable}");
        Console.WriteLine($"Solving: {path} (overwrite={overwrite})");

        var outcome = await PlateSolveService.SolveOneAsync(path, ra, dec, radius, overwrite, catalogDir, default);

        Console.WriteLine($"Success: {outcome.Success}");
        Console.WriteLine($"SolvedPath: {outcome.SolvedPath}");
        if (!outcome.Success)
        {
            Console.WriteLine($"Error: {outcome.ErrorMessage}");
            return;
        }

        var s = outcome.Result!.Summary;
        Console.WriteLine($"Center: {s.CenterRaHms} {s.CenterDecDms}");
        Console.WriteLine($"Pixel scale: {s.PixelScaleArcsec:F4} arcsec/px");
        Console.WriteLine($"Matched: {outcome.Result.NumMatched}/{outcome.Result.NumDetected}");
        Console.WriteLine($"RMS: {outcome.Result.RmsPixels:F2} px");
    }

    public static async Task RunCatalogProbe()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var assets = await GaiaCatalogService.EnumerateAssetsAsync(default);
            sw.Stop();
            long total = 0;
            foreach (var a in assets) total += a.Size;
            Console.WriteLine($"OK in {sw.Elapsed.TotalSeconds:F2}s — {assets.Count} assets, {total / 1_073_741_824.0:F2} GB");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"FAILED after {sw.Elapsed.TotalSeconds:F2}s — {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static async Task RunResultsPersistence(string[] args)
    {
        var path = args[1];
        var ra = double.Parse(args[2]);
        var dec = double.Parse(args[3]);
        var radius = double.Parse(args[4]);
        var catalogDir = args[5];

        var cfg = new AppConfig { GaiaCatalogPath = catalogDir };

        var results1 = new ResultsViewModel();
        Console.WriteLine($"[session 1] loaded {results1.Entries.Count} entries from disk at startup");

        var solve = new SolveViewModel(cfg) { FilePath = path, RaText = ra.ToString(), DecText = dec.ToString(), RadiusDeg = radius };
        solve.RefreshCatalogStatus();
        solve.OnSolved = results1.Add;
        solve.OnJobStarting = results1.Clear;
        await solve.SolveCommand.ExecuteAsync(null);
        Console.WriteLine($"[session 1] after one solve: {results1.Entries.Count} entries, status: {solve.StatusText}");

        // Second solve of the SAME entries list — OnJobStarting should clear the first
        // result before the second is added, so the count stays at 1, not 2.
        solve.FilePath = path; // re-trigger a fresh solve on the same file
        await solve.SolveCommand.ExecuteAsync(null);
        Console.WriteLine($"[session 1] after a second solve (auto-clear expected): {results1.Entries.Count} entries " +
                           $"(should be 1, not 2) — newest: {(results1.Entries.Count > 0 ? results1.Entries[0].FileName : "(none)")}");

        // Simulate an app restart: a brand new ResultsViewModel, same disk file.
        var results2 = new ResultsViewModel();
        Console.WriteLine($"[session 2, fresh instance] loaded {results2.Entries.Count} entries from disk");
        if (results2.Entries.Count > 0)
            Console.WriteLine($"  newest entry: {results2.Entries[0].FileName} at {results2.Entries[0].TimestampText}");
    }

    /// <summary>Compares the old one-shot-process-per-file approach against the new
    /// BatchSolverSession approach on the same real multi-file, same-target dataset — same
    /// verification the plan called for: results should match (no correctness regression from
    /// hint-carrying/radius-narrowing), and total wall time should drop.</summary>
    public static async Task RunBatchSpeedComparison(string oldDir, string newDir, string catalogDir)
    {
        var oldFiles = System.IO.Directory.GetFiles(oldDir, "*.FITS");
        var newFiles = System.IO.Directory.GetFiles(newDir, "*.FITS");
        Console.WriteLine($"Old-approach dir: {oldFiles.Length} files. New-approach dir: {newFiles.Length} files.");

        // OLD: one process per file (what BatchSolveService did before this change).
        var swOld = System.Diagnostics.Stopwatch.StartNew();
        var oldResults = new System.Collections.Generic.List<(string name, bool ok, int matched, int detected, double rms)>();
        foreach (var f in oldFiles)
        {
            var outcome = await PlateSolveService.SolveOneAsync(f, null, null, 0.5, overwriteExisting: true, catalogDir, default);
            oldResults.Add((System.IO.Path.GetFileName(f), outcome.Success,
                outcome.Result?.NumMatched ?? -1, outcome.Result?.NumDetected ?? -1, outcome.Result?.RmsPixels ?? -1));
        }
        swOld.Stop();
        Console.WriteLine($"\nOLD (one process per file): {swOld.Elapsed.TotalSeconds:F1}s total for {oldFiles.Length} files");
        foreach (var r in oldResults)
            Console.WriteLine($"  {r.name}: ok={r.ok} matched={r.matched}/{r.detected} rms={r.rms:F2}");

        // NEW: one persistent session for the whole batch, via the real BatchSolveService.
        var swNew = System.Diagnostics.Stopwatch.StartNew();
        var newResults = new System.Collections.Generic.List<(string name, bool ok, int matched, int detected, double rms)>();
        var progress = new Progress<string>(s => Console.WriteLine($"    {s}"));
        Action<StarFix.Models.SolveOutcome> onResult = outcome => newResults.Add(
            (System.IO.Path.GetFileName(outcome.SolvedPath), outcome.Success,
             outcome.Result?.NumMatched ?? -1, outcome.Result?.NumDetected ?? -1, outcome.Result?.RmsPixels ?? -1));
        await BatchSolveService.RunAsync(newFiles, 0.5, overwriteExisting: true, catalogDir, progress, onResult, default);
        swNew.Stop();
        Console.WriteLine($"\nNEW (persistent session): {swNew.Elapsed.TotalSeconds:F1}s total for {newFiles.Length} files");
        foreach (var r in newResults)
            Console.WriteLine($"  {r.name}: ok={r.ok} matched={r.matched}/{r.detected} rms={r.rms:F2}");

        Console.WriteLine($"\nSpeedup: {swOld.Elapsed.TotalSeconds / swNew.Elapsed.TotalSeconds:F2}x");
    }

    /// <summary>Confirms cancelling mid-batch actually tears down the persistent solve.exe
    /// process (BatchSolverSession.StopAsync's finally in BatchSolveService.RunAsync) rather
    /// than leaking it.</summary>
    public static async Task RunBatchCancel(string dir, string catalogDir, int delayMs = 3000)
    {
        var files = System.IO.Directory.GetFiles(dir, "*.fits");
        Console.WriteLine($"{files.Length} files; cancelling after {delayMs}ms...");

        var cts = new System.Threading.CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<string>(s => Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:F1}s] {s}"));
        _ = Task.Delay(delayMs).ContinueWith(_ => cts.Cancel());

        try
        {
            await BatchSolveService.RunAsync(files, 1.5, overwriteExisting: true, catalogDir, progress, null, cts.Token);
            Console.WriteLine("Batch finished before cancel took effect.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Cancelled as expected, {sw.ElapsedMilliseconds}ms after starting.");
        }

        await Task.Delay(500); // let any teardown finish
        var procs = System.Diagnostics.Process.GetProcessesByName("solve");
        Console.WriteLine($"Orphaned solve.exe processes after cancel: {procs.Length}");
        foreach (var p in procs) p.Dispose();
    }

    public static async Task RunBrowseFolderSkip(string dir)
    {
        var vm = new BatchSolveViewModel(new AppConfig());
        vm.FolderPickerFunc = () => Task.FromResult<string?>(dir);
        await vm.BrowseFolderCommand.ExecuteAsync(null);
        Console.WriteLine($"Status: {vm.Status}");
        Console.WriteLine("Files listed:");
        foreach (var line in vm.FilePathsText.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine($"  {line.Trim()}");
    }

    public static async Task RunCatalogDownload(string destDir)
    {
        SessionLogService.Initialize("selftest");
        var cfg = new StarFix.Models.AppConfig { GaiaCatalogPath = destDir };
        var vm = new StarFix.ViewModels.GaiaCatalogDownloadViewModel(cfg);
        await vm.StartAsync();
        Console.WriteLine($"After StartAsync: IsConfirmVisible={vm.IsConfirmVisible} IsError={vm.IsError} Headline={vm.HeadlineText}");
        if (vm.IsConfirmVisible)
        {
            await vm.ConfirmCommand.ExecuteAsync(null);
            Console.WriteLine($"After Confirm: IsDone={vm.IsDone} Status={vm.StatusText}");
        }
        Console.WriteLine();
        Console.WriteLine("--- diagnostics log ---");
        Console.WriteLine(SessionLogService.ReadAll());
    }
}
