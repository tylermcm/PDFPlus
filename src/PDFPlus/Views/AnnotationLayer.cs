using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PDFPlus.Controls;
using PDFPlus.Core;

namespace PDFPlus.Views;

public enum AnnotationTool { None, Highlight, Underline, Strikeout, Pen, Rectangle, Ellipse, Arrow, Note }

/// <summary>
/// Overlay that draws annotations with the active tool (live preview while dragging), selects existing
/// annotations, and hosts the sticky-note editor. Everything is written to the PDF through PdfDocument.
/// </summary>
public sealed class AnnotationLayer : Canvas
{
    private const double MinInkStep = 0.75;

    private PdfView? _view;
    private PdfDocument? _document;
    private AnnotationTool _tool;
    private readonly Dictionary<int, List<AnnotationInfo>> _cache = new();

    private bool _dragging;
    private int _dragPage;
    private Point _dragStart;
    private Point _dragEnd;
    private int _startChar = -1;
    private int _endChar = -1;
    private readonly List<Point> _ink = new();

    private AnnotationInfo? _selected;
    private FrameworkElement? _floating;
    private int _floatingPage;
    private Point _floatingAnchor;

    public Color Color { get; set; } = Color.FromRgb(0xFF, 0xD4, 0x00);
    public double StrokeWidth { get; set; } = 2;
    public AnnotationInfo? Selected => _selected;
    public bool IsEditingNote => _floating?.Tag as string == "note";

    public event EventHandler? SelectionChanged;

    public AnnotationLayer()
    {
        ClipToBounds = true;
    }

    public void Attach(PdfView view, PdfDocument document)
    {
        _view = view;
        _document = document;
        view.ViewChanged += (_, _) => Refresh();
        view.ZoomChanged += (_, _) => Refresh();
        document.PageContentChanged += (_, index) => _cache.Remove(index);
        document.PagesChanged += (_, _) =>
        {
            _cache.Clear();
            CloseFloating();
            SetSelected(null);
        };
    }

    public AnnotationTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            CancelDrag();
            CloseFloating();
            SetSelected(null);
            Background = value == AnnotationTool.None ? null : Brushes.Transparent;
            Cursor = value switch
            {
                AnnotationTool.None => null,
                AnnotationTool.Pen => Cursors.Pen,
                AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.Strikeout => Cursors.IBeam,
                AnnotationTool.Note => Cursors.Hand,
                _ => Cursors.Cross,
            };
        }
    }

    private bool IsMarkupTool => _tool is AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.Strikeout;

    private void Refresh()
    {
        PositionFloating();
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- coordinates

    private PageHit? Hit(Point layerPoint) => _view?.HitTest(TranslatePoint(layerPoint, _view));

    private Point ToLayer(int page, Point display) => _view!.TranslatePoint(_view.DisplayToViewport(page, display), this);

    private Point DisplayOnPage(int page, Point layerPoint)
    {
        var viewport = TranslatePoint(layerPoint, _view);
        var rect = _view!.PageRect(page);
        var size = _document!.PageSizes[page];
        return new Point(
            Math.Clamp((viewport.X - rect.X) / _view.Scale, 0, size.Width),
            Math.Clamp((viewport.Y - rect.Y) / _view.Scale, 0, size.Height));
    }

    private Rect LayerRect(int page, Rect pdfRect)
    {
        var display = _document!.GetPageToDisplay(page).TransformBounds(pdfRect);
        return new Rect(ToLayer(page, display.TopLeft), ToLayer(page, display.BottomRight));
    }

    // ---------------------------------------------------------------- drawing with a tool

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_tool == AnnotationTool.None || _document == null || _view == null || e.Handled) return;
        var position = e.GetPosition(this);
        if (_floating != null)
        {
            CloseFloating(commitNote: true);
            e.Handled = true;
            return;
        }
        if (Hit(position) is not { } hit) return;

        if (_tool == AnnotationTool.Note)
        {
            OpenNoteEditor(hit.PageIndex, hit.Display, null);
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragPage = hit.PageIndex;
        _dragStart = _dragEnd = hit.Display;
        _ink.Clear();
        _ink.Add(hit.Display);
        if (IsMarkupTool)
            _startChar = _endChar = _document.CharIndexAt(hit.PageIndex, _view.DisplayToPage(hit.PageIndex, hit.Display), 8);
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _document == null || _view == null) return;
        var display = DisplayOnPage(_dragPage, e.GetPosition(this));
        _dragEnd = display;
        if (_tool == AnnotationTool.Pen)
        {
            if ((_ink[^1] - display).Length >= MinInkStep) _ink.Add(display);
        }
        else if (IsMarkupTool)
        {
            var index = _document.CharIndexAt(_dragPage, _view.DisplayToPage(_dragPage, display), 30);
            if (index >= 0)
            {
                if (_startChar < 0) _startChar = index;
                _endChar = index;
            }
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging || _document == null) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;

        var page = _dragPage;
        switch (_tool)
        {
            case AnnotationTool.Highlight:
            case AnnotationTool.Underline:
            case AnnotationTool.Strikeout:
                var rects = MarkupRects();
                if (rects.Length > 0)
                {
                    var kind = _tool switch
                    {
                        AnnotationTool.Highlight => MarkupKind.Highlight,
                        AnnotationTool.Underline => MarkupKind.Underline,
                        _ => MarkupKind.Strikeout,
                    };
                    _document.AddTextMarkup(page, kind, rects, Color);
                }
                break;
            case AnnotationTool.Pen:
                _document.AddInk(page, [_ink.ToArray()], Color, StrokeWidth);
                break;
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
                var rect = new Rect(_dragStart, _dragEnd);
                if (rect.Width >= 3 && rect.Height >= 3)
                    _document.AddShape(page, _tool == AnnotationTool.Rectangle ? ShapeKind.Rectangle : ShapeKind.Ellipse, rect, Color, StrokeWidth);
                break;
            case AnnotationTool.Arrow:
                if ((_dragEnd - _dragStart).Length >= 5) _document.AddArrow(page, _dragStart, _dragEnd, Color, StrokeWidth);
                break;
        }
        CancelDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) CancelDrag();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_tool == AnnotationTool.None || _view == null || e.OriginalSource is TextBox) return;
        _view.ScrollByWheel(e.Delta, TranslatePoint(e.GetPosition(this), _view));
        e.Handled = true;
    }

    public void CancelDrag()
    {
        _dragging = false;
        _ink.Clear();
        _startChar = _endChar = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    private Rect[] MarkupRects()
    {
        if (_startChar < 0 || _endChar < 0 || _document == null) return [];
        return _document.TextRects(_dragPage, Math.Min(_startChar, _endChar), Math.Abs(_endChar - _startChar) + 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_view == null || _document == null) return;
        if (_dragging && (uint)_dragPage < (uint)_document.PageCount) DrawPreview(dc);

        if (_selected is { } selected && (uint)selected.PageIndex < (uint)_document.PageCount)
        {
            var rect = LayerRect(selected.PageIndex, selected.Bounds);
            rect.Inflate(4, 4);
            var accent = TryFindResource("Brush.Accent") as Brush ?? Brushes.OrangeRed;
            dc.DrawRectangle(null, new Pen(accent, 1.5) { DashStyle = new DashStyle([4, 3], 0) }, rect);
        }
    }

    private void DrawPreview(DrawingContext dc)
    {
        var scale = _view!.Scale;
        var brush = new SolidColorBrush(Color);
        var pen = new Pen(brush, Math.Max(1, StrokeWidth * scale)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (_tool)
        {
            case AnnotationTool.Highlight:
            case AnnotationTool.Underline:
            case AnnotationTool.Strikeout:
                var translucent = new SolidColorBrush(Color.FromArgb(110, Color.R, Color.G, Color.B));
                foreach (var r in MarkupRects())
                {
                    var rect = LayerRect(_dragPage, r);
                    var thin = new Pen(brush, Math.Max(1, rect.Height / 12));
                    if (_tool == AnnotationTool.Highlight) dc.DrawRectangle(translucent, null, rect);
                    else if (_tool == AnnotationTool.Underline) dc.DrawLine(thin, new Point(rect.Left, rect.Bottom - 1), new Point(rect.Right, rect.Bottom - 1));
                    else dc.DrawLine(thin, new Point(rect.Left, rect.Top + rect.Height / 2), new Point(rect.Right, rect.Top + rect.Height / 2));
                }
                break;
            case AnnotationTool.Pen:
                if (_ink.Count == 0) break;
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(ToLayer(_dragPage, _ink[0]), false, false);
                    context.PolyLineTo(_ink.Skip(1).Select(p => ToLayer(_dragPage, p)).ToList(), true, true);
                }
                dc.DrawGeometry(null, pen, geometry);
                break;
            case AnnotationTool.Rectangle:
                dc.DrawRectangle(null, pen, new Rect(ToLayer(_dragPage, _dragStart), ToLayer(_dragPage, _dragEnd)));
                break;
            case AnnotationTool.Ellipse:
                var box = new Rect(ToLayer(_dragPage, _dragStart), ToLayer(_dragPage, _dragEnd));
                dc.DrawEllipse(null, pen, new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2);
                break;
            case AnnotationTool.Arrow:
                var from = ToLayer(_dragPage, _dragStart);
                var to = ToLayer(_dragPage, _dragEnd);
                dc.DrawLine(pen, from, to);
                var direction = to - from;
                if (direction.Length > 1)
                {
                    direction.Normalize();
                    var head = Math.Max(8, StrokeWidth * 4) * scale;
                    var normal = new Vector(-direction.Y, direction.X);
                    var back = to - direction * head;
                    dc.DrawLine(pen, back + normal * head * 0.5, to);
                    dc.DrawLine(pen, back - normal * head * 0.5, to);
                }
                break;
        }
    }

    // ---------------------------------------------------------------- selecting existing annotations

    private List<AnnotationInfo> AnnotationsOn(int page)
    {
        if (!_cache.TryGetValue(page, out var list)) _cache[page] = list = _document!.GetAnnotations(page);
        return list;
    }

    /// <summary>Selects the topmost annotation under a click. Double-clicking a note opens its editor.</summary>
    public bool TrySelectAt(PageHit hit, int clickCount)
    {
        if (_view == null || _document == null || _tool != AnnotationTool.None) return false;
        var point = _view.DisplayToPage(hit.PageIndex, hit.Display);
        var match = AnnotationsOn(hit.PageIndex).LastOrDefault(a =>
        {
            var bounds = a.Bounds;
            bounds.Inflate(3, 3);
            return bounds.Contains(point);
        });
        if (match == null)
        {
            SetSelected(null);
            return false;
        }

        SetSelected(match);
        if (match.IsNote && clickCount == 2) OpenNoteEditor(match.PageIndex, hit.Display, match);
        return true;
    }

    public void ClearSelection() => SetSelected(null);

    public void DeleteSelected()
    {
        if (_selected is not { } selected || _document == null) return;
        SetSelected(null);
        _document.DeleteAnnotation(selected);
    }

    public void EditSelectedNote()
    {
        if (_selected is not { IsNote: true } note || _document == null) return;
        var display = _document.GetPageToDisplay(note.PageIndex).TransformBounds(note.Bounds);
        OpenNoteEditor(note.PageIndex, display.TopLeft, note);
    }

    private void SetSelected(AnnotationInfo? info)
    {
        if (_selected == info) return;
        _selected = info;
        if (_floating?.Tag as string == "selection") CloseFloating();
        if (info != null) ShowSelectionBar(info);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowSelectionBar(AnnotationInfo info)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (info.IsNote)
        {
            var text = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(info.Contents) ? "(empty note)" : info.Contents,
                MaxWidth = 260,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 4, 8, 4),
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            panel.Children.Add(text);
            panel.Children.Add(BarButton("", "Edit note", EditSelectedNote));
        }
        panel.Children.Add(BarButton("", "Delete (Del)", DeleteSelected));

        var display = _document!.GetPageToDisplay(info.PageIndex).TransformBounds(info.Bounds);
        ShowFloating(Chrome(panel), "selection", info.PageIndex, new Point(display.Left, display.Top));
    }

    private static Button BarButton(string glyph, string tip, Action action)
    {
        var button = new Button
        {
            Content = glyph,
            ToolTip = tip,
            Height = 30,
            MinWidth = 30,
            FontSize = 13,
            Style = (Style)Application.Current.FindResource("ToolButton"),
        };
        button.Click += (_, _) => action();
        return button;
    }

    // ---------------------------------------------------------------- sticky-note editor

    private void OpenNoteEditor(int page, Point display, AnnotationInfo? existing)
    {
        CloseFloating(commitNote: true);
        var box = new TextBox
        {
            Text = existing?.Contents ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = 260,
            Height = 96,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(6),
        };
        var save = new Button { Content = "Save note", Style = (Style)Application.Current.FindResource("AccentButton"), Height = 30, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("SecondaryButton"), Height = 30, MinWidth = 70, Margin = new Thickness(6, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(6) };
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var chrome = Chrome(panel);
        chrome.DataContext = (page, display, existing, box);

        save.Click += (_, _) => CloseFloating(commitNote: true);
        cancel.Click += (_, _) => CloseFloating();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseFloating();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                CloseFloating(commitNote: true);
                e.Handled = true;
            }
        };

        ShowFloating(chrome, "note", page, display);
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---------------------------------------------------------------- floating panels

    private static Border Chrome(UIElement child)
    {
        var border = new Border
        {
            Child = child,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            BorderThickness = new Thickness(1),
            Effect = (System.Windows.Media.Effects.Effect)Application.Current.FindResource("PopupShadow"),
        };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Popup");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return border;
    }

    private void ShowFloating(FrameworkElement element, string kind, int page, Point anchor)
    {
        CloseFloating();
        element.Tag = kind;
        _floating = element;
        _floatingPage = page;
        _floatingAnchor = anchor;
        Children.Add(element);
        element.Loaded += (_, _) => PositionFloating();
        PositionFloating();
    }

    private void PositionFloating()
    {
        if (_floating == null || _view == null || _document == null || (uint)_floatingPage >= (uint)_document.PageCount) return;
        var p = ToLayer(_floatingPage, _floatingAnchor);
        var height = _floating.ActualHeight;
        var isNote = _floating.Tag as string == "note";
        var x = isNote ? p.X + 26 : p.X - 4;
        var y = isNote ? p.Y : p.Y - height - 10;
        if (y < 4) y = isNote ? 4 : p.Y + 10;
        SetLeft(_floating, Math.Max(4, Math.Min(x, ActualWidth - _floating.ActualWidth - 4)));
        SetTop(_floating, y);
    }

    private void CloseFloating(bool commitNote = false)
    {
        if (_floating == null) return;
        var closing = _floating;
        _floating = null;
        Children.Remove(closing);

        if (commitNote && closing.Tag as string == "note" && _document != null &&
            closing.DataContext is ValueTuple<int, Point, AnnotationInfo?, TextBox> state)
        {
            var (page, display, existing, box) = state;
            var text = box.Text.Trim();
            if (existing != null)
            {
                if (text != existing.Contents) _document.UpdateNote(existing, text);
            }
            else if (text.Length > 0)
            {
                _document.AddNote(page, display, text, Color);
            }
            _view?.Focus();
        }
        if (_selected != null && closing.Tag as string == "note") ShowSelectionBar(_selected);
    }
}
