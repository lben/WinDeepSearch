using System.IO;
using System.Windows;
using WinDeepSearch.Core;

namespace WinDeepSearch;

/// <summary>The window opened from the right-click menu, or from History.</summary>
public partial class SearchWindow : Window
{
    public SearchWindow(SearchTarget target)
    {
        InitializeComponent();
        ShowTarget(target);
    }

    void ShowTarget(SearchTarget target)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(target.SelectedFile ?? target.Root));
        Title = "WinDeepSearch — " + (target.Items != null ? $"{target.Items.Count:N0} selected items" : name.Length > 0 ? name : target.Root);
        View.Show(target);
    }

    /// <summary>Adds an item selected together with the ones this window shows (see <see cref="Selection"/>).</summary>
    public void AddItem(string path)
    {
        var target = View.Target;
        ShowTarget(SearchTarget.FromPaths([.. target.Items ?? [target.SelectedFile ?? target.Root], path]));
    }

    public static void OpenSaved(HistorySummary summary, IReadOnlyList<FileResult> results)
    {
        var window = new SearchWindow(SearchTarget.Of(summary));
        window.View.ShowSaved(summary, results);
        window.Show();
    }

    public static void RunAgain(HistorySummary summary)
    {
        var window = new SearchWindow(SearchTarget.Of(summary));
        window.Show();
        _ = window.View.RunAgainAsync(summary);
    }
}
