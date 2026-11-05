using System.Text.RegularExpressions;

namespace WinDeepSearch.Core;

/// <summary>Finds the query in text and turns matches into hits with surrounding lines.</summary>
public sealed class Matcher
{
    /// <summary>Hits kept per file. Matches past this are still counted.</summary>
    public const int MaxHitsPerFile = 5000;

    const int MaxLineLength = 1000;
    const int LeadBeforeMatch = 200;

    readonly Regex _regex;
    readonly int _contextLines;
    readonly bool _namesOnly;

    public Matcher(SearchQuery query, int contextLines)
    {
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!query.MatchCase) options |= RegexOptions.IgnoreCase;
        // The timeout stops a pathological pattern from hanging a worker; the file is then reported as skipped.
        _regex = new Regex(query.UseRegex ? query.Text : Regex.Escape(query.Text), options, TimeSpan.FromSeconds(5));
        _contextLines = contextLines;
        _namesOnly = query.NamesOnly;
    }

    /// <summary>Returns a message explaining why the query can't run, or null when it can.</summary>
    public static string? Validate(SearchQuery query)
    {
        if (string.IsNullOrEmpty(query.Text)) return "Type some text or a regular expression first.";
        if (!query.UseRegex) return null;
        try
        {
            _ = new Regex(query.Text);
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }
    }

    public IReadOnlyList<TextRange> MatchName(string name)
    {
        var ranges = new List<TextRange>();
        bool matched = false;
        foreach (Match m in _regex.Matches(name))
        {
            matched = true;
            if (m.Length > 0) ranges.Add(new TextRange(m.Index, m.Length));
        }
        // Lookarounds alone match without covering any text. In a names-only search the name still matches (with
        // nothing to highlight); in a content search such a pattern (\b, say) would list every file by name.
        if (matched && ranges.Count == 0 && _namesOnly) ranges.Add(new TextRange(0, 0));
        return ranges;
    }

    public void Search(TextSegment segment, FileResult result) =>
        Search(segment.Text, 0, segment.Text.Length, segment, result);

    /// <summary>
    /// Records matches that start in [acceptFrom, acceptTo). Text before acceptFrom is still visible to
    /// lookbehinds and context lines; text after acceptTo lets matches and context lines run past it.
    /// Line 0 of <paramref name="segment"/> is the line that starts at text[0].
    /// Returns where the last recorded match ends (or acceptFrom), so the next window can start after it.
    /// </summary>
    internal int Search(string text, int acceptFrom, int acceptTo, TextSegment segment, FileResult result)
    {
        int lineIndex = 0, counted = 0, lastEnd = acceptFrom;
        int lineStart = -1, lineEnd = -1, hitLineIndex = 0;
        List<TextRange>? ranges = null;

        // The end of a last line with no line break after it is a position on that line too ((?<=\.)$ there); the
        // end after a final line break starts no line.
        int acceptLast = acceptTo == text.Length && text.Length > 0 && text[^1] != '\n' ? acceptTo + 1 : acceptTo;
        for (var m = _regex.Match(text, acceptFrom); m.Success && m.Index < acceptLast; m = m.NextMatch())
        {
            result.MatchCount++;
            lastEnd = m.Index + m.Length;
            lineIndex += text.AsSpan(counted, m.Index - counted).Count('\n');
            counted = m.Index;

            if (ranges == null || m.Index > lineEnd)
            {
                if (ranges != null) AddHit(text, lineStart, lineEnd, hitLineIndex, ranges, segment, result);
                lineStart = m.Index == 0 ? 0 : text.LastIndexOf('\n', m.Index - 1) + 1;
                lineEnd = LineEnd(text, lineStart);
                hitLineIndex = lineIndex;
                ranges = [];
            }
            ranges.Add(new TextRange(m.Index - lineStart, Math.Min(m.Length, lineEnd - m.Index)));
        }

        if (ranges != null) AddHit(text, lineStart, lineEnd, hitLineIndex, ranges, segment, result);
        return lastEnd;
    }

    void AddHit(string text, int lineStart, int lineEnd, int lineIndex, List<TextRange> ranges, TextSegment segment, FileResult result)
    {
        if (result.Hits.Count >= MaxHitsPerFile)
        {
            result.HitsTruncated = true;
            return;
        }

        var before = new List<(int Start, int End)>();
        for (int start = lineStart; before.Count < _contextLines && start > 0;)
        {
            int end = start - 1;
            start = end == 0 ? 0 : text.LastIndexOf('\n', end - 1) + 1;
            before.Insert(0, (start, end));
        }

        var context = new List<ContextLine>();
        for (int i = 0; i < before.Count; i++)
            context.Add(Line(text, before[i].Start, before[i].End, Number(segment, lineIndex - before.Count + i), []));

        int hitLine = context.Count;
        context.Add(Line(text, lineStart, lineEnd, Number(segment, lineIndex), ranges));

        for (int i = 1, end = lineEnd; i <= _contextLines && end + 1 < text.Length; i++)
        {
            int start = end + 1;
            end = LineEnd(text, start);
            context.Add(Line(text, start, end, Number(segment, lineIndex + i), []));
        }

        var boxes = segment.LineBoxes;
        result.Hits.Add(new Hit(
            segment.Locate(Number(segment, lineIndex)),
            context,
            hitLine,
            segment.Image,
            boxes != null && lineIndex < boxes.Length ? boxes[lineIndex] : null));
    }

    static int LineEnd(string text, int start)
    {
        int end = text.IndexOf('\n', start);
        return end < 0 ? text.Length : end;
    }

    static int Number(TextSegment segment, int lineIndex) =>
        segment.LineNumbers is { } numbers && lineIndex < numbers.Length ? numbers[lineIndex] : segment.FirstLine + lineIndex;

    /// <summary>Builds a display line, cutting very long lines down to the part around the first match.</summary>
    static ContextLine Line(string text, int start, int end, int number, IReadOnlyList<TextRange> ranges)
    {
        var line = text.AsSpan(start, end - start).TrimEnd('\r');
        if (line.Length <= MaxLineLength)
            return new ContextLine(number, line.ToString(), Clamp(ranges, 0, 0, line.Length));

        int from = ranges.Count > 0 ? Math.Max(0, Math.Min(ranges[0].Start, line.Length) - LeadBeforeMatch) : 0;
        int to = Math.Min(line.Length, from + MaxLineLength);
        string prefix = from > 0 ? "…" : "";
        string clipped = prefix + line[from..to].ToString() + (to < line.Length ? "…" : "");
        return new ContextLine(number, clipped, Clamp(ranges, from, prefix.Length, to));
    }

    static List<TextRange> Clamp(IReadOnlyList<TextRange> ranges, int from, int shift, int to)
    {
        var result = new List<TextRange>(ranges.Count);
        foreach (var r in ranges)
        {
            // A position at the very end (a lookbehind before $) still belongs to the line.
            if (r.Start > to || r.Start == to && r.Length > 0) break;
            int length = Math.Min(r.Length, to - r.Start);
            result.Add(new TextRange(r.Start - from + shift, length));
        }
        return result;
    }
}
