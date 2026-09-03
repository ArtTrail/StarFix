using System.Threading.Tasks;
using Avalonia.Controls;

namespace StarFix.Views;

/// <summary>Generic Cancel/Continue confirmation popup — Avalonia has no built-in MessageBox
/// equivalent, and this is the first place StarFix needs a plain yes/no prompt (Batch Solve
/// warning about already-solved files in the list), so it's written as a small reusable
/// dialog rather than a one-off window.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close(false);
        ContinueButton.Click += (_, _) => Close(true);
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message,
        string cancelText = "Cancel", string continueText = "Continue")
    {
        var dlg = new ConfirmDialog
        {
            Title = title,
        };
        dlg.MessageText.Text = message;
        dlg.CancelButton.Content = cancelText;
        dlg.ContinueButton.Content = continueText;
        return dlg.ShowDialog<bool>(owner);
    }
}
