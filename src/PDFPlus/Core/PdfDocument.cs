using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDFPlus.Native;
using static PDFPlus.Native.Pdfium;

namespace PDFPlus.Core;

public readonly record struct PageSize(double Width, double Height);

/// <summary>A search match. Rects are in PDF page space (points, origin bottom-left).</summary>
public sealed record SearchHit(int PageIndex, int CharIndex, int CharCount, Rect[] Rects);

/// <summary>Where a link or bookmark points. Location is in PDF page space when known.</summary>
public sealed record LinkTarget(int PageIndex, Point? Location, string? Uri);

public sealed class Bookmark(string title, LinkTarget? target)
{
    public string Title { get; } = title;
    public LinkTarget? Target { get; } = target;
    public List<Bookmark> Children { get; } = new();
}

/// <summary>One line of text to stamp, positioned in display points (origin top-left of the page as shown).</summary>
public readonly record struct TextRun(string Text, double X, double Baseline);

public sealed record PathFigureData(Point[] Points, bool Closed);

/// <summary>
/// An open PDF: rendering, text, links, form filling, page editing, saving and undo.
/// All public members are UI-thread friendly; <see cref="Render"/> is also safe from the render thread.
/// "Display space" means points with the origin at the top-left of the page after /Rotate is applied.
/// </summary>
public sealed unsafe partial class PdfDocument : IDisposable
{
    private const int MaxOpenPages = 24;
    private const int MaxUndoSteps = 40;
    private const long MaxUndoBytes = 768L * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct FormHost
    {
        public FPDF_FORMFILLINFO Info;
        public IntPtr Owner;
    }

    private static int _untitledCounter;

    private readonly Dispatcher _dispatcher;
    private readonly string _untitledName;
    private readonly string? _password;
    private PdfFile _file;
    private IntPtr _form;
    private FormHost* _formHost;
    private GCHandle _self;
    private volatile bool _disposed;

    private readonly Dictionary<int, IntPtr> _pages = new();
    private readonly Dictionary<IntPtr, int> _pageIndices = new();
    private readonly List<int> _pageLru = new();
    private readonly Dictionary<int, IntPtr> _textPages = new();
    private readonly Dictionary<int, Affine> _pageToDisplay = new();
    private PageSize[] _sizes = [];
    private int _formFocusPage = -1;
    private volatile int _formCursor;

    private readonly List<byte[]> _undo = new();
    private readonly List<byte[]> _redo = new();

    public event EventHandler? PagesChanged;
    public event EventHandler<int>? PageContentChanged;
    public event EventHandler? StateChanged;
    public event EventHandler<LinkTarget>? NavigateRequested;
    public event EventHandler<string>? NamedActionRequested;

    public string? FilePath { get; private set; }
    public string Title => FilePath != null ? Path.GetFileName(FilePath) : _untitledName;
    public bool IsDirty { get; private set; }
    public bool HasForms { get; private set; }
    public int PageCount => _sizes.Length;
    public IReadOnlyList<PageSize> PageSizes => _sizes;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool HasFormFocus => _formFocusPage >= 0;
    public int FormCursor => _formCursor;

    private PdfDocument(PdfFile file, string? path, string? password)
    {
        _dispatcher = Application.Current.Dispatcher;
        _file = file;
        _password = password;
        FilePath = path;
        _untitledName = $"Untitled {++_untitledCounter}.pdf";
        _self = GCHandle.Alloc(this);
        lock (PdfLibrary.Sync)
        {
            AttachFormLocked();
            ReadPageSizesLocked();
        }
    }

    /// <summary>Reads and parses the file off the UI thread, then finishes setup on the calling (UI) thread.</summary>
    public static Task<PdfDocument> OpenAsync(string path, string? password)
    {
        var fullPath = Path.GetFullPath(path);
        // No 'await' here: this class is unsafe, and C# disallows await in unsafe contexts.
        // GetAwaiter().GetResult() rethrows the original exception (not an AggregateException).
        return Task.Run(() => PdfFile.Load(fullPath, password)).ContinueWith(
            loaded => new PdfDocument(loaded.GetAwaiter().GetResult(), fullPath, password),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Creates a new untitled document built from the given files, in order.</summary>
    public static PdfDocument Combine(IEnumerable<(string Path, string? Password)> sources)
    {
        var doc = new PdfDocument(PdfFile.CreateEmpty(), null, null);
        try
        {
            foreach (var (path, password) in sources)
            {
                using var source = PdfFile.Load(path, password);
                lock (PdfLibrary.Sync)
                {
                    if (FPDF_ImportPagesByIndex(doc._file.Handle, source.Handle, null, 0, doc.PageCount) == 0)
                        throw new PdfOpenException($"Could not copy pages from {System.IO.Path.GetFileName(path)}.");
                    doc.ReadPageSizesLocked();
                }
            }
        }
        catch
        {
            doc.Dispose();
            throw;
        }
        doc.IsDirty = true;
        return doc;
    }

    // ---------------------------------------------------------------- form fill environment

    private void AttachFormLocked()
    {
        _formHost = (FormHost*)NativeMemory.AllocZeroed((nuint)sizeof(FormHost));
        _formHost->Info.version = 1;
        _formHost->Info.FFI_Invalidate = &OnFormInvalidate;
        _formHost->Info.FFI_SetCursor = &OnFormSetCursor;
        _formHost->Info.FFI_OnChange = &OnFormChange;
        _formHost->Info.FFI_ExecuteNamedAction = &OnFormNamedAction;
        _formHost->Info.FFI_DoURIAction = &OnFormUriAction;
        _formHost->Info.FFI_DoGoToAction = &OnFormGoToAction;
        _formHost->Owner = GCHandle.ToIntPtr(_self);

        _form = FPDFDOC_InitFormFillEnvironment(_file.Handle, &_formHost->Info);
        HasForms = _form != IntPtr.Zero && FPDF_GetFormType(_file.Handle) != 0;
        if (_form != IntPtr.Zero)
        {
            // PDFium takes a Windows COLORREF (0x00BBGGRR): this is a light blue, RGB(214, 226, 255).
            FPDF_SetFormFieldHighlightColor(_form, 0, 0xFFE2D6);
            FPDF_SetFormFieldHighlightAlpha(_form, 110);
        }
    }

    private void DetachFormLocked()
    {
        CloseAllPagesLocked();
        if (_form != IntPtr.Zero) FPDFDOC_ExitFormFillEnvironment(_form);
        _form = IntPtr.Zero;
        if (_formHost != null) NativeMemory.Free(_formHost);
        _formHost = null;
    }

    private static PdfDocument? OwnerOf(FPDF_FORMFILLINFO* info) =>
        GCHandle.FromIntPtr(((FormHost*)info)->Owner).Target as PdfDocument;

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    [UnmanagedCallersOnly]
    private static void OnFormInvalidate(FPDF_FORMFILLINFO* info, IntPtr page, double left, double top, double right, double bottom)
    {
        try
        {
            var doc = OwnerOf(info);
            if (doc != null && doc._pageIndices.TryGetValue(page, out var index))
                doc.Post(() => { if (!doc._disposed) doc.PageContentChanged?.Invoke(doc, index); });
        }
        catch { /* never let exceptions cross into native code */ }
    }

    [UnmanagedCallersOnly]
    private static void OnFormSetCursor(FPDF_FORMFILLINFO* info, int cursorType)
    {
        try { if (OwnerOf(info) is { } doc) doc._formCursor = cursorType; } catch { }
    }

    [UnmanagedCallersOnly]
    private static void OnFormChange(FPDF_FORMFILLINFO* info)
    {
        try { if (OwnerOf(info) is { } doc) doc.Post(doc.MarkDirty); } catch { }
    }

    [UnmanagedCallersOnly]
    private static void OnFormNamedAction(FPDF_FORMFILLINFO* info, byte* name)
    {
        try
        {
            if (OwnerOf(info) is { } doc && Marshal.PtrToStringUTF8((IntPtr)name) is { } action)
                doc.Post(() => doc.NamedActionRequested?.Invoke(doc, action));
        }
        catch { }
    }

    [UnmanagedCallersOnly]
    private static void OnFormUriAction(FPDF_FORMFILLINFO* info, byte* uri)
    {
        try
        {
            if (OwnerOf(info) is { } doc && Marshal.PtrToStringUTF8((IntPtr)uri) is { } target)
                doc.Post(() => doc.NavigateRequested?.Invoke(doc, new LinkTarget(-1, null, target)));
        }
        catch { }
    }

    [UnmanagedCallersOnly]
    private static void OnFormGoToAction(FPDF_FORMFILLINFO* info, int pageIndex, int zoomMode, float* positions, int count)
    {
        try
        {
            if (OwnerOf(info) is { } doc)
                doc.Post(() => doc.NavigateRequested?.Invoke(doc, new LinkTarget(pageIndex, null, null)));
        }
        catch { }
    }

    // ---------------------------------------------------------------- page handles

    private IntPtr PageLocked(int index)
    {
        if (_disposed || (uint)index >= (uint)_sizes.Length) return IntPtr.Zero;
        if (_pages.TryGetValue(index, out var page))
        {
            _pageLru.Remove(index);
            _pageLru.Add(index);
            return page;
        }

        page = FPDF_LoadPage(_file.Handle, index);
        if (page == IntPtr.Zero) return IntPtr.Zero;
        if (_form != IntPtr.Zero) FORM_OnAfterLoadPage(page, _form);
        _pages[index] = page;
        _pageIndices[page] = index;
        _pageLru.Add(index);

        for (var i = 0; _pages.Count > MaxOpenPages && i < _pageLru.Count;)
        {
            var victim = _pageLru[i];
            if (victim == index || victim == _formFocusPage) { i++; continue; }
            ClosePageLocked(victim);
        }
        return page;
    }

    private IntPtr TextPageLocked(int index)
    {
        if (_textPages.TryGetValue(index, out var textPage)) return textPage;
        var page = PageLocked(index);
        if (page == IntPtr.Zero) return IntPtr.Zero;
        textPage = FPDFText_LoadPage(page);
        if (textPage != IntPtr.Zero) _textPages[index] = textPage;
        return textPage;
    }

    private void ClosePageLocked(int index)
    {
        if (_textPages.Remove(index, out var textPage)) FPDFText_ClosePage(textPage);
        if (!_pages.Remove(index, out var page)) return;
        _pageIndices.Remove(page);
        _pageLru.Remove(index);
        if (_form != IntPtr.Zero) FORM_OnBeforeClosePage(page, _form);
        FPDF_ClosePage(page);
    }

    private void CloseAllPagesLocked()
    {
        if (_form != IntPtr.Zero) FORM_ForceToKillFocus(_form);
        _formFocusPage = -1;
        foreach (var index in _pages.Keys.ToList()) ClosePageLocked(index);
        _pageToDisplay.Clear();
    }

    private void ReadPageSizesLocked()
    {
        var count = Math.Max(0, FPDF_GetPageCount(_file.Handle));
        var sizes = new PageSize[count];
        FS_SIZEF size;
        for (var i = 0; i < count; i++)
        {
            sizes[i] = FPDF_GetPageSizeByIndexF(_file.Handle, i, &size) != 0 && size.Width >= 1 && size.Height >= 1
                ? new PageSize(size.Width, size.Height)
                : new PageSize(612, 792);
        }
        _sizes = sizes;
        _pageToDisplay.Clear();
    }

    /// <summary>Maps PDF page space to display space (points, top-left origin, rotation applied).</summary>
    public Affine GetPageToDisplay(int index)
    {
        lock (PdfLibrary.Sync) return PageToDisplayLocked(index);
    }

    private Affine PageToDisplayLocked(int index)
    {
        if (_pageToDisplay.TryGetValue(index, out var matrix)) return matrix;
        if ((uint)index >= (uint)_sizes.Length) return Affine.Identity;
        var size = _sizes[index];
        var page = PageLocked(index);
        if (page == IntPtr.Zero) return new Affine(1, 0, 0, -1, 0, size.Height);

        // Probe PDFium's own device mapping at 1/64 pt resolution; this honours CropBox offsets and /Rotate.
        const double k = 64, span = 1000;
        int sx = (int)Math.Round(size.Width * k), sy = (int)Math.Round(size.Height * k);
        int x0, y0, x1, y1, x2, y2;
        FPDF_PageToDevice(page, 0, 0, sx, sy, 0, 0, 0, &x0, &y0);
        FPDF_PageToDevice(page, 0, 0, sx, sy, 0, span, 0, &x1, &y1);
        FPDF_PageToDevice(page, 0, 0, sx, sy, 0, 0, span, &x2, &y2);
        matrix = new Affine(
            (x1 - x0) / (span * k), (y1 - y0) / (span * k),
            (x2 - x0) / (span * k), (y2 - y0) / (span * k),
            x0 / k, y0 / k);
        _pageToDisplay[index] = matrix;
        return matrix;
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Renders the part of a page (scaled to pageWidth x pageHeight pixels) inside clip.</summary>
    public BitmapSource? Render(int index, int pageWidth, int pageHeight, Int32Rect clip)
    {
        if (clip.Width <= 0 || clip.Height <= 0 || pageWidth <= 0 || pageHeight <= 0) return null;
        var stride = clip.Width * 4;
        var bytes = (long)stride * clip.Height;
        if (bytes > int.MaxValue) return null;

        var buffer = NativeMemory.Alloc((nuint)bytes);
        try
        {
            lock (PdfLibrary.Sync)
            {
                var page = PageLocked(index);
                if (page == IntPtr.Zero) return null;
                var bitmap = FPDFBitmap_CreateEx(clip.Width, clip.Height, FPDFBitmap_BGRx, (IntPtr)buffer, stride);
                if (bitmap == IntPtr.Zero) return null;
                FPDFBitmap_FillRect(bitmap, 0, 0, clip.Width, clip.Height, 0xFFFFFFFF);
                FPDF_RenderPageBitmap(bitmap, page, -clip.X, -clip.Y, pageWidth, pageHeight, 0, FPDF_ANNOT);
                if (_form != IntPtr.Zero)
                    FPDF_FFLDraw(_form, bitmap, page, -clip.X, -clip.Y, pageWidth, pageHeight, 0, FPDF_ANNOT);
                FPDFBitmap_Destroy(bitmap);
            }

            var source = BitmapSource.Create(clip.Width, clip.Height, 96, 96, PixelFormats.Bgr32, null, (IntPtr)buffer, (int)bytes, stride);
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    // ---------------------------------------------------------------- text

    public int CharCount(int index)
    {
        lock (PdfLibrary.Sync)
        {
            var textPage = TextPageLocked(index);
            return textPage == IntPtr.Zero ? 0 : FPDFText_CountChars(textPage);
        }
    }

    public int CharIndexAt(int index, Point pagePoint, double tolerance)
    {
        lock (PdfLibrary.Sync)
        {
            var textPage = TextPageLocked(index);
            if (textPage == IntPtr.Zero) return -1;
            var result = FPDFText_GetCharIndexAtPos(textPage, pagePoint.X, pagePoint.Y, tolerance, tolerance);
            return result < 0 ? -1 : result;
        }
    }

    public Rect[] TextRects(int index, int start, int count)
    {
        lock (PdfLibrary.Sync)
        {
            var textPage = TextPageLocked(index);
            return textPage == IntPtr.Zero || count <= 0 ? [] : RectsLocked(textPage, start, count);
        }
    }

    private static Rect[] RectsLocked(IntPtr textPage, int start, int count)
    {
        var n = FPDFText_CountRects(textPage, start, count);
        if (n <= 0) return [];
        var rects = new Rect[n];
        double left, top, right, bottom;
        for (var i = 0; i < n; i++)
        {
            FPDFText_GetRect(textPage, i, &left, &top, &right, &bottom);
            rects[i] = new Rect(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(top - bottom));
        }
        return rects;
    }

    public string GetText(int index, int start, int count)
    {
        if (count <= 0) return "";
        lock (PdfLibrary.Sync)
        {
            var textPage = TextPageLocked(index);
            if (textPage == IntPtr.Zero) return "";
            var buffer = new ushort[count + 1];
            fixed (ushort* p = buffer)
            {
                var written = FPDFText_GetText(textPage, start, count, p);
                return written <= 1 ? "" : new string((char*)p, 0, written - 1);
            }
        }
    }

    /// <summary>Expands a character index to the surrounding word.</summary>
    public (int Start, int Count) WordAt(int index, int charIndex)
    {
        lock (PdfLibrary.Sync)
        {
            var textPage = TextPageLocked(index);
            if (textPage == IntPtr.Zero) return (charIndex, 1);
            var total = FPDFText_CountChars(textPage);
            bool IsWord(int i) => i >= 0 && i < total && FPDFText_GetUnicode(textPage, i) is var c && c < 0x10000 && char.IsLetterOrDigit((char)c);
            if (!IsWord(charIndex)) return (charIndex, 1);
            int start = charIndex, end = charIndex;
            while (IsWord(start - 1)) start--;
            while (IsWord(end + 1)) end++;
            return (start, end - start + 1);
        }
    }

    public List<SearchHit> Search(string query, bool matchCase, bool wholeWord, CancellationToken token, Action<int>? pageDone = null)
    {
        var hits = new List<SearchHit>();
        var flags = (matchCase ? FPDF_MATCHCASE : 0) | (wholeWord ? FPDF_MATCHWHOLEWORD : 0);
        var count = _sizes.Length;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            lock (PdfLibrary.Sync)
            {
                if (_disposed || i >= _sizes.Length) break;
                var textPage = TextPageLocked(i);
                if (textPage == IntPtr.Zero) continue;
                var handle = FPDFText_FindStart(textPage, query, flags, 0);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    while (FPDFText_FindNext(handle) != 0)
                    {
                        var start = FPDFText_GetSchResultIndex(handle);
                        var length = FPDFText_GetSchCount(handle);
                        hits.Add(new SearchHit(i, start, length, RectsLocked(textPage, start, length)));
                    }
                }
                finally
                {
                    FPDFText_FindClose(handle);
                }
            }
            pageDone?.Invoke(i);
        }
        return hits;
    }

    // ---------------------------------------------------------------- links & bookmarks

    public LinkTarget? LinkAt(int index, Point pagePoint)
    {
        lock (PdfLibrary.Sync)
        {
            var page = PageLocked(index);
            if (page == IntPtr.Zero) return null;
            var link = FPDFLink_GetLinkAtPoint(page, pagePoint.X, pagePoint.Y);
            if (link == IntPtr.Zero) return null;
            var dest = FPDFLink_GetDest(_file.Handle, link);
            if (dest != IntPtr.Zero) return DestTargetLocked(dest);
            var action = FPDFLink_GetAction(link);
            return action == IntPtr.Zero ? null : ActionTargetLocked(action);
        }
    }

    private LinkTarget? ActionTargetLocked(IntPtr action)
    {
        switch (FPDFAction_GetType(action))
        {
            case PDFACTION_GOTO:
                var dest = FPDFAction_GetDest(_file.Handle, action);
                return dest == IntPtr.Zero ? null : DestTargetLocked(dest);
            case PDFACTION_URI:
                var length = FPDFAction_GetURIPath(_file.Handle, action, null, 0);
                if (length <= 1) return null;
                var buffer = new byte[length];
                fixed (byte* p = buffer) FPDFAction_GetURIPath(_file.Handle, action, p, length);
                return new LinkTarget(-1, null, Encoding.UTF8.GetString(buffer, 0, (int)length - 1));
            default:
                return null;
        }
    }

    private LinkTarget? DestTargetLocked(IntPtr dest)
    {
        var pageIndex = FPDFDest_GetDestPageIndex(_file.Handle, dest);
        if (pageIndex < 0) return null;
        int hasX, hasY, hasZoom;
        float x, y, zoom;
        Point? location = null;
        if (FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZoom, &x, &y, &zoom) != 0 && hasY != 0)
            location = new Point(hasX != 0 ? x : 0, y);
        return new LinkTarget(pageIndex, location, null);
    }

    public List<Bookmark> GetBookmarks()
    {
        var result = new List<Bookmark>();
        lock (PdfLibrary.Sync)
        {
            if (!_disposed) ReadBookmarksLocked(IntPtr.Zero, result, new HashSet<IntPtr>(), 0);
        }
        return result;
    }

    private void ReadBookmarksLocked(IntPtr parent, List<Bookmark> into, HashSet<IntPtr> seen, int depth)
    {
        if (depth > 32) return;
        var doc = _file.Handle;
        for (var bookmark = FPDFBookmark_GetFirstChild(doc, parent);
             bookmark != IntPtr.Zero && seen.Count < 20000 && seen.Add(bookmark);
             bookmark = FPDFBookmark_GetNextSibling(doc, bookmark))
        {
            var title = "";
            var length = FPDFBookmark_GetTitle(bookmark, null, 0);
            if (length > 2)
            {
                var buffer = new byte[length];
                fixed (byte* p = buffer) FPDFBookmark_GetTitle(bookmark, p, length);
                title = Encoding.Unicode.GetString(buffer, 0, (int)length - 2).Trim();
            }

            LinkTarget? target = null;
            var dest = FPDFBookmark_GetDest(doc, bookmark);
            if (dest != IntPtr.Zero) target = DestTargetLocked(dest);
            else if (FPDFBookmark_GetAction(bookmark) is var action && action != IntPtr.Zero) target = ActionTargetLocked(action);

            var item = new Bookmark(title, target);
            into.Add(item);
            ReadBookmarksLocked(bookmark, item.Children, seen, depth + 1);
        }
    }

    // ---------------------------------------------------------------- form interaction (UI thread)

    public int FormFieldTypeAt(int index, Point pagePoint)
    {
        if (!HasForms) return -1;
        lock (PdfLibrary.Sync)
        {
            var page = PageLocked(index);
            return page == IntPtr.Zero || _form == IntPtr.Zero ? -1 : FPDFPage_HasFormFieldAtPoint(_form, page, pagePoint.X, pagePoint.Y);
        }
    }

    public bool FormMouseMove(int index, Point pagePoint, int modifiers)
    {
        lock (PdfLibrary.Sync)
        {
            var page = PageLocked(index);
            return page != IntPtr.Zero && _form != IntPtr.Zero && FORM_OnMouseMove(_form, page, modifiers, pagePoint.X, pagePoint.Y) != 0;
        }
    }

    public bool FormMouseDown(int index, Point pagePoint, int modifiers, bool doubleClick)
    {
        lock (PdfLibrary.Sync)
        {
            var page = PageLocked(index);
            if (page == IntPtr.Zero || _form == IntPtr.Zero) return false;
            if (_formFocusPage >= 0 && _formFocusPage != index) FORM_ForceToKillFocus(_form);
            _formFocusPage = index;
            return doubleClick
                ? FORM_OnLButtonDoubleClick(_form, page, modifiers, pagePoint.X, pagePoint.Y) != 0
                : FORM_OnLButtonDown(_form, page, modifiers, pagePoint.X, pagePoint.Y) != 0;
        }
    }

    public bool FormMouseUp(int index, Point pagePoint, int modifiers)
    {
        lock (PdfLibrary.Sync)
        {
            var page = PageLocked(index);
            return page != IntPtr.Zero && _form != IntPtr.Zero && FORM_OnLButtonUp(_form, page, modifiers, pagePoint.X, pagePoint.Y) != 0;
        }
    }

    private bool WithFocusedFormPage(Func<IntPtr, bool> action)
    {
        if (_formFocusPage < 0 || _form == IntPtr.Zero) return false;
        lock (PdfLibrary.Sync)
        {
            var page = PageLocked(_formFocusPage);
            return page != IntPtr.Zero && action(page);
        }
    }

    public bool FormKeyDown(int virtualKey, int modifiers) =>
        WithFocusedFormPage(page => FORM_OnKeyDown(_form, page, virtualKey, modifiers) != 0);

    public bool FormChar(int character, int modifiers) =>
        WithFocusedFormPage(page => FORM_OnChar(_form, page, character, modifiers) != 0);

    public bool FormSelectAll() => WithFocusedFormPage(page => FORM_SelectAllText(_form, page) != 0);
    public bool FormUndo() => WithFocusedFormPage(page => FORM_Undo(_form, page) != 0);
    public bool FormRedo() => WithFocusedFormPage(page => FORM_Redo(_form, page) != 0);

    public bool FormReplaceSelection(string text) =>
        WithFocusedFormPage(page => { FORM_ReplaceSelection(_form, page, text); return true; });

    public string FormSelectedText()
    {
        var text = "";
        WithFocusedFormPage(page =>
        {
            var length = FORM_GetSelectedText(_form, page, null, 0);
            if (length <= 2) return false;
            var buffer = new byte[length];
            fixed (byte* p = buffer) FORM_GetSelectedText(_form, page, p, length);
            text = Encoding.Unicode.GetString(buffer, 0, (int)length - 2);
            return true;
        });
        return text;
    }

    public void FormKillFocus()
    {
        if (_form == IntPtr.Zero) return;
        lock (PdfLibrary.Sync) FORM_ForceToKillFocus(_form);
        _formFocusPage = -1;
    }

    // ---------------------------------------------------------------- page operations

    private void ApplyStructural(Action<IntPtr> operation)
    {
        PushUndo();
        try
        {
            lock (PdfLibrary.Sync)
            {
                CloseAllPagesLocked();
                try { operation(_file.Handle); }
                finally { ReadPageSizesLocked(); }
            }
        }
        finally
        {
            MarkDirty();
            PagesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RotatePages(IEnumerable<int> indices, int quarterTurns)
    {
        var pages = indices.Where(i => (uint)i < (uint)PageCount).Distinct().ToArray();
        if (pages.Length == 0) return;
        ApplyStructural(doc =>
        {
            foreach (var i in pages)
            {
                var page = FPDF_LoadPage(doc, i);
                if (page == IntPtr.Zero) continue;
                FPDFPage_SetRotation(page, ((FPDFPage_GetRotation(page) + quarterTurns) % 4 + 4) % 4);
                FPDF_ClosePage(page);
            }
        });
    }

    public void DeletePages(IEnumerable<int> indices)
    {
        var pages = indices.Where(i => (uint)i < (uint)PageCount).Distinct().OrderByDescending(i => i).ToArray();
        if (pages.Length == 0 || pages.Length >= PageCount) return;
        ApplyStructural(doc => { foreach (var i in pages) FPDFPage_Delete(doc, i); });
    }

    /// <summary>Moves pages so they end up just before the page currently at <paramref name="insertBefore"/> (PageCount = end).</summary>
    public void MovePages(IEnumerable<int> indices, int insertBefore)
    {
        var pages = indices.Where(i => (uint)i < (uint)PageCount).Distinct().OrderBy(i => i).ToArray();
        if (pages.Length == 0) return;
        var dest = Math.Clamp(insertBefore - pages.Count(i => i < insertBefore), 0, PageCount - pages.Length);
        var alreadyThere = pages.Select((p, k) => p == dest + k).All(x => x);
        if (alreadyThere) return;
        ApplyStructural(doc =>
        {
            fixed (int* p = pages)
                if (FPDF_MovePages(doc, p, (uint)pages.Length, dest) == 0)
                    throw new InvalidOperationException("Pages could not be moved.");
        });
    }

    public void InsertBlankPage(int index, double width, double height)
    {
        index = Math.Clamp(index, 0, PageCount);
        ApplyStructural(doc =>
        {
            var page = FPDFPage_New(doc, index, width, height);
            if (page != IntPtr.Zero) FPDF_ClosePage(page);
        });
    }

    /// <summary>Inserts every page of another PDF at <paramref name="index"/>. Returns the number of pages added.</summary>
    public int InsertPagesFrom(string path, string? password, int index)
    {
        using var source = PdfFile.Load(path, password);
        index = Math.Clamp(index, 0, PageCount);
        var before = PageCount;
        ApplyStructural(doc =>
        {
            if (FPDF_ImportPagesByIndex(doc, source.Handle, null, 0, index) == 0)
                throw new PdfOpenException("Pages could not be imported from that file.");
        });
        return PageCount - before;
    }

    public void ExtractPages(IEnumerable<int> indices, string outputPath)
    {
        var pages = indices.Where(i => (uint)i < (uint)PageCount).Distinct().OrderBy(i => i).ToArray();
        if (pages.Length == 0) return;
        lock (PdfLibrary.Sync)
        {
            if (_form != IntPtr.Zero) FORM_ForceToKillFocus(_form);
            _formFocusPage = -1;
            var target = FPDF_CreateNewDocument();
            try
            {
                fixed (int* p = pages)
                    if (FPDF_ImportPagesByIndex(target, _file.Handle, p, (uint)pages.Length, 0) == 0)
                        throw new IOException("Pages could not be copied.");
                PdfLibrary.WriteFileAtomically(outputPath, stream => PdfLibrary.Save(target, stream));
            }
            finally
            {
                FPDF_CloseDocument(target);
            }
        }
    }

    // ---------------------------------------------------------------- stamping (fill & sign)

    private void EditPage(int index, Action<IntPtr, IntPtr, Affine> edit)
    {
        if ((uint)index >= (uint)PageCount) return;
        PushUndo();
        lock (PdfLibrary.Sync)
        {
            var page = PageLocked(index);
            if (page == IntPtr.Zero) return;
            var displayToPage = PageToDisplayLocked(index).Invert();
            edit(_file.Handle, page, displayToPage);
            FPDFPage_GenerateContent(page);
            if (_textPages.Remove(index, out var textPage)) FPDFText_ClosePage(textPage);
        }
        MarkDirty();
        PageContentChanged?.Invoke(this, index);
    }

    public void AddText(int index, IReadOnlyList<TextRun> runs, double fontSize, Color color)
    {
        if (runs.All(r => string.IsNullOrEmpty(r.Text))) return;
        EditPage(index, (doc, page, displayToPage) =>
        {
            var needsUnicodeFont = runs.Any(r => r.Text.Any(c => c > 0xFF));
            var font = needsUnicodeFont ? LoadSystemFontLocked(doc) : IntPtr.Zero;
            try
            {
                foreach (var run in runs)
                {
                    if (string.IsNullOrEmpty(run.Text)) continue;
                    var obj = font != IntPtr.Zero
                        ? FPDFPageObj_CreateTextObj(doc, font, (float)fontSize)
                        : FPDFPageObj_NewTextObj(doc, "Helvetica", (float)fontSize);
                    if (obj == IntPtr.Zero) continue;
                    FPDFText_SetText(obj, run.Text);
                    FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, color.A);
                    // Text space is y-up; flip into display space at the baseline, then map to page space.
                    var m = new Affine(1, 0, 0, -1, run.X, run.Baseline).Then(displayToPage);
                    FPDFPageObj_Transform(obj, m.A, m.B, m.C, m.D, m.E, m.F);
                    FPDFPage_InsertObject(page, obj);
                }
            }
            finally
            {
                if (font != IntPtr.Zero) FPDFFont_Close(font);
            }
        });
    }

    /// <summary>Adds vector figures. strokeWidth &gt; 0 strokes (in local units); otherwise fills with nonzero winding.</summary>
    public void AddPath(int index, IReadOnlyList<PathFigureData> figures, Affine localToDisplay, Color color, double strokeWidth)
    {
        if (figures.All(f => f.Points.Length < 2)) return;
        EditPage(index, (doc, page, displayToPage) =>
        {
            var path = IntPtr.Zero;
            foreach (var figure in figures)
            {
                if (figure.Points.Length < 2) continue;
                var first = figure.Points[0];
                if (path == IntPtr.Zero) path = FPDFPageObj_CreateNewPath((float)first.X, (float)first.Y);
                else FPDFPath_MoveTo(path, (float)first.X, (float)first.Y);
                for (var i = 1; i < figure.Points.Length; i++)
                    FPDFPath_LineTo(path, (float)figure.Points[i].X, (float)figure.Points[i].Y);
                if (figure.Closed) FPDFPath_Close(path);
            }
            if (path == IntPtr.Zero) return;

            if (strokeWidth > 0)
            {
                FPDFPath_SetDrawMode(path, 0, 1);
                FPDFPageObj_SetStrokeColor(path, color.R, color.G, color.B, color.A);
                FPDFPageObj_SetStrokeWidth(path, (float)strokeWidth);
                FPDFPageObj_SetLineCap(path, 1);
                FPDFPageObj_SetLineJoin(path, 1);
            }
            else
            {
                FPDFPath_SetDrawMode(path, FPDF_FILLMODE_WINDING, 0);
                FPDFPageObj_SetFillColor(path, color.R, color.G, color.B, color.A);
            }

            var m = localToDisplay.Then(displayToPage);
            FPDFPageObj_Transform(path, m.A, m.B, m.C, m.D, m.E, m.F);
            FPDFPage_InsertObject(page, path);
        });
    }

    private static IntPtr LoadSystemFontLocked(IntPtr doc)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf" })
        {
            var path = Path.Combine(fonts, name);
            if (!File.Exists(path)) continue;
            var data = File.ReadAllBytes(path);
            fixed (byte* p = data)
            {
                var font = FPDFText_LoadFont(doc, p, (uint)data.Length, FPDF_FONT_TRUETYPE, 1);
                if (font != IntPtr.Zero) return font;
            }
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- save & undo

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var bytes = BuildOutput();
        PdfLibrary.WriteFileAtomically(fullPath, stream => stream.Write(bytes));
        FilePath = fullPath;
        IsDirty = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private byte[] Snapshot()
    {
        lock (PdfLibrary.Sync)
        {
            if (_form != IntPtr.Zero) FORM_ForceToKillFocus(_form);
            _formFocusPage = -1;
            return PdfLibrary.SaveToBytes(_file.Handle);
        }
    }

    private void PushUndo()
    {
        _undo.Add(Snapshot());
        _redo.Clear();
        long total = _undo.Sum(b => (long)b.Length);
        while (_undo.Count > MaxUndoSteps || (_undo.Count > 1 && total > MaxUndoBytes))
        {
            total -= _undo[0].Length;
            _undo.RemoveAt(0);
        }
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var current = Snapshot();
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        Restore(previous);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var current = Snapshot();
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(current);
        Restore(next);
    }

    private void Restore(byte[] bytes)
    {
        var file = PdfFile.Load(bytes, _password);
        lock (PdfLibrary.Sync)
        {
            DetachFormLocked();
            _file.Dispose();
            _file = file;
            AttachFormLocked();
            ReadPageSizesLocked();
        }
        MarkDirty();
        PagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkDirty()
    {
        if (_disposed) return;
        IsDirty = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        RenderService.Cancel(this);
        lock (PdfLibrary.Sync)
        {
            _disposed = true;
            DetachFormLocked();
            _file.Dispose();
        }
        if (_self.IsAllocated) _self.Free();
        _undo.Clear();
        _redo.Clear();
    }
}
