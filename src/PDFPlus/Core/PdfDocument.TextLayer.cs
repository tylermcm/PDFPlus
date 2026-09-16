using PDFPlus.Services;
using static PDFPlus.Native.Pdfium;

namespace PDFPlus.Core;

/// <summary>
/// Text recovery for pages whose own text layer can't be read.
///
/// Two kinds of page defeat ordinary text selection. A scanned page has no text layer at all. Worse, and far
/// more common, is a page drawn with subset fonts that carry no ToUnicode CMap: every character code in the
/// content stream is a raw glyph id, so the page looks perfect but copying it yields punctuation soup, and the
/// character boxes PDFium reports are the wrong size, which smears the selection highlight across the page.
///
/// Neither is fixable from the file: the mapping back to letters simply isn't in it. So for those pages the app
/// reads the text off the rendered image with the OCR engine built into Windows and uses that instead. The rest
/// of the class asks <see cref="RecognizedFor"/> first and otherwise goes on using PDFium exactly as before.
/// </summary>
public sealed unsafe partial class PdfDocument
{
    /// <summary>Below this many characters there isn't enough evidence to call a text layer broken.</summary>
    private const int MinCharsToJudge = 24;
    private const int QualitySampleSize = 400;

    private readonly Dictionary<int, RecognizedText> _recognized = new();
    private readonly Dictionary<int, bool> _readable = new();
    /// <summary>In-flight OCR, one task per page, so repeated clicks don't queue up duplicate work.</summary>
    internal readonly Dictionary<int, Task<bool>> Reading = new();

    /// <summary>Raised on the UI thread when a page's text has been recovered and selection should be redrawn.</summary>
    public event EventHandler<int>? TextLayerChanged;

    private void ForgetRecoveredText(int index)
    {
        lock (PdfLibrary.Sync)
        {
            _recognized.Remove(index);
            _readable.Remove(index);
        }
    }

    private void ForgetAllRecoveredText()
    {
        lock (PdfLibrary.Sync)
        {
            _recognized.Clear();
            _readable.Clear();
        }
    }

    internal RecognizedText? RecognizedFor(int index)
    {
        lock (PdfLibrary.Sync) return _recognized.GetValueOrDefault(index);
    }

    public bool HasRecoveredText(int index)
    {
        lock (PdfLibrary.Sync) return _recognized.ContainsKey(index);
    }

    /// <summary>True when this page's own text layer is missing or unusable and OCR could do better.</summary>
    public bool NeedsTextRecovery(int index)
    {
        if (!OcrService.IsAvailable) return false;
        lock (PdfLibrary.Sync)
        {
            if (_disposed || (uint)index >= (uint)_sizes.Length) return false;
            if (_recognized.ContainsKey(index)) return false;
            if (_readable.TryGetValue(index, out var readable)) return !readable;

            var textPage = TextPageLocked(index);
            readable = textPage != IntPtr.Zero && !LooksUnreadableLocked(index, textPage);
            _readable[index] = readable;
            return !readable;
        }
    }

    /// <summary>Stores recovered text for a page; null records that OCR ran and found nothing worth using.</summary>
    internal void StoreRecoveredText(int index, RecognizedText? text)
    {
        lock (PdfLibrary.Sync)
        {
            if (_disposed) return;
            if (text != null) _recognized[index] = text;
            else _readable[index] = true; // Don't keep re-running OCR on a page that has no text to find.
        }
        if (text != null) TextLayerChanged?.Invoke(this, index);
    }

    private bool LooksUnreadableLocked(int index, IntPtr textPage)
    {
        var total = FPDFText_CountChars(textPage);
        if (total <= 0) return true;               // A scan, or a page of pure graphics: nothing to select either way.
        if (total < MinCharsToJudge) return false; // A stray label or a page number; trust it.

        var pageWidth = _sizes[index].Width;
        var step = Math.Max(1, total / QualitySampleSize);
        int sampled = 0, unmapped = 0, oversized = 0;
        double left, right, bottom, top;

        for (var i = 0; i < total; i += step)
        {
            var c = FPDFText_GetUnicode(textPage, i);
            if (c is ' ' or '\t' or '\r' or '\n') continue;
            sampled++;
            if (IsUnmapped(c)) unmapped++;
            if (FPDFText_GetCharBox(textPage, i, &left, &right, &bottom, &top) != 0 &&
                Math.Abs(right - left) > pageWidth * 0.25) oversized++;
        }

        if (sampled < MinCharsToJudge) return false;

        // Control codes and replacement characters where letters belong mean the codes never were letters:
        // in glyph-id text the digits and the spaces inside a run land on C0 controls, which is unmistakable.
        // Character boxes far too wide for a single glyph mean the font's metrics came out wrong as well.
        return unmapped >= sampled * 0.05 || oversized >= sampled * 0.20;
    }

    private static bool IsUnmapped(uint c) =>
        c == 0 || c == 0xFFFD || (c < 0x20 && c is not ('\t' or '\r' or '\n')) || c is >= 0xE000 and <= 0xF8FF;
}
