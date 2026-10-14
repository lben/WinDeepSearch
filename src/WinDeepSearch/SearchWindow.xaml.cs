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
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(target.SelectedFile ?? target.Root));
        Title = "WinDeepSearch — " + (name.Length > 0 ? name : target.Root);
        View.Show(target);
    }

    public static void OpenSaved(HistorySummary summary, IReadOnlyList<FileResult> results)
    {
        var window = new SearchWindow(TargetOf(summary));
        window.View.ShowSaved(summary, results);
        window.Show();
    }

    public static void RunAgain(HistorySummary summary)
    {
        var window = new SearchWindow(TargetOf(summary));
        window.Show();
        _ = window.View.RunAgainAsync(summary);
    }

    static SearchTarget TargetOf(HistorySummary summary) =>
        summary.SingleFile != null ? SearchTarget.FromPath(summary.SingleFile) : new SearchTarget(summary.Root, null, false);
}
