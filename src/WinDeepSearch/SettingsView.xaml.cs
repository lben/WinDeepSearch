using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinDeepSearch.Core;

namespace WinDeepSearch;

public partial class SettingsView : UserControl
{
    static readonly int[] Limits = [20, 50, 100, 500];
    bool _loading;

    public SettingsView() => InitializeComponent();

    public void Refresh()
    {
        var settings = Settings.Load(AppPaths.SettingsFile);
        _loading = true;
        ShowIgnored(settings);
        ContextLines.Text = settings.ContextLines.ToString();
        int limit = Array.IndexOf(Limits, settings.HistoryLimit);
        HistoryLimit.SelectedIndex = limit >= 0 ? limit : 1;
        _loading = false;
        _ = ShowOcrUsageAsync();
    }

    /// <summary>
    /// Every window is a process of its own and can change settings, so each change starts from what is saved
    /// now, not from what this window read earlier.
    /// </summary>
    static Settings Change(Action<Settings> change)
    {
        var settings = Settings.Load(AppPaths.SettingsFile);
        change(settings);
        try
        {
            settings.Save(AppPaths.SettingsFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another window is writing the same file at this moment; this change is lost.
        }
        return settings;
    }

    void ShowIgnored(Settings settings)
    {
        IgnoredList.ItemsSource = settings.IgnoredFolders.ToList();
        NothingIgnored.Visibility = settings.IgnoredFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task ShowOcrUsageAsync()
    {
        OcrUsage.Text = "Working out how much space it uses…";
        long bytes = await Task.Run(() =>
        {
            try
            {
                return Directory.Exists(AppPaths.OcrCache)
                    ? new DirectoryInfo(AppPaths.OcrCache).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                    : 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        });
        OcrUsage.Text = bytes == 0 ? "Nothing remembered yet." : $"Using {Sizes.Format(bytes)} on this PC.";
    }

    void AddIgnored()
    {
        string value = NewIgnored.Text.Trim();
        if (value.Length == 0) return;
        bool duplicate = false;
        var settings = Change(s =>
        {
            duplicate = s.IgnoredFolders.Any(f => f.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (!duplicate) s.IgnoredFolders.Add(value);
        });
        IgnoredError.Text = duplicate ? $"\"{value}\" is already in the list." : "";
        IgnoredError.Visibility = duplicate ? Visibility.Visible : Visibility.Collapsed;
        if (!duplicate) NewIgnored.Clear();
        ShowIgnored(settings);
    }

    void AddIgnored_Click(object sender, RoutedEventArgs e) => AddIgnored();

    void NewIgnored_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddIgnored();
        e.Handled = true;
    }

    void RemoveIgnored_Click(object sender, RoutedEventArgs e)
    {
        string folder = (string)((FrameworkElement)sender).Tag;
        ShowIgnored(Change(s => s.IgnoredFolders.Remove(folder)));
    }

    void ResetIgnored_Click(object sender, RoutedEventArgs e)
    {
        IgnoredError.Visibility = Visibility.Collapsed;
        ShowIgnored(Change(s => s.IgnoredFolders = [.. Settings.DefaultIgnoredFolders]));
    }

    void FewerLines_Click(object sender, RoutedEventArgs e) => ChangeContextLines(-1);

    void MoreLines_Click(object sender, RoutedEventArgs e) => ChangeContextLines(+1);

    void ChangeContextLines(int by) =>
        ContextLines.Text = Change(s => s.ContextLines = Math.Clamp(s.ContextLines + by, 0, 20)).ContextLines.ToString();

    async void ClearOcr_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(AppPaths.OcrCache)) Directory.Delete(AppPaths.OcrCache, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A search is writing to it right now; whatever was deleted stays deleted.
            }
        });
        await ShowOcrUsageAsync();
    }

    void HistoryLimit_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HistoryLimit.SelectedIndex < 0) return;
        int limit = Limits[HistoryLimit.SelectedIndex];
        Change(s => s.HistoryLimit = limit);
    }

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Remove every saved search and its results?", "WinDeepSearch",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            new HistoryStore(AppPaths.History).Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Some entries are in use by another window; they will be trimmed later.
        }
    }
}
