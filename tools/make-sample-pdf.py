"""Writes a hand-built test PDF (no dependencies): a form, links, bookmarks, a rotated page, and filler pages.

usage: python tools/make-sample-pdf.py tests/sample.pdf
"""
import sys

FILLER_PAGES = 40

objects = {}
order = []


def ref(name):
    return f"{order.index(name) + 1} 0 R"


def declare(*names):
    for name in names:
        if name not in order:
            order.append(name)


def stream(content, extra=""):
    data = content.encode("latin-1")
    return f"<< /Length {len(data)} {extra}>>\nstream\n".encode("latin-1") + data + b"\nendstream"


def text_lines(lines, x=72, y=700, size=12, leading=16):
    ops = [f"BT /F1 {size} Tf {leading} TL {x} {y} Td"]
    for line in lines:
        ops.append(f"({line}) Tj T*")
    ops.append("ET")
    return "\n".join(ops)


filler_names = [f"filler{i}" for i in range(FILLER_PAGES)]
declare("catalog", "pages", "page1", "content1", "helv", "zadb", "page2", "content2", "page3", "content3",
        "name_field", "agree_field", "agree_on", "agree_off", "uri_link", "goto_link",
        "outlines", "bm1", "bm2", "bm3", "info")
for name in filler_names:
    declare(name, name + "_content")

page_refs = [ref("page1"), ref("page2"), ref("page3")] + [ref(n) for n in filler_names]
fonts = f"<< /F1 {ref('helv')} /Helv {ref('helv')} /ZaDb {ref('zadb')} >>"

objects["catalog"] = (
    f"<< /Type /Catalog /Pages {ref('pages')} /Outlines {ref('outlines')} "
    f"/AcroForm << /Fields [{ref('name_field')} {ref('agree_field')}] /DA (/Helv 0 Tf 0 g) /DR << /Font {fonts} >> >> >>"
)
objects["pages"] = f"<< /Type /Pages /Kids [{' '.join(page_refs)}] /Count {len(page_refs)} >>"
objects["helv"] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
objects["zadb"] = "<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>"

page1_text = "\n".join([
    "BT /F1 28 Tf 72 700 Td (PDFPlus test document) Tj ET",
    "BT /F1 12 Tf 72 656 Td (Name:) Tj ET",
    "BT /F1 12 Tf 72 598 Td (I agree:) Tj ET",
    "0 0 1 rg BT /F1 12 Tf 72 540 Td (Visit example.com) Tj ET",
    "BT /F1 12 Tf 72 500 Td (Jump to the last page) Tj ET 0 g",
    text_lines([
        "The quick brown fox jumps over the lazy dog. Search for the word fox.",
        "Select this text with the mouse, copy it, and paste it somewhere else.",
        "Fill the name field above, tick the box, then add a signature below.",
    ], y=460),
    "0.6 G 72 150 m 300 150 l S BT /F1 10 Tf 72 136 Td (Signature) Tj ET",
])
objects["page1"] = (
    f"<< /Type /Page /Parent {ref('pages')} /MediaBox [0 0 612 792] /Contents {ref('content1')} "
    f"/Resources << /Font {fonts} >> /Annots [{ref('name_field')} {ref('agree_field')} {ref('uri_link')} {ref('goto_link')}] >>"
)
objects["content1"] = stream(page1_text)

objects["name_field"] = (
    f"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [120 650 360 672] /F 4 /P {ref('page1')} "
    "/DA (/Helv 12 Tf 0 g) /MK << /BC [0.6 0.6 0.6] >> /V () >>"
)
objects["agree_on"] = stream("0.3 G 0.5 0.5 15 15 re S 0 g BT /ZaDb 12 Tf 2.5 3.5 Td (4) Tj ET",
                             f"/Type /XObject /Subtype /Form /BBox [0 0 16 16] /Resources << /Font {fonts} >> ")
objects["agree_off"] = stream("0.3 G 0.5 0.5 15 15 re S", "/Type /XObject /Subtype /Form /BBox [0 0 16 16] ")
objects["agree_field"] = (
    f"<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /Rect [140 594 156 610] /F 4 /P {ref('page1')} "
    f"/V /Off /AS /Off /DA (/ZaDb 0 Tf 0 g) /MK << /CA (4) >> /AP << /N << /Yes {ref('agree_on')} /Off {ref('agree_off')} >> >> >>"
)
objects["uri_link"] = "<< /Type /Annot /Subtype /Link /Rect [72 535 200 552] /Border [0 0 0] /A << /S /URI /URI (https://example.com/) >> >>"
objects["goto_link"] = f"<< /Type /Annot /Subtype /Link /Rect [72 495 220 512] /Border [0 0 0] /Dest [{ref('page3')} /XYZ 0 792 0] >>"

objects["page2"] = (
    f"<< /Type /Page /Parent {ref('pages')} /MediaBox [0 0 612 792] /Rotate 90 /Contents {ref('content2')} "
    f"/Resources << /Font {fonts} >> >>"
)
objects["content2"] = stream(text_lines(["This page has /Rotate 90.", "It should display in landscape."], size=18, leading=26))

objects["page3"] = f"<< /Type /Page /Parent {ref('pages')} /MediaBox [0 0 612 792] /Contents {ref('content3')} /Resources << /Font {fonts} >> >>"
objects["content3"] = stream(text_lines(["The last special page.", "Filler pages follow for scrolling tests."], size=20, leading=28))

for i, name in enumerate(filler_names):
    objects[name] = f"<< /Type /Page /Parent {ref('pages')} /MediaBox [0 0 612 792] /Contents {ref(name + '_content')} /Resources << /Font {fonts} >> >>"
    lines = [f"Filler page {i + 4}"] + [f"Line {n}: lorem ipsum dolor sit amet, consectetur adipiscing elit, fox {i}-{n}." for n in range(1, 36)]
    objects[name + "_content"] = stream(text_lines(lines, y=740, size=11, leading=19))

objects["outlines"] = f"<< /Type /Outlines /First {ref('bm1')} /Last {ref('bm3')} /Count 3 >>"
objects["bm1"] = f"<< /Title (Introduction) /Parent {ref('outlines')} /Next {ref('bm2')} /Dest [{ref('page1')} /Fit] >>"
objects["bm2"] = f"<< /Title (Rotated page) /Parent {ref('outlines')} /Prev {ref('bm1')} /Next {ref('bm3')} /Dest [{ref('page2')} /Fit] >>"
objects["bm3"] = f"<< /Title (The end) /Parent {ref('outlines')} /Prev {ref('bm2')} /Dest [{ref('page3')} /Fit] >>"
objects["info"] = "<< /Title (PDFPlus sample) /Producer (make-sample-pdf.py) >>"

out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
offsets = []
for number, name in enumerate(order, 1):
    body = objects[name]
    if isinstance(body, str):
        body = body.encode("latin-1")
    offsets.append(len(out))
    out += f"{number} 0 obj\n".encode() + body + b"\nendobj\n"

xref = len(out)
out += f"xref\n0 {len(order) + 1}\n0000000000 65535 f \n".encode()
for offset in offsets:
    out += f"{offset:010d} 00000 n \n".encode()
out += f"trailer\n<< /Size {len(order) + 1} /Root {ref('catalog')} /Info {ref('info')} >>\nstartxref\n{xref}\n%%EOF\n".encode()

with open(sys.argv[1], "wb") as f:
    f.write(out)
print(f"wrote {sys.argv[1]} ({len(page_refs)} pages, {len(out)} bytes)")
