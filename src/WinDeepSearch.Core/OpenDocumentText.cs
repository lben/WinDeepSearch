using System.IO.Compression;
using System.Text;
using System.Xml;

namespace WinDeepSearch.Core;

/// <summary>Text and pictures from LibreOffice/OpenDocument files (.odt, .ods, .odp and templates).</summary>
internal static class OpenDocumentText
{
    const string T = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    const string D = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    const string X = "http://www.w3.org/1999/xlink";

    public static IEnumerable<TextSegment> Read(string path, ImageText? images, ActiveFile active, CancellationToken ct)
    {
        using var zip = new ZipArchive(Files.OpenRead(path), ZipArchiveMode.Read);
        if (zip.GetEntry("content.xml") is not { } entry) yield break;

        var paragraphs = new List<string>();
        var pictures = new List<(int Paragraph, string Entry)>();
        using (var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true }))
        {
            var open = new Stack<(StringBuilder Text, int Index)>();
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when reader.NamespaceURI == T:
                        if (reader.LocalName is "p" or "h")
                        {
                            paragraphs.Add("");
                            if (!reader.IsEmptyElement) open.Push((new StringBuilder(), paragraphs.Count - 1));
                        }
                        else if (open.Count > 0)
                        {
                            if (reader.LocalName == "s") open.Peek().Text.Append(' ', int.TryParse(reader.GetAttribute("c", T), out int c) ? c : 1);
                            else if (reader.LocalName == "tab") open.Peek().Text.Append('\t');
                            else if (reader.LocalName == "line-break") open.Peek().Text.Append(' ');
                        }
                        break;
                    case XmlNodeType.Element when reader.NamespaceURI == D && reader.LocalName == "image":
                        if (reader.GetAttribute("href", X) is { } href && !href.Contains(':'))
                            pictures.Add((open.Count > 0 ? open.Peek().Index + 1 : 0, OfficeText.ResolvePart("", href)));
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        if (open.Count > 0) open.Peek().Text.Append(reader.Value);
                        break;
                    case XmlNodeType.EndElement when reader.NamespaceURI == T && reader.LocalName is "p" or "h":
                        if (open.Count > 0)
                        {
                            var (text, index) = open.Pop();
                            paragraphs[index] = text.ToString();
                        }
                        break;
                }
            }
        }

        foreach (var segment in OfficeText.Segments(paragraphs, n => $"Paragraph {n}")) yield return segment;
        active.Progress = 0.5;

        if (images == null) yield break;
        var seen = new HashSet<string>();
        foreach (var (paragraph, picture) in pictures)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(picture)) continue;
            string label = paragraph > 0 ? $"Paragraph {paragraph} · text in picture" : "Text in picture";
            var segment = OfficeText.ZipPicture(zip, picture, path, images, _ => label, active, ct);
            if (segment != null) yield return segment;
        }
    }
}
