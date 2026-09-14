using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PDFPlus.Controls;
using PDFPlus.Core;

namespace PDFPlus.Views;

/// <summary>
/// Overlay for Edit mode: outlines text and images under the mouse, retypes a line of text in place,
/// drags objects to move them, resizes images from their corners, and deletes the selection.
/// </summary>
public sealed class EditLayer : Canvas
{
    private const double DragThreshold = 4;
    private const double HandleReach = 7;

    private enum DragKind { None, Pending, Move, Resize }

    private PdfView? _view;
    private PdfDocument? _document;
    private bool _active;
    private readonly Dictionary<int, List<PageObjectInfo>> _cache = new();

    private PageObjectInfo? _hover;
    private PageObjectInfo? _selected;
    private FrameworkElement? _selectionBar;

    private DragKind _drag;
    private Point _dragStart;
    private Vector _moveDelta;
    private Point _resizeAnchor;
    private Rect _resizeRect;
    private double _aspect = 1;

    private TextBox? _editor;
    private PageObjectInfo? _editing;
    private FrameworkElement? _editorBar;

    /// <summary>Short status messages for the toast.</summary>
    public event EventHandler<string>? Message;
    public event EventHandler<PageHit>? AddImageRequested;
    public event EventHandler? SelectionChanged;

    public EditLayer()
    {
        ClipToBounds = true;
    }

    public void Attach(PdfView view, PdfDocument document)
    {
        _view = view;
        _document = document;
        view.ViewChanged += (_, _) => Reposition();
        view.ZoomChanged += (_, _) => Reposition();
        document.PageContentChanged += (_, index) => _cache.Remove(index);
        document.PagesChanged += (_, _) =>
        {
            _cache.Clear();
            CloseEditor();
            _hover = null;
            SetSelected(null);
        };
    }

    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active == value) return;
            if (!value)
            {
                CommitEditor();
                CancelDrag();
                SetSelected(null);
                _hover = null;
            }
            _active = value;
            Background = value ? Brushes.Transparent : null;
            Cursor = null;
            InvalidateVisual();
        }
    }

    public PageObjectInfo? Selected => _selected;
    public bool IsEditingText => _editor != null;

    // ---------------------------------------------------------------- coordinates & lookup

    private bool IsValid(PageObjectInfo? info) => info != null && _document != null && (uint)info.PageIndex < (uint)_document.PageCount;

    private PageHit? Hit(Point layerPoint) => _view?.HitTest(TranslatePoint(layerPoint, _view));

    private Point ToLayer(int page, Point display) => _view!.TranslatePoint(_view.DisplayToViewport(page, display), this);

    private Rect DisplayBounds(PageObjectInfo info) => _document!.GetPageToDisplay(info.PageIndex).TransformBounds(info.Bounds);

    private Rect LayerRect(int page, Rect display) => new(ToLayer(page, display.TopLeft), ToLayer(page, display.BottomRight));

    private Point DisplayOnPage(int page, Point layerPoint)
    {
        var viewport = TranslatePoint(layerPoint, _view);
        var rect = _view!.PageRect(page);
        return new Point((viewport.X - rect.X) / _view.Scale, (viewport.Y - rect.Y) / _view.Scale);
    }

    /// <summary>The object's outline in layer coordinates, optionally shifted (display points) and padded (DIPs).</summary>
    private Point[] LayerCorners(PageObjectInfo info, Vector displayOffset = default, double padding = 0)
    {
        var pad = padding / Math.Max(0.01, _view!.Scale);
        var toDisplay = _document!.GetPageToDisplay(info.PageIndex);
        return new[]
        {
            info.Corner(info.MinU - pad, info.MinV - pad), info.Corner(info.MaxU + pad, info.MinV - pad),
            info.Corner(info.MaxU + pad, info.MaxV + pad), info.Corner(info.MinU - pad, info.MaxV + pad),
        }.Select(c => ToLayer(info.PageIndex, toDisplay.Transform(c) + displayOffset)).ToArray();
    }

    private List<PageObjectInfo> ObjectsOn(int page)
    {
        if (!_cache.TryGetValue(page, out var list)) _cache[page] = list = _document!.GetPageObjects(page);
        return list;
    }

    /// <summary>Topmost text under the point, else topmost image.</summary>
    private PageObjectInfo? ObjectAt(PageHit hit)
    {
        var point = _view!.DisplayToPage(hit.PageIndex, hit.Display);
        var tolerance = 2 / Math.Max(0.1, _view.Zoom);
        var objects = ObjectsOn(hit.PageIndex);
        return objects.LastOrDefault(o => o.IsText && o.Contains(point, tolerance)) ??
               objects.LastOrDefault(o => !o.IsText && o.Contains(point, tolerance));
    }

    private int HandleAt(Point layerPoint)
    {
        if (_selected is not { IsText: false } image || !IsValid(image) || _editor != null) return -1;
        var rect = LayerRect(image.PageIndex, DisplayBounds(image));
        Point[] corners = [rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft];
        for (var i = 0; i < corners.Length; i++)
            if (Math.Abs(layerPoint.X - corners[i].X) <= HandleReach && Math.Abs(layerPoint.Y - corners[i].Y) <= HandleReach)
                return i;
        return -1;
    }

    private static bool IsInside(object? source, FrameworkElement? element)
    {
        if (element == null) return false;
        for (var node = source as DependencyObject; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, element)) return true;
        return false;
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_active || _document == null || _view == null || e.Handled) return;
        if (IsInside(e.OriginalSource, _editor) || IsInside(e.OriginalSource, _editorBar) || IsInside(e.OriginalSource, _selectionBar)) return;
        e.Handled = true;
        var position = e.GetPosition(this);
        CommitEditor();
        _view.Focus();

        var handle = HandleAt(position);
        if (handle >= 0 && _selected is { } image)
        {
            var bounds = DisplayBounds(image);
            Point[] corners = [bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft];
            _resizeAnchor = corners[(handle + 2) % 4];
            _resizeRect = bounds;
            _aspect = bounds.Width / Math.Max(0.01, bounds.Height);
            StartDrag(DragKind.Resize, position);
            return;
        }

        if (Hit(position) is not { } hit)
        {
            SetSelected(null);
            return;
        }
        var target = ObjectAt(hit);
        if (target == null)
        {
            SetSelected(null);
            if (_document.CharIndexAt(hit.PageIndex, _view.DisplayToPage(hit.PageIndex, hit.Display), 1) >= 0)
                Message?.Invoke(this, "This text can't be edited. It's inside a graphic or a scanned image.");
            return;
        }
        SetSelected(target);
        StartDrag(DragKind.Pending, position);
    }

    private void StartDrag(DragKind kind, Point position)
    {
        HideSelectionBar();
        _drag = kind;
        _dragStart = position;
        _moveDelta = default;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_active || _document == null || _view == null) return;
        var position = e.GetPosition(this);
        switch (_drag)
        {
            case DragKind.Pending when (position - _dragStart).Length >= DragThreshold:
                _drag = DragKind.Move;
                goto case DragKind.Move;
            case DragKind.Move:
                _moveDelta = (position - _dragStart) / _view.Scale;
                InvalidateVisual();
                return;
            case DragKind.Resize:
                UpdateResize(position, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                InvalidateVisual();
                return;
            case DragKind.Pending:
                return;
        }
        UpdateHover(position);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == null) return;
        _hover = null;
        InvalidateVisual();
    }

    private void UpdateHover(Point position)
    {
        PageObjectInfo? hover = null;
        Cursor? cursor = null;
        var handle = HandleAt(position);
        if (handle >= 0)
        {
            cursor = handle is 0 or 2 ? Cursors.SizeNWSE : Cursors.SizeNESW;
        }
        else if (Hit(position) is { } hit && ObjectAt(hit) is { } target)
        {
            hover = target;
            cursor = target.IsText ? Cursors.IBeam : Cursors.SizeAll;
        }
        Cursor = cursor;
        if (ReferenceEquals(hover, _hover)) return;
        _hover = hover;
        InvalidateVisual();
    }

    private void UpdateResize(Point layerPosition, bool freeAspect)
    {
        if (_selected is not { } image) return;
        var point = DisplayOnPage(image.PageIndex, layerPosition);
        var width = Math.Max(4, Math.Abs(point.X - _resizeAnchor.X));
        var height = Math.Max(4, Math.Abs(point.Y - _resizeAnchor.Y));
        if (!freeAspect)
        {
            if (width / height > _aspect) height = width / _aspect;
            else width = height * _aspect;
        }
        var x = point.X < _resizeAnchor.X ? _resizeAnchor.X - width : _resizeAnchor.X;
        var y = point.Y < _resizeAnchor.Y ? _resizeAnchor.Y - height : _resizeAnchor.Y;
        _resizeRect = new Rect(x, y, width, height);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragKind.None) return;
        var drag = _drag;
        _drag = DragKind.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
        if (_selected is not { } target || _document == null || !IsValid(target))
        {
            InvalidateVisual();
            return;
        }

        switch (drag)
        {
            case DragKind.Pending:
                if (target.IsText) OpenEditor(target);
                else ShowSelectionBar(target);
                break;
            case DragKind.Move:
                var toPage = _document.GetPageToDisplay(target.PageIndex).Invert();
                var pageDelta = toPage.Transform(new Point(_moveDelta.X, _moveDelta.Y)) - toPage.Transform(new Point(0, 0));
                _moveDelta = default;
                if (pageDelta.Length < 0.05) break;
                var bounds = target.Bounds;
                if (Run(() => _document.MoveObject(target, pageDelta)))
                    Reselect(target.PageIndex, target.Kind, target.Text, new Rect(bounds.Location + pageDelta, bounds.Size));
                break;
            case DragKind.Resize:
                var pageRect = _document.GetPageToDisplay(target.PageIndex).Invert().TransformBounds(_resizeRect);
                if (Run(() => _document.ResizeImage(target, pageRect)))
                    Reselect(target.PageIndex, target.Kind, "", pageRect);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != DragKind.None) CancelDrag();
    }

    private void CancelDrag()
    {
        _drag = DragKind.None;
        _moveDelta = default;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!_active || _document == null || _view == null || e.Handled || IsInside(e.OriginalSource, _editor)) return;
        e.Handled = true;
        CommitEditor();
        var hit = Hit(e.GetPosition(this));
        var target = hit is { } h ? ObjectAt(h) : null;
        SetSelected(target);

        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        if (target != null)
        {
            if (target.IsText) menu.Items.Add(DocumentView.MenuItemFor("Edit text", "\uE70F", () => OpenEditor(target), "Enter"));
            menu.Items.Add(DocumentView.MenuItemFor(target.IsText ? "Delete text" : "Delete image", "\uE74D", DeleteSelected, "Del"));
        }
        if (hit is { } where)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(DocumentView.MenuItemFor("Add image here…", "\uEB9F", () => AddImageRequested?.Invoke(this, where)));
        }
        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (!_active || _view == null) return;
        _view.ScrollByWheel(e.Delta, TranslatePoint(e.GetPosition(this), _view));
        e.Handled = true;
    }

    // ---------------------------------------------------------------- selection

    private void SetSelected(PageObjectInfo? info)
    {
        if (ReferenceEquals(_selected, info)) return;
        _selected = info;
        HideSelectionBar();
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection() => SetSelected(null);

    public void EditSelected()
    {
        if (_selected is { IsText: true } run) OpenEditor(run);
    }

    public void DeleteSelected()
    {
        if (_selected is not { } target || _document == null || !IsValid(target)) return;
        SetSelected(null);
        if (Run(() => _document.DeleteObject(target)))
            Message?.Invoke(this, (target.IsText ? "Text deleted" : "Image deleted") + "   ·   Ctrl+Z to undo");
    }

    /// <summary>Selects the image nearest a display rectangle, e.g. one that was just added.</summary>
    public void SelectImage(int page, Rect displayRect)
    {
        if (_document == null || (uint)page >= (uint)_document.PageCount) return;
        Reselect(page, PageObjectKind.Image, "", _document.GetPageToDisplay(page).Invert().TransformBounds(displayRect));
    }

    internal void SetHover(PageObjectInfo? info)
    {
        _hover = info;
        InvalidateVisual();
    }

    /// <summary>After an edit the page is re-read, so find the changed object again by kind, text and position.</summary>
    private void Reselect(int page, PageObjectKind kind, string text, Rect expected)
    {
        if (_document == null || (uint)page >= (uint)_document.PageCount) return;
        static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
        var center = Center(expected);
        var match = ObjectsOn(page)
            .Where(o => o.Kind == kind && (kind == PageObjectKind.Image || o.Text == text))
            .MinBy(o => (Center(o.Bounds) - center).Length);
        if (match == null || (Center(match.Bounds) - center).Length > 6 + Math.Max(expected.Width, expected.Height) * 0.1)
        {
            SetSelected(null);
            return;
        }
        SetSelected(match);
        ShowSelectionBar(match);
    }

    private bool Run(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            SetSelected(null);
            Message?.Invoke(this, "Couldn't make that change: " + ex.Message);
            return false;
        }
    }

    private void ShowSelectionBar(PageObjectInfo target)
    {
        HideSelectionBar();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (target.IsText) panel.Children.Add(AnnotationLayer.BarButton("\uE70F", "Edit text (Enter)", () => OpenEditor(target)));
        panel.Children.Add(AnnotationLayer.BarButton("\uE74D", "Delete (Del)", DeleteSelected));
        foreach (var button in panel.Children.OfType<Button>()) button.Focusable = false;
        _selectionBar = AnnotationLayer.Chrome(panel);
        _selectionBar.Loaded += (_, _) => Reposition();
        Children.Add(_selectionBar);
        Reposition();
    }

    private void HideSelectionBar()
    {
        if (_selectionBar == null) return;
        Children.Remove(_selectionBar);
        _selectionBar = null;
    }

    private void Reposition()
    {
        if (_view == null || _document == null) return;
        PositionEditor();
        if (_selectionBar != null && IsValid(_selected))
        {
            var corners = LayerCorners(_selected!);
            PlaceAbove(_selectionBar, corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.Y));
        }
        InvalidateVisual();
    }

    private void PlaceAbove(FrameworkElement element, double left, double top, double bottom)
    {
        var height = element.ActualHeight;
        var y = top - height - 8;
        if (y < 4) y = bottom + 8;
        SetLeft(element, Math.Max(4, Math.Min(left - 4, ActualWidth - element.ActualWidth - 4)));
        SetTop(element, y);
    }

    // ---------------------------------------------------------------- in-place text editor

    public void OpenEditor(PageObjectInfo run)
    {
        if (!_active || _document == null || _view == null || !run.IsText || !IsValid(run)) return;
        CloseEditor();
        SetSelected(run);
        _editing = run;

        var look = run.Font ?? FontLook.Default;
        var color = Color.FromRgb(run.Color.R, run.Color.G, run.Color.B);
        var lightText = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 200;
        var box = new TextBox
        {
            Text = run.Text,
            FontFamily = new FontFamily(look.Family),
            FontWeight = FontWeight.FromOpenTypeWeight(Math.Clamp(look.Weight, 1, 999)),
            FontStyle = look.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(color),
            Background = new SolidColorBrush(lightText ? Color.FromRgb(0x30, 0x30, 0x34) : Colors.White),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0, 0),
        };
        box.SetValue(StyleProperty, null);
        box.SetResourceReference(Control.BorderBrushProperty, "Brush.Accent");
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Tab)
            {
                CommitEditor();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelEditor();
                e.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, e) =>
        {
            // The TextBox's own right-click menu takes focus briefly; don't commit for that.
            if (e.NewFocus is MenuItem or System.Windows.Controls.ContextMenu) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_editor == box && !box.IsKeyboardFocusWithin && Keyboard.FocusedElement is not (MenuItem or System.Windows.Controls.ContextMenu)) CommitEditor();
            }, DispatcherPriority.Input);
        };
        box.TextChanged += (_, _) => PositionEditor();

        var label = look.Family + (look.Bold ? " Bold" : "") + (look.Italic ? " Italic" : "") + $"  ·  {run.FontSize:0.#} pt";
        var caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), FontSize = 12 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(caption);
        panel.Children.Add(AnnotationLayer.BarButton("\uE73E", "Save text (Enter)", CommitEditor));
        panel.Children.Add(AnnotationLayer.BarButton("\uE74D", "Delete this text", () =>
        {
            var target = _editing;
            CloseEditor();
            if (target == null) return;
            SetSelected(target);
            DeleteSelected();
        }));
        panel.Children.Add(AnnotationLayer.BarButton("\uE711", "Cancel (Esc)", CancelEditor));
        foreach (var button in panel.Children.OfType<Button>()) button.Focusable = false;

        _editor = box;
        _editorBar = AnnotationLayer.Chrome(panel);
        _editorBar.Loaded += (_, _) => PositionEditor();
        Children.Add(box);
        Children.Add(_editorBar);
        PositionEditor();
        InvalidateVisual();
        Dispatcher.BeginInvoke(() =>
        {
            if (_editor != box) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void PositionEditor()
    {
        if (_editor == null || _editing is not { } run || _view == null || !IsValid(run)) return;
        var scale = _view.Scale;
        var toDisplay = _document!.GetPageToDisplay(run.PageIndex);
        var topLeft = ToLayer(run.PageIndex, toDisplay.Transform(run.Corner(run.MinU, run.MaxV)));
        var topRight = ToLayer(run.PageIndex, toDisplay.Transform(run.Corner(run.MaxU, run.MaxV)));
        var angle = Math.Atan2(topRight.Y - topLeft.Y, topRight.X - topLeft.X) * 180 / Math.PI;

        var fontSize = Math.Max(1, run.FontSize * scale);
        _editor.FontSize = fontSize;
        var typeface = new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight, FontStretches.Normal);
        var textWidth = new FormattedText(_editor.Text + "  ", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        // The default TextBox template insets text by its 1.5 border plus a 2 DIP margin.
        const double inset = 3.5;
        _editor.Width = Math.Max((run.MaxU - run.MinU) * scale, textWidth) + inset * 2 + 4;
        _editor.Height = Math.Max(fontSize * 1.25, (run.MaxV - run.MinV) * scale) + 4;
        _editor.RenderTransform = Math.Abs(angle) < 0.01 ? Transform.Identity : new RotateTransform(angle);
        SetLeft(_editor, topLeft.X - inset);
        SetTop(_editor, topLeft.Y - 2);

        if (_editorBar != null) PlaceAbove(_editorBar, topLeft.X - inset, topLeft.Y - 2, topLeft.Y + _editor.Height);
    }

    /// <summary>Writes the editor's text into the PDF (if it changed) and closes the editor.</summary>
    public void CommitEditor()
    {
        if (_editor == null || _editing is not { } run || _document == null) return;
        var text = _editor.Text;
        CloseEditor();
        SetSelected(null);
        if (Collapse(text) == Collapse(run.Text) || !IsValid(run)) return;

        TextEditResult? result = null;
        if (!Run(() => result = _document.ReplaceText(run, text)) || result == null) return;
        var look = run.Font ?? FontLook.Default;
        var original = look.PdfName.Split('-', ',')[0];
        var message = result.Outcome switch
        {
            TextFontOutcome.Removed => "Text deleted   ·   Ctrl+Z to undo",
            TextFontOutcome.SubstituteFont => $"{look.Family} doesn't have some of those characters, so this text uses {result.FontFamily}",
            TextFontOutcome.MatchingFont when !look.IsInstalled => $"{original} isn't installed, so the new text uses {result.FontFamily}",
            _ => null,
        };
        if (message != null) Message?.Invoke(this, message);
    }

    public void CancelEditor()
    {
        CloseEditor();
        SetSelected(null);
    }

    private void CloseEditor()
    {
        if (_editor == null) return;
        var box = _editor;
        _editor = null;
        _editing = null;
        var hadFocus = box.IsKeyboardFocusWithin;
        Children.Remove(box);
        if (_editorBar != null) Children.Remove(_editorBar);
        _editorBar = null;
        if (hadFocus) _view?.Focus();
        InvalidateVisual();
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---------------------------------------------------------------- drawing

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!_active || _view == null || _document == null) return;
        var accent = TryFindResource("Brush.Accent") as SolidColorBrush ?? Brushes.DodgerBlue;
        var tint = new SolidColorBrush(Color.FromArgb(36, accent.Color.R, accent.Color.G, accent.Color.B));
        var dashed = new Pen(accent, 1) { DashStyle = new DashStyle([3, 3], 0) };
        var solid = new Pen(accent, 1.5);

        if (_hover != null && !ReferenceEquals(_hover, _selected) && _drag == DragKind.None && IsValid(_hover))
            dc.DrawGeometry(null, dashed, Polygon(LayerCorners(_hover, padding: 2)));

        if (_selected is not { } selected || !IsValid(selected) || ReferenceEquals(selected, _editing)) return;
        switch (_drag)
        {
            case DragKind.Move:
                dc.DrawGeometry(null, dashed, Polygon(LayerCorners(selected, padding: 2)));
                dc.DrawGeometry(tint, solid, Polygon(LayerCorners(selected, _moveDelta, 2)));
                break;
            case DragKind.Resize:
                dc.DrawRectangle(tint, solid, LayerRect(selected.PageIndex, _resizeRect));
                break;
            default:
                dc.DrawGeometry(null, solid, Polygon(LayerCorners(selected, padding: selected.IsText ? 2 : 0)));
                if (!selected.IsText)
                {
                    var rect = LayerRect(selected.PageIndex, DisplayBounds(selected));
                    foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft })
                        dc.DrawRectangle(Brushes.White, solid, new Rect(corner.X - 4, corner.Y - 4, 8, 8));
                }
                break;
        }
    }

    private static StreamGeometry Polygon(Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, true);
            context.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        geometry.Freeze();
        return geometry;
    }
}
