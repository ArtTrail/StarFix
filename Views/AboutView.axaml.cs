using Avalonia.Controls;

namespace StarFix.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppVersion.Version}";
    }
}
