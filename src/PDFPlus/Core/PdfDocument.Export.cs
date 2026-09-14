using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFPlus.Native;
using static PDFPlus.Native.Pdfium;

namespace PDFPlus.Core;

public sealed record PdfProtection(string UserPassword, string OwnerPassword, bool AllowPrint, bool AllowCopy, bool AllowEdit);

public sealed record CompressionResult(long OriginalBytes, long CompressedBytes, int ImagesRecompressed, bool Written);

public enum ImageExportFormat { Png, Jpeg }

public sealed unsafe partial class PdfDocument
{
    private bool _removeProtection;

    /// <summary>True if the file was opened with a security handler (password or permissions).</summary>
    public bool IsEncrypted
    {
        get
        {
            lock (PdfLibrary.Sync)
                return !_disposed && FPDF_GetSecurityHandlerRevision(_file.Handle) != -1;
        }
    }

    /// <summary>Password settings applied whenever this document is saved; null keeps the file's existing security.</summary>
    public PdfProtection? Protection { get; private set; }

    public bool IsProtected => Protection != null || (IsEncrypted && !_removeProtection);

    public void SetProtection(PdfProtection protection)
    {
        Protection = protection;
        _removeProtection = false;
        MarkDirty();
    }

    public void ClearProtection()
    {
        Protection = null;
        _removeProtection = true;
        MarkDirty();
    }

    /// <summary>Serializes the document with the requested security applied.</summary>
    private byte[] BuildOutput()
    {
        byte[] bytes;
        lock (PdfLibrary.Sync)
        {
            if (_form != IntPtr.Zero) FORM_ForceToKillFocus(_form);
            _formFocusPage = -1;
            var flags = Protection != null || _removeProtection ? FPDF_REMOVE_SECURITY : 0;
            bytes = PdfLibrary.SaveToBytes(_file.Handle, flags);
        }
        return Protection != null ? PdfSecurity.Encrypt(bytes, Protection) : bytes;
    }

    // ---------------------------------------------------------------- page images

    public void ExportPageImage(int index, string path, double dpi, ImageExportFormat format, int jpegQuality = 90)
    {
        var size = PageSizes[index];
        var width = Math.Max(1, (int)Math.Round(size.Width / 72 * dpi));
        var height = Math.Max(1, (int)Math.Round(size.Height / 72 * dpi));
        var scale = Math.Min(1.0, 12000.0 / Math.Max(width, height));
        width = Math.Max(1, (int)(width * scale));
        height = Math.Max(1, (int)(height * scale));

        var bitmap = Render(index, width, height, new Int32Rect(0, 0, width, height))
                     ?? throw new IOException($"Page {index + 1} could not be rendered.");
        BitmapEncoder encoder = format == ImageExportFormat.Png
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = jpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(WithDpi(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0), dpi * scale)));
        PdfLibrary.WriteFileAtomically(path, encoder.Save);
    }

    private static BitmapSource WithDpi(BitmapSource source, double dpi)
    {
        var stride = source.PixelWidth * 3;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, dpi, dpi, PixelFormats.Bgr24, null, pixels, stride);
        result.Freeze();
        return result;
    }

    // ---------------------------------------------------------------- compression

    /// <summary>
    /// Writes a smaller copy: large images are downsampled and re-encoded as JPEG, then unused objects are dropped.
    /// The open document is not modified. Nothing is written if the result isn't smaller.
    /// </summary>
    public CompressionResult SaveCompressedCopy(string path, int maxDpi, int jpegQuality, CancellationToken token, Action<int>? pageDone = null)
    {
        var original = BuildOutput();
        byte[] source;
        lock (PdfLibrary.Sync)
        {
            if (_form != IntPtr.Zero) FORM_ForceToKillFocus(_form);
            _formFocusPage = -1;
            source = PdfLibrary.SaveToBytes(_file.Handle, IsEncrypted ? FPDF_REMOVE_SECURITY : 0);
        }

        var images = 0;
        byte[] output;
        using (var work = PdfFile.Load(source, null))
        {
            int pageCount;
            lock (PdfLibrary.Sync) pageCount = FPDF_GetPageCount(work.Handle);
            for (var i = 0; i < pageCount; i++)
            {
                token.ThrowIfCancellationRequested();
                lock (PdfLibrary.Sync)
                {
                    var page = FPDF_LoadPage(work.Handle, i);
                    if (page == IntPtr.Zero) continue;
                    var changed = RecompressImagesLocked(work.Handle, page, maxDpi, jpegQuality);
                    if (changed > 0) FPDFPage_GenerateContent(page);
                    images += changed;
                    FPDF_ClosePage(page);
                }
                pageDone?.Invoke(i);
            }
            lock (PdfLibrary.Sync) output = PdfLibrary.SaveToBytes(work.Handle);
        }

        output = PdfSecurity.Compact(output);
        // Keep the copy protected: explicit protection wins; otherwise re-lock an encrypted file with its open password.
        var protection = Protection ?? (IsEncrypted && !_removeProtection ? new PdfProtection(_password ?? "", "", true, true, true) : null);
        if (protection != null) output = PdfSecurity.Encrypt(output, protection);

        if (output.Length >= original.Length)
            return new CompressionResult(original.Length, output.Length, images, false);

        PdfLibrary.WriteFileAtomically(path, stream => stream.Write(output));
        return new CompressionResult(original.Length, output.Length, images, true);
    }

    private static int RecompressImagesLocked(IntPtr document, IntPtr page, int maxDpi, int jpegQuality)
    {
        var changed = 0;
        var count = FPDFPage_CountObjects(page);
        for (var i = 0; i < count; i++)
        {
            var obj = FPDFPage_GetObject(page, i);
            if (obj == IntPtr.Zero || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_IMAGE) continue;

            FPDF_IMAGEOBJ_METADATA meta;
            if (FPDFImageObj_GetImageMetadata(obj, page, &meta) == 0) continue;
            if (meta.bits_per_pixel <= 1 || meta.width < 64 || meta.height < 64) continue;

            var dpi = Math.Max(meta.horizontal_dpi, meta.vertical_dpi);
            var scale = dpi > maxDpi ? maxDpi / dpi : 1.0;
            var rawLength = FPDFImageObj_GetImageDataRaw(obj, null, 0);
            if (rawLength < 20_000) continue;

            var jpeg = EncodeImageAsJpeg(obj, scale, jpegQuality);
            if (jpeg == null || jpeg.Length >= rawLength * 0.85) continue;
            if (HasTransparency(document, page, obj)) continue;

            fixed (byte* data = jpeg)
            {
                var access = new FPDF_FILEACCESS { m_FileLen = (uint)jpeg.Length, m_GetBlock = &ReadMemoryBlock, m_Param = (IntPtr)data };
                var pageHandle = page;
                if (FPDFImageObj_LoadJpegFileInline(&pageHandle, 1, obj, &access) != 0) changed++;
            }
        }
        return changed;
    }

    private static byte[]? EncodeImageAsJpeg(IntPtr imageObject, double scale, int quality)
    {
        var bitmap = FPDFImageObj_GetBitmap(imageObject);
        if (bitmap == IntPtr.Zero) return null;
        try
        {
            var width = FPDFBitmap_GetWidth(bitmap);
            var height = FPDFBitmap_GetHeight(bitmap);
            var stride = FPDFBitmap_GetStride(bitmap);
            var buffer = FPDFBitmap_GetBuffer(bitmap);
            var format = FPDFBitmap_GetFormat(bitmap) switch
            {
                FPDFBitmap_Gray => PixelFormats.Gray8,
                FPDFBitmap_BGR => PixelFormats.Bgr24,
                _ => PixelFormats.Bgr32,
            };
            if (width <= 0 || height <= 0 || buffer == IntPtr.Zero) return null;

            BitmapSource source = BitmapSource.Create(width, height, 96, 96, format, null, buffer, stride * height, stride);
            if (scale < 0.98) source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            if (format == PixelFormats.Bgr32) source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);

            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            FPDFBitmap_Destroy(bitmap);
        }
    }

    /// <summary>JPEG has no alpha, so images drawn with a soft mask or color key are left alone.</summary>
    private static bool HasTransparency(IntPtr document, IntPtr page, IntPtr imageObject)
    {
        var rendered = FPDFImageObj_GetRenderedBitmap(document, page, imageObject);
        if (rendered == IntPtr.Zero) return true;
        try
        {
            if (FPDFBitmap_GetFormat(rendered) != FPDFBitmap_BGRA) return false;
            var width = FPDFBitmap_GetWidth(rendered);
            var height = FPDFBitmap_GetHeight(rendered);
            var stride = FPDFBitmap_GetStride(rendered);
            var buffer = (byte*)FPDFBitmap_GetBuffer(rendered);
            for (var y = 0; y < height; y++)
            {
                var row = buffer + (long)y * stride;
                for (var x = 0; x < width; x++)
                    if (row[x * 4 + 3] != 255) return true;
            }
            return false;
        }
        finally
        {
            FPDFBitmap_Destroy(rendered);
        }
    }

    [UnmanagedCallersOnly]
    private static int ReadMemoryBlock(IntPtr param, uint position, byte* destination, uint size)
    {
        Buffer.MemoryCopy((byte*)param + position, destination, size, size);
        return 1;
    }
}

/// <summary>PDFsharp-based steps PDFium can't do: AES-256 encryption and dropping unreferenced objects.</summary>
internal static class PdfSecurity
{
    public static byte[] Encrypt(byte[] pdf, PdfProtection protection)
    {
        using var input = new MemoryStream(pdf);
        var document = PdfSharp.Pdf.IO.PdfReader.Open(input);
        var settings = document.SecuritySettings;
        settings.UserPassword = protection.UserPassword;
        // A distinct owner password keeps the permission restrictions meaningful.
        settings.OwnerPassword = string.IsNullOrEmpty(protection.OwnerPassword) ? Guid.NewGuid().ToString("N") : protection.OwnerPassword;
        settings.PermitPrint = protection.AllowPrint;
        settings.PermitFullQualityPrint = protection.AllowPrint;
        settings.PermitExtractContent = protection.AllowCopy;
        settings.PermitModifyDocument = protection.AllowEdit;
        settings.PermitAssembleDocument = protection.AllowEdit;
        settings.PermitAnnotations = protection.AllowEdit;
        settings.PermitFormsFill = true;
        document.SecurityHandler.SetEncryptionToV5(true);
        document.Options.CompressContentStreams = true;
        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    /// <summary>Rewrites the file without unreferenced objects and with compressed content streams.</summary>
    public static byte[] Compact(byte[] pdf)
    {
        try
        {
            using var input = new MemoryStream(pdf);
            var document = PdfSharp.Pdf.IO.PdfReader.Open(input);
            document.Options.CompressContentStreams = true;
            using var output = new MemoryStream();
            document.Save(output, false);
            return output.Length > 0 && output.Length < pdf.Length ? output.ToArray() : pdf;
        }
        catch
        {
            return pdf;
        }
    }
}
