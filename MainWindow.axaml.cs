using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using StarFix.Services;
using StarFix.ViewModels;
using StarFix.Views;

namespace StarFix;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"StarFix v{AppVersion.Version}";
        AttributionText.Text = $"© Art Trail 2026  ·  StarFix v{AppVersion.Version}  ·  Plate solving via Gaia DR3 + astroalign";
        // DataContext isn't set yet at this point (App.axaml.cs assigns it via object
        // initializer, which runs after this constructor body) — wire the delegate once it
        // actually arrives, same pattern as ResultsView's DataContextChanged handler.
        DataContextChanged += (_, _) =>
        {
            Vm.Solve.OnBatchSolveRequested = OpenBatchSolveWindow;
            Vm.Solve.ConfirmAlreadySolvedFunc = () => ConfirmDialog.ShowAsync(this, "Already Solved",
                "This file already appears solved. Continue anyway (it'll be re-solved), or cancel?");
            Vm.LaunchInstallerAndExit = LaunchInstallerAndExit;
            _ = Vm.RunStartupUpdateCheckAsync();
        };
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    /// <summary>Starts the downloaded installer, then shuts the app down so the installer can
    /// overwrite files StarFix currently has open — it can't do that while they're locked.
    /// Inno Setup's own upgrade handling (fixed AppId, see installer\StarFix.iss) takes it
    /// from there: an in-place upgrade over the existing per-user install, no separate
    /// uninstall step.</summary>
    private void LaunchInstallerAndExit(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[Update] Failed to launch installer: {ex}");
            return;
        }
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void OpenBatchSolveWindow()
    {
        var vm = new BatchSolveViewModel(Vm.Config) { OnResult = Vm.Results.Add, OnJobStarting = Vm.Results.Clear };
        var win = new BatchSolveWindow { DataContext = vm };
        win.Show(this);
    }

    private void OnDownloadCatalogClick(object? sender, RoutedEventArgs e)
    {
        var vm = new GaiaCatalogDownloadViewModel(Vm.Config);
        Window? win = null;
        vm.CloseCallback = () => win?.Close();
        win = new GaiaCatalogDownloadWindow { DataContext = vm };
        win.Opened += async (_, _) => await vm.StartAsync();
        win.Closed += (_, _) => Vm.Solve.RefreshCatalogStatus();
        win.Show(this);
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var vm = new SettingsViewModel(Vm.Config);
        Window? win = null;
        vm.CloseCallback = () => win?.Close();
        win = new SettingsWindow { DataContext = vm };
        win.Show(this);
    }

    private void OnDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        Window? win = null;
        var diagVm = new DiagnosticsViewModel();
        diagVm.SaveFileFunc = SaveDiagnosticsLogAsync;
        diagVm.OpenLogFileFunc = OpenPreviousLogAsync;
        diagVm.CloseCallback = () => win?.Close();

        win = new Window
        {
            Title = "Diagnostics — Session Log",
            Width = 900,
            Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DiagnosticsView { DataContext = diagVm },
        };
        win.Opened += (_, _) => diagVm.Connect();
        win.Closed += (_, _) => diagVm.Disconnect();
        win.Show(this);
    }

    private void OnUserGuideClick(object? sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title = "User Guide",
            Width = 920,
            Height = 820,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new UserGuideView(),
        };
        win.Show(this);
    }

    private void OnRevisionHistoryClick(object? sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title = "Revision History",
            Width = 800,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new RevisionHistoryView(),
        };
        win.Show(this);
    }

    private void OnBugReportClick(object? sender, RoutedEventArgs e)
    {
        Window? win = null;
        var bugVm = new BugReportViewModel();
        bugVm.CloseCallback = () => win?.Close();

        win = new Window
        {
            Title = "Submit Feedback",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new BugReportView { DataContext = bugVm },
        };
        win.Show(this);
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title = "About StarFix",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new AboutView(),
        };
        win.Show(this);
    }

    private async Task<string?> OpenPreviousLogAsync()
    {
        var logsDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StarFix", "logs");

        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Previous Session Log",
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(logsDir),
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Log files") { Patterns = ["*.log"] },
                new("All files") { Patterns = ["*"] },
            }
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private async Task<string?> SaveDiagnosticsLogAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Session Log",
            SuggestedFileName = $"StarFix_diagnostics_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("Text files") { Patterns = ["*.txt"] },
                new("All files")  { Patterns = ["*"] },
            }
        });
        return file?.Path.LocalPath;
    }
}
