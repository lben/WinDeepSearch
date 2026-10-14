using System.Text;
using WinDeepSearch.Core;
using Xunit;

namespace WinDeepSearch.Tests;

public sealed class SearchTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("wds-test-").FullName;
    readonly string _cache = Directory.CreateTempSubdirectory("wds-cache-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        Directory.Delete(_cache, true);
    }

    string Write(string relative, string text, Encoding? encoding = null)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    string PathOf(string relative)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    async Task<List<FileResult>> Search(string text, bool regex = false, bool matchCase = false, bool images = true,
        Settings? settings = null, string? singleFile = null)
    {
        var engine = new SearchEngine(new SearchQuery(text, regex, matchCase, images), _dir, singleFile, settings ?? new Settings(), new ImageText(_cache));
        await engine.RunAsync(TestContext.Current.CancellationToken);
        return [.. engine.Results];
    }

    static FileResult Single(List<FileResult> results, string fileName) =>
        Assert.Single(results, r => Path.GetFileName(r.Path) == fileName);

    static string HitText(Hit hit) => hit.Context[hit.HitLine].Text;

    static IEnumerable<string> Matched(Hit hit)
    {
        var line = hit.Context[hit.HitLine];
        return line.Matches.Select(m => line.Text.Substring(m.Start, m.Length));
    }

    [Fact]
    public async Task RegexLookaheadAndLookbehindFindOnlyTheIntendedText()
    {
        Write("notes.txt", "Invoice #2041 sent\ninvoice 33 is too short\nINVOICE#7781 overdue\nInvoiceWorker started\nTotal: 8215 due\n");

        var lookahead = Single(await Search(@"invoice\s*#?(?=\d{4})", regex: true), "notes.txt");
        Assert.Equal(2, lookahead.MatchCount);
        Assert.Equal(["Invoice #", "INVOICE#"], lookahead.Hits.SelectMany(Matched));
        Assert.Equal(["Line 1", "Line 3"], lookahead.Hits.Select(h => h.Location));

        var lookbehind = Single(await Search(@"(?<=Total:\s*)\d+", regex: true), "notes.txt");
        Assert.Equal(["8215"], lookbehind.Hits.SelectMany(Matched));

        var caseSensitive = Single(await Search("Invoice", matchCase: true), "notes.txt");
        Assert.Equal(2, caseSensitive.MatchCount); // "Invoice #2041" and "InvoiceWorker", not "invoice" or "INVOICE"
    }

    [Fact]
    public async Task EndOfLineAnchorsWorkWithWindowsLineEndings()
    {
        Write("crlf.txt", "Total due: 8215\r\nPaid: 0\r\n");

        var total = Single(await Search(@"due: \d+$", regex: true), "crlf.txt");
        Assert.Equal(["due: 8215"], total.Hits.SelectMany(Matched));
        var paid = Single(await Search("^Paid: 0$", regex: true), "crlf.txt");
        Assert.Equal("Line 2", paid.Hits.Single().Location);
    }

    [Fact]
    public async Task PlainTextModeTreatsRegexCharactersLiterally()
    {
        Write("a.txt", "price is $5.00 (approx)\nprice is $5000\n");
        var result = Single(await Search("$5.00 (approx)"), "a.txt");
        Assert.Equal(1, result.MatchCount);
        Assert.Equal("Line 1", result.Hits[0].Location);
    }

    [Fact]
    public async Task ContextShowsTheConfiguredLinesAroundEachMatch()
    {
        Write("log.txt", string.Join("\n", Enumerable.Range(1, 20).Select(i => i == 10 ? "line 10 has the needle twice: needle" : $"line {i}")));

        var hit = Assert.Single(Single(await Search("needle", settings: new Settings { ContextLines = 2 }), "log.txt").Hits);
        Assert.Equal([8, 9, 10, 11, 12], hit.Context.Select(c => c.Number));
        Assert.Equal(2, hit.HitLine);
        Assert.Equal(["needle", "needle"], Matched(hit));
        Assert.All(hit.Context.Where((_, i) => i != hit.HitLine), c => Assert.Empty(c.Matches));
    }

    [Fact]
    public async Task RecognisesTextEncodingsAndIgnoresBinaryFiles()
    {
        Write("utf16-bom.txt", "first\ncafé crème\n", new UnicodeEncoding(false, true));
        Write("utf16-nobom.txt", "first\ncafé crème\n", new UnicodeEncoding(false, false));
        Write("utf8.txt", "first\ncafé crème\n");
        File.WriteAllBytes(PathOf("ansi.txt"), Encoding.Latin1.GetBytes("first\ncafé crème\n"));
        File.WriteAllBytes(PathOf("program.bin"), [0x4D, 0x5A, 0, 0, 3, 0, .. Encoding.ASCII.GetBytes("café crème"), 0, 0, 0xFF, 0x01, 0x02]);

        var results = await Search("café crème");
        Assert.Equal(["ansi.txt", "utf16-bom.txt", "utf16-nobom.txt", "utf8.txt"], results.Select(r => Path.GetFileName(r.Path)).Order());
        Assert.All(results, r => Assert.Equal("Line 2", Assert.Single(r.Hits).Location));
    }

    [Fact]
    public async Task AnAnsiFileWhoseAccentsComeLateIsStillReadAsAnsi()
    {
        // Like an Excel "CSV (Comma delimited)" export: plain ASCII rows first, the first accented letter far in.
        var rows = Enumerable.Range(1, 400).Select(i => $"row {i},plain ascii text,{i * 3}").Append("row 999,café crème,1");
        File.WriteAllBytes(PathOf("export.csv"), Encoding.Latin1.GetBytes(string.Join("\r\n", rows)));

        var hit = Single(await Search("café crème"), "export.csv").Hits.Single();
        Assert.Equal("Line 401", hit.Location);
        Assert.Contains("row 400,plain ascii text,1200", hit.Context.Select(c => c.Text));
    }

    [Fact]
    public async Task APatternThatTimesOutOnAFileNameOnlySkipsThatFile()
    {
        // Nested quantifiers backtrack exponentially on a long name that almost matches.
        Write("Quarterly financial report for the fiscal year twenty draft-final.txt", "x");
        Write("normal.txt", "abc");

        var engine = new SearchEngine(new SearchQuery(@"^(\w+\s?)+$", true, false, false), _dir, null, new Settings(), new ImageText(_cache));
        await engine.RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains(engine.Results, r => Path.GetFileName(r.Path) == "normal.txt");
        var skipped = Assert.Single(engine.Skipped);
        Assert.StartsWith("Quarterly", Path.GetFileName(skipped.Path));
    }

    [Fact]
    public async Task FilesLargerThanOneChunkAreSearchedCompletelyWithCorrectLineNumbers()
    {
        // Lines of 100 characters; the file spans three reading chunks. Markers sit on lines that straddle
        // chunk boundaries and next to them, so a match near a boundary must be found once, with its true line.
        const int lineLength = 100, lines = 100_000;
        int boundaryLine = TextFiles.ChunkChars / lineLength - TextFiles.LookaheadChars / lineLength;
        var markers = new SortedSet<int> { 1, boundaryLine - 1, boundaryLine, boundaryLine + 1, 2 * boundaryLine, lines };
        var builder = new StringBuilder(lines * lineLength);
        for (int i = 1; i <= lines; i++)
        {
            string line = markers.Contains(i) ? $"MARKER-{i}" : $"filler {i}";
            // On the boundary line a run of Q's crosses the exact point where the first chunk stops accepting matches.
            if (i == boundaryLine) line = line.PadRight(60, '.') + new string('Q', 25);
            builder.Append(line.PadRight(lineLength - 1, '.')).Append('\n');
        }
        Write("big.log", builder.ToString());

        var result = Single(await Search(@"MARKER-\d+", regex: true), "big.log");
        Assert.Equal(markers.Count, result.MatchCount);
        Assert.Equal(markers.Select(m => $"MARKER-{m}"), result.Hits.SelectMany(Matched));
        Assert.Equal(markers, result.Hits.Select(h => h.Context[h.HitLine].Number));

        var crossing = Single(await Search("Q+", regex: true), "big.log");
        Assert.Equal(1, crossing.MatchCount);
        Assert.Equal([new string('Q', 25)], crossing.Hits.SelectMany(Matched));
        Assert.Equal(boundaryLine, crossing.Hits[0].Context[crossing.Hits[0].HitLine].Number);
    }

    [Fact]
    public async Task ReadsWordExcelPowerPointAndOpenDocumentFiles()
    {
        Fixtures.Docx(PathOf("letter.docx"), ["Dear customer,", "Please pay Invo|ice #2041 by Friday.", "Thanks"], textBox: "Boxed note about Invoice #2041");
        Fixtures.Xlsx(PathOf("budget.xlsx"), "Receivables", new()
        {
            [1] = ["Customer", "Reference", "Amount"],
            [2] = ["123 Main St\nSpringfield", "Address on two lines"],
            [18] = ["Riverbend Outfitters", "Invoice #2041", "8215.5"],
        });
        Fixtures.Pptx(PathOf("deck.pptx"), [["Agenda", "Welcome"], ["Cash collection", "Riverbend: Invoice #2041"]], firstSlideNotes: "Mention Invoice #2041 early");
        Fixtures.Odt(PathOf("memo.odt"), ["Memo", "Invoice  #2041 is late"]);
        Fixtures.Docx(PathOf("strict.docx"), ["Saved as Strict Open XML", "Invoice #2041 again"], strict: true);

        var results = await Search(@"Invoice\s+#2041", regex: true);

        // The text box is counted once although Word stores it twice.
        var letter = Single(results, "letter.docx");
        Assert.Equal(2, letter.MatchCount);
        Assert.Equal(["Paragraph 2", "Paragraph 5"], letter.Hits.Select(h => h.Location));
        Assert.Equal("Please pay Invoice #2041 by Friday.", HitText(letter.Hits[0]));
        Assert.Equal("Boxed note about Invoice #2041", HitText(letter.Hits[1]));

        var sheet = Single(results, "budget.xlsx").Hits.Single();
        Assert.Equal("Receivables · row 18", sheet.Location);
        Assert.Equal("Riverbend Outfitters │ Invoice #2041 │ 8215.5", HitText(sheet));

        Assert.Equal(["Slide 1 notes", "Slide 2"], Single(results, "deck.pptx").Hits.Select(h => h.Location).Order());
        Assert.Equal(["Paragraph 2"], Single(results, "memo.odt").Hits.Select(h => h.Location));
        Assert.Equal(["Paragraph 2"], Single(results, "strict.docx").Hits.Select(h => h.Location));
    }

    [Fact]
    public async Task ReadsOlderOfficeFormatsThroughTheWindowsFilters()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows search filters (IFilter) only exist on Windows.");
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Legacy")))
            if (!file.EndsWith(".txt")) File.Copy(file, PathOf(Path.GetFileName(file)));
        // RTF has the same problem as the binary formats: read raw, its accents and formatting split the words.
        Write("old.rtf", @"{\rtf1\ansi{\fonttbl\f0\fswiss Helvetica;}\f0\pard First paragraph\par Second has caf\'e9 cr\'e8me in it\par}");

        var results = await Search("after text|replaceMe|This is on page 1|café crème", regex: true);

        foreach (var (name, text) in new[] { ("hyperlink.doc", "after text"), ("Simple.xls", "replaceMe"), ("basic_test_ppt_file.ppt", "This is on page 1"), ("old.rtf", "café crème") })
        {
            var hit = Single(results, name).Hits[0];
            Assert.StartsWith("Paragraph ", hit.Location);
            Assert.Contains(text, HitText(hit));
        }
    }

    [Fact]
    public async Task ReadsPdfText()
    {
        Fixtures.Pdf(PathOf("contract.pdf"), ["7. Billing", "7.3 The first invoice is Invoice #1001.", "8. Term"]);

        var hit = Single(await Search("Invoice #1001"), "contract.pdf").Hits.Single();
        Assert.Equal("Page 1", hit.Location);
        Assert.Contains("Invoice #1001", HitText(hit));
        Assert.Contains("7. Billing", hit.Context.Select(c => c.Text));
    }

    [Fact]
    public async Task ReadsTextInPicturesOnlyWhenEnabled()
    {
        var picture = Fixtures.TextPicture("Quarterly report", "Invoice 2041 overdue");
        File.WriteAllBytes(PathOf("scan.png"), picture);
        Fixtures.Docx(PathOf("with-picture.docx"), ["No text match here"], picture);
        Fixtures.Xlsx(PathOf("with-picture.xlsx"), "Charts", new() { [1] = ["nothing"] }, picture);
        Fixtures.Pptx(PathOf("with-picture.pptx"), [["Title only"]], firstSlidePicture: picture);
        Fixtures.Pdf(PathOf("with-picture.pdf"), ["Plain page"], picture);

        var results = await Search("Invoice 2041");
        Assert.Equal("Text in image · line 2", Single(results, "scan.png").Hits.Single().Location);
        Assert.Equal("Paragraph 2 · text in picture", Single(results, "with-picture.docx").Hits.Single().Location);
        Assert.Equal("Charts · text in picture", Single(results, "with-picture.xlsx").Hits.Single().Location);
        Assert.Equal("Slide 1 · text in picture", Single(results, "with-picture.pptx").Hits.Single().Location);
        Assert.Equal("Page 2 · text in picture", Single(results, "with-picture.pdf").Hits.Single().Location);

        foreach (var hit in results.SelectMany(r => r.Hits))
        {
            Assert.NotNull(hit.Image);
            Assert.NotNull(hit.LineBox);
            Assert.Contains("Quarterly report", hit.Context.Select(c => c.Text));
            // The hover preview draws the picture again and outlines the matching line inside it.
            using var bitmap = Content.LoadPicture(hit.Image);
            Assert.NotNull(bitmap);
            Assert.InRange(hit.LineBox.X + hit.LineBox.Width, 1, bitmap.Width);
            Assert.InRange(hit.LineBox.Y + hit.LineBox.Height, 1, bitmap.Height);
        }

        Assert.Empty(await Search("Invoice 2041", images: false));
    }

    [Fact]
    public async Task ASearchableScanIsCountedOnce()
    {
        var scan = Fixtures.TextPicture("Quarterly report", "Invoice 2041 overdue");
        Fixtures.Pdf(PathOf("searchable-scan.pdf"), ["Cover"], scan, hiddenText: ["Quarterly report", "Invoice 2041 overdue"]);

        var result = Single(await Search("Invoice 2041"), "searchable-scan.pdf");
        Assert.Equal(1, result.MatchCount);
        Assert.Equal("Page 2", result.Hits.Single().Location);
    }

    [Fact]
    public async Task RemembersTextReadFromPictures()
    {
        File.WriteAllBytes(PathOf("scan.png"), Fixtures.TextPicture("Invoice 2041 overdue"));
        Assert.Single(await Search("2041"));

        // Change what was remembered: the next search must use the remembered text instead of reading the picture again.
        string remembered = Assert.Single(Directory.GetFiles(_cache, "*.json", SearchOption.AllDirectories));
        File.WriteAllText(remembered, File.ReadAllText(remembered).Replace("2041", "9999"));

        Assert.Empty(await Search("2041"));
        Assert.Single(await Search("9999"));
    }

    [Fact]
    public async Task SkipsIgnoredFoldersAndReportsFileNameMatches()
    {
        Write("src/app.js", "const invoice = 1;");
        Write("node_modules/lib/index.js", "const invoice = 2;");
        Write("Invoice 2041.txt", "nothing relevant inside");

        var results = await Search("invoice");
        Assert.Equal(["Invoice 2041.txt", "app.js"], results.Select(r => Path.GetFileName(r.Path)).Order(StringComparer.Ordinal));

        var named = Single(results, "Invoice 2041.txt");
        Assert.Equal([new TextRange(0, 7)], named.NameMatches);
        Assert.Equal(0, named.MatchCount);

        var withNodeModules = await Search("invoice", settings: new Settings { IgnoredFolders = [] });
        Assert.Equal(3, withNodeModules.Count);

        // A full path skips only that folder.
        var withoutSrc = await Search("invoice", settings: new Settings { IgnoredFolders = [Path.Combine(_dir, "src")] });
        Assert.Equal(["Invoice 2041.txt", "index.js"], withoutSrc.Select(r => Path.GetFileName(r.Path)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnOfficeFormatWindowsCannotReadIsCountedAsUnreadable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows search filters (IFilter) only exist on Windows.");
        // OneNote's filter comes with Microsoft Office; without it (or for a damaged file) nothing can read this.
        File.WriteAllBytes(PathOf("notebook.one"), [0xE4, 0x52, 0x5C, 0x7B, 0x8C, 0xD8, 0xA7, 0x4D, 1, 2, 3, 4, 5, 6, 7, 8]);

        var engine = new SearchEngine(new SearchQuery("needle", false, false, false), _dir, null, new Settings(), new ImageText(_cache));
        await engine.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, engine.Unreadable);
    }

    [Fact]
    public async Task OnlineOnlyCloudFilesAreReportedInsteadOfDownloaded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Cloud-file attributes are Windows file attributes.");
        string cloud = Write("cloud.txt", "needle");
        File.SetAttributes(cloud, FileAttributes.Offline);

        var engine = new SearchEngine(new SearchQuery("needle", false, false, false), _dir, null, new Settings(), new ImageText(_cache));
        await engine.RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(engine.Results);
        var skipped = Assert.Single(engine.Skipped);
        Assert.Equal(cloud, skipped.Path);
        Assert.Equal("Online-only file, not downloaded", skipped.Reason);
    }

    [Fact]
    public void SettingsAreKeptBetweenRuns()
    {
        string file = Path.Combine(_dir, "settings.json");
        var defaults = Settings.Load(file);
        Assert.Equal(3, defaults.ContextLines);
        Assert.Contains("node_modules", defaults.IgnoredFolders);

        new Settings { IgnoredFolders = ["build"], ContextLines = 5, HistoryLimit = 20 }.Save(file);
        var loaded = Settings.Load(file);
        Assert.Equal(["build"], loaded.IgnoredFolders);
        Assert.Equal((5, 20), (loaded.ContextLines, loaded.HistoryLimit));
    }

    [Fact]
    public async Task SearchingOneSelectedFileIgnoresTheRestOfTheFolder()
    {
        string selected = Write("one.txt", "needle\nneedle\n");
        Write("two.txt", "needle\n");

        var results = await Search("needle", singleFile: selected);
        Assert.Equal(2, Single(results, "one.txt").MatchCount);
        Assert.Single(results);
    }

    [Fact]
    public async Task SkippingAFileLetsTheRestOfTheSearchFinish()
    {
        Write("small.txt", "needle");
        // Big enough to still be reading when the skip arrives.
        using (var huge = File.Create(PathOf("huge.txt")))
        {
            var block = Encoding.ASCII.GetBytes(new string('x', 1024 * 1024));
            for (int i = 0; i < 300; i++) huge.Write(block);
            huge.Write("\nneedle\n"u8);
        }

        var engine = new SearchEngine(new SearchQuery("needle", false, false, false), _dir, null, new Settings(), new ImageText(_cache));
        var run = engine.RunAsync(TestContext.Current.CancellationToken);
        while (!run.IsCompleted)
        {
            if (engine.Active.Values.FirstOrDefault(a => a.Path.EndsWith("huge.txt")) is { } huge) huge.Skip();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        await run;

        var skipped = Assert.Single(engine.Skipped);
        Assert.EndsWith("huge.txt", skipped.Path);
        Assert.Equal("small.txt", Path.GetFileName(Assert.Single(engine.Results).Path));
        Assert.Equal(2, engine.FilesDone);
    }

    [Fact]
    public void HistoryKeepsTheNewestSearchesWithTheirResults()
    {
        var store = new HistoryStore(Path.Combine(_dir, "history"));
        var result = new FileResult { Path = @"C:\a.txt", MatchCount = 1, Hits = [new Hit("Line 1", [new ContextLine(1, "needle", [new TextRange(0, 6)])], 0)] };
        for (int i = 0; i < 3; i++)
        {
            var summary = new HistorySummary($"id{i}", DateTimeOffset.UnixEpoch.AddDays(i), 1.5, new SearchQuery($"q{i}", true, false, true), @"C:\", null, 0, 1, 1, 0, false);
            store.Save(summary, [result], keep: 2);
        }

        Assert.Equal(["q2", "q1"], store.List().Select(s => s.Query.Text));
        var loaded = Assert.Single(store.LoadResults("id2"));
        Assert.Equal("needle", loaded.Hits[0].Context[0].Text);
        Assert.Equal(new TextRange(0, 6), loaded.Hits[0].Context[0].Matches[0]);
        Assert.Empty(store.LoadResults("id0"));
    }

    [Fact]
    public void InvalidRegexIsReportedBeforeSearching()
    {
        Assert.NotNull(Matcher.Validate(new SearchQuery("(unclosed", true, false, false)));
        Assert.Null(Matcher.Validate(new SearchQuery("(unclosed", false, false, false)));
        Assert.NotNull(Matcher.Validate(new SearchQuery("", false, false, false)));
    }
}
