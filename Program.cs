using Avalonia;
using System;
using System.Threading.Tasks;

namespace StarFix;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
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

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
