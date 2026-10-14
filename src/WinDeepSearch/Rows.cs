using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using WinDeepSearch.Core;
using ImageSource = System.Windows.Media.ImageSource;

namespace WinDeepSearch;

public abstract class Row : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SectionRow : Row
{
    string _label = "";

    public string Label
    {
        get => _label;
        set { _label = value; Changed(); }
    }

    public override string ToString() => Label;
}

/// <summary>One file in the results: collapsed it shows the first match; expanded, its matching lines follow it.</summary>
public sealed class FileRow : Row
{
    bool _expanded;
    Preview? _preview;

    public FileRow(FileResult result, string root, int sequence)
    {
        Result = result;
        Sequence = sequence;
        string dir = Path.GetDirectoryName(result.Path) ?? "";
        Folder = dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? dir[root.Length..].TrimStart('\\', '/') : dir;
        string ext = Path.GetExtension(result.Path).TrimStart('.').ToLowerInvariant();
        Badge = ext.Length is > 0 and <= 4 ? ext.ToUpperInvariant() : "FILE";
        (BadgeBackground, BadgeForeground) = BadgeColors(ext);
        if (result.Hits.Count > 0)
        {
            FirstLocation = result.Hits[0].Location;
            FirstLine = Marked.From(result.Hits[0].Context[result.Hits[0].HitLine], trimStart: true);
        }
    }

    public FileResult Result { get; }
    public int Sequence { get; }
    public bool IsNameMatch => Result.NameMatches.Count > 0;
    public Marked Name => new(Path.GetFileName(Result.Path), Result.NameMatches);
    public string Folder { get; }
    public string Badge { get; }
    public Brush BadgeBackground { get; }
    public Brush BadgeForeground { get; }
    public bool HasHits => Result.Hits.Count > 0;
    public bool HasNoHits => !HasHits;
    public string FirstLocation { get; } = "";
    public Marked? FirstLine { get; }
    public string CountText => Result.MatchCount switch
    {
        0 => "Name only",
        1 => "1 match",
        var n => n.ToString("N0", CultureInfo.InvariantCulture) + " matches",
    };

    /// <summary>Rows shown under this file while it is expanded.</summary>
    public int ChildRows { get; set; }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            Changed();
            Changed(nameof(ShowFirst));
            Changed(nameof(ChevronAngle));
            Changed(nameof(HasPreview));
        }
    }

    public bool ShowFirst => HasHits && !IsExpanded;
    public double ChevronAngle => IsExpanded ? 0 : -90;

    /// <summary>Hovering a collapsed file shows its first two matches; an expanded file lists them instead.</summary>
    public bool HasPreview => HasHits && !IsExpanded;
    public Preview Preview => _preview ??= new Preview(this, Result.Hits.Take(2).ToList(), Result.Hits.Count - 2);

    public override string ToString() => $"{Path.GetFileName(Result.Path)} · {CountText}";

    static (Brush, Brush) BadgeColors(string ext) => ext switch
    {
        "pdf" => Pair(0xFDE7E9, 0xA4262C),
        "doc" or "docx" or "docm" or "dotx" or "rtf" or "odt" => Pair(0xE6EEFA, 0x1B5BAE),
        "xls" or "xlsx" or "xlsm" or "xltx" or "ods" or "csv" => Pair(0xE3F2E8, 0x0E6B38),
        "ppt" or "pptx" or "pptm" or "ppsx" or "odp" => Pair(0xFBE9E0, 0xA63D1E),
        "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "tif" or "tiff" or "heic" => Pair(0xEFE7F7, 0x6B2FA0),
        _ => Pair(0xEEEEEE, 0x424242),
    };

    static (Brush, Brush) Pair(int background, int foreground) => (Solid(background), Solid(foreground));

    static Brush Solid(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}

public sealed class HitRow(FileRow file, Hit hit) : Row
{
    Preview? _preview;

    public FileRow File { get; } = file;
    public Hit Hit { get; } = hit;
    public string Location => Hit.Location;
    public Marked Line { get; } = Marked.From(hit.Context[hit.HitLine], trimStart: true);
    public Preview Preview => _preview ??= new Preview(File, [Hit], 0);

    public override string ToString() => $"{Location} · {Line.Text}";
}

public sealed class MoreRow(FileRow file, int count) : Row
{
    public FileRow File { get; } = file;
    public string Label { get; } = $"Show {count.ToString("N0", CultureInfo.InvariantCulture)} more matching {(count == 1 ? "line" : "lines")}";

    public override string ToString() => Label;
}

/// <summary>A file in the "being searched" panel.</summary>
public sealed class ActiveRow(ActiveFile file, string root) : Row
{
    public ActiveFile File { get; } = file;
    public string Name { get; } = Path.GetFileName(file.Path);
    public string Folder { get; } = Path.GetDirectoryName(file.Path) is { } dir && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? dir[root.Length..].TrimStart('\\', '/') : Path.GetDirectoryName(file.Path) ?? "";
    public string Size { get; } = Sizes.Format(file.Length);
    public string SkipName => "Skip " + Name;
    public double Percent { get; private set; }
    public string Stats { get; private set; } = "";

    public void Refresh()
    {
        double progress = File.Progress;
        double seconds = File.Elapsed.TotalSeconds;
        string percent = (progress * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        string? detail = File.Detail;
        if (detail != null)
        {
            Stats = $"{percent} · {detail}";
        }
        else
        {
            double rate = progress * File.Length / Math.Max(seconds, 0.001);
            string left = progress > 0.01 ? " · about " + Sizes.Duration(seconds * (1 - progress) / progress) + " left" : "";
            Stats = $"{percent} · {Sizes.Format((long)rate)}/s{left}";
        }
        Percent = progress * 100;
        Changed(nameof(Percent));
        Changed(nameof(Stats));
    }
}

public static class Sizes
{
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };

    public static string Duration(double seconds) => seconds switch
    {
        < 10 => seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s",
        < 60 => seconds.ToString("0", CultureInfo.InvariantCulture) + " s",
        _ => $"{(int)(seconds / 60)} min {(int)(seconds % 60):00} s",
    };
}

/// <summary>What the hover preview shows.</summary>
public sealed class Preview(FileRow file, IReadOnlyList<Hit> hits, int more)
{
    public string Title { get; } = Path.GetFileName(file.Result.Path);
    public string Folder { get; } = "  " + file.Folder;
    public IReadOnlyList<PreviewBlock> Blocks { get; } = hits.Select(h => new PreviewBlock(h)).ToList();
    public bool HasMore => more > 0;
    public string More => $"+{more.ToString("N0", CultureInfo.InvariantCulture)} more matching {(more == 1 ? "line" : "lines")} in this file · use the arrow to list them all";
}

public sealed record PreviewLine(string Number, Marked Text, bool IsHit);

public sealed class PreviewBlock(Hit hit) : INotifyPropertyChanged
{
    ImageSource? _picture;
    bool _loading;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Location => hit.Location;

    public IReadOnlyList<PreviewLine> Lines { get; } = hit.Context
        .Select((line, i) => new PreviewLine(line.Number.ToString(CultureInfo.InvariantCulture), Marked.From(line), i == hit.HitLine))
        .ToList();

    public bool HasPicture => hit.Image != null && hit.LineBox != null;

    /// <summary>The part of the picture around the matching line, outlined. Loaded the first time it is shown.</summary>
    public ImageSource? Picture
    {
        get
        {
            if (HasPicture && !_loading)
            {
                _loading = true;
                _ = Task.Run(Load);
            }
            return _picture;
        }
    }

    void Load()
    {
        try
        {
            using var source = Content.LoadPicture(hit.Image!);
            if (source == null) return;
            _picture = Crop(source, hit.LineBox!);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
        }
        catch (Exception)
        {
            // The file changed or went away since the search; the preview still shows the text lines.
        }
    }

    static BitmapSource Crop(SKBitmap source, PixelRect box)
    {
        int margin = Math.Max(box.Height * 3, 40);
        int left = Math.Max(0, box.X - margin), top = Math.Max(0, box.Y - margin);
        int right = Math.Min(source.Width, box.X + box.Width + margin), bottom = Math.Min(source.Height, box.Y + box.Height + margin);
        int width = Math.Max(1, right - left), height = Math.Max(1, bottom - top);

        using var crop = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(crop))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, -left, -top);
            float stroke = Math.Max(3, height / 60f);
            using var pen = new SKPaint { Color = new SKColor(0xF0, 0x8C, 0x00), Style = SKPaintStyle.Stroke, StrokeWidth = stroke, IsAntialias = true };
            canvas.DrawRect(box.X - left - stroke, box.Y - top - stroke, box.Width + 2 * stroke, box.Height + 2 * stroke, pen);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, crop.GetPixels(), crop.ByteCount, crop.RowBytes);
        bitmap.Freeze();
        return bitmap;
    }
}
