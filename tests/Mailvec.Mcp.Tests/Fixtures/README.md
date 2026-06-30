# Test fixtures — real PDFs for render validation

These back `GetAttachmentPageImageToolTests`. The generated quadrant/text PDFs
in that test cover page-selection, orientation, dimensions, and non-blank
rendering deterministically; these committed files add real-world fidelity.

Keep them **small** (a page or two) — they're committed to the repo.

## Files

- **`text-sample.pdf`** — a real digital PDF with Helvetica text (standard-14
  font, no embedding). Validates real glyph rasterisation, not just blank pages.
  Checked in.

- **`digital-table-sample.pdf`** — a real **digital** invoice (embedded fonts, a
  logo, a column-aligned table). The "text → image" path most mail attachments
  use: PDFium rasterises real fonts + vector table rules, distinct from the
  scanned fixture's "image → image" re-decode. Its text *is* extractable
  (`AttachmentTextExtractor` → `done`), the inverse of the scan — so the two
  fixtures bracket both regimes. Checked in.

- **`scanned-sample.pdf`** — *(provide this)* a real **scanned / image-only**
  PDF: a page that is a raster image with **no embedded text layer**. This is the
  marquee case for `get_attachment_page_image` — the one `get_attachment_text`
  can't read. A redacted receipt, statement, or letter is ideal. Drop it here
  with exactly this name and the skipped test activates automatically.

  A good scanned fixture: one or two pages, has visible dark content (so the
  not-blank assertion is meaningful), and genuinely lacks a text layer (so
  `AttachmentTextExtractor` returns `no_text`). Avoid anything sensitive — these
  are public in the repo.
