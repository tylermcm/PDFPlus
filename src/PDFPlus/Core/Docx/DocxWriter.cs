using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace PDFPlus.Core.Docx;

/// <summary>
/// Writes a <see cref="FlowDocument"/> back out as a Word document.
///
/// The package is built from scratch every time rather than patched into the file that was opened. That keeps
/// the writer simple and the output valid, at the cost of dropping anything the reader didn't understand, which
/// is why the app warns before overwriting a document that used such features.
/// </summary>
public static class DocxWriter
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    private const string Office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    // The other direction of the conversions in DocxFormat: WPF's 1/96 inch units back into Word's.
    private const double DipToHalfPoints = 72.0 * 2 / 96;
    private const double DipToTwips = 1440.0 / 96;
    private const double DipToEmus = 914400.0 / 96;

    /// <summary>Numbering ids matching the two abstract lists written into numbering.xml.</summary>
    private const int BulletNumId = 1, DecimalNumId = 2;

    private sealed class Package
    {
        public readonly List<(string Id, string Target, string Type, bool External)> Relationships = new();
        public readonly List<(string Name, byte[] Data)> Media = new();
        private int _next = 10;

        public string AddImage(byte[] data, string extension)
        {
            var id = $"rId{_next++}";
            var name = $"image{Media.Count + 1}{extension}";
            Media.Add((name, data));
            Relationships.Add((id, $"media/{name}", $"{Office}/image", false));
            return id;
        }

        public string AddHyperlink(string uri)
        {
            var id = $"rId{_next++}";
            Relationships.Add((id, uri, $"{Office}/hyperlink", true));
            return id;
        }
    }

    public static void Write(FlowDocument document, string path, PageSetup? page = null)
    {
        page ??= PageSetup.Default;
        var package = new Package();
        var body = new XElement(W + "body");
        foreach (var block in document.Blocks) WriteBlock(block, body, package, null);

        int Twips(double dip) => (int)Math.Round(dip * DipToTwips);
        body.Add(new XElement(W + "sectPr",
            new XElement(W + "pgSz",
                new XAttribute(W + "w", Twips(page.Width)), new XAttribute(W + "h", Twips(page.Height))),
            new XElement(W + "pgMar",
                new XAttribute(W + "top", Twips(page.Margin.Top)), new XAttribute(W + "right", Twips(page.Margin.Right)),
                new XAttribute(W + "bottom", Twips(page.Margin.Bottom)), new XAttribute(W + "left", Twips(page.Margin.Left)))));

        var documentXml = new XDocument(new XElement(W + "document",
            new XAttribute(XNamespace.Xmlns + "w", W),
            new XAttribute(XNamespace.Xmlns + "r", R),
            new XAttribute(XNamespace.Xmlns + "a", A),
            new XAttribute(XNamespace.Xmlns + "wp", Wp),
            new XAttribute(XNamespace.Xmlns + "pic", Pic),
            body));

        // Write to memory first so a failure part way through cannot leave a half-written file behind.
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "[Content_Types].xml", ContentTypesXml(package));
            Add(archive, "_rels/.rels", PackageRels());
            Add(archive, "word/document.xml", documentXml);
            Add(archive, "word/styles.xml", StylesXml(document));
            Add(archive, "word/numbering.xml", NumberingXml());
            Add(archive, "word/_rels/document.xml.rels", DocumentRels(package));
            foreach (var (name, data) in package.Media)
            {
                var entry = archive.CreateEntry($"word/media/{name}", CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(data, 0, data.Length);
            }
        }
        File.WriteAllBytes(path, buffer.ToArray());
    }

    // ---------------------------------------------------------------- blocks

    private static void WriteBlock(Block block, XElement parent, Package package, (int Id, int Level)? numbering)
    {
        switch (block)
        {
            case Paragraph paragraph:
                parent.Add(WriteParagraph(paragraph, package, numbering));
                break;
            case List list:
                // The reader expresses a nested list as a deeper left margin, so read the level back out of it.
                var id = list.MarkerStyle == TextMarkerStyle.Disc ? BulletNumId : DecimalNumId;
                var level = Math.Clamp((int)Math.Round(list.Margin.Left / DocxFormat.IndentPerLevel), 0, 8);
                foreach (var item in list.ListItems)
                foreach (var child in item.Blocks)
                    WriteBlock(child, parent, package, (id, level));
                break;
            case Table table:
                parent.Add(WriteTable(table, package));
                break;
            case Section section:
                foreach (var child in section.Blocks) WriteBlock(child, parent, package, numbering);
                break;
            case BlockUIContainer container when container.Child is Image image:
                var run = new XElement(W + "r");
                if (WriteImage(image, package) is { } drawing) run.Add(drawing);
                parent.Add(new XElement(W + "p", run));
                break;
        }
    }

    private static XElement WriteParagraph(Paragraph paragraph, Package package, (int Id, int Level)? numbering)
    {
        var properties = new XElement(W + "pPr");

        if (paragraph.BreakPageBefore) properties.Add(new XElement(W + "pageBreakBefore"));

        if (numbering is { } list)
            properties.Add(new XElement(W + "numPr",
                new XElement(W + "ilvl", new XAttribute(W + "val", list.Level)),
                new XElement(W + "numId", new XAttribute(W + "val", list.Id))));

        var alignment = paragraph.TextAlignment switch
        {
            TextAlignment.Center => "center",
            TextAlignment.Right => "right",
            TextAlignment.Justify => "both",
            _ => null,
        };
        if (alignment != null) properties.Add(new XElement(W + "jc", new XAttribute(W + "val", alignment)));

        // Word keeps the gaps around a paragraph in its spacing, not in a margin.
        if (paragraph.Margin.Top > 0.5 || paragraph.Margin.Bottom > 0.5)
            properties.Add(new XElement(W + "spacing",
                new XAttribute(W + "before", (int)Math.Round(Math.Max(0, paragraph.Margin.Top) * DipToTwips)),
                new XAttribute(W + "after", (int)Math.Round(Math.Max(0, paragraph.Margin.Bottom) * DipToTwips))));

        var indent = new XElement(W + "ind");
        if (paragraph.Margin.Left > 0.5) indent.Add(new XAttribute(W + "left", (int)Math.Round(paragraph.Margin.Left * DipToTwips)));
        if (paragraph.Margin.Right > 0.5) indent.Add(new XAttribute(W + "right", (int)Math.Round(paragraph.Margin.Right * DipToTwips)));
        if (paragraph.TextIndent > 0.5)
            indent.Add(new XAttribute(W + "firstLine", (int)Math.Round(paragraph.TextIndent * DipToTwips)));
        else if (paragraph.TextIndent < -0.5)
            indent.Add(new XAttribute(W + "hanging", (int)Math.Round(-paragraph.TextIndent * DipToTwips)));
        if (indent.HasAttributes) properties.Add(indent);

        if (paragraph.LineHeight > 0 && !double.IsNaN(paragraph.LineHeight))
            properties.Add(new XElement(W + "spacing",
                new XAttribute(W + "line", (int)Math.Round(paragraph.LineHeight * DipToTwips)),
                new XAttribute(W + "lineRule", "exact")));

        // The formatting of the paragraph mark. Without it a heading's size would live only on its runs, and
        // a paragraph whose text was deleted would forget how it was styled.
        var mark = RunProperties(paragraph, paragraph.TextDecorations);
        if (mark.HasElements) properties.Add(mark);

        var element = new XElement(W + "p");
        if (properties.HasElements) element.Add(properties);
        foreach (var inline in paragraph.Inlines) WriteInline(inline, element, package);
        return element;
    }

    private static XElement WriteTable(Table table, Package package)
    {
        var border = new Func<string, XElement>(name => new XElement(W + name,
            new XAttribute(W + "val", "single"), new XAttribute(W + "sz", 4), new XAttribute(W + "color", "auto")));

        var element = new XElement(W + "tbl",
            new XElement(W + "tblPr",
                new XElement(W + "tblW", new XAttribute(W + "w", 0), new XAttribute(W + "type", "auto")),
                new XElement(W + "tblBorders",
                    border("top"), border("left"), border("bottom"), border("right"),
                    border("insideH"), border("insideV"))));

        if (table.Columns.Count > 0)
        {
            var grid = new XElement(W + "tblGrid");
            foreach (var column in table.Columns)
                // The reader stores proportional widths as star units, so the value is meaningful either way.
                grid.Add(new XElement(W + "gridCol", new XAttribute(W + "w",
                    column.Width.Value > 0 && !double.IsNaN(column.Width.Value)
                        ? (int)Math.Round(column.Width.Value * DipToTwips)
                        : 2880)));
            element.Add(grid);
        }

        foreach (var group in table.RowGroups)
        foreach (var row in group.Rows)
        {
            var rowElement = new XElement(W + "tr");
            foreach (var cell in row.Cells)
            {
                var cellProperties = new XElement(W + "tcPr",
                    new XElement(W + "tcW", new XAttribute(W + "w", 0), new XAttribute(W + "type", "auto")));
                if (cell.ColumnSpan > 1)
                    cellProperties.Add(new XElement(W + "gridSpan", new XAttribute(W + "val", cell.ColumnSpan)));
                if (cell.RowSpan > 1)
                    cellProperties.Add(new XElement(W + "vMerge", new XAttribute(W + "val", "restart")));
                if (cell.Background is SolidColorBrush { Color: var fill })
                    cellProperties.Add(new XElement(W + "shd",
                        new XAttribute(W + "val", "clear"),
                        new XAttribute(W + "fill", $"{fill.R:X2}{fill.G:X2}{fill.B:X2}")));

                var cellElement = new XElement(W + "tc", cellProperties);

                foreach (var block in cell.Blocks) WriteBlock(block, cellElement, package, null);
                // Word rejects a table cell with no paragraph in it.
                if (!cellElement.Elements(W + "p").Any()) cellElement.Add(new XElement(W + "p"));
                rowElement.Add(cellElement);
            }
            element.Add(rowElement);
        }
        return element;
    }

    // ---------------------------------------------------------------- inlines

    private static void WriteInline(Inline inline, XElement parent, Package package)
    {
        switch (inline)
        {
            case Run run when run.Text.Length > 0:
                parent.Add(TextRun(run.Text, run, package));
                break;
            case LineBreak:
                parent.Add(new XElement(W + "r", new XElement(W + "br")));
                break;
            case Hyperlink link:
                var element = new XElement(W + "hyperlink");
                if (link.NavigateUri is { } uri)
                    element.Add(new XAttribute(R + "id", package.AddHyperlink(uri.ToString())));
                foreach (var child in link.Inlines) WriteInline(child, element, package);
                parent.Add(element);
                break;
            case Span span:
                foreach (var child in span.Inlines) WriteInline(child, parent, package);
                break;
            case InlineUIContainer { Child: Image image }:
                var imageRun = new XElement(W + "r");
                if (WriteImage(image, package) is { } drawing) imageRun.Add(drawing);
                parent.Add(imageRun);
                break;
        }
    }

    /// <summary>
    /// Character formatting, read from the element's effective values. WPF inherits these down the tree, so a
    /// run inside a heading reports the heading's size even though nothing was set on the run itself; writing
    /// the effective value is what carries paragraph styling into a file that has no styles of its own.
    /// </summary>
    private static XElement RunProperties(TextElement source, TextDecorationCollection? decorations)
    {
        var properties = new XElement(W + "rPr");

        if (source.FontFamily?.Source is { Length: > 0 } name)
            properties.Add(new XElement(W + "rFonts", new XAttribute(W + "ascii", name), new XAttribute(W + "hAnsi", name)));
        if (source.FontWeight.ToOpenTypeWeight() >= 600) properties.Add(new XElement(W + "b"));
        if (source.FontStyle == FontStyles.Italic) properties.Add(new XElement(W + "i"));
        if (decorations?.Contains(TextDecorations.Underline[0]) == true)
            properties.Add(new XElement(W + "u", new XAttribute(W + "val", "single")));
        if (decorations?.Contains(TextDecorations.Strikethrough[0]) == true)
            properties.Add(new XElement(W + "strike"));
        if (source.FontSize > 0)
            properties.Add(new XElement(W + "sz", new XAttribute(W + "val", (int)Math.Round(source.FontSize * DipToHalfPoints))));
        if (source.Foreground is SolidColorBrush { Color: var colour })
            properties.Add(new XElement(W + "color", new XAttribute(W + "val", $"{colour.R:X2}{colour.G:X2}{colour.B:X2}")));

        return properties;
    }

    private static XElement TextRun(string text, Inline source, Package package)
    {
        var properties = RunProperties(source, source.TextDecorations);

        var run = new XElement(W + "r");
        if (properties.HasElements) run.Add(properties);

        // A tab is its own element in Word, so split the text around any.
        var parts = text.Split('\t');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) run.Add(new XElement(W + "tab"));
            if (parts[i].Length == 0) continue;
            run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), parts[i]));
        }
        return run;
    }

    private static XElement? WriteImage(Image image, Package package)
    {
        if (image.Source is not BitmapSource bitmap) return null;

        byte[] data;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            data = stream.ToArray();
        }
        catch
        {
            return null;
        }

        var id = package.AddImage(data, ".png");
        var width = image.Width > 0 ? image.Width : bitmap.Width;
        var height = image.Height > 0 ? image.Height : bitmap.Height;
        var cx = (long)Math.Round(Math.Max(1, width) * DipToEmus);
        var cy = (long)Math.Round(Math.Max(1, height) * DipToEmus);

        return new XElement(W + "drawing",
            new XElement(Wp + "inline",
                new XAttribute("distT", 0), new XAttribute("distB", 0),
                new XAttribute("distL", 0), new XAttribute("distR", 0),
                new XElement(Wp + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                new XElement(Wp + "docPr", new XAttribute("id", package.Media.Count), new XAttribute("name", $"Picture {package.Media.Count}")),
                new XElement(A + "graphic",
                    new XElement(A + "graphicData",
                        new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
                        new XElement(Pic + "pic",
                            new XElement(Pic + "nvPicPr",
                                new XElement(Pic + "cNvPr", new XAttribute("id", 0), new XAttribute("name", $"Picture {package.Media.Count}")),
                                new XElement(Pic + "cNvPicPr")),
                            new XElement(Pic + "blipFill",
                                new XElement(A + "blip", new XAttribute(R + "embed", id)),
                                new XElement(A + "stretch", new XElement(A + "fillRect"))),
                            new XElement(Pic + "spPr",
                                new XElement(A + "xfrm",
                                    new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                                    new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                                new XElement(A + "prstGeom",
                                    new XAttribute("prst", "rect"), new XElement(A + "avLst"))))))));
    }

    // ---------------------------------------------------------------- package parts

    private static XDocument StylesXml(FlowDocument document)
    {
        var font = document.FontFamily?.Source is { Length: > 0 } name ? name : "Calibri";
        var size = (int)Math.Round((document.FontSize > 0 ? document.FontSize : 11) * DipToHalfPoints);

        return new XDocument(new XElement(W + "styles",
            new XAttribute(XNamespace.Xmlns + "w", W),
            new XElement(W + "docDefaults",
                new XElement(W + "rPrDefault",
                    new XElement(W + "rPr",
                        new XElement(W + "rFonts", new XAttribute(W + "ascii", font), new XAttribute(W + "hAnsi", font)),
                        new XElement(W + "sz", new XAttribute(W + "val", size))))),
            new XElement(W + "style",
                new XAttribute(W + "type", "paragraph"),
                new XAttribute(W + "styleId", "Normal"),
                new XAttribute(W + "default", "1"),
                new XElement(W + "name", new XAttribute(W + "val", "Normal")))));
    }

    private static XDocument NumberingXml()
    {
        XElement Abstract(int id, string format, string text)
        {
            var element = new XElement(W + "abstractNum", new XAttribute(W + "abstractNumId", id));
            for (var level = 0; level < 9; level++)
                element.Add(new XElement(W + "lvl",
                    new XAttribute(W + "ilvl", level),
                    new XElement(W + "start", new XAttribute(W + "val", 1)),
                    new XElement(W + "numFmt", new XAttribute(W + "val", format)),
                    new XElement(W + "lvlText", new XAttribute(W + "val", text)),
                    new XElement(W + "lvlJc", new XAttribute(W + "val", "left")),
                    new XElement(W + "pPr", new XElement(W + "ind",
                        new XAttribute(W + "left", 720 * (level + 1)),
                        new XAttribute(W + "hanging", 360)))));
            return element;
        }

        return new XDocument(new XElement(W + "numbering",
            new XAttribute(XNamespace.Xmlns + "w", W),
            Abstract(0, "bullet", "•"),
            Abstract(1, "decimal", "%1."),
            new XElement(W + "num", new XAttribute(W + "numId", BulletNumId),
                new XElement(W + "abstractNumId", new XAttribute(W + "val", 0))),
            new XElement(W + "num", new XAttribute(W + "numId", DecimalNumId),
                new XElement(W + "abstractNumId", new XAttribute(W + "val", 1)))));
    }

    private static XDocument PackageRels() => new(new XElement(Rel + "Relationships",
        new XElement(Rel + "Relationship",
            new XAttribute("Id", "rId1"),
            new XAttribute("Type", $"{Office}/officeDocument"),
            new XAttribute("Target", "word/document.xml"))));

    private static XDocument DocumentRels(Package package)
    {
        var root = new XElement(Rel + "Relationships",
            new XElement(Rel + "Relationship",
                new XAttribute("Id", "rId1"), new XAttribute("Type", $"{Office}/styles"), new XAttribute("Target", "styles.xml")),
            new XElement(Rel + "Relationship",
                new XAttribute("Id", "rId2"), new XAttribute("Type", $"{Office}/numbering"), new XAttribute("Target", "numbering.xml")));

        foreach (var (id, target, type, external) in package.Relationships)
        {
            var element = new XElement(Rel + "Relationship",
                new XAttribute("Id", id), new XAttribute("Type", type), new XAttribute("Target", target));
            if (external) element.Add(new XAttribute("TargetMode", "External"));
            root.Add(element);
        }
        return new XDocument(root);
    }

    private static XDocument ContentTypesXml(Package package)
    {
        var root = new XElement(ContentTypes + "Types",
            new XElement(ContentTypes + "Default",
                new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ContentTypes + "Default",
                new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            Override("/word/document.xml", "wordprocessingml.document.main"),
            Override("/word/styles.xml", "wordprocessingml.styles"),
            Override("/word/numbering.xml", "wordprocessingml.numbering"));

        if (package.Media.Count > 0)
            root.Add(new XElement(ContentTypes + "Default",
                new XAttribute("Extension", "png"), new XAttribute("ContentType", "image/png")));
        return new XDocument(root);

        XElement Override(string part, string kind) => new(ContentTypes + "Override",
            new XAttribute("PartName", part),
            new XAttribute("ContentType", $"application/vnd.openxmlformats-officedocument.{kind}+xml"));
    }

    private static void Add(ZipArchive archive, string name, XDocument document)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }
}
