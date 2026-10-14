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
        _loading = true;
        ShowIgnored();
        ContextLines.Text = App.Settings.ContextLines.ToString();
        int limit = Array.IndexOf(Limits, App.Settings.HistoryLimit);
        HistoryLimit.SelectedIndex = limit >= 0 ? limit : 1;
        _loading = false;
        _ = ShowOcrUsageAsync();
    }

    void ShowIgnored()
    {
        IgnoredList.ItemsSource = App.Settings.IgnoredFolders.ToList();
        NothingIgnored.Visibility = App.Settings.IgnoredFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        bool duplicate = App.Settings.IgnoredFolders.Any(f => f.Equals(value, StringComparison.OrdinalIgnoreCase));
        IgnoredError.Text = duplicate ? $"\"{value}\" is already in the list." : "";
        IgnoredError.Visibility = duplicate ? Visibility.Visible : Visibility.Collapsed;
        if (duplicate) return;
        App.Settings.IgnoredFolders.Add(value);
        App.SaveSettings();
        NewIgnored.Clear();
        ShowIgnored();
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
        App.Settings.IgnoredFolders.Remove((string)((FrameworkElement)sender).Tag);
        App.SaveSettings();
        ShowIgnored();
    }

    void ResetIgnored_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.IgnoredFolders = [.. Settings.DefaultIgnoredFolders];
        App.SaveSettings();
        IgnoredError.Visibility = Visibility.Collapsed;
        ShowIgnored();
    }

    void FewerLines_Click(object sender, RoutedEventArgs e) => SetContextLines(App.Settings.ContextLines - 1);

    void MoreLines_Click(object sender, RoutedEventArgs e) => SetContextLines(App.Settings.ContextLines + 1);

    void SetContextLines(int lines)
    {
        App.Settings.ContextLines = Math.Clamp(lines, 0, 20);
        App.SaveSettings();
        ContextLines.Text = App.Settings.ContextLines.ToString();
    }

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
        App.Settings.HistoryLimit = Limits[HistoryLimit.SelectedIndex];
        App.SaveSettings();
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
