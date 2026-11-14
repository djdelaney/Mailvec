#!/usr/bin/env python3
"""Probe a running `parse` container: one text extraction, one PDF render.

Called by ops/image-smoke.sh. The point is the native libraries, not parser
logic (the xUnit suites cover that): text extraction proves the managed
parsers load on this image, and a page render proves libpdfium and
libSkiaSharp are present and built for the image's architecture. Those are
exactly what a per-RID packaging slip breaks while every test stays green
(see the dotnet-linux job's comment in .github/workflows/ci.yml).

Usage: image-smoke-parse.py <base-url> <pdf-fixture>
Standard library only. Run with `python3 -I`.
"""

import base64
import json
import sys
import urllib.request


def build_eml(pdf: bytes) -> bytes:
    b64 = base64.encodebytes(pdf).decode("ascii")
    return (
        "From: smoke@example.com\r\n"
        "To: smoke@example.com\r\n"
        "Subject: image smoke test\r\n"
        "Message-ID: <image-smoke@example.com>\r\n"
        "Date: Mon, 02 Nov 2026 09:00:00 +0000\r\n"
        "MIME-Version: 1.0\r\n"
        'Content-Type: multipart/mixed; boundary="smoke-boundary"\r\n'
        "\r\n"
        "--smoke-boundary\r\n"
        "Content-Type: text/plain; charset=utf-8\r\n"
        "\r\n"
        "Smoke test body.\r\n"
        "--smoke-boundary\r\n"
        'Content-Type: application/pdf; name="sample.pdf"\r\n'
        'Content-Disposition: attachment; filename="sample.pdf"\r\n'
        "Content-Transfer-Encoding: base64\r\n"
        "\r\n"
        f"{b64}"
        "--smoke-boundary--\r\n"
    ).encode("ascii")


def post(base: str, path: str, body: bytes) -> dict:
    req = urllib.request.Request(
        base.rstrip("/") + path,
        data=body,
        method="POST",
        headers={"Content-Type": "application/octet-stream"},
    )
    # A local container, never a proxy: the runner's environment may set one.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(req, timeout=120) as resp:
        return json.load(resp)


def main() -> int:
    base, fixture = sys.argv[1], sys.argv[2]
    with open(fixture, "rb") as f:
        eml = build_eml(f.read())

    parsed = post(base, "/v1/message?extractText=true", eml)
    atts = parsed.get("attachments") or []
    if len(atts) != 1:
        print(f"FAIL parse: expected 1 attachment, got {len(atts)}", file=sys.stderr)
        return 1
    att = atts[0]
    text = (att.get("extractedText") or "").strip()
    if att.get("extractionStatus") != "done" or not text:
        print(
            f"FAIL extract: status={att.get('extractionStatus')!r}, {len(text)} chars",
            file=sys.stderr,
        )
        return 1
    print(f"ok   extract: {len(text)} chars of PDF text (PdfPig + MimeKit)")

    part = att["partIndex"]
    render = post(base, f"/v1/parts/{part}/pdf-pages?first=0&max=1", eml)
    pages = render.get("pages") or []
    if render.get("pageCount", 0) < 1 or not pages:
        print(f"FAIL render: {render.get('pageCount')} pages, {len(pages)} returned", file=sys.stderr)
        return 1
    jpeg = base64.b64decode(pages[0])
    if not jpeg.startswith(b"\xff\xd8\xff"):
        print(f"FAIL render: page 1 is not a JPEG ({jpeg[:4]!r})", file=sys.stderr)
        return 1
    print(f"ok   render: page 1 of {render['pageCount']} as a {len(jpeg)}-byte JPEG (PDFium + SkiaSharp)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
