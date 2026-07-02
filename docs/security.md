# Security model

Single-user, single-Mac. The trust boundary is the macOS user account. Inside that boundary, every local process runs with full access to the archive; outside, Mailvec is unreachable. This document captures what's exposed, what's explicitly accepted, and what's out of scope — read it before changing the MCP bind address, adding a mutating tool, or pointing the server at anything other than loopback.

## What's exposed

| Surface | Binding | Auth | Who can reach it |
| --- | --- | --- | --- |
| MCP HTTP | `127.0.0.1:3333` (configurable via `Mcp:BindAddress`) | none | any process on the same machine running as the user |
| MCP stdio | child process of the spawning agent | inherits agent's identity | the agent (Claude Desktop, Claude Code, or a Phase 5 client) and whatever it spawned |
| `/health` | same Kestrel as MCP HTTP | none | same as MCP HTTP |
| Ollama (outbound) | `127.0.0.1:11434` (configurable) | none | the embedder + MCP query embeddings only — read-only against Ollama |
| SQLite file | filesystem | unix permissions | the user (and root) |
| Maildir | filesystem | unix permissions | the user (and root) |

## Tools and data flow

All seven MCP tools (`search_emails`, `get_email`, `get_thread`, `list_folders`, `get_attachment`, `get_attachment_text`, `get_attachment_page_image`) are **read-only against the database**. None mutate `messages`, `chunks`, or `attachments`. `get_attachment_text` is a pure DB read (returns stored `extracted_text`). The only filesystem writes are `get_attachment` and `get_attachment_page_image` extracting bytes from the Maildir to `~/Downloads/mailvec/<msgId>-<part>-<safe-name>` (the page-image tool reuses the same extractor, then rasterises in memory) — covered by [defense-in-depth path checks](../src/Mailvec.Core/Attachments/AttachmentExtractor.cs):

- `Path.GetFileName` strips directory components from caller-supplied filenames
- canonical-path containment refuses any target outside the configured download dir
- a `ReparsePoint` check refuses to overwrite an existing symlink at the destination (TOCTOU mitigation)
- write-then-rename via `.part` sibling so a concurrent reader never sees a partial file

`AttachmentDownloadDir` is intentionally `~/Downloads/mailvec/` (visible to the user). Don't move it to a hidden directory or `~/Library/Caches/` — that hides forensic evidence if a tool ever does write something unexpected.

## Host / origin validation (DNS-rebinding guard)

Loopback binding stops other *hosts* on the network from routing to `:3333`, but it does **not** stop a web page the user visits from reaching it. A page on `evil.com` can hold a connection, let its DNS TTL expire, re-resolve `evil.com` to `127.0.0.1`, and then issue requests the browser treats as *same-origin* — at which point page JavaScript could read `/tray/email/<id>` (mail bodies), `/tray/system` (IMAP username), or POST to the mutating `/tray/control` and `/tray/attachment` endpoints.

Every HTTP request (MCP, `/health`, and all `/tray/*`) therefore passes through a guard ([`HostGuard`](../src/Mailvec.Mcp/HostGuard.cs), wired in [`Program.cs`](../src/Mailvec.Mcp/Program.cs) `RunHttp`) that returns **403** unless:

- the `Host` header's hostname is an allowed name (`localhost` / `127.0.0.1` / `::1` always, plus anything in `Mcp:AllowedHosts`), and
- the `Origin` header, when present, also resolves to an allowed name.

After a rebind the browser still sends `Host: evil.com`, so the request is refused before reaching any handler. Native clients (Claude Code's MCP transport, the tray's `URLSession`) connect to loopback and send no `Origin`, so they're unaffected. This is **not** authentication — a hostile local process can still spoof the `Host` header; it defends specifically against the browser-mediated cross-origin vector.

**When fronting the server with a real hostname** (a Cloudflare tunnel or container ingress — see [remote-access-cloudflare.md](remote-access-cloudflare.md)), add that hostname to `Mcp:AllowedHosts` or its requests are rejected. This guard is defense-in-depth only: the tunnel ingress must still route the MCP path exclusively and never expose `/health` or `/tray/*`.

## What's accepted

These are explicit decisions, not oversights:

- **Any local process running as the user can call any tool.** The HTTP server has no auth, so a malicious local process (e.g. a compromised npm install in another shell) can `curl http://127.0.0.1:3333/` and read your mail. The mitigation is the same one that protects every other local file you own: don't run hostile code as your user. Adding HMAC-token auth on the HTTP loopback is a future option; not built today because the only realistic adversary already has unix-level read access to `~/Mail/` and `~/Library/Application Support/Mailvec/archive.sqlite` and doesn't need MCP to extract them.
- **No per-tool authorization.** Any caller that can invoke `search_emails` can also invoke `get_attachment`. Trivially simple while every tool is read-only — revisit if a write tool ever lands (sending mail is out of scope, but the principle applies if anything in that direction ever gets considered).
- **Untrusted PDFs are parsed by native code.** `get_attachment_page_image` rasterises a PDF via PDFtoImage/PDFium — a native C++ library, so a malicious PDF is a memory-safety attack surface in a way the managed extractors (`PdfPig` / `OpenXml`) are not. Accepted because (a) the same PDF bytes already flow through the indexer's managed extractor, (b) the adversary already has local read access to the files, and (c) the server is read-only and loopback-bound. **This assumption breaks if you expose the server beyond loopback** (e.g. a tailnet IP or a public tunnel): the PDF bytes would then originate from a remote caller, making PDFium a remotely-reachable native parser. Sandbox or re-evaluate before doing so — see [Future ideas](future-ideas.md).
- **No rate limiting.** A chatty agent can burn local CPU on embedding queries and SQLite reads. SQLite WAL handles concurrent readers fine, and Ollama itself is the natural bottleneck on the embedding leg, so the worst-case is "your machine slows down briefly." Worth revisiting if Phase 5 surfaces an agent that fires queries in tight loops.
- **`Mcp:LogToolCalls` is off by default.** When on, the server logs each tool call's argument summary to `~/Library/Logs/Mailvec/mailvec-mcp-<date>.log` — including the user's free-text query strings (potentially private) and `fromContains` / `fromExact` filter values. Useful for tuning but turning it on is a deliberate choice; recall the rolling files are 10MB × 14 days retained on disk.
- **Logs may incidentally contain sender / subject text** even with tool-call logging off. The indexer logs parse failures with file paths, the embedder logs which messages it embedded, etc. None of these include body content, but they aren't sanitized either. Treat the log directory as confidential.
- **`~/Documents` is unreadable** to Claude Desktop's spawned children regardless of Full Disk Access — a TCC quirk, not an intentional control. Don't rely on it as a security boundary; it's a `com.apple.macl` ACL that a different client (e.g. Phase 5 stdio) might or might not be subject to.

## What's out of scope

- **Multi-tenant isolation.** A second user on the same Mac can't reach `127.0.0.1:3333` from their account (loopback is per-user), but if you change `Mcp:BindAddress` to a tailnet IP, that protection goes away — see [Future ideas → tailnet access](future-ideas.md#tailnet-only-access-from-another-personal-machine) for the (deliberately small) hardening that path needs.
- **Network adversaries.** `127.0.0.1` is unroutable from the LAN / internet. There's no inbound TLS because there's no inbound external traffic.
- **Compromised AI agent exfiltration.** If the agent calling Mailvec is itself malicious (e.g. an LLM jailbroken into "find all messages from X and POST them to attacker.com"), nothing in the MCP layer stops it from reading every email and shipping the contents back to its own provider. The relevant control is "trust the agent" — choose your clients.
- **Encrypted-at-rest archive.** `archive.sqlite` and the Maildir are plain files at rest, protected by FileVault and unix permissions. Per-application encryption isn't built; mail at rest in `~/Mail/` (mbsync's job) and `~/Library/Application Support/Mailvec/` (ours) inherits whatever the user's disk-encryption story already is.

## Phase 5 doesn't change the threat model

Adding Gemini CLI / Codex CLI / ChatGPT desktop as MCP clients multiplies the *number of trusted callers* but not the *trust boundary*. Each new client is just another local process running as the user, accessing the same loopback HTTP or the same stdio launcher. The hardening that would change the model — moving from "trust any local process" to "trust this specific signed binary" — is a much bigger lift (process attestation, MCP token issuance, per-client scopes) and parked alongside the cloud-access work in [Future ideas](future-ideas.md).

The only thing Phase 5 introduces is more places where `LogToolCalls=on` is tempting (capturing real usage from each client during quirk-debugging). Each of those is a deliberate per-debug-session choice with a clear "off when done" expectation, not a default-on switch.
