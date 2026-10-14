using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using WinDeepSearch.Core;

namespace WinDeepSearch;

/// <summary>Decodes pictures SkiaSharp can't (TIFF, HEIC and any format with a Windows codec) with Windows Imaging.</summary>
static class WicDecoder
{
    public static (SKBitmap? Frame, int FrameCount) Decode(Stream stream, int index)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        int count = decoder.Frames.Count;
        if (index < 0 || index >= count) return (null, count);

        BitmapSource frame = decoder.Frames[index];
        long pixels = (long)frame.PixelWidth * frame.PixelHeight;
        if (pixels > ImageDecoder.MaxPixels)
        {
            double scale = Math.Sqrt(ImageDecoder.MaxPixels / (double)pixels);
            frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }

        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        var bitmap = new SKBitmap(new SKImageInfo(bgra.PixelWidth, bgra.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        bgra.CopyPixels(new Int32Rect(0, 0, bgra.PixelWidth, bgra.PixelHeight), bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        return (bitmap, count);
    }
}
