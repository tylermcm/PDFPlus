"""Writes tests/unmapped-text.pdf: a table drawn with an embedded Identity-H font that has no ToUnicode CMap.

This reproduces a very common real-world PDF defect. The page looks perfect on screen, but every character
code in the content stream is a raw glyph id, and without a ToUnicode CMap nothing can map those ids back to
letters. Selecting and copying yields punctuation soup, and the per-character boxes PDFium reports are wrong,
so the selection highlight smears across the page instead of following the words.

usage: python tools/make-unmapped-text-pdf.py tests/unmapped-text.pdf
"""
import struct
import sys

FONT = r"C:\Windows\Fonts\arial.ttf"


def read_tables(data):
    num_tables = struct.unpack(">H", data[4:6])[0]
    tables = {}
    for i in range(num_tables):
        tag, _checksum, offset, length = struct.unpack(">4sIII", data[12 + i * 16:28 + i * 16])
        tables[tag.decode("latin-1")] = (offset, length)
    return tables


def unicode_to_gid(data, tables):
    """Parses the (3,1) format 4 cmap subtable into a {codepoint: glyph id} map."""
    base = tables["cmap"][0]
    count = struct.unpack(">H", data[base + 2:base + 4])[0]
    subtable = None
    for i in range(count):
        platform, encoding, offset = struct.unpack(">HHI", data[base + 4 + i * 8:base + 12 + i * 8])
        if (platform, encoding) == (3, 1):
            subtable = base + offset
    if subtable is None:
        raise SystemExit("no (3,1) cmap subtable in " + FONT)

    seg_x2 = struct.unpack(">H", data[subtable + 6:subtable + 8])[0]
    segs = seg_x2 // 2
    ends = struct.unpack(f">{segs}H", data[subtable + 14:subtable + 14 + seg_x2])
    starts_at = subtable + 16 + seg_x2
    starts = struct.unpack(f">{segs}H", data[starts_at:starts_at + seg_x2])
    deltas = struct.unpack(f">{segs}h", data[starts_at + seg_x2:starts_at + seg_x2 * 2])
    range_at = starts_at + seg_x2 * 2
    range_offsets = struct.unpack(f">{segs}H", data[range_at:range_at + seg_x2])

    mapping = {}
    for i in range(segs):
        for code in range(starts[i], min(ends[i], 0xFFFE) + 1):
            if range_offsets[i] == 0:
                gid = (code + deltas[i]) & 0xFFFF
            else:
                at = range_at + i * 2 + range_offsets[i] + (code - starts[i]) * 2
                gid = struct.unpack(">H", data[at:at + 2])[0]
                if gid:
                    gid = (gid + deltas[i]) & 0xFFFF
            if gid:
                mapping[code] = gid
    return mapping


def advance_widths(data, tables):
    units = struct.unpack(">H", data[tables["head"][0] + 18:tables["head"][0] + 20])[0]
    metrics = struct.unpack(">H", data[tables["hhea"][0] + 34:tables["hhea"][0] + 36])[0]
    hmtx = tables["hmtx"][0]
    widths = []
    for i in range(metrics):
        widths.append(struct.unpack(">H", data[hmtx + i * 4:hmtx + i * 4 + 2])[0] * 1000 // units)
    return widths


font = open(FONT, "rb").read()
tables = read_tables(font)
gids = unicode_to_gid(font, tables)
widths = advance_widths(font, tables)


def glyphs(text):
    return [gids.get(ord(ch), 0) for ch in text]


def show(text):
    """The text as a hex string of glyph ids: what Identity-H writes into the content stream."""
    return "<" + "".join(f"{g:04X}" for g in glyphs(text)) + ">"


def width_of(text, size):
    total = sum(widths[g] if g < len(widths) else 500 for g in glyphs(text))
    return total * size / 1000.0


HEADERS = ["Procedure Category", "Procedure Category Description", "Coverage(IN)", "Deductible Applies"]
ROWS = [
    ("01", "DIAGNOSTIC", "100%", "N"),
    ("02", "PREVENTIVE", "100%", "N"),
    ("04", "MINOR RESTORATIVE", "80%", "Y"),
    ("05", "ENDODONTICS", "50%", "Y"),
    ("06", "PERIODONTICS", "50%", "Y"),
    ("07", "ORAL SURGERY", "50%", "Y"),
    ("08", "MAJOR RESTORATIVE", "50%", "Y"),
    ("09", "PROSTHODONTICS", "50%", "Y"),
]
COLUMNS = [56, 196, 386, 476]
RIGHT = 556
TOP = 720
ROW_HEIGHT = 30

ops = []
ops.append("0.85 0.85 0.85 RG 0.75 w")
y = TOP
for i in range(len(ROWS) + 2):
    line = TOP - i * ROW_HEIGHT
    ops.append(f"{COLUMNS[0] - 8} {line} m {RIGHT} {line} l S")
for x in [COLUMNS[0] - 8] + [c - 10 for c in COLUMNS[1:]] + [RIGHT]:
    ops.append(f"{x} {TOP} m {x} {TOP - (len(ROWS) + 1) * ROW_HEIGHT} l S")

ops.append("BT 0 0 0 rg /F1 11 Tf")
for text, x in zip(HEADERS, COLUMNS):
    ops.append(f"1 0 0 1 {x} {TOP - 20} Tm {show(text)} Tj")
ops.append("ET")

ops.append("BT 0.1 0.1 0.1 rg /F1 10 Tf")
for row_index, row in enumerate(ROWS):
    line = TOP - (row_index + 2) * ROW_HEIGHT + 10
    for text, x in zip(row, COLUMNS):
        ops.append(f"1 0 0 1 {x} {line} Tm {show(text)} Tj")
ops.append("ET")

ops.append("BT 0 0 0 rg /F1 14 Tf 1 0 0 1 56 764 Tm " + show("Plan Benefit Summary") + " Tj ET")
content = "\n".join(ops).encode("latin-1")

used = sorted({g for text in HEADERS + [c for row in ROWS for c in row] + ["Plan Benefit Summary"] for g in glyphs(text)})
w_array = " ".join(f"{g} [{widths[g] if g < len(widths) else 500}]" for g in used)

objects = [
    b"<< /Type /Catalog /Pages 2 0 R >>",
    b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
    b"<< /Length " + str(len(content)).encode() + b" >>\nstream\n" + content + b"\nendstream",
    # No /ToUnicode: this is the defect. Everything else about the font is valid.
    b"<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAA+Arial /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
    ("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AAAAAA+Arial /CIDToGIDMap /Identity "
     "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
     "/FontDescriptor 7 0 R /DW 1000 /W [" + w_array + "] >>").encode("latin-1"),
    (b"<< /Type /FontDescriptor /FontName /AAAAAA+Arial /Flags 4 /FontBBox [-665 -325 2000 1006] "
     b"/ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 8 0 R >>"),
    b"<< /Length " + str(len(font)).encode() + b" /Length1 " + str(len(font)).encode() + b" >>\nstream\n" + font + b"\nendstream",
]

out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
offsets = []
for number, body in enumerate(objects, start=1):
    offsets.append(len(out))
    out += f"{number} 0 obj\n".encode() + body + b"\nendobj\n"

start = len(out)
out += f"xref\n0 {len(objects) + 1}\n".encode()
out += b"0000000000 65535 f \n"
for offset in offsets:
    out += f"{offset:010d} 00000 n \n".encode()
out += f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\nstartxref\n{start}\n%%EOF\n".encode()

path = sys.argv[1] if len(sys.argv) > 1 else "tests/unmapped-text.pdf"
open(path, "wb").write(out)
print(f"{path}  {len(out) / 1024:.0f} KB")
