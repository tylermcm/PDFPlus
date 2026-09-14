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

            // ---- Phase 3: editing existing text and images
            await EditTestsAsync(input, outputDirectory, Check, log);

            // ---- Home: images to PDF, previews
            ImagesToPdfTests(input, outputDirectory, Check);
        }
        catch (Exception ex)
        {
            Check("unexpected exception", false, ex.ToString());
        }

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "selftest.log"), log.ToString());
        return failures;
    }

    private static async Task EditTestsAsync(string input, string outputDirectory, Action<string, bool, string> check, StringBuilder log)
    {
        var none = CancellationToken.None;
        int ImageCount(PdfDocument d) => d.GetPageObjects(0).Count(o => !o.IsText);

        using (var doc = await PdfDocument.OpenAsync(input, null))
        {
            var fox = doc.GetPageObjects(0).FirstOrDefault(o => o.IsText && o.Text.StartsWith("The quick brown fox"));
            check("edit: find text run", fox != null, string.Join(" | ", doc.GetPageObjects(0).Where(o => o.IsText).Select(o => o.Text)));
            if (fox == null) return;
            log.AppendLine($"      run font={fox.Font} size={fox.FontSize:0.##} bounds={fox.Bounds}");

            var before = RenderFull(doc, 0);
            var result = doc.ReplaceText(fox, "The quick brown cat naps under the warm sun.");
            check("edit: standard font reused", result.Outcome == TextFontOutcome.OriginalFont, result.ToString());
            var hit = doc.Search("The quick brown cat", false, false, none).FirstOrDefault();
            check("edit: new text searchable, old text gone", hit != null && doc.Search("jumps over the lazy", false, false, none).Count == 0, "");
            if (hit != null)
            {
                var old = fox.Bounds;
                check("edit: new text starts where the old did", Math.Abs(hit.Rects[0].X - old.X) < 1.5, $"{hit.Rects[0]} vs {old}");
                var region = doc.GetPageToDisplay(0).TransformBounds(Rect.Union(old, hit.Rects[0]));
                region.Inflate(4, 4);
                var after = RenderFull(doc, 0);
                var changed = CountChangedOutside(before, after, region);
                check("edit: rest of the page untouched", changed < 30, $"{changed} pixels changed outside the edit");
                SavePng(after, Path.Combine(outputDirectory, "edit-standard-font.png"));
            }

            var name = doc.GetPageObjects(0).FirstOrDefault(o => o.IsText && o.Text == "Name:");
            if (name != null)
            {
                var unicode = doc.ReplaceText(name, "Namn → Łukasz:");
                check("edit: characters the font lacks embed a Windows font",
                    unicode.Outcome is TextFontOutcome.MatchingFont or TextFontOutcome.SubstituteFont &&
                    doc.Search("Łukasz", false, false, none).Count == 1, unicode.ToString());
            }
            else
            {
                check("edit: find 'Name:' run", false, "");
            }

            var cat = doc.GetPageObjects(0).First(o => o.IsText && o.Text.StartsWith("The quick brown cat"));
            doc.MoveObject(cat, new Vector(0, -120));
            var moved = doc.GetPageObjects(0).FirstOrDefault(o => o.IsText && o.Text.StartsWith("The quick brown cat"));
            var movedHit = doc.Search("The quick brown cat", false, false, none).FirstOrDefault();
            check("edit: move text", moved != null && Math.Abs(moved.Bounds.Y - (cat.Bounds.Y - 120)) < 1 &&
                                     movedHit != null && hit != null && Math.Abs(movedHit.Rects[0].Y - (hit.Rects[0].Y - 120)) < 1.5,
                $"{moved?.Bounds} / {movedHit?.Rects[0]}");

            var png = Path.Combine(outputDirectory, "edit-image.png");
            WriteTestImage(png, 360, 240);
            var imagesBefore = ImageCount(doc);
            var placed = doc.AddImage(0, png, new Point(420, 330));
            var image = doc.GetPageObjects(0).LastOrDefault(o => !o.IsText);
            check("edit: add image", ImageCount(doc) == imagesBefore + 1 && image != null, placed.ToString());
            if (image != null)
            {
                var shown = doc.GetPageToDisplay(0).TransformBounds(image.Bounds);
                check("edit: image lands where placed", Math.Abs(shown.X - placed.X) < 1 && Math.Abs(shown.Y - placed.Y) < 1 && Math.Abs(shown.Width - placed.Width) < 1,
                    $"{shown} vs {placed}");
                doc.ResizeImage(image, new Rect(image.Bounds.X, image.Bounds.Y, image.Bounds.Width / 2, image.Bounds.Height / 2));
                var resized = doc.GetPageObjects(0).Last(o => !o.IsText);
                check("edit: resize image", Math.Abs(resized.Bounds.Width - image.Bounds.Width / 2) < 1, resized.Bounds.ToString());
                doc.MoveObject(resized, new Vector(-250, 0));
                var render = RenderFull(doc, 0);
                SavePng(render, Path.Combine(outputDirectory, "edit-image-page.png"));
                check("edit: image renders", CountColored(render) > 3000, $"{CountColored(render)} colored pixels");
            }

            var editedPath = Path.Combine(outputDirectory, "edited-sample.pdf");
            doc.Save(editedPath);
            var growth = new FileInfo(editedPath).Length - new FileInfo(input).Length;
            using (var reopened = await PdfDocument.OpenAsync(editedPath, null))
                check("edit: edits survive save", reopened.Search("Łukasz", false, false, none).Count == 1 &&
                                                  reopened.Search("brown cat naps", false, false, none).Count == 1 && ImageCount(reopened) == imagesBefore + 1,
                    $"file grew {growth:N0} bytes");

            doc.DeleteObject(doc.GetPageObjects(0).Last(o => !o.IsText));
            check("edit: delete image", ImageCount(doc) == imagesBefore, "");
            doc.Undo();
            check("edit: undo brings it back", ImageCount(doc) == imagesBefore + 1, "");
        }

        var samplePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, "edit-sample.pdf");
        if (!File.Exists(samplePath))
        {
            check("edit sample (tests/edit-sample.pdf missing)", false, "");
            return;
        }
        using (var doc = await PdfDocument.OpenAsync(samplePath, null))
        {
            var objects = doc.GetPageObjects(0);
            foreach (var o in objects)
                log.AppendLine($"      {o.Kind} [{string.Join(",", o.Indices)}] \"{o.Text}\" {o.Font?.PdfName} -> {o.Font?.Family} w{o.Font?.Weight} " +
                               $"size={o.FontSize:0.##} bounds={o.Bounds.X:0.#},{o.Bounds.Y:0.#} {o.Bounds.Width:0.#}x{o.Bounds.Height:0.#}");

            var invoice = objects.FirstOrDefault(o => o.IsText && o.Text.StartsWith("Invoice number"));
            check("edit sample: find invoice line", invoice != null, "");
            if (invoice == null) return;
            var before = RenderFull(doc, 0);
            var reuse = doc.ReplaceText(invoice, "Invoice number: INV-10244");
            check("edit sample: subset font reused for glyphs it has", reuse.Outcome == TextFontOutcome.OriginalFont, reuse.ToString());
            var afterReuse = RenderFull(doc, 0);
            var region = doc.GetPageToDisplay(0).TransformBounds(invoice.Bounds);
            region.Inflate(4, 4);
            var changed = CountChangedOutside(before, afterReuse, region);
            check("edit sample: rest of the page untouched", changed < 30, $"{changed} pixels changed outside the edit");
            SavePng(before, Path.Combine(outputDirectory, "edit-sample-before.png"));
            SavePng(afterReuse, Path.Combine(outputDirectory, "edit-sample-reuse.png"));

            invoice = doc.GetPageObjects(0).First(o => o.IsText && o.Text.StartsWith("Invoice number"));
            var match = doc.ReplaceText(invoice, "Invoice number: INV-10599 (paid)");
            check("edit sample: missing glyphs embed matching Windows font",
                match.Outcome == TextFontOutcome.MatchingFont && match.FontFamily == "Calibri" &&
                doc.Search("INV-10599 (paid)", false, false, none).Count == 1, match.ToString());

            var bold = objects.FirstOrDefault(o => o.IsText && o.Text.Contains("4.2 million"));
            check("edit sample: bold words are their own run", bold?.Font?.Bold == true, bold?.Text ?? "none");

            var picture = doc.GetPageObjects(0).FirstOrDefault(o => !o.IsText);
            check("edit sample: canvas image found", picture != null, "");
            if (picture != null)
            {
                doc.MoveObject(picture, new Vector(60, 0));
                var movedPicture = doc.GetPageObjects(0).FirstOrDefault(o => !o.IsText);
                check("edit sample: move image", movedPicture != null && Math.Abs(movedPicture.Bounds.X - picture.Bounds.X - 60) < 1, movedPicture?.Bounds.ToString() ?? "");
            }
            SavePng(RenderFull(doc, 0), Path.Combine(outputDirectory, "edit-sample-final.png"));

            var editedPath = Path.Combine(outputDirectory, "edit-sample-edited.pdf");
            doc.Save(editedPath);
            var growth = new FileInfo(editedPath).Length - new FileInfo(samplePath).Length;
            using var reopened = await PdfDocument.OpenAsync(editedPath, null);
            check("edit sample: saved with a small font subset", growth < 80_000 && reopened.Search("INV-10599 (paid)", false, false, none).Count == 1,
                $"file grew {growth:N0} bytes");
        }
    }

    private static void ImagesToPdfTests(string input, string outputDirectory, Action<string, bool, string> check)
    {
        var wide = Path.Combine(outputDirectory, "images-wide.png");
        var tall = Path.Combine(outputDirectory, "images-tall.png");
        WriteTestImage(wide, 400, 300);
        WriteTestImage(tall, 200, 500);
        using (var doc = PdfDocument.CreateFromImages([wide, tall]))
        {
            PageSize first = doc.PageSizes[0], second = doc.PageSizes[1];
            check("images to pdf: a page per picture, no undo history", doc.PageCount == 2 && !doc.CanUndo && doc.IsDirty, $"{doc.PageCount} pages");
            check("images to pdf: pages shaped like the pictures",
                Math.Abs(first.Width - 792) < 0.5 && Math.Abs(first.Height - 594) < 0.5 && Math.Abs(second.Height - 792) < 0.5 && Math.Abs(second.Width - 316.8) < 0.5,
                $"{first.Width}x{first.Height}, {second.Width}x{second.Height}");
            var render = RenderFull(doc, 1);
            var pixels = new int[render.PixelWidth * render.PixelHeight];
            render.CopyPixels(pixels, render.PixelWidth * 4, 0);
            var covered = pixels.Count(p => ((p >> 16) & 0xFF) + ((p >> 8) & 0xFF) + (p & 0xFF) < 750) / (double)pixels.Length;
            check("images to pdf: picture fills its page", covered > 0.9, $"{covered:P0} covered");
            var path = Path.Combine(outputDirectory, "images-to-pdf.pdf");
            doc.Save(path);
            check("images to pdf: saves", new FileInfo(path).Length > 1000, $"{new FileInfo(path).Length:N0} bytes");
        }

        var preview = PdfThumbnail.Render(input, 150, 200);
        check("home preview: first page thumbnail", preview.Image is { PixelHeight: > 150 } && preview.PageCount == 43 && !preview.IsProtected,
            $"{preview.Image?.PixelWidth}x{preview.Image?.PixelHeight}, {preview.PageCount} pages");
        var locked = Path.Combine(outputDirectory, "protected.pdf");
        if (File.Exists(locked)) check("home preview: protected file", PdfThumbnail.Render(locked, 150, 200).IsProtected, "");
    }

    private static BitmapSource RenderFull(PdfDocument doc, int page)
    {
        var size = doc.PageSizes[page];
        int w = (int)Math.Round(size.Width), h = (int)Math.Round(size.Height);
        return doc.Render(page, w, h, new Int32Rect(0, 0, w, h))!;
    }

    private static int CountChangedOutside(BitmapSource a, BitmapSource b, Rect region)
    {
        int w = a.PixelWidth, h = a.PixelHeight;
        var pa = new int[w * h];
        var pb = new int[w * h];
        a.CopyPixels(pa, w * 4, 0);
        b.CopyPixels(pb, w * 4, 0);
        var changed = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (region.Contains(x + 0.5, y + 0.5)) continue;
            int p = pa[y * w + x], q = pb[y * w + x];
            var diff = Math.Abs(((p >> 16) & 0xFF) - ((q >> 16) & 0xFF)) + Math.Abs(((p >> 8) & 0xFF) - ((q >> 8) & 0xFF)) + Math.Abs((p & 0xFF) - (q & 0xFF));
            if (diff > 24) changed++;
        }
        return changed;
    }

    private static void WriteTestImage(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = (byte)(255 * y / height);
            pixels[i + 1] = (byte)(80 + 100 * x / width);
            pixels[i + 2] = (byte)(255 - 255 * x / width);
            pixels[i + 3] = x > width - 40 && y < 40 ? (byte)0 : (byte)255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
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
