using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Media;
using PDFPlus.Core;
using PDFPlus.Services;

namespace PDFPlus.Views;

/// <summary>Draw or type a signature. The result is stored as vector outlines, never as an image.</summary>
public sealed class SignatureDialog : Window
{
    private static readonly string[] ScriptFonts =
    [
        "Segoe Script", "Ink Free", "Lucida Handwriting", "Brush Script MT", "Freestyle Script",
        "Segoe Print", "Mistral", "Bradley Hand ITC", "Kristen ITC", "Gabriola",
    ];

    private readonly InkCanvas _ink;
    private readonly TextBox _name;
    private readonly StackPanel _fonts;
    private readonly ToggleButton _drawTab;
    private readonly ToggleButton _typeTab;
    private readonly FrameworkElement _drawPanel;
    private readonly FrameworkElement _typePanel;
    private string _fontName;

    public SavedSignature? Result { get; private set; }

    public SignatureDialog(Window? owner)
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Title = "Create signature";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (owner is { IsLoaded: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        ThemeManager.ApplyTitleBar(this);

        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fonts = ScriptFonts.Where(installed.Contains).ToList();
        if (fonts.Count == 0) fonts.Add("Segoe UI");
        _fontName = fonts[0];

        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 20), Width = 560 };

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _drawTab = Tab("Draw", true);
        _typeTab = Tab("Type", false);
        tabs.Children.Add(_drawTab);
        tabs.Children.Add(_typeTab);
        root.Children.Add(tabs);

        // Draw panel: white pad with a signing line.
        var pad = new Grid { Height = 210 };
        pad.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), BorderBrush = new SolidColorBrush(Color.FromRgb(210, 212, 218)), BorderThickness = new Thickness(1) });
        pad.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(28, 0, 28, 52), VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromRgb(200, 202, 208)), IsHitTestVisible = false,
        });
        pad.Children.Add(new TextBlock
        {
            Text = "✕", Foreground = new SolidColorBrush(Color.FromRgb(170, 172, 180)), FontSize = 16,
            Margin = new Thickness(30, 0, 0, 58), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
        });
        _ink = new InkCanvas { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Pen };
        _ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Colors.Black, Width = 2.6, Height = 2.6, FitToCurve = true, IgnorePressure = false,
        };
        pad.Children.Add(_ink);
        var clear = new Button
        {
            Content = "Clear", Style = (Style)Application.Current.FindResource("SecondaryButton"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0), Height = 28, MinWidth = 60,
        };
        clear.Click += (_, _) => _ink.Strokes.Clear();
        pad.Children.Add(clear);
        _drawPanel = pad;
        root.Children.Add(pad);

        // Type panel: name box plus font choices previewing the name.
        var typePanel = new StackPanel { Visibility = Visibility.Collapsed };
        _name = new TextBox { Height = 34, FontSize = 15 };
        typePanel.Children.Add(new TextBlock { Text = "Your name", Margin = new Thickness(0, 0, 0, 6) });
        typePanel.Children.Add(_name);
        _fonts = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var font in fonts.Take(6)) _fonts.Children.Add(FontChoice(font));
        typePanel.Children.Add(_fonts);
        _name.TextChanged += (_, _) => RefreshFontPreviews();
        _typePanel = typePanel;
        root.Children.Add(typePanel);

        _drawTab.Click += (_, _) => SelectTab(draw: true);
        _typeTab.Click += (_, _) => SelectTab(draw: false);

        var note = new TextBlock
        {
            Text = "Signatures are saved on this computer only so you can reuse them.",
            FontSize = 12, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        root.Children.Add(note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "Use signature", Style = (Style)Application.Current.FindResource("AccentButton"), IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("SecondaryButton"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        RefreshFontPreviews();
    }

    private static ToggleButton Tab(string text, bool isChecked) => new()
    {
        Content = text,
        IsChecked = isChecked,
        Style = (Style)Application.Current.FindResource("ToolToggle"),
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        FontSize = 13,
        Padding = new Thickness(14, 0, 14, 0),
        Margin = new Thickness(0, 0, 6, 0),
    };

    private void SelectTab(bool draw)
    {
        _drawTab.IsChecked = draw;
        _typeTab.IsChecked = !draw;
        _drawPanel.Visibility = draw ? Visibility.Visible : Visibility.Collapsed;
        _typePanel.Visibility = draw ? Visibility.Collapsed : Visibility.Visible;
        if (!draw) _name.Focus();
    }

    private ToggleButton FontChoice(string font)
    {
        var button = new ToggleButton
        {
            Tag = font,
            Height = 52,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4),
            Style = (Style)Application.Current.FindResource("ToolToggle"),
            IsChecked = font == _fontName,
            Content = new TextBlock { FontFamily = new FontFamily(font), FontSize = 26, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
        };
        button.Click += (_, _) =>
        {
            _fontName = font;
            foreach (var child in _fonts.Children.OfType<ToggleButton>()) child.IsChecked = (string)child.Tag == font;
        };
        return button;
    }

    private void RefreshFontPreviews()
    {
        var text = string.IsNullOrWhiteSpace(_name.Text) ? "Your Name" : _name.Text;
        foreach (var child in _fonts.Children.OfType<ToggleButton>())
            if (child.Content is TextBlock block) block.Text = text;
    }

    private void Accept()
    {
        Geometry? geometry = null;
        if (_drawTab.IsChecked == true)
        {
            if (_ink.Strokes.Count == 0) return;
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var stroke in _ink.Strokes) group.Children.Add(stroke.GetGeometry(stroke.DrawingAttributes));
            geometry = group;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) return;
            var formatted = new FormattedText(_name.Text.Trim(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(_fontName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 72, Brushes.Black, 1.0);
            geometry = formatted.BuildGeometry(new Point(0, 0));
        }

        var figures = GeometryData.Flatten(geometry, 0.2);
        var bounds = GeometryData.Bounds(figures);
        if (figures.Count == 0 || bounds.IsEmpty || bounds.Width < 1) return;
        figures = GeometryData.Translate(figures, -bounds.X, -bounds.Y);
        Result = new SavedSignature
        {
            Data = GeometryData.ToMarkup(figures),
            Width = bounds.Width,
            Height = Math.Max(1, bounds.Height),
        };
        DialogResult = true;
    }
}
