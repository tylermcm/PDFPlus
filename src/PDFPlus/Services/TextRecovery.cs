using PDFPlus.Core;

namespace PDFPlus.Services;

/// <summary>
/// Drives OCR for pages whose text layer can't be read, one page at a time and never twice for the same page.
/// This lives outside <see cref="PdfDocument"/> because that class is compiled as unsafe, and C# doesn't allow
/// 'await' in an unsafe context.
/// </summary>
internal static class TextRecovery
{
    public static bool IsAvailable => OcrService.IsAvailable;

    /// <summary>
    /// Reads a page with OCR if its own text layer is missing or unusable. Returns true when the page's text
    /// is now readable, false when it was already fine, OCR is unavailable, or the page holds no text.
    /// Call on the UI thread.
    /// </summary>
    public static Task<bool> EnsureAsync(PdfDocument document, int index, bool force = false)
    {
        if (document.HasRecoveredText(index)) return Task.FromResult(true);
        if (document.Reading.TryGetValue(index, out var running)) return running;
        if (!force && !document.NeedsTextRecovery(index)) return Task.FromResult(false);

        var task = RunAsync(document, index);
        document.Reading[index] = task;
        task.ContinueWith(_ => document.Reading.Remove(index),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        return task;
    }

    private static async Task<bool> RunAsync(PdfDocument document, int index)
    {
        RecognizedText? text = null;
        try
        {
            text = await OcrService.ReadPageAsync(document, index);
        }
        catch
        {
            // A page that won't render or recognise just keeps whatever text it already had.
        }
        document.StoreRecoveredText(index, text);
        return text != null;
    }

    /// <summary>
    /// Reads every page that needs it, reporting pages finished so far. Used by "Read text with OCR", which
    /// also forces pages the automatic check decided were fine.
    /// </summary>
    public static async Task<int> EnsureAllAsync(PdfDocument document, bool force, IProgress<int>? progress, CancellationToken token)
    {
        var recovered = 0;
        for (var i = 0; i < document.PageCount; i++)
        {
            token.ThrowIfCancellationRequested();
            if (await EnsureAsync(document, i, force)) recovered++;
            progress?.Report(i + 1);
        }
        return recovered;
    }
}
