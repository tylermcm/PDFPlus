using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFPlus.Native;
using static PDFPlus.Native.Pdfium;

namespace PDFPlus.Core;

public sealed record PdfPreview(BitmapSource? Image, int PageCount, bool IsProtected);

/// <summary>First-page previews for the Home screen, rendered without setting up a full document.</summary>
public static unsafe class PdfThumbnail
{
    /// <summary>Renders page 1 to fit within the given pixel size. Safe to call from a background thread.</summary>
    public static PdfPreview Render(string path, int maxWidth, int maxHeight)
    {
        PdfFile file;
        try
        {
            file = PdfFile.Load(path, null);
        }
        catch (PdfPasswordRequiredException)
        {
            return new PdfPreview(null, 0, true);
        }

        using (file)
        {
            int pageCount, width, height;
            byte[] pixels;
            lock (PdfLibrary.Sync)
            {
                pageCount = FPDF_GetPageCount(file.Handle);
                if (pageCount <= 0) return new PdfPreview(null, 0, false);
                FS_SIZEF size;
                if (FPDF_GetPageSizeByIndexF(file.Handle, 0, &size) == 0 || size.Width < 1 || size.Height < 1)
                    size = new FS_SIZEF { Width = 612, Height = 792 };
                var scale = Math.Min(maxWidth / size.Width, maxHeight / size.Height);
                width = Math.Max(1, (int)(size.Width * scale));
                height = Math.Max(1, (int)(size.Height * scale));

                var page = FPDF_LoadPage(file.Handle, 0);
                if (page == IntPtr.Zero) return new PdfPreview(null, pageCount, false);
                pixels = new byte[width * height * 4];
                fixed (byte* p = pixels)
                {
                    var bitmap = FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRx, (IntPtr)p, width * 4);
                    if (bitmap != IntPtr.Zero)
                    {
                        FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                        FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, FPDF_ANNOT);
                        FPDFBitmap_Destroy(bitmap);
                    }
                }
                FPDF_ClosePage(page);
            }

            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            image.Freeze();
            return new PdfPreview(image, pageCount, false);
        }
    }
}
