using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PDFPlus.Core;

namespace PDFPlus.Services;

public static class PrintService
{
    public static void Print(PdfDocument document, int currentPage)
    {
        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            MinPage = 1,
            MaxPage = (uint)document.PageCount,
            PageRange = new PageRange(1, document.PageCount),
        };
        if (dialog.ShowDialog() != true) return;

        List<int> pages = dialog.PageRangeSelection switch
        {
            PageRangeSelection.CurrentPage => [currentPage],
            PageRangeSelection.UserPages => Enumerable
                .Range(dialog.PageRange.PageFrom, Math.Max(0, dialog.PageRange.PageTo - dialog.PageRange.PageFrom + 1))
                .Select(p => p - 1)
                .Where(p => p >= 0 && p < document.PageCount)
                .ToList(),
            _ => Enumerable.Range(0, document.PageCount).ToList(),
        };
        if (pages.Count == 0) return;

        var paper = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        dialog.PrintDocument(new Paginator(document, pages, paper), document.Title);
    }

    private sealed class Paginator(PdfDocument document, List<int> pages, Size paper) : DocumentPaginator
    {
        private const double PrintDpi = 200;

        public override bool IsPageCountValid => true;
        public override int PageCount => pages.Count;
        public override Size PageSize { get; set; } = paper;
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            var index = pages[pageNumber];
            var size = document.PageSizes[index];
            var visual = new DrawingVisual();

            var widthPx = (int)Math.Min(7000, size.Width / 72 * PrintDpi);
            var heightPx = (int)Math.Min(7000, size.Height / 72 * PrintDpi);
            var bitmap = document.Render(index, widthPx, heightPx, new Int32Rect(0, 0, widthPx, heightPx));

            using (var dc = visual.RenderOpen())
            {
                if (bitmap != null)
                {
                    // Auto-rotate landscape pages onto portrait paper (and vice versa), then fit.
                    var rotate = size.Width > size.Height != PageSize.Width > PageSize.Height;
                    var contentWidth = (rotate ? size.Height : size.Width) * 96 / 72;
                    var contentHeight = (rotate ? size.Width : size.Height) * 96 / 72;
                    var scale = Math.Min(PageSize.Width / contentWidth, PageSize.Height / contentHeight);
                    var w = contentWidth * scale;
                    var h = contentHeight * scale;
                    dc.PushTransform(new TranslateTransform((PageSize.Width - w) / 2, (PageSize.Height - h) / 2));
                    if (rotate)
                    {
                        dc.PushTransform(new TranslateTransform(w, 0));
                        dc.PushTransform(new RotateTransform(90));
                        dc.DrawImage(bitmap, new Rect(0, 0, h, w));
                        dc.Pop();
                        dc.Pop();
                    }
                    else
                    {
                        dc.DrawImage(bitmap, new Rect(0, 0, w, h));
                    }
                    dc.Pop();
                }
            }
            return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
        }
    }
}
