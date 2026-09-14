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
            var window = new MainWindow { ShowActivated = false };
            window.WindowState = WindowState.Normal;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000;
            window.Top = 0;
            window.Width = 1400;
            window.Height = 900;
            window.Show();
            await Settle(700);
            Snap(window, "01-welcome");

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
        var width = content.ActualWidth;
        var height = content.ActualHeight;
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
