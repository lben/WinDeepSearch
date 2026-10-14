using System.Windows;
using System.Windows.Controls;

namespace WinDeepSearch;

/// <summary>The app opened from the Start menu: Search (whole C:\ drive by default), History and Settings.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SearchPage.Show(SearchTarget.App);
        Nav.SelectedItem = NavSearch;
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
