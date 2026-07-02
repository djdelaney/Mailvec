# Reading attachments

There are three tools, for three jobs:

| Tool | Returns | Best for |
| --- | --- | --- |
| `get_attachment` | the **file on disk** (+ inline image/small-text) | getting the actual file; images |
| `get_attachment_text` | the **extracted text** (PDF/DOCX) inline | "what does this document say" — works over a remote connection, no filesystem |
| `get_attachment_page_image` | a **rendered page** as an inline JPEG | layout that text loses (tables, forms, signatures) or scanned/image-only PDFs |

All three take the email (`id` or `messageId`) plus `partIndex` from the `get_email` response.

## `get_attachment` — the file

`get_attachment` extracts a single email attachment to `~/Downloads/mailvec/` (configurable via `Mcp:AttachmentDownloadDir`) and returns the absolute path. It deliberately does **not** try to ship the bytes back through MCP — Claude.ai's MCP bridge currently mishandles non-image binary blobs and rejects them as "unsupported image format". Putting the file on disk delegates the "interpret bytes by file type" job to whichever tool is best at it.

## How Claude actually reads the file

Depends on the client:

- **Claude Code** — the built-in `Read` tool can open the saved path directly and handles PDFs, text, images, etc. natively. Nothing extra to install.
- **Claude.ai web / Claude Desktop** — Claude can't read arbitrary local paths. To make `get_attachment` useful end-to-end, **install a filesystem MCP server** alongside Mailvec. The official one is [`@modelcontextprotocol/server-filesystem`](https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem). Point it at `~/Downloads/mailvec/`, then Claude can call its `read_text_file` / `read_media_file` tools on the path Mailvec just returned. Without a filesystem MCP, `get_attachment` still works — Claude just tells you "I saved it to /Users/.../Downloads/mailvec/foo.pdf" and you open it yourself in Finder.

## Inline content blocks

For convenience, two cases are also inlined as native MCP content blocks regardless of client:

- **Image attachments** are inlined as `ImageContentBlock` so Claude vision can describe / OCR them in one round trip.
- **Small text-ish files** (`text/*`, `application/json`, `application/xml`, etc., under `Mcp:AttachmentInlineTextMaxBytes` — default 256 KB) have their decoded UTF-8 text included as an additional text block.

The file is also always saved to disk in those cases, so a downstream tool can still pick it up.

## `get_attachment_text` — the document's text

`get_attachment_text` returns the text Mailvec already extracted from the attachment at ingest time (PDF via PdfPig, DOCX via OpenXml), straight from the database — **no filesystem touched**. Because it returns a plain text block, it renders on every client and works identically over a remote (HTTP/OAuth) connection, where a returned filesystem path would be meaningless. This is the path for "summarise this contract" / "what's the total on this invoice". When extraction couldn't produce text (scanned/image-only PDF, encrypted, oversize, unsupported), it says so and points you at `get_attachment` or `get_attachment_page_image`.

## `get_attachment_page_image` — a rendered page

`get_attachment_page_image` rasterises **one page** of a PDF to a JPEG and returns it inline as an `ImageContentBlock` (the one binary type that renders reliably on every client, including a remote connector). Use it when layout carries meaning that flattened text loses — tables, forms, charts, signatures — or for **scanned / image-only PDFs** that have no text layer at all. One page per call (`page`, 1-based): read `get_attachment_text` first to find the page you need, then render just that one. The renderer caps the long edge at ~1536px (matching what Claude downsamples to anyway), so payloads stay small. Only PDFs are supported.

## Attachment content indexing

Beyond delivery, attachment **contents** are indexed. At ingest time the indexer extracts text from PDF (PdfPig), DOCX / XLSX / PPTX (OpenXml), iCalendar (`.ics`) and vCard (`.vcf`), and plain text, feeding it into FTS5 and the vector index — so a query that only appears inside an attachment returns the parent email and identifies which attachment drove the hit (`matchedAttachment { partIndex, fileName }`).

Scanned / image-only PDFs (and inline/attached images) have no text layer, so the indexer leaves them at `extraction_status='no_text'` (PDFs) or `'unsupported'` (images). The **embedder** then recovers them out of band with a local Ollama vision model (`Embedder:OcrEnabled` / `ImageOcrEnabled`, both on by default; `Ollama:VisionModel`, default `qwen2.5vl:7b`): it renders each page/image, transcribes it, writes the text back as `status='ocr'`, and re-queues the message so the recovered text becomes keyword- and vector-searchable like any other. See [`docs/contributing/attachment-ocr.md`](contributing/attachment-ocr.md) and the full format/gate list in [`docs/contributing/attachment-indexing.md`](contributing/attachment-indexing.md). `get_attachment_page_image` still renders any PDF page on demand for layout Claude wants to see directly.
