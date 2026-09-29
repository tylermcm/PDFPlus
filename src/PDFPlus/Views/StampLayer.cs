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
    private const double HandleSize = 11;
    private const double RotateArm = 26;
    private static readonly Color[] InkColors = [Colors.Black, Color.FromRgb(0x1D, 0x4E, 0xD8), Color.FromRgb(0xC6, 0x28, 0x28)];

    private sealed class Live
    {
        public StampKind Kind;
        public int Page;
        public Point Position;      // display points, top-left of the content
        public double Size;         // text: font size (pt); graphics: width (pt)
        public Size Box;            // text box in display points; empty means it grows with the text
        public double Angle;        // degrees, clockwise on screen
        public FontLook? Look;      // font copied from nearby page text, when one was found
        public PageObjectInfo? Style;
        public Color Color;
        public List<PathFigureData>? Figures;
        public Size GeometrySize;
        public double StrokeLocal;  // stroke width in geometry units; 0 = filled
        public Grid Root = null!;
        public Grid? Holder;
        public TextBox? Editor;
        public Canvas? Handles;
        public TextBlock? FontLabel;
        public Canvas? Art;
        public WpfPath? Shape;
    }

    private PdfView? _view;
    private PdfDocument? _document;
    private Live? _live;
    private UIElement? _dragHandle;
    private int _resizeCorner;
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
        FocusEditor(live);
    }

    /// <summary>
    /// Starts a text box. An empty <paramref name="displayBox"/> grows with what's typed; a dragged size wraps inside it.
    /// <paramref name="style"/> is page text to copy the font from, or null for the default.
    /// </summary>
    public void PlaceText(PageHit hit, Size displayBox, PageObjectInfo? style)
    {
        if (_view == null || _document == null) return;
        Commit();

        var live = new Live
        {
            Kind = StampKind.Text,
            Page = hit.PageIndex,
            Color = ColorFromSettings(),
            Box = displayBox,
            Style = style,
            Look = style?.Font,
            Size = style is { FontSize: >= 4 and <= 96 } ? style.FontSize : Math.Clamp(AppSettings.Current.TextSize, 4, 96),
        };
        live.Position = displayBox.IsEmpty ? new Point(hit.Display.X, hit.Display.Y - live.Size * 0.7) : hit.Display;
        BuildText(live, "");

        _live = live;
        Children.Add(live.Root);
        Reposition();
        FocusEditor(live);
    }

    private void FocusEditor(Live live)
    {
        if (live.Editor is not { } editor) return;
        Dispatcher.BeginInvoke(() =>
        {
            editor.Focus();
            editor.CaretIndex = editor.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
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

    /// <summary>Test hook: rotates the live item the way dragging the round handle does.</summary>
    internal void SetLiveAngle(double degrees)
    {
        if (_live is not { } live || live.Editor == null) return;
        live.Angle = degrees;
        Reposition();
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
        var scale = _view.Scale;
        var runs = new List<TextRun>();
        var baseline = editor.FontFamily.Baseline * editor.FontSize;
        // Positions come from the editor itself, so wrapped lines land where they look; rotation is applied below.
        for (var line = 0; line < editor.LineCount; line++)
        {
            var text = editor.GetLineText(line).TrimEnd('\r', '\n');
            if (text.Trim().Length == 0) continue;
            var start = editor.GetCharacterIndexFromLineIndex(line);
            var charRect = editor.GetRectFromCharacterIndex(start);
            if (double.IsInfinity(charRect.X) || double.IsInfinity(charRect.Y)) continue;
            runs.Add(new TextRun(text, live.Position.X + charRect.X / scale, live.Position.Y + (charRect.Y + baseline) / scale));
        }
        if (runs.Count == 0) return;

        var (width, height) = BoxDip(live);
        var center = new Point(live.Position.X + width / scale / 2, live.Position.Y + height / scale / 2);
        _document.AddStyledText(live.Page, runs, live.Size, live.Color, RotationAbout(center, live.Angle), live.Style);
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
            if (live.Box.IsEmpty)
            {
                editor.Width = double.NaN;
                editor.Height = double.NaN;
            }
            else
            {
                editor.Width = Math.Max(24, live.Box.Width * scale);
                editor.Height = Math.Max(editor.FontSize * 1.3, live.Box.Height * scale);
            }
            UpdateFontLabel(live);
            PositionHandles(live);
            ApplyRotation(live);
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

        if (live.Editor != null)
        {
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0), FontSize = 11.5 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            live.FontLabel = label;
            buttons.Children.Add(label);
        }

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
        if (live.Editor != null)
        {
            live.Handles = BuildHandles(live);
            holder.Children.Add(live.Handles);
        }
        live.Holder = holder;
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
            TextWrapping = live.Box.IsEmpty ? TextWrapping.NoWrap : TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 30,
            FontFamily = new FontFamily(live.Look?.Family ?? "Arial"),
            FontWeight = FontWeight.FromOpenTypeWeight(Math.Clamp(live.Look?.Weight ?? 400, 1, 999)),
            FontStyle = live.Look?.Italic == true ? FontStyles.Italic : FontStyles.Normal,
            VerticalContentAlignment = VerticalAlignment.Top,
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

    // ---------------------------------------------------------------- text box handles

    private (double Width, double Height) BoxDip(Live live)
    {
        var editor = live.Editor!;
        editor.UpdateLayout();
        var scale = _view!.Scale;
        var width = live.Box.IsEmpty ? editor.ActualWidth : live.Box.Width * scale;
        var height = live.Box.IsEmpty ? editor.ActualHeight : live.Box.Height * scale;
        return (Math.Max(16, width), Math.Max(12, height));
    }

    private Canvas BuildHandles(Live live)
    {
        var canvas = new Canvas();
        var stem = new WpfPath { StrokeThickness = 1.5, IsHitTestVisible = false };
        stem.SetResourceReference(WpfPath.StrokeProperty, "Brush.Accent");
        canvas.Children.Add(stem);
        for (var corner = 0; corner < 4; corner++)
        {
            var dot = HandleDot(corner is 0 or 2 ? Cursors.SizeNWSE : Cursors.SizeNESW);
            HookCorner(dot, corner);
            canvas.Children.Add(dot);
        }
        var spin = HandleDot(Cursors.Hand);
        spin.ToolTip = "Drag to rotate (hold Shift for 15 degree steps)";
        HookRotate(spin);
        canvas.Children.Add(spin);
        return canvas;
    }

    private static System.Windows.Shapes.Ellipse HandleDot(Cursor cursor)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = HandleSize,
            Height = HandleSize,
            Fill = Brushes.White,
            StrokeThickness = 1.5,
            Cursor = cursor,
        };
        dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.Accent");
        return dot;
    }

    private void PositionHandles(Live live)
    {
        if (live.Handles is not { } canvas || live.Editor == null) return;
        var (width, height) = BoxDip(live);
        var half = HandleSize / 2;
        Point[] corners = [new(0, 0), new(width, 0), new(width, height), new(0, height)];
        for (var i = 0; i < corners.Length; i++)
        {
            var dot = canvas.Children[i + 1];
            SetLeft((UIElement)dot, corners[i].X - half);
            SetTop((UIElement)dot, corners[i].Y - half);
        }
        var spin = (UIElement)canvas.Children[5];
        SetLeft(spin, width / 2 - half);
        SetTop(spin, height + RotateArm - half);
        // Below the box: the item's toolbar sits above it.
        ((WpfPath)canvas.Children[0]).Data = new LineGeometry(new Point(width / 2, height), new Point(width / 2, height + RotateArm));
    }

    private void ApplyRotation(Live live)
    {
        if (live.Holder is not { } holder || live.Editor == null) return;
        if (Math.Abs(live.Angle) < 0.01)
        {
            holder.RenderTransform = Transform.Identity;
            return;
        }
        var (width, height) = BoxDip(live);
        holder.RenderTransform = new RotateTransform(live.Angle, width / 2, height / 2);
    }

    private void HookCorner(UIElement dot, int corner)
    {
        dot.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_live?.Editor == null) return;
            _dragHandle = dot;
            _resizeCorner = corner;
            dot.CaptureMouse();
            e.Handled = true;
        };
        dot.MouseMove += (_, e) =>
        {
            if (_dragHandle == dot && dot.IsMouseCaptured && _live is { } live) ResizeBox(live, e.GetPosition(this));
        };
        dot.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_dragHandle != dot) return;
            _dragHandle = null;
            dot.ReleaseMouseCapture();
            e.Handled = true;
        };
    }

    private void HookRotate(UIElement dot)
    {
        dot.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_live?.Editor == null) return;
            _dragHandle = dot;
            dot.CaptureMouse();
            e.Handled = true;
        };
        dot.MouseMove += (_, e) =>
        {
            if (_dragHandle != dot || !dot.IsMouseCaptured || _live is not { } live || _view == null) return;
            var (width, height) = BoxDip(live);
            var scale = _view.Scale;
            var center = new Point(live.Position.X + width / scale / 2, live.Position.Y + height / scale / 2);
            var mouse = DisplayPoint(live.Page, e.GetPosition(this));
            var angle = Math.Atan2(mouse.Y - center.Y, mouse.X - center.X) * 180 / Math.PI - 90;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) angle = Math.Round(angle / 15) * 15;
            live.Angle = (angle + 540) % 360 - 180;
            Reposition();
        };
        dot.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_dragHandle != dot) return;
            _dragHandle = null;
            dot.ReleaseMouseCapture();
            e.Handled = true;
        };
    }

    /// <summary>Drags one corner; the opposite corner stays where it is, even when the box is rotated.</summary>
    private void ResizeBox(Live live, Point layerPoint)
    {
        if (_view == null || live.Editor == null) return;
        var scale = _view.Scale;
        var (widthDip, heightDip) = BoxDip(live);
        var box = new Size(widthDip / scale, heightDip / scale);
        var center = new Point(live.Position.X + box.Width / 2, live.Position.Y + box.Height / 2);
        var signX = _resizeCorner is 1 or 2 ? 1 : -1;
        var signY = _resizeCorner is 2 or 3 ? 1 : -1;

        var anchor = Rotate(center, live.Angle, new Vector(-signX * box.Width / 2, -signY * box.Height / 2));
        var local = Unrotate(DisplayPoint(live.Page, layerPoint) - anchor, live.Angle);
        var width = Math.Max(24, Math.Abs(local.X));
        var height = Math.Max(live.Size * 1.3, Math.Abs(local.Y));
        var moved = Rotate(anchor, live.Angle, new Vector(signX * width / 2, signY * height / 2));

        live.Box = new Size(width, height);
        live.Position = new Point(moved.X - width / 2, moved.Y - height / 2);
        Reposition();
    }

    private static Point Rotate(Point origin, double angle, Vector offset)
    {
        var radians = angle * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return new Point(origin.X + offset.X * cos - offset.Y * sin, origin.Y + offset.X * sin + offset.Y * cos);
    }

    private static Vector Unrotate(Vector offset, double angle)
    {
        var radians = -angle * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return new Vector(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos);
    }

    private Point DisplayPoint(int page, Point layerPoint)
    {
        var viewport = TranslatePoint(layerPoint, _view);
        var rect = _view!.PageRect(page);
        return new Point((viewport.X - rect.X) / _view.Scale, (viewport.Y - rect.Y) / _view.Scale);
    }

    /// <summary>Rotation around a point, in display space, to match what the box looks like on screen.</summary>
    private static Affine RotationAbout(Point center, double angle)
    {
        if (Math.Abs(angle) < 0.01) return Affine.Identity;
        var radians = angle * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return Affine.Translation(-center.X, -center.Y)
            .Then(new Affine(cos, sin, -sin, cos, 0, 0))
            .Then(Affine.Translation(center.X, center.Y));
    }

    private static void UpdateFontLabel(Live live)
    {
        if (live.FontLabel is not { } label) return;
        var look = live.Look;
        var name = look?.Family ?? "Arial";
        if (look?.Bold == true) name += " Bold";
        if (look?.Italic == true) name += " Italic";
        label.Text = $"{name}  {live.Size:0.#} pt";
        label.ToolTip = look != null
            ? "Font copied from the text you clicked next to"
            : "No text nearby to copy a font from, so this uses the default";
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
