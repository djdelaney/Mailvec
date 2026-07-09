# Reading attachments

There are three tools, for three jobs:

| Tool | Returns | Best for |
| --- | --- | --- |
| `get_attachment` | an **image** or **small text file** inline (else a summary) | viewing a photo/image; a quick CSV/JSON/text |
| `get_attachment_text` | the **extracted text** (PDF/DOCX) inline | "what does this document say" — works over a remote connection, no filesystem |
| `get_attachment_page_image` | a **rendered page** as an inline JPEG | layout that text loses (tables, forms, signatures) or scanned/image-only PDFs |

All three take the email (`id` or `messageId`) plus `partIndex` from the `get_email` response.

## `get_attachment` — inline image / text

`get_attachment` decodes a single attachment **in memory** and returns its content inline — nothing is written to disk:

- **Image attachments** come back as an `ImageContentBlock` so Claude vision can describe / OCR them in one round trip.
- **Small text-ish files** (`text/*`, `application/json`, `application/xml`, etc., under `Mcp:AttachmentInlineTextMaxBytes` — default 256 KB) have their decoded UTF-8 text included as a text block.
- **Any other binary type** (PDF, DOCX, zip, …) returns just a short summary pointing at the right tool: `get_attachment_text` for the document's extracted text, or `get_attachment_page_image` to view a PDF page.

It deliberately does **not** ship arbitrary binary back through MCP — Claude.ai's bridge maps every non-image blob to an image block and rejects it as "unsupported image format" (which is why only `image/*` is inlined). It also no longer persists the file to `~/Downloads/mailvec/`: writing mail content to disk on every read was a needless privacy footprint (see the pre-go-live data-leak review) and is meaningless in a containerised deployment, where that path isn't the user's Downloads folder.

**Need the actual file on disk?** Use the tray's Save button (which calls `/tray/attachment`) or `mailvec extract-attachments` — the explicit, user-initiated download paths. `Mcp:AttachmentDownloadDir` (default `~/Downloads/mailvec/`) configures where those write.

## `get_attachment_text` — the document's text

`get_attachment_text` returns the text Mailvec already extracted from the attachment at ingest time (PDF via PdfPig, DOCX via OpenXml), straight from the database — **no filesystem touched**. Because it returns a plain text block, it renders on every client and works identically over a remote (HTTP/OAuth) connection, where a returned filesystem path would be meaningless. This is the path for "summarise this contract" / "what's the total on this invoice". When extraction couldn't produce text (scanned/image-only PDF, encrypted, oversize, unsupported), it says so and points you at `get_attachment_page_image`.

## `get_attachment_page_image` — a rendered page

`get_attachment_page_image` rasterises **one page** of a PDF to a JPEG and returns it inline as an `ImageContentBlock` (the one binary type that renders reliably on every client, including a remote connector). Use it when layout carries meaning that flattened text loses — tables, forms, charts, signatures — or for **scanned / image-only PDFs** that have no text layer at all. One page per call (`page`, 1-based): read `get_attachment_text` first to find the page you need, then render just that one. The renderer caps the long edge at ~1536px (matching what Claude downsamples to anyway), so payloads stay small. Only PDFs are supported.

## Attachment content indexing

Beyond delivery, attachment **contents** are indexed. At ingest time the indexer extracts text from PDF (PdfPig), DOCX / XLSX / PPTX (OpenXml), iCalendar (`.ics`) and vCard (`.vcf`), and plain text, feeding it into FTS5 and the vector index — so a query that only appears inside an attachment returns the parent email and identifies which attachment drove the hit (`matchedAttachment { partIndex, fileName }`).

Scanned / image-only PDFs (and inline/attached images) have no text layer, so the indexer leaves them at `extraction_status='no_text'` (PDFs) or `'unsupported'` (images). The **embedder** then recovers them out of band with a local Ollama vision model (`Embedder:OcrEnabled` / `ImageOcrEnabled`, both on by default; `Ollama:VisionModel`, default `qwen2.5vl:7b`): it renders each page/image, transcribes it, writes the text back as `status='ocr'`, and re-queues the message so the recovered text becomes keyword- and vector-searchable like any other. See [`docs/contributing/attachment-ocr.md`](contributing/attachment-ocr.md) and the full format/gate list in [`docs/contributing/attachment-indexing.md`](contributing/attachment-indexing.md). `get_attachment_page_image` still renders any PDF page on demand for layout Claude wants to see directly.
