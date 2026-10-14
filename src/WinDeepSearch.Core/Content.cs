using System.IO.Compression;
using SkiaSharp;

namespace WinDeepSearch.Core;

/// <summary>Sends each file to the reader that understands it.</summary>
public static class Content
{
    static readonly HashSet<string> OfficeExtensions = [".docx", ".docm", ".dotx", ".dotm", ".xlsx", ".xlsm", ".xltx", ".xltm", ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm"];
    static readonly HashSet<string> OpenDocumentExtensions = [".odt", ".ott", ".ods", ".ots", ".odp", ".otp"];
    static readonly HashSet<string> FilterExtensions = [".doc", ".dot", ".xls", ".xlt", ".ppt", ".pot", ".pps", ".rtf"];
    static readonly HashSet<string> ImageExtensions = [".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif"];

    internal static void Search(string path, Matcher matcher, ImageText? images, ActiveFile active, FileResult result, CancellationToken ct)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        IEnumerable<TextSegment>? segments =
            ext == ".pdf" ? Pdf(path, images, active, ct)
            : OfficeExtensions.Contains(ext) ? OfficeText.Read(path, images, active, ct)
            : OpenDocumentExtensions.Contains(ext) ? OpenDocumentText.Read(path, images, active, ct)
            : FilterExtensions.Contains(ext) && OperatingSystem.IsWindows() ? FilterText.Read(path, ct)
            : ImageExtensions.Contains(ext) ? (images == null ? [] : ImageFile(path, ext, images, active, ct))
            : null;

        if (segments == null)
        {
            TextFiles.Search(path, matcher, active, result, ct);
            return;
        }
        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();
            matcher.Search(segment, result);
        }
    }

    /// <summary>Loads the picture behind an OCR match again, for the hover preview.</summary>
    public static SKBitmap? LoadPicture(ImageSource source)
    {
        switch (source.Kind)
        {
            case "file":
                using (var stream = Files.OpenRead(source.Path))
                    return ImageDecoder.Decode(stream, source.Index).Bitmap;
            case "zip":
                using (var zip = new ZipArchive(Files.OpenRead(source.Path), ZipArchiveMode.Read))
                {
                    if (source.Entry == null || zip.GetEntry(source.Entry) is not { } entry) return null;
                    using var stream = entry.Open();
                    return ImageDecoder.Decode(stream, 0).Bitmap;
                }
            case "pdf" when source.Region != null:
                using (var pdf = new PdfFile(source.Path))
                    return pdf.RenderRegion(source.Index, source.Region, source.Dpi);
            default:
                return null;
        }
    }

    static IEnumerable<TextSegment> Pdf(string path, ImageText? images, ActiveFile active, CancellationToken ct)
    {
        using var pdf = new PdfFile(path);
        for (int index = 0; index < pdf.PageCount; index++)
        {
            ct.ThrowIfCancellationRequested();
            int page = index + 1, pageIndex = index;
            var (text, pictures) = pdf.ReadPage(index, images != null);
            if (text.Length > 0) yield return new TextSegment { Text = text, Locate = _ => $"Page {page}" };

            foreach (var picture in pictures)
            {
                string label = picture.CoversPage ? $"Page {page} · scanned page" : $"Page {page} · text in picture";
                var segment = images!.Read(
                    ImageText.Key(path, $"pdf:{index}:{picture.Region}:{picture.Dpi}"),
                    () => pdf.RenderRegion(pageIndex, picture.Region, picture.Dpi),
                    new ImageSource("pdf", path, null, index, picture.Region, picture.Dpi),
                    _ => label, active, $"Reading text in pictures · page {page} of {pdf.PageCount}", null, ct);
                if (segment != null) yield return segment;
            }
            active.Progress = page / (double)pdf.PageCount;
        }
    }

    static IEnumerable<TextSegment> ImageFile(string path, string ext, ImageText images, ActiveFile active, CancellationToken ct)
    {
        // Only TIFF files commonly hold several pages; counting frames means reading the file, so skip it otherwise.
        int frames = 1;
        if (ext is ".tif" or ".tiff")
        {
            using var stream = Files.OpenRead(path);
            frames = Math.Max(1, ImageDecoder.Decode(stream, -1).FrameCount);
        }

        for (int i = 0; i < frames; i++)
        {
            ct.ThrowIfCancellationRequested();
            int frame = i;
            Func<int, string> locate = frames > 1 ? _ => $"Page {frame + 1} · text in image" : n => $"Text in image · line {n}";
            var segment = images.Read(
                ImageText.Key(path, $"frame:{frame}"),
                () =>
                {
                    using var stream = Files.OpenRead(path);
                    return ImageDecoder.Decode(stream, frame).Bitmap;
                },
                new ImageSource("file", path, null, frame, null, 0),
                locate, active,
                frames > 1 ? $"Reading text in image · page {frame + 1} of {frames}" : "Reading text in image",
                frames > 1 ? null : p => active.Progress = p,
                ct);
            if (segment != null) yield return segment;
            active.Progress = (frame + 1) / (double)frames;
        }
    }
}
