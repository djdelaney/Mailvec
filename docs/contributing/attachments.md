# `view_attachment` — contributor notes

User-facing docs ("how Claude reads the file", inline content blocks, attachment content indexing) live in [`docs/attachments.md`](../attachments.md). This page is the implementation gotcha collection — read it before editing the MCP `view_attachment` tool, `AttachmentExtractor`, or related code.

**Sibling tools.** None of the three attachment tools writes to disk. The two viewer tools decode bytes out of the Maildir *in memory*: `view_attachment` inlines an image (`ImageContentBlock`) or a small text file (`TextContentBlock`), and returns a summary for anything else; `get_attachment_page_image` renders a PDF page to JPEG via `PdfRenderer`/PDFium (see the native-dep note in `ops/UPGRADING.md` and the renderer's sizing/JPEG rationale in `PdfRenderer.cs`). `get_attachment_text` never touches the Maildir at all — it's a pure DB read of `extracted_text`. The shared decode path for the two viewers is `AttachmentExtractor.ExtractInMemory`.

## Why binary types return a summary, not bytes

- **Don't ship arbitrary binary bytes to Claude over MCP.** For a type we can't inline (PDF, DOCX, zip, …), `view_attachment` returns a text summary pointing at `get_attachment_text` / `get_attachment_page_image` rather than the raw bytes. Earlier attempts to send bytes via `EmbeddedResourceBlock` foundered on Claude.ai's bridge mapping every blob to `image` regardless of MIME — which is also why the *only* binary we inline is `image/*`, and why `get_attachment_page_image` returns an image rather than the raw PDF.
- **In-memory only, by design.** `view_attachment` used to persist the file to `~/Downloads/mailvec/` and return the path; that leaked mail content to disk on every read and was meaningless in a container (the path isn't the user's Downloads). The disk-writing `AttachmentExtractor.Extract` now backs only the explicit, user-initiated download paths — the tray's Save button (`/tray/attachment`) and `mailvec extract-attachments`. Don't route the read-only MCP tools back through it.
- **Disk-download path details** (`Extract`, tray/CLI only): writes to `Mcp:AttachmentDownloadDir` (default `~/Downloads/mailvec/`; avoid `~/Library/Caches/` (hidden) and `~/Documents/` (TCC-blocked)), with output filename `{messageId}-{partIndex}-{sanitized-name}` — the id+index prefix guarantees no collisions and keeps the originating email greppable.

## Cross-tool contract

- **`get_email` advertises the `partIndex` Claude needs.** Don't rename `partIndex` without updating all the attachment tools — Claude reads the field name from `get_email`'s output schema and passes it back to `view_attachment`, `get_attachment_text`, and `get_attachment_page_image`.

## Security

- **Filename sanitization + path containment.** `AttachmentExtractor.ResolveSafeFileName` runs `Path.GetFileName` on the claimed filename, which strips any directory component regardless of separator style. `ResolveSafeOutputPath` does a defence-in-depth canonical-path containment check (`Path.GetFullPath` of target inside `Path.GetFullPath` of download dir) and refuses to overwrite an existing symlink at the destination (TOCTOU vector). Don't relax either.

## MIME handling

- **Override `application/octet-stream` from the filename extension.** Many mailers attach PDFs / DOCX / images with `Content-Type: application/octet-stream`. `AttachmentExtractor.ResolveContentType` substitutes a real MIME when the declared type is generic and the extension is known. Specific declared MIMEs are preserved.
- **Images and small text-ish files are inlined as native MCP blocks.** `image/*` → `ImageContentBlock` so Claude vision works in one round trip. `text/*` + a few application MIMEs (json, xml, yaml) under `Mcp:AttachmentInlineTextMaxBytes` → an extra `TextContentBlock` with strict UTF-8 decoding (`UTF8Encoding(throwOnInvalidBytes: true)` so a CSV that claims `text/*` but isn't valid UTF-8 just omits the inline text). Both come straight from the in-memory bytes — nothing is written to disk.
- **Image bytes are only passed through verbatim when native-format AND small.** Claude vision accepts exactly JPEG/PNG/GIF/WebP (`ViewAttachmentTool.ClaudeNativeImageTypes`), and a raw multi-MB photo base64s past client message limits — so anything non-native or over `ImagePassThroughMaxBytes` (1 MB, a const not a config knob: it tracks protocol/client ceilings, not preference) is normalised via `Mailvec.Pdf`'s `ImageRenderer.TryNormalize` (white-flatten, ≤1536px long edge, JPEG q85 — same payload shape as `get_attachment_page_image`, TIFF routed through LibTiff). `TryNormalize` returning null (HEIC, SVG, corrupt) degrades to a summary block, never a doomed `ImageContentBlock`. Don't inline `att.Bytes` directly for images — that reintroduces both failure modes.
- **`get_attachment_text` windows its output.** `maxChars` (default 50,000, hard cap 200,000) + `offset` page through `extracted_text`, which can legitimately be 2,000,000 chars (`MaxExtractedTextChars`) — an unwindowed return can exceed a client's whole context. The slice logic (`SliceWindow`) nudges both ends off UTF-16 surrogate-pair halves so a window never serialises U+FFFD; `view_attachment`'s inline-text display shares the same slicer and 50k cap (the 256 KB decode cap is ~5× that in chars — decode-eligibility and display size are different budgets). `get_email` advertises per-attachment `extractedTextChars` so the client can plan paging up front.
- **`Blob` / `Data` setters take `ReadOnlyMemory<byte>` of the *base64 string's UTF-8 encoding*** (matters for `ImageContentBlock.Data`). Use `Encoding.UTF8.GetBytes(Convert.ToBase64String(rawBytes))`. Don't pass raw bytes (the SDK won't base64-encode for you) and don't pass a `string` (won't compile).

## Idempotency

- **Re-extraction is idempotent** (disk-download path only). In `AttachmentExtractor.Extract`, if the target file already exists with matching size, we skip the rewrite and set `WasReused: true`. Hashing would be more rigorous but Maildir parsing is the dominant cost; size is a good-enough fingerprint. `ExtractInMemory` has no such concept — it re-decodes each call and never touches disk.
