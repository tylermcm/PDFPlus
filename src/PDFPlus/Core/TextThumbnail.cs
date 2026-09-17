using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PDFPlus.Core;

/// <summary>
/// First-page previews for text files on the Home screen, drawn from the first few lines so a .txt card looks
/// like the document it is rather than like a PDF that failed to load.
///
/// Call on the UI thread: RenderTargetBitmap and FlowDocument both need one.
/// </summary>
public static class TextThumbnail
{
    private const int MaxLines = 26;
    private const int MaxLineLength = 90;

    public static PdfPreview Render(string path, int maxWidth, int maxHeight)
    {
        var lines = ReadFirstLines(path);

        // A page shape, like the PDF previews sit in.
        var width = Math.Max(1, maxWidth);
        var height = Math.Max(1, (int)Math.Round(width * 11.0 / 8.5));
        if (height > maxHeight)
        {
            height = Math.Max(1, maxHeight);
            width = Math.Max(1, (int)Math.Round(height * 8.5 / 11.0));
        }

        var typeface = new Typeface(new FontFamily("Consolas, Courier New"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var fontSize = Math.Max(3.0, height / 34.0);
        var margin = width * 0.1;
        var ink = new SolidColorBrush(Color.FromRgb(0x33, 0x35, 0x3C));
        ink.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var y = margin;
            foreach (var line in lines)
            {
                if (y + fontSize > height - margin) break;
                if (line.Length > 0)
                {
                    var text = new FormattedText(line, System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, typeface, fontSize, ink, 1.0)
                    {
                        MaxTextWidth = Math.Max(1, width - margin * 2),
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis,
                    };
                    context.DrawText(text, new Point(margin, y));
                }
                y += fontSize * 1.45;
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return new PdfPreview(bitmap, 0, false);
    }

    private static List<string> ReadFirstLines(string path)
    {
        var lines = new List<string>();
        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            while (lines.Count < MaxLines && reader.ReadLine() is { } line)
            {
                // Tabs would otherwise render as a single narrow glyph and throw the indentation out.
                line = line.Replace("\t", "    ");
                lines.Add(line.Length > MaxLineLength ? line[..MaxLineLength] : line);
            }
        }
        catch
        {
            // An unreadable file just gets a blank sheet, the same as an empty one.
        }
        return lines;
    }
}
