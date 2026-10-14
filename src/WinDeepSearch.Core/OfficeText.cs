using System.IO.Compression;
using System.Text;
using System.Xml;

namespace WinDeepSearch.Core;

/// <summary>Text and pictures from Word, Excel and PowerPoint files (.docx, .xlsx, .pptx and their variants).</summary>
internal static class OfficeText
{
    const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    const string V = "urn:schemas-microsoft-com:vml";
    const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    const int LinesPerSegment = 5000;

    static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true };

    public static IEnumerable<TextSegment> Read(string path, ImageText? images, ActiveFile active, CancellationToken ct)
    {
        using var zip = new ZipArchive(Files.OpenRead(path), ZipArchiveMode.Read);
        var doc = new Doc(zip, path, images, active, ct);
        IEnumerable<TextSegment> segments =
            zip.GetEntry("word/document.xml") != null ? Word(doc)
            : zip.GetEntry("xl/workbook.xml") != null ? Excel(doc)
            : zip.GetEntry("ppt/presentation.xml") != null ? PowerPoint(doc)
            : [];
        foreach (var segment in segments) yield return segment;
    }

    sealed record Doc(ZipArchive Zip, string Path, ImageText? Images, ActiveFile Active, CancellationToken Ct);

    static IEnumerable<TextSegment> Word(Doc doc)
    {
        var parts = new List<(string Entry, string Label)> { ("word/document.xml", "Paragraph") };
        foreach (var entry in doc.Zip.Entries)
        {
            string name = entry.FullName, file = name[(name.LastIndexOf('/') + 1)..];
            if (!name.StartsWith("word/", StringComparison.Ordinal) || name.Count(c => c == '/') != 1) continue;
            if (file.StartsWith("header", StringComparison.Ordinal)) parts.Add((name, "Header · paragraph"));
            else if (file.StartsWith("footer", StringComparison.Ordinal)) parts.Add((name, "Footer · paragraph"));
            else if (file is "footnotes.xml") parts.Add((name, "Footnotes · paragraph"));
            else if (file is "endnotes.xml") parts.Add((name, "Endnotes · paragraph"));
            else if (file is "comments.xml") parts.Add((name, "Comments · paragraph"));
        }

        for (int i = 0; i < parts.Count; i++)
        {
            doc.Ct.ThrowIfCancellationRequested();
            var (entry, label) = parts[i];
            var (paragraphs, pictures) = Paragraphs(doc.Zip, entry, W);
            foreach (var segment in Segments(paragraphs, n => $"{label} {n}")) yield return segment;
            foreach (var segment in PictureText(doc, entry, pictures, p => p > 0 ? $"{label} {p} · text in picture" : "Text in picture"))
                yield return segment;
            doc.Active.Progress = (i + 1) / (double)parts.Count;
        }
    }

    static IEnumerable<TextSegment> PowerPoint(Doc doc)
    {
        const string presentation = "ppt/presentation.xml";
        var rels = Relationships(doc.Zip, presentation);
        var slideIds = new List<string>();
        using (var reader = XmlReader.Create(doc.Zip.GetEntry(presentation)!.Open(), Settings))
        {
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sldId" && reader.NamespaceURI == P && reader.GetAttribute("id", R) is { } id)
                    slideIds.Add(id);
        }

        for (int i = 0; i < slideIds.Count; i++)
        {
            doc.Ct.ThrowIfCancellationRequested();
            if (!rels.TryGetValue(slideIds[i], out var slide)) continue;
            int number = i + 1;
            var (paragraphs, pictures) = Paragraphs(doc.Zip, slide.Target, A);
            foreach (var segment in Segments(paragraphs, _ => $"Slide {number}")) yield return segment;
            foreach (var segment in PictureText(doc, slide.Target, pictures, _ => $"Slide {number} · text in picture")) yield return segment;

            var notes = Relationships(doc.Zip, slide.Target).Values.FirstOrDefault(r => r.Type.EndsWith("/notesSlide", StringComparison.Ordinal));
            if (notes.Target != null)
                foreach (var segment in Segments(Paragraphs(doc.Zip, notes.Target, A).Text, _ => $"Slide {number} notes")) yield return segment;

            doc.Active.Progress = number / (double)slideIds.Count;
        }
    }

    static IEnumerable<TextSegment> Excel(Doc doc)
    {
        const string workbook = "xl/workbook.xml";
        var rels = Relationships(doc.Zip, workbook);
        var shared = SharedStrings(doc.Zip, rels.Values.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)).Target);
        var sheets = new List<(string Name, string Id)>();
        using (var reader = XmlReader.Create(doc.Zip.GetEntry(workbook)!.Open(), Settings))
        {
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet" && reader.NamespaceURI == S && reader.GetAttribute("id", R) is { } id)
                    sheets.Add((reader.GetAttribute("name") ?? "Sheet", id));
        }

        for (int i = 0; i < sheets.Count; i++)
        {
            doc.Ct.ThrowIfCancellationRequested();
            var (name, id) = sheets[i];
            if (!rels.TryGetValue(id, out var sheet) || doc.Zip.GetEntry(sheet.Target) is not { } entry) continue;

            var lines = new List<string>();
            var rows = new List<int>();
            using (var stream = entry.Open()) Rows(stream, shared, lines, rows);
            foreach (var segment in Segments(lines, n => $"{name} · row {n}", rows)) yield return segment;

            foreach (var drawing in Relationships(doc.Zip, sheet.Target).Values.Where(r => r.Type.EndsWith("/drawing", StringComparison.Ordinal)))
            {
                var (text, pictures) = Paragraphs(doc.Zip, drawing.Target, A);
                foreach (var segment in Segments(text, _ => $"{name} · text in shape")) yield return segment;
                foreach (var segment in PictureText(doc, drawing.Target, pictures, _ => $"{name} · text in picture")) yield return segment;
            }
            doc.Active.Progress = (i + 1) / (double)sheets.Count;
        }
    }

    /// <summary>Reads the text of each pictures referenced from <paramref name="part"/>, once per picture.</summary>
    static IEnumerable<TextSegment> PictureText(Doc doc, string part, List<(int Paragraph, string RelId)> pictures, Func<int, string> label)
    {
        if (doc.Images == null || pictures.Count == 0) yield break;
        var rels = Relationships(doc.Zip, part);
        var seen = new HashSet<string>();
        foreach (var (paragraph, relId) in pictures)
        {
            if (!rels.TryGetValue(relId, out var target) || !seen.Add(target.Target)) continue;
            var segment = ZipPicture(doc.Zip, target.Target, doc.Path, doc.Images, _ => label(paragraph), doc.Active, doc.Ct);
            if (segment != null) yield return segment;
        }
    }

    /// <summary>Reads the text in a picture stored inside a zip-based document.</summary>
    internal static TextSegment? ZipPicture(ZipArchive zip, string entryName, string path, ImageText images, Func<int, string> locate, ActiveFile active, CancellationToken ct)
    {
        var entry = zip.GetEntry(entryName);
        if (entry == null) return null;
        return images.Read(
            ImageText.Key(path, "zip:" + entryName),
            () =>
            {
                using var stream = entry.Open();
                return ImageDecoder.Decode(stream, 0).Bitmap;
            },
            new ImageSource("zip", path, entryName, 0, null, 0),
            locate, active, "Reading text in pictures", null, ct);
    }

    internal static IEnumerable<TextSegment> Segments(List<string> lines, Func<int, string> locate, List<int>? numbers = null)
    {
        for (int start = 0; start < lines.Count; start += LinesPerSegment)
        {
            int count = Math.Min(LinesPerSegment, lines.Count - start);
            yield return new TextSegment
            {
                Text = string.Join('\n', lines.GetRange(start, count)),
                Locate = locate,
                FirstLine = start + 1,
                LineNumbers = numbers?.GetRange(start, count).ToArray(),
            };
        }
    }

    /// <summary>
    /// The text of every paragraph in a part, in document order, with the pictures found along the way and the
    /// number of the paragraph each sits in (0 when outside any paragraph).
    /// </summary>
    static (List<string> Text, List<(int Paragraph, string RelId)> Pictures) Paragraphs(ZipArchive zip, string part, string ns)
    {
        var paragraphs = new List<string>();
        var pictures = new List<(int, string)>();
        if (zip.GetEntry(part) is not { } entry) return (paragraphs, pictures);

        using var reader = XmlReader.Create(entry.Open(), Settings);
        var open = new Stack<(StringBuilder Text, int Index)>();
        bool inText = false;
        while (reader.Read())
        {
            // Office saves text boxes and shapes twice, a modern copy and a fallback for old readers; read one.
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Fallback" && reader.NamespaceURI == MarkupCompatibility)
            {
                reader.Skip();
                if (reader.EOF) break;
            }

            switch (reader.NodeType)
            {
                // Checked first: in slides and drawings the picture shares the paragraphs' namespace.
                case XmlNodeType.Element when (reader.LocalName == "blip" && reader.NamespaceURI == A) || (reader.LocalName == "imagedata" && reader.NamespaceURI == V):
                    if (reader.GetAttribute(reader.LocalName == "blip" ? "embed" : "id", R) is { } id)
                        pictures.Add((open.Count > 0 ? open.Peek().Index + 1 : 0, id));
                    break;
                case XmlNodeType.Element when reader.NamespaceURI == ns:
                    switch (reader.LocalName)
                    {
                        case "p":
                            paragraphs.Add("");
                            if (!reader.IsEmptyElement) open.Push((new StringBuilder(), paragraphs.Count - 1));
                            break;
                        case "t":
                            inText = !reader.IsEmptyElement;
                            break;
                        case "tab" when ns == W && open.Count > 0:
                            open.Peek().Text.Append('\t');
                            break;
                        case "br" or "cr" when open.Count > 0:
                            open.Peek().Text.Append(' ');
                            break;
                    }
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (inText && open.Count > 0) open.Peek().Text.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement when reader.NamespaceURI == ns:
                    if (reader.LocalName == "t") inText = false;
                    else if (reader.LocalName == "p" && open.Count > 0)
                    {
                        var (text, index) = open.Pop();
                        paragraphs[index] = text.ToString();
                    }
                    break;
            }
        }
        return (paragraphs, pictures);
    }

    static void Rows(Stream xml, List<string> shared, List<string> lines, List<int> rows)
    {
        using var reader = XmlReader.Create(xml, Settings);
        var cells = new List<string>();
        var value = new StringBuilder();
        string? type = null;
        int row = 0;
        bool inValue = false;
        while (reader.Read())
        {
            if (reader.NamespaceURI != S && reader.NodeType is XmlNodeType.Element or XmlNodeType.EndElement) continue;
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    switch (reader.LocalName)
                    {
                        case "row":
                            row = int.TryParse(reader.GetAttribute("r"), out int r) ? r : row + 1;
                            cells.Clear();
                            break;
                        case "c":
                            type = reader.GetAttribute("t");
                            value.Clear();
                            break;
                        case "v" or "t":
                            inValue = !reader.IsEmptyElement;
                            break;
                    }
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (inValue) value.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement:
                    switch (reader.LocalName)
                    {
                        case "v" or "t":
                            inValue = false;
                            break;
                        case "c":
                            string cell = type switch
                            {
                                "s" => int.TryParse(value.ToString(), out int i) && i >= 0 && i < shared.Count ? shared[i] : "",
                                "b" => value.ToString() == "1" ? "TRUE" : "FALSE",
                                _ => value.ToString(),
                            };
                            if (cell.Length > 0) cells.Add(cell);
                            break;
                        case "row":
                            if (cells.Count > 0)
                            {
                                lines.Add(string.Join(" │ ", cells));
                                rows.Add(row);
                            }
                            break;
                    }
                    break;
            }
        }
    }

    static List<string> SharedStrings(ZipArchive zip, string? part)
    {
        var strings = new List<string>();
        if (zip.GetEntry(part ?? "xl/sharedStrings.xml") is not { } entry) return strings;
        using var reader = XmlReader.Create(entry.Open(), Settings);
        var current = new StringBuilder();
        bool inText = false;
        int phonetic = 0;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.NamespaceURI == S:
                    if (reader.LocalName == "si") current.Clear();
                    else if (reader.LocalName == "rPh" && !reader.IsEmptyElement) phonetic++;
                    else if (reader.LocalName == "t") inText = !reader.IsEmptyElement && phonetic == 0;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (inText) current.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement when reader.NamespaceURI == S:
                    if (reader.LocalName == "t") inText = false;
                    else if (reader.LocalName == "rPh") phonetic--;
                    else if (reader.LocalName == "si") strings.Add(current.ToString());
                    break;
            }
        }
        return strings;
    }

    /// <summary>Maps relationship ids of a part to the zip entry they point at and the relationship type.</summary>
    static Dictionary<string, (string Target, string Type)> Relationships(ZipArchive zip, string part)
    {
        var map = new Dictionary<string, (string, string)>();
        int slash = part.LastIndexOf('/');
        string dir = part[..(slash + 1)];
        if (zip.GetEntry(dir + "_rels/" + part[(slash + 1)..] + ".rels") is not { } entry) return map;

        using var reader = XmlReader.Create(entry.Open(), Settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
            if (reader.GetAttribute("TargetMode") == "External") continue;
            if (reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
                map[id] = (ResolvePart(dir, target), reader.GetAttribute("Type") ?? "");
        }
        return map;
    }

    internal static string ResolvePart(string dir, string target)
    {
        var parts = new List<string>();
        foreach (var piece in (target.StartsWith('/') ? target[1..] : dir + target).Split('/'))
        {
            if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (piece is not ("." or "")) parts.Add(piece);
        }
        return string.Join('/', parts);
    }
}
