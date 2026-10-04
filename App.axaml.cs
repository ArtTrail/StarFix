using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StarFix.Services;
using StarFix.ViewModels;

namespace StarFix;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SessionLogService.Initialize($"v{AppVersion.Version}");

            var mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
            desktop.MainWindow = mainWindow;

            // Issue #8: clear the persisted results panel on normal exit so a fresh launch
            // starts empty instead of reloading the previous session's solves.
            desktop.ShutdownRequested += (_, _) => ResultsHistoryService.Clear();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
