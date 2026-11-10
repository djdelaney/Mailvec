#!/usr/bin/env python3
"""Regenerate the two synthetic PDF fixtures in this directory.

    python3 make-fixtures.py

Everything on both pages is invented (the demo mailbox's contractor, see
assets/screenshots/demo-mail.py). Nothing here may come from a real document:
these files are public.

digital-table-sample.pdf  a digital invoice: text, a filled header band and a
                          ruled table, US Letter. Standard library only.
scanned-sample.pdf        a letter rasterised to a JPEG and wrapped as an
                          image-only page, the way a scanner writes one. Needs
                          macOS `sips` for the rasterisation.

Each file is written as a single revision. Never edit one in a PDF editor and
save: editors append an incremental update and the previous content stays in
the file.
"""
import os
import struct
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))


def esc(s):
    return s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")


def build(objects, info):
    """objects: bytes bodies for 1..n. info: object number of /Info."""
    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for i, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    for off in offsets:
        out += b"%010d 00000 n \n" % off
    out += b"trailer\n<< /Size %d /Root 1 0 R /Info %d 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (
        len(objects) + 1, info, xref)
    return bytes(out)


def stream(data, extra=b""):
    return b"<< /Length %d %s>>\nstream\n" % (len(data), extra) + data + b"\nendstream"


def text(x, y, size, s, font="F1"):
    return "BT /%s %g Tf %g %g Td (%s) Tj ET\n" % (font, size, x, y, esc(s))


FONTS = (b"<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> "
         b"/F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >> >> >>")


def page_pdf(content, width, height, title):
    return build([
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] /Contents 4 0 R /Resources %s >>"
        % (width, height, FONTS),
        stream(content.encode("latin-1")),
        b"<< /Title (%s) /Producer (make-fixtures.py) >>" % esc(title).encode(),
    ], info=5)


def table_invoice():
    c = "0.13 0.23 0.36 rg 0 712 612 80 re f\n"            # header band
    c += "1 1 1 rg 54 732 36 36 re f 0.13 0.23 0.36 rg 62 740 20 20 re f\n"  # logo
    c += "1 1 1 rg\n" + text(104, 752, 20, "Bayside Remodeling", "F2")
    c += text(104, 734, 10, "14 Harbor Road, Port Example  -  billing@bayside.example")
    c += "0 0 0 rg\n" + text(54, 672, 16, "Invoice 2231", "F2")
    c += text(54, 652, 10, "Issued 12 March 2026   Due 11 April 2026")
    c += text(380, 672, 10, "Bill to", "F2")
    c += text(380, 658, 10, "Ada Example")
    c += text(380, 644, 10, "9 Sample Street, Port Example")
    rows = [
        ("Description", "Qty", "Unit price", "Amount"),
        ("Bathroom demolition and haul-away", "1", "850.00", "850.00"),
        ("Porcelain floor tile, supplied and laid", "42", "18.50", "777.00"),
        ("Vanity unit with basin and tap", "1", "1,240.00", "1,240.00"),
        ("Walk-in shower enclosure", "1", "2,150.00", "2,150.00"),
        ("Plumbing rough-in and connection", "6", "95.00", "570.00"),
        ("Electrical: fan, lighting, heated rail", "4", "110.00", "440.00"),
        ("Paint and finishing", "1", "380.00", "380.00"),
    ]
    top, rh = 610, 24
    xs = [54, 330, 390, 474, 558]
    c += "0.9 0.92 0.95 rg 54 %d 504 %d re f 0 0 0 rg 0.5 w\n" % (top - rh, rh)
    for i in range(len(rows) + 1):
        c += "54 %d m 558 %d l S\n" % (top - i * rh, top - i * rh)
    for x in xs:
        c += "%d %d m %d %d l S\n" % (x, top, x, top - len(rows) * rh)
    for i, row in enumerate(rows):
        y = top - i * rh - 16
        font = "F2" if i == 0 else "F1"
        for x, cell in zip(xs, row):
            c += text(x + 6, y, 10, cell, font)
    y = top - len(rows) * rh - 26
    for label, amount, font in (("Subtotal", "6,407.00", "F1"), ("Sales tax 6%", "384.42", "F1"),
                                ("Total due", "6,791.42", "F2")):
        c += text(396, y, 11, label, font) + text(480, y, 11, amount, font)
        y -= 18
    c += text(54, 96, 9, "Synthetic test fixture. Every name, address and amount on this page is invented.")
    return page_pdf(c, 612, 792, "Bayside Remodeling invoice 2231 (synthetic)")


def letter_source(scale):
    """The letter the 'scan' is made from, drawn at `scale` so sips rasterises it sharply."""
    lines = [
        ("F2", 20, "Keel & Plumb Surveyors"),
        ("F1", 10, "3 Quay Lane, Port Example"),
        ("F1", 10, ""),
        ("F1", 11, "12 March 2026"),
        ("F1", 11, ""),
        ("F1", 11, "Dear Ada Example,"),
        ("F1", 11, ""),
        ("F1", 11, "Thank you for asking us to inspect the bathroom floor at 9 Sample Street"),
        ("F1", 11, "ahead of the remodel. We visited on 10 March and found the following."),
        ("F1", 11, ""),
        ("F1", 11, "1. The joists under the bath are sound, with no sign of rot or past leaks."),
        ("F1", 11, "2. The subfloor by the window is soft over about two square metres and"),
        ("F1", 11, "   should be replaced before any tile is laid."),
        ("F1", 11, "3. The waste pipe has adequate fall and needs no alteration."),
        ("F1", 11, ""),
        ("F1", 11, "Our fee for this inspection is 180.00, reference KP-0457."),
        ("F1", 11, ""),
        ("F1", 11, "Yours sincerely,"),
        ("F1", 11, ""),
        ("F2", 11, "Marco Bell"),
        ("F1", 9, ""),
        ("F1", 9, "Synthetic test fixture. Every name, address and amount on this page is invented."),
    ]
    c = "%g 0 0 %g 0 0 cm\n" % (scale, scale)
    c += "0.97 0.96 0.93 rg 0 0 612 792 re f\n"              # paper tint
    c += "0.2 0.2 0.2 rg 72 702 468 2 re f 0 0 0 rg\n"
    y = 716
    for font, size, s in lines:
        if s:
            c += text(72, y, size, s, font)
        y -= size + 9 if y < 700 else 34
    c += "0.75 0.75 0.75 rg 0 0 5 792 re f\n"                # scanner edge shadow
    return page_pdf(c, int(612 * scale), int(792 * scale), "letter source")


def jpeg_size(data):
    i = 2
    while i < len(data):
        marker, length = data[i + 1], struct.unpack(">H", data[i + 2:i + 4])[0]
        if 0xC0 <= marker <= 0xC3:
            h, w = struct.unpack(">HH", data[i + 5:i + 9])
            return w, h
        i += 2 + length
    raise ValueError("no SOF marker")


def scanned_letter():
    scale = 2.2  # ~158 dpi on a Letter page
    with tempfile.TemporaryDirectory() as tmp:
        src, jpg = os.path.join(tmp, "letter.pdf"), os.path.join(tmp, "letter.jpg")
        with open(src, "wb") as f:
            f.write(letter_source(scale))
        subprocess.run(["sips", "-s", "format", "jpeg", "-s", "formatOptions", "70", src, "--out", jpg],
                       check=True, stdout=subprocess.DEVNULL)
        with open(jpg, "rb") as f:
            data = f.read()
    w, h = jpeg_size(data)
    image = stream(data, b"/Type /XObject /Subtype /Image /Width %d /Height %d "
                         b"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode " % (w, h))
    return build([
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
        b"/Resources << /XObject << /Im0 5 0 R >> >> >>",
        stream(b"q 612 0 0 792 0 0 cm /Im0 Do Q"),
        image,
        b"<< /Title (Scanned letter \\(synthetic\\)) /Producer (make-fixtures.py) >>",
    ], info=6)


def main():
    for name, make in (("digital-table-sample.pdf", table_invoice), ("scanned-sample.pdf", scanned_letter)):
        data = make()
        assert data.count(b"%%EOF") == 1, name
        with open(os.path.join(HERE, name), "wb") as f:
            f.write(data)
        print(name, len(data), "bytes")


if __name__ == "__main__":
    sys.exit(main())
