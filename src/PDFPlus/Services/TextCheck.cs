using System.IO;
using System.Text;
using PDFPlus.Core;

namespace PDFPlus.Services;

/// <summary>
/// Reports what the app can read from a PDF's text layer, and what OCR reads instead:
/// PDFPlus.exe --textcheck input.pdf output-folder  (see textcheck.log)
///
/// Written for diagnosing "selecting text copies gibberish" reports. Only the first lines of each page are
/// logged, so the report can be shared without handing over the document.
/// </summary>
internal static class TextCheck
{
    private const int PagesToReport = 5;
    private const int SampleChars = 160;

    public static async Task<int> RunAsync(string input, string outputDirectory)
    {
        var log = new StringBuilder();
        Directory.CreateDirectory(outputDirectory);
        try
        {
            PdfLibrary.EnsureInitialized();
            using var document = await PdfDocument.OpenAsync(input, null);

            log.AppendLine($"file       {Path.GetFileName(input)}");
            log.AppendLine($"pages      {document.PageCount}");
            log.AppendLine($"ocr engine {(OcrService.IsAvailable ? "available" : "NOT available (no Windows OCR language pack)")}");
            log.AppendLine();

            for (var i = 0; i < Math.Min(PagesToReport, document.PageCount); i++)
            {
                var needs = document.NeedsTextRecovery(i);
                log.AppendLine($"--- page {i + 1} ---");
                log.AppendLine($"  chars in text layer : {document.CharCount(i)}");
                log.AppendLine($"  verdict             : {(needs ? "UNREADABLE, OCR would be used" : "readable, used as-is")}");
                log.AppendLine($"  text layer says     : {Sample(document.GetText(i, 0, SampleChars))}");

                if (!needs) continue;
                await TextRecovery.EnsureAsync(document, i);
                log.AppendLine($"  ocr says            : {Sample(document.GetText(i, 0, SampleChars))}");
                log.AppendLine($"  hit test round trip : {RoundTrip(document, i)}");
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"FAILED  {ex.Message}");
        }

        var path = Path.Combine(outputDirectory, "textcheck.log");
        File.WriteAllText(path, log.ToString());
        Console.WriteLine(log.ToString());
        return 0;
    }

    /// <summary>
    /// Picks a word in the middle of the page, asks where it is, and then asks what is at that spot. Getting the
    /// same word back proves the recovered boxes landed in PDF page space the right way up and the right way round,
    /// which is what makes dragging over the page select the words under the pointer.
    /// </summary>
    private static string RoundTrip(PdfDocument document, int page)
    {
        var count = document.CharCount(page);
        if (count == 0) return "no text";

        var (start, length) = document.WordAt(page, count / 2);
        var word = document.GetText(page, start, length);
        var rects = document.TextRects(page, start, length);
        if (rects.Length == 0) return $"'{word}' has no box";

        var centre = new System.Windows.Point(rects[0].X + rects[0].Width / 2, rects[0].Y + rects[0].Height / 2);
        var found = document.CharIndexAt(page, centre, 5);
        if (found < 0) return $"FAILED: nothing found at the centre of '{word}' ({centre:F0})";

        var (foundStart, foundLength) = document.WordAt(page, found);
        var back = document.GetText(page, foundStart, foundLength);
        return back == word ? $"OK, '{word}' found at {centre:F0}" : $"FAILED: '{word}' at {centre:F0} came back as '{back}'";
    }

    /// <summary>Collapses whitespace and makes control codes visible, since those are the tell-tale sign.</summary>
    private static string Sample(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Replace('\n', ' ').Replace('\r', ' '))
            builder.Append(c < 0x20 ? $"<{(int)c:X2}>" : c.ToString());
        return builder.ToString().Trim();
    }
}
