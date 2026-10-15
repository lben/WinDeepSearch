using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace WinDeepSearch.Core;

/// <summary>
/// A PDF opened with PDFium. PDFium is not thread-safe, so every call into it holds one process-wide lock;
/// OCR of the rendered pictures happens outside the lock.
/// </summary>
internal sealed unsafe class PdfFile : IDisposable
{
    /// <summary>Resolution used to render pictures for reading.</summary>
    public const int RenderDpi = 200;
    const int MaxRenderSide = 4000;

    static readonly Lock Gate = new();
    static bool _initialized;

    readonly FileStream _stream;
    readonly GCHandle _self;
    readonly FileAccess* _access;
    readonly nint _document;

    public int PageCount { get; }

    public PdfFile(string path)
    {
        _stream = Files.OpenRead(path);
        _self = GCHandle.Alloc(this);
        _access = (FileAccess*)NativeMemory.AllocZeroed((nuint)sizeof(FileAccess));
        try
        {
            if (OperatingSystem.IsWindows() && _stream.Length > uint.MaxValue)
                throw new IOException("PDFs over 4 GB can't be read.");
            _access->FileLength = new CULong((nuint)_stream.Length);
            _access->GetBlock = &GetBlock;
            _access->Param = GCHandle.ToIntPtr(_self);

            lock (Gate)
            {
                if (!_initialized)
                {
                    FPDF_InitLibrary();
                    _initialized = true;
                }
                _document = FPDF_LoadCustomDocument(_access, null);
                if (_document == 0) throw new IOException((int)FPDF_GetLastError().Value switch
                {
                    3 => "Damaged: not a valid PDF",
                    4 => "Protected with a password",
                    5 => "Uses an encryption the PDF reader doesn't support",
                    var code => $"The PDF reader couldn't open it (error {code})",
                });
                PageCount = FPDF_GetPageCount(_document);
            }
        }
        catch
        {
            Release();
            throw;
        }
    }

    public sealed record Picture(PixelRect Region, int Dpi, bool CoversPage);

    /// <summary>Returns the page's text and the pictures on it worth reading.</summary>
    public (string Text, List<Picture> Pictures) ReadPage(int index, bool wantPictures)
    {
        lock (Gate)
        {
            nint page = FPDF_LoadPage(_document, index);
            if (page == 0) return ("", []);
            try
            {
                return (PageText(page), wantPictures && !HasInvisibleText(page) ? Pictures(page) : []);
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Renders part of a page, given in pixels at <paramref name="dpi"/>.</summary>
    public SKBitmap RenderRegion(int index, PixelRect region, int dpi)
    {
        lock (Gate)
        {
            nint page = FPDF_LoadPage(_document, index);
            if (page == 0) throw new IOException("PDFium couldn't load the page.");
            nint bitmap = FPDFBitmap_Create(region.Width, region.Height, 0);
            try
            {
                var (width, height) = PageSize(page, dpi);
                FPDFBitmap_FillRect(bitmap, 0, 0, region.Width, region.Height, new CULong(0xFFFFFFFF));
                FPDF_RenderPageBitmap(bitmap, page, -region.X, -region.Y, width, height, 0, FPDF_ANNOT);

                var result = new SKBitmap(new SKImageInfo(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                int stride = FPDFBitmap_GetStride(bitmap);
                byte* source = (byte*)FPDFBitmap_GetBuffer(bitmap);
                byte* target = (byte*)result.GetPixels();
                for (int y = 0; y < region.Height; y++)
                    Buffer.MemoryCopy(source + y * stride, target + y * result.RowBytes, result.RowBytes, region.Width * 4);
                return result;
            }
            finally
            {
                FPDFBitmap_Destroy(bitmap);
                FPDF_ClosePage(page);
            }
        }
    }

    static string PageText(nint page)
    {
        nint textPage = FPDFText_LoadPage(page);
        if (textPage == 0) return "";
        try
        {
            int count = FPDFText_CountChars(textPage);
            if (count <= 0) return "";
            var buffer = new char[count + 1];
            fixed (char* p = buffer)
            {
                int written = FPDFText_GetText(textPage, 0, count, p);
                return new string(buffer, 0, Math.Max(0, written - 1)).ReplaceLineEndings("\n");
            }
        }
        finally
        {
            FPDFText_ClosePage(textPage);
        }
    }

    /// <summary>
    /// A searchable scan already carries the recognised text as an invisible layer over its picture; reading the
    /// picture again would count every match twice.
    /// </summary>
    static bool HasInvisibleText(nint page)
    {
        int count = FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
            if (IsInvisibleText(FPDFPage_GetObject(page, i))) return true;
        return false;
    }

    static bool IsInvisibleText(nint obj)
    {
        switch (FPDFPageObj_GetType(obj))
        {
            case FPDF_PAGEOBJ_TEXT:
                return FPDFTextObj_GetTextRenderMode(obj) == FPDF_TEXTRENDERMODE_INVISIBLE;
            case FPDF_PAGEOBJ_FORM:
                int count = FPDFFormObj_CountObjects(obj);
                for (int i = 0; i < count; i++)
                    if (IsInvisibleText(FPDFFormObj_GetObject(obj, new CULong((uint)i)))) return true;
                return false;
            default:
                return false;
        }
    }

    static List<Picture> Pictures(nint page)
    {
        var bounds = new List<(float Left, float Bottom, float Right, float Top)>();
        int count = FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++) CollectImages(FPDFPage_GetObject(page, i), bounds);

        float pageWidth = FPDF_GetPageWidthF(page), pageHeight = FPDF_GetPageHeightF(page);
        var pictures = new List<Picture>();
        foreach (var b in bounds)
        {
            // Big pictures are rendered at a lower resolution so the bitmap stays a sensible size.
            float longest = Math.Max(b.Right - b.Left, b.Top - b.Bottom) / 72f * RenderDpi;
            int dpi = longest > MaxRenderSide ? (int)(RenderDpi * MaxRenderSide / longest) : RenderDpi;
            var (width, height) = PageSize(page, dpi);

            FPDF_PageToDevice(page, 0, 0, width, height, 0, b.Left, b.Bottom, out int x1, out int y1);
            FPDF_PageToDevice(page, 0, 0, width, height, 0, b.Right, b.Top, out int x2, out int y2);
            int left = Math.Clamp(Math.Min(x1, x2), 0, width), right = Math.Clamp(Math.Max(x1, x2), 0, width);
            int top = Math.Clamp(Math.Min(y1, y2), 0, height), bottom = Math.Clamp(Math.Max(y1, y2), 0, height);
            if (right - left < ImageText.MinSide && bottom - top < ImageText.MinSide) continue;

            var region = new PixelRect(left, top, right - left, bottom - top);
            if (pictures.Any(p => p.Dpi == dpi && p.Region == region)) continue;
            bool coversPage = (b.Right - b.Left) * (b.Top - b.Bottom) > 0.7f * pageWidth * pageHeight;
            pictures.Add(new Picture(region, dpi, coversPage));
        }
        return pictures;
    }

    static void CollectImages(nint obj, List<(float, float, float, float)> bounds)
    {
        switch (FPDFPageObj_GetType(obj))
        {
            case FPDF_PAGEOBJ_IMAGE:
                if (FPDFImageObj_GetImagePixelSize(obj, out uint w, out uint h) != 0 && w < ImageText.MinSide && h < ImageText.MinSide) return;
                if (FPDFPageObj_GetBounds(obj, out float left, out float bottom, out float right, out float top) != 0)
                    bounds.Add((left, bottom, right, top));
                break;
            case FPDF_PAGEOBJ_FORM:
                int count = FPDFFormObj_CountObjects(obj);
                for (int i = 0; i < count; i++) CollectImages(FPDFFormObj_GetObject(obj, new CULong((uint)i)), bounds);
                break;
        }
    }

    static (int Width, int Height) PageSize(nint page, int dpi) =>
        ((int)Math.Round(FPDF_GetPageWidthF(page) * dpi / 72.0), (int)Math.Round(FPDF_GetPageHeightF(page) * dpi / 72.0));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int GetBlock(nint param, CULong position, byte* buffer, CULong size)
    {
        try
        {
            var file = (PdfFile)GCHandle.FromIntPtr(param).Target!;
            file._stream.Position = (long)position.Value;
            file._stream.ReadExactly(new Span<byte>(buffer, checked((int)size.Value)));
            return 1;
        }
        catch
        {
            return 0; // PDFium treats this as a read error; an exception must not cross into native code.
        }
    }

    public void Dispose()
    {
        if (_document != 0)
        {
            lock (Gate) FPDF_CloseDocument(_document);
        }
        Release();
    }

    void Release()
    {
        NativeMemory.Free(_access);
        if (_self.IsAllocated) _self.Free();
        _stream.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileAccess
    {
        public CULong FileLength;
        public delegate* unmanaged[Cdecl]<nint, CULong, byte*, CULong, int> GetBlock;
        public nint Param;
    }

    const string Lib = "pdfium";
    const int FPDF_PAGEOBJ_TEXT = 1;
    const int FPDF_PAGEOBJ_IMAGE = 3;
    const int FPDF_PAGEOBJ_FORM = 5;
    const int FPDF_TEXTRENDERMODE_INVISIBLE = 3;
    const int FPDF_ANNOT = 0x01;

    [DllImport(Lib)] static extern void FPDF_InitLibrary();
    [DllImport(Lib)] static extern nint FPDF_LoadCustomDocument(FileAccess* access, byte* password);
    [DllImport(Lib)] static extern CULong FPDF_GetLastError();
    [DllImport(Lib)] static extern void FPDF_CloseDocument(nint document);
    [DllImport(Lib)] static extern int FPDF_GetPageCount(nint document);
    [DllImport(Lib)] static extern nint FPDF_LoadPage(nint document, int index);
    [DllImport(Lib)] static extern void FPDF_ClosePage(nint page);
    [DllImport(Lib)] static extern float FPDF_GetPageWidthF(nint page);
    [DllImport(Lib)] static extern float FPDF_GetPageHeightF(nint page);
    [DllImport(Lib)] static extern int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
    [DllImport(Lib)] static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Lib)] static extern nint FPDFText_LoadPage(nint page);
    [DllImport(Lib)] static extern void FPDFText_ClosePage(nint textPage);
    [DllImport(Lib)] static extern int FPDFText_CountChars(nint textPage);
    [DllImport(Lib)] static extern int FPDFText_GetText(nint textPage, int start, int count, char* result);
    [DllImport(Lib)] static extern int FPDFPage_CountObjects(nint page);
    [DllImport(Lib)] static extern nint FPDFPage_GetObject(nint page, int index);
    [DllImport(Lib)] static extern int FPDFPageObj_GetType(nint obj);
    [DllImport(Lib)] static extern int FPDFPageObj_GetBounds(nint obj, out float left, out float bottom, out float right, out float top);
    [DllImport(Lib)] static extern int FPDFFormObj_CountObjects(nint form);
    [DllImport(Lib)] static extern nint FPDFFormObj_GetObject(nint form, CULong index);
    [DllImport(Lib)] static extern int FPDFImageObj_GetImagePixelSize(nint image, out uint width, out uint height);
    [DllImport(Lib)] static extern int FPDFTextObj_GetTextRenderMode(nint text);
    [DllImport(Lib)] static extern nint FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Lib)] static extern int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, CULong color);
    [DllImport(Lib)] static extern nint FPDFBitmap_GetBuffer(nint bitmap);
    [DllImport(Lib)] static extern int FPDFBitmap_GetStride(nint bitmap);
    [DllImport(Lib)] static extern void FPDFBitmap_Destroy(nint bitmap);
}
