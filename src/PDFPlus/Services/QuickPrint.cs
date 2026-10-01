using System.IO;
using PDFPlus.Core;
using PDFPlus.Views;

namespace PDFPlus.Services;

/// <summary>
/// The shell's "print" and "printto" verbs. Another program hands us a file and expects it on paper with no window:
/// Outlook's Quick Print, Explorer's right-click Print, and dragging a PDF onto a printer all come through here.
/// Returns the process exit code.
/// </summary>
internal static class QuickPrint
{
    public static async Task<int> RunAsync(string path, string? printerName)
    {
        try
        {
            if (!File.Exists(path))
            {
                Dialogs.Error(null, "Nothing to print", $"{Path.GetFileName(path)} couldn't be found.");
                return 1;
            }

            PdfLibrary.EnsureInitialized();
            string? password = null;
            while (true)
            {
                try
                {
                    using var document = await PdfDocument.OpenAsync(path, password);
                    PrintService.PrintSilently(document, printerName);
                    return 0;
                }
                catch (PdfPasswordRequiredException ex)
                {
                    // Nothing can be printed without it, so this is the one time Quick Print has to ask.
                    password = Dialogs.Password(null, Path.GetFileName(path), ex.WrongPassword);
                    if (password == null) return 1;
                }
            }
        }
        catch (Exception ex)
        {
            Dialogs.Error(null, $"Couldn't print {Path.GetFileName(path)}", ex.Message);
            return 1;
        }
    }
}
