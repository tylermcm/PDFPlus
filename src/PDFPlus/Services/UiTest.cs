using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFPlus.Controls;
using PDFPlus.Core;
using PDFPlus.Views;

namespace PDFPlus.Services;

/// <summary>
/// Scripted walk through the real UI, rendered off-screen to PNGs (no global mouse or keyboard input):
/// PDFPlus.exe --uitest input.pdf output-folder
/// </summary>
internal static class UiTest
{
    public static async Task<int> RunAsync(string input, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var log = new StringBuilder();
        var failures = 0;

        void Snap(Window window, string name)
        {
            try
            {
                SaveWindow(window, Path.Combine(outputDirectory, name + ".png"));
                log.AppendLine($"saved {name}.png");
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine($"FAIL snapshot {name}: {ex.Message}");
            }
        }

        try
        {
            // Home screen: a made-up recent history (test mode never loads or saves the real settings).
            var testFolder = Path.GetDirectoryName(Path.GetFullPath(input))!;
            var recent = AppSettings.Current;
            var seeds = new (string Name, double HoursAgo, int Page, bool Pinned)[]
            {
                ("quarterly-report-2025.pdf", 400, 0, false), // doesn't exist: shows the "not found" state
                ("images.pdf", 30, 1, false),
                ("edit-sample.pdf", 5, 0, true),
                (Path.GetFileName(input), 2, 11, false),
            };
            foreach (var (name, hoursAgo, page, pinned) in seeds)
            {
                var path = Path.Combine(testFolder, name);
                recent.AddRecent(path);
                var details = recent.DetailsFor(path)!;
                details.Opened = DateTime.Now.AddHours(-hoursAgo);
                details.Page = page;
                if (pinned) recent.SetPinned(path, true);
            }

            var window = new MainWindow { ShowActivated = false };
            window.WindowState = WindowState.Normal;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;
            window.Top = 0;
            window.Width = 1400;
            window.Height = 900;
            window.Show();
            await Settle(2000);
            Snap(window, "01-home");

            recent.RememberPage(Path.GetFullPath(input), 0);
            await window.OpenFileAsync(input);
            await Settle(1800);
            var view = window.DocumentHost.Children.OfType<DocumentView>().First();
            Snap(window, "02-document");

            view.ShowSearch();
            view.SearchBox.Text = "fox";
            view.FindNext(false);
            await Settle(1500);
            Snap(window, "03-search");
            view.HideSearch();

            view.Viewer.GoToPage(0, new Point(0, 470));
            await Settle(900);
            view.Stamps.Place(StampKind.Text, new PageHit(0, new Point(110, 720)), null);
            await Settle(300);
            if (FindDescendant<TextBox>(view.Stamps) is { } editor) editor.Text = "Typed with the Text tool\nSecond line";
            await Settle(400);
            Snap(window, "04-text-stamp-live");
            view.Stamps.Commit();

            view.Stamps.Place(StampKind.Signature, new PageHit(0, new Point(170, 622)), MakeSignature("Jane Doe"));
            await Settle(400);
            Snap(window, "05-signature-live");
            view.Stamps.Commit();

            view.Stamps.Place(StampKind.Check, new PageHit(0, new Point(148, 190)), null);
            view.Stamps.Commit();

            var doc = view.Document;
            doc.FormMouseDown(0, new Point(200, 661), 0, false);
            doc.FormMouseUp(0, new Point(200, 661), 0);
            foreach (var ch in "Jane Doe") doc.FormChar(ch, 0);
            await Settle(1500);
            Snap(window, "06-filled-and-signed");
            doc.FormKillFocus();

            view.Viewer.SetZoom(4, new Point(420, 260));
            await Settle(2500);
            Snap(window, "07-zoom-400");

            view.Viewer.FitMode = FitMode.Width;
            view.Viewer.GoToPage(1);
            await Settle(1500);
            Snap(window, "08-rotated-page");

            ThemeManager.Apply(true);
            await Settle(900);
            Snap(window, "09-dark-theme");
            ThemeManager.Apply(false);

            var dialog = new SignatureDialog(null) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0 };
            dialog.Show();
            await Settle(600);
            Snap(dialog, "10-signature-dialog");
            dialog.Close();

            var shortcuts = ShortcutsDialog.Create(null);
            shortcuts.ShowActivated = false;
            shortcuts.WindowStartupLocation = WindowStartupLocation.Manual;
            shortcuts.Left = -30000;
            shortcuts.Top = 0;
            shortcuts.Show();
            await Settle(600);
            Snap(shortcuts, "10b-shortcuts");
            shortcuts.Close();

            // Phase 2: annotate mode, selection bar and note editor.
            view.Viewer.FitMode = FitMode.Width;
            view.Viewer.GoToPage(0);
            window.SetToolMode(MainWindow.ToolMode.Annotate);
            var fox = doc.Search("quick brown fox", false, false, CancellationToken.None).FirstOrDefault(h => h.PageIndex == 0);
            if (fox != null) doc.AddTextMarkup(0, MarkupKind.Highlight, fox.Rects, Color.FromRgb(0xFF, 0xD4, 0x00));
            doc.AddInk(0, [[new Point(380, 60), new Point(420, 95), new Point(470, 50), new Point(520, 90)]], Color.FromRgb(0xE5, 0x39, 0x35), 2);
            doc.AddShape(0, ShapeKind.Ellipse, new Rect(60, 175, 120, 40), Color.FromRgb(0x2F, 0x80, 0xED), 2);
            doc.AddArrow(0, new Point(300, 240), new Point(215, 285), Color.FromRgb(0x3D, 0xDC, 0x84), 2);
            doc.AddNote(0, new Point(470, 250), "Check this paragraph before sending.", Color.FromRgb(0xFF, 0xD4, 0x00));
            view.SetAnnotationTool(AnnotationTool.Pen);
            await Settle(1800);
            Snap(window, "11-annotate-mode");

            view.SetAnnotationTool(AnnotationTool.None);
            await Settle(300);
            view.Annotations.TrySelectAt(new PageHit(0, new Point(480, 260)), 1);
            await Settle(600);
            Snap(window, "12-note-selected");
            view.Annotations.EditSelectedNote();
            await Settle(600);
            Snap(window, "13-note-editor");
            view.Annotations.Tool = AnnotationTool.None;
            window.SetToolMode(MainWindow.ToolMode.FillSign);
            await Settle(400);
            Snap(window, "14-fill-sign-row");

            // Right-click menu with a text selection, rendered off-screen.
            window.SetToolMode(MainWindow.ToolMode.None);
            view.Viewer.SelectAllOnPage(0);
            var menu = view.BuildViewContextMenu(new Point(500, 400));
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
            menu.HorizontalOffset = -30000;
            menu.IsOpen = true;
            await Settle(600);
            try
            {
                SaveElement(menu, Path.Combine(outputDirectory, "15-context-menu.png"));
                log.AppendLine("saved 15-context-menu.png");
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine($"FAIL snapshot context menu: {ex.Message}");
            }
            menu.IsOpen = false;
            view.Viewer.ClearSelection();

            // Phase 3: Edit mode on the sample, then on a browser-made PDF with embedded subset fonts.
            window.SetToolMode(MainWindow.ToolMode.Edit);
            view.Viewer.FitMode = FitMode.Width;
            view.Viewer.GoToPage(0);
            await Settle(1200);
            var line = doc.GetPageObjects(0).First(o => o.IsText && o.Text.StartsWith("Select this text"));
            view.Edits.SetHover(line);
            await Settle(300);
            Snap(window, "16-edit-mode");
            view.Edits.OpenEditor(line);
            await Settle(400);
            if (FindDescendant<TextBox>(view.Edits) is { } lineEditor) lineEditor.Text = "Select this line and type whatever you like here.";
            await Settle(400);
            Snap(window, "17-text-editor");
            view.Edits.CommitEditor();
            await Settle(1500);
            Snap(window, "18-text-edited");

            var picture = Path.Combine(outputDirectory, "uitest-picture.png");
            WritePicture(picture);
            view.AddImageAt(0, new Point(430, 440), picture);
            await Settle(1500);
            Snap(window, "19-image-selected");

            var realWorld = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, "edit-sample.pdf");
            if (File.Exists(realWorld))
            {
                await window.OpenFileAsync(realWorld);
                await Settle(1500);
                var sampleView = window.DocumentHost.Children.OfType<DocumentView>().Last();
                sampleView.Viewer.SetZoom(1.5);
                sampleView.Viewer.GoToPage(0);
                await Settle(1500);
                var invoice = sampleView.Document.GetPageObjects(0).First(o => o.IsText && o.Text.StartsWith("Invoice"));
                sampleView.Edits.OpenEditor(invoice);
                await Settle(400);
                if (FindDescendant<TextBox>(sampleView.Edits) is { } invoiceEditor) invoiceEditor.Text = "Invoice number: INV-10599 (paid)";
                await Settle(400);
                Snap(window, "20-real-world-editor");
                sampleView.Edits.CommitEditor();
                await Settle(1800);
                Snap(window, "21-real-world-edited");
                sampleView.Document.Save(Path.Combine(outputDirectory, "uitest-edit-sample.pdf"));
                log.AppendLine("saved uitest-edit-sample.pdf");
            }
            else
            {
                failures++;
                log.AppendLine("FAIL tests/edit-sample.pdf missing");
            }

            // Home again, with documents open: thumbnails, list layout and dark theme.
            window.ShowHome();
            await Settle(1800);
            Snap(window, "22-home-with-tabs");
            window.Home.Scroller.ScrollToEnd();
            await Settle(500);
            Snap(window, "25-home-recent-cards");
            window.Home.Scroller.ScrollToTop();
            window.Home.SetLayout(true);
            await Settle(600);
            Snap(window, "23-home-list");
            ThemeManager.Apply(true);
            await Settle(900);
            Snap(window, "24-home-list-dark");
            ThemeManager.Apply(false);
            window.Home.SetLayout(false);

            // Leave the document clean so closing the window does not prompt.
            doc.Save(Path.Combine(outputDirectory, "uitest-result.pdf"));
            log.AppendLine($"saved uitest-result.pdf, pages={doc.PageCount}");
        }
        catch (Exception ex)
        {
            failures++;
            log.AppendLine("FAIL " + ex);
        }

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "uitest.log"), log.ToString());
        return failures;
    }

    private static async Task Settle(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until) await Task.Delay(50);
    }

    private static SavedSignature MakeSignature(string name)
    {
        var family = Fonts.SystemFontFamilies.Any(f => f.Source == "Segoe Script") ? "Segoe Script" : "Segoe UI";
        var text = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(family), 72, Brushes.Black, 1.0);
        var figures = GeometryData.Flatten(text.BuildGeometry(new Point()), 0.2);
        var bounds = GeometryData.Bounds(figures);
        figures = GeometryData.Translate(figures, -bounds.X, -bounds.Y);
        return new SavedSignature { Data = GeometryData.ToMarkup(figures), Width = bounds.Width, Height = bounds.Height };
    }

    private static void WritePicture(string path)
    {
        const int width = 320, height = 200;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var sun = (x - 230) * (x - 230) + (y - 60) * (y - 60) < 900;
            var hill = y > 140 - 30 * Math.Sin(x / 45.0);
            (pixels[i + 2], pixels[i + 1], pixels[i]) = sun ? ((byte)255, (byte)196, (byte)40)
                : hill ? ((byte)60, (byte)(150 + y / 4), (byte)80)
                : ((byte)(120 + y / 3), (byte)(180 + y / 5), (byte)250);
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(Application.Current.TryFindResource("Brush.Canvas") as Brush, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveWindow(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        // Include the content's own margin, or dialogs lose their right and bottom edges (where the buttons are).
        var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }
}
