using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace StarFix.Views;

public partial class UserGuideView : UserControl
{
    public UserGuideView()
    {
        InitializeComponent();
    }

    private void ScrollToSection(Control target)
    {
        // TranslatePoint gives the target's current on-screen position relative to the
        // ScrollViewer's own coordinate space (i.e. already relative to the current scroll
        // position) — adding that to the current offset lands the target at the top of the
        // viewport regardless of where either currently sits.
        var point = target.TranslatePoint(new Point(0, 0), MainScrollViewer);
        if (point.HasValue)
            MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, MainScrollViewer.Offset.Y + point.Value.Y);
    }

    private void GoToSection1(object? sender, RoutedEventArgs e) => ScrollToSection(Section1);
    private void GoToSection2(object? sender, RoutedEventArgs e) => ScrollToSection(Section2);
    private void GoToSection3(object? sender, RoutedEventArgs e) => ScrollToSection(Section3);
    private void GoToSection4(object? sender, RoutedEventArgs e) => ScrollToSection(Section4);
    private void GoToSection5(object? sender, RoutedEventArgs e) => ScrollToSection(Section5);
    private void GoToSection6(object? sender, RoutedEventArgs e) => ScrollToSection(Section6);
    private void GoToSection7(object? sender, RoutedEventArgs e) => ScrollToSection(Section7);
    private void GoToSection8(object? sender, RoutedEventArgs e) => ScrollToSection(Section8);
    private void GoToSection9(object? sender, RoutedEventArgs e) => ScrollToSection(Section9);

    private void OnBackToTopClick(object? sender, RoutedEventArgs e) =>
        MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, 0);
}
