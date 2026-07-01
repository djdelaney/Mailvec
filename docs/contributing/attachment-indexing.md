# Attachment indexing — what we extract, and what we skip (and why)

This is the rationale reference for attachment text recovery: which formats we
read, and every size/dimension/filetype gate that makes us *skip* a file. The
goal is that when someone later asks "why isn't this PDF/image/spreadsheet
searchable?", the answer (and whether it's intentional) is written down.

For the vision-model OCR design specifically, see
[`attachment-ocr.md`](attachment-ocr.md). For `get_attachment` (serving raw
bytes) see [`attachments.md`](attachments.md).

## The pipeline in one picture

Two independent processes touch attachment content, and they produce a single
stable `attachments.extraction_status` enum:

- **Indexer** — extracts text *at index time* via
  `AttachmentTextExtractor` (pure-managed, no shell-out). Feeds
  `messages.attachment_text` (FTS) and per-attachment vector chunks.
- **Embedder** — OCRs the things the indexer can't read (scanned PDFs, image
  attachments) *out of band* with a local vision model, re-stamps them `ocr`,
  and re-queues the message.

`extraction_status`: `done` · `ocr` · `no_text` · `unsupported` · `oversize` ·
`encrypted` · `failed`. All persisted verbatim and surfaced to clients (e.g.
`get_email` tells the user "I couldn't read this PDF because it was encrypted").

## Formats we extract (indexer)

Everything here is **pure-managed — no native deps, no shell-out, no OCR** in the
indexer path. (The one native dep in the wider system, PDFium via `Mailvec.Pdf`,
is used only for OCR rasterisation in the embedder/MCP, never the indexer.)

| Format | Library | What we pull |
|--------|---------|--------------|
| PDF | PdfPig (Apache 2.0) | text in content-stream order; scanned/image-only PDFs come back `no_text` and are handed to the embedder's OCR pass |
| DOCX / XLSX / PPTX | DocumentFormat.OpenXml (MIT) | DOCX paragraphs; XLSX sheet names + shared-string table; PPTX slide text runs |
| iCalendar (.ics) | hand-rolled | unfolded SUMMARY / LOCATION / DTSTART / ORGANIZER / ATTENDEE, machine noise dropped |
| vCard (.vcf) | hand-rolled | unfolded name / org / title / email / phone / address / note; QP-decoded; PHOTO blob dropped |
| plain text | — | UTF-8, Windows-1252 fallback |

**Routing** (`AttachmentTextExtractor.ResolveFormat`) trusts the content-type
first, then falls back to the filename extension — senders mislabel constantly
(`.ics` as `text/plain`, a photo as `application/octet-stream`). For the
structured text formats (ics / vcf) the **extension check precedes the generic
`text/` branch**, otherwise a `.ics` mislabeled `text/plain` would be stored as
raw VCALENDAR instead of clean fields.

## The gates — why we skip

### Size cap → `oversize`
`Indexer:AttachmentMaxBytes` (**25 MB**). PdfPig and OpenXML DOM-load the whole
file; a 200 MB image-heavy PDF can exhaust RAM during parse. Checked against the
*declared* MIME size first (cheap pre-filter), then re-checked after MIME decode
to catch under-reported sizes. Nothing bigger is even attempted.

### Output cap
`AttachmentTextExtractor.MaxExtractedTextChars` (**2,000,000**). Bounds a single
pathological document's contribution to the index; extraction stops appending
past it.

### Image OCR byte gate → stays `unsupported`
`Embedder:ImageOcrMinBytes` (**50 KB**). Below this, image attachments are
overwhelmingly logos, icons, email-signature glyphs, bullets, and tracking
pixels — no readable content — and every vision call costs real seconds. On a
real corpus the large majority of image attachments fall here. They stay
`unsupported` (not an error — just "nothing worth OCRing").

### Image OCR dimension / aspect gates → `no_text`
Applied *after* decode (can't be expressed in SQL):
`Embedder:ImageOcrMinDimension` (**200 px** short edge) and
`Embedder:ImageOcrMaxAspectRatio` (**8.0**). A short edge under 200 px is an
icon/avatar; an aspect ratio over 8 is a banner strip or spacer. Neither carries
readable text. Marked terminally `no_text` so the queue drains instead of
re-decoding them every cycle.

### GIF exclusion → stays `unsupported`
The image-OCR candidate gate excludes `image/gif`. Rationale: animated marketing
and reaction GIFs plus banner strips dominate the format, and first-frame OCR of
an animation yields nothing.

This exclusion is **mostly redundant with the byte gate** — on our corpus 230 of
240 GIFs are below 50 KB anyway, and of the ~10 large ones, most are animated
(76-frame marketing loops, an 87×33 animated banner). Verified trade-off: the
exclusion's only real cost is a **handful of static content GIFs** (notably one
946×740 map with directions) that it skips. We accept that miss rather than spend
vision calls decoding first frames of animations. If that ever feels wrong, the
fix is to drop the `<> 'image/gif'` clause from `MessageRepository.ImageOcrMatch`
and let the byte + dimension gates do the filtering.

## What we intentionally don't support (and why)

| Type | Why not |
|------|---------|
| Legacy binary Office (`.doc` / `.xls` / `.ppt`) | needs a different library (e.g. NPOI); OpenXML can't read the old binary formats. Low value for the count. |
| Archives (`.zip` / `.7z`) | would need unpack + recurse + per-entry size guards |
| Video / audio | needs speech-to-text; out of scope |
| Crypto signatures (`pkcs7` / `pgp`) | signatures carry no text |
| Encrypted PDF / DOCX | no key → `encrypted` (surfaced to the user, not retried) |
| HEIC | SkiaSharp ships no HEIC codec; would need native libheif → decodes to null → `failed` |
| `message/rfc822`, TNEF (`winmail.dat`), `.pkpass`, iWork `.pages`, PSD / InDesign | niche / low-yield; bespoke parsing for a handful of files each |

## "Unsupported is mostly a mirage"

`unsupported` is the single largest `extraction_status` bucket (~47% of rows),
which *looks* like a huge indexing gap. It isn't: on our corpus ~99% of those
rows have **no recoverable text** — below-gate images, GIFs, crypto signatures,
encrypted files, video. The genuinely-recoverable frontier at any time is small
and specific (e.g. the xlsx/pptx addition recovered ~60 files). Before treating a
big `unsupported` count as work to do, break it down by content-type + size — the
number is almost always dominated by things that are correctly skipped.

## Source of truth (keep this doc honest)

The numbers above are defaults; the live values live in code:

- `Mailvec.Core/Options/IndexerOptions.cs` → `AttachmentMaxBytes`
- `Mailvec.Core/Options/EmbedderOptions.cs` → `ImageOcrMinBytes`,
  `ImageOcrMinDimension`, `ImageOcrMaxAspectRatio`, `ImageOcrEnabled`
- `Mailvec.Core/Attachments/AttachmentTextExtractor.cs` → `MaxExtractedTextChars`,
  and `ResolveFormat` (the format/routing table)
- `Mailvec.Core/Data/MessageRepository.cs` → `ImageOcrMatch` (the image-OCR
  candidate gate, shared by the candidate query and the `/health` count) and
  `EnumerateImagesNeedingOcr`

One-time backfills for format/routing additions run through
`mailvec extract-attachments --reextract-{calendar,vcard,office}` (see
`ExtractAttachmentsCommand`).
