using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
using PDFPlus.Core;

namespace PDFPlus.Views;

/// <summary>
/// The editor for text and rich text files. A <see cref="RichTextBox"/> does the editing, undo and selection;
/// this adds the page-shaped sheet, zoom, find and replace, and the formatting the toolbar drives.
/// </summary>
public partial class TextView : UserControl
{
    /// <summary>Where a run of text sits in the flattened document, used to map a search hit back to a position.</summary>
    private readonly record struct RunSpan(TextPointer Start, int TextIndex, int Length);

    private const double MinZoom = 0.5, MaxZoom = 3.0;

    private string _flattened = "";
    private List<RunSpan> _spans = new();
    private bool _indexStale = true;
    private bool _suppressDirty;
    private double _zoom = 1;
    private readonly PageLayout _pages;
    private readonly System.Windows.Threading.DispatcherTimer _repaginate;

    public TextDocument Document { get; }

    /// <summary>Raised when the title, dirty state, caret or selection changed and the chrome should catch up.</summary>
    public event EventHandler? StatusChanged;
    public event EventHandler? SaveRequested;

    public TextView(TextDocument document)
    {
        InitializeComponent();
        Document = document;

        // Code and data read far better in a monospaced face; prose does not. A rich document brings its own
        // fonts, so only plain text gets one imposed on it.
        if (document.Format == TextFormat.Plain)
        {
            Editor.FontFamily = new FontFamily("Consolas, Courier New");
            Editor.FontSize = 13.5;
        }
        Editor.Document = document.Content;

        MarkPageBreaks(document.Content.Blocks);

        _pages = new PageLayout(Editor, PageBackdrop, Sheet);
        _pages.Configure(document.Page);

        // Re-laying out after every keystroke would be wasteful; a short pause after typing stops is enough.
        _repaginate = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180),
            IsEnabled = false,
        };
        _repaginate.Tick += (_, _) =>
        {
            _repaginate.Stop();
            RunLayout(_pages.Update);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        };

        Editor.TextChanged += OnTextChanged;
        Editor.SelectionChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
        Loaded += (_, _) =>
        {
            Editor.Focus();
            RunLayout(_pages.Update);
        };
    }

    /// <summary>Plain text cannot carry bold, colours or fonts, so the toolbar offers them only for rich text.</summary>
    public bool SupportsFormatting => TextDocument.IsRich(Document.Format);

    public bool CanUndo => Editor.CanUndo;
    public bool CanRedo => Editor.CanRedo;
    public double Zoom => _zoom;

    /// <summary>
    /// Draws a line above each paragraph that starts a new page. There are no pages in a continuous editor, so
    /// this is the only place an explicit break from the file can show itself.
    /// </summary>
    private static void MarkPageBreaks(BlockCollection blocks)
    {
        var rule = new SolidColorBrush(Color.FromArgb(0x66, 0x6B, 0x6F, 0x7A));
        rule.Freeze();
        foreach (var block in blocks.Where(b => b.BreakPageBefore))
        {
            block.BorderBrush = rule;
            block.BorderThickness = new Thickness(0, 1, 0, 0);
            block.Padding = new Thickness(0, 12, 0, 0);
        }
    }

    public void Undo() => Editor.Undo();
    public void Redo() => Editor.Redo();
    public void FocusEditor() => Editor.Focus();
    public void SelectAll() => Editor.SelectAll();

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // PageLayout changes block margins to make room for the paper gap. WPF reports those presentation-only
        // changes as TextChanged too. Do not let them queue another layout pass: doing so creates a permanent
        // layout -> TextChanged -> timer -> layout loop and can leave the editor busy forever.
        if (_suppressDirty) return;

        _indexStale = true;
        Repaginate();
        Document.IsDirty = true;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Queues a page layout pass, coalescing the ones that arrive while someone is typing.</summary>
    private void Repaginate()
    {
        _repaginate.Stop();
        _repaginate.Start();
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the document as it really is, without the spacing the page layout
    /// adds. Everything that reads or saves the document goes through here.
    /// </summary>
    public void WithTrueLayout(Action work) => RunLayout(() => _pages.Suspend(work));

    /// <summary>
    /// Laying pages out moves blocks by changing their margins, and WPF reports that as the text changing.
    /// It isn't: nothing the user wrote is different, so the document must not come out of it looking edited.
    /// </summary>
    private void RunLayout(Action work)
    {
        if (_layingOut)
        {
            work();
            return;
        }

        var dirty = Document.IsDirty;
        _layingOut = true;
        _suppressDirty = true;
        try
        {
            work();
        }
        finally
        {
            // Dependency-property changes raise TextChanged synchronously. Releasing the guard here matters:
            // leaving it set until a later dispatcher turn can swallow real typing that arrives first.
            _suppressDirty = false;
            _layingOut = false;
            Document.IsDirty = dirty;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _layingOut;

    public int PageCount => _pages.PageCount;

    // ---------------------------------------------------------------- status

    /// <summary>A short summary for the status area: where the caret is and how much text there is.</summary>
    public string StatusText()
    {
        var words = CountWords();
        if (IsPageView)
        {
            var pages = PageViewer.PageCount;
            return $"{pages:N0} page{(pages == 1 ? "" : "s")}   ·   read-only   ·   {words:N0} words";
        }

        var caret = Editor.CaretPosition;
        var line = 1;
        foreach (var block in Document.Content.Blocks)
        {
            if (block.ContentStart.CompareTo(caret) > 0) break;
            line++;
        }

        var selection = Editor.Selection.Text.Length;
        var sheets = $"{PageCount:N0} page{(PageCount == 1 ? "" : "s")}";
        return selection > 0
            ? $"Line {Math.Max(1, line - 1)}   ·   {selection:N0} selected   ·   {words:N0} words   ·   {sheets}"
            : $"Line {Math.Max(1, line - 1)}   ·   {words:N0} words   ·   {sheets}";
    }

    private int CountWords()
    {
        var text = FlattenedText();
        var words = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) inWord = false;
            else if (!inWord) { inWord = true; words++; }
        }
        return words;
    }

    // ---------------------------------------------------------------- zoom

    public void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        SheetScale.ScaleX = SheetScale.ScaleY = _zoom;
        // The page viewer has its own zoom, in percent.
        if (IsPageView) PageViewer.Zoom = _zoom * 100;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => SetZoom(_zoom + 0.1);
    public void ZoomOut() => SetZoom(_zoom - 0.1);
    public void ResetZoom() => SetZoom(1);

    private void OnScrollerWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        SetZoom(_zoom * Math.Pow(1.0015, e.Delta));
        e.Handled = true;
    }

    // ---------------------------------------------------------------- formatting

    /// <summary>Applies a property to the selection, or to what is typed next when nothing is selected.</summary>
    public void ApplyFormat(DependencyProperty property, object value)
    {
        if (!SupportsFormatting) return;
        Editor.Selection.ApplyPropertyValue(property, value);
        Editor.Focus();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleBold() => ApplyFormat(TextElement.FontWeightProperty,
        SelectionHas(TextElement.FontWeightProperty, FontWeights.Bold) ? FontWeights.Normal : FontWeights.Bold);

    public void ToggleItalic() => ApplyFormat(TextElement.FontStyleProperty,
        SelectionHas(TextElement.FontStyleProperty, FontStyles.Italic) ? FontStyles.Normal : FontStyles.Italic);

    public void ToggleUnderline() => ApplyFormat(Inline.TextDecorationsProperty,
        SelectionHas(Inline.TextDecorationsProperty, TextDecorations.Underline) ? new TextDecorationCollection() : TextDecorations.Underline);

    public void SetAlignment(TextAlignment alignment) => ApplyFormat(Block.TextAlignmentProperty, alignment);

    /// <summary>WPF's own commands toggle the list off again when the caret is already in one of that kind.</summary>
    public void ToggleList(TextMarkerStyle marker)
    {
        if (!SupportsFormatting) return;
        if (marker == TextMarkerStyle.Disc) EditingCommands.ToggleBullets.Execute(null, Editor);
        else EditingCommands.ToggleNumbering.Execute(null, Editor);
        Editor.Focus();
    }

    /// <summary>The list the caret sits in, if any, so the toolbar can show which button is active.</summary>
    public TextMarkerStyle? CurrentListMarker =>
        InList(out var list) ? list!.MarkerStyle : null;

    public bool SelectionHas(DependencyProperty property, object value)
    {
        var current = Editor.Selection.GetPropertyValue(property);
        return current != DependencyProperty.UnsetValue && Equals(current, value);
    }

    public object? SelectionValue(DependencyProperty property)
    {
        var value = Editor.Selection.GetPropertyValue(property);
        return value == DependencyProperty.UnsetValue ? null : value;
    }

    private bool InList(out List? list)
    {
        list = (Editor.CaretPosition.Paragraph?.Parent as ListItem)?.Parent as List;
        return list != null;
    }

    // ---------------------------------------------------------------- find and replace

    public void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        if (Editor.Selection.Text is { Length: > 0 and < 120 } selected && !selected.Contains('\n')) FindBox.Text = selected;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    public void HideFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    public bool IsFindOpen => FindBar.Visibility == Visibility.Visible;

    // ---------------------------------------------------------------- page view

    /// <summary>
    /// True while showing real, paginated pages. WPF's paginated viewers cannot edit, so this is a way to look
    /// at the document rather than a second way to work on it.
    /// </summary>
    public bool IsPageView { get; private set; }

    public void SetPageView(bool pages)
    {
        if (pages == IsPageView) return;
        IsPageView = pages;

        if (pages)
        {
            PageViewer.Document = Paginate();
            PageViewer.Zoom = _zoom * 100;
            PageViewer.Visibility = Visibility.Visible;
            Scroller.Visibility = Visibility.Collapsed;
        }
        else
        {
            PageViewer.Document = null;
            ReleasePages();
            PageViewer.Visibility = Visibility.Collapsed;
            Scroller.Visibility = Visibility.Visible;
            Editor.Focus();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private Uri? _pagePackage;

    /// <summary>
    /// Lays the document out on real pages by paginating a copy of it to XPS, which is what lets the viewer
    /// stack pages with gaps between them the way a word processor does.
    ///
    /// A copy, not the document itself: pagination needs page dimensions set on the FlowDocument, and the one
    /// in the editor has to stay as it is. It also means looking at pages can never disturb what is being edited.
    /// </summary>
    private FixedDocumentSequence? Paginate()
    {
        ReleasePages();
        try
        {
            FlowDocument? source = null;
            WithTrueLayout(() => source = Clone(Document.Content));
            var copy = source!;
            copy.PageWidth = Document.Page.Width;
            copy.PageHeight = Document.Page.Height;
            copy.PagePadding = Document.Page.Margin;
            copy.ColumnWidth = double.PositiveInfinity;

            var stream = new MemoryStream();
            var package = Package.Open(stream, FileMode.Create, FileAccess.ReadWrite);
            _pagePackage = new Uri($"pack://pdfplus{Guid.NewGuid():N}.xps");
            PackageStore.AddPackage(_pagePackage, package);

            var xps = new XpsDocument(package, CompressionOption.Fast, _pagePackage.AbsoluteUri);
            XpsDocument.CreateXpsDocumentWriter(xps).Write(((IDocumentPaginatorSource)copy).DocumentPaginator);
            return xps.GetFixedDocumentSequence();
        }
        catch
        {
            // Pagination is a view of the document, not the document; failing to build it must not lose anything.
            ReleasePages();
            return null;
        }
    }

    private void ReleasePages()
    {
        if (_pagePackage == null) return;
        try
        {
            PackageStore.RemovePackage(_pagePackage);
        }
        catch
        {
            // Already gone.
        }
        _pagePackage = null;
    }

    /// <summary>A deep copy, through the clipboard format that carries images as well as text.</summary>
    private static FlowDocument Clone(FlowDocument source)
    {
        var copy = new FlowDocument();
        using var buffer = new MemoryStream();
        new TextRange(source.ContentStart, source.ContentEnd).Save(buffer, DataFormats.XamlPackage);
        buffer.Position = 0;
        new TextRange(copy.ContentStart, copy.ContentEnd).Load(buffer, DataFormats.XamlPackage);
        copy.FontFamily = source.FontFamily;
        copy.FontSize = source.FontSize;

        // The clipboard format carries text formatting but not page breaks, so put those back by hand.
        CopyPageBreaks(source.Blocks, copy.Blocks);
        return copy;
    }

    private static void CopyPageBreaks(BlockCollection source, BlockCollection target)
    {
        if (source.Count != target.Count) return;
        var from = source.ToList();
        var to = target.ToList();
        for (var i = 0; i < from.Count; i++)
        {
            to[i].BreakPageBefore = from[i].BreakPageBefore;
            if (from[i] is Section fromSection && to[i] is Section toSection)
                CopyPageBreaks(fromSection.Blocks, toSection.Blocks);
        }
    }

    // ---------------------------------------------------------------- notice

    /// <summary>
    /// Shows a quiet line above the document, for things worth saying once. Deliberately not a dialog: it is
    /// information about the file, not a question, and it should not stand between the user and their document.
    /// </summary>
    public void ShowNotice(string message)
    {
        NoticeText.Text = message;
        NoticeBar.Visibility = Visibility.Visible;
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e) => NoticeBar.Visibility = Visibility.Collapsed;

    private void OnCloseFindClick(object sender, RoutedEventArgs e) => HideFind();
    private void OnFindNextClick(object sender, RoutedEventArgs e) => FindNext(false);
    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => FindNext(true);
    private void OnFindOptionChanged(object sender, RoutedEventArgs e) => UpdateFindStatus();
    private void OnFindTextChanged(object sender, TextChangedEventArgs e) => UpdateFindStatus();

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FindNext(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
        else if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
    }

    private void OnReplaceBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Replace(); e.Handled = true; }
        else if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
    }

    private void OnReplaceClick(object sender, RoutedEventArgs e) => Replace();

    private void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        var query = FindBox.Text;
        if (query.Length == 0) return;

        var replaced = 0;
        Editor.BeginChange();
        try
        {
            Editor.CaretPosition = Editor.Document.ContentStart;
            while (FindNext(false, announce: false))
            {
                Editor.Selection.Text = ReplaceBox.Text;
                replaced++;
                if (replaced > 100_000) break; // A replacement containing the query would otherwise never end.
            }
        }
        finally
        {
            Editor.EndChange();
        }
        FindStatus.Text = replaced == 0 ? "Not found" : $"Replaced {replaced:N0}";
    }

    private void Replace()
    {
        if (FindBox.Text.Length == 0) return;
        var selected = Editor.Selection.Text;
        var matches = string.Equals(selected, FindBox.Text,
            MatchCaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        if (matches) Editor.Selection.Text = ReplaceBox.Text;
        FindNext(false);
    }

    public bool FindNext(bool backwards, bool announce = true)
    {
        var query = FindBox.Text;
        if (query.Length == 0) return false;

        BuildIndex();
        var comparison = MatchCaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var caret = OffsetOf(backwards ? Editor.Selection.Start : Editor.Selection.End);

        var found = backwards
            ? LastIndexBefore(query, caret, comparison)
            : _flattened.IndexOf(query, Math.Min(caret, Math.Max(0, _flattened.Length)), comparison);

        // Wrap around, which is what every other editor does.
        if (found < 0)
            found = backwards ? _flattened.LastIndexOf(query, comparison) : _flattened.IndexOf(query, comparison);

        if (found < 0)
        {
            if (announce) FindStatus.Text = "Not found";
            return false;
        }

        if (PointerAt(found) is not { } start || PointerAt(found + query.Length) is not { } end) return false;
        Editor.Selection.Select(start, end);
        Editor.CaretPosition = end;
        BringSelectionIntoView(start);
        if (announce) UpdateFindStatus();
        return true;
    }

    private int LastIndexBefore(string query, int caret, StringComparison comparison)
    {
        var limit = Math.Clamp(caret - 1, 0, Math.Max(0, _flattened.Length - 1));
        return _flattened.Length == 0 ? -1 : _flattened.LastIndexOf(query, limit, comparison);
    }

    private void UpdateFindStatus()
    {
        var query = FindBox.Text;
        if (query.Length == 0)
        {
            FindStatus.Text = "";
            return;
        }
        BuildIndex();
        var comparison = MatchCaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var count = 0;
        for (var at = 0; at <= _flattened.Length - query.Length;)
        {
            var found = _flattened.IndexOf(query, at, comparison);
            if (found < 0) break;
            count++;
            at = found + 1;
        }
        FindStatus.Text = count == 0 ? "Not found" : $"{count:N0} match{(count == 1 ? "" : "es")}";
    }

    private void BringSelectionIntoView(TextPointer start)
    {
        var box = start.GetCharacterRect(LogicalDirection.Forward);
        if (box.IsEmpty) return;
        var target = Editor.TransformToAncestor(Scroller).Transform(new Point(0, box.Top));
        if (target.Y < 0 || target.Y > Scroller.ViewportHeight - 40)
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + target.Y - Scroller.ViewportHeight / 3);
    }

    // ---------------------------------------------------------------- flattened text index

    /// <summary>
    /// The document as one string, plus where each run of text starts. Searching the flattened text finds
    /// matches that straddle a formatting boundary, and the run list maps an index back to a position without
    /// keeping a pointer per character.
    /// </summary>
    private void BuildIndex()
    {
        if (!_indexStale) return;
        _indexStale = false;

        var builder = new StringBuilder();
        var spans = new List<RunSpan>();
        var pointer = Document.Content.ContentStart;

        while (pointer != null)
        {
            switch (pointer.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    var text = pointer.GetTextInRun(LogicalDirection.Forward);
                    spans.Add(new RunSpan(pointer, builder.Length, text.Length));
                    builder.Append(text);
                    pointer = pointer.GetPositionAtOffset(text.Length);
                    continue;
                case TextPointerContext.ElementEnd when pointer.Parent is Paragraph:
                    builder.Append('\n');
                    break;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }

        _flattened = builder.ToString();
        _spans = spans;
    }

    private string FlattenedText()
    {
        BuildIndex();
        return _flattened;
    }

    private TextPointer? PointerAt(int index)
    {
        foreach (var span in _spans)
        {
            if (index < span.TextIndex || index > span.TextIndex + span.Length) continue;
            return span.Start.GetPositionAtOffset(index - span.TextIndex);
        }
        return _spans.Count > 0 ? _spans[^1].Start.GetPositionAtOffset(_spans[^1].Length) : null;
    }

    private int OffsetOf(TextPointer position)
    {
        BuildIndex();
        foreach (var span in _spans)
        {
            var offset = span.Start.GetOffsetToPosition(position);
            if (offset >= 0 && offset <= span.Length) return span.TextIndex + offset;
        }
        return 0;
    }

    // ---------------------------------------------------------------- keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && IsFindOpen)
        {
            HideFind();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    /// <summary>Replaces the whole document, used when reverting or reloading.</summary>
    internal void ReplaceContent(Action change)
    {
        _suppressDirty = true;
        try
        {
            change();
        }
        finally
        {
            _suppressDirty = false;
            _indexStale = true;
        }
    }
}
