using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.Win32;
using WinDeepSearch.Core;
using WinDeepSearch.Tests;
using Xunit;

namespace WinDeepSearch.UiTests;

/// <summary>
/// Uses the installed app the way a person does: from Explorer, the search bar, the mouse. All tests are in
/// this one class so xunit runs them one after another; they share the screen.
/// </summary>
public sealed class UiTests : IDisposable
{
    const string InvoiceRegex = @"invoice\s*#?(?=\d{4})";

    static readonly string AppExe = Environment.GetEnvironmentVariable("WDS_APP")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WinDeepSearch", "WinDeepSearch.exe");
    static readonly string Shots = Environment.GetEnvironmentVariable("WDS_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "wds-screenshots");

    readonly UIA3Automation _automation = new();
    readonly string _dir = Path.Combine(Directory.CreateTempSubdirectory("wds-ui-").FullName, "Acme");

    public UiTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Shots);
        var log = Enumerable.Range(1, 28).Select(i => i % 4 == 2 ? $"10:{i:00}:00 INFO Created Invoice #{4000 + i} for account 0093" : $"10:{i:00}:00 DEBUG step {i}");
        File.WriteAllLines(Path.Combine(_dir, "billing.log"), log);
        File.WriteAllText(Path.Combine(_dir, "Invoice 2041.txt"), "nothing relevant inside");
        Fixtures.Docx(Path.Combine(_dir, "letter.docx"), ["Dear customer,", "Please pay Invoice #2041 by Friday.", "Thanks"]);
        Fixtures.Pdf(Path.Combine(_dir, "contract.pdf"), ["7. Billing", "The first invoice is Invoice #1001.", "8. Term"]);
        File.WriteAllBytes(Path.Combine(_dir, "scan.png"), Fixtures.TextPicture("Quarterly report", "Invoice 2041 overdue"));
        // A two-page TIFF goes through the Windows imaging decoder, which the engine can't do on its own.
        File.WriteAllBytes(Path.Combine(_dir, "pages.tif"), Fixtures.Tiff(Fixtures.TextPicture("Cover page"), Fixtures.TextPicture("Remit Invoice 3310 now")));
        Directory.CreateDirectory(Path.Combine(_dir, "Archive"));
        File.WriteAllText(Path.Combine(_dir, "Archive", "readme.txt"), "nothing relevant inside");
    }

    public void Dispose()
    {
        foreach (var process in Process.GetProcessesByName("WinDeepSearch")) process.Kill();
        _automation.Dispose();
    }

    [Fact]
    public void SearchFromAFolderRanksResultsShowsContextAndOpensExplorer()
    {
        var window = Launch("--path", _dir);
        Search(window, InvoiceRegex, regex: true);

        var names = ResultNames(window);
        Assert.Contains("File names · 1", names);
        Assert.Contains("Invoice 2041.txt · Name only", names);
        Assert.Contains("billing.log · 7 matches", names);
        Assert.Contains("letter.docx · 1 match", names);
        Assert.Contains("contract.pdf · 1 match", names);
        Assert.Contains("scan.png · 1 match", names);
        Assert.Contains("pages.tif · 1 match", names);
        Assert.True(names.IndexOf("billing.log · 7 matches") < names.IndexOf("letter.docx · 1 match"), "Files with more matches come first.");
        Shot(window, "01-results");

        // Hovering a file shows the lines around its first matches.
        var tip = HoverForToolTip(Item(window, "billing.log"));
        Assert.Contains(Texts(tip), t => t.Contains("Created Invoice #4002"));
        Assert.Contains(Texts(tip), t => t.Contains("DEBUG step 1"));
        ShotScreen("02-hover-context");

        // Text read from a picture comes with the picture.
        tip = HoverForToolTip(Item(window, "scan.png"));
        Retry.WhileNull(() => tip.FindFirstDescendant(cf => cf.ByControlType(ControlType.Image)), TimeSpan.FromSeconds(10), throwOnTimeout: true);
        Thread.Sleep(500);
        ShotScreen("03-hover-picture");

        // The match on the TIFF's second page is labelled with its page.
        Item(window, "pages.tif").FindFirstDescendant(cf => cf.ByAutomationId("ExpandButton")).AsButton().Invoke();
        Assert.Contains(ResultNames(window), n => n.StartsWith("Page 2 · text in image"));
        Item(window, "pages.tif").FindFirstDescendant(cf => cf.ByAutomationId("ExpandButton")).AsButton().Invoke();

        // The arrow lists four matching lines, and "Show 3 more" the rest.
        Item(window, "billing.log").FindFirstDescendant(cf => cf.ByAutomationId("ExpandButton")).AsButton().Invoke();
        names = ResultNames(window);
        Assert.Equal(4, names.Count(n => n.StartsWith("Line ")));
        Item(window, "Show 3 more matching lines").Click();
        Retry.WhileFalse(() => ResultNames(window).Count(n => n.StartsWith("Line ")) == 7, TimeSpan.FromSeconds(5), throwOnTimeout: true);
        Shot(window, "04-expanded");

        // Clicking a match opens File Explorer on the file's folder, with the file selected.
        Item(window, "Line 2 ·").Click();
        var explorer = Retry.WhileNull(() => ExplorerWindow("Acme"), TimeSpan.FromSeconds(20), throwOnTimeout: true, ignoreException: true).Result;
        var selected = Retry.WhileNull(() => ExplorerSelection(_dir), TimeSpan.FromSeconds(20)).Result;
        ShotScreen("05b-explorer-selection");
        Assert.True(selected == Path.Combine(_dir, "billing.log"), $"Selected: {selected ?? "nothing"}. Explorer windows: {DescribeExplorerWindows()}");
        explorer.Close();
    }

    [Fact]
    public void StartedFromAFileCanSearchJustThatFile()
    {
        var window = Launch("--path", Path.Combine(_dir, "billing.log"));
        var justThisFile = window.FindFirstDescendant(cf => cf.ByAutomationId("JustThisFile")).AsCheckBox();
        Assert.False(justThisFile.IsChecked);

        justThisFile.IsChecked = true;
        Search(window, "Invoice", regex: false, pressEnter: true);

        var names = ResultNames(window);
        Assert.Contains("billing.log · 7 matches", names);
        Assert.Equal(7, names.Count(n => n.StartsWith("Line ")));
        Assert.DoesNotContain(names, n => n.StartsWith("letter.docx"));
        Shot(window, "05-just-this-file");
    }

    [Fact]
    public void TabsRunSearchesOverTheSameFolderSideBySide()
    {
        var window = Launch("--path", _dir);
        // A window from the right-click menu has History and Settings too.
        Assert.NotNull(window.FindFirstDescendant(cf => cf.ByAutomationId("NavHistory")));

        Search(window, InvoiceRegex, regex: true, waitForFinish: false);
        window.FindFirstDescendant(cf => cf.ByAutomationId("NewTabButton")).AsButton().Invoke();
        Retry.WhileFalse(() => TabNames(window).Count == 2, TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);
        // The new tab searches the same folder and starts empty.
        Assert.Equal("", window.FindFirstDescendant(cf => cf.ByAutomationId("QueryBox")).AsTextBox().Text);
        Assert.Contains(_dir, Texts(window));
        Search(window, "Please pay", regex: false);
        var names = ResultNames(window);
        Assert.Contains("letter.docx · 1 match", names);
        Assert.DoesNotContain(names, n => n.StartsWith("billing.log"));
        // The first tab finishes on its own while the second one is shown.
        Retry.WhileFalse(() => TabNames(window)[0] == InvoiceRegex + " · 11 matches", TimeSpan.FromSeconds(60), throwOnTimeout: true, ignoreException: true);
        Assert.Equal("Please pay · 1 match", TabNames(window)[1]);
        Shot(window, "18-tabs");

        Tabs(window).Items[0].Select();
        Retry.WhileFalse(() => ResultNames(window).Contains("billing.log · 7 matches"), TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);

        // Ctrl+T opens a tab, Ctrl+W closes the one shown, and × closes any. A Ctrl+click on the shown tab first
        // must not leave the window without one.
        window.Focus();
        using (Keyboard.Pressing(VirtualKeyShort.CONTROL)) Tabs(window).Items[0].Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_T);
        Retry.WhileFalse(() => TabNames(window).Count == 3, TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
        Retry.WhileFalse(() => TabNames(window).Count == 2, TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);
        Tabs(window).FindFirstDescendant(cf => cf.ByName("Close tab Please pay")).AsButton().Invoke();
        Retry.WhileFalse(() => TabNames(window).Count == 1, TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);
        Assert.StartsWith(InvoiceRegex, TabNames(window)[0]);
    }

    [Fact]
    public void FilesThatCouldNotBeReadAreListedWithTheReason()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.docx"), "not a zip archive");
        var window = Launch("--path", _dir);
        Search(window, "Invoice", regex: false);

        var unreadable = window.FindFirstDescendant(cf => cf.ByAutomationId("UnreadableList")).AsListBox();
        Assert.Equal(["broken.docx · Damaged, or protected with a password"], unreadable.Items.Select(i => i.Name));
        Assert.Contains(Texts(window), t => t.Contains("1 couldn't be read"));
        Shot(window, "19-unreadable");
    }

    [Fact]
    public void SearchingSelectedItemsCoversOnlyThemAndTheirSubfolders()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Archive", "2019"));
        File.WriteAllText(Path.Combine(_dir, "Archive", "2019", "old.txt"), "Invoice #1999 paid");
        File.WriteAllText(Path.Combine(_dir, "extra1.txt"), "nothing relevant inside");
        File.WriteAllText(Path.Combine(_dir, "extra2.txt"), "nothing relevant inside");
        // The list the Windows 11 menu command writes for several selected items.
        string list = Path.Combine(Path.GetTempPath(), $"WinDeepSearch-selection-{Guid.NewGuid():N}.txt");
        string[] selected = ["letter.docx", "Archive", "billing.log", "pages.tif", "Invoice 2041.txt", "extra1.txt", "extra2.txt"];
        File.WriteAllLines(list, selected.Select(name => Path.Combine(_dir, name)));

        var window = Launch("--items", list);
        Assert.Equal("WinDeepSearch — 7 selected items", window.Title);
        Assert.False(File.Exists(list), "The app deletes the list once it has read it.");
        Assert.Contains($"Search in 7 selected items and their subfolders, in {_dir}", Texts(window));
        // Folders first, then files, as Explorer lists them; past six items, the first five and "+2 more".
        var chips = window.FindFirstDescendant(cf => cf.ByAutomationId("SelectedItems"));
        Assert.Equal(["Archive", "billing.log", "extra1.txt", "extra2.txt", "Invoice 2041.txt"], SelectedNames(chips));
        Shot(window, "06a-selected-items");
        var more = chips.FindFirstDescendant(cf => cf.ByAutomationId("MoreItemsButton")).AsButton();
        Assert.Equal("+2 more", more.Name);
        more.Invoke();
        Retry.WhileFalse(() => SelectedNames(window).Count == 7, TimeSpan.FromSeconds(5), ignoreException: true);
        Shot(window, "06b-selected-items-all");
        Assert.Equal(["Archive", "billing.log", "extra1.txt", "extra2.txt", "Invoice 2041.txt", "letter.docx", "pages.tif"], SelectedNames(window));
        Assert.Equal("Show fewer", window.FindFirstDescendant(cf => cf.ByAutomationId("MoreItemsButton")).Name);

        Search(window, "Invoice", regex: false);
        var names = ResultNames(window);
        Assert.Contains("billing.log · 7 matches", names);
        Assert.Contains("letter.docx · 1 match", names);
        Assert.Contains("old.txt · 1 match", names);
        Assert.DoesNotContain(names, n => n.StartsWith("contract.pdf") || n.StartsWith("scan.png"));
        Shot(window, "06c-selected-items-results");
        window.Close();

        // History keeps the selection, and Search again covers the same items.
        string historyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeepSearch", "history");
        Retry.WhileFalse(() => Directory.GetFiles(historyDir, "*.summary.json").Any(f => File.ReadAllText(f).Contains("extra1.txt")),
            TimeSpan.FromSeconds(30), throwOnTimeout: true, ignoreException: true);
        var app = Launch();
        app.FindFirstDescendant(cf => cf.ByAutomationId("NavHistory")).Click();
        var history = Retry.WhileNull(() => app.FindFirstDescendant(cf => cf.ByAutomationId("HistoryList"))?.AsListBox(), TimeSpan.FromSeconds(10), throwOnTimeout: true).Result;
        history.Items.First(i => i.Name.StartsWith($"Invoice · 7 selected items in {_dir}: Archive, billing.log, extra1.txt")).Select();
        app.FindFirstDescendant(cf => cf.ByAutomationId("SearchAgain")).AsButton().Invoke();
        Retry.WhileFalse(() => TabNames(app).Count == 2, TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);
        WaitForFinish(app);
        Assert.Contains("old.txt · 1 match", ResultNames(app));
        Assert.DoesNotContain(ResultNames(app), n => n.StartsWith("contract.pdf"));
    }

    [Fact]
    public void ItemsSelectedAtADriveRootAreShownInThatDrive()
    {
        string root = Path.GetPathRoot(_dir)!;
        string[] items = [Path.Combine(root, $"wds-a-{Guid.NewGuid():N}"), Path.Combine(root, $"wds-b-{Guid.NewGuid():N}")];
        foreach (string item in items) Directory.CreateDirectory(item);
        try
        {
            string list = Path.Combine(Path.GetTempPath(), $"WinDeepSearch-selection-{Guid.NewGuid():N}.txt");
            File.WriteAllLines(list, items);
            var window = Launch("--items", list);
            Assert.Contains($"Search in 2 selected items and their subfolders, in {root}", Texts(window));
        }
        finally
        {
            foreach (string item in items) Directory.Delete(item);
        }
    }

    [Fact]
    public void ClassicMenuCopiesForOneSelectionShareOneWindow()
    {
        // The classic menu starts the app once per selected item, all at about the same time.
        string[] selected = ["Archive", "billing.log", "letter.docx"];
        foreach (string name in selected)
            Process.Start(new ProcessStartInfo(AppExe) { ArgumentList = { "--select", Path.Combine(_dir, name) } });

        var window = Retry.WhileNull(() => SearchWindowTitled("WinDeepSearch — 3 selected items"), TimeSpan.FromSeconds(30), throwOnTimeout: true).Result;
        Assert.Equal(selected, SelectedNames(window));
        Retry.WhileFalse(() => Process.GetProcessesByName("WinDeepSearch").Length == 1, TimeSpan.FromSeconds(10), throwOnTimeout: true);
        Assert.Single(_automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window)), w => w.Name.StartsWith("WinDeepSearch"));
        Shot(window, "06d-classic-menu-selection");
    }

    [Fact]
    public void ListingEveryMatchSaysWhenLinesWereNotKept()
    {
        string dir = Directory.CreateTempSubdirectory("wds-many-").FullName;
        string file = Path.Combine(dir, "many.log");
        File.WriteAllLines(file, Enumerable.Range(1, 5005).Select(i => $"hit {i}"));

        var window = Launch("--path", file);
        window.FindFirstDescendant(cf => cf.ByAutomationId("JustThisFile")).AsCheckBox().IsChecked = true;
        Search(window, "hit", regex: false);

        var list = window.FindFirstDescendant(cf => cf.ByAutomationId("ResultsList"));
        list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
        const string note = "Only the first 5,000 matching lines were kept; the file has 5,005 matches";
        Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByName(note)), TimeSpan.FromSeconds(20), throwOnTimeout: true, ignoreException: true);
        Shot(window, "17-lines-not-kept");
    }

    [Fact]
    public void ASlowFileShowsProgressAndCanBeSkipped()
    {
        string dir = Directory.CreateTempSubdirectory("wds-skip-").FullName;
        File.WriteAllText(Path.Combine(dir, "small.txt"), "needle");
        using (var huge = File.Create(Path.Combine(dir, "huge.log")))
        {
            var block = new byte[16 * 1024 * 1024];
            Array.Fill(block, (byte)'x');
            for (int i = 0; i < 160; i++) huge.Write(block); // 2.5 GB
        }

        var window = Launch("--path", dir);

        // A broken regular expression is reported instead of searched.
        Search(window, "(unclosed", regex: true, waitForFinish: false);
        var error = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ErrorText")), TimeSpan.FromSeconds(10), throwOnTimeout: true).Result;
        Assert.False(string.IsNullOrWhiteSpace(error.Name));
        Assert.Equal("Ready · press Enter to search", window.FindFirstDescendant(cf => cf.ByAutomationId("StatusText")).Name);

        Search(window, "needle", regex: false, waitForFinish: false);
        var skip = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByName("Skip huge.log")), TimeSpan.FromSeconds(60), throwOnTimeout: true).Result;
        Shot(window, "06-searching");
        skip.AsButton().Invoke();

        WaitForFinish(window);
        Assert.Contains(window.FindFirstDescendant(cf => cf.ByAutomationId("SkippedList")).AsListBox().Items,
            i => i.Name.StartsWith("huge.log · 2.5 GB · skipped by you at"));
        Assert.Contains("small.txt · 1 match", ResultNames(window));
        Shot(window, "07-skipped");

        // While a search runs the button is Stop, and it ends the whole search.
        Search(window, "needle", regex: false, waitForFinish: false);
        var go = window.FindFirstDescendant(cf => cf.ByAutomationId("SearchButton")).AsButton();
        Retry.WhileFalse(() => go.Name == "Stop", TimeSpan.FromSeconds(10), throwOnTimeout: true);
        go.Invoke();
        var status = window.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"));
        Retry.WhileFalse(() => status.Name.StartsWith("Stopped"), TimeSpan.FromSeconds(30), throwOnTimeout: true);
    }

    [Fact]
    public void MainAppSearchesAnyFolderAndKeepsHistoryAndSettings()
    {
        var window = Launch();
        var scope = window.FindFirstDescendant(cf => cf.ByAutomationId("ScopeBox")).AsTextBox();
        Assert.Equal(@"C:\", scope.Text);
        Shot(window, "08-main-app");

        // Pasted the way File Explorer's "Copy as path" puts it, in quotes.
        scope.Text = $"\"{_dir}\"";
        string historyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDeepSearch", "history");
        int saved = Directory.Exists(historyDir) ? Directory.GetFiles(historyDir, "*.summary.json").Length : 0;
        Search(window, "Invoice #2041", regex: false);
        Assert.Contains("letter.docx · 1 match", ResultNames(window));
        // The search is written to history in the background once it finishes.
        Retry.WhileFalse(() => Directory.Exists(historyDir) && Directory.GetFiles(historyDir, "*.summary.json").Length > saved,
            TimeSpan.FromSeconds(30), throwOnTimeout: true);

        window.FindFirstDescendant(cf => cf.ByAutomationId("NavHistory")).Click();
        var history = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId("HistoryList"))?.AsListBox(), TimeSpan.FromSeconds(10), throwOnTimeout: true).Result;
        Retry.WhileFalse(() => history.Items.Any(i => i.Name.StartsWith("Invoice #2041")), TimeSpan.FromSeconds(10), throwOnTimeout: true);
        Retry.WhileFalse(() => window.FindFirstDescendant(cf => cf.ByAutomationId("SavedFiles")).AsListBox().Items.Length > 0, TimeSpan.FromSeconds(10), throwOnTimeout: true);
        Shot(window, "09-history");

        // The filter narrows the list.
        var filter = window.FindFirstDescendant(cf => cf.ByAutomationId("HistoryFilter")).AsTextBox();
        filter.Text = "no search looks like this";
        Retry.WhileFalse(() => history.Items.Length == 0, TimeSpan.FromSeconds(5), throwOnTimeout: true);
        filter.Text = "";
        Retry.WhileFalse(() => history.Items.Length > 0, TimeSpan.FromSeconds(5), throwOnTimeout: true);
        history.Items.First(i => i.Name.StartsWith("Invoice #2041")).Select();

        // Open results shows the saved results in a new tab of this window.
        window.FindFirstDescendant(cf => cf.ByAutomationId("OpenResults")).AsButton().Invoke();
        Retry.WhileFalse(() => TabNames(window).Count == 2, TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);
        Assert.Equal("Invoice #2041 · Saved results", TabNames(window)[1]);
        Assert.Contains(Texts(window), t => t.StartsWith("Saved results from"));
        Assert.Contains("letter.docx · 1 match", ResultNames(window));

        // Search again runs it anew in another tab, and it is saved as a new entry.
        int beforeAgain = SavedSearches(historyDir);
        window.FindFirstDescendant(cf => cf.ByAutomationId("NavHistory")).Click();
        window.FindFirstDescendant(cf => cf.ByAutomationId("SearchAgain")).AsButton().Invoke();
        Retry.WhileFalse(() => TabNames(window).Count == 3, TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);
        WaitForFinish(window);
        Assert.Contains("letter.docx · 1 match", ResultNames(window));
        Retry.WhileFalse(() => SavedSearches(historyDir) == beforeAgain + 1, TimeSpan.FromSeconds(30), throwOnTimeout: true);

        // Remove deletes the selected entry, and the list shows what is left.
        window.FindFirstDescendant(cf => cf.ByAutomationId("NavHistory")).Click();
        history = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId("HistoryList"))?.AsListBox(), TimeSpan.FromSeconds(10), throwOnTimeout: true).Result;
        int beforeRemove = SavedSearches(historyDir);
        window.FindFirstDescendant(cf => cf.ByAutomationId("RemoveHistory")).AsButton().Invoke();
        Retry.WhileFalse(() => SavedSearches(historyDir) == beforeRemove - 1, TimeSpan.FromSeconds(10), throwOnTimeout: true);
        Retry.WhileFalse(() => history.Items.Length == beforeRemove - 1, TimeSpan.FromSeconds(10), throwOnTimeout: true);

        // Settings changes are saved and still there after the app restarts.
        window.FindFirstDescendant(cf => cf.ByAutomationId("NavSettings")).Click();
        Assert.Contains(".git", Texts(window.FindFirstDescendant(cf => cf.ByAutomationId("IgnoredList"))));
        Assert.Equal("3", window.FindFirstDescendant(cf => cf.ByAutomationId("ContextLines")).Name);
        window.FindFirstDescendant(cf => cf.ByAutomationId("NewIgnored")).AsTextBox().Text = "zz-ignored";
        window.FindFirstDescendant(cf => cf.ByAutomationId("AddIgnored")).AsButton().Invoke();
        window.FindFirstDescendant(cf => cf.ByName("More lines")).AsButton().Invoke();
        Shot(window, "10-settings");
        window.Close();

        var restarted = Launch();
        restarted.FindFirstDescendant(cf => cf.ByAutomationId("NavSettings")).Click();
        Retry.WhileFalse(() => Texts(restarted.FindFirstDescendant(cf => cf.ByAutomationId("IgnoredList"))).Contains("zz-ignored"), TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);
        Assert.Equal("4", restarted.FindFirstDescendant(cf => cf.ByAutomationId("ContextLines")).Name);
        restarted.FindFirstDescendant(cf => cf.ByName("Stop ignoring zz-ignored")).AsButton().Invoke();
        restarted.FindFirstDescendant(cf => cf.ByName("Fewer lines")).AsButton().Invoke();
        Retry.WhileFalse(() => !Texts(restarted.FindFirstDescendant(cf => cf.ByAutomationId("IgnoredList"))).Contains("zz-ignored"), TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);
        Assert.Equal("3", restarted.FindFirstDescendant(cf => cf.ByAutomationId("ContextLines")).Name);
    }

    [Fact]
    public void SettingsChangedInTwoWindowsAreBothKept()
    {
        // Every window is a process of its own, and each shows Settings.
        var first = Launch();
        var second = Launch();
        first.FindFirstDescendant(cf => cf.ByAutomationId("NavSettings")).Click();
        second.FindFirstDescendant(cf => cf.ByAutomationId("NavSettings")).Click();
        try
        {
            AddIgnored(first, "zz-first");
            AddIgnored(second, "zz-second");

            var list = second.FindFirstDescendant(cf => cf.ByAutomationId("IgnoredList"));
            Retry.WhileFalse(() => Texts(list).Contains("zz-second"), TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true);
            Assert.Contains("zz-first", Texts(list));
            Assert.Contains("zz-first", Settings.Load(AppPaths.SettingsFile).IgnoredFolders);
            Assert.Contains("zz-second", Settings.Load(AppPaths.SettingsFile).IgnoredFolders);
        }
        finally
        {
            second.FindFirstDescendant(cf => cf.ByName("Reset to defaults")).AsButton().Invoke();
        }
    }

    static void AddIgnored(Window window, string name)
    {
        window.FindFirstDescendant(cf => cf.ByAutomationId("NewIgnored")).AsTextBox().Text = name;
        window.FindFirstDescendant(cf => cf.ByAutomationId("AddIgnored")).AsButton().Invoke();
    }

    [Fact]
    public void FollowsTheWindowsDarkTheme()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var personalize = Registry.CurrentUser.CreateSubKey(key);
        object? previous = personalize.GetValue("AppsUseLightTheme");
        personalize.SetValue("AppsUseLightTheme", 0, RegistryValueKind.DWord);
        try
        {
            var window = Launch("--path", _dir);
            Search(window, InvoiceRegex, regex: true);
            Item(window, "billing.log").FindFirstDescendant(cf => cf.ByAutomationId("ExpandButton")).AsButton().Invoke();
            HoverForToolTip(Item(window, "Line 2 ·"));
            ShotScreen("11-dark");
        }
        finally
        {
            if (previous == null) personalize.DeleteValue("AppsUseLightTheme", throwOnMissingValue: false);
            else personalize.SetValue("AppsUseLightTheme", previous, RegistryValueKind.DWord);
        }
    }

    [Fact]
    public void ExplorerRightClickMenusOpenASearchWindow()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dir}\"") { UseShellExecute = true });
        var explorer = Retry.WhileNull(() => ExplorerWindow("Acme"), TimeSpan.FromSeconds(30), ignoreException: true).Result;
        if (explorer == null)
        {
            ShotScreen("explorer-missing");
            var titles = _automation.GetDesktop().FindAllChildren().Select(w => $"{w.ClassName}: {w.Name}");
            Assert.Fail("No Explorer window for Acme. Top-level windows: " + string.Join(" | ", titles));
        }
        try
        {
            // Explorer hides known extensions by default, so the item may be called "billing".
            var file = Retry.WhileNull(() => explorer.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem).And(cf.ByName("billing.log").Or(cf.ByName("billing")))),
                TimeSpan.FromSeconds(20)).Result;
            if (file == null) ShotScreen("explorer-item-missing");
            Assert.NotNull(file);

            // Windows 11 menu.
            file.RightClick();
            var entry = WaitForMenuItem(_automation.GetDesktop());
            ShotScreen("12-windows11-menu");
            entry.Click();
            var window = Retry.WhileNull(() => SearchWindowTitled("WinDeepSearch — billing.log"), TimeSpan.FromSeconds(30), throwOnTimeout: true).Result;
            Shot(window, "13-opened-from-menu");
            window.Close();

            // The classic menu behind "Show more options" (a Win32 menu, window class #32768).
            file.RightClick();
            WaitForMenuItem(_automation.GetDesktop(), "Show more options").Click();
            var classic = Retry.WhileNull(() => _automation.GetDesktop().FindFirstChild(cf => cf.ByClassName("#32768")), TimeSpan.FromSeconds(20), throwOnTimeout: true).Result;
            entry = WaitForMenuItem(classic);
            ShotScreen("14-classic-menu");
            Assert.Single(classic.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName("WinDeepSearch"))));
            entry.Click();
            Retry.WhileNull(() => SearchWindowTitled("WinDeepSearch — billing.log"), TimeSpan.FromSeconds(30), throwOnTimeout: true).Result.Close();

            // A folder.
            var folder = Retry.WhileNull(() => explorer.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem).And(cf.ByName("Archive"))),
                TimeSpan.FromSeconds(20), throwOnTimeout: true).Result;
            folder.RightClick();
            WaitForMenuItem(_automation.GetDesktop()).Click();
            Retry.WhileNull(() => SearchWindowTitled("WinDeepSearch — Archive"), TimeSpan.FromSeconds(30), throwOnTimeout: true).Result.Close();

            // A file and a folder selected together.
            file.Patterns.SelectionItem.Pattern.Select();
            folder.Patterns.SelectionItem.Pattern.AddToSelection();
            folder.RightClick();
            WaitForMenuItem(_automation.GetDesktop()).Click();
            var both = Retry.WhileNull(() => SearchWindowTitled("WinDeepSearch — 2 selected items"), TimeSpan.FromSeconds(30), throwOnTimeout: true).Result;
            Assert.Equal(["Archive", "billing.log"], SelectedNames(both));
            Shot(both, "14b-two-selected-items");
            both.Close();

            // The empty space inside the open folder.
            var view = explorer.FindFirstDescendant(cf => cf.ByControlType(ControlType.List).And(cf.ByName("Items View")))
                ?? explorer.FindFirstDescendant(cf => cf.ByControlType(ControlType.List));
            var area = view.BoundingRectangle;
            Mouse.RightClick(new System.Drawing.Point(area.Right - 40, area.Bottom - 40));
            WaitForMenuItem(_automation.GetDesktop()).Click();
            var background = Retry.WhileNull(() => SearchWindowTitled("WinDeepSearch — Acme"), TimeSpan.FromSeconds(30), throwOnTimeout: true).Result;
            Shot(background, "15-from-folder-background");
        }
        catch
        {
            ShotScreen("explorer-menu-failure");
            throw;
        }
        finally
        {
            explorer.Close();
        }
    }

    [Fact]
    public void ClassicDriveEntryOpensTheDriveRoot()
    {
        // Runs the classic "WinDeepSearch" verb on C:\ the way Explorer does from a drive's menu. Explorer quotes
        // the path as "C:\", where the trailing backslash escapes the closing quote.
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = 0x100, // SEE_MASK_NOASYNC
            Verb = "WinDeepSearch",
            File = @"C:\",
            Show = 1,
        };
        Assert.True(ShellExecuteEx(ref info), "Windows has no WinDeepSearch entry for drives.");

        var window = Retry.WhileNull(() => SearchWindowTitled(@"WinDeepSearch — C:\"), TimeSpan.FromSeconds(30), throwOnTimeout: true).Result;
        Assert.Contains(@"C:\", Texts(window));
        Shot(window, "16-from-drive");
    }

    [Fact]
    public void Windows11MenuCommandOpensADriveRootFolder()
    {
        // Calls the installed Windows 11 menu command directly, as Explorer does for the empty space inside C:\,
        // checking the argument it passes for a drive root.
        string dll = Path.Combine(Path.GetDirectoryName(AppExe)!, "WinDeepSearch.ShellExt.dll");
        var library = NativeLibrary.Load(dll);
        var getClassObject = Marshal.GetDelegateForFunctionPointer<DllGetClassObject>(NativeLibrary.GetExport(library, "DllGetClassObject"));
        var clsid = new Guid("2CADCD13-EDE6-4B0F-94BB-2C46DF5954F1");
        var factoryId = typeof(IClassFactory).GUID;
        Marshal.ThrowExceptionForHR(getClassObject(ref clsid, ref factoryId, out var factoryPointer));
        var factory = (IClassFactory)Marshal.GetObjectForIUnknown(factoryPointer);
        var commandId = typeof(IExplorerCommand).GUID;
        Marshal.ThrowExceptionForHR(factory.CreateInstance(0, ref commandId, out var commandPointer));
        var command = (IExplorerCommand)Marshal.GetObjectForIUnknown(commandPointer);

        Marshal.ThrowExceptionForHR(command.GetTitle(0, out var title));
        Assert.Equal("WinDeepSearch", Marshal.PtrToStringUni(title));
        Marshal.FreeCoTaskMem(title);

        var itemId = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(@"C:\", 0, ref itemId, out var item));
        var arrayId = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b"); // IShellItemArray
        Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromShellItem(item, ref arrayId, out var items));
        Marshal.ThrowExceptionForHR(command.Invoke(items, 0));

        var window = Retry.WhileNull(() => SearchWindowTitled(@"WinDeepSearch — C:\"), TimeSpan.FromSeconds(30), throwOnTimeout: true).Result;
        Assert.Contains(@"C:\", Texts(window));
    }

    Window Launch(params string[] args)
    {
        var start = new ProcessStartInfo(AppExe);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var app = Application.Launch(start);
        var window = app.GetMainWindow(_automation, TimeSpan.FromSeconds(60));
        Assert.NotNull(window);
        return window;
    }

    void Search(Window window, string text, bool regex, bool waitForFinish = true, bool pressEnter = false)
    {
        var toggle = window.FindFirstDescendant(cf => cf.ByAutomationId("RegexToggle")).AsToggleButton();
        if ((toggle.ToggleState == ToggleState.On) != regex) toggle.Toggle();
        var query = window.FindFirstDescendant(cf => cf.ByAutomationId("QueryBox")).AsTextBox();
        if (pressEnter)
        {
            // Typed and started with Enter, the way people use it.
            query.Text = "";
            query.Focus();
            Keyboard.Type(text);
            // Typing alone must not start a search.
            Assert.EndsWith("press Enter to search", window.FindFirstDescendant(cf => cf.ByAutomationId("StatusText")).Name);
            Keyboard.Press(VirtualKeyShort.ENTER);
        }
        else
        {
            query.Text = text;
            window.FindFirstDescendant(cf => cf.ByAutomationId("SearchButton")).AsButton().Invoke();
        }
        if (waitForFinish) WaitForFinish(window);
    }

    static void WaitForFinish(Window window)
    {
        var status = window.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"));
        // The first search also loads the OCR models.
        Retry.WhileFalse(() => status.Name.StartsWith("Finished"), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(250), throwOnTimeout: true);
    }

    // While rows are being inserted, WPF briefly rebuilds the list's accessibility tree, so lookups retry.
    static List<string> ResultNames(Window window) =>
        Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ResultsList")).AsListBox().Items.Select(i => i.Name).ToList(),
            TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true).Result;

    static ListBoxItem Item(Window window, string prefix) =>
        Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ResultsList")).AsListBox().Items.FirstOrDefault(i => i.Name.StartsWith(prefix)),
            TimeSpan.FromSeconds(10), throwOnTimeout: true, ignoreException: true).Result;

    AutomationElement HoverForToolTip(AutomationElement element)
    {
        var point = element.GetClickablePoint();
        // A tooltip opens on mouse movement; when one doesn't, nudge the mouse and wait again.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Mouse.MoveTo(point.X, point.Y + 2 * attempt);
            // WPF shows a tooltip in its own popup window, so it sits a level below the desktop.
            var tip = Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.ToolTip)), TimeSpan.FromSeconds(5), ignoreException: true).Result;
            if (tip != null) return tip;
        }
        ShotScreen("tooltip-missing");
        throw new Xunit.Sdk.XunitException("No tooltip appeared.");
    }

    // A closed classic menu can stay in the tree, hidden; clicking its item before the new menu shows does nothing.
    static AutomationElement WaitForMenuItem(AutomationElement within, string name = "WinDeepSearch") =>
        Retry.WhileNull(() => within.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(name))).FirstOrDefault(e => !e.IsOffscreen),
            TimeSpan.FromSeconds(20), throwOnTimeout: true, ignoreException: true).Result;

    // The title is the folder name, followed by " - File Explorer" on some Windows versions.
    Window? ExplorerWindow(string folder) =>
        _automation.GetDesktop().FindAllChildren(cf => cf.ByClassName("CabinetWClass"))
            .FirstOrDefault(w => w.Name == folder || w.Name.StartsWith(folder + " - "))?.AsWindow();

    Window? SearchWindowTitled(string title) =>
        _automation.GetDesktop().FindFirstChild(cf => cf.ByControlType(ControlType.Window).And(cf.ByName(title)))?.AsWindow();

    static int SavedSearches(string historyDir) =>
        Directory.Exists(historyDir) ? Directory.GetFiles(historyDir, "*.summary.json").Length : 0;

    /// <summary>The file selected in the Explorer window showing <paramref name="folder"/>, asked through the Shell.</summary>
    static string ExplorerSelection(string folder)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        foreach (dynamic window in shell.Windows())
        {
            try
            {
                if (!string.Equals((string)window.Document.Folder.Self.Path, folder, StringComparison.OrdinalIgnoreCase)) continue;
                dynamic items = window.Document.SelectedItems();
                if (items.Count > 0) return (string)items.Item(0).Path;
            }
            catch (Exception)
            {
                // Not a folder window, or still loading.
            }
        }
        return null;
    }

    static string DescribeExplorerWindows()
    {
        var found = new List<string>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        foreach (dynamic window in shell.Windows())
        {
            try
            {
                found.Add($"{(string)window.LocationURL} [{(int)window.Document.SelectedItems().Count} selected]");
            }
            catch (Exception e)
            {
                found.Add("? " + e.GetType().Name + ": " + e.Message);
            }
        }
        return found.Count == 0 ? "none" : string.Join(" | ", found);
    }

    static ListBox Tabs(Window window) => window.FindFirstDescendant(cf => cf.ByAutomationId("SearchTabs")).AsListBox();

    static List<string> TabNames(Window window) => Tabs(window).Items.Select(i => i.Name).ToList();

    /// <summary>The names of the selected items listed under the search scope.</summary>
    static List<string> SelectedNames(AutomationElement element) =>
        element.FindAllDescendants(cf => cf.ByAutomationId("SelectedItemName")).Select(t => t.Name).ToList();

    static List<string> Texts(AutomationElement element) =>
        element.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();

    static void Shot(AutomationElement element, string name) => Capture.Element(element).ToFile(Path.Combine(Shots, name + ".png"));

    static void ShotScreen(string name) => Capture.Screen().ToFile(Path.Combine(Shots, name + ".png"));

    delegate int DllGetClassObject(ref Guid clsid, ref Guid iid, out nint factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public nint Window;
        public string Verb;
        public string File;
        public string Parameters;
        public string Directory;
        public int Show;
        public nint InstanceApp;
        public nint IdList;
        public string Class;
        public nint ClassKey;
        public uint HotKey;
        public nint Icon;
        public nint Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, out nint item);

    [DllImport("shell32.dll")]
    static extern int SHCreateShellItemArrayFromShellItem(nint item, ref Guid iid, out nint array);

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IClassFactory
    {
        [PreserveSig] int CreateInstance(nint outer, ref Guid iid, out nint instance);
        [PreserveSig] int LockServer(bool lockServer);
    }

    [ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(nint items, out nint name);
        [PreserveSig] int GetIcon(nint items, out nint icon);
        [PreserveSig] int GetToolTip(nint items, out nint tip);
        [PreserveSig] int GetCanonicalName(out Guid name);
        [PreserveSig] int GetState(nint items, bool okToBeSlow, out int state);
        [PreserveSig] int Invoke(nint items, nint bindContext);
        [PreserveSig] int GetFlags(out int flags);
        [PreserveSig] int EnumSubCommands(out nint commands);
    }
}
