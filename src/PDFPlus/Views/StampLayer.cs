using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PDFPlus.Controls;
using PDFPlus.Core;
using PDFPlus.Services;
using WpfPath = System.Windows.Shapes.Path;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace PDFPlus.Views;

public enum StampKind { Text, Date, Check, Cross, Signature }

/// <summary>
/// Overlay for the one fill-and-sign item currently being placed. The item stays editable (move,
/// resize, recolor, type) until it is committed, at which point it is written into the PDF page.
/// </summary>
public sealed class StampLayer : Canvas
{
    private const double ToolbarRow = 40;
    private static readonly Color[] InkColors = [Colors.Black, Color.FromRgb(0x1D, 0x4E, 0xD8), Color.FromRgb(0xC6, 0x28, 0x28)];

    private sealed class Live
    {
        public StampKind Kind;
        public int Page;
        public Point Position;      // display points, top-left of the content
        public double Size;         // text: font size (pt); graphics: width (pt)
        public Color Color;
        public List<PathFigureData>? Figures;
        public Size GeometrySize;
        public double StrokeLocal;  // stroke width in geometry units; 0 = filled
        public Grid Root = null!;
        public TextBox? Editor;
        public Canvas? Art;
        public WpfPath? Shape;
    }

    private PdfView? _view;
    private PdfDocument? _document;
    private Live? _live;
    private UIElement? _dragHandle;
    private Point _dragStart;
    private Point _dragOrigin;

    public bool HasLive => _live != null;

    public StampLayer()
    {
        ClipToBounds = true;
    }

    public void Attach(PdfView view, PdfDocument document)
    {
        _view = view;
        _document = document;
        view.ViewChanged += (_, _) => Reposition();
        view.ZoomChanged += (_, _) => Reposition();
        document.PagesChanged += (_, _) => Cancel();
    }

    public void Place(StampKind kind, PageHit hit, SavedSignature? signature)
    {
        if (_view == null || _document == null) return;
        Commit();

        var live = new Live
        {
            Kind = kind,
            Page = hit.PageIndex,
            Color = ColorFromSettings(),
        };
        var pageSize = _document.PageSizes[hit.PageIndex];

        switch (kind)
        {
            case StampKind.Text:
            case StampKind.Date:
                live.Size = Math.Clamp(AppSettings.Current.TextSize, 4, 96);
                live.Position = new Point(hit.Display.X, hit.Display.Y - live.Size * 0.7);
                BuildText(live, kind == StampKind.Date ? DateTime.Now.ToString("d") : "");
                break;
            case StampKind.Check:
                live.Figures = [new PathFigureData([new Point(0.5, 5.6), new Point(3.8, 9), new Point(9.5, 1)], false)];
                live.GeometrySize = new Size(10, 10);
                live.StrokeLocal = 1.5;
                live.Size = 12;
                break;
            case StampKind.Cross:
                live.Figures =
                [
                    new PathFigureData([new Point(1, 1), new Point(9, 9)], false),
                    new PathFigureData([new Point(9, 1), new Point(1, 9)], false),
                ];
                live.GeometrySize = new Size(10, 10);
                live.StrokeLocal = 1.5;
                live.Size = 11;
                break;
            case StampKind.Signature:
                if (signature == null) return;
                live.Figures = GeometryData.Flatten(Geometry.Parse(signature.Data), 0.2);
                live.GeometrySize = new Size(Math.Max(1, signature.Width), Math.Max(1, signature.Height));
                live.Size = Math.Min(160, pageSize.Width * 0.35);
                break;
        }

        if (live.Figures != null)
        {
            var height = live.Size * live.GeometrySize.Height / live.GeometrySize.Width;
            live.Position = new Point(hit.Display.X - live.Size / 2, hit.Display.Y - height / 2);
            BuildArt(live);
        }

        _live = live;
        Children.Add(live.Root);
        Reposition();

        if (live.Editor is { } editor)
        {
            Dispatcher.BeginInvoke(() =>
            {
                editor.Focus();
                editor.CaretIndex = editor.Text.Length;
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>Writes the live item into the PDF.</summary>
    public void Commit()
    {
        if (_live is not { } live || _view == null || _document == null) return;
        _live = null;
        try
        {
            if (live.Editor is { } editor) CommitText(live, editor);
            else if (live.Figures is { } figures)
            {
                var k = live.Size / live.GeometrySize.Width;
                _document.AddPath(live.Page, figures, Affine.Scale(k, k).Then(Affine.Translation(live.Position.X, live.Position.Y)), live.Color, live.StrokeLocal);
            }
        }
        finally
        {
            Retire(live);
        }
    }

    /// <summary>Keeps the committed item on screen a moment so there's no blank flash before the page re-renders.</summary>
    private void Retire(Live live)
    {
        if (live.Editor is { IsKeyboardFocusWithin: true }) _view?.Focus();
        live.Root.IsHitTestVisible = false;
        foreach (var child in live.Root.Children.OfType<FrameworkElement>())
        {
            if (Grid.GetRow(child) == 0) child.Visibility = Visibility.Hidden;
            else if (child is Grid holder)
                foreach (var outline in holder.Children.OfType<WpfRectangle>()) outline.Visibility = Visibility.Hidden;
        }

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Children.Remove(live.Root);
        };
        timer.Start();
    }

    public void Cancel()
    {
        if (_live == null) return;
        Children.Remove(_live.Root);
        _live = null;
    }

    private void CommitText(Live live, TextBox editor)
    {
        if (string.IsNullOrWhiteSpace(editor.Text) || _view == null || _document == null) return;
        editor.UpdateLayout();
        var pageRect = _view.PageRect(live.Page);
        var runs = new List<TextRun>();
        var baseline = editor.FontFamily.Baseline * editor.FontSize;
        for (var line = 0; line < editor.LineCount; line++)
        {
            var text = editor.GetLineText(line).TrimEnd('\r', '\n');
            if (text.Trim().Length == 0) continue;
            var start = editor.GetCharacterIndexFromLineIndex(line);
            var charRect = editor.GetRectFromCharacterIndex(start);
            var topLeft = editor.TranslatePoint(charRect.TopLeft, _view);
            runs.Add(new TextRun(text, (topLeft.X - pageRect.X) / _view.Scale, (topLeft.Y + baseline - pageRect.Y) / _view.Scale));
        }
        _document.AddText(live.Page, runs, live.Size, live.Color);
    }

    private void Reposition()
    {
        if (_live is not { } live || _view == null || _document == null) return;
        if ((uint)live.Page >= (uint)_document.PageCount)
        {
            Cancel();
            return;
        }
        var scale = _view.Scale;
        var viewportPoint = _view.DisplayToViewport(live.Page, live.Position);
        var p = _view.TranslatePoint(viewportPoint, this);
        SetLeft(live.Root, p.X);
        SetTop(live.Root, p.Y - ToolbarRow);

        if (live.Editor is { } editor)
        {
            editor.FontSize = live.Size * scale;
        }
        else if (live.Art is { } art && live.Shape is { } shape)
        {
            var width = live.Size * scale;
            var k = width / live.GeometrySize.Width;
            art.Width = width;
            art.Height = live.GeometrySize.Height * k;
            shape.RenderTransform = new ScaleTransform(k, k);
        }
    }

    // ---------------------------------------------------------------- building the overlay

    private Grid CreateRoot(Live live, FrameworkElement content)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ToolbarRow) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            Margin = new Thickness(-4, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Effect = (System.Windows.Media.Effects.Effect)Application.Current.FindResource("PopupShadow"),
        };
        toolbar.SetResourceReference(Border.BackgroundProperty, "Brush.Popup");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Child = buttons;

        var move = ToolbarButton("", "Drag to move");
        move.Cursor = Cursors.SizeAll;
        HookDrag(move);
        buttons.Children.Add(move);

        var smaller = ToolbarButton("", "Smaller");
        smaller.Click += (_, _) => Resize(1 / 1.15);
        buttons.Children.Add(smaller);

        var bigger = ToolbarButton("", "Bigger");
        bigger.Click += (_, _) => Resize(1.15);
        buttons.Children.Add(bigger);

        var color = ToolbarButton("●", "Change color");
        color.FontFamily = new FontFamily("Segoe UI Symbol");
        color.FontSize = 16;
        color.Foreground = new SolidColorBrush(live.Color);
        color.Click += (_, _) => CycleColor(color);
        buttons.Children.Add(color);

        var done = ToolbarButton("", "Done (click outside)");
        done.Click += (_, _) => Commit();
        buttons.Children.Add(done);

        var delete = ToolbarButton("", "Remove");
        delete.Click += (_, _) => Cancel();
        buttons.Children.Add(delete);

        root.Children.Add(toolbar);

        var holder = new Grid();
        Grid.SetRow(holder, 1);
        holder.Children.Add(content);
        var outline = new WpfRectangle
        {
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
            Margin = new Thickness(-4),
            IsHitTestVisible = false,
        };
        outline.SetResourceReference(WpfRectangle.StrokeProperty, "Brush.Accent");
        holder.Children.Add(outline);
        root.Children.Add(holder);
        return root;
    }

    private static Button ToolbarButton(string glyph, string tip) => new()
    {
        Content = glyph,
        ToolTip = tip,
        Height = 30,
        MinWidth = 30,
        FontSize = 13,
        Padding = new Thickness(6, 0, 6, 0),
        Style = (Style)Application.Current.FindResource("ToolButton"),
    };

    private void BuildText(Live live, string initial)
    {
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        template.VisualTree = host;

        var brush = new SolidColorBrush(live.Color);
        var editor = new TextBox
        {
            Template = template,
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 30,
            FontFamily = new FontFamily("Arial"),
            Foreground = brush,
            CaretBrush = brush,
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Commit();
                _view?.Focus();
                e.Handled = true;
            }
        };
        editor.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            Resize(e.Delta > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
        };
        live.Editor = editor;
        live.Root = CreateRoot(live, editor);
    }

    private void BuildArt(Live live)
    {
        var shape = new WpfPath
        {
            Data = GeometryData.ToGeometry(live.Figures!),
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        ApplyShapeColor(live, shape);
        var art = new Canvas { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        art.Children.Add(shape);
        HookDrag(art);
        art.MouseWheel += (_, e) =>
        {
            Resize(e.Delta > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
        };
        live.Art = art;
        live.Shape = shape;
        live.Root = CreateRoot(live, art);
    }

    private static void ApplyShapeColor(Live live, WpfPath shape)
    {
        var brush = new SolidColorBrush(live.Color);
        if (live.StrokeLocal > 0)
        {
            shape.Stroke = brush;
            shape.StrokeThickness = live.StrokeLocal;
            shape.Fill = null;
        }
        else
        {
            shape.Fill = brush;
            shape.Stroke = null;
        }
    }

    private void HookDrag(UIElement handle)
    {
        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_live == null) return;
            _dragHandle = handle;
            _dragStart = e.GetPosition(this);
            _dragOrigin = _live.Position;
            handle.CaptureMouse();
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (_dragHandle != handle || !handle.IsMouseCaptured || _live == null || _view == null || _document == null) return;
            var delta = (e.GetPosition(this) - _dragStart) / _view.Scale;
            var size = _document.PageSizes[_live.Page];
            _live.Position = new Point(
                Math.Clamp(_dragOrigin.X + delta.X, -10, size.Width - 4),
                Math.Clamp(_dragOrigin.Y + delta.Y, -10, size.Height - 4));
            Reposition();
        };
        handle.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_dragHandle != handle) return;
            _dragHandle = null;
            handle.ReleaseMouseCapture();
            e.Handled = true;
        };
    }

    private void Resize(double factor)
    {
        if (_live is not { } live || _document == null) return;
        if (live.Editor != null)
        {
            live.Size = Math.Clamp(live.Size * factor, 4, 96);
            AppSettings.Current.TextSize = Math.Round(live.Size, 1);
        }
        else
        {
            live.Size = Math.Clamp(live.Size * factor, 4, _document.PageSizes[live.Page].Width);
        }
        Reposition();
    }

    private void CycleColor(Button swatch)
    {
        if (_live is not { } live) return;
        var index = Array.IndexOf(InkColors, live.Color);
        live.Color = InkColors[(index + 1) % InkColors.Length];
        var brush = new SolidColorBrush(live.Color);
        swatch.Foreground = brush;
        if (live.Editor is { } editor)
        {
            editor.Foreground = brush;
            editor.CaretBrush = brush;
        }
        if (live.Shape is { } shape) ApplyShapeColor(live, shape);
        AppSettings.Current.InkColor = live.Color.ToString();
        AppSettings.Current.Save();
    }

    private static Color ColorFromSettings()
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(AppSettings.Current.InkColor);
            color.A = 255;
            return InkColors.FirstOrDefault(c => c.R == color.R && c.G == color.G && c.B == color.B, Colors.Black);
        }
        catch
        {
            return Colors.Black;
        }
    }
}
