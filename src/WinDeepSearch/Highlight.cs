using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WinDeepSearch.Core;
using TextRange = WinDeepSearch.Core.TextRange;

namespace WinDeepSearch;

/// <summary>
/// Text with the matched parts marked. <see cref="Whole"/>: the pattern matched only positions (lookarounds alone),
/// so the whole text is marked, in its own color.
/// </summary>
public sealed record Marked(string Text, IReadOnlyList<TextRange> Ranges, bool Whole = false)
{
    public static Marked Of(string text, IReadOnlyList<TextRange> ranges) => new(text, ranges, PositionsOnly(ranges));

    public static Marked From(ContextLine line, bool trimStart = false)
    {
        if (!trimStart) return Of(line.Text, line.Matches);
        string trimmed = line.Text.TrimStart();
        int cut = line.Text.Length - trimmed.Length;
        var ranges = line.Matches.Select(r => new TextRange(Math.Max(0, r.Start - cut), r.Length - Math.Max(0, cut - r.Start))).Where(r => r.Length > 0).ToList();
        return new Marked(trimmed, ranges, PositionsOnly(line.Matches));
    }

    static bool PositionsOnly(IReadOnlyList<TextRange> ranges) => ranges.Count > 0 && ranges.All(r => r.Length == 0);
}

/// <summary>Shows a <see cref="Marked"/> in a TextBlock, with the matches highlighted.</summary>
public static class Highlight
{
    static readonly Brush MatchBackground = Frozen(Color.FromRgb(0xFF, 0xE0, 0x8A));
    static readonly Brush MatchForeground = Frozen(Color.FromRgb(0x1B, 0x1B, 0x1B));
    static readonly Brush WholeMatchBackground = Frozen(Color.FromRgb(0xCC, 0xE4, 0xF7));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(Marked), typeof(Highlight), new PropertyMetadata(null, OnTextChanged));

    public static Marked? GetText(DependencyObject element) => (Marked?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, Marked? value) => element.SetValue(TextProperty, value);

    static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (TextBlock)d;
        block.Inlines.Clear();
        if (e.NewValue is not Marked marked) return;

        string text = marked.Text;
        if (marked.Whole)
        {
            block.Inlines.Add(new Run(text) { Background = WholeMatchBackground, Foreground = MatchForeground });
            return;
        }
        int position = 0;
        foreach (var range in marked.Ranges.OrderBy(r => r.Start))
        {
            int start = Math.Clamp(range.Start, position, text.Length);
            int end = Math.Clamp(range.Start + range.Length, start, text.Length);
            if (start > position) block.Inlines.Add(new Run(text[position..start]));
            if (end > start) block.Inlines.Add(new Run(text[start..end]) { Background = MatchBackground, Foreground = MatchForeground });
            position = end;
        }
        if (position < text.Length) block.Inlines.Add(new Run(text[position..]));
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
