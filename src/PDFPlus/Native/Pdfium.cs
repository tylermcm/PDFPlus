using System.Runtime.InteropServices;

namespace PDFPlus.Native;

// Hand-written bindings for the subset of the PDFium C API this app uses.
// Signatures mirror the headers shipped in bblanchon.PDFium.Win32 155.0.8044.
// PDFium is not thread-safe: every call must happen under PdfLibrary.Sync.

[StructLayout(LayoutKind.Sequential)]
internal struct FS_SIZEF
{
    public float Width;
    public float Height;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEWRITE
{
    public int version;
    public delegate* unmanaged<FPDF_FILEWRITE*, byte*, uint, int> WriteBlock;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FORMFILLINFO
{
    public int version;
    public IntPtr Release;
    public delegate* unmanaged<FPDF_FORMFILLINFO*, IntPtr, double, double, double, double, void> FFI_Invalidate;
    public IntPtr FFI_OutputSelectedRect;
    public delegate* unmanaged<FPDF_FORMFILLINFO*, int, void> FFI_SetCursor;
    public IntPtr FFI_SetTimer;
    public IntPtr FFI_KillTimer;
    public IntPtr FFI_GetLocalTime;
    public delegate* unmanaged<FPDF_FORMFILLINFO*, void> FFI_OnChange;
    public IntPtr FFI_GetPage;
    public IntPtr FFI_GetCurrentPage;
    public IntPtr FFI_GetRotation;
    public delegate* unmanaged<FPDF_FORMFILLINFO*, byte*, void> FFI_ExecuteNamedAction;
    public IntPtr FFI_SetTextFieldFocus;
    public delegate* unmanaged<FPDF_FORMFILLINFO*, byte*, void> FFI_DoURIAction;
    public delegate* unmanaged<FPDF_FORMFILLINFO*, int, int, float*, int, void> FFI_DoGoToAction;
    public IntPtr m_pJsPlatform;
    public int xfa_disabled;
    // Version 2 callbacks (17 pointers). We register as version 1, so these stay zeroed.
    public fixed long Version2Callbacks[17];
}

internal static unsafe class Pdfium
{
    private const string Lib = "pdfium";

    public const int FPDF_ANNOT = 0x01;
    public const int FPDFBitmap_BGRx = 3;
    public const uint FPDF_MATCHCASE = 0x1;
    public const uint FPDF_MATCHWHOLEWORD = 0x2;
    public const uint PDFACTION_GOTO = 1;
    public const uint PDFACTION_URI = 3;
    public const int FPDF_FILLMODE_WINDING = 2;
    public const int FPDF_FONT_TRUETYPE = 2;

    public const int FXCT_ARROW = 0, FXCT_NESW = 1, FXCT_NWSE = 2, FXCT_VBEAM = 3, FXCT_HBEAM = 4, FXCT_HAND = 5;
    public const int FWL_EVENTFLAG_ShiftKey = 1, FWL_EVENTFLAG_ControlKey = 2, FWL_EVENTFLAG_AltKey = 4;

    // fpdfview.h
    [DllImport(Lib)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr dataBuf, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Lib)] public static extern uint FPDF_GetLastError();
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport(Lib)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int pageIndex, FS_SIZEF* size);
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Lib)] public static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, int* deviceX, int* deviceY);
    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Lib)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    // fpdf_formfill.h
    [DllImport(Lib)] public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, FPDF_FORMFILLINFO* formInfo);
    [DllImport(Lib)] public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);
    [DllImport(Lib)] public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);
    [DllImport(Lib)] public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);
    [DllImport(Lib)] public static extern int FORM_OnMouseMove(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);
    [DllImport(Lib)] public static extern int FORM_OnLButtonDown(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);
    [DllImport(Lib)] public static extern int FORM_OnLButtonUp(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);
    [DllImport(Lib)] public static extern int FORM_OnLButtonDoubleClick(IntPtr form, IntPtr page, int modifier, double pageX, double pageY);
    [DllImport(Lib)] public static extern int FORM_OnKeyDown(IntPtr form, IntPtr page, int keyCode, int modifier);
    [DllImport(Lib)] public static extern int FORM_OnChar(IntPtr form, IntPtr page, int ch, int modifier);
    [DllImport(Lib)] public static extern uint FORM_GetSelectedText(IntPtr form, IntPtr page, void* buffer, uint bufLen);
    [DllImport(Lib)] public static extern void FORM_ReplaceSelection(IntPtr form, IntPtr page, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Lib)] public static extern int FORM_SelectAllText(IntPtr form, IntPtr page);
    [DllImport(Lib)] public static extern int FORM_Undo(IntPtr form, IntPtr page);
    [DllImport(Lib)] public static extern int FORM_Redo(IntPtr form, IntPtr page);
    [DllImport(Lib)] public static extern int FORM_ForceToKillFocus(IntPtr form);
    [DllImport(Lib)] public static extern int FPDFPage_HasFormFieldAtPoint(IntPtr form, IntPtr page, double pageX, double pageY);
    [DllImport(Lib)] public static extern void FPDF_SetFormFieldHighlightColor(IntPtr form, int fieldType, uint color);
    [DllImport(Lib)] public static extern void FPDF_SetFormFieldHighlightAlpha(IntPtr form, byte alpha);
    [DllImport(Lib)] public static extern void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Lib)] public static extern int FPDF_GetFormType(IntPtr document);

    // fpdf_text.h
    [DllImport(Lib)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Lib)] public static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Lib)] public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [DllImport(Lib)] public static extern int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTolerance, double yTolerance);
    // Note PDFium's parameter order here: left, right, bottom, top (not the left/top/right/bottom used by GetRect).
    [DllImport(Lib)] public static extern int FPDFText_GetCharBox(IntPtr textPage, int index, double* left, double* right, double* bottom, double* top);
    [DllImport(Lib)] public static extern int FPDFText_GetText(IntPtr textPage, int startIndex, int count, ushort* result);
    [DllImport(Lib)] public static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);
    [DllImport(Lib)] public static extern int FPDFText_GetRect(IntPtr textPage, int rectIndex, double* left, double* top, double* right, double* bottom);
    [DllImport(Lib)] public static extern IntPtr FPDFText_FindStart(IntPtr textPage, [MarshalAs(UnmanagedType.LPWStr)] string findWhat, uint flags, int startIndex);
    [DllImport(Lib)] public static extern int FPDFText_FindNext(IntPtr handle);
    [DllImport(Lib)] public static extern int FPDFText_GetSchResultIndex(IntPtr handle);
    [DllImport(Lib)] public static extern int FPDFText_GetSchCount(IntPtr handle);
    [DllImport(Lib)] public static extern void FPDFText_FindClose(IntPtr handle);

    // fpdf_doc.h
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] public static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, void* buffer, uint bufLen);
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);
    [DllImport(Lib)] public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
    [DllImport(Lib)] public static extern uint FPDFAction_GetType(IntPtr action);
    [DllImport(Lib)] public static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);
    [DllImport(Lib)] public static extern uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, void* buffer, uint bufLen);
    [DllImport(Lib)] public static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);
    [DllImport(Lib)] public static extern int FPDFDest_GetLocationInPage(IntPtr dest, int* hasX, int* hasY, int* hasZoom, float* x, float* y, float* zoom);
    [DllImport(Lib)] public static extern IntPtr FPDFLink_GetLinkAtPoint(IntPtr page, double x, double y);
    [DllImport(Lib)] public static extern IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);
    [DllImport(Lib)] public static extern IntPtr FPDFLink_GetAction(IntPtr link);

    // fpdf_edit.h
    [DllImport(Lib)] public static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Lib)] public static extern IntPtr FPDFPage_New(IntPtr document, int pageIndex, double width, double height);
    [DllImport(Lib)] public static extern void FPDFPage_Delete(IntPtr document, int pageIndex);
    [DllImport(Lib)] public static extern int FPDF_MovePages(IntPtr document, int* pageIndices, uint pageIndicesLen, int destPageIndex);
    [DllImport(Lib)] public static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
    [DllImport(Lib)] public static extern int FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);
    [DllImport(Lib)] public static extern int FPDFPage_GenerateContent(IntPtr page);
    [DllImport(Lib)] public static extern void FPDFPageObj_Destroy(IntPtr pageObject);
    [DllImport(Lib)] public static extern void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c, double d, double e, double f);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_NewTextObj(IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string font, float fontSize);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float fontSize);
    [DllImport(Lib)] public static extern int FPDFText_SetText(IntPtr textObject, [MarshalAs(UnmanagedType.LPWStr)] string text);
    [DllImport(Lib)] public static extern IntPtr FPDFText_LoadFont(IntPtr document, byte* data, uint size, int fontType, int cid);
    [DllImport(Lib)] public static extern void FPDFFont_Close(IntPtr font);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetFillColor(IntPtr pageObject, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetStrokeColor(IntPtr pageObject, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetStrokeWidth(IntPtr pageObject, float width);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetLineJoin(IntPtr pageObject, int lineJoin);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetLineCap(IntPtr pageObject, int lineCap);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_CreateNewPath(float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_MoveTo(IntPtr path, float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_LineTo(IntPtr path, float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_Close(IntPtr path);
    [DllImport(Lib)] public static extern int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);

    // fpdf_ppo.h / fpdf_save.h
    [DllImport(Lib)] public static extern int FPDF_ImportPagesByIndex(IntPtr destDoc, IntPtr srcDoc, int* pageIndices, uint length, int index);
    [DllImport(Lib)] public static extern int FPDF_SaveAsCopy(IntPtr document, FPDF_FILEWRITE* fileWrite, uint flags);
    [DllImport(Lib)] public static extern int FPDF_GetSecurityHandlerRevision(IntPtr document);

    public const uint FPDF_REMOVE_SECURITY = 1 << 2;

    // Bitmap inspection (image compression)
    public const int FPDFBitmap_Gray = 1, FPDFBitmap_BGR = 2, FPDFBitmap_BGRA = 4;
    [DllImport(Lib)] public static extern int FPDFBitmap_GetFormat(IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetWidth(IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetHeight(IntPtr bitmap);
    [DllImport(Lib)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    // fpdf_edit.h: page objects and images
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    [DllImport(Lib)] public static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetType(IntPtr pageObject);
    [DllImport(Lib)] public static extern IntPtr FPDFImageObj_GetBitmap(IntPtr imageObject);
    [DllImport(Lib)] public static extern IntPtr FPDFImageObj_GetRenderedBitmap(IntPtr document, IntPtr page, IntPtr imageObject);
    [DllImport(Lib)] public static extern int FPDFImageObj_GetImageMetadata(IntPtr imageObject, IntPtr page, FPDF_IMAGEOBJ_METADATA* metadata);
    [DllImport(Lib)] public static extern uint FPDFImageObj_GetImageDataRaw(IntPtr imageObject, void* buffer, uint bufLen);
    [DllImport(Lib)] public static extern int FPDFImageObj_LoadJpegFileInline(IntPtr* pages, int count, IntPtr imageObject, FPDF_FILEACCESS* fileAccess);

    // fpdf_annot.h
    public const int FPDF_ANNOT_TEXT = 1, FPDF_ANNOT_LINK = 2, FPDF_ANNOT_SQUARE = 5, FPDF_ANNOT_CIRCLE = 6,
        FPDF_ANNOT_HIGHLIGHT = 9, FPDF_ANNOT_UNDERLINE = 10, FPDF_ANNOT_SQUIGGLY = 11, FPDF_ANNOT_STRIKEOUT = 12,
        FPDF_ANNOT_INK = 15, FPDF_ANNOT_POPUP = 16, FPDF_ANNOT_WIDGET = 20;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;
    public const int FPDFANNOT_COLORTYPE_Color = 0, FPDFANNOT_COLORTYPE_InteriorColor = 1;
    [DllImport(Lib)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Lib)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern int FPDFPage_GetAnnotIndex(IntPtr page, IntPtr annot);
    [DllImport(Lib)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetRect(IntPtr annot, FS_RECTF* rect);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetRect(IntPtr annot, FS_RECTF* rect);
    [DllImport(Lib)] public static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, FS_QUADPOINTSF* quadPoints);
    [DllImport(Lib)] public static extern int FPDFAnnot_AddInkStroke(IntPtr annot, FS_POINTF* points, nuint pointCount);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, ushort* buffer, uint bufLen);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [DllImport(Lib)] public static extern IntPtr FPDFAnnot_GetLinkedAnnot(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key);

    // fpdf_edit.h / fpdf_transformpage.h: editing existing page objects
    public const int FPDF_PAGEOBJ_TEXT = 1;
    public const int FPDF_TEXTRENDERMODE_FILL = 0, FPDF_TEXTRENDERMODE_INVISIBLE = 3, FPDF_TEXTRENDERMODE_CLIP = 7;
    [DllImport(Lib)] public static extern int FPDFPage_InsertObjectAtIndex(IntPtr page, IntPtr pageObject, nuint index);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr pageObject);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetMatrix(IntPtr pageObject, FS_MATRIX* matrix);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetMatrix(IntPtr pageObject, FS_MATRIX* matrix);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetBounds(IntPtr pageObject, float* left, float* bottom, float* right, float* top);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetRotatedBounds(IntPtr pageObject, FS_QUADPOINTSF* quadPoints);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetFillColor(IntPtr pageObject, uint* r, uint* g, uint* b, uint* a);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_GetClipPath(IntPtr pageObject);
    [DllImport(Lib)] public static extern void FPDFPageObj_TransformClipPath(IntPtr pageObject, double a, double b, double c, double d, double e, double f);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_NewImageObj(IntPtr document);
    [DllImport(Lib)] public static extern int FPDFImageObj_SetBitmap(IntPtr* pages, int count, IntPtr imageObject, IntPtr bitmap);
    [DllImport(Lib)] public static extern uint FPDFTextObj_GetText(IntPtr textObject, IntPtr textPage, ushort* buffer, uint length);
    [DllImport(Lib)] public static extern IntPtr FPDFTextObj_GetFont(IntPtr textObject);
    [DllImport(Lib)] public static extern int FPDFTextObj_GetFontSize(IntPtr textObject, float* size);
    [DllImport(Lib)] public static extern int FPDFTextObj_GetTextRenderMode(IntPtr textObject);
    [DllImport(Lib)] public static extern int FPDFTextObj_SetTextRenderMode(IntPtr textObject, int renderMode);
    [DllImport(Lib)] public static extern nuint FPDFFont_GetBaseFontName(IntPtr font, byte* buffer, nuint length);
    [DllImport(Lib)] public static extern nuint FPDFFont_GetFamilyName(IntPtr font, byte* buffer, nuint length);
    [DllImport(Lib)] public static extern int FPDFFont_GetFlags(IntPtr font);
    [DllImport(Lib)] public static extern int FPDFFont_GetWeight(IntPtr font);
    [DllImport(Lib)] public static extern int FPDFFont_GetItalicAngle(IntPtr font, int* angle);
    [DllImport(Lib)] public static extern int FPDFFont_GetIsEmbedded(IntPtr font);
    [DllImport(Lib)] public static extern IntPtr FPDFFont_GetGlyphPath(IntPtr font, uint glyph, float fontSize);
    [DllImport(Lib)] public static extern int FPDFGlyphPath_CountGlyphSegments(IntPtr glyphPath);
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_MATRIX
{
    public float a, b, c, d, e, f;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_RECTF
{
    public float left, top, right, bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_POINTF
{
    public float x, y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_QUADPOINTSF
{
    public float x1, y1, x2, y2, x3, y3, x4, y4;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEACCESS
{
    public uint m_FileLen;
    public delegate* unmanaged<IntPtr, uint, byte*, uint, int> m_GetBlock;
    public IntPtr m_Param;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FPDF_IMAGEOBJ_METADATA
{
    public uint width, height;
    public float horizontal_dpi, vertical_dpi;
    public uint bits_per_pixel;
    public int colorspace;
    public int marked_content_id;
}
