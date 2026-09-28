using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PDFPlus.Core;
using PDFPlus.Services;

namespace PDFPlus.Views;

/// <summary>
/// The text and rich text half of the window: opening, saving and the formatting toolbar. PDFs are handled in
/// MainWindow.xaml.cs; the two share the tab strip, the title bar and the More menu.
/// </summary>
public partial class MainWindow
{
    private const string TextExtensions = "*.txt;*.md;*.markdown;*.log;*.csv;*.tsv;*.ini;*.json;*.xml;*.rtf;*.docx";

    internal const string TextFilter =
        "Documents|" + TextExtensions + "|" +
        "Word documents (*.docx)|*.docx|Rich text (*.rtf)|*.rtf|Plain text (*.txt)|*.txt|All files (*.*)|*.*";

    /// <summary>What the Open dialog offers now that PDFPlus is not only a PDF viewer.</summary>
    internal const string AllDocumentsFilter =
        "All documents|*.pdf;" + TextExtensions + "|" +
        "PDF files (*.pdf)|*.pdf|" +
        "Word documents (*.docx)|*.docx|" +
        "Text and rich text|" + TextExtensions + "|" +
        "All files (*.*)|*.*";

    private static readonly double[] FontSizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 60, 72];

    private bool _syncingTextToolbar;

    /// <summary>Word documents already warned about, so overwriting nags once rather than on every save.</summary>
    private readonly HashSet<string> _warnedAboutDocx = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"a, b and c", for listing what a document uses that PDFPlus won't keep.</summary>
    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    // ---------------------------------------------------------------- opening

    private async Task OpenTextFileAsync(string path)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.AppStarting;
            var document = await TextDocument.OpenAsync(path);
            Mouse.OverrideCursor = null;
            AddTextTab(document);
            AppSettings.Current.AddRecent(path);
            RefreshRecent();
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Dialogs.Error(this, $"Couldn't open {Path.GetFileName(path)}", ex.Message);
        }
    }

    internal void NewTextDocument() => AddTextTab(TextDocument.Empty());

    private void AddTextTab(TextDocument document)
    {
        var editor = new TextView(document) { Visibility = Visibility.Collapsed };
        var tab = new DocumentTab(editor);

        // Worth saying once, quietly, above the document rather than in front of it.
        if (document.Unsupported.Count > 0)
            editor.ShowNotice($"This document uses {Join(document.Unsupported)}. PDFPlus doesn't keep those, " +
                              "so they'd be lost if you save over the original.");
        editor.StatusChanged += (_, _) =>
        {
            tab.Refresh();
            if (_active == tab) UpdateChrome();
        };
        editor.SaveRequested += (_, _) => Save(tab, saveAs: false);

        DocumentHost.Children.Add(editor);
        _tabs.Add(tab);
        SelectTab(tab);
    }

    // ---------------------------------------------------------------- saving

    private bool SaveText(DocumentTab tab, TextView editor, bool saveAs)
    {
        var document = editor.Document;
        var path = document.FilePath;
        if (saveAs || path == null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = TextFilter,
                FileName = document.Title,
                DefaultExt = document.Format == TextFormat.Rtf ? ".rtf" : ".txt",
                AddExtension = true,
            };
            if (path != null) dialog.InitialDirectory = Path.GetDirectoryName(path);
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        var target = TextDocument.FormatOf(path);

        // Saving a rich document under a .txt name drops everything but the words, so say so first.
        if (TextDocument.IsRich(document.Format) && target == TextFormat.Plain &&
            Dialogs.Ask(this, "Save as plain text?",
                "Bold, fonts, colours, lists, tables and pictures aren't part of a plain text file and won't be " +
                "kept. Save with a .docx or .rtf name to keep them.",
                "Save as plain text", null) != AskResult.Primary)
            return false;

        // Overwriting a Word document rebuilds it from what PDFPlus understood, so confirm the first time.
        if (target == TextFormat.Docx && document.Unsupported.Count > 0 && _warnedAboutDocx.Add(path) &&
            Dialogs.Ask(this, "Overwrite the Word document?",
                $"This document uses {Join(document.Unsupported)}. PDFPlus can't keep that, so saving over it " +
                "will lose it. Save under a new name to keep the original.",
                "Overwrite", "Save as…") is var answer && answer != AskResult.Primary)
        {
            return answer == AskResult.Secondary && SaveText(tab, editor, saveAs: true);
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            // The page layout pushes blocks down with margins; the file must not be given those.
            editor.WithTrueLayout(() => document.Save(path));
            AppSettings.Current.AddRecent(path);
            RefreshRecent();
            UpdateChrome();
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Couldn't save", ex.Message);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // ---------------------------------------------------------------- toolbar state

    private void UpdateTextChrome(TextView editor)
    {
        _syncingTextToolbar = true;
        try
        {
            if (FontFamilyBox.Items.Count == 0) FillFontPickers();

            PageViewButton.IsChecked = editor.IsPageView;

            // Nothing can be typed or restyled while the paginated view is showing.
            var formatting = editor.SupportsFormatting && !editor.IsPageView;
            var hint = formatting ? null : "Plain text files can't keep formatting. Save as .rtf to add it.";
            foreach (var control in new Control[]
                     {
                         FontFamilyBox, FontSizeBox, BoldButton, ItalicButton, UnderlineButton, TextColorButton,
                         AlignLeftButton, AlignCenterButton, AlignRightButton, BulletsButton, NumberingButton,
                     })
            {
                control.IsEnabled = formatting;
                control.ToolTip = hint ?? control.ToolTip;
            }

            TextUndoButton.IsEnabled = editor.CanUndo;
            TextRedoButton.IsEnabled = editor.CanRedo;
            TextZoomButton.Content = $"{Math.Round(editor.Zoom * 100)}%";
            TextStatus.Text = editor.StatusText();

            if (!formatting) return;

            BoldButton.IsChecked = editor.SelectionHas(TextElement.FontWeightProperty, FontWeights.Bold);
            ItalicButton.IsChecked = editor.SelectionHas(TextElement.FontStyleProperty, FontStyles.Italic);
            UnderlineButton.IsChecked = editor.SelectionValue(Inline.TextDecorationsProperty) is TextDecorationCollection { Count: > 0 };

            var alignment = editor.SelectionValue(Block.TextAlignmentProperty) as TextAlignment?;
            AlignLeftButton.IsChecked = alignment is null or TextAlignment.Left;
            AlignCenterButton.IsChecked = alignment == TextAlignment.Center;
            AlignRightButton.IsChecked = alignment == TextAlignment.Right;

            var marker = editor.CurrentListMarker;
            BulletsButton.IsChecked = marker == TextMarkerStyle.Disc;
            NumberingButton.IsChecked = marker == TextMarkerStyle.Decimal;

            if (editor.SelectionValue(TextElement.FontFamilyProperty) is FontFamily family) FontFamilyBox.Text = family.Source;
            if (editor.SelectionValue(TextElement.FontSizeProperty) is double size) FontSizeBox.Text = $"{Math.Round(size):0}";
            if (editor.SelectionValue(TextElement.ForegroundProperty) is SolidColorBrush brush) TextColorSwatch.Background = brush;
        }
        finally
        {
            _syncingTextToolbar = false;
        }
    }

    private void FillFontPickers()
    {
        foreach (var family in Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            FontFamilyBox.Items.Add(family);
        foreach (var size in FontSizes) FontSizeBox.Items.Add($"{size:0}");
    }

    // ---------------------------------------------------------------- toolbar commands

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTextToolbar || ActiveText is not { } editor) return;
        if (FontFamilyBox.SelectedItem is string name) editor.ApplyFormat(TextElement.FontFamilyProperty, new FontFamily(name));
    }

    private void OnFontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTextToolbar || ActiveText is not { } editor) return;
        if (FontSizeBox.SelectedItem is string text && double.TryParse(text, out var size))
            editor.ApplyFormat(TextElement.FontSizeProperty, size);
    }

    private void OnBoldClick(object sender, RoutedEventArgs e) => WithEditor(editor => editor.ToggleBold());
    private void OnItalicClick(object sender, RoutedEventArgs e) => WithEditor(editor => editor.ToggleItalic());
    private void OnUnderlineClick(object sender, RoutedEventArgs e) => WithEditor(editor => editor.ToggleUnderline());
    private void OnAlignLeftClick(object sender, RoutedEventArgs e) => WithEditor(editor => editor.SetAlignment(TextAlignment.Left));
    private void OnAlignCenterClick(object sender, RoutedEventArgs e) => WithEditor(editor => editor.SetAlignment(TextAlignment.Center));
    private void OnAlignRightClick(object sender, RoutedEventArgs e) => WithEditor(editor => editor.SetAlignment(TextAlignment.Right));
    private void OnBulletsClick(object sender, RoutedEventArgs e) => WithEditor(editor => editor.ToggleList(TextMarkerStyle.Disc));
    private void OnNumberingClick(object sender, RoutedEventArgs e) => WithEditor(editor => editor.ToggleList(TextMarkerStyle.Decimal));

    private void OnTextZoomResetClick(object sender, RoutedEventArgs e) => WithEditor(editor => editor.ResetZoom());

    private void OnPageViewClick(object sender, RoutedEventArgs e) =>
        WithEditor(editor => editor.SetPageView(!editor.IsPageView));

    private void OnTextColorClick(object sender, RoutedEventArgs e)
    {
        if (ActiveText is not { } editor) return;
        var menu = new ContextMenu { PlacementTarget = TextColorButton, Placement = PlacementMode.Bottom };
        foreach (var (name, value) in new (string, string)[]
                 {
                     ("Default", "#1C1D22"), ("Grey", "#6B6F7A"), ("Red", "#C42B1C"), ("Orange", "#C26A00"),
                     ("Green", "#0F7B0F"), ("Blue", "#0F5FBF"), ("Purple", "#6B2FA0"),
                 })
        {
            var colour = (Color)ColorConverter.ConvertFromString(value);
            menu.Items.Add(DocumentView.MenuItemFor(name, null, () =>
            {
                editor.ApplyFormat(TextElement.ForegroundProperty, new SolidColorBrush(colour));
                UpdateChrome();
            }));
        }
        menu.IsOpen = true;
    }

    private void WithEditor(Action<TextView> action)
    {
        if (ActiveText is not { } editor) return;
        action(editor);
        UpdateChrome();
    }
}
