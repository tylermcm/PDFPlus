# PDFPlus

A fast, portable PDF viewer and editor for Windows. No account, no subscription.

## Features

**Home**
- Opens with a dashboard: drop PDFs to open them, or drop pictures to turn them into a PDF
- "Pick up where you left off": reopen the last file at the page you were reading (every file remembers its page)
- Tool tiles (Edit, Fill & sign, Annotate, Organize, Combine, Images to PDF, Compress, Protect): pick a tool, then a file
- Recent files with first-page previews, page counts and when you opened them; search, pin favorites to the top,
  switch between thumbnails and a list, open with a specific tool, show in folder, or remove from the list
- The house button next to the tabs brings Home back while documents are open

**Viewing**
- Tabs (open many PDFs at once; opening a PDF while PDFPlus is running adds a tab)
- Smooth continuous scrolling with background rendering. Only visible pages are drawn, so huge documents stay fast
- Zoom: Ctrl+wheel at the cursor, presets, fit width, fit page. Very high zoom renders a sharp tile of just the visible area
- Page thumbnails, bookmarks (outline), clickable links (web links ask before opening)
- Find with match case / whole word, highlighted results, next/previous
- Text selection and copy (drag, double-click a word, Ctrl+A for the page)
- Selecting text on scanned pages, and on pages whose fonts carry no character map (where other viewers copy
  gibberish), by reading the page with the OCR engine built into Windows. It happens by itself when a page
  needs it; you can also force it from the right-click menu or read a whole document from the ⋯ menu
- Password-protected PDFs, printing, light and dark themes

**Page tools**
- Reorder pages by dragging thumbnails (multi-select with Ctrl/Shift)
- Rotate, delete, insert blank pages, insert pages from other PDFs (or drop PDFs onto the thumbnails)
- Extract a page range to a new PDF, split a document every N pages, combine several PDFs into one

**Edit** (toolbar → Edit, or press E)
- Click a line of text and retype it in place; Enter saves, Esc cancels, empty text deletes the line
- Keeps the position, size and color. The PDF's own font is reused when it has every character you type;
  otherwise the matching Windows font (e.g. Calibri) is embedded as a small subset, usually a few tens of KB
- Drag text or images to move them, drag an image's corner to resize it (Shift for free aspect), Del removes
- Add images (PNG, JPG, BMP, GIF, TIFF) from the toolbar, the right-click menu, or by dropping them onto a page.
  JPEGs are embedded unchanged and phone photos are rotated upright; PNG transparency is kept
- Limits: edits one line at a time (no paragraph reflow), and text inside graphics or scanned pages can't be edited

**Annotate** (toolbar → Annotate)
- Highlight, underline and strike out text by dragging across it
- Freehand pen, rectangles, ellipses, arrows and sticky notes, in six colors and three line widths
- Click an annotation to select it; Del removes it, double-click a note to edit it
- Saved as standard PDF annotations, so Acrobat, browsers and phones show them too

**Security & export** (toolbar → ⋯)
- Password protect with AES-256, choose whether printing, copying and editing are allowed; change or remove the password
- Export pages as PNG or JPG at 72/150/300 dpi
- Save a compressed copy (large images are downsampled; text stays sharp)
- Read text with OCR across the whole document, for scans and for PDFs whose text copies as gibberish
- Check for updates, and turn the automatic check on or off

**Fill & sign** (toolbar → Fill & Sign)
- Fill interactive PDF forms (text fields, checkboxes, radio buttons, dropdowns)
- Type text anywhere, add checkmarks, crosses and today's date (for flat forms that aren't interactive)
- Draw or type a signature. It's placed as vector outlines, is reusable, and is saved only on this computer
- Move, resize and recolor items before they're applied; Ctrl+Z undoes everything

## Build

Requires the .NET 8 SDK. PDFPlus ships as an MSI; there is no separate portable build.

```powershell
.\build-installer.ps1
```

This produces `dist\PDFPlus-1.1.1-x64.msi` (built with [WiX Toolset 5](https://wixtoolset.org), MS-RL, restored from NuGet).

- Installs for all users into Program Files, with a Start menu shortcut and an Add/Remove Programs entry
- Registers PDFPlus for PDFs under "Open with" and Settings → Default apps (Windows doesn't let installers
  take over the default app, so pick PDFPlus there if you want it to open PDFs on double-click)
- Installing a newer version upgrades in place; uninstalling keeps your settings in `%APPDATA%\PDFPlus`
- Silent install: `msiexec /i PDFPlus-1.1.1-x64.msi /qn`, add `DESKTOP_SHORTCUT=1` for a desktop shortcut

PDFPlus installs as ordinary files rather than one packed single-file exe, which is the difference between
opening in about eight seconds and opening in about 0.6. Windows scans a large opaque binary every time it runs;
as separate files the runtime is mapped straight from disk. The MSI compresses them, so the download is the same
size either way. See the note in `PDFPlus.csproj` before reaching for `PublishSingleFile`.

For development: `dotnet run --project src\PDFPlus`.

## Updates

PDFPlus checks the [releases page](https://github.com/tylermcm/PDFPlus/releases) for a newer version when it
starts, at most once a day, and offers to download and install it. You can skip a version, or turn the check
off entirely under ⋯ → "Check for updates automatically"; ⋯ → "Check for updates" looks straight away.

The check is a plain request for the public list of releases. Nothing about you, your settings or your files
is sent, and nothing else in PDFPlus uses the network.

For the check to work, a release needs a `.msi` (or `.exe`) attached whose filename contains the version,
like `PDFPlus-1.2.3-x64.msi` — which is what `build-installer.ps1` produces. The release tag itself doesn't
have to be a version number.

## Reading text from pictures

Some PDFs can't be selected or searched: scans hold pictures of words rather than words, and some generators
embed fonts without the table that maps them back to letters, so the page looks perfect but copying it
produces punctuation soup. PDFPlus notices both and reads those pages with the OCR engine built into Windows,
then uses what it read for selection, copy and find. This runs on your computer; nothing is uploaded.

It needs an OCR language pack, which Windows installs with most display languages. If yours is missing, add it
under Settings → Time & language → Language & region → your language → Language options.

## Settings

Settings (theme, recent files, saved signatures) are stored in `%APPDATA%\PDFPlus`, and uninstalling leaves them
alone. If an empty `PDFPlus.settings.json` sits beside `PDFPlus.exe`, they are kept there instead; that needs a
writable folder, so it does nothing for the usual Program Files install.

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
| E | Edit text and images (Enter retypes the selected text, Del deletes it) |
| Home / End, Space | First / last page, page down |
| Del (in sidebar) | Delete selected pages |
| Esc | Finish placing text or a signature |

## Development checks

```powershell
python tools\make-sample-pdf.py tests\sample.pdf
# tests\edit-sample.pdf: print tools\make-edit-sample.html with Edge (command inside the file)
python tools\make-unmapped-text-pdf.py tests\unmapped-text.pdf   # a PDF whose text copies as gibberish
# run these from the build output, e.g. artifacts\installer\publish
PDFPlus.exe --selftest tests\sample.pdf out   # engine checks, see out\selftest.log
PDFPlus.exe --uitest tests\sample.pdf out     # renders UI screenshots off-screen
PDFPlus.exe --textcheck file.pdf out          # what the text layer says vs what OCR reads, see out\textcheck.log
PDFPlus.exe --updatecheck                     # what the update check finds, see %TEMP%\PDFPlus\updatecheck.log
```

## Startup timing

Every run writes `%TEMP%\PDFPlus\startup.log`: a breakdown of where the time went, measured from when the
process was created rather than from the first line of managed code, so packaging costs show up instead of
hiding. It is the first place to look when someone says PDFPlus is slow to open.

```
runtime start, before Main                     130      130
...
window shown                                   504      172
total                                          634
```

## Licenses

PDFPlus uses [PDFium](https://pdfium.googlesource.com/pdfium/) (BSD-3-Clause), the PDF engine inside Chrome,
via the prebuilt binaries from [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries),
and [PDFsharp](https://github.com/empira/PDFsharp) (MIT) for encryption and compaction.
