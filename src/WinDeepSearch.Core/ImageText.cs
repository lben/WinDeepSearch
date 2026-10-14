using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;

namespace WinDeepSearch.Core;

/// <summary>Reads text in pictures with PaddleOCR and remembers what it read on disk.</summary>
public sealed class ImageText(string cacheDir)
{
    /// <summary>Pictures smaller than this on both sides (icons) are not read.</summary>
    public const int MinSide = 48;

    // One recognizer, used by one file at a time; it already spreads each picture over all cores.
    static readonly SemaphoreSlim Gate = new(1, 1);
    static RapidOcr? _engine;

    public string CacheDir { get; } = cacheDir;

    internal sealed record OcrLine(string Text, PixelRect Box);

    /// <summary>Identifies one picture in one version of a file, for the cache.</summary>
    internal static string Key(string path, string part)
    {
        var info = new FileInfo(path);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{part}";
    }

    /// <summary>Returns the picture's text as a segment, or null when it has none.</summary>
    internal TextSegment? Read(string key, Func<SKBitmap?> decode, ImageSource source, Func<int, string> locate,
        ActiveFile active, string detail, Action<double>? progress, CancellationToken ct)
    {
        string file = CacheFile(key);
        var lines = Load(file);
        if (lines == null)
        {
            lines = Recognize(decode, active, detail, progress, ct);
            Save(file, lines);
        }
        if (lines.Length == 0) return null;

        return new TextSegment
        {
            Text = string.Join('\n', lines.Select(l => l.Text)),
            Locate = locate,
            Image = source,
            LineBoxes = lines.Select(l => l.Box).ToArray(),
        };
    }

    static OcrLine[] Recognize(Func<SKBitmap?> decode, ActiveFile active, string detail, Action<double>? progress, CancellationToken ct)
    {
        SKBitmap? bitmap;
        try
        {
            bitmap = decode();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return []; // A damaged picture inside an otherwise readable file.
        }

        using (bitmap)
        {
            if (bitmap == null || Math.Max(bitmap.Width, bitmap.Height) < MinSide) return [];

            active.Detail = "Waiting for another file to finish reading text in pictures";
            Gate.Wait(ct);
            try
            {
                active.Detail = detail;
                _engine ??= CreateEngine();
                var report = progress == null ? null : new LineProgress(progress);
                var result = _engine.Detect(bitmap, RapidOcrOptions.Default, report, ct);
                return result.TextBlocks
                    .Where(b => !string.IsNullOrWhiteSpace(b.Text))
                    .Select(b => new OcrLine(b.Text.Trim(), Bounds(b.BoxPoints)))
                    .ToArray();
            }
            finally
            {
                Gate.Release();
                active.Detail = null;
            }
        }
    }

    static RapidOcr CreateEngine()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, RapidOcr.ModelsFolderName, RapidOcr.ModelsVersion);
        var engine = new RapidOcr();
        engine.InitModels(
            Path.Combine(dir, RapidOcr.DefaultDetModelPath),
            Path.Combine(dir, RapidOcr.DefaultClsModelPath),
            Path.Combine(dir, RapidOcr.DefaultRecModelPath),
            Path.Combine(dir, RapidOcr.DefaultKeysFilePath));
        return engine;
    }

    static PixelRect Bounds(SKPointI[] points)
    {
        int left = points.Min(p => p.X), top = points.Min(p => p.Y);
        return new PixelRect(left, top, points.Max(p => p.X) - left, points.Max(p => p.Y) - top);
    }

    string CacheFile(string key)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(CacheDir, hash[..2], hash + ".json");
    }

    static OcrLine[]? Load(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<OcrLine[]>(File.ReadAllBytes(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    static void Save(string file, OcrLine[] lines)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(lines));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not remembering only costs time on the next search.
        }
    }

    sealed class LineProgress(Action<double> report) : IProgress<(int Completed, int Total)>
    {
        public void Report((int Completed, int Total) value)
        {
            if (value.Total > 0) report(value.Completed / (double)value.Total);
        }
    }
}
