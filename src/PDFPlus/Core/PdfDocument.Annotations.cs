using System.Windows;
using System.Windows.Media;
using PDFPlus.Native;
using static PDFPlus.Native.Pdfium;

namespace PDFPlus.Core;

public enum MarkupKind { Highlight, Underline, Strikeout, Squiggly }

public enum ShapeKind { Rectangle, Ellipse }

/// <summary>An existing annotation. Bounds are in PDF page space.</summary>
public sealed record AnnotationInfo(int PageIndex, int Index, int Subtype, Rect Bounds, string Contents)
{
    public bool IsNote => Subtype == FPDF_ANNOT_TEXT;
}

public sealed unsafe partial class PdfDocument
{
    private static readonly HashSet<int> EditableAnnotationTypes =
    [
        FPDF_ANNOT_TEXT, 3 /* free text */, 4 /* line */, FPDF_ANNOT_SQUARE, FPDF_ANNOT_CIRCLE, 7, 8,
        FPDF_ANNOT_HIGHLIGHT, FPDF_ANNOT_UNDERLINE, FPDF_ANNOT_SQUIGGLY, FPDF_ANNOT_STRIKEOUT, 13 /* stamp */, 14, FPDF_ANNOT_INK,
    ];

    /// <summary>
    /// Runs an annotation edit on a page, then reloads that page so the form environment's cached
    /// annotation list can't go stale. PDFium generates appearance streams on the next render.
    /// </summary>
    private void EditAnnotations(int index, Action<IntPtr, Affine> edit)
    {
        if ((uint)index >= (uint)PageCount) return;
        PushUndo();
        lock (PdfLibrary.Sync)
        {
            var page = PageLocked(index);
            if (page == IntPtr.Zero) return;
            edit(page, PageToDisplayLocked(index).Invert());
            if (_form != IntPtr.Zero && _formFocusPage == index)
            {
                FORM_ForceToKillFocus(_form);
                _formFocusPage = -1;
            }
            ClosePageLocked(index);
        }
        MarkDirty();
        PageContentChanged?.Invoke(this, index);
    }

    private static IntPtr CreateAnnot(IntPtr page, int subtype, Color color)
    {
        var annot = FPDFPage_CreateAnnot(page, subtype);
        if (annot == IntPtr.Zero) return IntPtr.Zero;
        FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);
        FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, color.A);
        FPDFAnnot_SetStringValue(annot, "T", Environment.UserName);
        FPDFAnnot_SetStringValue(annot, "M", PdfDate(DateTimeOffset.Now));
        return annot;
    }

    private static string PdfDate(DateTimeOffset time)
    {
        var offset = time.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"D:{time:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
    }

    private static void SetRect(IntPtr annot, Rect pageRect)
    {
        var rect = new FS_RECTF
        {
            left = (float)pageRect.Left,
            bottom = (float)pageRect.Top,
            right = (float)pageRect.Right,
            top = (float)pageRect.Bottom,
        };
        FPDFAnnot_SetRect(annot, &rect);
    }

    /// <summary>Highlight / underline / strikeout over text. Rects come from <see cref="TextRects"/> (page space).</summary>
    public void AddTextMarkup(int index, MarkupKind kind, IReadOnlyList<Rect> textRects, Color color)
    {
        if (textRects.Count == 0) return;
        var subtype = kind switch
        {
            MarkupKind.Highlight => FPDF_ANNOT_HIGHLIGHT,
            MarkupKind.Underline => FPDF_ANNOT_UNDERLINE,
            MarkupKind.Strikeout => FPDF_ANNOT_STRIKEOUT,
            _ => FPDF_ANNOT_SQUIGGLY,
        };
        EditAnnotations(index, (page, _) =>
        {
            var annot = CreateAnnot(page, subtype, color);
            if (annot == IntPtr.Zero) return;
            var bounds = Rect.Empty;
            foreach (var r in textRects)
            {
                // Quad order: upper-left, upper-right, lower-left, lower-right.
                var quad = new FS_QUADPOINTSF
                {
                    x1 = (float)r.Left, y1 = (float)r.Bottom,
                    x2 = (float)r.Right, y2 = (float)r.Bottom,
                    x3 = (float)r.Left, y3 = (float)r.Top,
                    x4 = (float)r.Right, y4 = (float)r.Top,
                };
                FPDFAnnot_AppendAttachmentPoints(annot, &quad);
                bounds.Union(r);
            }
            SetRect(annot, bounds);
            FPDFPage_CloseAnnot(annot);
        });
    }

    /// <summary>Freehand ink. Each stroke is a polyline in display space.</summary>
    public void AddInk(int index, IReadOnlyList<Point[]> displayStrokes, Color color, double width)
    {
        var strokes = displayStrokes.Where(s => s.Length > 0).ToList();
        if (strokes.Count == 0) return;
        EditAnnotations(index, (page, displayToPage) =>
        {
            var annot = CreateAnnot(page, FPDF_ANNOT_INK, color);
            if (annot == IntPtr.Zero) return;
            FPDFAnnot_SetBorder(annot, 0, 0, (float)width);
            var bounds = Rect.Empty;
            foreach (var stroke in strokes)
            {
                // A single point would draw nothing; duplicate it so a dot appears.
                var source = stroke.Length == 1 ? [stroke[0], stroke[0] + new Vector(0.01, 0.01)] : stroke;
                var points = new FS_POINTF[source.Length];
                for (var i = 0; i < source.Length; i++)
                {
                    var p = displayToPage.Transform(source[i]);
                    points[i] = new FS_POINTF { x = (float)p.X, y = (float)p.Y };
                    bounds.Union(p);
                }
                fixed (FS_POINTF* ptr = points) FPDFAnnot_AddInkStroke(annot, ptr, (nuint)points.Length);
            }
            bounds.Inflate(width, width);
            SetRect(annot, bounds);
            FPDFPage_CloseAnnot(annot);
        });
    }

    /// <summary>Line with an arrowhead at <paramref name="to"/>, stored as an ink annotation so every viewer shows it.</summary>
    public void AddArrow(int index, Point from, Point to, Color color, double width)
    {
        var direction = to - from;
        if (direction.Length < 0.5) return;
        direction.Normalize();
        var headLength = Math.Max(8, width * 4);
        var normal = new Vector(-direction.Y, direction.X);
        var back = to - direction * headLength;
        var left = back + normal * headLength * 0.5;
        var right = back - normal * headLength * 0.5;
        AddInk(index, [[from, to], [left, to, right]], color, width);
    }

    public void AddShape(int index, ShapeKind kind, Rect displayRect, Color color, double width)
    {
        if (displayRect.Width < 1 || displayRect.Height < 1) return;
        EditAnnotations(index, (page, displayToPage) =>
        {
            var annot = CreateAnnot(page, kind == ShapeKind.Rectangle ? FPDF_ANNOT_SQUARE : FPDF_ANNOT_CIRCLE, color);
            if (annot == IntPtr.Zero) return;
            FPDFAnnot_SetBorder(annot, 0, 0, (float)width);
            SetRect(annot, displayToPage.TransformBounds(displayRect));
            FPDFPage_CloseAnnot(annot);
        });
    }

    /// <summary>Sticky note whose icon's top-left is at a display point.</summary>
    public void AddNote(int index, Point display, string text, Color color)
    {
        EditAnnotations(index, (page, displayToPage) =>
        {
            var annot = CreateAnnot(page, FPDF_ANNOT_TEXT, color);
            if (annot == IntPtr.Zero) return;
            FPDFAnnot_SetStringValue(annot, "Contents", text);
            SetRect(annot, displayToPage.TransformBounds(new Rect(display, new Size(20, 20))));
            FPDFPage_CloseAnnot(annot);
        });
    }

    public List<AnnotationInfo> GetAnnotations(int index)
    {
        var result = new List<AnnotationInfo>();
        lock (PdfLibrary.Sync)
        {
            var page = PageLocked(index);
            if (page == IntPtr.Zero) return result;
            var count = FPDFPage_GetAnnotCount(page);
            for (var i = 0; i < count; i++)
            {
                var annot = FPDFPage_GetAnnot(page, i);
                if (annot == IntPtr.Zero) continue;
                try
                {
                    var subtype = FPDFAnnot_GetSubtype(annot);
                    if (!EditableAnnotationTypes.Contains(subtype)) continue;
                    FS_RECTF r;
                    if (FPDFAnnot_GetRect(annot, &r) == 0) continue;
                    var bounds = new Rect(Math.Min(r.left, r.right), Math.Min(r.top, r.bottom), Math.Abs(r.right - r.left), Math.Abs(r.top - r.bottom));
                    result.Add(new AnnotationInfo(index, i, subtype, bounds, ReadAnnotString(annot, "Contents")));
                }
                finally
                {
                    FPDFPage_CloseAnnot(annot);
                }
            }
        }
        return result;
    }

    private static string ReadAnnotString(IntPtr annot, string key)
    {
        var length = FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (length <= 2) return "";
        var buffer = new ushort[(length + 1) / 2];
        fixed (ushort* p = buffer)
        {
            FPDFAnnot_GetStringValue(annot, key, p, length);
            return new string((char*)p, 0, (int)(length / 2) - 1);
        }
    }

    public void DeleteAnnotation(AnnotationInfo info)
    {
        EditAnnotations(info.PageIndex, (page, _) =>
        {
            var annot = FPDFPage_GetAnnot(page, info.Index);
            if (annot == IntPtr.Zero) return;
            var toRemove = new List<int> { info.Index };
            var popup = FPDFAnnot_GetLinkedAnnot(annot, "Popup");
            if (popup != IntPtr.Zero)
            {
                var popupIndex = FPDFPage_GetAnnotIndex(page, popup);
                if (popupIndex >= 0) toRemove.Add(popupIndex);
                FPDFPage_CloseAnnot(popup);
            }
            FPDFPage_CloseAnnot(annot);
            foreach (var i in toRemove.OrderByDescending(i => i)) FPDFPage_RemoveAnnot(page, i);
        });
    }

    public void UpdateNote(AnnotationInfo info, string text)
    {
        EditAnnotations(info.PageIndex, (page, _) =>
        {
            var annot = FPDFPage_GetAnnot(page, info.Index);
            if (annot == IntPtr.Zero) return;
            FPDFAnnot_SetStringValue(annot, "Contents", text);
            FPDFAnnot_SetStringValue(annot, "M", PdfDate(DateTimeOffset.Now));
            FPDFPage_CloseAnnot(annot);
        });
    }
}
