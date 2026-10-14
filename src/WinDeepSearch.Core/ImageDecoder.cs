using SkiaSharp;

namespace WinDeepSearch.Core;

public static class ImageDecoder
{
    /// <summary>Larger pictures are scaled down before reading, so one huge image can't exhaust memory.</summary>
    public const long MaxPixels = 60_000_000;

    /// <summary>
    /// Decodes formats SkiaSharp can't read, such as TIFF and HEIC. The Windows app plugs in the Windows
    /// imaging codecs here. Returns the requested frame (or null) and the number of frames; frame -1 only counts.
    /// </summary>
    public static Func<Stream, int, (SKBitmap? Frame, int FrameCount)>? Fallback { get; set; }

    /// <summary>
    /// Decodes frame <paramref name="index"/>, upright, and reports how many frames the image has.
    /// Frame -1 only counts frames.
    /// </summary>
    public static (SKBitmap? Bitmap, int FrameCount) Decode(Stream stream, int index)
    {
        using var data = SKData.Create(stream);
        if (data == null) return (null, 0);
        using (var codec = SKCodec.Create(data))
        {
            if (codec != null) return (index == 0 ? DecodeUpright(codec) : null, 1);
        }
        return Fallback?.Invoke(data.AsStream(), index) ?? (null, 0);
    }

    static SKBitmap? DecodeUpright(SKCodec codec)
    {
        var info = codec.Info;
        long pixels = (long)info.Width * info.Height;
        SKBitmap? bitmap;
        if (pixels <= MaxPixels)
        {
            bitmap = SKBitmap.Decode(codec);
        }
        else
        {
            var size = codec.GetScaledDimensions((float)Math.Sqrt(MaxPixels / (double)pixels));
            if ((long)size.Width * size.Height > MaxPixels) return null;
            bitmap = SKBitmap.Decode(codec, info.WithSize(size.Width, size.Height));
        }
        if (bitmap == null) return null;

        // Phone photos are often stored sideways with a rotation flag; the reader needs them upright.
        int degrees = codec.EncodedOrigin switch
        {
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0,
        };
        return degrees == 0 ? bitmap : Rotate(bitmap, degrees);
    }

    static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        bool swap = degrees != 180;
        var rotated = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0);
        }
        source.Dispose();
        return rotated;
    }
}
