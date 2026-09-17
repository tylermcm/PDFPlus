"""Writes tests/sample.docx: a Word document exercising what PDFPlus's .docx reader understands.

Hand-built (no dependencies) in the shape Word itself writes: styles.xml with heading styles, numbering.xml with
a bullet list and a numbered list, an inline PNG, a hyperlink relationship, a table, and a footnote so the
"things this document uses that PDFPlus won't keep" warning has something to find.

usage: python tools/make-docx-sample.py tests/sample.docx
"""
import struct
import sys
import zipfile
import zlib

W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'
R = 'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"'
WP = 'xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"'
A = 'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"'
PIC = 'xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"'
REL = 'xmlns="http://schemas.openxmlformats.org/package/2006/relationships"'
OFFICE = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"


def png(width, height, rgb):
    """A solid-colour PNG, written by hand so this script needs nothing installed."""
    raw = b"".join(b"\x00" + bytes(rgb) * width for _ in range(height))

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw))
            + chunk(b"IEND", b""))


def run(text, bold=False, italic=False, underline=False, colour=None, size=None, font=None):
    props = ""
    if font:
        props += '<w:rFonts w:ascii="%s" w:hAnsi="%s"/>' % (font, font)
    if bold:
        props += "<w:b/>"
    if italic:
        props += "<w:i/>"
    if underline:
        props += '<w:u w:val="single"/>'
    if colour:
        props += '<w:color w:val="%s"/>' % colour
    if size:
        props += '<w:sz w:val="%d"/>' % (size * 2)
    if props:
        props = "<w:rPr>%s</w:rPr>" % props
    return '<w:r>%s<w:t xml:space="preserve">%s</w:t></w:r>' % (props, text)


def para(runs, style=None, align=None, num=None, level=0, before=None, after=None, indent=None):
    props = ""
    if style:
        props += '<w:pStyle w:val="%s"/>' % style
    if num is not None:
        props += '<w:numPr><w:ilvl w:val="%d"/><w:numId w:val="%d"/></w:numPr>' % (level, num)
    if before is not None or after is not None:
        props += '<w:spacing w:before="%d" w:after="%d"/>' % (before or 0, after or 0)
    if indent is not None:
        props += '<w:ind w:left="%d"/>' % indent
    if align:
        props += '<w:jc w:val="%s"/>' % align
    if props:
        props = "<w:pPr>%s</w:pPr>" % props
    return "<w:p>%s%s</w:p>" % (props, "".join(runs))


def cell(text, bold=False, fill=None, span=None):
    props = '<w:tcW w:w="2880" w:type="dxa"/>'
    if span:
        props += '<w:gridSpan w:val="%d"/>' % span
    if fill:
        props += '<w:shd w:val="clear" w:fill="%s"/>' % fill
    return "<w:tc><w:tcPr>%s</w:tcPr>%s</w:tc>" % (props, para([run(text, bold=bold)]))


image = png(160, 90, (224, 52, 75))

drawing = (
    '<w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0" ' + WP + ">"
    '<wp:extent cx="1524000" cy="857250"/><wp:docPr id="1" name="Picture 1"/>'
    "<a:graphic " + A + '><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">'
    "<pic:pic " + PIC + '><pic:nvPicPr><pic:cNvPr id="0" name="swatch.png"/><pic:cNvPicPr/></pic:nvPicPr>'
    '<pic:blipFill><a:blip r:embed="rId5"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>'
    '<pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="1524000" cy="857250"/></a:xfrm>'
    '<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>'
    "</a:graphicData></a:graphic></wp:inline></w:drawing></w:r>")

table = (
    '<w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/><w:tblBorders>'
    '<w:top w:val="single" w:sz="4" w:color="auto"/><w:left w:val="single" w:sz="4" w:color="auto"/>'
    '<w:bottom w:val="single" w:sz="4" w:color="auto"/><w:right w:val="single" w:sz="4" w:color="auto"/>'
    '<w:insideH w:val="single" w:sz="4" w:color="auto"/><w:insideV w:val="single" w:sz="4" w:color="auto"/>'
    "</w:tblBorders></w:tblPr>"
    '<w:tblGrid><w:gridCol w:w="3600"/><w:gridCol w:w="2400"/><w:gridCol w:w="2400"/></w:tblGrid>'
    "<w:tr>" + cell("Region", True, fill="D9E2F3") + cell("Sales", True, fill="D9E2F3")
    + cell("Change", True, fill="D9E2F3") + "</w:tr>"
    "<w:tr>" + cell("North") + cell("1,240") + cell("+12%") + "</w:tr>"
    "<w:tr>" + cell("South") + cell("980") + cell("-3%") + "</w:tr>"
    "</w:tbl>")

hyperlink = ('<w:hyperlink r:id="rId6"><w:r><w:rPr><w:color w:val="0563C1"/>'
             '<w:u w:val="single"/></w:rPr><w:t>the full report</w:t></w:r></w:hyperlink>')
footnote_ref = '<w:r><w:footnoteReference w:id="2"/></w:r>'

body = "".join([
    para([run("Quarterly Report")], style="Heading1"),
    para([run("Prepared by the Finance Team, March 2026", italic=True, colour="6B6F7A")]),
    para([run("Revenue grew to "), run("$4.2 million", bold=True),
          '<w:r><w:rPr><w:rStyle w:val="Emphasis"/></w:rPr><w:t xml:space="preserve"> (a record)</w:t></w:r>',
          run(" this quarter, driven by strong demand in the "),
          run("northern region", underline=True), run(".")]),
    para([run("Highlights")], style="Heading2"),
    para([run("Record revenue in the northern region")], num=1),
    para([run("Two new distribution partners")], num=1),
    para([run("Costs held flat year on year")], num=1),
    para([run("Nested detail under the last point")], num=1, level=1),
    para([run("Next steps")], style="Heading2"),
    para([run("Confirm the Q3 forecast")], num=2),
    para([run("Renew the distribution contracts")], num=2),
    table,
    para([run("")]),
    para([drawing], align="center"),
    para([run("Centred caption", italic=True, size=9)], align="center"),
    para([run("See "), hyperlink, run(" for details."), footnote_ref]),
    para([run("An indented paragraph with generous spacing, to check w:ind and w:spacing come across.")],
         indent=720, before=240, after=240),
    para([run("Small capitals")], style="Caption"),
    # An explicit page break, so the reader has one to carry across.
    '<w:p><w:pPr><w:pageBreakBefore/></w:pPr>' + run("This paragraph starts a new page.") + "</w:p>",
    para([run("Right aligned closing line", size=11)], align="right"),
])

document = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    "<w:document " + W + " " + R + " " + WP + " " + A + "><w:body>" + body +
    '<w:sectPr><w:pgSz w:w="12240" w:h="15840"/>'
    '<w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440"/></w:sectPr>'
    "</w:body></w:document>")

styles = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    "<w:styles " + W + ">"
    "<w:docDefaults><w:rPrDefault><w:rPr>"
    '<w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/><w:sz w:val="22"/>'
    "</w:rPr></w:rPrDefault></w:docDefaults>"
    '<w:style w:type="paragraph" w:styleId="Normal" w:default="1"><w:name w:val="Normal"/></w:style>'
    '<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/>'
    '<w:pPr><w:outlineLvl w:val="0"/></w:pPr>'
    '<w:rPr><w:rFonts w:ascii="Calibri Light" w:hAnsi="Calibri Light"/>'
    '<w:color w:val="2F5496"/><w:sz w:val="32"/></w:rPr></w:style>'
    '<w:style w:type="character" w:styleId="Emphasis"><w:name w:val="Emphasis"/>'
    '<w:rPr><w:i/><w:color w:val="C00000"/></w:rPr></w:style>'
    '<w:style w:type="paragraph" w:styleId="Caption"><w:basedOn w:val="Heading2"/>'
    '<w:name w:val="caption"/><w:rPr><w:smallCaps/><w:sz w:val="20"/></w:rPr></w:style>'
    '<w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/>'
    '<w:pPr><w:outlineLvl w:val="1"/></w:pPr>'
    '<w:rPr><w:rFonts w:ascii="Calibri Light" w:hAnsi="Calibri Light"/>'
    '<w:color w:val="2F5496"/><w:sz w:val="26"/></w:rPr></w:style>'
    "</w:styles>")

numbering = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    "<w:numbering " + W + ">"
    '<w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/>'
    '<w:lvlText w:val="•"/></w:lvl>'
    '<w:lvl w:ilvl="1"><w:numFmt w:val="bullet"/><w:lvlText w:val="o"/></w:lvl></w:abstractNum>'
    '<w:abstractNum w:abstractNumId="1"><w:lvl w:ilvl="0"><w:numFmt w:val="decimal"/>'
    '<w:lvlText w:val="%1."/></w:lvl></w:abstractNum>'
    '<w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>'
    '<w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>'
    "</w:numbering>")

footnotes = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    "<w:footnotes " + W + '><w:footnote w:id="2"><w:p><w:r>'
    "<w:t>Figures are unaudited.</w:t></w:r></w:p></w:footnote></w:footnotes>")

package_rels = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    "<Relationships " + REL + '><Relationship Id="rId1" Type="' + OFFICE +
    '/officeDocument" Target="word/document.xml"/></Relationships>')

document_rels = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    "<Relationships " + REL + ">"
    '<Relationship Id="rId1" Type="' + OFFICE + '/styles" Target="styles.xml"/>'
    '<Relationship Id="rId2" Type="' + OFFICE + '/numbering" Target="numbering.xml"/>'
    '<Relationship Id="rId3" Type="' + OFFICE + '/footnotes" Target="footnotes.xml"/>'
    '<Relationship Id="rId5" Type="' + OFFICE + '/image" Target="media/swatch.png"/>'
    '<Relationship Id="rId6" Type="' + OFFICE + '/hyperlink" '
    'Target="https://example.com/report" TargetMode="External"/>'
    "</Relationships>")

content_types = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
    '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
    '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
    '<Default Extension="xml" ContentType="application/xml"/>'
    '<Default Extension="png" ContentType="image/png"/>'
    '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument'
    '.wordprocessingml.document.main+xml"/>'
    '<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument'
    '.wordprocessingml.styles+xml"/>'
    '<Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument'
    '.wordprocessingml.numbering+xml"/>'
    '<Override PartName="/word/footnotes.xml" ContentType="application/vnd.openxmlformats-officedocument'
    '.wordprocessingml.footnotes+xml"/>'
    "</Types>")

path = sys.argv[1] if len(sys.argv) > 1 else "tests/sample.docx"
with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
    archive.writestr("[Content_Types].xml", content_types)
    archive.writestr("_rels/.rels", package_rels)
    archive.writestr("word/document.xml", document)
    archive.writestr("word/styles.xml", styles)
    archive.writestr("word/numbering.xml", numbering)
    archive.writestr("word/footnotes.xml", footnotes)
    archive.writestr("word/_rels/document.xml.rels", document_rels)
    archive.writestr("word/media/swatch.png", image)

with open(path, "rb") as handle:
    print("%s  %d bytes" % (path, len(handle.read())))
