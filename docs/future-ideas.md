# Future ideas

Considered, then deferred. Captured here so the reasoning isn't lost if someone re-opens the question later.

## Cross-vendor / cloud-LLM access via public HTTPS

The Anthropic / Google / OpenAI cloud clients (Claude.ai web app, Gemini in the browser, ChatGPT Connectors) cannot reach `127.0.0.1` since they're themselves cloud services. Exposing Mailvec to them would need three things on top of today's HTTP transport:

1. **Public reachability.** Cloudflare Tunnel (`cloudflared`) or Tailscale **Funnel** (the public variant — ordinary tailnet doesn't reach those clients) terminates TLS so the MCP server can stay bound to `127.0.0.1` and the tunnel connects locally.
2. **OAuth 2.1 (PKCE).** Cloud connectors expect MCP's standard OAuth flow. The .NET MCP SDK has authentication scaffolding; the open call is the issuer — self-hosted, Cloudflare Access, or Tailscale identity in front are all viable, with different implications for who can approve a new login.
3. **Per-tool authorization.** All current tools are read-only against the local DB and Maildir, so the simplest scope is "any authenticated user can call any tool." Revisit if mutating tools are added.

**The Anthropic slice of this shipped.** Cloudflare Tunnel + Access Managed OAuth is live and serves every Claude surface — see [remote-access-cloudflare.md](remote-access-cloudflare.md) for the as-built wiring. So (1) and (2) above are solved *generically*: the tunnel and the OAuth front are vendor-agnostic infrastructure that a ChatGPT or Gemini connector could register against too.

**The cross-vendor part is still deferred, and the reason has changed.** It's no longer operational cost — that's a sunk cost now. It's (3): there is still no per-tool or per-client authorization. Today's model is "one identity, all seven tools, the whole mailbox." Adding a second vendor's connector means handing a second cloud that same unscoped access, and the Access policy has no way to say "this client gets `search_emails` but not `view_attachment`." That's a real design problem (Access service tokens per client? per-tool scopes at the origin?), not a config toggle — and it's the same per-client scoping [security.md](security.md) parks under "More local clients don't change the threat model". Un-defer when there's an actual reason to want a non-Claude cloud client, and expect to solve scoping first.

Note this is the *only* surviving cross-vendor item: the local-agent half (per-provider stdio/HTTP config for Gemini CLI, Codex CLI, ChatGPT desktop) was dropped outright in 2026-08-10, because one OAuth-gated endpoint already serves any MCP-capable client and per-provider wiring bought nothing a URL doesn't. Cloud access is a different question — it's blocked on authorization, not on plumbing.

## ~~Tailnet-only access from another personal machine~~ (obsolete)

Was a middle ground between local-only and public: a laptop on the same Tailscale tailnet hitting the Mac mini's MCP server, gated by Tailscale ACLs at the network layer instead of OAuth. **Moot now.** The server no longer lives on the Mac, and the public OAuth-gated tunnel already reaches every device from anywhere — a tailnet path would be strictly more setup for strictly less reach. Kept only so the idea isn't re-proposed.

## Multi-user / federated identity

Still out of scope — the archive is single-account and nothing scopes results per-caller, so a second identity on the Access policy would get the owner's entire mailbox rather than a view of their own. That's a data-model problem, not an auth-config one. See [security.md → What's out of scope](security.md#whats-out-of-scope).

## Adversarial testing of the prompt-injection framing

**The one deferred item that makes a shipped control weaker than it looks**, so
it's written down rather than left implied.

Mailvec classifies everything it returns as untrusted sender-controlled data —
in `ServerInstructions`, in every mail-bearing tool description
(`ToolText.UntrustedContent`), and via `ReadOnly`/`OpenWorld` tool annotations.
`McpSurfaceTests` pins all of it at the wire. **But those tests assert the text
*reaches the client*, not that a model *acts on it*.** A crafted message that
talked an agent into chaining Mailvec output into another connector would pass
every test in this repo today. The framing is a mitigation of unmeasured
strength; the test count says nothing about its efficacy.

What closing it would actually take — closer in shape to the `baselines/` eval
harness than to a unit test:

- **Hostile fixtures across every channel the framing claims to cover**: plain
  text body, hidden HTML (the `font-size:0` / preheader tricks `HtmlToText`
  already strips for other reasons), an attachment filename, extracted PDF text,
  and OCR'd text rendered *into* a page image. The last is the interesting one —
  it's the only channel where the payload never exists as text anywhere in the
  pipeline, so nothing upstream could filter it even in principle.
- **A real model with a second, observable, harmless tool attached** — the
  measurement is whether the model reaches for that tool, not whether it says
  something alarming. Without a second tool there's no exfiltration path to
  observe and the test proves nothing.
- **A pass criterion that survives model updates.** Injection success is
  probabilistic, so a single run is noise; this needs a rate over N trials and a
  threshold, which is why it belongs with the eval harness rather than in
  `dotnet test`. It will also need re-running on model changes, like a ranking
  baseline.

Deliberately **not** the answer: regex/heuristic detection of "injection-looking"
text. It fails open on everything it doesn't match while reading like a control
that works, and it would make this item look closed. See
[security.md → Hostile mail content](security.md#hostile-mail-content-indirect-prompt-injection).

Un-defer when either a mutating tool lands (raising what a successful injection
gets you from "read your own mail" to "act on your behalf"), or Mailvec is
routinely used alongside connectors that can send or post.

## A GUI, if one is ever wanted again

A SwiftUI menu-bar tray app and the plain-REST `/tray/*` surface it polled were
**removed** once the container became the only deployment in use and the author
stopped using the tray. Recoverable from git history if that reverses.

Two things to know before recovering it, because neither is visible in the diff:

- **The Swift app recovers cleanly; the C# glue does not.** The app talks JSON
  over HTTP and is self-contained. The ~3.4k lines of `Core/Tray` + `Mcp/Tray`
  glue were wired into Core and Mcp APIs that will have moved, so expect to
  rewrite it rather than cherry-pick.
- **A remote GUI needs identity work that dominates the effort.** `/tray/*` had
  no per-request auth and relied entirely on being loopback-only. Exposing it
  needs origin authentication (`Mcp:Access` could cover it with an audience
  policy), a non-interactive credential the app can hold — which makes it a
  *second identity* and invalidates the single-identity acceptances in
  security.md — and CSRF protection on the mutating POSTs, which previously
  relied on being unreachable rather than on any token.

## Packaged distribution (installer + notarized artifacts)

Today the **only** way to get any part of Mailvec is to build from source: clone
the repo, install the prereqs via Homebrew (including the .NET 10 SDK), then
`ops/install-all.sh`. That's fine for the author and for contributors; it's a
real adoption wall for anyone else. A distribution story would have two
artifacts, both buildable from the existing scripts:

1. **Services + CLI.** The four .NET binaries are already `dotnet publish`-ed
   by `ops/install.sh`; a release artifact would be that published output
   (self-contained, like the MCPB, to drop the .NET SDK prerequisite) plus
   the installer running against it instead of the working tree. Signing +
   notarization applies here too — launchd runs local unsigned binaries fine,
   but downloaded ones carry quarantine. A Homebrew tap/cask is the
   alternative packaging, with its own update story.
2. **Prebuilt `.mcpb` per release.** `ops/build-mcpb.sh` output attached to
   the GitHub Release — it's already self-contained; it just isn't published
   anywhere. (It's the read-side only: without the installed services there
   is nothing to search — the `setupHint` guard covers that failure mode.)

CI can build both on a `v*` tag now that unified versioning + tagging
exist. What stays user-owned regardless of packaging: mbsync config, the IMAP
app-password in the Keychain, and Ollama model pulls — the installer
prompts/checks for these but deliberately doesn't own them.

Deferred until there are actual second users to distribute to.

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

## Faster mail arrival: one-minute polling (shipped)

**Kept here only so the reasoning that made it safe isn't re-derived.**
`MBSYNC_INTERVAL_SECONDS` now defaults to **60** in `compose.yml`,
`.env.example` and the Dockerfile, promoted after running at that cadence on
the author's deployment. The load-bearing parts moved to the code they govern:
the Dockerfile comment explains why the loop cannot overlap its own runs (the
interval is a delay *after* completion, not an independent timer, so a backlog
pull never queues anything behind it), `.env.example` explains what a shorter
interval actually costs, and CLAUDE.md's heartbeat section records the
prerequisite — the beat runs on its own 60s timer, because beating only on sync
completion would report a *busy* sidecar as dead during exactly the long
backlog pulls an operator most wants to watch. **Do not re-couple the beat
cadence to the sync cadence**; that is the bug the prerequisite removed.

The macOS launchd plist deliberately stayed at 600s. Its comment records a
dated observation of `.mbsyncstate` lock failures at 300s, on a path this
deployment no longer exercises, and launchd's `StartInterval` is a genuinely
independent timer rather than a sleep-after-completion — so the container's
result does not transfer. Re-measure on a live macOS install before changing
it; overwriting a measurement with an inference is how a runbook goes quietly
wrong.

**Still deferred: going below a minute by splitting the sync.** Keep one
serialized runner, poll INBOX on a tight loop, and retain a less frequent
full-account `mbsync -a` for labels, server-side filing, moves, deletions and
flags. More machinery, and it changes freshness semantics outside INBOX, so
it's a fallback to a *measured* cost problem rather than a next step. The two
costs that would trigger it: provider throttling, or SQLite writer-lock
contention from unbatched indexer scans — scans are watcher-driven, so a
shorter cadence doesn't add them, it spreads the same mail across ~10x as many,
and a scan's dominant cost is independent of how much new mail it carries
(`MaildirScanner`'s mtime fast path touches `sync_state` for every file it
walks). Watch embedder `SQLITE_BUSY` retries and OCR throughput, not scan
duration.

## Still open (small)

Carried forward from the original design doc — none are committed work, all gated on a problem actually being observed:

- **`datetime(date_sent)` expression index.** `date_sent` stores mixed-offset
  ISO strings, so every date-ordered query (query-less browse, `FolderStats`'s
  per-folder oldest/latest, date-range filters) wraps the column in
  `datetime()` for correct ordering — which makes `idx_messages_date_sent`
  unusable and full-scans instead. Fix is a v10 migration adding
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
