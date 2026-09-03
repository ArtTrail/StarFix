using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StarFix.ViewModels;

namespace StarFix.Views;

public partial class SolveView : UserControl
{
    public SolveView()
    {
        InitializeComponent();
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
