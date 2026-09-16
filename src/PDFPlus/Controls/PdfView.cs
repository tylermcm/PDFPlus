using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDFPlus.Core;
using static PDFPlus.Native.Pdfium;

namespace PDFPlus.Controls;

public enum FitMode { None, Width, Page }

public enum ViewTool { Select, Hand }

/// <summary>A point on a page, in display points (top-left origin, rotation applied).</summary>
public readonly record struct PageHit(int PageIndex, Point Display);

/// <summary>
/// Continuous-scroll page viewer. Implements IScrollInfo directly so only visible pages are drawn,
/// regardless of document length. Bitmaps come from the shared render thread; very large zooms get a
/// low-res base image plus a sharp tile covering just the visible area.
/// </summary>
public sealed class PdfView : FrameworkElement, IScrollInfo
{
    public const double PointsToDip = 96.0 / 72.0;
    public const double MinZoom = 0.1;
    public const double MaxZoom = 8.0;
    private const double PageGap = 16;
    private const double PageMargin = 20;
    private const long MaxFullPixels = 12_000_000;
    private const long BasePixels = 3_000_000;

    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6, 8];
    private static readonly Brush ShadowBrush = Frozen(new SolidColorBrush(Color.FromArgb(46, 0, 0, 0)));
    private static readonly Brush HitBrush = Frozen(new SolidColorBrush(Color.FromArgb(110, 255, 214, 0)));
    private static readonly Brush ActiveHitBrush = Frozen(new SolidColorBrush(Color.FromArgb(140, 255, 120, 0)));
    private static readonly Brush SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(90, 51, 119, 255)));

    private sealed class PageBitmaps
    {
        public BitmapSource? Full;
        public int FullW, FullH, FullVersion = -1;
        public int PendingW, PendingH, PendingVersion = -1;

        public BitmapSource? Tile;
        public Int32Rect TileRect;
        public int TileW, TileH, TileVersion = -1;
        public long TileRequest;
        public Int32Rect PendingTileRect;
        public int PendingTileW, PendingTileH, PendingTileVersion;
    }

    private enum DragMode { None, Pan, Select, Form, Link }

    private PdfDocument? _document;
    private Rect[] _layout = [];
    private double _zoom = 1;
    private FitMode _fitMode = FitMode.Width;
    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private double _xShift;
    private int _currentPage = -1;

    private readonly Dictionary<int, PageBitmaps> _bitmaps = new();
    private readonly Dictionary<int, (Affine ToDisplay, Affine ToPage)> _matrices = new();
    private int[] _versions = [];
    private bool _updateQueued;
    private long _tileCounter;

    private IReadOnlyList<SearchHit> _hits = [];
    private Dictionary<int, List<int>> _hitsByPage = new();
    private int _activeHit = -1;

    private (int Page, int Char)? _selAnchor;
    private (int Page, int Char)? _selFocus;
    private readonly Dictionary<int, Rect[]> _selectionCache = new();

    private DragMode _drag;
    private Point _dragStart;
    private Point _panOrigin;
    private int _formPage = -1;
    private LinkTarget? _pressedLink;
    private Point _lastHover = new(double.NaN, double.NaN);

    public event EventHandler? ZoomChanged;
    public event EventHandler? CurrentPageChanged;
    public event EventHandler? ViewChanged;
    public event EventHandler<LinkTarget>? LinkActivated;
    public event EventHandler<PageHit>? PlacementRequested;

    public PdfView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // ---------------------------------------------------------------- public surface

    public PdfDocument? Document
    {
        get => _document;
        set
        {
            if (_document == value) return;
            if (_document != null)
            {
                _document.PagesChanged -= OnPagesChanged;
                _document.PageContentChanged -= OnPageContentChanged;
                _document.TextLayerChanged -= OnTextLayerChanged;
            }
            _document = value;
            if (_document != null)
            {
                _document.PagesChanged += OnPagesChanged;
                _document.PageContentChanged += OnPageContentChanged;
                _document.TextLayerChanged += OnTextLayerChanged;
            }
            _offset = new Point();
            ClearSelection();
            SetSearchHits([], -1);
            ResetPages();
            if (_fitMode != FitMode.None) ApplyFit();
        }
    }

    public double Zoom => _zoom;
    public double Scale => _zoom * PointsToDip;
    public int CurrentPageIndex => Math.Max(0, _currentPage);
    public ViewTool Tool { get; set; }
    public bool PlacementMode { get; set; }
    public bool HasSelection => _selAnchor != null && _selFocus != null;

    public FitMode FitMode
    {
        get => _fitMode;
        set
        {
            _fitMode = value;
            ApplyFit();
        }
    }

    public void SetZoom(double zoom, Point? anchor = null)
    {
        _fitMode = FitMode.None;
        SetZoomCore(Math.Clamp(zoom, MinZoom, MaxZoom), anchor);
    }

    public void ZoomIn() => SetZoom(ZoomSteps.FirstOrDefault(z => z > _zoom + 0.001, MaxZoom));
    public void ZoomOut() => SetZoom(ZoomSteps.LastOrDefault(z => z < _zoom - 0.001, MinZoom));

    public void GoToPage(int index, Point? display = null)
    {
        if (_layout.Length == 0) return;
        index = Math.Clamp(index, 0, _layout.Length - 1);
        var rect = _layout[index];
        var y = display is { } d ? rect.Y + d.Y * Scale - 32 : rect.Y - PageGap / 2;
        var x = _offset.X;
        if (display is { } dx && rect.Width > _viewport.Width)
            x = rect.X + dx.X * Scale + _xShift - 32;
        SetOffsetCore(x, y);
        if (_currentPage != index)
        {
            _currentPage = index;
            CurrentPageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void NavigateTo(LinkTarget target)
    {
        if (_document == null || (uint)target.PageIndex >= (uint)_layout.Length) return;
        Point? display = target.Location is { } location ? Matrices(target.PageIndex).ToDisplay.Transform(location) : null;
        GoToPage(target.PageIndex, display);
    }

    /// <summary>Viewport rectangle (DIPs) of a page as currently laid out.</summary>
    public Rect PageRect(int index)
    {
        var r = _layout[index];
        return new Rect(r.X - _offset.X + _xShift, r.Y - _offset.Y, r.Width, r.Height);
    }

    public Point DisplayToViewport(int page, Point display)
    {
        var r = PageRect(page);
        return new Point(r.X + display.X * Scale, r.Y + display.Y * Scale);
    }

    public PageHit? HitTest(Point viewportPoint, bool nearest = false)
    {
        if (_layout.Length == 0) return null;
        var lx = viewportPoint.X + _offset.X - _xShift;
        var ly = viewportPoint.Y + _offset.Y;
        var index = IndexAtY(ly);
        if (index + 1 < _layout.Length && ly > _layout[index].Bottom && ly - _layout[index].Bottom > _layout[index + 1].Y - ly)
            index++;
        var r = _layout[index];
        if (!nearest && !r.Contains(lx, ly)) return null;
        var display = new Point(
            Math.Clamp((lx - r.X) / Scale, 0, r.Width / Scale),
            Math.Clamp((ly - r.Y) / Scale, 0, r.Height / Scale));
        return new PageHit(index, display);
    }

    public Point DisplayToPage(int page, Point display) => Matrices(page).ToPage.Transform(display);

    // ---------------------------------------------------------------- search & selection

    public void SetSearchHits(IReadOnlyList<SearchHit> hits, int active)
    {
        _hits = hits;
        _hitsByPage = new Dictionary<int, List<int>>();
        for (var i = 0; i < hits.Count; i++)
        {
            if (!_hitsByPage.TryGetValue(hits[i].PageIndex, out var list))
                _hitsByPage[hits[i].PageIndex] = list = new List<int>();
            list.Add(i);
        }
        _activeHit = active;
        InvalidateVisual();
    }

    public void ShowHit(int index)
    {
        if ((uint)index >= (uint)_hits.Count || _document == null) return;
        _activeHit = index;
        var hit = _hits[index];
        if ((uint)hit.PageIndex >= (uint)_layout.Length) return;
        var m = Matrices(hit.PageIndex).ToDisplay;
        var bounds = Rect.Empty;
        foreach (var r in hit.Rects) bounds.Union(m.TransformBounds(r));
        if (!bounds.IsEmpty)
        {
            var topLeft = DisplayToViewport(hit.PageIndex, bounds.TopLeft);
            var bottomRight = DisplayToViewport(hit.PageIndex, bounds.BottomRight);
            var x = _offset.X;
            var y = _offset.Y;
            if (topLeft.Y < 40 || bottomRight.Y > _viewport.Height - 40) y = _offset.Y + topLeft.Y - _viewport.Height / 3;
            if (topLeft.X < 0 || bottomRight.X > _viewport.Width) x = _offset.X + topLeft.X - _viewport.Width / 3;
            SetOffsetCore(x, y);
        }
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (_selAnchor == null && _selFocus == null) return;
        _selAnchor = null;
        _selFocus = null;
        _selectionCache.Clear();
        InvalidateVisual();
    }

    private void SetSelection((int Page, int Char) anchor, (int Page, int Char)? focus)
    {
        _selAnchor = anchor;
        _selFocus = focus;
        _selectionCache.Clear();
        InvalidateVisual();
    }

    private ((int Page, int Char) Start, (int Page, int Char) End) OrderedSelection()
    {
        var a = _selAnchor!.Value;
        var b = _selFocus!.Value;
        return (a.Page, a.Char).CompareTo((b.Page, b.Char)) <= 0 ? (a, b) : (b, a);
    }

    private Rect[] SelectionRects(int page)
    {
        if (!HasSelection || _document == null) return [];
        if (_selectionCache.TryGetValue(page, out var cached)) return cached;
        var (start, end) = OrderedSelection();
        Rect[] rects = [];
        if (page >= start.Page && page <= end.Page)
        {
            var from = page == start.Page ? start.Char : 0;
            var to = page == end.Page ? end.Char : _document.CharCount(page) - 1;
            if (to >= from) rects = _document.TextRects(page, from, to - from + 1);
        }
        _selectionCache[page] = rects;
        return rects;
    }

    /// <summary>The current text selection as PDF-space rectangles, grouped by page.</summary>
    public List<(int Page, Rect[] Rects)> SelectionTextRects()
    {
        var result = new List<(int Page, Rect[] Rects)>();
        if (!HasSelection || _document == null) return result;
        var (start, end) = OrderedSelection();
        for (var page = start.Page; page <= end.Page; page++)
        {
            var rects = SelectionRects(page);
            if (rects.Length > 0) result.Add((page, rects));
        }
        return result;
    }

    public string SelectedText()
    {
        if (_document == null) return "";
        if (_document.HasFormFocus) return _document.FormSelectedText();
        if (!HasSelection) return "";
        var (start, end) = OrderedSelection();
        var builder = new StringBuilder();
        for (var page = start.Page; page <= end.Page; page++)
        {
            var from = page == start.Page ? start.Char : 0;
            var to = page == end.Page ? end.Char : _document.CharCount(page) - 1;
            if (to < from) continue;
            if (builder.Length > 0) builder.AppendLine();
            builder.Append(_document.GetText(page, from, to - from + 1));
        }
        return builder.ToString();
    }

    public bool CopySelection()
    {
        var text = SelectedText();
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SelectAllOnPage(int page)
    {
        if (_document == null || (uint)page >= (uint)_layout.Length) return;
        var count = _document.CharCount(page);
        if (count > 0) SetSelection((page, 0), (page, count - 1));
    }

    // ---------------------------------------------------------------- layout

    private void OnPagesChanged(object? sender, EventArgs e)
    {
        var anchor = CaptureAnchor();
        ClearSelection();
        SetSearchHits([], -1);
        ResetPages();
        if (_fitMode != FitMode.None) ApplyFit();
        RestoreAnchor(anchor);
    }

    /// <summary>A page's text was recovered by OCR, so any character indexes we were holding now mean something else.</summary>
    private void OnTextLayerChanged(object? sender, int index)
    {
        ClearSelection();
        QueueUpdate();
    }

    private void OnPageContentChanged(object? sender, int index)
    {
        if ((uint)index >= (uint)_versions.Length) return;
        _versions[index]++;
        _selectionCache.Remove(index);
        QueueUpdate();
    }

    private void ResetPages()
    {
        foreach (var entry in _bitmaps.Values) entry.PendingVersion = -2;
        _bitmaps.Clear();
        _matrices.Clear();
        _versions = new int[_document?.PageCount ?? 0];
        RebuildLayout();
        _currentPage = -1;
        UpdateCurrentPage();
        QueueUpdate();
        InvalidateVisual();
    }

    private (Affine ToDisplay, Affine ToPage) Matrices(int page)
    {
        if (_matrices.TryGetValue(page, out var m)) return m;
        var toDisplay = _document!.GetPageToDisplay(page);
        m = (toDisplay, toDisplay.Invert());
        _matrices[page] = m;
        return m;
    }

    private void RebuildLayout()
    {
        var sizes = _document?.PageSizes ?? [];
        var rects = new Rect[sizes.Count];
        var maxWidth = 0.0;
        foreach (var s in sizes) maxWidth = Math.Max(maxWidth, s.Width * Scale);
        var y = PageMargin;
        for (var i = 0; i < sizes.Count; i++)
        {
            var w = sizes[i].Width * Scale;
            var h = sizes[i].Height * Scale;
            rects[i] = new Rect(PageMargin + (maxWidth - w) / 2, y, w, h);
            y += h + PageGap;
        }
        _layout = rects;
        _extent = sizes.Count == 0 ? new Size() : new Size(maxWidth + 2 * PageMargin, y - PageGap + PageMargin);
        _xShift = Math.Max(0, (_viewport.Width - _extent.Width) / 2);
        ScrollOwner?.InvalidateScrollInfo();
    }

    private int IndexAtY(double y)
    {
        int lo = 0, hi = _layout.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_layout[mid].Y <= y) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private (int First, int Last) VisibleRange(double extra)
    {
        if (_layout.Length == 0) return (0, -1);
        return (IndexAtY(_offset.Y - extra), IndexAtY(_offset.Y + _viewport.Height + extra));
    }

    private (int Page, double RelY, double RelX)? CaptureAnchor()
    {
        if (_layout.Length == 0) return null;
        var page = IndexAtY(_offset.Y);
        var r = _layout[page];
        return (page, (_offset.Y - r.Y) / r.Height, (_offset.X + _viewport.Width / 2 - _xShift - r.X) / r.Width);
    }

    private void RestoreAnchor((int Page, double RelY, double RelX)? anchor)
    {
        if (anchor is not { } a || _layout.Length == 0) return;
        var r = _layout[Math.Min(a.Page, _layout.Length - 1)];
        SetOffsetCore(r.X + a.RelX * r.Width + _xShift - _viewport.Width / 2, r.Y + a.RelY * r.Height);
    }

    private double ComputeFitZoom(FitMode mode)
    {
        if (_document == null || _document.PageCount == 0 || _viewport.Width <= 0) return _zoom;
        var sizes = _document.PageSizes;
        var availableWidth = Math.Max(50, _viewport.Width - 2 * PageMargin);
        if (mode == FitMode.Width)
            return Math.Clamp(availableWidth / (sizes.Max(s => s.Width) * PointsToDip), MinZoom, MaxZoom);
        var page = sizes[Math.Clamp(CurrentPageIndex, 0, sizes.Count - 1)];
        var byWidth = availableWidth / (page.Width * PointsToDip);
        var byHeight = Math.Max(50, _viewport.Height - 2 * PageMargin) / (page.Height * PointsToDip);
        return Math.Clamp(Math.Min(byWidth, byHeight), MinZoom, MaxZoom);
    }

    private void ApplyFit()
    {
        if (_fitMode == FitMode.None || _document == null) return;
        var zoom = ComputeFitZoom(_fitMode);
        if (_fitMode == FitMode.Page)
        {
            var page = CurrentPageIndex;
            SetZoomCore(zoom, null);
            GoToPage(page);
        }
        else
        {
            SetZoomCore(zoom, null);
        }
    }

    private void SetZoomCore(double zoom, Point? anchor)
    {
        var a = anchor ?? new Point(_viewport.Width / 2, _viewport.Height / 2);
        (int Page, double Rx, double Ry)? hit = null;
        if (_layout.Length > 0)
        {
            var lx = a.X + _offset.X - _xShift;
            var ly = a.Y + _offset.Y;
            var page = IndexAtY(ly);
            var r = _layout[page];
            hit = (page, (lx - r.X) / r.Width, (ly - r.Y) / r.Height);
        }

        var changed = Math.Abs(zoom - _zoom) > 1e-6;
        _zoom = zoom;
        RebuildLayout();

        if (hit is { } h && _layout.Length > 0)
        {
            var r = _layout[h.Page];
            SetOffsetCore(r.X + h.Rx * r.Width + _xShift - a.X, r.Y + h.Ry * r.Height - a.Y);
        }
        _selectionCache.Clear();
        QueueUpdate();
        InvalidateVisual();
        if (changed) ZoomChanged?.Invoke(this, EventArgs.Empty);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCurrentPage()
    {
        if (_layout.Length == 0)
        {
            _currentPage = -1;
            return;
        }
        var index = IndexAtY(_offset.Y + _viewport.Height * 0.4);
        var atEnd = _offset.Y >= _extent.Height - _viewport.Height - 1;
        if (atEnd && _layout[^1].Y < _offset.Y + _viewport.Height) index = _layout.Length - 1;
        if (index == _currentPage) return;
        _currentPage = index;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize != _viewport)
        {
            var anchor = CaptureAnchor();
            _viewport = finalSize;
            if (_fitMode != FitMode.None && _document != null)
            {
                var zoom = ComputeFitZoom(_fitMode);
                if (Math.Abs(zoom - _zoom) > 1e-4)
                {
                    _zoom = zoom;
                    ZoomChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            RebuildLayout();
            RestoreAnchor(anchor);
            SetOffsetCore(_offset.X, _offset.Y, force: true);
        }
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        QueueUpdate();
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- IScrollInfo

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - 48);
    public void LineDown() => SetVerticalOffset(_offset.Y + 48);
    public void LineLeft() => SetHorizontalOffset(_offset.X - 48);
    public void LineRight() => SetHorizontalOffset(_offset.X + 48);
    public void PageUp() => SetVerticalOffset(_offset.Y - Math.Max(48, _viewport.Height - 48));
    public void PageDown() => SetVerticalOffset(_offset.Y + Math.Max(48, _viewport.Height - 48));
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width * 0.9);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width * 0.9);
    public void MouseWheelUp() => LineUp();
    public void MouseWheelDown() => LineDown();
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();
    public void SetHorizontalOffset(double offset) => SetOffsetCore(offset, _offset.Y);
    public void SetVerticalOffset(double offset) => SetOffsetCore(_offset.X, offset);
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    private void SetOffsetCore(double x, double y, bool force = false)
    {
        x = Math.Clamp(x, 0, Math.Max(0, _extent.Width - _viewport.Width));
        y = Math.Clamp(y, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (!force && x == _offset.X && y == _offset.Y) return;
        _offset = new Point(x, y);
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
        QueueUpdate();
        UpdateCurrentPage();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- rendering

    private void QueueUpdate()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateRenders);
    }

    private void UpdateRenders()
    {
        _updateQueued = false;
        var doc = _document;
        if (doc == null || _layout.Length == 0 || _viewport.Width <= 0 || _versions.Length != _layout.Length) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (first, last) = VisibleRange(0);
        var (nearFirst, nearLast) = VisibleRange(_viewport.Height * 0.75);

        for (var i = nearFirst; i <= nearLast; i++)
        {
            var visible = i >= first && i <= last;
            var layout = _layout[i];
            var pw = Math.Max(1, (int)Math.Round(layout.Width * dpi));
            var ph = Math.Max(1, (int)Math.Round(layout.Height * dpi));
            var pixels = (long)pw * ph;
            if (!_bitmaps.TryGetValue(i, out var entry)) _bitmaps[i] = entry = new PageBitmaps();

            var version = _versions[i];
            int fw = pw, fh = ph;
            if (pixels > MaxFullPixels)
            {
                var f = Math.Sqrt((double)BasePixels / pixels);
                fw = Math.Max(1, (int)(pw * f));
                fh = Math.Max(1, (int)(ph * f));
            }

            var haveFull = entry.FullW == fw && entry.FullH == fh && entry.FullVersion == version;
            var pendingFull = entry.PendingW == fw && entry.PendingH == fh && entry.PendingVersion == version;
            if (!haveFull && !pendingFull)
                RequestFull(doc, i, entry, fw, fh, version, visible ? 0 : 1);

            if (pixels > MaxFullPixels)
            {
                if (visible) RequestTile(doc, i, entry, pw, ph, version, dpi);
            }
            else
            {
                entry.Tile = null;
                entry.TileRequest = 0;
            }
        }

        foreach (var key in _bitmaps.Keys.Where(k => k < nearFirst - 2 || k > nearLast + 2).ToList())
        {
            var stale = _bitmaps[key];
            stale.PendingVersion = -2;
            stale.TileRequest = 0;
            _bitmaps.Remove(key);
        }
    }

    private void RequestFull(PdfDocument doc, int page, PageBitmaps entry, int w, int h, int version, int priority)
    {
        entry.PendingW = w;
        entry.PendingH = h;
        entry.PendingVersion = version;
        RenderService.Enqueue(new RenderRequest
        {
            Document = doc,
            PageIndex = page,
            PageWidth = w,
            PageHeight = h,
            Clip = new Int32Rect(0, 0, w, h),
            Priority = priority,
            IsStale = () => entry.PendingW != w || entry.PendingH != h || entry.PendingVersion != version || _document != doc,
            Completed = bitmap =>
            {
                if (doc != _document) return;
                var current = entry.PendingW == w && entry.PendingH == h && entry.PendingVersion == version;
                if (current)
                {
                    entry.PendingVersion = -1;
                    if (bitmap != null) entry.Full = bitmap;
                    entry.FullW = w;
                    entry.FullH = h;
                    entry.FullVersion = version;
                    InvalidateVisual();
                }
                else if (bitmap != null && entry.Full == null)
                {
                    entry.Full = bitmap;
                    InvalidateVisual();
                }
            },
        });
    }

    private void RequestTile(PdfDocument doc, int page, PageBitmaps entry, int pw, int ph, int version, double dpi)
    {
        var layout = _layout[page];
        var visible = new Rect(_offset.X - _xShift - layout.X, _offset.Y - layout.Y, _viewport.Width, _viewport.Height);
        visible.Intersect(new Rect(0, 0, layout.Width, layout.Height));
        if (visible.IsEmpty) return;
        var want = new Rect(visible.X * dpi, visible.Y * dpi, visible.Width * dpi, visible.Height * dpi);

        static bool Covers(Int32Rect r, Rect want) =>
            r.X <= want.X && r.Y <= want.Y && r.X + r.Width >= want.Right && r.Y + r.Height >= want.Bottom;

        if (entry.Tile != null && entry.TileW == pw && entry.TileH == ph && entry.TileVersion == version && Covers(entry.TileRect, want))
            return;
        if (entry.TileRequest != 0 && entry.PendingTileW == pw && entry.PendingTileH == ph && entry.PendingTileVersion == version &&
            Covers(entry.PendingTileRect, want))
            return;

        var pad = 240 * dpi;
        var left = Math.Max(0, Math.Floor(want.X - pad));
        var top = Math.Max(0, Math.Floor(want.Y - pad));
        var right = Math.Min(pw, Math.Ceiling(want.Right + pad));
        var bottom = Math.Min(ph, Math.Ceiling(want.Bottom + pad));
        var clip = new Int32Rect((int)left, (int)top, (int)(right - left), (int)(bottom - top));
        if (clip.Width <= 0 || clip.Height <= 0) return;

        var id = ++_tileCounter;
        entry.TileRequest = id;
        entry.PendingTileRect = clip;
        entry.PendingTileW = pw;
        entry.PendingTileH = ph;
        entry.PendingTileVersion = version;

        RenderService.Enqueue(new RenderRequest
        {
            Document = doc,
            PageIndex = page,
            PageWidth = pw,
            PageHeight = ph,
            Clip = clip,
            Priority = 0,
            IsStale = () => entry.TileRequest != id || _document != doc,
            Completed = bitmap =>
            {
                if (entry.TileRequest != id || doc != _document) return;
                entry.TileRequest = 0;
                if (bitmap != null)
                {
                    entry.Tile = bitmap;
                    entry.TileRect = clip;
                    entry.TileW = pw;
                    entry.TileH = ph;
                    entry.TileVersion = version;
                }
                InvalidateVisual();
            },
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_document == null || _layout.Length == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var (first, last) = VisibleRange(0);
        for (var i = first; i <= last; i++)
        {
            var layout = _layout[i];
            var pw = Math.Max(1, (int)Math.Round(layout.Width * dpi));
            var ph = Math.Max(1, (int)Math.Round(layout.Height * dpi));
            var x = Math.Round((layout.X - _offset.X + _xShift) * dpi) / dpi;
            var y = Math.Round((layout.Y - _offset.Y) * dpi) / dpi;
            var rect = new Rect(x, y, pw / dpi, ph / dpi);

            dc.DrawRectangle(ShadowBrush, null, new Rect(rect.X - 1, rect.Y, rect.Width + 2, rect.Height + 2));
            dc.DrawRectangle(Brushes.White, null, rect);

            if (_bitmaps.TryGetValue(i, out var entry))
            {
                if (entry.Full != null) dc.DrawImage(entry.Full, rect);
                if (entry.Tile != null && entry.TileW == pw && entry.TileH == ph)
                {
                    var t = entry.TileRect;
                    dc.DrawImage(entry.Tile, new Rect(rect.X + t.X / dpi, rect.Y + t.Y / dpi, t.Width / dpi, t.Height / dpi));
                }
            }

            DrawOverlays(dc, i, rect);
        }
    }

    private void DrawOverlays(DrawingContext dc, int page, Rect pageRect)
    {
        var hasHits = _hitsByPage.TryGetValue(page, out var hitIndices);
        var selection = HasSelection ? SelectionRects(page) : [];
        if (!hasHits && selection.Length == 0) return;

        var toDisplay = Matrices(page).ToDisplay;
        var scale = pageRect.Width / (_document!.PageSizes[page].Width);
        Rect ToViewport(Rect pdfRect)
        {
            var d = toDisplay.TransformBounds(pdfRect);
            return new Rect(pageRect.X + d.X * scale, pageRect.Y + d.Y * scale, d.Width * scale, d.Height * scale);
        }

        if (hasHits)
        {
            foreach (var hitIndex in hitIndices!)
            {
                var brush = hitIndex == _activeHit ? ActiveHitBrush : HitBrush;
                foreach (var r in _hits[hitIndex].Rects) dc.DrawRectangle(brush, null, ToViewport(r));
            }
        }
        foreach (var r in selection) dc.DrawRectangle(SelectionBrush, null, ToViewport(r));
    }

    // ---------------------------------------------------------------- input

    private static int Modifiers()
    {
        var modifiers = 0;
        var keys = Keyboard.Modifiers;
        if (keys.HasFlag(ModifierKeys.Shift)) modifiers |= FWL_EVENTFLAG_ShiftKey;
        if (keys.HasFlag(ModifierKeys.Control)) modifiers |= FWL_EVENTFLAG_ControlKey;
        if (keys.HasFlag(ModifierKeys.Alt)) modifiers |= FWL_EVENTFLAG_AltKey;
        return modifiers;
    }

    private static Cursor CursorFor(int formCursor) => formCursor switch
    {
        FXCT_VBEAM or FXCT_HBEAM => Cursors.IBeam,
        FXCT_HAND => Cursors.Hand,
        FXCT_NESW => Cursors.SizeNESW,
        FXCT_NWSE => Cursors.SizeNWSE,
        _ => Cursors.Arrow,
    };

    private Point DisplayOnPage(int page, Point viewportPoint)
    {
        var r = PageRect(page);
        return new Point((viewportPoint.X - r.X) / Scale, (viewportPoint.Y - r.Y) / Scale);
    }

    private void BeginPan(Point p)
    {
        _drag = DragMode.Pan;
        _dragStart = p;
        _panOrigin = _offset;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
    }

    /// <summary>
    /// Called for a left click on a page (not on a form field) before links and text selection.
    /// Arguments are the hit and the click count; return true to consume the click.
    /// </summary>
    public Func<PageHit, int, bool>? PreviewPageClick { get; set; }

    /// <summary>
    /// Raised when the user tries to select text on a page whose own text layer can't be read. The host is
    /// expected to run OCR for that page; until it does, there is nothing meaningful to select.
    /// </summary>
    public event EventHandler<int>? TextRecoveryNeeded;

    /// <summary>Wheel handling shared with overlays: Ctrl zooms at the pointer, Shift scrolls sideways.</summary>
    public void ScrollByWheel(int delta, Point position)
    {
        if (_document == null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            SetZoom(_zoom * Math.Pow(1.0015, delta), position);
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            SetHorizontalOffset(_offset.X - delta * 0.5);
        else
            SetVerticalOffset(_offset.Y - delta * 0.5);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_document == null) return;
        ScrollByWheel(e.Delta, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_document == null) return;
        var p = e.GetPosition(this);

        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && (Tool == ViewTool.Hand || Keyboard.IsKeyDown(Key.Space)) && !PlacementMode))
        {
            BeginPan(p);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        var hit = HitTest(p);
        if (PlacementMode)
        {
            if (hit is { } placement) PlacementRequested?.Invoke(this, placement);
            e.Handled = true;
            return;
        }

        if (hit is not { } h)
        {
            ClearSelection();
            if (_document.HasFormFocus) _document.FormKillFocus();
            BeginPan(p);
            e.Handled = true;
            return;
        }

        var pagePoint = DisplayToPage(h.PageIndex, h.Display);
        if (_document.HasForms && _document.FormFieldTypeAt(h.PageIndex, pagePoint) >= 0)
        {
            ClearSelection();
            _document.FormMouseDown(h.PageIndex, pagePoint, Modifiers(), e.ClickCount == 2);
            _drag = DragMode.Form;
            _formPage = h.PageIndex;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (_document.HasFormFocus) _document.FormKillFocus();

        if (PreviewPageClick?.Invoke(h, e.ClickCount) == true)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }

        if (_document.LinkAt(h.PageIndex, pagePoint) is { } link)
        {
            _pressedLink = link;
            _drag = DragMode.Link;
            _dragStart = p;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // A page whose text layer is unusable has to be read by OCR before selection means anything. Pan for now:
        // the host starts the recovery and the drag works on the next attempt.
        if (Tool == ViewTool.Select && _document.NeedsTextRecovery(h.PageIndex))
        {
            TextRecoveryNeeded?.Invoke(this, h.PageIndex);
            ClearSelection();
            BeginPan(p);
            e.Handled = true;
            return;
        }

        var charIndex = _document.CharIndexAt(h.PageIndex, pagePoint, 5);
        if (charIndex >= 0)
        {
            if (e.ClickCount == 2)
            {
                var (start, count) = _document.WordAt(h.PageIndex, charIndex);
                SetSelection((h.PageIndex, start), (h.PageIndex, start + count - 1));
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _selAnchor != null)
                SetSelection(_selAnchor.Value, (h.PageIndex, charIndex));
            else
                SetSelection((h.PageIndex, charIndex), null);
            _drag = DragMode.Select;
            _dragStart = p;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        ClearSelection();
        BeginPan(p);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_document == null) return;
        var p = e.GetPosition(this);

        switch (_drag)
        {
            case DragMode.Pan:
                SetOffsetCore(_panOrigin.X - (p.X - _dragStart.X), _panOrigin.Y - (p.Y - _dragStart.Y));
                return;
            case DragMode.Form:
                _document.FormMouseMove(_formPage, DisplayToPage(_formPage, DisplayOnPage(_formPage, p)), Modifiers());
                Cursor = CursorFor(_document.FormCursor);
                return;
            case DragMode.Link:
                return;
            case DragMode.Select:
                if (p.Y < 0) SetVerticalOffset(_offset.Y + p.Y / 2);
                else if (p.Y > _viewport.Height) SetVerticalOffset(_offset.Y + (p.Y - _viewport.Height) / 2);
                if (HitTest(p, nearest: true) is { } s && _selAnchor != null)
                {
                    var index = _document.CharIndexAt(s.PageIndex, DisplayToPage(s.PageIndex, s.Display), 40);
                    if (index >= 0 && (p - _dragStart).Length > 3) SetSelection(_selAnchor.Value, (s.PageIndex, index));
                }
                return;
        }

        if (Math.Abs(p.X - _lastHover.X) < 1 && Math.Abs(p.Y - _lastHover.Y) < 1) return;
        _lastHover = p;
        UpdateHoverCursor(p);
    }

    private void UpdateHoverCursor(Point p)
    {
        if (_document == null) return;
        if (PlacementMode)
        {
            Cursor = HitTest(p) != null ? Cursors.Cross : Cursors.No;
            return;
        }
        if (Tool == ViewTool.Hand)
        {
            Cursor = Cursors.SizeAll;
            return;
        }
        if (HitTest(p) is not { } h)
        {
            Cursor = Cursors.Arrow;
            return;
        }
        var pagePoint = DisplayToPage(h.PageIndex, h.Display);
        if (_document.HasForms && _document.FormFieldTypeAt(h.PageIndex, pagePoint) >= 0)
        {
            _document.FormMouseMove(h.PageIndex, pagePoint, Modifiers());
            Cursor = CursorFor(_document.FormCursor);
            return;
        }
        if (_document.LinkAt(h.PageIndex, pagePoint) != null)
        {
            Cursor = Cursors.Hand;
            return;
        }
        Cursor = _document.CharIndexAt(h.PageIndex, pagePoint, 2) >= 0 ? Cursors.IBeam : Cursors.Arrow;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var p = e.GetPosition(this);
        var mode = _drag;
        _drag = DragMode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_document == null) return;

        switch (mode)
        {
            case DragMode.Form:
                _document.FormMouseUp(_formPage, DisplayToPage(_formPage, DisplayOnPage(_formPage, p)), Modifiers());
                break;
            case DragMode.Link:
                if ((p - _dragStart).Length <= 4 && _pressedLink != null) LinkActivated?.Invoke(this, _pressedLink);
                _pressedLink = null;
                break;
            case DragMode.Select:
                if (_selFocus == null) ClearSelection();
                break;
        }
        _lastHover = new Point(double.NaN, double.NaN);
        UpdateHoverCursor(p);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is DragMode.Pan or DragMode.Select) _drag = DragMode.None;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_document == null || e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (_document.HasFormFocus)
        {
            if (key == Key.Escape)
            {
                _document.FormKillFocus();
                e.Handled = true;
                return;
            }
            if (ctrl)
            {
                switch (key)
                {
                    case Key.C: CopySelection(); break;
                    case Key.X:
                        if (CopySelection()) _document.FormReplaceSelection("");
                        break;
                    case Key.V:
                        try { if (Clipboard.ContainsText()) _document.FormReplaceSelection(Clipboard.GetText()); } catch { }
                        break;
                    case Key.A: _document.FormSelectAll(); break;
                    case Key.Z: _document.FormUndo(); break;
                    case Key.Y: _document.FormRedo(); break;
                    default: return;
                }
                e.Handled = true;
                return;
            }
            if (key is >= Key.F1 and <= Key.F24) return;

            _document.FormKeyDown(KeyInterop.VirtualKeyFromKey(key), Modifiers());
            if (key == Key.Back) _document.FormChar(8, 0);
            else if (key == Key.Enter) _document.FormChar(13, 0);
            e.Handled = true;
            return;
        }

        if (ctrl)
        {
            if (key == Key.C && CopySelection()) e.Handled = true;
            else if (key == Key.A)
            {
                SelectAllOnPage(CurrentPageIndex);
                e.Handled = true;
            }
            return;
        }

        switch (key)
        {
            case Key.Down: LineDown(); break;
            case Key.Up: LineUp(); break;
            case Key.Left: LineLeft(); break;
            case Key.Right: LineRight(); break;
            case Key.PageDown: PageDown(); break;
            case Key.PageUp: PageUp(); break;
            case Key.Space:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) PageUp(); else PageDown();
                break;
            case Key.Home: GoToPage(0); break;
            case Key.End: GoToPage(_layout.Length - 1); break;
            case Key.Escape: ClearSelection(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Keep Tab inside form fields instead of moving WPF keyboard focus.
        if (_document?.HasFormFocus == true && e.Key == Key.Tab)
        {
            _document.FormKeyDown(KeyInterop.VirtualKeyFromKey(Key.Tab), Modifiers());
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (_document?.HasFormFocus != true || string.IsNullOrEmpty(e.Text)) return;
        foreach (var ch in e.Text)
            if (ch >= 0x20 && ch != 0x7F) _document.FormChar(ch, 0);
        e.Handled = true;
    }
}
