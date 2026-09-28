using System.IO;
using System.Text;
using System.Windows.Documents;
using System.Windows.Markup;

namespace PDFPlus.Core;

public enum TextFormat
{
    /// <summary>.txt, .md, .log, .csv and friends: characters only, no styling to keep.</summary>
    Plain,
    /// <summary>.rtf: styling is part of the file and survives a round trip.</summary>
    Rtf,
    /// <summary>.docx: a Word document. Read and written for what it says, not for how Word lays it out.</summary>
    Docx,
}

/// <summary>
/// A text or rich text file open for editing, held as the <see cref="FlowDocument"/> the editor binds to.
///
/// Saving writes the format the file arrived in, so opening a .txt and pressing Ctrl+S leaves a .txt. For plain
/// text that means keeping the details a naive save would quietly change: the encoding, whether there was a byte
/// order mark, and whether lines ended in CRLF or LF.
/// </summary>
public sealed class TextDocument
{
    /// <summary>Plain text is one paragraph per line, so a big log would otherwise build a huge visual tree.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    private static int _untitledCounter;
    private readonly string _untitledName;

    private TextDocument(FlowDocument content, string? path, TextFormat format, Encoding encoding, bool byteOrderMark, string newLine)
    {
        Unsupported = [];
        Page = Docx.PageSetup.Default;
        Content = content;
        FilePath = path;
        Format = format;
        Encoding = encoding;
        ByteOrderMark = byteOrderMark;
        NewLine = newLine;
        _untitledName = $"Untitled {++_untitledCounter}.txt";
    }

    public FlowDocument Content { get; }
    public string? FilePath { get; private set; }
    public TextFormat Format { get; private set; }
    public Encoding Encoding { get; private set; }
    public bool ByteOrderMark { get; private set; }
    public string NewLine { get; private set; }
    public bool IsDirty { get; set; }

    /// <summary>
    /// Things the file uses that PDFPlus knowingly does not keep, such as headers or footnotes. Empty for
    /// formats that round trip completely. The app warns with this before overwriting the original.
    /// </summary>
    public IReadOnlyList<string> Unsupported { get; private set; }

    /// <summary>The page the document was written for. Plain and rich text have no such thing, so they get a default.</summary>
    public Docx.PageSetup Page { get; private set; }

    public string Title => FilePath != null ? Path.GetFileName(FilePath) : _untitledName;

    /// <summary>File types the editor can open. PDFs are handled by <see cref="PdfDocument"/> instead.</summary>
    public static readonly string[] Extensions =
        [".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".ini", ".json", ".xml", ".rtf", ".docx"];

    public static bool Handles(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static TextFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".rtf" => TextFormat.Rtf,
        ".docx" => TextFormat.Docx,
        _ => TextFormat.Plain,
    };

    /// <summary>Formats that keep bold, fonts, colour and lists.</summary>
    public static bool IsRich(TextFormat format) => format is TextFormat.Rtf or TextFormat.Docx;

    public static TextDocument Empty()
    {
        var document = NewFlowDocument();
        document.Blocks.Add(new Paragraph());
        return new TextDocument(document, null, TextFormat.Plain, new UTF8Encoding(false), false, Environment.NewLine);
    }

    public static async Task<TextDocument> OpenAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var length = new FileInfo(fullPath).Length;
        if (length > MaxBytes)
            throw new IOException($"This file is {length / 1024 / 1024:N0} MB. PDFPlus opens text files up to {MaxBytes / 1024 / 1024} MB.");

        var format = FormatOf(fullPath);

        if (format == TextFormat.Docx)
        {
            // Building a FlowDocument has to happen on the thread that will show it, so this part isn't awaited.
            var import = Docx.DocxReader.Read(fullPath);
            return new TextDocument(import.Document, fullPath, format, new UTF8Encoding(false), false, Environment.NewLine)
            {
                Unsupported = import.Unsupported,
                Page = import.Page,
            };
        }

        var bytes = await File.ReadAllBytesAsync(fullPath);

        if (format == TextFormat.Rtf)
        {
            var document = NewFlowDocument();
            using var stream = new MemoryStream(bytes, writable: false);
            new TextRange(document.ContentStart, document.ContentEnd).Load(stream, System.Windows.DataFormats.Rtf);
            return new TextDocument(document, fullPath, format, new UTF8Encoding(false), false, Environment.NewLine);
        }

        var (encoding, byteOrderMark, offset) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, offset, bytes.Length - offset);
        var newLine = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
        return new TextDocument(FromPlainText(text), fullPath, format, encoding, byteOrderMark, newLine);
    }

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var format = FormatOf(fullPath);

        if (format == TextFormat.Docx)
        {
            Docx.DocxWriter.Write(Content, fullPath, Page);
        }
        else if (format == TextFormat.Rtf)
        {
            using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
            new TextRange(Content.ContentStart, Content.ContentEnd).Save(stream, System.Windows.DataFormats.Rtf);
        }
        else
        {
            // Saving as plain text when the document arrived as RTF drops the styling, which is what the user
            // asked for by choosing a .txt name; the formatting toolbar warns before that happens.
            var encoding = format == Format ? Encoding : new UTF8Encoding(false);
            var writeBom = format == Format && ByteOrderMark;
            using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
            if (writeBom)
            {
                var preamble = encoding.GetPreamble();
                stream.Write(preamble, 0, preamble.Length);
            }
            var bytes = encoding.GetBytes(ToPlainText());
            stream.Write(bytes, 0, bytes.Length);
        }

        FilePath = fullPath;
        Format = format;
        IsDirty = false;
    }

    /// <summary>The document as characters, with the line endings the file was opened with.</summary>
    public string ToPlainText()
    {
        var lines = Content.Blocks.OfType<Paragraph>()
            .Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Replace("\r", "").Replace("\n", ""));
        return string.Join(NewLine, lines);
    }

    private static FlowDocument NewFlowDocument() => new()
    {
        PagePadding = new System.Windows.Thickness(0),
        // A FlowDocument defaults to justified columns, which is not what a document editor should look like.
        ColumnWidth = double.PositiveInfinity,
    };

    private static FlowDocument FromPlainText(string text)
    {
        var document = NewFlowDocument();
        foreach (var line in text.Split('\n'))
            document.Blocks.Add(new Paragraph(new Run(line.TrimEnd('\r'))) { Margin = new System.Windows.Thickness(0) });
        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
        return document;
    }

    /// <summary>Reads a byte order mark if there is one; otherwise assumes UTF-8, which also covers plain ASCII.</summary>
    private static (Encoding Encoding, bool ByteOrderMark, int Offset) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true), true, 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(false, true), true, 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(true, true), true, 2);
        return (new UTF8Encoding(false), false, 0);
    }
}
