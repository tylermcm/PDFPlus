"""Writes an image-heavy test PDF for compression checks: 3 pages, each with a large photo-like RGB image.

usage: python tools/make-image-pdf.py tests/images.pdf
"""
import math
import random
import sys
import zlib

WIDTH, HEIGHT = 1600, 1200
random.seed(7)


def photo_like(seed):
    rows = []
    for y in range(HEIGHT):
        row = bytearray(WIDTH * 3)
        for x in range(WIDTH):
            n = random.randint(-12, 12)
            r = 128 + 90 * math.sin((x + seed * 97) / 131.0) + n
            g = 128 + 90 * math.sin((y + seed * 53) / 97.0) + n
            b = 128 + 90 * math.sin((x + y) / 173.0 + seed) + n
            i = x * 3
            row[i] = max(0, min(255, int(r)))
            row[i + 1] = max(0, min(255, int(g)))
            row[i + 2] = max(0, min(255, int(b)))
        rows.append(bytes(row))
    return zlib.compress(b"".join(rows), 6)


objects = []


def add(body):
    objects.append(body if isinstance(body, bytes) else body.encode("latin-1"))
    return len(objects)


def stream(data, extra=""):
    return f"<< /Length {len(data)} {extra}>>\nstream\n".encode("latin-1") + data + b"\nendstream"


catalog = add("")  # placeholder, filled below
pages = add("")
font = add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
page_ids = []
for n in range(3):
    image = add(stream(photo_like(n), f"/Type /XObject /Subtype /Image /Width {WIDTH} /Height {HEIGHT} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode "))
    content = add(stream(f"BT /F1 20 Tf 72 740 Td (Image page {n + 1}) Tj ET q 288 0 0 216 162 400 cm /Im1 Do Q".encode("latin-1")))
    page_ids.append(add(f"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R "
                        f"/Resources << /Font << /F1 {font} 0 R >> /XObject << /Im1 {image} 0 R >> >> >>"))

objects[catalog - 1] = f"<< /Type /Catalog /Pages {pages} 0 R >>".encode("latin-1")
objects[pages - 1] = f"<< /Type /Pages /Kids [{' '.join(f'{p} 0 R' for p in page_ids)}] /Count {len(page_ids)} >>".encode("latin-1")

out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
offsets = []
for number, body in enumerate(objects, 1):
    offsets.append(len(out))
    out += f"{number} 0 obj\n".encode() + body + b"\nendobj\n"
xref = len(out)
out += f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n".encode()
for offset in offsets:
    out += f"{offset:010d} 00000 n \n".encode()
out += f"trailer\n<< /Size {len(objects) + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()

with open(sys.argv[1], "wb") as f:
    f.write(out)
print(f"wrote {sys.argv[1]} ({len(out) / 1e6:.1f} MB)")
