using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFPlus.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PDFPlus.Services;

/// <summary>
/// Reads text off a rendered page using the OCR engine built into Windows. It runs entirely on this machine
/// and needs no network and no extra download, so it doesn't change the promise that files never leave the PC.
/// Used only when a page's own text layer is missing or unusable; see PdfDocument.NeedsTextRecovery.
/// </summary>
internal static class OcrService
{
    /// <summary>Render resolution for recognition. Well above screen DPI, because OCR accuracy depends on it.</summary>
    private const double Dpi = 300;

    private static OcrEngine? _engine;
    private static bool _tried;

    /// <summary>Null when Windows has no OCR language pack installed, which is the one case we can't recover from.</summary>
    private static OcrEngine? Engine()
    {
        if (_tried) return _engine;
        _tried = true;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_engine == null && OcrEngine.IsLanguageSupported(new Language("en-US")))
                _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        }
        catch
        {
            // Older Windows, or the OCR runtime class isn't registered. Callers fall back to the PDF's own text.
            _engine = null;
        }
        return _engine;
    }

    public static bool IsAvailable => Engine() != null;

    /// <summary>
    /// Renders one page and recognises its text, returning boxes in PDF page space.
    /// Returns null when OCR isn't available or the page turned out to be blank.
    /// </summary>
    public static async Task<RecognizedText?> ReadPageAsync(PdfDocument document, int index)
    {
        var engine = Engine();
        if (engine == null) return null;
        if ((uint)index >= (uint)document.PageCount) return null;

        var size = document.PageSizes[index];
        var scale = Dpi / 72.0;
        var limit = (double)OcrEngine.MaxImageDimension;
        scale = Math.Min(scale, limit / Math.Max(size.Width, size.Height));
        if (scale <= 0) return null;

        var width = Math.Max(1, (int)Math.Round(size.Width * scale));
        var height = Math.Max(1, (int)Math.Round(size.Height * scale));

        // Rendering and the pixel copy both move tens of megabytes around, so they stay off the UI thread.
        var bitmap = await Task.Run(() =>
        {
            var rendered = document.Render(index, width, height, new Int32Rect(0, 0, width, height));
            return rendered == null ? null : ToSoftwareBitmap(rendered);
        });
        if (bitmap == null) return null;

        OcrResult result;
        using (bitmap)
        {
            result = await engine.RecognizeAsync(bitmap);
        }
        if (result == null) return null;

        // OCR works in bitmap pixels; the rest of the app works in PDF page space (origin bottom-left, /Rotate applied).
        var toPage = document.GetPageToDisplay(index).Invert();
        var builder = new RecognizedTextBuilder();
        foreach (var line in InReadingOrder(result.Lines))
        {
            builder.StartLine();
            foreach (var word in line.Words)
            {
                var box = word.BoundingRect;
                var display = new Rect(box.X / scale, box.Y / scale, box.Width / scale, box.Height / scale);
                builder.AddWord(word.Text, toPage.TransformBounds(display));
            }
        }
        return builder.IsEmpty ? null : builder.Build();
    }

    /// <summary>
    /// Sorts recognised lines the way a person reads them. The engine returns lines in its own order, which on
    /// a table can run down one column before starting the next; that would make dragging across a row select
    /// scattered text. Lines sitting side by side are gathered into a row and ordered left to right.
    /// </summary>
    private static List<OcrLine> InReadingOrder(IReadOnlyList<OcrLine> lines)
    {
        var boxed = lines
            .Select(line => (Line: line, Box: Bounds(line)))
            .Where(item => item.Box.Height > 0)
            .OrderBy(item => item.Box.Top)
            .ToList();

        var ordered = new List<OcrLine>(boxed.Count);
        for (var i = 0; i < boxed.Count;)
        {
            var row = new List<(OcrLine Line, Rect Box)> { boxed[i] };
            var band = boxed[i].Box;
            var j = i + 1;
            while (j < boxed.Count && Overlap(band, boxed[j].Box) > Math.Min(band.Height, boxed[j].Box.Height) * 0.5)
            {
                row.Add(boxed[j]);
                band.Union(boxed[j].Box);
                j++;
            }
            ordered.AddRange(row.OrderBy(item => item.Box.Left).Select(item => item.Line));
            i = j;
        }
        return ordered;
    }

    private static double Overlap(Rect a, Rect b) => Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

    private static Rect Bounds(OcrLine line)
    {
        var box = Rect.Empty;
        foreach (var word in line.Words)
            box.Union(new Rect(word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height));
        return box;
    }

    private static SoftwareBitmap? ToSoftwareBitmap(BitmapSource source)
    {
        try
        {
            var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            return SoftwareBitmap.CreateCopyFromBuffer(
                pixels.AsBuffer(), BitmapPixelFormat.Bgra8, converted.PixelWidth, converted.PixelHeight, BitmapAlphaMode.Ignore);
        }
        catch
        {
            return null;
        }
    }
}
