using Avalonia;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StarFix.Models;
using StarFix.Services;
using StarFix.ViewModels;

namespace StarFix;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless mode (issue #2): lets another app trigger a solve without StarFix's GUI
        // ever appearing. Goes through the exact same PlateSolveService the GUI's Solve button
        // uses, reads the same Settings (catalog path, overwrite-vs-new-file), and appends to
        // the same Results history — a headless solve looks identical afterward to one run by
        // hand, it just never showed a window while doing it.
        if (args.Length > 0 && args[0] == "--solve")
        {
            RunHeadlessSolve(args);
            return;
        }

        // TEMPORARY dev self-test hook (not part of the shipped app) — exercises the real
        // PlateSolveService against the real bundled solve.exe without needing to click
        // through the live GUI. Remove once end-to-end GUI testing is otherwise verified.
        if (args.Length > 0 && args[0] == "--selftest")
        {
            SelfTest.Run(args).GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 0 && args[0] == "--selftest-catalog")
        {
            SelfTest.RunCatalogProbe().GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 1 && args[0] == "--selftest-catalog-download")
        {
            SelfTest.RunCatalogDownload(args[1]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 0 && args[0] == "--selftest-results-persistence")
        {
            SelfTest.RunResultsPersistence(args).GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 2 && args[0] == "--selftest-batch-speed")
        {
            SelfTest.RunBatchSpeedComparison(args[1], args[2], args[3]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 1 && args[0] == "--selftest-batch-cancel")
        {
            var delayMs = args.Length > 3 ? int.Parse(args[3]) : 3000;
            SelfTest.RunBatchCancel(args[1], args[2], delayMs).GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 1 && args[0] == "--selftest-browsefolder-skip")
        {
            SelfTest.RunBrowseFolderSkip(args[1]).GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Body of --solve headless mode. Exits the process directly (0 on a successful
    /// solve, 1 on a failed solve, 2 on a bad invocation) rather than returning, so a calling
    /// script/app can check the exit code without needing to also parse output.</summary>
    private static void RunHeadlessSolve(string[] args)
    {
        string? file = null;
        double? ra = null, dec = null;
        double radius = 0.5;
        bool json = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ra":
                    ra = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--dec":
                    dec = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "-r":
                case "--radius":
                    radius = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    file ??= args[i];
                    break;
            }
        }

        if (file is null)
        {
            Console.Error.WriteLine("Usage: StarFix.exe --solve <file> [--ra <deg>] [--dec <deg>] [-r <radius>] [--json]");
            Environment.Exit(2);
            return;
        }

        SessionLogService.Initialize($"v{AppVersion.Version} (headless)");
        SessionLogService.Write($"[Headless] Solve requested: {file}");

        var cfg = ConfigService.Load();
        if (string.IsNullOrWhiteSpace(cfg.GaiaCatalogPath))
        {
            cfg.GaiaCatalogPath = Path.Combine(ConfigService.AppDataDir, "gaia_catalog");
            ConfigService.Save(cfg);
        }

        var outcome = PlateSolveService
            .SolveOneAsync(file, ra, dec, radius, cfg.OverwriteExisting, cfg.GaiaCatalogPath, default)
            .GetAwaiter().GetResult();

        // Same as ResultsViewModel.Add — a headless solve shows up in the GUI's Results panel
        // (and persists across restarts) exactly like one run by hand.
        var entries = ResultsHistoryService.Load();
        entries.Insert(0, new SolveResultEntry
        {
            FileName = Path.GetFileName(outcome.SolvedPath),
            Success = outcome.Success,
            ErrorMessage = outcome.ErrorMessage,
            Result = outcome.Result,
            CompletedAt = outcome.CompletedAt,
        });
        ResultsHistoryService.Save(entries);

        if (json)
        {
            string payload = outcome.Success
                ? JsonSerializer.Serialize(new
                {
                    ok = true,
                    solved_path = outcome.SolvedPath,
                    summary = outcome.Result?.Summary,
                    text = outcome.Result?.Text,
                    num_detected = outcome.Result?.NumDetected,
                    num_catalog = outcome.Result?.NumCatalog,
                    num_matched = outcome.Result?.NumMatched,
                    rms_pixels = outcome.Result?.RmsPixels,
                    fwhm_used = outcome.Result?.FwhmUsed,
                    match_cap_used = outcome.Result?.MatchCapUsed,
                })
                : JsonSerializer.Serialize(new { ok = false, error = outcome.ErrorMessage, source_path = outcome.SourcePath });
            Console.WriteLine(payload);
        }
        else
        {
            Console.WriteLine(outcome.Success ? outcome.Result?.Text ?? "Solved." : $"FAILED — {outcome.ErrorMessage}");
        }

        SessionLogService.Write($"[Headless] {(outcome.Success ? "OK" : "FAILED")} — {file}");
        Environment.Exit(outcome.Success ? 0 : 1);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
