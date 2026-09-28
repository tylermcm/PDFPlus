using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using static PDFPlus.Core.Docx.DocxFormat;

namespace PDFPlus.Core.Docx;

/// <summary>The page the document was written for: its size and margins, in WPF units.</summary>
public sealed record PageSetup(double Width, double Height, Thickness Margin)
{
    /// <summary>US Letter with one inch margins, which is what Word starts a new document with.</summary>
    public static readonly PageSetup Default = new(816, 1056, new Thickness(96));
}

/// <summary>What a .docx turned into, plus anything in it PDFPlus knowingly left behind.</summary>
public sealed record DocxImport(FlowDocument Document, IReadOnlyList<string> Unsupported, PageSetup Page);

/// <summary>
/// Reads a Word document into a <see cref="FlowDocument"/>.
///
/// A .docx is a zip of XML. This understands the parts that carry what a document says: paragraphs and their
/// runs, the styles behind them, bullet and numbered lists, tables, inline pictures, hyperlinks and footnotes.
/// It deliberately does not try to reproduce Word's page layout.
///
/// Anything found that would not survive is collected in <see cref="DocxImport.Unsupported"/> so the app can say
/// so out loud rather than discard it quietly.
/// </summary>
public static class DocxReader
{
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";


    public static DocxImport Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);

        var documentPart = Part(archive, "word/document.xml")
                           ?? throw new InvalidDataException("This file isn't a Word document: word/document.xml is missing.");

        var context = new ReadContext(
            archive,
            ReadRelationships(archive),
            ReadStyles(archive),
            ReadNumbering(archive),
            ReadFootnotes(archive),
            unsupported);

        NoteUnsupportedParts(archive, unsupported);

        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = new FontFamily(context.Styles.DefaultFont),
            FontSize = context.Styles.DefaultSize,
        };

        var body = documentPart.Root?.Element(W + "body");
        if (body == null) return new DocxImport(document, unsupported.ToList(), PageSetup.Default);

        foreach (var block in ReadBlocks(body.Elements(), context)) document.Blocks.Add(block);
        foreach (var block in FootnoteSection(context)) document.Blocks.Add(block);
        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());

        return new DocxImport(document, unsupported.ToList(), ReadPageSetup(body));
    }

    private sealed class ReadContext(
        ZipArchive archive,
        Dictionary<string, Relationship> relationships,
        StyleSheet styles,
        Dictionary<string, NumberingLevel> numbering,
        Dictionary<string, string> footnotes,
        SortedSet<string> unsupported)
    {
        public ZipArchive Archive { get; } = archive;
        public Dictionary<string, Relationship> Relationships { get; } = relationships;
        public StyleSheet Styles { get; } = styles;
        public Dictionary<string, NumberingLevel> Numbering { get; } = numbering;
        public Dictionary<string, string> Footnotes { get; } = footnotes;
        public SortedSet<string> Unsupported { get; } = unsupported;
        /// <summary>Set by a page break inside a run; the next paragraph starts the new page.</summary>
        public bool PendingPageBreak { get; set; }
        /// <summary>Footnotes actually referenced, in the order they appear, so they can be listed at the end.</summary>
        public List<string> UsedFootnotes { get; } = new();
    }

    private sealed record Relationship(string Target, bool External);

    private sealed record NumberingLevel(TextMarkerStyle Marker, int Level);

    // ---------------------------------------------------------------- blocks

    /// <summary>
    /// Word has no list element: consecutive paragraphs simply carry the same numbering id. They are gathered
    /// here so the editor gets real WPF lists, which is what makes Enter continue a list.
    /// </summary>
    private static IEnumerable<Block> ReadBlocks(IEnumerable<XElement> elements, ReadContext context)
    {
        List? list = null;
        string? listKey = null;

        foreach (var element in elements)
        {
            if (element.Name == W + "p")
            {
                var key = NumberingKey(element);
                if (key != null && context.Numbering.TryGetValue(key, out var level))
                {
                    if (list == null || listKey != key)
                    {
                        if (list != null) yield return list;
                        list = new List
                        {
                            MarkerStyle = level.Marker,
                            Margin = new Thickness(level.Level * IndentPerLevel, 4, 0, 4),
                            Padding = new Thickness(IndentPerLevel * 0.7, 0, 0, 0),
                        };
                        listKey = key;
                    }
                    list.ListItems.Add(new ListItem(ReadParagraph(element, context, inList: true)));
                    continue;
                }

                if (list != null)
                {
                    yield return list;
                    list = null;
                    listKey = null;
                }
                yield return ReadParagraph(element, context, inList: false);
            }
            else if (element.Name == W + "tbl")
            {
                if (list != null)
                {
                    yield return list;
                    list = null;
                    listKey = null;
                }
                yield return ReadTable(element, context);
            }
            else if (element.Name == W + "sdt")
            {
                // A content control. Its text lives inside, so keep that and note the control itself is lost.
                context.Unsupported.Add("content controls");
                if (element.Element(W + "sdtContent") is { } content)
                    foreach (var block in ReadBlocks(content.Elements(), context))
                        yield return block;
            }
        }

        if (list != null) yield return list;
    }

    private static string? NumberingKey(XElement paragraph)
    {
        var numPr = paragraph.Element(W + "pPr")?.Element(W + "numPr");
        var id = numPr?.Element(W + "numId")?.Attribute(W + "val")?.Value;
        if (id == null || id == "0") return null;
        var level = numPr?.Element(W + "ilvl")?.Attribute(W + "val")?.Value ?? "0";
        return $"{id}:{level}";
    }

    private static Paragraph ReadParagraph(XElement element, ReadContext context, bool inList)
    {
        var properties = element.Element(W + "pPr");
        var style = context.Styles.Resolve(properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value);

        // Direct formatting beats the style, which beats the document defaults.
        var paragraphFormat = ParagraphFormatOf(properties).Over(style?.Paragraph ?? default);
        var runFormat = RunFormatOf(properties?.Element(W + "rPr"))
            .Over(style?.Run ?? default)
            .Over(context.Styles.DocumentDefaults);

        var paragraph = new Paragraph();
        Apply(paragraph, runFormat);

        // A page break is written either as a property of this paragraph or as a break in the run before it.
        if (context.PendingPageBreak || properties?.Element(W + "pageBreakBefore") != null)
        {
            paragraph.BreakPageBefore = true;
            context.PendingPageBreak = false;
        }

        var top = paragraphFormat.SpaceBefore ?? (paragraphFormat.OutlineLevel is >= 0 ? 12 : 0);
        var bottom = paragraphFormat.SpaceAfter ?? 8;
        // A list already indents itself, so an item's own left indent would double it up.
        var left = inList ? 0 : paragraphFormat.IndentLeft ?? 0;
        paragraph.Margin = new Thickness(left, top, paragraphFormat.IndentRight ?? 0, bottom);

        if (paragraphFormat.Alignment is { } alignment) paragraph.TextAlignment = alignment;
        if (paragraphFormat.LineHeight is { } lineHeight and > 0) paragraph.LineHeight = lineHeight;
        if (paragraphFormat.IndentFirstLine is { } firstLine) paragraph.TextIndent = firstLine;

        foreach (var inline in ReadInlines(element.Elements(), context, runFormat)) paragraph.Inlines.Add(inline);
        return paragraph;
    }

    private static void Apply(TextElement target, RunFormat format)
    {
        if (format.Font is { } font) target.FontFamily = new FontFamily(font);
        if (format.Size is { } size) target.FontSize = size;
        if (format.Bold is { } bold) target.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        if (format.Italic is { } italic) target.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
        if (format.Colour is { } colour) target.Foreground = Brush(colour);
        if (format.Highlight is { } highlight) target.Background = Brush(highlight);
    }

    // ---------------------------------------------------------------- tables

    private static Table ReadTable(XElement element, ReadContext context)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 10) };
        var borders = ReadBorders(element.Element(W + "tblPr")?.Element(W + "tblBorders"));

        foreach (var width in element.Element(W + "tblGrid")?.Elements(W + "gridCol") ?? [])
            // An absolute width, not a star: a FlowDocument can be laid out with no width to divide up, and
            // star sizing then collapses the table into a single column.
            table.Columns.Add(new TableColumn
            {
                Width = Twips(width.Attribute(W + "w")?.Value) is { } dip and > 0
                    ? new GridLength(dip)
                    : GridLength.Auto,
            });

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        // A vertically merged cell is a "restart" followed by continuations; the restart grows instead.
        var merging = new Dictionary<int, TableCell>();
        var widest = 0;

        foreach (var rowElement in element.Elements(W + "tr"))
        {
            var row = new TableRow();
            var column = 0;

            foreach (var cellElement in rowElement.Elements(W + "tc"))
            {
                var cellProperties = cellElement.Element(W + "tcPr");
                var span = int.TryParse(cellProperties?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value, out var columns) ? Math.Max(1, columns) : 1;
                var merge = cellProperties?.Element(W + "vMerge");

                if (merge != null && merge.Attribute(W + "val")?.Value != "restart")
                {
                    if (merging.TryGetValue(column, out var above)) above.RowSpan++;
                    column += span;
                    continue;
                }

                var cell = new TableCell
                {
                    BorderBrush = borders.Brush,
                    BorderThickness = borders.Thickness,
                    Padding = new Thickness(6, 3, 6, 3),
                    ColumnSpan = span,
                };
                if (Colour(cellProperties?.Element(W + "shd")?.Attribute(W + "fill")?.Value) is { } fill)
                    cell.Background = Brush(fill);

                foreach (var block in ReadBlocks(cellElement.Elements(), context)) cell.Blocks.Add(block);
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph());
                // Cell text does not need the space a body paragraph gets under it.
                foreach (var paragraph in cell.Blocks.OfType<Paragraph>())
                    paragraph.Margin = new Thickness(paragraph.Margin.Left, 0, 0, 0);

                row.Cells.Add(cell);
                if (merge != null) merging[column] = cell;
                else merging.Remove(column);
                column += span;
            }

            widest = Math.Max(widest, column);
            group.Rows.Add(row);
        }

        while (table.Columns.Count < widest) table.Columns.Add(new TableColumn());
        return table;
    }

    private static (Brush Brush, Thickness Thickness) ReadBorders(XElement? borders)
    {
        var defaultBrush = Brush(Color.FromRgb(0xD9, 0xDB, 0xE1));
        if (borders == null) return (defaultBrush, new Thickness(0.75));

        double Side(string name)
        {
            var side = borders.Element(W + name);
            if (side == null) return 0;
            if (side.Attribute(W + "val")?.Value is "nil" or "none") return 0;
            var size = side.Attribute(W + "sz")?.Value;
            return double.TryParse(size, out var eighths) && eighths > 0
                ? Math.Clamp(eighths * EighthPointsToDip, 0.5, 8)
                : 0.75;
        }

        var inside = Math.Max(Side("insideH"), Side("insideV"));
        var thickness = new Thickness(
            Math.Max(Side("left"), inside), Math.Max(Side("top"), inside),
            Math.Max(Side("right"), inside), Math.Max(Side("bottom"), inside));

        var colour = Colour(borders.Elements().Select(e => e.Attribute(W + "color")?.Value)
            .FirstOrDefault(value => value is { Length: 6 }));
        return (colour is { } found ? Brush(found) : defaultBrush, thickness);
    }

    // ---------------------------------------------------------------- inlines

    private static IEnumerable<Inline> ReadInlines(IEnumerable<XElement> elements, ReadContext context, RunFormat inherited)
    {
        foreach (var element in elements)
        {
            if (element.Name == W + "r")
            {
                foreach (var inline in ReadRun(element, context, inherited)) yield return inline;
            }
            else if (element.Name == W + "hyperlink")
            {
                var link = new Hyperlink();
                foreach (var inline in ReadInlines(element.Elements(), context, inherited)) link.Inlines.Add(inline);

                var id = element.Attribute(R + "id")?.Value;
                if (id != null && context.Relationships.GetValueOrDefault(id) is { External: true } target &&
                    Uri.TryCreate(target.Target, UriKind.Absolute, out var uri))
                    link.NavigateUri = uri;
                yield return link;
            }
            else if (element.Name == W + "ins")
            {
                // A tracked insertion: keep the text, since that is what the document currently says.
                context.Unsupported.Add("tracked changes");
                foreach (var inline in ReadInlines(element.Elements(), context, inherited)) yield return inline;
            }
            else if (element.Name == W + "del")
            {
                context.Unsupported.Add("tracked changes");
            }
            else if (element.Name == W + "fldSimple")
            {
                context.Unsupported.Add("fields such as page numbers");
                foreach (var inline in ReadInlines(element.Elements(), context, inherited)) yield return inline;
            }
            else if (element.Name == W + "smartTag" || element.Name == W + "bookmarkStart")
            {
                if (element.Name == W + "smartTag")
                    foreach (var inline in ReadInlines(element.Elements(), context, inherited))
                        yield return inline;
            }
        }
    }

    private static IEnumerable<Inline> ReadRun(XElement element, ReadContext context, RunFormat inherited)
    {
        var properties = element.Element(W + "rPr");
        var style = context.Styles.Resolve(properties?.Element(W + "rStyle")?.Attribute(W + "val")?.Value);
        var format = RunFormatOf(properties).Over(style?.Run ?? default).Over(inherited);

        foreach (var child in element.Elements())
        {
            if (child.Name == W + "t")
            {
                yield return Styled(Transform(child.Value, format), format);
            }
            else if (child.Name == W + "br")
            {
                // A page break belongs to the paragraph that follows it, which is how WPF expresses one.
                if (child.Attribute(W + "type")?.Value == "page") context.PendingPageBreak = true;
                else yield return new LineBreak();
            }
            else if (child.Name == W + "tab")
            {
                yield return Styled("\t", format);
            }
            else if (child.Name == W + "sym")
            {
                // A symbol font character, written as a code point in the private use area.
                var code = child.Attribute(W + "char")?.Value;
                if (int.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out var value))
                    yield return Styled(char.ConvertFromUtf32(value is >= 0xF000 and <= 0xF0FF ? value - 0xF000 + 0x2022 : value), format);
            }
            else if (child.Name == W + "drawing" || child.Name == W + "pict" || child.Name == W + "object")
            {
                if (ReadPicture(child, context) is { } picture) yield return picture;
            }
            else if (child.Name == W + "footnoteReference" || child.Name == W + "endnoteReference")
            {
                var id = child.Attribute(W + "id")?.Value;
                if (id != null && context.Footnotes.TryGetValue(id, out var note))
                {
                    context.UsedFootnotes.Add(note);
                    yield return new Run($"[{context.UsedFootnotes.Count}]")
                    {
                        BaselineAlignment = BaselineAlignment.Superscript,
                        FontSize = Math.Max(7, (format.Size ?? 11) * 0.75),
                    };
                }
                else
                {
                    context.Unsupported.Add("footnotes and endnotes");
                }
            }
            else if (child.Name == W + "instrText")
            {
                context.Unsupported.Add("fields such as page numbers");
            }
        }
    }

    /// <summary>Word applies capitalisation as formatting rather than changing the text.</summary>
    private static string Transform(string text, RunFormat format) =>
        format.Caps == true || format.SmallCaps == true ? text.ToUpperInvariant() : text;

    private static Inline Styled(string text, RunFormat format)
    {
        var run = new Run(text);
        Apply(run, format);
        if (format.Underline == true) run.TextDecorations = TextDecorations.Underline;
        if (format.Strike == true) run.TextDecorations = TextDecorations.Strikethrough;
        if (format.SmallCaps == true && format.Size is { } size) run.FontSize = size * 0.85;
        return run;
    }

    private static Inline? ReadPicture(XElement element, ReadContext context)
    {
        var embed = element.Descendants(A + "blip").Attributes(R + "embed").FirstOrDefault()?.Value
                    ?? element.Descendants().Attributes(R + "id").FirstOrDefault()?.Value;
        if (embed == null || context.Relationships.GetValueOrDefault(embed) is not { External: false } relationship) return null;

        var target = relationship.Target.TrimStart('/');
        var entry = context.Archive.GetEntry("word/" + target) ?? context.Archive.GetEntry(target);
        if (entry == null) return null;

        BitmapImage bitmap;
        try
        {
            using var stream = entry.Open();
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;

            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = buffer;
            bitmap.EndInit();
            bitmap.Freeze();
        }
        catch
        {
            // A picture in a format WPF can't decode, such as WMF, is left out.
            context.Unsupported.Add("pictures in formats Windows can't show");
            return null;
        }

        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        var extent = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "extent");
        if (double.TryParse(extent?.Attribute("cx")?.Value, out var cx) && cx > 0) image.Width = cx * EmusToDip;
        if (double.TryParse(extent?.Attribute("cy")?.Value, out var cy) && cy > 0) image.Height = cy * EmusToDip;
        if (element.Descendants().Any(e => e.Name.LocalName == "anchor")) context.Unsupported.Add("pictures anchored to the page");

        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };
    }

    // ---------------------------------------------------------------- footnotes

    /// <summary>
    /// Footnotes are shown as a numbered list at the end rather than at the foot of a page, because there are
    /// no pages here. Keeping the words beats discarding them.
    /// </summary>
    private static IEnumerable<Block> FootnoteSection(ReadContext context)
    {
        if (context.UsedFootnotes.Count == 0) yield break;

        yield return new Paragraph(new Run("Footnotes"))
        {
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 24, 0, 6),
        };

        for (var i = 0; i < context.UsedFootnotes.Count; i++)
            yield return new Paragraph(new Run($"[{i + 1}]  {context.UsedFootnotes[i]}"))
            {
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 4),
            };
    }

    private static Dictionary<string, string> ReadFootnotes(ZipArchive archive)
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "word/footnotes.xml", "word/endnotes.xml" })
        {
            var part = Part(archive, name);
            if (part?.Root == null) continue;

            foreach (var note in part.Root.Elements())
            {
                var id = note.Attribute(W + "id")?.Value;
                // Ids 0 and -1 are Word's separator notes, which have no content worth showing.
                if (id == null || id is "0" or "-1" || notes.ContainsKey(id)) continue;
                var text = string.Concat(note.Descendants(W + "t").Select(t => t.Value)).Trim();
                if (text.Length > 0) notes[id] = text;
            }
        }
        return notes;
    }

    // ---------------------------------------------------------------- parts

    private static XDocument? Part(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry == null) return null;
        using var stream = entry.Open();
        try
        {
            return XDocument.Load(stream);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, Relationship> ReadRelationships(ZipArchive archive)
    {
        var map = new Dictionary<string, Relationship>(StringComparer.Ordinal);
        var part = Part(archive, "word/_rels/document.xml.rels");
        if (part?.Root == null) return map;

        foreach (var element in part.Root.Elements(Rel + "Relationship"))
        {
            var id = element.Attribute("Id")?.Value;
            var target = element.Attribute("Target")?.Value;
            if (id == null || target == null) continue;
            map[id] = new Relationship(target, element.Attribute("TargetMode")?.Value == "External");
        }
        return map;
    }

    private static StyleSheet ReadStyles(ZipArchive archive)
    {
        var declared = new Dictionary<string, DocxStyle>(StringComparer.Ordinal);
        var part = Part(archive, "word/styles.xml");
        if (part?.Root == null) return new StyleSheet(declared, default, "Calibri", 11);

        var defaultRun = part.Root.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
        var defaults = RunFormatOf(defaultRun);

        foreach (var element in part.Root.Elements(W + "style"))
        {
            var id = element.Attribute(W + "styleId")?.Value;
            if (id == null) continue;
            declared[id] = new DocxStyle(
                id,
                element.Element(W + "name")?.Attribute(W + "val")?.Value,
                element.Element(W + "basedOn")?.Attribute(W + "val")?.Value,
                ParagraphFormatOf(element.Element(W + "pPr")),
                RunFormatOf(element.Element(W + "rPr")));
        }

        return new StyleSheet(declared, defaults, defaults.Font ?? "Calibri", defaults.Size ?? 11);
    }

    /// <summary>Maps "numId:level" to a marker and depth, following numId to its abstract definition.</summary>
    private static Dictionary<string, NumberingLevel> ReadNumbering(ZipArchive archive)
    {
        var levels = new Dictionary<string, NumberingLevel>(StringComparer.Ordinal);
        var part = Part(archive, "word/numbering.xml");
        if (part?.Root == null) return levels;

        var abstractFormats = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var element in part.Root.Elements(W + "abstractNum"))
        {
            var id = element.Attribute(W + "abstractNumId")?.Value;
            if (id == null) continue;
            var byLevel = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var level in element.Elements(W + "lvl"))
                byLevel[level.Attribute(W + "ilvl")?.Value ?? "0"] =
                    level.Element(W + "numFmt")?.Attribute(W + "val")?.Value ?? "bullet";
            abstractFormats[id] = byLevel;
        }

        foreach (var element in part.Root.Elements(W + "num"))
        {
            var numId = element.Attribute(W + "numId")?.Value;
            var abstractId = element.Element(W + "abstractNumId")?.Attribute(W + "val")?.Value;
            if (numId == null || abstractId == null || !abstractFormats.TryGetValue(abstractId, out var byLevel)) continue;
            foreach (var (level, format) in byLevel)
                levels[$"{numId}:{level}"] = new NumberingLevel(Marker(format), int.TryParse(level, out var depth) ? depth : 0);
        }
        return levels;
    }

    private static TextMarkerStyle Marker(string format) => format switch
    {
        "bullet" => TextMarkerStyle.Disc,
        "upperLetter" => TextMarkerStyle.UpperLatin,
        "lowerLetter" => TextMarkerStyle.LowerLatin,
        "upperRoman" => TextMarkerStyle.UpperRoman,
        "lowerRoman" => TextMarkerStyle.LowerRoman,
        "none" => TextMarkerStyle.None,
        _ => TextMarkerStyle.Decimal,
    };

    /// <summary>The page size and margins from the final section, which is the one that describes the document.</summary>
    private static PageSetup ReadPageSetup(XElement body)
    {
        var section = body.Elements(W + "sectPr").LastOrDefault()
                      ?? body.Descendants(W + "sectPr").LastOrDefault();
        if (section == null) return PageSetup.Default;

        var size = section.Element(W + "pgSz");
        var margin = section.Element(W + "pgMar");

        var width = Twips(size?.Attribute(W + "w")?.Value) ?? PageSetup.Default.Width;
        var height = Twips(size?.Attribute(W + "h")?.Value) ?? PageSetup.Default.Height;

        var thickness = new Thickness(
            Twips(margin?.Attribute(W + "left")?.Value) ?? 96,
            Twips(margin?.Attribute(W + "top")?.Value) ?? 96,
            Twips(margin?.Attribute(W + "right")?.Value) ?? 96,
            Twips(margin?.Attribute(W + "bottom")?.Value) ?? 96);

        // A landscape or oddly sized page is fine; a nonsensical one is not worth honouring.
        if (width is < 144 or > 4000 || height is < 144 or > 4000) return PageSetup.Default;
        return new PageSetup(width, height, thickness);
    }

    /// <summary>Parts whose mere presence means something in the file won't come across.</summary>
    private static void NoteUnsupportedParts(ZipArchive archive, SortedSet<string> unsupported)
    {
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.StartsWith("word/header", StringComparison.Ordinal)) unsupported.Add("page headers");
            else if (name.StartsWith("word/footer", StringComparison.Ordinal)) unsupported.Add("page footers");
            else if (name.StartsWith("word/comments", StringComparison.Ordinal)) unsupported.Add("comments");
        }
    }
}
