using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StarFix.ViewModels;

namespace StarFix.Views;

public partial class SolveView : UserControl
{
    // Issue #10: FITS extensions accepted for drag-and-drop (same set as the Browse dialog).
    private static readonly string[] FitsExtensions = [".fits", ".fit", ".fts", ".fz"];

    public SolveView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static bool IsFits(string path) =>
        FitsExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Allow the drop only if at least one dragged file looks like a FITS file.
        var hasFits = e.DataTransfer.TryGetFiles()?.Any(f => IsFits(f.Path.LocalPath)) ?? false;
        e.DragEffects = hasFits ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var firstFits = e.DataTransfer.TryGetFiles()?
            .Select(f => f.Path.LocalPath)
            .FirstOrDefault(IsFits);

        if (firstFits is not null && DataContext is SolveViewModel vm)
            vm.FilePath = firstFits;
        e.Handled = true;
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var results = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a FITS file",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("FITS files") { Patterns = ["*.fits", "*.fit", "*.fts", "*.fz"] },
                new("All files")  { Patterns = ["*"] },
            }
        });

        if (results.Count > 0 && DataContext is SolveViewModel vm)
            vm.FilePath = results[0].Path.LocalPath;
    }
}
