# `get_attachment` — contributor notes

User-facing docs ("how Claude reads the file", inline content blocks, attachment content indexing) live in [`docs/attachments.md`](../attachments.md). This page is the implementation gotcha collection — read it before editing the MCP `get_attachment` tool, `AttachmentExtractor`, or related code.

## Why we ship paths, not bytes

- **Don't ship binary bytes to Claude over MCP.** Current design writes the file to a user-visible directory and returns a text response with the path. Claude Code's built-in `Read` handles PDFs/text/images natively; on Claude.ai or Claude Desktop, a filesystem MCP server (e.g. `@modelcontextprotocol/server-filesystem`) pointed at the download dir picks up the read. (Earlier attempts to send bytes via `EmbeddedResourceBlock` foundered on Claude.ai's bridge mapping every blob to `image` regardless of MIME.)
- **Where to write.** `Mcp:AttachmentDownloadDir` defaults to `~/Downloads/mailvec/`. Avoid `~/Library/Caches/` (hidden) and `~/Documents/` (TCC-blocked).
- **Output filename is `{messageId}-{partIndex}-{sanitized-name}`.** The id+index prefix guarantees no collisions and keeps the originating email greppable.

## Cross-tool contract

- **`get_email` advertises the `partIndex` Claude needs.** Don't rename `partIndex` without updating both tools — Claude reads the field name from `get_email`'s output schema and passes it back to `get_attachment`.

## Security

- **Filename sanitization + path containment.** `AttachmentExtractor.ResolveSafeFileName` runs `Path.GetFileName` on the claimed filename, which strips any directory component regardless of separator style. `ResolveSafeOutputPath` does a defence-in-depth canonical-path containment check (`Path.GetFullPath` of target inside `Path.GetFullPath` of download dir) and refuses to overwrite an existing symlink at the destination (TOCTOU vector). Don't relax either.

## MIME handling

- **Override `application/octet-stream` from the filename extension.** Many mailers attach PDFs / DOCX / images with `Content-Type: application/octet-stream`. `AttachmentExtractor.ResolveContentType` substitutes a real MIME when the declared type is generic and the extension is known. Specific declared MIMEs are preserved.
- **Images and small text-ish files are also inlined as native MCP blocks.** `image/*` → `ImageContentBlock` so Claude vision works in one round trip without a filesystem MCP. `text/*` + a few application MIMEs (json, xml, yaml) under `Mcp:AttachmentInlineTextMaxBytes` → an extra `TextContentBlock` with strict UTF-8 decoding (`UTF8Encoding(throwOnInvalidBytes: true)` so a CSV that claims `text/*` but isn't valid UTF-8 just omits the inline text). The file lands on disk in both cases regardless.
- **`Blob` / `Data` setters take `ReadOnlyMemory<byte>` of the *base64 string's UTF-8 encoding*** (matters for `ImageContentBlock.Data`). Use `Encoding.UTF8.GetBytes(Convert.ToBase64String(rawBytes))`. Don't pass raw bytes (the SDK won't base64-encode for you) and don't pass a `string` (won't compile).

## Idempotency

- **Re-extraction is idempotent.** If the target file already exists with matching size, we skip the rewrite and set `WasReused: true`. Hashing would be more rigorous but Maildir parsing is the dominant cost; size is a good-enough fingerprint.
