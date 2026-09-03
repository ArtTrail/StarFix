using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StarFix.ViewModels;

namespace StarFix.Views;

public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ResultsViewModel vm)
            {
                vm.SaveFileFunc = SaveResultsAsync;
                vm.Entries.CollectionChanged += OnEntriesChanged;
            }
        };
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // New entries are inserted at index 0 (newest-first), so "auto-scroll as it fills up"
        // means snapping back to the top so the just-added result is immediately visible,
        // not the bottom. Posted for the same reason as BatchSolveWindow's log auto-scroll —
        // needs the layout pass from the new item to have already run.
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.UIThread.Post(() => ResultsScrollViewer.ScrollToHome());
    }

    private async Task<string?> SaveResultsAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Results",
            SuggestedFileName = $"StarFix_results_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("Text files") { Patterns = ["*.txt"] },
                new("All files")  { Patterns = ["*"] },
            }
        });
        return file?.Path.LocalPath;
    }
}
