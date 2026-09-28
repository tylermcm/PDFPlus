using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PDFPlus.Core.Docx;

namespace PDFPlus.Views;

/// <summary>
/// Breaks a continuously flowing editor into pages.
///
/// A <see cref="RichTextBox"/> has no concept of a page: it lays text out in one column as tall as it needs.
/// This measures where the text actually sits and pushes each block down so that none of them straddles the
/// bottom of a page, then draws the page rectangles behind the text. The result is an editor you can type in
/// that still looks like pages.
///
/// The one thing it cannot do is what Word does when a paragraph is longer than the space left: Word splits the
/// paragraph across the boundary, and splitting one here would mean splitting the element the text lives in,
/// which would change the document. So a long paragraph moves as a whole and leaves the rest of the page empty.
///
/// The pushes live in each block's top margin, which is also where real paragraph spacing lives, so
/// <see cref="Suspend"/> puts the original margins back before anything reads or saves the document.
/// </summary>
internal sealed class PageLayout
{
    /// <summary>The gap drawn between one page and the next.</summary>
    private const double Gap = 28;
    /// <summary>Layout is iterative, since pushing one block moves everything after it.</summary>
    private const int MaxPasses = 200;

    private readonly RichTextBox _editor;
    private readonly Canvas _backdrop;
    private readonly FrameworkElement _host;
    private readonly Dictionary<Block, Thickness> _original = new();
    private readonly List<InlineUIContainer> _spacers = [];
    private PageSetup _page = PageSetup.Default;
    private bool _applied;

    public PageLayout(RichTextBox editor, Canvas backdrop, FrameworkElement host)
    {
        _editor = editor;
        _backdrop = backdrop;
        _host = host;
    }

    public int PageCount { get; private set; } = 1;

    public void Configure(PageSetup page)
    {
        _page = page;
        _editor.Margin = new Thickness(page.Margin.Left, page.Margin.Top, page.Margin.Right, 0);
        _host.Width = page.Width;
    }

    /// <summary>Height of the text area on one page.</summary>
    private double ContentHeight => Math.Max(72, _page.Height - _page.Margin.Top - _page.Margin.Bottom);

    /// <summary>Vertical distance from the end of one page's text to the start of the next page's text.</summary>
    private double BreakHeight => _page.Margin.Bottom + Gap + _page.Margin.Top;

    /// <summary>The bottom of page <paramref name="page"/>'s text, in editor coordinates.</summary>
    private double LimitOf(int page) => page * ContentHeight + (page - 1) * BreakHeight;

    /// <summary>
    /// Puts the real margins back, runs <paramref name="work"/>, then lays the pages out again. Anything that
    /// reads or writes the document goes through here so it never sees the layout's own spacing.
    /// </summary>
    public void Suspend(Action work)
    {
        var wasApplied = _applied;
        Restore();
        try
        {
            work();
        }
        finally
        {
            if (wasApplied) Update();
        }
    }

    private void Restore()
    {
        // A spacer is a zero-width inline used only to make one wrapped line start on the next sheet. Remove
        // those before restoring block margins so readers and writers always see the author's real document.
        foreach (var spacer in _spacers)
            if (spacer.Parent is Paragraph paragraph)
                paragraph.Inlines.Remove(spacer);
        _spacers.Clear();

        foreach (var (block, margin) in _original) block.Margin = margin;
        _original.Clear();
        _applied = false;
    }

    /// <summary>Measures the document and pushes blocks so none crosses the bottom of a page.</summary>
    public void Update()
    {
        if (!_editor.IsLoaded || _editor.Document is not { } document) return;

        Restore();
        _editor.UpdateLayout();

        var blocks = document.Blocks.ToList();
        var contentHeight = ContentHeight;
        // A paragraph can cross more than one boundary. Remember the last boundary handled while this layout
        // is being built so the next pass looks for the following one rather than inserting the same gap again.
        var splitThrough = new Dictionary<Paragraph, int>();

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var moved = false;
            foreach (var block in blocks)
            {
                if (!Measure(block, out var top, out var bottom)) continue;

                var page = Math.Max(1, (int)Math.Floor(top / (contentHeight + BreakHeight)) + 1);
                var limit = LimitOf(page);
                var pageTop = page == 1 ? 0 : LimitOf(page - 1) + BreakHeight;

                // A block the author asked to start a page goes to the next one unless it is already there.
                var forced = block.BreakPageBefore && top > pageTop + 0.5;

                if (!forced)
                {
                    var boundary = block is Paragraph paragraph && splitThrough.TryGetValue(paragraph, out var handled)
                        ? Math.Max(page, handled + 1)
                        : page;
                    limit = LimitOf(boundary);
                    if (bottom <= limit + 0.5) continue;

                    // A paragraph is allowed to split between wrapped lines, just as it does in Word. A
                    // zero-width inline makes the first line that would cross the boundary tall enough to put
                    // its text at the next page's content top. Unlike a paragraph break it adds no character,
                    // and Restore removes it before save/copy/search.
                    if (block is Paragraph flowing && InsertLineSpacer(flowing, limit))
                    {
                        splitThrough[flowing] = boundary;
                        _applied = true;
                        moved = true;
                        _editor.UpdateLayout();
                        break;
                    }

                    // Tables and other indivisible blocks move as a whole when they fit on a fresh page. An
                    // object taller than the content area is the one case WPF has to let straddle a boundary.
                    if (bottom - top > contentHeight) continue;
                }

                var target = limit + BreakHeight;
                var push = target - top;
                if (push <= 0.5) continue;

                _original.TryAdd(block, block.Margin);
                block.Margin = new Thickness(block.Margin.Left, block.Margin.Top + push, block.Margin.Right, block.Margin.Bottom);
                _applied = true;
                moved = true;
                _editor.UpdateLayout();
                break;
            }
            if (!moved) break;
        }

        Draw(blocks);
    }

    /// <summary>
    /// Inserts presentation-only height at the beginning of the first rendered line that would cross
    /// <paramref name="limit"/>. The inline has no width and contributes no text.
    /// </summary>
    private bool InsertLineSpacer(Paragraph paragraph, double limit)
    {
        var line = paragraph.ContentStart.GetLineStartPosition(0);
        while (line != null && line.CompareTo(paragraph.ContentEnd) < 0)
        {
            var rect = line.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty && rect.Bottom > limit + 0.5)
            {
                var target = limit + BreakHeight;
                var height = target - rect.Top + Math.Max(1, rect.Height);
                if (height <= rect.Height + 0.5) return false;

                var marker = new Border
                {
                    Width = 0,
                    Height = height,
                    IsHitTestVisible = false,
                    Focusable = false,
                };
                var insertion = line.GetInsertionPosition(LogicalDirection.Forward);
                if (insertion == null || insertion.CompareTo(paragraph.ContentEnd) >= 0) return false;
                var spacer = new InlineUIContainer(marker, insertion)
                {
                    BaselineAlignment = BaselineAlignment.Baseline,
                };
                _spacers.Add(spacer);
                return true;
            }

            var next = line.GetLineStartPosition(1);
            if (next == null || next.CompareTo(line) <= 0) break;
            line = next;
        }
        return false;
    }

    private bool Measure(Block block, out double top, out double bottom)
    {
        top = bottom = 0;
        try
        {
            var start = block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var end = block.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            if (start.IsEmpty || end.IsEmpty) return false;
            top = start.Top;
            bottom = Math.Max(end.Bottom, start.Bottom);
            return bottom > top - 0.5;
        }
        catch
        {
            // A block that isn't laid out yet has no rectangle; it will be measured on the next pass.
            return false;
        }
    }

    /// <summary>Draws one sheet of paper per page behind the text.</summary>
    private void Draw(List<Block> blocks)
    {
        var used = 0.0;
        foreach (var block in blocks)
            if (Measure(block, out _, out var bottom))
                used = Math.Max(used, bottom);

        PageCount = Math.Max(1, (int)Math.Ceiling((used + BreakHeight) / (ContentHeight + BreakHeight)));

        _backdrop.Children.Clear();
        for (var page = 0; page < PageCount; page++)
        {
            var sheet = new Border
            {
                Width = _page.Width,
                Height = _page.Height,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 10, ShadowDepth = 1, Opacity = 0.18, Direction = 270,
                },
            };
            sheet.SetResourceReference(Border.BackgroundProperty, "Brush.Toolbar");
            sheet.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            Canvas.SetTop(sheet, page * (_page.Height + Gap));
            Canvas.SetLeft(sheet, 0);
            _backdrop.Children.Add(sheet);
        }

        _host.Height = PageCount * _page.Height + (PageCount - 1) * Gap;
    }
}
