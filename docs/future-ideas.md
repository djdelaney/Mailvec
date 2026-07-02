# Future ideas

Considered, then deferred. Captured here so the reasoning isn't lost if someone re-opens the question later.

## Cross-vendor / cloud-LLM access via public HTTPS

The Anthropic / Google / OpenAI cloud clients (Claude.ai web app, Gemini in the browser, ChatGPT Connectors) cannot reach `127.0.0.1` since they're themselves cloud services. Exposing Mailvec to them would need three things on top of today's HTTP transport:

1. **Public reachability.** Cloudflare Tunnel (`cloudflared`) or Tailscale **Funnel** (the public variant — ordinary tailnet doesn't reach those clients) terminates TLS so the MCP server can stay bound to `127.0.0.1` and the tunnel connects locally.
2. **OAuth 2.1 (PKCE).** Cloud connectors expect MCP's standard OAuth flow. The .NET MCP SDK has authentication scaffolding; the open call is the issuer — self-hosted, Cloudflare Access, or Tailscale identity in front are all viable, with different implications for who can approve a new login.
3. **Per-tool authorization.** All current tools are read-only against the local DB and Maildir, so the simplest scope is "any authenticated user can call any tool." Revisit if mutating tools are added.

Deferred because the value of "Claude.ai / ChatGPT / Gemini in the browser searching my email" is real but lower than the operational cost of running OAuth + a public tunnel for a single-user system. The Phase 5 local-agent path (Gemini CLI, Codex CLI, ChatGPT desktop) covers most of the same use cases without the auth surface or external tunnel dependency.

## Tailnet-only access from another personal machine

A middle ground between local-only and public — laptop on the same Tailscale tailnet hitting the Mac mini's MCP server. Tailscale ACLs gate at the network layer, so no OAuth is needed; the change is one config knob (`Mcp:BindAddress` from `127.0.0.1` to the tailnet IP) plus a launchd plist re-render. Cheap when wanted; not built today.

## Multi-user / federated identity

Implied by any cloud-access path. Out of scope for this single-user system.

## Still open (small)

Carried forward from the original design doc — none are committed work, all gated on a problem actually being observed:

- **Thread reconstruction.** Today's `In-Reply-To` / `References` heuristic is acceptable; revisit if mismatches with Fastmail's JMAP threading become a usability issue.
- **JMAP-specific metadata.** IMAP flags are available via mbsync, but JMAP-only fields (masked email, server-side labels) would require a separate JMAP path. Not currently planned.
- **WAL checkpointing strategy.** No periodic auto-checkpoint configured beyond SQLite's default (every 1000 frames). For one-off cleanup after a bulk embed, `mailvec checkpoint` runs `PRAGMA wal_checkpoint(TRUNCATE)`. Worth measuring `-wal` file growth on a long-running install before deciding whether automatic periodic checkpoints are needed.

## Out of scope entirely

Sending mail, modifying server-side state (marking read, moving, deleting), multi-account support, calendar/contacts/files (even though Fastmail offers these via CalDAV/CardDAV/WebDAV — this project is mail-only), a web UI, and real-time push notifications (mbsync is timer-driven, not IDLE/JMAP push).

(OCR for image-only PDFs and images was originally out of scope; it now ships in the embedder via a local Ollama vision model — see [contributing/attachment-ocr.md](contributing/attachment-ocr.md).)
