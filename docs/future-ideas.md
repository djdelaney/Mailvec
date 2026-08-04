# Future ideas

Considered, then deferred. Captured here so the reasoning isn't lost if someone re-opens the question later.

## Cross-vendor / cloud-LLM access via public HTTPS

The Anthropic / Google / OpenAI cloud clients (Claude.ai web app, Gemini in the browser, ChatGPT Connectors) cannot reach `127.0.0.1` since they're themselves cloud services. Exposing Mailvec to them would need three things on top of today's HTTP transport:

1. **Public reachability.** Cloudflare Tunnel (`cloudflared`) or Tailscale **Funnel** (the public variant — ordinary tailnet doesn't reach those clients) terminates TLS so the MCP server can stay bound to `127.0.0.1` and the tunnel connects locally.
2. **OAuth 2.1 (PKCE).** Cloud connectors expect MCP's standard OAuth flow. The .NET MCP SDK has authentication scaffolding; the open call is the issuer — self-hosted, Cloudflare Access, or Tailscale identity in front are all viable, with different implications for who can approve a new login.
3. **Per-tool authorization.** All current tools are read-only against the local DB and Maildir, so the simplest scope is "any authenticated user can call any tool." Revisit if mutating tools are added.

**The Anthropic slice of this shipped.** Cloudflare Tunnel + Access Managed OAuth is live and serves every Claude surface — see [remote-access-cloudflare.md](remote-access-cloudflare.md) for the as-built wiring. So (1) and (2) above are solved *generically*: the tunnel and the OAuth front are vendor-agnostic infrastructure that a ChatGPT or Gemini connector could register against too.

**The cross-vendor part is still deferred, and the reason has changed.** It's no longer operational cost — that's a sunk cost now. It's (3): there is still no per-tool or per-client authorization. Today's model is "one identity, all seven tools, the whole mailbox." Adding a second vendor's connector means handing a second cloud that same unscoped access, and the Access policy has no way to say "this client gets `search_emails` but not `view_attachment`." That's a real design problem (Access service tokens per client? per-tool scopes at the origin?), not a config toggle — and it's the same hardening [security.md](security.md) parks under Phase 5. Un-defer when there's an actual reason to want a non-Claude cloud client, and expect to solve scoping first.

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

## A remote story for `/tray/*`

Referenced from [security.md](security.md#up-health-and-tray), which says
re-enabling the tray surface on an internet-fronted deployment "means building
that first". This is that entry.

Today `/tray/*` is loopback-only by construction, and `TrayExposureGuard`
**refuses to start** the server if it is enabled on anything but a loopback-only
deployment — a deliberate hard failure, not a default, because the symptom of
getting it wrong is full message bodies, the folder map, full-text search and
the IMAP account served with no authentication, plus mutating POSTs. That guard
is the invariant; nothing below weakens it.

What a remote tray would actually need, and why none of it is free:

- **Per-request authentication at the origin.** The surface has none of its own.
  `Mcp:Access` now exists and could plausibly cover `/tray/*` with an audience
  policy — that's the cheapest path and didn't exist when the guard was written.
- **A credential the tray can hold.** A SwiftUI menu-bar app polling every 5s
  needs a non-interactive credential; an Access service token in the macOS
  keychain is the obvious candidate, which makes the tray a *second identity*
  and invalidates the single-identity acceptances in security.md.
- **CSRF protection on the mutating POSTs** (`/tray/control`, `/tray/attachment`),
  which currently rely on being unreachable rather than on any token.
- **Revisiting the guard's trigger**, which keys off a non-loopback bind — the
  correct signal today precisely because HostGuard always admits loopback Host
  names.

**Not worth doing for its own sake.** The tray is a local-install convenience;
the container deployment has no consumer for it. Un-defer only if someone
actually wants a remote tray, and expect the identity work above to dominate.

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

## Near-realtime mail via an IMAP IDLE watcher

**The problem.** New mail becomes searchable somewhere between instantly and
~10 minutes after it arrives, and the spread is almost entirely one term. The
chain is: mbsync pulls it (`MBSYNC_INTERVAL_SECONDS`, **600s**) → the indexer's
`MaildirWatcher` sees the new file (500ms debounce, effectively immediate) →
the embedder picks it up (30s poll). Everything downstream of mbsync is already
event-driven; mbsync is the only polling step, and it dominates.

**Why not just lower the interval.** Already considered and rejected once — the
600s figure is inherited from the launchd plist for a reason recorded in the
Dockerfile: tighter schedules hit mbsync's `.mbsyncstate` flock and fail with
`channel is locked` when a backlog pull overruns the interval. Polling harder
trades latency for a failure mode that gets *worse* the more mail there is to
sync. An IDLE watcher sidesteps it by not polling at all — it syncs when the
server says there's something to sync.

**The shape.** [`goimapnotify`](https://gitlab.com/shackra/goimapnotify) (or
similar) holds an IMAP IDLE connection and runs a command on new mail; it's the
conventional pairing with isync. Either a new sidecar or folded into the
existing `mbsync` image (Alpine + isync + one Go binary). It needs no
capabilities — outbound TLS only — so it inherits the current hardening posture
(`cap_drop: [ALL]`, `no-new-privileges`, mem/pids limits) unchanged, and it
wants the same Fastmail app password, already a compose file-secret.

**Four things to get right, two of which this repo has already learned
elsewhere:**

1. **Syncs must still be serialized.** IDLE-triggered runs can collide with each
   other and with the periodic backstop, which is the same `.mbsyncstate` flock
   collision that capped the interval in the first place — event-driven doesn't
   make it go away, it makes it bursty. The pattern is already in the codebase:
   `MessageIngestService` funnels the watcher pulse and the periodic timer
   through a coalescing single-slot channel with one consumer, precisely so two
   scans can't overlap. Same problem, same fix.
2. **Keep a periodic sync as a backstop, just longer.** IDLE connections drop
   silently; a pure-event design misses mail for as long as nobody notices the
   connection died. IDLE plus a 15–30 min fallback, rather than IDLE instead of
   polling.
3. **The liveness beat must not ride the sync events.** This is the sharp one.
   Today `mbsync-loop` writes its heartbeat after every attempt, which is honest
   only because attempts are on a fixed 600s cadence that the beat file
   declares. Event-driven syncing breaks that: a quiet night legitimately
   produces no events, and a beat-on-sync design would read as *dead* rather
   than idle — the exact failure `ServiceHeartbeat` already documents ("the
   liveness beat must never be emitted from the work loop", learned when one
   Ollama batch outlived the embedder's poll interval). The watcher needs its
   own timer-based beat. The good news is the file format already
   self-describes: `MbsyncHeartbeatFile` reads the interval from the beat's
   second line and `ServiceHeartbeat` judges staleness against it, so as long as
   the watcher writes *its beat cadence* (not its sync cadence), `/health`,
   `/up` and the `mailvec-mbsync` Kuma monitor all adapt with no code change.
4. **Folder scope and connection count.** IDLE is per-mailbox, so watching every
   Fastmail folder means a connection per folder. Realistically: watch `INBOX`,
   trigger a full `mbsync -a`, and let the backstop cover the rest. Worth
   checking Fastmail's per-account connection limits before widening.

**One semantic change to note:** a stale mbsync heartbeat currently means "the
loop stopped." Afterwards it would mean "the watcher died, *or* the IDLE
connection dropped and the backstop also failed" — still actionable, but the
alert text should say so.

**Un-defer when** the latency is actually felt — someone asking about a mail
they know arrived and getting nothing. Until then the cheap experiment is to
drop `MBSYNC_INTERVAL_SECONDS` and see whether the flock contention the 600s
figure was chosen to avoid actually materialises on this corpus; that answer is
worth having before building anything.

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
