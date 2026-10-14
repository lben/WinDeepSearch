using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WinDeepSearch.Core;

namespace WinDeepSearch;

/// <summary>What a search window searches: a folder, or a file and the folder it is in.</summary>
public sealed record SearchTarget(string Root, string? SelectedFile, bool FromApp)
{
    public static SearchTarget FromPath(string path)
    {
        string full = Path.GetFullPath(path);
        return File.Exists(full) ? new(Path.GetDirectoryName(full)!, full, false) : new(full, null, false);
    }

    /// <summary>The main app's Search page, where the folder is typed in (C:\ by default).</summary>
    public static readonly SearchTarget App = new(@"C:\", null, true);
}

public partial class SearchView : UserControl
{
    /// <summary>
    /// Matching lines kept across all files of one search. Past it, files keep only their first matching line,
    /// so a search that matches nearly everything on a drive can't run out of memory. Counts stay exact.
    /// </summary>
    const int MaxStoredHits = 200_000;
    const int LinesShownWhenExpanded = 4;

    static readonly Comparer<FileRow> Ranking = Comparer<FileRow>.Create((a, b) =>
        a.Result.MatchCount != b.Result.MatchCount ? b.Result.MatchCount.CompareTo(a.Result.MatchCount) : a.Sequence.CompareTo(b.Sequence));

    readonly ObservableCollection<Row> _rows = [];
    readonly List<FileRow> _named = [], _content = [];
    readonly SectionRow _namedHeader = new(), _contentHeader = new();
    readonly HashSet<FileRow> _expanded = [];
    readonly ObservableCollection<ActiveRow> _active = [];
    readonly ObservableCollection<string> _skipped = [];
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = new();

    SearchTarget _target = SearchTarget.App;
    SearchEngine? _engine;
    CancellationTokenSource? _cts;
    string _root = "";
    string? _single;
    bool _restart;
    int _sequence, _storedHits, _filesWithMatches;
    long _matches;

    public SearchView()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _rows;
        ActiveList.ItemsSource = _active;
        SkippedList.ItemsSource = _skipped;
        PanelThreads.Text = $"{Environment.ProcessorCount} threads";
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Pump(), Dispatcher) { IsEnabled = false };
        Unloaded += (_, _) => _cts?.Cancel();
    }

    bool Running => _cts != null;

    public void Show(SearchTarget target)
    {
        _target = target;
        FolderScope.Visibility = !target.FromApp && target.SelectedFile == null ? Visibility.Visible : Visibility.Collapsed;
        FolderScopePath.Text = target.Root;
        FileScope.Visibility = target.SelectedFile != null ? Visibility.Visible : Visibility.Collapsed;
        FileScopePath.Text = target.SelectedFile ?? "";
        AppScope.Visibility = target.FromApp ? Visibility.Visible : Visibility.Collapsed;
        UpdateParentScope();
        QueryBox.Focus();
    }

    /// <summary>Shows a search from history with the results it found back then.</summary>
    public void ShowSaved(HistorySummary summary, IReadOnlyList<FileResult> results)
    {
        Restore(summary);
        _root = summary.Root;
        _single = summary.SingleFile;
        foreach (var result in results) Add(result);
        SavedBanner.Visibility = Visibility.Visible;
        SavedBannerText.Text = $"Saved results from {summary.StartedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)}. Files may have changed since; press Search to run it again.";
        StatusText.Text = "Saved search";
        StatusCounts.Text = Counts();
        StatusSkipped.Text = summary.Skipped > 0 ? $"{summary.Skipped} skipped · results may be incomplete" : "";
        UpdateEmptyText(false);
    }

    /// <summary>Runs a search from history again with the same text, options and folder.</summary>
    public Task RunAgainAsync(HistorySummary summary)
    {
        Restore(summary);
        return RunAsync();
    }

    void Restore(HistorySummary summary)
    {
        Show(new SearchTarget(summary.SingleFile != null ? Path.GetDirectoryName(summary.SingleFile)! : summary.Root, summary.SingleFile, false));
        JustThisFile.IsChecked = summary.SingleFile != null;
        QueryBox.Text = summary.Query.Text;
        RegexToggle.IsChecked = summary.Query.UseRegex;
        CaseToggle.IsChecked = summary.Query.MatchCase;
        ImagesToggle.IsChecked = summary.Query.ReadImages;
    }

    public async Task RunAsync()
    {
        var query = new SearchQuery(QueryBox.Text, RegexToggle.IsChecked == true, CaseToggle.IsChecked == true, ImagesToggle.IsChecked == true);
        string root = _target.FromApp ? ScopeBox.Text.Trim() : _target.Root;
        string? error = Matcher.Validate(query) ?? (Directory.Exists(root) ? null : $"The folder \"{root}\" doesn't exist.");
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        if (error != null) return;

        // Read now rather than at startup: the main app may have changed them since this window opened.
        var settings = Settings.Load(AppPaths.SettingsFile);
        Clear();
        _root = root;
        _single = _target.SelectedFile != null && JustThisFile.IsChecked == true ? _target.SelectedFile : null;
        ListHeading.Text = _single != null ? "Matches in " + Path.GetFileName(_single) : "Results";
        var engine = _engine = new SearchEngine(query, root, _single, settings, new ImageText(AppPaths.OcrCache));
        var cts = _cts = new CancellationTokenSource();
        SetRunning(true);
        var started = DateTimeOffset.Now;
        _clock.Restart();
        _timer.Start();

        bool stopped = false;
        try
        {
            await Task.Run(() => engine.RunAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }

        _timer.Stop();
        _clock.Stop();
        // Pick up whatever arrived after the last tick, including a file skipped right at the end.
        do Pump();
        while (!engine.Results.IsEmpty);
        _cts = null;
        cts.Dispose();
        SetRunning(false);
        Finish(engine, stopped);
        SaveHistory(query, started, stopped, settings.HistoryLimit);

        if (_restart)
        {
            _restart = false;
            await RunAsync();
        }
    }

    void Clear()
    {
        _rows.Clear();
        _named.Clear();
        _content.Clear();
        _expanded.Clear();
        _active.Clear();
        _skipped.Clear();
        _sequence = _storedHits = _filesWithMatches = 0;
        _matches = 0;
        SavedBanner.Visibility = Visibility.Collapsed;
        SkippedPanel.Visibility = Visibility.Collapsed;
        StatusCounts.Text = StatusSkipped.Text = "";
        UpdateEmptyText(true);
    }

    void SetRunning(bool running)
    {
        GoButton.Content = running ? "Stop" : "Search";
        GoButton.Style = (Style)FindResource(running ? "DefaultButtonStyle" : "AccentButtonStyle");
        OverallProgress.Visibility = Visibility.Visible;
        PanelTitle.Text = running ? "Searching now" : PanelTitle.Text;
    }

    /// <summary>Moves new results, skipped files and progress from the engine to the window.</summary>
    void Pump()
    {
        if (_engine is not { } engine) return;
        for (int added = 0; added < 2000 && engine.Results.TryDequeue(out var result); added++) Add(result);
        while (engine.Skipped.TryDequeue(out var skipped))
        {
            string reason = skipped.Reason == "Skipped" ? "stopped at " + skipped.Progress.ToString("0%", CultureInfo.InvariantCulture) : skipped.Reason;
            _skipped.Add($"{Path.GetFileName(skipped.Path)} · {reason}");
        }
        SkippedPanel.Visibility = _skipped.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkippedTitle.Text = $"Skipped · {_skipped.Count}";

        var current = engine.Active.Values.Where(a => _single != null || a.Elapsed.TotalSeconds >= 0.5).ToList();
        for (int i = _active.Count - 1; i >= 0; i--)
            if (!current.Contains(_active[i].File)) _active.RemoveAt(i);
        foreach (var file in current.OrderBy(a => a.StartedAt))
            if (!_active.Any(r => r.File == file)) _active.Add(new ActiveRow(file, _root));
        foreach (var row in _active) row.Refresh();
        PanelNote.Visibility = _active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelNote.Text = "Working through small files…";

        long done = engine.FilesDone, found = engine.FilesFound;
        OverallProgress.Value = found == 0 ? 0 : done / (double)found;
        PanelCounts.Text = $"{done:N0} of {found:N0}{(engine.Enumerating ? "+" : "")} files";
        StatusText.Text = "Searching… " + Sizes.Duration(_clock.Elapsed.TotalSeconds);
        StatusCounts.Text = Counts();
        StatusSkipped.Text = Problems(engine);
        UpdateEmptyText(true);
    }

    void Finish(SearchEngine engine, bool stopped)
    {
        _active.Clear();
        PanelTitle.Text = stopped ? "Search stopped" : "Search complete";
        PanelNote.Visibility = Visibility.Visible;
        PanelNote.Text = stopped ? "Stopped before every file was searched."
            : _skipped.Count > 0 ? "Every file was searched except the skipped ones below."
            : "Every file in scope was searched.";
        long done = engine.FilesDone;
        OverallProgress.Value = stopped && engine.FilesFound > 0 ? done / (double)engine.FilesFound : 1;
        PanelCounts.Text = $"{done:N0} of {engine.FilesFound:N0} files";
        string time = Sizes.Duration(_clock.Elapsed.TotalSeconds);
        StatusText.Text = stopped ? "Stopped after " + time : "Finished in " + time;
        StatusCounts.Text = Counts();
        StatusSkipped.Text = Problems(engine);
        UpdateEmptyText(false);
    }

    string Counts()
    {
        string names = _named.Count == 0 ? "" : $"{_named.Count:N0} name {(_named.Count == 1 ? "match" : "matches")} · ";
        return $"{names}{_matches:N0} {(_matches == 1 ? "match" : "matches")} in {_filesWithMatches:N0} {(_filesWithMatches == 1 ? "file" : "files")}";
    }

    string Problems(SearchEngine engine)
    {
        var parts = new List<string>();
        if (_skipped.Count > 0) parts.Add($"{_skipped.Count} skipped · results may be incomplete");
        if (engine.Unreadable > 0) parts.Add($"{engine.Unreadable:N0} couldn't be read");
        return string.Join(" · ", parts);
    }

    void UpdateEmptyText(bool running)
    {
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count == 0 && _engine != null) EmptyText.Text = running ? "Searching…" : "No matches.";
    }

    void Add(FileResult result)
    {
        if (_storedHits >= MaxStoredHits && result.Hits.Count > 1) result.Hits.RemoveRange(1, result.Hits.Count - 1);
        _storedHits += result.Hits.Count;
        _matches += result.MatchCount;
        if (result.MatchCount > 0) _filesWithMatches++;

        var row = new FileRow(result, _root, _sequence++);
        var list = row.IsNameMatch ? _named : _content;
        int k = list.BinarySearch(row, Ranking);
        if (k < 0) k = ~k;
        list.Insert(k, row);
        if (row.IsNameMatch && _named.Count == 1) _rows.Insert(0, _namedHeader);
        if (!row.IsNameMatch && _content.Count == 1) _rows.Add(_contentHeader);
        _rows.Insert(RowIndex(row, k), row);
        _namedHeader.Label = $"File names · {_named.Count:N0}";
        _contentHeader.Label = $"Contents · {_content.Count:N0} {(_content.Count == 1 ? "file" : "files")}";

        // Searching one file lists all of its matching lines straight away.
        if (_single != null) Expand(row, all: true);
    }

    /// <summary>Where a file goes in the list, given its rank within its section.</summary>
    int RowIndex(FileRow row, int rank)
    {
        int index = (row.IsNameMatch ? 1 : NamedBlockRows() + 1) + rank;
        foreach (var expanded in _expanded)
            if (expanded.IsNameMatch == row.IsNameMatch && Ranking.Compare(expanded, row) < 0) index += expanded.ChildRows;
        return index;
    }

    int NamedBlockRows() => _named.Count == 0 ? 0 : 1 + _named.Count + _expanded.Where(e => e.IsNameMatch).Sum(e => e.ChildRows);

    void Expand(FileRow file, bool all)
    {
        if (file.IsExpanded) Collapse(file);
        var hits = file.Result.Hits;
        int shown = all ? hits.Count : Math.Min(LinesShownWhenExpanded, hits.Count);
        int at = _rows.IndexOf(file) + 1;
        for (int i = 0; i < shown; i++) _rows.Insert(at + i, new HitRow(file, hits[i]));
        if (shown < hits.Count) _rows.Insert(at + shown, new MoreRow(file, hits.Count - shown));
        file.ChildRows = shown + (shown < hits.Count ? 1 : 0);
        file.IsExpanded = true;
        _expanded.Add(file);
    }

    void Collapse(FileRow file)
    {
        int at = _rows.IndexOf(file) + 1;
        for (int i = 0; i < file.ChildRows; i++) _rows.RemoveAt(at);
        file.ChildRows = 0;
        file.IsExpanded = false;
        _expanded.Remove(file);
    }

    void ShowAll(MoreRow more)
    {
        var file = more.File;
        int at = _rows.IndexOf(more);
        _rows.RemoveAt(at);
        var hits = file.Result.Hits;
        for (int i = file.ChildRows - 1; i < hits.Count; i++) _rows.Insert(at++, new HitRow(file, hits[i]));
        file.ChildRows = hits.Count;
    }

    void Activate(Row? row)
    {
        switch (row)
        {
            case FileRow file: Shell.ShowInExplorer(file.Result.Path); break;
            case HitRow hit: Shell.ShowInExplorer(hit.File.Result.Path); break;
            case MoreRow more: ShowAll(more); break;
        }
    }

    void SaveHistory(SearchQuery query, DateTimeOffset started, bool stopped, int keep)
    {
        // History keeps the best-ranked files and a few lines each, so saved searches stay small.
        var results = _named.Concat(_content).Take(2000)
            .Select(f => new FileResult { Path = f.Result.Path, NameMatches = f.Result.NameMatches, MatchCount = f.Result.MatchCount, Hits = f.Result.Hits.Take(50).ToList() })
            .ToList();
        var summary = new HistorySummary(HistoryStore.NewId(), started, _clock.Elapsed.TotalSeconds, query, _root, _single,
            _named.Count, (int)Math.Min(_matches, int.MaxValue), _filesWithMatches, _skipped.Count, stopped);
        _ = Task.Run(() =>
        {
            try
            {
                new HistoryStore(AppPaths.History).Save(summary, results, keep);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // History is a convenience; a failed save must not disturb the search window.
            }
        });
    }

    void UpdateParentScope()
    {
        bool show = _target.SelectedFile != null && JustThisFile.IsChecked != true;
        ParentScope.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ParentScope.Text = $"Unticked, so this searches {_target.Root} and all its subfolders";
    }

    async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (Running) _cts!.Cancel();
        else await RunAsync();
    }

    async void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (Running)
        {
            _restart = true;
            _cts!.Cancel();
        }
        else
        {
            await RunAsync();
        }
    }

    void QueryBox_TextChanged(object sender, TextChangedEventArgs e) =>
        QueryPlaceholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt) return;
        ToggleButton? toggle = e.SystemKey switch
        {
            Key.R => RegexToggle,
            Key.C => CaseToggle,
            Key.I => ImagesToggle,
            _ => null,
        };
        if (toggle == null) return;
        toggle.IsChecked = toggle.IsChecked != true;
        e.Handled = true;
    }

    void JustThisFile_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _cts?.Cancel();
        UpdateParentScope();
        if (Running) return;
        _engine = null;
        Clear();
        StatusText.Text = "Scope changed · press Enter to search";
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(ScopeBox.Text) ? ScopeBox.Text : null };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ScopeBox.Text = dialog.FolderName;
    }

    void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Activate(((ListBoxItem)sender).DataContext as Row);

    void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Activate(ResultsList.SelectedItem as Row);
        e.Handled = true;
    }

    void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not FileRow file) return;
        if (file.IsExpanded) Collapse(file);
        else Expand(file, all: false);
        e.Handled = true;
    }

    void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ActiveRow row) row.File.Skip();
    }
}

static class Shell
{
    /// <summary>
    /// Shows the file selected in a File Explorer window on its folder. "explorer /select" does not reliably
    /// select the file on current Windows builds; this is the Shell function made for it.
    /// </summary>
    public static void ShowInExplorer(string path)
    {
        if (SHParseDisplayName(path, 0, out nint item, 0, out _) != 0)
        {
            // The file is gone (an old result from History): open its folder instead.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
            return;
        }
        try
        {
            SHOpenFolderAndSelectItems(item, 0, 0, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(item);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, nint bindContext, out nint item, uint attributesIn, out uint attributesOut);

    // With no child items, the item itself is selected inside its parent folder.
    [DllImport("shell32.dll")]
    static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);
}
