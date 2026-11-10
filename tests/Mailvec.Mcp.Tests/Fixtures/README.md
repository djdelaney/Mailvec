# Test fixtures — PDFs for render validation

These back `GetAttachmentPageImageToolTests` and the parser contract tests. The
generated quadrant/text PDFs in that test cover page-selection, orientation,
dimensions, and non-blank rendering deterministically; these committed files
add whole-document shapes.

**Every fixture here is synthetic.** These files are public. Never commit a real
document, and never "redact" one in a PDF editor: editors save an incremental
update, so the original page stays in the file and can be recovered by cutting
it at the first `%%EOF`. `digital-table-sample.pdf` and `scanned-sample.pdf` are
written by [`make-fixtures.py`](make-fixtures.py); change the script and re-run
it rather than editing the PDFs.

Keep them **small** (a page or two) — they're committed to the repo.

## Files

- **`text-sample.pdf`** — a digital PDF with Helvetica text (standard-14 font,
  no embedding). Validates real glyph rasterisation, not just blank pages.

- **`digital-table-sample.pdf`** — a **digital** invoice: text in two fonts, a
  filled header band with a logo block, and a ruled, column-aligned table. The
  "text → image" path most mail attachments use: PDFium rasterises glyphs and
  vector table rules, distinct from the scanned fixture's "image → image"
  re-decode. Its text *is* extractable (`AttachmentTextExtractor` → `done`), the
  inverse of the scan — so the two fixtures bracket both regimes. It uses the
  standard-14 fonts, so it does not exercise embedded-font rendering.

- **`scanned-sample.pdf`** — a **scanned-style / image-only** PDF: a letter
  rasterised to a JPEG and wrapped as a page with **no text layer**, the way a
  scanner writes one. This is the marquee case for
  `get_attachment_page_image` — the one `get_attachment_text` can't read. The
  test guards on the file's existence, so a replacement needs exactly this name.

  A good replacement: one or two pages, visible dark content (so the not-blank
  assertion is meaningful), and no text layer (so `AttachmentTextExtractor`
  returns `no_text`).
