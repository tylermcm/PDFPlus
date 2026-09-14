using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFPlus.Native;
using static PDFPlus.Native.Pdfium;

namespace PDFPlus.Core;

public enum PageObjectKind { Text, Image }

public enum TextFontOutcome
{
    /// <summary>The PDF's own font had every character.</summary>
    OriginalFont,
    /// <summary>The same family was embedded from Windows.</summary>
    MatchingFont,
    /// <summary>The family isn't installed (or lacks characters), so a different one was used.</summary>
    SubstituteFont,
    /// <summary>The new text was empty, so the line was deleted.</summary>
    Removed,
}

public sealed record TextEditResult(TextFontOutcome Outcome, string FontFamily);

/// <summary>
/// Something editable on a page: a run of same-styled text on one line, or an image.
/// Geometry is in PDF page space, as extents along <see cref="U"/> (the baseline direction) and <see cref="V"/> (perpendicular, up).
/// </summary>
public sealed record PageObjectInfo(
    int PageIndex, PageObjectKind Kind, int[] Indices, int ObjectCount,
    Vector U, Vector V, double MinU, double MaxU, double MinV, double MaxV,
    string Text, double FontSize, Color Color, FontLook? Font)
{
    public bool IsText => Kind == PageObjectKind.Text;

    public Point Corner(double u, double v) => new(U.X * u + V.X * v, U.Y * u + V.Y * v);

    /// <summary>Bottom-left, bottom-right, top-right and top-left in the object's own frame.</summary>
    public Point[] Corners => [Corner(MinU, MinV), Corner(MaxU, MinV), Corner(MaxU, MaxV), Corner(MinU, MaxV)];

    public Rect Bounds
    {
        get
        {
            var corners = Corners;
            var rect = new Rect(corners[0], corners[1]);
            rect.Union(corners[2]);
            rect.Union(corners[3]);
            return rect;
        }
    }

    public bool Contains(Point pagePoint, double tolerance)
    {
        var p = (Vector)pagePoint;
        var u = p * U;
        var v = p * V;
        return u >= MinU - tolerance && u <= MaxU + tolerance && v >= MinV - tolerance && v <= MaxV + tolerance;
    }
}

public sealed unsafe partial class PdfDocument
{
    private const string PageChangedMessage = "The page changed since it was read. Try again.";

    // ---------------------------------------------------------------- reading objects

    private sealed class TextPiece
    {
        public IntPtr Font;
        public double Size;
        public uint Rgba;
        public Vector U;
        public double BaselineV, StartU, MinU, MaxU, MinV, MaxV;
        public string Text = "";
    }

    private sealed class RunBuilder
    {
        public readonly List<int> Indices = new();
        public readonly StringBuilder Text = new();
        public readonly TextPiece First;
        public double MinU, MaxU, MinV, MaxV;

        public RunBuilder(int index, TextPiece piece)
        {
            First = piece;
            (MinU, MaxU, MinV, MaxV) = (piece.MinU, piece.MaxU, piece.MinV, piece.MaxV);
            Indices.Add(index);
            Text.Append(piece.Text);
        }

        /// <summary>Same font, size and color, continuing the same line without a big gap.</summary>
        public bool Accepts(TextPiece piece)
        {
            var size = First.Size;
            var gap = piece.StartU - MaxU;
            return piece.Font == First.Font && piece.Rgba == First.Rgba &&
                   Math.Abs(piece.Size - size) <= size * 0.08 && piece.U * First.U > 0.999 &&
                   Math.Abs(piece.BaselineV - First.BaselineV) <= size * 0.25 &&
                   gap >= -size * 0.6 && gap <= size * 1.2;
        }

        public void Add(int index, TextPiece piece)
        {
            var gap = piece.StartU - MaxU;
            if (gap > First.Size * 0.15 && Text.Length > 0 && !char.IsWhiteSpace(Text[^1]) &&
                piece.Text.Length > 0 && !char.IsWhiteSpace(piece.Text[0]))
                Text.Append(' ');
            Text.Append(piece.Text);
            Indices.Add(index);
            MinU = Math.Min(MinU, piece.MinU);
            MaxU = Math.Max(MaxU, piece.MaxU);
            MinV = Math.Min(MinV, piece.MinV);
            MaxV = Math.Max(MaxV, piece.MaxV);
        }
    }

    /// <summary>Text runs and images on a page, in drawing order. Text inside form XObjects and invisible (OCR) text is skipped.</summary>
    public List<PageObjectInfo> GetPageObjects(int index)
    {
        var result = new List<PageObjectInfo>();
        lock (PdfLibrary.Sync)
        {
            var page = PageLocked(index);
            var textPage = TextPageLocked(index);
            if (page == IntPtr.Zero || textPage == IntPtr.Zero) return result;
            var count = FPDFPage_CountObjects(page);
            RunBuilder? run = null;

            void Flush()
            {
                var text = run == null ? "" : CleanLine(run.Text.ToString());
                if (run != null && text.Length > 0)
                {
                    var first = run.First;
                    var color = Color.FromArgb((byte)first.Rgba, (byte)(first.Rgba >> 24), (byte)(first.Rgba >> 16), (byte)(first.Rgba >> 8));
                    result.Add(new PageObjectInfo(index, PageObjectKind.Text, run.Indices.ToArray(), count,
                        first.U, new Vector(-first.U.Y, first.U.X), run.MinU, run.MaxU, run.MinV, run.MaxV,
                        text, first.Size, color, DescribeFontLocked(first.Font)));
                }
                run = null;
            }

            for (var i = 0; i < count; i++)
            {
                var obj = FPDFPage_GetObject(page, i);
                if (obj == IntPtr.Zero) continue;
                switch (FPDFPageObj_GetType(obj))
                {
                    case FPDF_PAGEOBJ_IMAGE:
                        Flush();
                        if (ObjectQuad(obj) is { } quad)
                        {
                            double minX = quad.Min(p => p.X), maxX = quad.Max(p => p.X), minY = quad.Min(p => p.Y), maxY = quad.Max(p => p.Y);
                            if (maxX - minX >= 1 && maxY - minY >= 1)
                                result.Add(new PageObjectInfo(index, PageObjectKind.Image, [i], count, new Vector(1, 0), new Vector(0, 1),
                                    minX, maxX, minY, maxY, "", 0, Colors.Transparent, null));
                        }
                        break;
                    case FPDF_PAGEOBJ_TEXT:
                        if (ReadTextPiece(obj, textPage) is not { } piece) break;
                        if (run != null && run.Accepts(piece)) run.Add(i, piece);
                        else if (piece.Text.Trim().Length > 0)
                        {
                            Flush();
                            run = new RunBuilder(i, piece);
                        }
                        break;
                }
            }
            Flush();
        }
        return result;
    }

    private static TextPiece? ReadTextPiece(IntPtr obj, IntPtr textPage)
    {
        var mode = FPDFTextObj_GetTextRenderMode(obj);
        if (mode is < 0 or FPDF_TEXTRENDERMODE_INVISIBLE or FPDF_TEXTRENDERMODE_CLIP) return null;
        FS_MATRIX m;
        float fontSize;
        if (FPDFPageObj_GetMatrix(obj, &m) == 0 || FPDFTextObj_GetFontSize(obj, &fontSize) == 0) return null;

        var u = new Vector(m.a, m.b);
        var scale = u.Length;
        if (scale < 1e-6) return null;
        u /= scale;
        var v = new Vector(-u.Y, u.X);
        var size = fontSize * Math.Abs(m.a * m.d - m.b * m.c) / scale;
        if (size < 0.5) return null;

        uint r = 0, g = 0, b = 0, a = 255;
        FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a);
        var origin = new Vector(m.e, m.f);
        var piece = new TextPiece
        {
            Font = FPDFTextObj_GetFont(obj),
            Size = size,
            Rgba = (r << 24) | (g << 16) | (b << 8) | a,
            U = u,
            BaselineV = origin * v,
            StartU = origin * u,
            Text = ObjectText(obj, textPage),
        };
        // A line box from the font size, grown to the glyphs' actual extent.
        piece.MinU = piece.MaxU = piece.StartU;
        piece.MinV = piece.BaselineV - size * 0.22;
        piece.MaxV = piece.BaselineV + size * 0.9;
        if (ObjectQuad(obj) is { } quad)
        {
            foreach (var p in quad)
            {
                var pu = (Vector)p * u;
                var pv = (Vector)p * v;
                piece.MinU = Math.Min(piece.MinU, pu);
                piece.MaxU = Math.Max(piece.MaxU, pu);
                piece.MinV = Math.Min(piece.MinV, pv);
                piece.MaxV = Math.Max(piece.MaxV, pv);
            }
        }
        return piece;
    }

    private static Point[]? ObjectQuad(IntPtr obj)
    {
        FS_QUADPOINTSF q;
        if (FPDFPageObj_GetRotatedBounds(obj, &q) != 0)
            return [new Point(q.x1, q.y1), new Point(q.x2, q.y2), new Point(q.x3, q.y3), new Point(q.x4, q.y4)];
        float left, bottom, right, top;
        if (FPDFPageObj_GetBounds(obj, &left, &bottom, &right, &top) != 0)
            return [new Point(left, bottom), new Point(right, bottom), new Point(right, top), new Point(left, top)];
        return null;
    }

    private static string ObjectText(IntPtr obj, IntPtr textPage)
    {
        var length = FPDFTextObj_GetText(obj, textPage, null, 0);
        if (length <= 2) return "";
        var buffer = new ushort[(length + 1) / 2];
        fixed (ushort* p = buffer)
        {
            FPDFTextObj_GetText(obj, textPage, p, length);
            return new string((char*)p, 0, (int)(length / 2) - 1);
        }
    }

    private static FontLook DescribeFontLocked(IntPtr font)
    {
        if (font == IntPtr.Zero) return FontLook.Default;
        string baseName = "", family = "";
        var length = FPDFFont_GetBaseFontName(font, null, 0);
        if (length > 1)
        {
            var buffer = new byte[(int)length];
            fixed (byte* p = buffer) FPDFFont_GetBaseFontName(font, p, length);
            baseName = Encoding.UTF8.GetString(buffer, 0, (int)length - 1);
        }
        length = FPDFFont_GetFamilyName(font, null, 0);
        if (length > 1)
        {
            var buffer = new byte[(int)length];
            fixed (byte* p = buffer) FPDFFont_GetFamilyName(font, p, length);
            family = Encoding.UTF8.GetString(buffer, 0, (int)length - 1);
        }
        int angle;
        var italicAngle = FPDFFont_GetItalicAngle(font, &angle) != 0 ? angle : 0;
        return FontResolver.Describe(baseName, family, FPDFFont_GetFlags(font), FPDFFont_GetWeight(font), italicAngle, FPDFFont_GetIsEmbedded(font) != 0);
    }

    /// <summary>One line: control characters and line breaks become spaces, ends trimmed.</summary>
    private static string CleanLine(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text) builder.Append(ch < 0x20 || ch == '\uFFFE' ? ' ' : ch);
        return builder.ToString().Trim();
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---------------------------------------------------------------- editing

    /// <summary>
    /// Runs an edit on a page's objects, regenerates its content stream and reloads the page.
    /// If the edit throws, the document is rolled back to the state before it started.
    /// </summary>
    private void EditObjects(int index, Action<IntPtr, IntPtr> edit)
    {
        if ((uint)index >= (uint)PageCount) return;
        PushUndo();
        try
        {
            lock (PdfLibrary.Sync)
            {
                var page = PageLocked(index);
                if (page == IntPtr.Zero) throw new InvalidOperationException("The page couldn't be loaded.");
                if (_form != IntPtr.Zero && _formFocusPage == index)
                {
                    FORM_ForceToKillFocus(_form);
                    _formFocusPage = -1;
                }
                // Removing text objects invalidates text pages, so drop the cached one first.
                if (_textPages.Remove(index, out var textPage)) FPDFText_ClosePage(textPage);
                edit(_file.Handle, page);
                FPDFPage_GenerateContent(page);
                ClosePageLocked(index);
            }
        }
        catch
        {
            if (!_suppressUndo && _undo.Count > 0)
            {
                var snapshot = _undo[^1];
                _undo.RemoveAt(_undo.Count - 1);
                Restore(snapshot);
            }
            throw;
        }
        MarkDirty();
        PageContentChanged?.Invoke(this, index);
    }

    private static IntPtr[]? ResolveObjectsLocked(IntPtr page, PageObjectInfo info)
    {
        if (FPDFPage_CountObjects(page) != info.ObjectCount) return null;
        var expected = info.IsText ? FPDF_PAGEOBJ_TEXT : FPDF_PAGEOBJ_IMAGE;
        var objects = new IntPtr[info.Indices.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            var obj = FPDFPage_GetObject(page, info.Indices[i]);
            if (obj == IntPtr.Zero || FPDFPageObj_GetType(obj) != expected) return null;
            objects[i] = obj;
        }
        return objects;
    }

    /// <summary>
    /// Replaces a text run with new text at the same position, size and color. The PDF's own font is reused when it can
    /// show every character; otherwise a subset of the matching Windows font is embedded. Empty text deletes the run.
    /// </summary>
    public TextEditResult ReplaceText(PageObjectInfo run, string text)
    {
        if (!run.IsText) throw new ArgumentException("That isn't text.", nameof(run));
        text = CleanLine(text);
        var look = run.Font ?? FontLook.Default;
        var result = new TextEditResult(TextFontOutcome.Removed, look.Family);

        EditObjects(run.PageIndex, (doc, page) =>
        {
            var objects = ResolveObjectsLocked(page, run) ?? throw new InvalidOperationException(PageChangedMessage);
            var first = objects[0];
            FS_MATRIX matrix;
            float size;
            uint r = 0, g = 0, b = 0, a = 255;
            FPDFPageObj_GetMatrix(first, &matrix);
            FPDFTextObj_GetFontSize(first, &size);
            FPDFPageObj_GetFillColor(first, &r, &g, &b, &a);
            var mode = FPDFTextObj_GetTextRenderMode(first);
            var font = FPDFTextObj_GetFont(first);
            var style = new TextStyle(matrix, size, r, g, b, a, mode, (nuint)run.Indices[0]);

            if (text.Length > 0)
            {
                var created = IntPtr.Zero;
                if (font != IntPtr.Zero && CanReuseFontLocked(page, font, look, size, text))
                {
                    created = AddTextObjectLocked(doc, page, font, text, style, verify: true);
                    if (created != IntPtr.Zero) result = new TextEditResult(TextFontOutcome.OriginalFont, look.Family);
                }
                if (created == IntPtr.Zero)
                {
                    var embedded = FontResolver.CreateEmbeddableFont(look, text)
                                   ?? throw new InvalidOperationException("No installed font can show this text.");
                    IntPtr loaded;
                    fixed (byte* data = embedded.Data)
                        loaded = FPDFText_LoadFont(doc, data, (uint)embedded.Data.Length, FPDF_FONT_TRUETYPE, 1);
                    if (loaded == IntPtr.Zero) throw new InvalidOperationException("The replacement font couldn't be loaded.");
                    try
                    {
                        created = AddTextObjectLocked(doc, page, loaded, text, style, verify: false);
                    }
                    finally
                    {
                        FPDFFont_Close(loaded);
                    }
                    if (created == IntPtr.Zero) throw new InvalidOperationException("The new text couldn't be added.");
                    var same = string.Equals(embedded.Family, look.Family, StringComparison.OrdinalIgnoreCase);
                    result = new TextEditResult(same ? TextFontOutcome.MatchingFont : TextFontOutcome.SubstituteFont, embedded.Family);
                }
            }

            foreach (var obj in objects)
            {
                FPDFPage_RemoveObject(page, obj);
                FPDFPageObj_Destroy(obj);
            }
        });
        return result;
    }

    private readonly record struct TextStyle(FS_MATRIX Matrix, float Size, uint R, uint G, uint B, uint A, int Mode, nuint InsertAt);

    /// <summary>
    /// A subset font only contains the glyphs its creator used, so it's reused only for characters already drawn with it on this
    /// page. Complete fonts (embedded or standard) are checked glyph by glyph. Encoding is verified after insertion.
    /// </summary>
    private static bool CanReuseFontLocked(IntPtr page, IntPtr font, FontLook look, float size, string text)
    {
        HashSet<Rune>? seen = null;
        if (look.IsSubset)
        {
            seen = new HashSet<Rune>();
            var textPage = FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero) return false;
            try
            {
                var count = FPDFPage_CountObjects(page);
                for (var i = 0; i < count; i++)
                {
                    var obj = FPDFPage_GetObject(page, i);
                    if (obj != IntPtr.Zero && FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_TEXT && FPDFTextObj_GetFont(obj) == font)
                        foreach (var rune in ObjectText(obj, textPage).EnumerateRunes()) seen.Add(rune);
                }
            }
            finally
            {
                FPDFText_ClosePage(textPage);
            }
        }

        foreach (var rune in text.EnumerateRunes().Distinct())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            if (seen != null)
            {
                if (!seen.Contains(rune)) return false;
                continue;
            }
            var path = FPDFFont_GetGlyphPath(font, (uint)rune.Value, size);
            if (path == IntPtr.Zero || FPDFGlyphPath_CountGlyphSegments(path) <= 0) return false;
        }
        return true;
    }

    private static IntPtr AddTextObjectLocked(IntPtr doc, IntPtr page, IntPtr font, string text, TextStyle style, bool verify)
    {
        var obj = FPDFPageObj_CreateTextObj(doc, font, style.Size);
        if (obj == IntPtr.Zero) return IntPtr.Zero;
        FPDFText_SetText(obj, text);
        FPDFPageObj_SetFillColor(obj, style.R, style.G, style.B, style.A);
        if (style.Mode > FPDF_TEXTRENDERMODE_FILL && style.Mode != FPDF_TEXTRENDERMODE_INVISIBLE)
            FPDFTextObj_SetTextRenderMode(obj, style.Mode);
        var matrix = style.Matrix;
        FPDFPageObj_SetMatrix(obj, &matrix);
        if (FPDFPage_InsertObjectAtIndex(page, obj, style.InsertAt) == 0) FPDFPage_InsertObject(page, obj);
        if (!verify) return obj;

        // Read the text back: a character the font can't encode comes back different.
        var textPage = FPDFText_LoadPage(page);
        var readBack = textPage == IntPtr.Zero ? "" : ObjectText(obj, textPage);
        if (textPage != IntPtr.Zero) FPDFText_ClosePage(textPage);
        if (Collapse(readBack) == Collapse(text)) return obj;

        FPDFPage_RemoveObject(page, obj);
        FPDFPageObj_Destroy(obj);
        return IntPtr.Zero;
    }

    /// <summary>Moves a text run or image by a distance in page space.</summary>
    public void MoveObject(PageObjectInfo info, Vector pageDelta)
    {
        if (pageDelta.Length < 0.01) return;
        TransformObject(info, Affine.Translation(pageDelta.X, pageDelta.Y));
    }

    /// <summary>Scales an image so its bounding box becomes <paramref name="pageBounds"/> (page space).</summary>
    public void ResizeImage(PageObjectInfo info, Rect pageBounds)
    {
        var old = info.Bounds;
        if (old.Width < 0.01 || old.Height < 0.01 || pageBounds.Width < 1 || pageBounds.Height < 1) return;
        TransformObject(info, Affine.Translation(-old.X, -old.Y)
            .Then(Affine.Scale(pageBounds.Width / old.Width, pageBounds.Height / old.Height))
            .Then(Affine.Translation(pageBounds.X, pageBounds.Y)));
    }

    private void TransformObject(PageObjectInfo info, Affine m)
    {
        EditObjects(info.PageIndex, (_, page) =>
        {
            foreach (var obj in ResolveObjectsLocked(page, info) ?? throw new InvalidOperationException(PageChangedMessage))
            {
                FPDFPageObj_Transform(obj, m.A, m.B, m.C, m.D, m.E, m.F);
                // The clip travels with the object; otherwise moving it out of its clip would hide it.
                if (FPDFPageObj_GetClipPath(obj) != IntPtr.Zero) FPDFPageObj_TransformClipPath(obj, m.A, m.B, m.C, m.D, m.E, m.F);
            }
        });
    }

    public void DeleteObject(PageObjectInfo info)
    {
        EditObjects(info.PageIndex, (_, page) =>
        {
            foreach (var obj in ResolveObjectsLocked(page, info) ?? throw new InvalidOperationException(PageChangedMessage))
            {
                FPDFPage_RemoveObject(page, obj);
                FPDFPageObj_Destroy(obj);
            }
        });
    }

    // ---------------------------------------------------------------- adding images

    /// <summary>
    /// Adds a picture centred on a display point, at its natural size but no more than half the page.
    /// JPEGs are embedded as-is; other formats keep full quality (with transparency). Returns the display rectangle used.
    /// </summary>
    public Rect AddImage(int index, string path, Point displayCenter) => AddImage(index, LoadImage(path), displayCenter, 0.5);

    /// <summary>A new untitled PDF with one page per picture. Each page has the picture's shape, 11 inches on the long side.</summary>
    public static PdfDocument CreateFromImages(IReadOnlyList<string> paths)
    {
        var doc = new PdfDocument(PdfFile.CreateEmpty(), null, null) { _suppressUndo = true };
        try
        {
            foreach (var path in paths)
            {
                var image = LoadImage(path);
                var scale = 792 / Math.Max(image.Width, image.Height);
                double width = image.Width * scale, height = image.Height * scale;
                doc.InsertBlankPage(doc.PageCount, width, height);
                doc.AddImage(doc.PageCount - 1, image, new Point(width / 2, height / 2), 1);
            }
        }
        catch
        {
            doc.Dispose();
            throw;
        }
        doc._suppressUndo = false;
        doc.IsDirty = true;
        return doc;
    }

    /// <summary>Decoded picture plus its upright size in points.</summary>
    private sealed record LoadedImage(byte[] Bytes, BitmapFrame Frame, bool IsJpeg, int Orientation, double Width, double Height);

    private static LoadedImage LoadImage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        BitmapFrame frame;
        try
        {
            frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} isn't an image PDFPlus can read.", ex);
        }

        var isJpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
        var orientation = isJpeg ? ReadOrientation(frame) : 1;
        double dpiX = frame.DpiX > 1 ? frame.DpiX : 96, dpiY = frame.DpiY > 1 ? frame.DpiY : 96;
        var width = frame.PixelWidth * 72 / dpiX;
        var height = frame.PixelHeight * 72 / dpiY;
        if (orientation is 6 or 8) (width, height) = (height, width);
        return new LoadedImage(bytes, frame, isJpeg, orientation, width, height);
    }

    /// <summary>Adds a decoded picture. <paramref name="maxPageFraction"/> below 1 also stops small pictures being enlarged.</summary>
    private Rect AddImage(int index, LoadedImage image, Point displayCenter, double maxPageFraction)
    {
        var (bytes, frame, isJpeg, orientation, width, height) = image;
        var pageSize = PageSizes[index];
        var fit = Math.Min(pageSize.Width * maxPageFraction / width, pageSize.Height * maxPageFraction / height);
        if (maxPageFraction < 1) fit = Math.Min(1, fit);
        width *= fit;
        height *= fit;
        var x = Math.Clamp(displayCenter.X - width / 2, 0, Math.Max(0, pageSize.Width - width));
        var y = Math.Clamp(displayCenter.Y - height / 2, 0, Math.Max(0, pageSize.Height - height));
        var displayRect = new Rect(x, y, width, height);

        byte[]? pixels = null;
        var opaque = true;
        if (!isJpeg)
        {
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            converted.CopyPixels(pixels, frame.PixelWidth * 4, 0);
            for (var i = 3; i < pixels.Length && opaque; i += 4) opaque = pixels[i] == 255;
        }

        EditObjects(index, (doc, page) =>
        {
            var obj = FPDFPageObj_NewImageObj(doc);
            if (obj == IntPtr.Zero) throw new InvalidOperationException("The image couldn't be created.");
            var loaded = false;
            if (pixels == null)
            {
                fixed (byte* data = bytes)
                {
                    var access = new FPDF_FILEACCESS { m_FileLen = (uint)bytes.Length, m_GetBlock = &ReadMemoryBlock, m_Param = (IntPtr)data };
                    loaded = FPDFImageObj_LoadJpegFileInline(null, 0, obj, &access) != 0;
                }
            }
            else
            {
                fixed (byte* data = pixels)
                {
                    var bitmap = FPDFBitmap_CreateEx(frame.PixelWidth, frame.PixelHeight, opaque ? FPDFBitmap_BGRx : FPDFBitmap_BGRA, (IntPtr)data, frame.PixelWidth * 4);
                    if (bitmap != IntPtr.Zero)
                    {
                        loaded = FPDFImageObj_SetBitmap(null, 0, obj, bitmap) != 0;
                        FPDFBitmap_Destroy(bitmap);
                    }
                }
            }
            if (!loaded)
            {
                FPDFPageObj_Destroy(obj);
                throw new InvalidDataException("The image data couldn't be embedded.");
            }

            // Image space is the unit square (y up). Undo EXIF rotation, stretch over the display rect, then map to the page.
            var upright = orientation switch
            {
                3 => new Affine(-1, 0, 0, -1, 1, 1),
                6 => new Affine(0, -1, 1, 0, 0, 1),
                8 => new Affine(0, 1, -1, 0, 1, 0),
                _ => Affine.Identity,
            };
            var m = upright
                .Then(new Affine(displayRect.Width, 0, 0, -displayRect.Height, displayRect.X, displayRect.Bottom))
                .Then(PageToDisplayLocked(index).Invert());
            var matrix = new FS_MATRIX { a = (float)m.A, b = (float)m.B, c = (float)m.C, d = (float)m.D, e = (float)m.E, f = (float)m.F };
            FPDFPageObj_SetMatrix(obj, &matrix);
            FPDFPage_InsertObject(page, obj);
        });
        return displayRect;
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation)
                return orientation;
        }
        catch
        {
            // Missing or unreadable EXIF: treat as upright.
        }
        return 1;
    }
}
