# PDFPlus

A fast, portable PDF viewer and editor for Windows. One exe, no installer, no account, no subscription.

## Features

**Viewing**
- Tabs (open many PDFs at once; opening a PDF while PDFPlus is running adds a tab)
- Smooth continuous scrolling with background rendering. Only visible pages are drawn, so huge documents stay fast
- Zoom: Ctrl+wheel at the cursor, presets, fit width, fit page. Very high zoom renders a sharp tile of just the visible area
- Page thumbnails, bookmarks (outline), clickable links (web links ask before opening)
- Find with match case / whole word, highlighted results, next/previous
- Text selection and copy (drag, double-click a word, Ctrl+A for the page)
- Password-protected PDFs, printing, light and dark themes

**Page tools**
- Reorder pages by dragging thumbnails (multi-select with Ctrl/Shift)
- Rotate, delete, insert blank pages, insert pages from other PDFs (or drop PDFs onto the thumbnails)
- Extract a page range to a new PDF, split a document every N pages, combine several PDFs into one

**Annotate** (toolbar → Annotate)
- Highlight, underline and strike out text by dragging across it
- Freehand pen, rectangles, ellipses, arrows and sticky notes, in six colors and three line widths
- Click an annotation to select it; Del removes it, double-click a note to edit it
- Saved as standard PDF annotations, so Acrobat, browsers and phones show them too

**Security & export** (toolbar → ⋯)
- Password protect with AES-256, choose whether printing, copying and editing are allowed; change or remove the password
- Export pages as PNG or JPG at 72/150/300 dpi
- Save a compressed copy (large images are downsampled; text stays sharp)

**Fill & sign** (toolbar → Fill & Sign)
- Fill interactive PDF forms (text fields, checkboxes, radio buttons, dropdowns)
- Type text anywhere, add checkmarks, crosses and today's date (for flat forms that aren't interactive)
- Draw or type a signature. It's placed as vector outlines, is reusable, and is saved only on this computer
- Move, resize and recolor items before they're applied; Ctrl+Z undoes everything

## Build

Requires the .NET 8 SDK.

```powershell
.\build.ps1
```

This produces `dist\PDFPlus.exe`, a single self-contained file you can copy anywhere.

For development: `dotnet run --project src\PDFPlus`.

## Portable settings

Settings (theme, recent files, saved signatures) are stored in `%APPDATA%\PDFPlus`. To keep them next to the exe
instead (e.g. on a USB stick), create an empty file named `PDFPlus.settings.json` beside `PDFPlus.exe`.

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+O / Ctrl+S / Ctrl+Shift+S | Open / Save / Save as |
| Ctrl+W, Ctrl+Tab | Close tab, next tab |
| Ctrl+F, F3, Shift+F3 | Find, next, previous |
| Ctrl+Z / Ctrl+Y | Undo / Redo |
| Ctrl + / Ctrl -, Ctrl+wheel | Zoom |
| Ctrl+0 / Ctrl+1 / Ctrl+2 | Fit page / Actual size / Fit width |
| F4 | Toggle sidebar |
| V / H | Select tool / Hand tool |
| Home / End, Space | First / last page, page down |
| Del (in sidebar) | Delete selected pages |
| Esc | Finish placing text or a signature |

## Development checks

```powershell
python tools\make-sample-pdf.py tests\sample.pdf
PDFPlus.exe --selftest tests\sample.pdf out   # engine checks, see out\selftest.log
PDFPlus.exe --uitest tests\sample.pdf out     # renders UI screenshots off-screen
```

## Licenses

PDFPlus uses [PDFium](https://pdfium.googlesource.com/pdfium/) (BSD-3-Clause), the PDF engine inside Chrome,
via the prebuilt binaries from [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries).
