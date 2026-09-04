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

        // ASTAP-compatible mode: lets third-party software that hardcodes calls to
        // astap_cli (N.I.N.A. and similar capture tools) use StarFix as a drop-in
        // replacement, without that software needing to know StarFix exists. Kept
        // deliberately separate from --solve above rather than blended into it — the two
        // use different unit conventions (RA in hours vs. degrees, south-polar-distance
        // vs. declination) that would be easy to mix up on one shared command surface.
        // Your own apps (TransitLab, etc.) should call --solve directly instead; this mode
        // exists only to imitate a tool you don't control the source of.
        if (args.Length > 0 && args[0] == "--astap-compat")
        {
            RunAstapCompat(args);
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

    /// <summary>Body of --astap-compat mode. Recognizes ASTAP's own real flags (confirmed
    /// against an actual astap_cli invocation's recorded CMDLINE from earlier testing, plus
    /// a third-party astap_cli README): -f (file), -ra (hours), -spd (south-polar-distance,
    /// i.e. dec+90, degrees), -r (search radius, degrees), -update (write the WCS into the
    /// file — without it, ASTAP only solves and reports, it doesn't touch the file). Any
    /// other ASTAP flag (-fov, -z, -s, -t, -d, ...) is accepted and ignored — best-effort:
    /// StarFix doesn't have an equivalent tuning knob for most of them, and a caller passing
    /// one shouldn't cause a hard failure over it.
    ///
    /// This hasn't been tested against a real copy of N.I.N.A. or another real caller —
    /// only against the recorded real astap_cli invocation from earlier project testing and
    /// third-party documentation of the flag set, since ASTAP's own official CLI reference
    /// page didn't yield its full detail through automated fetching.</summary>
    private static void RunAstapCompat(string[] args)
    {
        string? file = null;
        double? raHours = null, spdDeg = null;
        double radius = 0.5;
        bool update = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-f":
                    file = args[++i];
                    break;
                case "-ra":
                    raHours = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "-spd":
                    spdDeg = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "-r":
                    radius = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "-update":
                    update = true;
                    break;
                default:
                    // Unrecognized ASTAP flag — best-effort skip its value too, so an
                    // unsupported "-fov 2" doesn't make "2" get misread as the file path.
                    if (args[i].StartsWith('-') && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        i++;
                    break;
            }
        }

        if (file is null)
        {
            Console.Error.WriteLine("Usage: StarFix.exe --astap-compat -f <file> [-ra <hours>] [-spd <deg>] [-r <radius_deg>] [-update]");
            Environment.Exit(2);
            return;
        }

        // -ra is hours (x15 to degrees); -spd is south-polar-distance, i.e. dec+90. Neither
        // given means "read it from the file's own header instead" — solve.exe already does
        // exactly that fallback (and already handles .fz files correctly there too), so
        // passing null through and letting it happen there avoids duplicating that logic.
        double? raDeg = raHours * 15.0;
        double? decDeg = spdDeg - 90.0;

        SessionLogService.Initialize($"v{AppVersion.Version} (astap-compat)");
        SessionLogService.Write($"[AstapCompat] Solve requested: {file} (update={update})");

        var cfg = ConfigService.Load();
        if (string.IsNullOrWhiteSpace(cfg.GaiaCatalogPath))
        {
            cfg.GaiaCatalogPath = Path.Combine(ConfigService.AppDataDir, "gaia_catalog");
            ConfigService.Save(cfg);
        }

        var outcome = PlateSolveService
            .SolveOneAsync(file, raDeg, decDeg, radius, overwriteExisting: true, cfg.GaiaCatalogPath,
                dryRun: !update, applyWcsSuffix: false, default)
            .GetAwaiter().GetResult();

        if (outcome.Success)
        {
            WriteAstapIniSidecar(file, outcome, args);
            var entries = ResultsHistoryService.Load();
            entries.Insert(0, new SolveResultEntry
            {
                FileName = Path.GetFileName(file),
                Success = true,
                Result = outcome.Result,
                CompletedAt = outcome.CompletedAt,
            });
            ResultsHistoryService.Save(entries);
        }

        SessionLogService.Write($"[AstapCompat] {(outcome.Success ? "OK" : "FAILED")} — {file}" +
            (outcome.Success ? "" : $" — {outcome.ErrorMessage}"));
        Console.WriteLine(outcome.Success ? "Solution found." : $"No solution: {outcome.ErrorMessage}");
        Environment.Exit(outcome.Success ? 0 : 1);
    }

    /// <summary>Writes the same .ini sidecar ASTAP itself writes next to a solved file — this
    /// is the real thing calling software checks for (confirmed from a recorded real ASTAP
    /// run: PLTSOLVD/CRPIX/CRVAL/CDELT/CROTA/CD-matrix key=value lines, plus a CMDLINE record
    /// of the invocation), not anything printed to stdout.</summary>
    private static void WriteAstapIniSidecar(string filePath, SolveOutcome outcome, string[] originalArgs)
    {
        var s = outcome.Result?.Summary;
        if (s is null) return;

        var ic = CultureInfo.InvariantCulture;
        string E(double v) => v.ToString("E16", ic);

        var cmdline = $"\"{Environment.ProcessPath}\" {string.Join(' ', originalArgs)}";
        var lines = new[]
        {
            "PLTSOLVD=T",
            $"CRPIX1= {E(s.Crpix1)}",
            $"CRPIX2= {E(s.Crpix2)}",
            $"CRVAL1= {E(s.CenterRaDeg)}",
            $"CRVAL2= {E(s.CenterDecDeg)}",
            $"CDELT1= {E(s.PixelScaleXArcsec / 3600.0)}",
            $"CDELT2= {E(s.PixelScaleYArcsec / 3600.0)}",
            $"CROTA1= {E(s.RotationDeg)}",
            $"CROTA2= {E(s.RotationDeg)}",
            $"CD1_1= {E(s.Cd1_1)}",
            $"CD1_2= {E(s.Cd1_2)}",
            $"CD2_1= {E(s.Cd2_1)}",
            $"CD2_2= {E(s.Cd2_2)}",
            $"CMDLINE=\"{cmdline}\"",
        };

        var iniPath = Path.ChangeExtension(filePath, ".ini");
        File.WriteAllLines(iniPath, lines);
        SessionLogService.Write($"[AstapCompat] Wrote {Path.GetFileName(iniPath)}");
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
