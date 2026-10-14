namespace WinDeepSearch.Core;

public sealed record SearchQuery(string Text, bool UseRegex, bool MatchCase, bool ReadImages);

public readonly record struct TextRange(int Start, int Length);

public sealed record PixelRect(int X, int Y, int Width, int Height);

/// <summary>One line shown around a match. <see cref="Matches"/> is empty except on the matching line.</summary>
public sealed record ContextLine(int Number, string Text, IReadOnlyList<TextRange> Matches);

/// <summary>
/// Where the picture behind an OCR match lives, so the hover preview can draw it again.
/// Kind "file": an image file, <see cref="Index"/> is the frame.
/// Kind "zip": a picture stored inside an Office or OpenDocument file at <see cref="Entry"/>.
/// Kind "pdf": page <see cref="Index"/> rendered at <see cref="Dpi"/>, cropped to <see cref="Region"/>.
/// </summary>
public sealed record ImageSource(string Kind, string Path, string? Entry, int Index, PixelRect? Region, int Dpi);

/// <summary>A matching line with the lines around it. <see cref="HitLine"/> indexes <see cref="Context"/>.</summary>
public sealed record Hit(string Location, IReadOnlyList<ContextLine> Context, int HitLine, ImageSource? Image = null, PixelRect? LineBox = null);

public sealed class FileResult
{
    public required string Path { get; init; }
    public IReadOnlyList<TextRange> NameMatches { get; init; } = [];
    public List<Hit> Hits { get; init; } = [];
    public int MatchCount { get; set; }
}

/// <summary>A run of text from one place in a file, searched as a unit.</summary>
public sealed class TextSegment
{
    public required string Text { get; init; }

    /// <summary>Turns a line number into the location label shown next to a match.</summary>
    public required Func<int, string> Locate { get; init; }

    public int FirstLine { get; init; } = 1;

    /// <summary>Line numbers when they are not consecutive (spreadsheet rows); indexed by line.</summary>
    public int[]? LineNumbers { get; init; }

    public ImageSource? Image { get; init; }

    /// <summary>Where each line sits in the picture; indexed by line.</summary>
    public PixelRect[]? LineBoxes { get; init; }
}

public sealed record SkippedFile(string Path, double Progress, string Reason);
