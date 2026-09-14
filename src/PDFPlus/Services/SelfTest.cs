using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDFPlus.Core;

namespace PDFPlus.Services;

/// <summary>
/// Headless smoke test of the PDFium integration, used during development:
/// PDFPlus.exe --selftest input.pdf output-folder  (exit code = number of failures; see selftest.log)
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string input, string outputDirectory)
    {
        var log = new StringBuilder();
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
            if (!ok) failures++;
        }

        Directory.CreateDirectory(outputDirectory);
        try
        {
            PdfLibrary.EnsureInitialized();
            using var doc = await PdfDocument.OpenAsync(input, null);
            Check("open", doc.PageCount > 0, $"{doc.PageCount} pages, forms={doc.HasForms}");

            var size = doc.PageSizes[0];
            int w = (int)size.Width, h = (int)size.Height;
            var bitmap = doc.Render(0, w, h, new Int32Rect(0, 0, w, h));
            var ink = bitmap == null ? 0 : CountInk(bitmap);
            Check("render page 1", ink > 200, $"{ink} dark pixels");
            if (bitmap != null) SavePng(bitmap, Path.Combine(outputDirectory, "page1.png"));

            var tile = doc.Render(0, w * 4, h * 4, new Int32Rect(200, 200, 300, 300));
            Check("render clipped tile", tile is { PixelWidth: 300, PixelHeight: 300 });

            var origin = doc.GetPageToDisplay(0).Transform(new Point(0, size.Height));
            Check("page matrix", Math.Abs(origin.X) < 0.5 && Math.Abs(origin.Y) < 0.5, origin.ToString());

            var rotated = doc.PageSizes[1];
            Check("rotated page size", rotated.Width > rotated.Height, $"{rotated.Width}x{rotated.Height}");

            var hits = doc.Search("PDFPlus", false, false, CancellationToken.None);
            Check("search", hits.Count > 0 && hits[0].Rects.Length > 0, $"{hits.Count} hits");

            var charIndex = doc.CharIndexAt(0, new Point(80, 710), 5);
            Check("char hit test", charIndex >= 0, $"index {charIndex}");
            Check("get text", doc.GetText(0, 0, 7) == "PDFPlus", $"'{doc.GetText(0, 0, 7)}'");

            var bookmarks = doc.GetBookmarks();
            Check("bookmarks", bookmarks.Count == 3 && bookmarks[2].Target?.PageIndex == 2, $"{bookmarks.Count}");

            var uri = doc.LinkAt(0, new Point(100, 543));
            Check("uri link", uri?.Uri?.StartsWith("https://example.com") == true, uri?.Uri ?? "none");
            var jump = doc.LinkAt(0, new Point(100, 503));
            Check("internal link", jump?.PageIndex == 2, $"page {jump?.PageIndex}");

            var fieldType = doc.FormFieldTypeAt(0, new Point(200, 661));
            Check("form field hit", fieldType == 6, $"type {fieldType}");
            doc.FormMouseDown(0, new Point(200, 661), 0, false);
            doc.FormMouseUp(0, new Point(200, 661), 0);
            foreach (var ch in "Hi there") doc.FormChar(ch, 0);
            doc.FormKillFocus();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check("form typing marks dirty", doc.IsDirty);

            doc.AddText(0, [new TextRun("Stamped text", 72, 420)], 14, Colors.Black);
            doc.AddPath(0, [new PathFigureData([new Point(0, 5), new Point(4, 9), new Point(10, 0)], false)],
                Affine.Scale(2, 2).Then(Affine.Translation(300, 380)), Colors.Blue, 1);
            Check("stamped text searchable", doc.Search("Stamped text", false, false, CancellationToken.None).Count == 1);
            doc.AddText(1, [new TextRun("Rotated stamp", 40, 60)], 12, Colors.Red);
            var rotatedHit = doc.Search("Rotated stamp", false, false, CancellationToken.None).FirstOrDefault();
            var rotatedBounds = rotatedHit == null ? Rect.Empty : doc.GetPageToDisplay(1).TransformBounds(rotatedHit.Rects[0]);
            Check("stamp on rotated page lands where placed", !rotatedBounds.IsEmpty && Math.Abs(rotatedBounds.X - 40) < 6 && Math.Abs(rotatedBounds.Bottom - 60) < 6,
                rotatedBounds.ToString());

            var before = doc.PageCount;
            doc.RotatePages([2], 1);
            doc.MovePages([doc.PageCount - 1], 0);
            doc.InsertBlankPage(1, 612, 792);
            Check("insert blank page", doc.PageCount == before + 1);
            doc.DeletePages([1]);
            Check("delete page", doc.PageCount == before);
            doc.Undo();
            Check("undo", doc.PageCount == before + 1);
            doc.Redo();
            Check("redo", doc.PageCount == before);

            var saved = Path.Combine(outputDirectory, "saved.pdf");
            doc.Save(saved);
            Check("save clears dirty", !doc.IsDirty);
            using (var reopened = await PdfDocument.OpenAsync(saved, null))
            {
                Check("reopen saved", reopened.PageCount == before, $"{reopened.PageCount}");
                Check("stamp persists after save", reopened.Search("Stamped text", false, false, CancellationToken.None).Count == 1);
            }

            var extracted = Path.Combine(outputDirectory, "extracted.pdf");
            doc.ExtractPages([0, 1], extracted);
            using (var part = await PdfDocument.OpenAsync(extracted, null))
                Check("extract pages", part.PageCount == 2, $"{part.PageCount}");

            using (var combined = PdfDocument.Combine([(input, null), (extracted, null)]))
                Check("combine files", combined.PageCount == before + 2, $"{combined.PageCount}");

            // ---- Phase 2: annotations
            var p = doc.PageCount - 1;
            doc.AddTextMarkup(p, MarkupKind.Highlight, doc.TextRects(p, 0, 12), Colors.Yellow);
            doc.AddInk(p, [[new Point(100, 100), new Point(150, 140), new Point(200, 100)]], Colors.Red, 2);
            doc.AddShape(p, ShapeKind.Rectangle, new Rect(100, 300, 150, 80), Colors.Blue, 2);
            doc.AddArrow(p, new Point(300, 300), new Point(400, 400), Colors.Green, 2);
            doc.AddNote(p, new Point(500, 50), "Remember this", Colors.Orange);
            var annotations = doc.GetAnnotations(p);
            Check("annotations created", annotations.Count == 5, $"{annotations.Count}");
            Check("note contents", annotations.Any(a => a.IsNote && a.Contents == "Remember this"));
            var pw = (int)doc.PageSizes[p].Width;
            var ph = (int)doc.PageSizes[p].Height;
            var annotated = doc.Render(p, pw, ph, new Int32Rect(0, 0, pw, ph));
            var colored = annotated == null ? 0 : CountColored(annotated);
            Check("annotations render", colored > 500, $"{colored} colored pixels");
            if (annotated != null) SavePng(annotated, Path.Combine(outputDirectory, "annotations.png"));
            doc.DeleteAnnotation(annotations.First(a => a.Subtype == 5));
            Check("delete annotation", doc.GetAnnotations(p).Count == 4);
            doc.UpdateNote(doc.GetAnnotations(p).First(a => a.IsNote), "Updated");
            Check("update note", doc.GetAnnotations(p).First(a => a.IsNote).Contents == "Updated");

            // ---- Phase 2: export
            var png = Path.Combine(outputDirectory, "export-page1.png");
            doc.ExportPageImage(0, png, 150, ImageExportFormat.Png);
            Check("export png", File.Exists(png) && new FileInfo(png).Length > 1000);
            var jpg = Path.Combine(outputDirectory, "export-page1.jpg");
            doc.ExportPageImage(0, jpg, 100, ImageExportFormat.Jpeg);
            Check("export jpeg", File.Exists(jpg) && new FileInfo(jpg).Length > 1000);

            // ---- Phase 2: password protection
            doc.SetProtection(new PdfProtection("secret", "", AllowPrint: true, AllowCopy: false, AllowEdit: false));
            var locked = Path.Combine(outputDirectory, "protected.pdf");
            doc.Save(locked);
            try
            {
                using var probe = await PdfDocument.OpenAsync(locked, null);
                Check("protected file needs password", false);
            }
            catch (PdfPasswordRequiredException)
            {
                Check("protected file needs password", true);
            }
            using (var unlocked = await PdfDocument.OpenAsync(locked, "secret"))
            {
                Check("open with password", unlocked.PageCount == doc.PageCount && unlocked.IsEncrypted);
                Check("annotations survive encryption", unlocked.GetAnnotations(p).Count == 4);
                unlocked.ClearProtection();
                var plain = Path.Combine(outputDirectory, "unprotected.pdf");
                unlocked.Save(plain);
                using var reopenedPlain = await PdfDocument.OpenAsync(plain, null);
                Check("remove password", reopenedPlain.PageCount == doc.PageCount && !reopenedPlain.IsEncrypted);
            }

            // ---- Phase 2: compression
            var imagesPdf = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, "images.pdf");
            if (File.Exists(imagesPdf))
            {
                using var heavy = await PdfDocument.OpenAsync(imagesPdf, null);
                var compressedPath = Path.Combine(outputDirectory, "compressed.pdf");
                var result = heavy.SaveCompressedCopy(compressedPath, 100, 70, CancellationToken.None);
                Check("compress images", result.Written && result.ImagesRecompressed == 3 && result.CompressedBytes < result.OriginalBytes / 3,
                    $"{result.OriginalBytes:N0} -> {result.CompressedBytes:N0} bytes, {result.ImagesRecompressed} images");
                using var light = await PdfDocument.OpenAsync(compressedPath, null);
                var cw = (int)light.PageSizes[0].Width;
                var ch = (int)light.PageSizes[0].Height;
                var compressedRender = light.Render(0, cw, ch, new Int32Rect(0, 0, cw, ch));
                Check("compressed copy renders image", compressedRender != null && CountColored(compressedRender) > 20000);
                if (compressedRender != null) SavePng(compressedRender, Path.Combine(outputDirectory, "compressed-page1.png"));
            }
            else
            {
                Check("compress images (tests/images.pdf missing)", false);
            }
        }
        catch (Exception ex)
        {
            Check("unexpected exception", false, ex.ToString());
        }

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "selftest.log"), log.ToString());
        return failures;
    }

    private static int CountInk(BitmapSource bitmap)
    {
        var pixels = new int[bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels.Count(p => ((p >> 16) & 0xFF) + ((p >> 8) & 0xFF) + (p & 0xFF) < 300);
    }

    private static int CountColored(BitmapSource bitmap)
    {
        var pixels = new int[bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels.Count(px =>
        {
            int r = (px >> 16) & 0xFF, g = (px >> 8) & 0xFF, b = px & 0xFF;
            return Math.Abs(r - g) > 40 || Math.Abs(g - b) > 40 || Math.Abs(r - b) > 40;
        });
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
