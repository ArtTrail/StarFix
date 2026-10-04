using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StarFix.ViewModels;

namespace StarFix.Views;

public partial class BatchSolveWindow : Window
{
    public BatchSolveWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += (_, _) =>
        {
            if (DataContext is BatchSolveViewModel vm)
            {
                vm.FolderPickerFunc = BrowseFolderAsync;
                vm.ConfirmAlreadySolvedFunc = ConfirmAlreadySolvedAsync;
                // Auto-scroll as new coloured log lines are appended (issue #9).
                vm.LogLines.CollectionChanged += OnLogLinesChanged;
            }
        };
    }

    // Issue #10: drag files or folders onto the window to add them to the batch list.
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = (e.DataTransfer.TryGetFiles()?.Any() ?? false) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.Path.LocalPath).ToList();
        if (paths is { Count: > 0 } && DataContext is BatchSolveViewModel vm)
            vm.AddPaths(paths);
        e.Handled = true;
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Posted rather than called inline — ScrollToEnd needs the layout pass triggered by the
        // new item to have already run, or Extent/Viewport are still stale.
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
