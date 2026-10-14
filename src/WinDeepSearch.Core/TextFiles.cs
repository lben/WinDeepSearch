using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace WinDeepSearch.Core;

/// <summary>Recognises plain-text files by their content and searches them in chunks of any size.</summary>
internal static class TextFiles
{
    const int SampleBytes = 8192;
    internal const int ChunkChars = 4 * 1024 * 1024;
    // Kept after the accepted range so matches and the context lines below them can run past it.
    internal const int LookaheadChars = 64 * 1024;
    // Kept before the accepted range for lookbehinds and the context lines above a match.
    const int LookbehindChars = 4 * 1024;

    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static void Search(string path, Matcher matcher, ActiveFile active, FileResult result, CancellationToken ct)
    {
        using var stream = Files.OpenRead(path);
        var sample = new byte[SampleBytes];
        int sampled = stream.ReadAtLeast(sample, SampleBytes, throwOnEndOfStream: false);
        var (encoding, bom) = Detect(sample.AsSpan(0, sampled));
        if (encoding == null) return;

        stream.Position = bom;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        var buffer = new char[ChunkChars];
        string carry = "";
        int acceptFrom = 0, firstLine = 1;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = reader.ReadBlock(buffer, 0, ChunkChars);
            bool end = read < ChunkChars;
            string window = string.Concat(carry, buffer.AsSpan(0, read));
            int acceptTo = end ? window.Length : window.Length - LookaheadChars;

            int lastEnd = matcher.Search(window, acceptFrom, acceptTo, new TextSegment { Text = window, Locate = LineLabel, FirstLine = firstLine }, result);
            if (stream.Length > 0) active.Progress = Math.Min(1, stream.Position / (double)stream.Length);
            if (end) return;

            int keepFrom = window.LastIndexOf('\n', acceptTo - LookbehindChars) + 1;
            // A huge line with no break nearby: carry a fixed amount instead of the whole line.
            if (keepFrom < acceptTo - 4 * LookbehindChars) keepFrom = acceptTo - LookbehindChars;
            firstLine += window.AsSpan(0, keepFrom).Count('\n');
            carry = window[keepFrom..];
            // A match that ran past acceptTo was already counted; the next window starts after it.
            acceptFrom = Math.Max(acceptTo, lastEnd) - keepFrom;
        }
    }

    static string LineLabel(int line) => "Line " + line.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Returns the encoding and byte-order-mark length, or a null encoding for binary content.</summary>
    internal static (Encoding? Encoding, int Bom) Detect(ReadOnlySpan<byte> sample)
    {
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return (new UTF8Encoding(false), 3);
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0, 0])) return (new UTF32Encoding(false, false), 4);
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return (new UnicodeEncoding(false, false), 2);
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return (new UnicodeEncoding(true, false), 2);

        if (sample.Contains((byte)0))
        {
            // UTF-16 without a byte-order mark: ASCII-range text leaves a zero in every other byte.
            int pairs = sample.Length / 2, zeroOdd = 0, zeroEven = 0;
            for (int i = 0; i + 1 < sample.Length; i += 2)
            {
                if (sample[i] == 0) zeroEven++;
                if (sample[i + 1] == 0) zeroOdd++;
            }
            if (pairs > 0 && zeroOdd > pairs * 0.3 && zeroEven < pairs * 0.05) return (new UnicodeEncoding(false, false), 0);
            if (pairs > 0 && zeroEven > pairs * 0.3 && zeroOdd < pairs * 0.05) return (new UnicodeEncoding(true, false), 0);
            return (null, 0);
        }

        int control = 0;
        foreach (byte b in sample)
            if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1B)) control++;
        if (control > sample.Length / 50) return (null, 0);

        // The sample may end part-way through a multi-byte character.
        for (int cut = 0; cut <= 3 && cut <= sample.Length; cut++)
            if (Utf8.IsValid(sample[..^cut])) return (new UTF8Encoding(false), 0);

        return (Encoding.GetEncoding(1252), 0);
    }
}
