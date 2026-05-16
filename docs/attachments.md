# Reading attachments

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

## Attachment content indexing

Beyond delivery, attachment **contents** are indexed at ingest time (Phase 4.5). PDF / DOCX / plain-text bodies are extracted into FTS5 and embedded into the vector index, so a query that only appears inside a PDF body returns the parent email and identifies which attachment drove the hit (`matchedAttachment { partIndex, fileName }`). OCR for image-only PDFs is out of scope — use `get_attachment` to drop the file on disk and let Claude vision handle it.
