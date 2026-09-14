using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PDFPlus.Core;

namespace PDFPlus.Views;

public sealed record ImageExportOptions(int[] Pages, double Dpi, ImageExportFormat Format);

public sealed record CompressPreset(string Name, int Dpi, int Quality);

/// <summary>Dialogs for password protection, image export and compression.</summary>
public static class ToolsDialogs
{
    public static PdfProtection? Protect(Window? owner, string fileName)
    {
        PdfProtection? result = null;
        var window = Dialogs.CreateWindow(owner, "Password protect");
        var body = Dialogs.Body("Protect with a password", $"\"{fileName}\" will need this password to open. It's encrypted with AES-256.");

        var password = new PasswordBox { Height = 32, Margin = new Thickness(0, 12, 0, 0) };
        var confirm = new PasswordBox { Height = 32, Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(Label("Password"));
        body.Children.Add(password);
        body.Children.Add(Label("Confirm password"));
        body.Children.Add(confirm);

        body.Children.Add(Label("Anyone who opens the file may"));
        var print = Check("Print", true);
        var copy = Check("Copy text and images", true);
        var edit = Check("Edit, rearrange and annotate pages", true);
        body.Children.Add(print);
        body.Children.Add(copy);
        body.Children.Add(edit);

        var warning = Note("If you forget the password, the file can't be opened. PDFPlus can't recover it.");
        body.Children.Add(warning);
        var error = Note("");
        error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        error.Visibility = Visibility.Collapsed;
        body.Children.Add(error);

        var ok = Dialogs.MakeButton("Protect and save", true);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            string? problem = password.Password.Length == 0 ? "Enter a password."
                : password.Password != confirm.Password ? "The passwords don't match."
                : null;
            if (problem != null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                return;
            }
            result = new PdfProtection(password.Password, "", print.IsChecked == true, copy.IsChecked == true, edit.IsChecked == true);
            window.Close();
        };
        var cancel = Dialogs.MakeButton("Cancel", false);
        cancel.IsCancel = true;
        body.Children.Add(Dialogs.Row(ok, cancel));

        window.Content = body;
        window.Loaded += (_, _) => password.Focus();
        window.ShowDialog();
        return result;
    }

    public static ImageExportOptions? ExportImages(Window? owner, int pageCount, int[] selection)
    {
        ImageExportOptions? result = null;
        var window = Dialogs.CreateWindow(owner, "Export as images");
        var body = Dialogs.Body("Export pages as images", "Each page becomes its own image file.");

        body.Children.Add(Label("Pages"));
        var range = new TextBox
        {
            Height = 32,
            Text = selection.Length > 1 ? PageRanges.Format(selection) : $"1-{pageCount}",
        };
        body.Children.Add(range);

        var format = ImageExportFormat.Png;
        body.Children.Add(Label("Format"));
        body.Children.Add(ChoiceRow([("PNG (sharp, larger)", ImageExportFormat.Png), ("JPG (photos, smaller)", ImageExportFormat.Jpeg)], format, v => format = v));

        var dpi = 150.0;
        body.Children.Add(Label("Resolution"));
        body.Children.Add(ChoiceRow([("72 dpi (screen)", 72.0), ("150 dpi", 150.0), ("300 dpi (print)", 300.0)], dpi, v => dpi = v));

        var error = Note("");
        error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        error.Visibility = Visibility.Collapsed;
        body.Children.Add(error);

        var ok = Dialogs.MakeButton("Export", true);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            var pages = PageRanges.Parse(range.Text, pageCount);
            if (pages == null)
            {
                error.Text = $"Use page numbers between 1 and {pageCount}, like 1-3, 5.";
                error.Visibility = Visibility.Visible;
                return;
            }
            result = new ImageExportOptions(pages, dpi, format);
            window.Close();
        };
        var cancel = Dialogs.MakeButton("Cancel", false);
        cancel.IsCancel = true;
        body.Children.Add(Dialogs.Row(ok, cancel));

        window.Content = body;
        window.ShowDialog();
        return result;
    }

    public static CompressPreset? Compress(Window? owner)
    {
        CompressPreset? result = null;
        var window = Dialogs.CreateWindow(owner, "Compress");
        var body = Dialogs.Body("Save a smaller copy",
            "Large images are downsampled and re-encoded. Text and drawings stay sharp. Your open document isn't changed.");

        CompressPreset[] presets =
        [
            new("High quality: 150 dpi images", 150, 85),
            new("Balanced: 110 dpi images", 110, 75),
            new("Smallest file: 72 dpi images", 72, 60),
        ];
        var chosen = presets[1];
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var preset in presets)
        {
            var toggle = Toggle(preset.Name, preset == chosen);
            toggle.HorizontalContentAlignment = HorizontalAlignment.Left;
            toggle.Margin = new Thickness(0, 0, 0, 4);
            toggle.Height = 38;
            toggle.Click += (_, _) =>
            {
                chosen = preset;
                foreach (var other in list.Children.OfType<ToggleButton>()) other.IsChecked = other == toggle;
            };
            list.Children.Add(toggle);
        }
        body.Children.Add(list);

        var ok = Dialogs.MakeButton("Choose where to save…", true);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            result = chosen;
            window.Close();
        };
        var cancel = Dialogs.MakeButton("Cancel", false);
        cancel.IsCancel = true;
        body.Children.Add(Dialogs.Row(ok, cancel));

        window.Content = body;
        window.ShowDialog();
        return result;
    }

    // ---------------------------------------------------------------- building blocks

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 6),
    };

    private static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 12, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        return note;
    }

    private static CheckBox Check(string text, bool isChecked)
    {
        var box = new CheckBox { Content = text, IsChecked = isChecked, Margin = new Thickness(0, 3, 0, 3) };
        box.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        return box;
    }

    private static ToggleButton Toggle(string text, bool isChecked) => new()
    {
        Content = text,
        IsChecked = isChecked,
        Style = (Style)Application.Current.FindResource("ToolToggle"),
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        FontSize = 13,
        Padding = new Thickness(12, 0, 12, 0),
    };

    private static WrapPanel ChoiceRow<T>((string Label, T Value)[] options, T selected, Action<T> changed)
    {
        var row = new WrapPanel();
        foreach (var (label, value) in options)
        {
            var toggle = Toggle(label, EqualityComparer<T>.Default.Equals(value, selected));
            toggle.Margin = new Thickness(0, 0, 6, 4);
            toggle.Click += (_, _) =>
            {
                changed(value);
                foreach (var other in row.Children.OfType<ToggleButton>()) other.IsChecked = other == toggle;
            };
            row.Children.Add(toggle);
        }
        return row;
    }
}
