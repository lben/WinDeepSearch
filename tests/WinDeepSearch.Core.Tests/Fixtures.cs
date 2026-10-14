using System.IO.Compression;
using System.Security;
using System.Text;
using SkiaSharp;

namespace WinDeepSearch.Tests;

/// <summary>Builds real files of each supported kind for the tests.</summary>
public static class Fixtures
{
    const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    const string Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    const string ImageRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";

    /// <summary>A white picture with black text, one line per argument.</summary>
    public static byte[] TextPicture(params string[] lines)
    {
        using var font = new SKFont(SKTypeface.Default, 40);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var bitmap = new SKBitmap(900, 60 + 70 * lines.Length);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            for (int i = 0; i < lines.Length; i++) canvas.DrawText(lines[i], 40, 70 + 70 * i, font, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static void Docx(string path, string[] paragraphs, byte[]? picture = null)
    {
        var body = new StringBuilder();
        foreach (var p in paragraphs) body.Append(Paragraph(p));
        if (picture != null)
            body.Append($"<w:p><w:r><w:drawing><a:graphic><a:graphicData uri=\"{Pic}\"><pic:pic><pic:blipFill><a:blip r:embed=\"rIdPic\"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></w:drawing></w:r></w:p>");

        var entries = new List<(string, object)>
        {
            ("[Content_Types].xml", ContentTypes("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
            ("_rels/.rels", Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "word/document.xml"))),
            ("word/document.xml", $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\" xmlns:a=\"{A}\" xmlns:pic=\"{Pic}\"><w:body>{body}</w:body></w:document>"),
            ("word/_rels/document.xml.rels", Rels(("rIdPic", ImageRel, "media/image1.png"))),
        };
        if (picture != null) entries.Add(("word/media/image1.png", picture));
        Zip(path, entries);
    }

    /// <summary>
    /// A Word paragraph. A '|' in the text splits it into separate runs, the way Word stores text
    /// typed at different times.
    /// </summary>
    static string Paragraph(string text) =>
        "<w:p>" + string.Concat(text.Split('|').Select(run => $"<w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(run)}</w:t></w:r>")) + "</w:p>";

    /// <summary>A workbook with one sheet; <paramref name="rows"/> maps row numbers to cell texts.</summary>
    public static void Xlsx(string path, string sheetName, Dictionary<int, string[]> rows, byte[]? picture = null)
    {
        var shared = new List<string>();
        var sheet = new StringBuilder($"<worksheet xmlns=\"{S}\" xmlns:r=\"{R}\"><sheetData>");
        foreach (var (number, cells) in rows.OrderBy(r => r.Key))
        {
            sheet.Append($"<row r=\"{number}\">");
            foreach (var cell in cells)
            {
                if (double.TryParse(cell, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    sheet.Append($"<c><v>{cell}</v></c>");
                }
                else
                {
                    shared.Add(cell);
                    sheet.Append($"<c t=\"s\"><v>{shared.Count - 1}</v></c>");
                }
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData>");
        if (picture != null) sheet.Append("<drawing r:id=\"rIdDrawing\"/>");
        sheet.Append("</worksheet>");

        var entries = new List<(string, object)>
        {
            ("[Content_Types].xml", ContentTypes("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            ("_rels/.rels", Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml"))),
            ("xl/workbook.xml", $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"{SecurityElement.Escape(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", Rels(
                ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml"),
                ("rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings", "sharedStrings.xml"))),
            ("xl/sharedStrings.xml", $"<sst xmlns=\"{S}\">" + string.Concat(shared.Select(s => $"<si><t xml:space=\"preserve\">{SecurityElement.Escape(s)}</t></si>")) + "</sst>"),
            ("xl/worksheets/sheet1.xml", sheet.ToString()),
        };
        if (picture != null)
        {
            entries.Add(("xl/worksheets/_rels/sheet1.xml.rels", Rels(("rIdDrawing", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing", "../drawings/drawing1.xml"))));
            entries.Add(("xl/drawings/drawing1.xml", $"<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"{A}\" xmlns:r=\"{R}\"><xdr:oneCellAnchor><xdr:pic><xdr:blipFill><a:blip r:embed=\"rIdPic\"/></xdr:blipFill></xdr:pic></xdr:oneCellAnchor></xdr:wsDr>"));
            entries.Add(("xl/drawings/_rels/drawing1.xml.rels", Rels(("rIdPic", ImageRel, "../media/image1.png"))));
            entries.Add(("xl/media/image1.png", picture));
        }
        Zip(path, entries);
    }

    /// <summary>
    /// A presentation. The slide files are numbered in the opposite order to the slides, as happens after
    /// slides are reordered, so the reader must follow the presentation's slide list.
    /// </summary>
    public static void Pptx(string path, string[][] slides, string? firstSlideNotes = null, byte[]? firstSlidePicture = null)
    {
        var entries = new List<(string, object)>
        {
            ("[Content_Types].xml", ContentTypes("/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml")),
            ("_rels/.rels", Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "ppt/presentation.xml"))),
        };
        var ids = new StringBuilder();
        var presentationRels = new List<(string, string, string)>();
        for (int i = 0; i < slides.Length; i++)
        {
            string file = $"slide{slides.Length - i}.xml";
            ids.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rIdS{i}\"/>");
            presentationRels.Add(($"rIdS{i}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide", "slides/" + file));

            var shapes = new StringBuilder("<p:sp><p:txBody>");
            foreach (var p in slides[i]) shapes.Append($"<a:p><a:r><a:t>{SecurityElement.Escape(p)}</a:t></a:r></a:p>");
            shapes.Append("</p:txBody></p:sp>");
            var slideRels = new List<(string, string, string)>();
            if (i == 0 && firstSlidePicture != null)
            {
                shapes.Append("<p:pic><p:blipFill><a:blip r:embed=\"rIdPic\"/></p:blipFill></p:pic>");
                slideRels.Add(("rIdPic", ImageRel, "../media/image1.png"));
                entries.Add(("ppt/media/image1.png", firstSlidePicture));
            }
            if (i == 0 && firstSlideNotes != null)
            {
                slideRels.Add(("rIdNotes", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide", "../notesSlides/notesSlide1.xml"));
                entries.Add(("ppt/notesSlides/notesSlide1.xml", $"<p:notes xmlns:p=\"{P}\" xmlns:a=\"{A}\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{SecurityElement.Escape(firstSlideNotes)}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:notes>"));
            }
            entries.Add(("ppt/slides/" + file, $"<p:sld xmlns:p=\"{P}\" xmlns:a=\"{A}\" xmlns:r=\"{R}\"><p:cSld><p:spTree>{shapes}</p:spTree></p:cSld></p:sld>"));
            entries.Add(("ppt/slides/_rels/" + file + ".rels", Rels([.. slideRels])));
        }
        entries.Add(("ppt/presentation.xml", $"<p:presentation xmlns:p=\"{P}\" xmlns:r=\"{R}\"><p:sldIdLst>{ids}</p:sldIdLst></p:presentation>"));
        entries.Add(("ppt/_rels/presentation.xml.rels", Rels([.. presentationRels])));
        Zip(path, entries);
    }

    public static void Odt(string path, string[] paragraphs)
    {
        const string office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        const string text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        var body = string.Concat(paragraphs.Select(p => $"<text:p>{SecurityElement.Escape(p).Replace("  ", "<text:s text:c=\"2\"/>")}</text:p>"));
        Zip(path,
        [
            ("mimetype", "application/vnd.oasis.opendocument.text"),
            ("content.xml", $"<office:document-content xmlns:office=\"{office}\" xmlns:text=\"{text}\"><office:body><office:text>{body}</office:text></office:body></office:document-content>"),
        ]);
    }

    /// <summary>
    /// A PDF whose first page has the given text lines and, when given, a second page holding the picture
    /// (decoded from PNG) on part of the page.
    /// </summary>
    public static void Pdf(string path, string[] lines, byte[]? picturePng = null)
    {
        var objects = new List<byte[]>();
        int Add(string body) { objects.Add(Encoding.Latin1.GetBytes(body)); return objects.Count; }
        int AddStream(string dictionary, byte[] data)
        {
            var bytes = new List<byte>(Encoding.Latin1.GetBytes($"<< {dictionary} /Length {data.Length} >>\nstream\n"));
            bytes.AddRange(data);
            bytes.AddRange(Encoding.Latin1.GetBytes("\nendstream"));
            objects.Add([.. bytes]);
            return objects.Count;
        }

        // Object numbers are fixed up front: 1 catalog, 2 pages, 3 font.
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add(""); // pages, filled in below
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var kids = new List<int>();
        var text = new StringBuilder("BT /F1 14 Tf 72 720 Td 18 TL\n");
        foreach (var line in lines) text.Append('(').Append(line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj T*\n");
        text.Append("ET");
        int content = AddStream("", Encoding.Latin1.GetBytes(text.ToString()));
        kids.Add(Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {content} 0 R >>"));

        if (picturePng != null)
        {
            using var bitmap = SKBitmap.Decode(picturePng);
            var rgb = new byte[bitmap.Width * bitmap.Height * 3];
            for (int y = 0, i = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    rgb[i++] = c.Red; rgb[i++] = c.Green; rgb[i++] = c.Blue;
                }
            int image = AddStream($"/Type /XObject /Subtype /Image /Width {bitmap.Width} /Height {bitmap.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8", rgb);
            int width = 450, height = 450 * bitmap.Height / bitmap.Width;
            int draw = AddStream("", Encoding.Latin1.GetBytes($"q {width} 0 0 {height} 72 400 cm /Im1 Do Q"));
            kids.Add(Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 {image} 0 R >> >> /Contents {draw} 0 R >>"));
        }
        objects[1] = Encoding.Latin1.GetBytes($"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");

        using var file = File.Create(path);
        void Write(string s) => file.Write(Encoding.Latin1.GetBytes(s));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Write($"{i + 1} 0 obj\n");
            file.Write(objects[i]);
            Write("\nendobj\n");
        }
        long xref = file.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    static string ContentTypes(string mainPart, string mainType) =>
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/>" +
        $"<Override PartName=\"{mainPart}\" ContentType=\"{mainType}\"/></Types>";

    static string Rels(params (string Id, string Type, string Target)[] rels) =>
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        string.Concat(rels.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"{r.Type}\" Target=\"{r.Target}\"/>")) +
        "</Relationships>";

    static void Zip(string path, IEnumerable<(string Name, object Content)> entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(content as byte[] ?? Encoding.UTF8.GetBytes((string)content));
        }
    }
}
