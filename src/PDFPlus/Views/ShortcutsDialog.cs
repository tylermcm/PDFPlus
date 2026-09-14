using System.Windows;
using System.Windows.Controls;

namespace PDFPlus.Views;

/// <summary>Keyboard shortcuts as grouped rows with key caps, in two columns that scroll on small screens.</summary>
internal static class ShortcutsDialog
{
    // Keys: "+" joins keys pressed together, " / " separates alternatives. Plus, Minus and Wheel get friendly labels.
    private static readonly (string Title, (string Keys, string Action)[] Rows)[] LeftColumn =
    [
        ("Files & tabs",
        [
            ("Ctrl+O", "Open a PDF"),
            ("Ctrl+S", "Save"),
            ("Ctrl+Shift+S", "Save as"),
            ("Ctrl+P", "Print"),
            ("Ctrl+W", "Close tab"),
            ("Ctrl+Tab", "Next tab"),
            ("Ctrl+Shift+Tab", "Previous tab"),
        ]),
        ("View",
        [
            ("Ctrl+Plus", "Zoom in"),
            ("Ctrl+Minus", "Zoom out"),
            ("Ctrl+Wheel", "Zoom where the mouse is"),
            ("Ctrl+0", "Fit page"),
            ("Ctrl+1", "Actual size"),
            ("Ctrl+2", "Fit width"),
            ("F4", "Show or hide the sidebar"),
        ]),
    ];

    private static readonly (string Title, (string Keys, string Action)[] Rows)[] RightColumn =
    [
        ("Find & move around",
        [
            ("Ctrl+F", "Find in document"),
            ("F3 / Shift+F3", "Next / previous match"),
            ("Home / End", "First / last page"),
            ("Space", "Page down"),
        ]),
        ("Tools",
        [
            ("V", "Select text"),
            ("H", "Hand: drag to scroll"),
            ("E", "Edit text & images"),
        ]),
        ("Editing",
        [
            ("Ctrl+Z", "Undo"),
            ("Ctrl+Y", "Redo"),
            ("Enter", "Retype the selected text"),
            ("Del", "Delete the selected item"),
            ("Esc", "Cancel, or finish placing"),
        ]),
        ("Pages sidebar",
        [
            ("Ctrl+Click", "Select several pages"),
            ("Del", "Delete the selected pages"),
        ]),
    ];

    public static void Show(Window? owner) => Create(owner).ShowDialog();

    internal static Window Create(Window? owner)
    {
        var window = Dialogs.CreateWindow(owner, "Keyboard shortcuts");
        window.SizeToContent = SizeToContent.Height;
        window.Width = 740;

        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Keyboard shortcuts", FontSize = 16, FontWeight = FontWeights.SemiBold });
        var intro = new TextBlock
        {
            Text = "They work while a document is open, except when you're typing in a box.",
            Margin = new Thickness(0, 4, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        root.Children.Add(intro);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var right = BuildColumn(RightColumn);
        Grid.SetColumn(right, 2);
        columns.Children.Add(BuildColumn(LeftColumn));
        columns.Children.Add(right);

        root.Children.Add(new ScrollViewer
        {
            Content = columns,
            Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Clamp(SystemParameters.WorkArea.Height * 0.62, 240, 580),
        });

        var ok = Dialogs.MakeButton("OK", true);
        ok.IsDefault = true;
        ok.IsCancel = true;
        ok.Click += (_, _) => window.Close();
        var buttons = Dialogs.Row(ok);
        buttons.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(buttons);

        window.Content = root;
        return window;
    }

    private static StackPanel BuildColumn((string Title, (string Keys, string Action)[] Rows)[] sections)
    {
        var column = new StackPanel();
        foreach (var (title, rows) in sections)
        {
            var header = new TextBlock
            {
                Text = title.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, column.Children.Count == 0 ? 6 : 14, 0, 2),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            column.Children.Add(header);

            foreach (var (keys, action) in rows)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(KeyCombos(keys));
                var text = new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);

                var line = new Border { Child = row, Padding = new Thickness(0, 3, 0, 3), BorderThickness = new Thickness(0, 0, 0, 1) };
                line.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
                column.Children.Add(line);
            }
        }
        return column;
    }

    private static WrapPanel KeyCombos(string spec)
    {
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var alternatives = spec.Split(" / ");
        for (var i = 0; i < alternatives.Length; i++)
        {
            if (i > 0) panel.Children.Add(Joiner("/", 6));
            var keys = alternatives[i].Split('+');
            for (var j = 0; j < keys.Length; j++)
            {
                if (j > 0) panel.Children.Add(Joiner("+", 3));
                panel.Children.Add(KeyCap(keys[j] switch
                {
                    "Plus" => "+",
                    "Minus" => "−",
                    "Wheel" => "Scroll",
                    var key => key,
                }));
            }
        }
        return panel;
    }

    private static Border KeyCap(string label)
    {
        var text = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var cap = new Border
        {
            Child = text,
            MinWidth = 26,
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1, 1, 1, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cap.SetResourceReference(Border.BackgroundProperty, "Brush.Sidebar");
        cap.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return cap;
    }

    private static TextBlock Joiner(string symbol, double spacing)
    {
        var text = new TextBlock { Text = symbol, FontSize = 11, Margin = new Thickness(spacing, 0, spacing, 0), VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        return text;
    }
}
