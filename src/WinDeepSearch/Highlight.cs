using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WinDeepSearch.Core;
using TextRange = WinDeepSearch.Core.TextRange;

namespace WinDeepSearch;

/// <summary>Text with the matched parts marked.</summary>
public sealed record Marked(string Text, IReadOnlyList<TextRange> Ranges)
{
    public static Marked From(ContextLine line, bool trimStart = false)
    {
        if (!trimStart) return new Marked(line.Text, line.Matches);
        string trimmed = line.Text.TrimStart();
        int cut = line.Text.Length - trimmed.Length;
        return new Marked(trimmed, line.Matches.Select(r => new TextRange(Math.Max(0, r.Start - cut), r.Length - Math.Max(0, cut - r.Start))).Where(r => r.Length > 0).ToList());
    }
}

/// <summary>Shows a <see cref="Marked"/> in a TextBlock, with the matches highlighted.</summary>
public static class Highlight
{
    static readonly Brush MatchBackground = Frozen(Color.FromRgb(0xFF, 0xE0, 0x8A));
    static readonly Brush MatchForeground = Frozen(Color.FromRgb(0x1B, 0x1B, 0x1B));

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
