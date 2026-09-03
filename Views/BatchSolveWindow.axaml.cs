using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StarFix.ViewModels;

namespace StarFix.Views;

public partial class BatchSolveWindow : Window
{
    public BatchSolveWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is BatchSolveViewModel vm)
            {
                vm.FolderPickerFunc = BrowseFolderAsync;
                vm.ConfirmAlreadySolvedFunc = ConfirmAlreadySolvedAsync;
                vm.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BatchSolveViewModel.Log)) return;
        // Posted rather than called inline — ScrollToEnd needs the layout pass triggered by the
        // Text change to have already run, or Extent/Viewport are still stale.
        Dispatcher.UIThread.Post(() => LogScrollViewer.ScrollToEnd());
    }

    private async Task<string?> BrowseFolderAsync()
    {
        var results = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a folder of FITS files",
            AllowMultiple = false,
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    private Task<bool> ConfirmAlreadySolvedAsync(int alreadySolvedCount, int totalCount) =>
        ConfirmDialog.ShowAsync(this, "Already Solved Files Found",
            $"{alreadySolvedCount} of {totalCount} file(s) in this batch appear to already be solved. " +
            "Continue anyway (they'll be re-solved), or cancel?");
}
