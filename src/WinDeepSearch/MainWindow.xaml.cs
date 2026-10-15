using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinDeepSearch.Core;

namespace WinDeepSearch;

/// <summary>A tab on the Search page: one search, with what its header shows.</summary>
public sealed class SearchTab : INotifyPropertyChanged
{
    bool _canClose;

    public SearchTab(SearchView view)
    {
        View = view;
        view.StatusChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public SearchView View { get; }
    public bool HasQuery => View.QueryText.Length > 0;
    public string Label => HasQuery ? View.QueryText : "New search";
    public string Outcome => View.Outcome;
    public bool Running => View.IsRunning;
    public string CloseName => "Close tab " + Label;

    public bool CanClose
    {
        get => _canClose;
        set
        {
            _canClose = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanClose)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Outcome.Length > 0 ? $"{Label} · {Outcome}" : Label;
}

/// <summary>
/// Every WinDeepSearch window: Search (in tabs), History and Settings. Opened from the Start menu it searches any
/// folder (C:\ by default); from the right-click menu, what was clicked or selected.
/// </summary>
public partial class MainWindow : Window
{
    readonly ObservableCollection<SearchTab> _tabs = [];

    public MainWindow(SearchTarget? target = null)
    {
        InitializeComponent();
        Tabs.ItemsSource = _tabs;
        target ??= SearchTarget.App;
        AddTab(target);
        ShowTitle(target);
        Nav.SelectedItem = NavSearch;
        // The searches of every tab end with the window.
        Closed += (_, _) =>
        {
            foreach (var tab in _tabs) tab.View.Stop();
        };
    }

    SearchTab Current => (SearchTab)Tabs.SelectedItem;

    SearchTab AddTab(SearchTarget target)
    {
        var view = new SearchView();
        view.Show(target);
        var tab = new SearchTab(view);
        _tabs.Add(tab);
        foreach (var t in _tabs) t.CanClose = _tabs.Count > 1;
        Tabs.SelectedItem = tab;
        return tab;
    }

    void NewTab()
    {
        // The same folder, file or selection, and the same "Just this selected file".
        var current = Current.View;
        var tab = AddTab(current.Target);
        tab.View.JustThisFile.IsChecked = current.JustThisFile.IsChecked;
    }

    void CloseTab(SearchTab tab)
    {
        if (_tabs.Count < 2) return;
        tab.View.Stop();
        int index = _tabs.IndexOf(tab);
        bool shown = tab == Current;
        _tabs.Remove(tab);
        foreach (var t in _tabs) t.CanClose = _tabs.Count > 1;
        if (shown) Tabs.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)];
    }

    void ShowTitle(SearchTarget target)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(target.SelectedFile ?? target.Root));
        Title = target.FromApp ? "WinDeepSearch"
            : "WinDeepSearch — " + (target.Items != null ? $"{target.Items.Count:N0} selected items" : name.Length > 0 ? name : target.Root);
    }

    /// <summary>Adds an item selected together with the ones this window was opened for (see <see cref="Selection"/>).</summary>
    public void AddItem(string path)
    {
        var view = _tabs[0].View;
        var target = view.Target;
        target = SearchTarget.FromPaths([.. target.Items ?? [target.SelectedFile ?? target.Root], path]);
        view.Show(target);
        ShowTitle(target);
    }

    /// <summary>Shows a search from History in a new tab, with the results it found back then.</summary>
    public void OpenSaved(HistorySummary summary, IReadOnlyList<FileResult> results)
    {
        AddTab(SearchTarget.Of(summary)).View.ShowSaved(summary, results);
        Nav.SelectedItem = NavSearch;
    }

    /// <summary>Runs a search from History again in a new tab.</summary>
    public void RunAgain(HistorySummary summary)
    {
        var tab = AddTab(SearchTarget.Of(summary));
        Nav.SelectedItem = NavSearch;
        _ = tab.View.RunAgainAsync(summary);
    }

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is not SearchTab tab)
        {
            // Ctrl+click unselects a list item; a tab stays shown until another one is picked or it is closed.
            if (e.RemovedItems is [SearchTab shown] && _tabs.Contains(shown))
                Dispatcher.BeginInvoke(() => Tabs.SelectedItem = shown);
            return;
        }
        TabContent.Content = tab.View;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => tab.View.QueryBox.Focus());
    }

    void NewTab_Click(object sender, RoutedEventArgs e) => NewTab();

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SearchTab tab) CloseTab(tab);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || SearchPage.Visibility != Visibility.Visible) return;
        if (e.Key == Key.T) NewTab();
        else if (e.Key == Key.W) CloseTab(Current);
        else return;
        e.Handled = true;
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SearchPage.Visibility = Nav.SelectedItem == NavSearch ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = Nav.SelectedItem == NavHistory ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = Nav.SelectedItem == NavSettings ? Visibility.Visible : Visibility.Collapsed;
        if (Nav.SelectedItem == NavHistory) HistoryPage.Refresh();
        if (Nav.SelectedItem == NavSettings) SettingsPage.Refresh();
    }
}
