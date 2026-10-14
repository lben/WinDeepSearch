using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinDeepSearch.Core;

namespace WinDeepSearch;

public sealed class HistoryItem(HistorySummary summary)
{
    public HistorySummary Summary { get; } = summary;
    public string Query => Summary.Query.Text;
    public string Scope => Summary.SingleFile ?? Summary.Root;

    public IReadOnlyList<string> Flags { get; } =
    [
        .. summary.Query.UseRegex ? [".*"] : Array.Empty<string>(),
        .. summary.Query.MatchCase ? ["Aa"] : Array.Empty<string>(),
        .. summary.Query.ReadImages ? ["OCR"] : Array.Empty<string>(),
    ];

    public string When => Summary.StartedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string Meta => $"{When} · {Summary.Matches:N0} {(Summary.Matches == 1 ? "match" : "matches")}";

    public string Totals
    {
        get
        {
            string names = Summary.NameMatches > 0 ? $"{Summary.NameMatches:N0} name {(Summary.NameMatches == 1 ? "match" : "matches")} · " : "";
            return $"{names}{Summary.Matches:N0} {(Summary.Matches == 1 ? "match" : "matches")} in {Summary.FilesWithMatches:N0} {(Summary.FilesWithMatches == 1 ? "file" : "files")}";
        }
    }

    public override string ToString() => $"{Query} · {Scope}";
}

/// <summary>Past searches and the results they found.</summary>
public partial class HistoryView : UserControl
{
    readonly HistoryStore _store = new(AppPaths.History);
    List<HistoryItem> _all = [];
    List<FileResult> _results = [];

    public HistoryView() => InitializeComponent();

    public void Refresh()
    {
        string? selected = (List.SelectedItem as HistoryItem)?.Summary.Id;
        _all = _store.List().Select(s => new HistoryItem(s)).ToList();
        CountText.Text = $"{_all.Count} {(_all.Count == 1 ? "search" : "searches")} saved";
        ApplyFilter();
        List.SelectedItem = _all.FirstOrDefault(i => i.Summary.Id == selected) ?? List.Items.Cast<HistoryItem>().FirstOrDefault();
    }

    void ApplyFilter()
    {
        string filter = FilterBox.Text.Trim();
        List.ItemsSource = _all.Where(i => filter.Length == 0
            || i.Query.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || i.Scope.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        EmptyText.Text = _all.Count == 0 ? "No saved searches yet." : "No saved searches match that filter.";
        EmptyText.Visibility = List.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    async void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not HistoryItem item)
        {
            Detail.Visibility = Visibility.Collapsed;
            NoSelection.Visibility = Visibility.Visible;
            return;
        }
        var s = item.Summary;
        Detail.Visibility = Visibility.Visible;
        NoSelection.Visibility = Visibility.Collapsed;
        DetailQuery.Text = s.Query.Text;
        DetailFlags.Text = string.Join(" · ",
            s.Query.UseRegex ? "Regular expression" : "Plain text",
            s.Query.MatchCase ? "Match case" : "Ignore case",
            s.Query.ReadImages ? "Text in images" : "No text in images");
        DetailScope.Text = item.Scope;
        DetailMeta.Text = $"Searched {item.When} · took {Sizes.Duration(s.Seconds)}{(s.Stopped ? " · stopped early" : "")}";
        DetailSummary.Text = item.Totals;
        DetailSkipped.Text = s.Skipped > 0 ? $"{s.Skipped} {(s.Skipped == 1 ? "file" : "files")} skipped" : "";

        SavedFiles.ItemsSource = null;
        var results = await Task.Run(() => _store.LoadResults(s.Id));
        if (List.SelectedItem != item) return;
        _results = results;
        SavedFiles.ItemsSource = results.Select((r, i) => new FileRow(r, s.Root, i)).ToList();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is HistoryItem item) SearchWindow.OpenSaved(item.Summary, _results);
    }

    void Again_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is HistoryItem item) SearchWindow.RunAgain(item.Summary);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not HistoryItem item) return;
        try
        {
            _store.Delete(item.Summary.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Already gone or in use; the refresh shows what is left.
        }
        Refresh();
    }
}
