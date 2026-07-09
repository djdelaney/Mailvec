# Future ideas

Considered, then deferred. Captured here so the reasoning isn't lost if someone re-opens the question later.

## Cross-vendor / cloud-LLM access via public HTTPS

The Anthropic / Google / OpenAI cloud clients (Claude.ai web app, Gemini in the browser, ChatGPT Connectors) cannot reach `127.0.0.1` since they're themselves cloud services. Exposing Mailvec to them would need three things on top of today's HTTP transport:

1. **Public reachability.** Cloudflare Tunnel (`cloudflared`) or Tailscale **Funnel** (the public variant — ordinary tailnet doesn't reach those clients) terminates TLS so the MCP server can stay bound to `127.0.0.1` and the tunnel connects locally.
2. **OAuth 2.1 (PKCE).** Cloud connectors expect MCP's standard OAuth flow. The .NET MCP SDK has authentication scaffolding; the open call is the issuer — self-hosted, Cloudflare Access, or Tailscale identity in front are all viable, with different implications for who can approve a new login.
3. **Per-tool authorization.** All current tools are read-only against the local DB and Maildir, so the simplest scope is "any authenticated user can call any tool." Revisit if mutating tools are added.

Deferred because the value of "Claude.ai / ChatGPT / Gemini in the browser searching my email" is real but lower than the operational cost of running OAuth + a public tunnel for a single-user system. The Phase 5 local-agent path (Gemini CLI, Codex CLI, ChatGPT desktop) covers most of the same use cases without the auth surface or external tunnel dependency.

*Update: the Claude-iOS slice of this is no longer deferred — a concrete design exists in [remote-access-cloudflare.md](remote-access-cloudflare.md) (Cloudflare Tunnel + Access), and the Docker deployment already ships a `cloudflared` sidecar (`compose.yml`, `tunnel` profile) with go-live tracked in [deploy-docker.md](deploy-docker.md). The cross-vendor (ChatGPT/Gemini browser) part remains deferred.*

## Tailnet-only access from another personal machine

A middle ground between local-only and public — laptop on the same Tailscale tailnet hitting the Mac mini's MCP server. Tailscale ACLs gate at the network layer, so no OAuth is needed; the change is two config knobs (`Mcp:BindAddress` from `127.0.0.1` to the tailnet IP, **and** the tailnet IP/hostname added to `Mcp:AllowedHosts` — HostGuard 403s any Host header that isn't loopback or allowlisted) plus a launchd plist re-render. Cheap when wanted; not built today.

## Multi-user / federated identity

Implied by any cloud-access path. Out of scope for this single-user system.

## Packaged distribution (installer + notarized artifacts)

Today the **only** way to get any part of Mailvec is to build from source: clone
the repo, install the prereqs via Homebrew (including the .NET 10 SDK, and full
Xcode + xcodegen if you want the tray), then `ops/install-all.sh`. That's fine
for the author and for contributors; it's a real adoption wall for anyone else.
A distribution story would have three artifacts, all buildable from the
existing scripts:

1. **Notarized tray `.app`.** `ops/build-tray.sh` already signs with a
   Developer ID certificate when one is in the keychain — but without
   notarization, a downloaded `.app` is killed by Gatekeeper on another
   machine (`install-tray.sh`'s quarantine-strip only covers local builds).
   The missing lane is `xcrun notarytool submit` (App Store Connect API key)
   + `xcrun stapler staple`, after which a zipped `.app` can be attached to a
   GitHub Release. This removes the Xcode + xcodegen prerequisite for tray
   users entirely.
2. **Services + CLI.** The four .NET binaries are already `dotnet publish`-ed
   by `ops/install.sh`; a release artifact would be that published output
   (self-contained, like the MCPB, to drop the .NET SDK prerequisite) plus
   the installer running against it instead of the working tree. Signing +
   notarization applies here too — launchd runs local unsigned binaries fine,
   but downloaded ones carry quarantine. A Homebrew tap/cask is the
   alternative packaging, with its own update story.
3. **Prebuilt `.mcpb` per release.** `ops/build-mcpb.sh` output attached to
   the GitHub Release — it's already self-contained; it just isn't published
   anywhere. (It's the read-side only: without the installed services there
   is nothing to search — the `setupHint` guard covers that failure mode.)

CI can build all three on a `v*` tag now that unified versioning + tagging
exist. What stays user-owned regardless of packaging: mbsync config, the IMAP
app-password in the Keychain, and Ollama model pulls — the installer
prompts/checks for these but deliberately doesn't own them.

Deferred until there are actual second users to distribute to; sequenced so
the tray notarization lane (the biggest UX win per unit of work) can ship
first on its own.

## Internationalization (CJK search + localized reply trimming)

Parked until there's a real user with substantial non-English mail. Two
separate problems, one trigger:

1. **CJK is dead in the keyword leg.** `messages_fts` uses `porter unicode61`,
   which segments on whitespace/punctuation — Chinese/Japanese text indexes as
   one giant token, so BM25 matches nothing inside it and hybrid quietly
   degrades to vector-only (losing exact-match strength: names, order numbers,
   domains). The standard fix is FTS5's built-in **trigram** tokenizer (works
   for any language, no segmenter), but it changes BM25 behavior for English
   too — shifting ranking on *every* keyword query. That makes it a design
   session, not a patch: full `rebuild-fts`, complete eval re-baseline, and
   possibly a dual-index design (porter for Latin, trigram shadow index) to
   avoid regressing the tuned English experience. Measurement gap: the eval
   query set is English — scoring a CJK improvement needs CJK mail and CJK
   labeled queries first.
2. **ReplyTrimmer only speaks English** ("On … wrote:",
   "-----Original Message-----"). Localized markers ("Am … schrieb:",
   "Le … a écrit :", 差出人:) sail past it, so non-English reply threads
   re-embed the full quoted history in every message — inflating the vector
   space and BM25 term counts with exactly the duplication the trimmer exists
   to prevent. Mechanically easy (Gmail/Outlook localizations are well
   documented; add patterns + per-language fixtures) and the cheap first move
   when the trigger arrives; still needs re-processing affected messages and
   a re-baseline.

## Still open (small)

Carried forward from the original design doc — none are committed work, all gated on a problem actually being observed:

- **`datetime(date_sent)` expression index.** `date_sent` stores mixed-offset
  ISO strings, so every date-ordered query (query-less browse, `FolderStats`'s
  per-folder oldest/latest, date-range filters) wraps the column in
  `datetime()` for correct ordering — which makes `idx_messages_date_sent`
  unusable and full-scans instead. Fix is a v9 migration adding
  `CREATE INDEX … ON messages(datetime(date_sent))` (or a normalized-UTC sort
  column). Parked because at ~80k messages the scan is tens of ms: benchmark
  against a live-DB copy (`ops/export-db.sh`, then time browse/`list_folders`
  with and without the index) before shipping a migration. Un-park when that
  latency is user-visible or a corpus hits 200k+.
- **Thread reconstruction.** Today's `In-Reply-To` / `References` heuristic is acceptable; revisit if mismatches with Fastmail's JMAP threading become a usability issue.
- **JMAP-specific metadata.** IMAP flags are available via mbsync, but JMAP-only fields (masked email, server-side labels) would require a separate JMAP path. Not currently planned.
- **WAL checkpointing strategy.** No periodic auto-checkpoint configured beyond SQLite's default (every 1000 frames). For one-off cleanup after a bulk embed, `mailvec checkpoint` runs `PRAGMA wal_checkpoint(TRUNCATE)`. Worth measuring `-wal` file growth on a long-running install before deciding whether automatic periodic checkpoints are needed.

## Out of scope entirely

Sending mail, modifying server-side state (marking read, moving, deleting), multi-account support, calendar/contacts/files (even though Fastmail offers these via CalDAV/CardDAV/WebDAV — this project is mail-only), a web UI, and real-time push notifications (mbsync is timer-driven, not IDLE/JMAP push).

(OCR for image-only PDFs and images was originally out of scope; it now ships in the embedder via a local Ollama vision model — see [contributing/attachment-ocr.md](contributing/attachment-ocr.md).)
