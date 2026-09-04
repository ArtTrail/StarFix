using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace StarFix.Views;

public partial class UserGuideView : UserControl
{
    private readonly List<TextBlock> _matches = new();
    private int _matchIndex = -1;
    private string _lastQuery = "";

    public UserGuideView()
    {
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunFind();
    }

    private void FindNext_Click(object? sender, RoutedEventArgs e) => RunFind();

    private void SearchClear_Click(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _matches.Clear();
        _matchIndex = -1;
        _lastQuery = "";
        SearchStatus.Text = "";
    }

    private void RunFind()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            SearchStatus.Text = "";
            return;
        }

        if (!query.Equals(_lastQuery, StringComparison.OrdinalIgnoreCase))
        {
            _matches.Clear();
            _matchIndex = -1;
            _lastQuery = query;
            CollectMatches(ContentPanel, query, _matches);
        }

        if (_matches.Count == 0)
        {
            SearchStatus.Text = "No matches";
            return;
        }

        _matchIndex = (_matchIndex + 1) % _matches.Count;
        _matches[_matchIndex].BringIntoView();
        SearchStatus.Text = $"{_matchIndex + 1} / {_matches.Count}";
    }

    private static void CollectMatches(ILogical parent, string query, List<TextBlock> results)
    {
        foreach (var child in parent.LogicalChildren)
        {
            if (child is TextBlock tb &&
                tb.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                results.Add(tb);
            CollectMatches(child, query, results);
        }
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
    private void GoToSection10(object? sender, RoutedEventArgs e) => ScrollToSection(Section10);

    private void OnBackToTopClick(object? sender, RoutedEventArgs e) =>
        MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, 0);
}
