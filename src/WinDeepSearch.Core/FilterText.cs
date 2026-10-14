using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace WinDeepSearch.Core;

/// <summary>
/// Text from older Office formats (.doc, .xls, .ppt) and .rtf through the Windows search filters (IFilter),
/// the same components Windows Search uses to index them.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FilterText
{
    const int MaxChars = 64 * 1024 * 1024;

    public static IEnumerable<TextSegment> Read(string path, CancellationToken ct)
    {
        string text = Extract(path, ct);
        var lines = text.Split('\n').ToList();
        return OfficeText.Segments(lines, n => $"Paragraph {n}");
    }

    static string Extract(string path, CancellationToken ct)
    {
        int hr = LoadIFilter(path, 0, out nint unknown);
        if (hr < 0 || unknown == 0) throw new IOException($"No Windows filter can read this file (0x{hr:X8}).");

        var filter = (IFilter)Marshal.GetObjectForIUnknown(unknown);
        Marshal.Release(unknown);
        const int bufferChars = 8192;
        nint buffer = Marshal.AllocCoTaskMem(bufferChars * sizeof(char));
        try
        {
            const uint flags = 1 /* canon paragraphs */ | 2 /* canon hyphens */ | 4 /* canon spaces */ | 8 /* hard line breaks */ | 16 /* index attributes */;
            hr = filter.Init(flags, 0, 0, out _);
            if (hr < 0) throw new IOException($"The Windows filter couldn't read this file (0x{hr:X8}).");

            var text = new StringBuilder();
            while (text.Length < MaxChars)
            {
                ct.ThrowIfCancellationRequested();
                hr = filter.GetChunk(out var chunk);
                if (hr is FILTER_E_EMBEDDING_UNAVAILABLE or FILTER_E_LINK_UNAVAILABLE) continue; // Skip that object, keep reading.
                if (hr < 0) break; // The end, or an error the filter won't recover from: keep what was read.
                if ((chunk.Flags & CHUNK_TEXT) == 0) continue;
                if (chunk.BreakType != 0 && text.Length > 0) text.Append('\n');

                while (true)
                {
                    uint count = bufferChars;
                    hr = filter.GetText(ref count, buffer);
                    if (hr < 0) break; // FILTER_E_NO_MORE_TEXT or a read error ends this chunk.
                    text.Append(Marshal.PtrToStringUni(buffer, (int)Math.Min(count, bufferChars)));
                    if (hr == FILTER_S_LAST_TEXT) break;
                }
            }
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\v', '\n').Replace('\f', '\n').ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
            Marshal.ReleaseComObject(filter);
        }
    }

    const int FILTER_E_EMBEDDING_UNAVAILABLE = unchecked((int)0x80041707);
    const int FILTER_E_LINK_UNAVAILABLE = unchecked((int)0x80041708);
    const int FILTER_S_LAST_TEXT = 0x00041709;
    const int CHUNK_TEXT = 1;

    [DllImport("query.dll", CharSet = CharSet.Unicode)]
    static extern int LoadIFilter(string path, nint outer, out nint filter);

    [ComImport, Guid("89BCB740-6119-101A-BCB7-00DD010655AF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFilter
    {
        [PreserveSig] int Init(uint flags, uint attributeCount, nint attributes, out uint resultFlags);
        [PreserveSig] int GetChunk(out StatChunk chunk);
        [PreserveSig] int GetText(ref uint count, nint buffer);
        [PreserveSig] int GetValue(out nint value);
        [PreserveSig] int BindRegion(FilterRegion origin, ref Guid riid, out nint unknown);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StatChunk
    {
        public uint Id;
        public int BreakType;
        public int Flags;
        public uint Locale;
        public Guid PropertySet;
        public uint PropertyKind;
        public nint PropertyValue;
        public uint SourceId;
        public uint SourceStart;
        public uint SourceLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FilterRegion
    {
        public uint Chunk;
        public uint Start;
        public uint Extent;
    }
}
