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

## Faster mail arrival via one-minute polling

**The problem.** New mail becomes searchable somewhere between instantly and
~10 minutes after it arrives, and the spread is almost entirely one term. The
chain is: mbsync pulls it (`MBSYNC_INTERVAL_SECONDS`, **600s**) → the indexer's
`MaildirWatcher` sees the new file (500ms debounce, effectively immediate) →
the embedder picks it up (30s idle poll). Keyword search is ready during the
indexer pass; semantic search follows after the embedder pass. Everything after
mbsync is already fast enough that the ten-minute IMAP poll dominates normal
steady-state delivery.

**The key finding.** The current Linux sidecar cannot overlap its own mbsync
runs. `mbsync-loop` starts `mbsync -a`, waits for that exact child to finish,
and only then sleeps for `MBSYNC_INTERVAL_SECONDS`. (It used to write the
heartbeat between those two steps; that moved to its own timer — see the
prerequisite below.) The
interval is therefore a delay *after completion*, not an independent timer. If
a backlog pull takes 12 minutes, no one-minute run starts behind it; the next
run starts one minute after the backlog completes. This differs from the old
rationale copied into the Dockerfile and launchd plist, which says a short
schedule collides with an in-flight run. A genuinely separate invocation can
still contend for `.mbsyncstate`, but the production Compose stack declares
one mbsync service and no request-time sync path.

**Verified 2026-08-04, and the Dockerfile comment has been corrected** to state
that the loop cannot self-overlap and that 600s is a load choice rather than a
safety floor. **The launchd plist comment was deliberately left alone**: it
records a dated observation (92% of runs succeeding at 300s, 8% failing with
`channel is locked`), and launchd's own skip-while-running behaviour says those
failures had some other cause. Overwriting a measurement with an inference is
how a runbook goes quietly wrong — re-measure on a live macOS install before
rewriting it, and note that path is not exercised by the author's deployment.

**First move: one minute.** Set the deployment's
`MBSYNC_INTERVAL_SECONDS=60` and recreate only the mbsync sidecar. Do not change
the image default in the same step: a deployment override makes the first move
easy to reverse and separates "does this cadence work on the real account?"
from "should this become the product default?" The effective start-to-start
cadence is `sync duration + 60s`; for the normal incremental case that should
put most new mail on disk within roughly a minute rather than ten.

### Prerequisite: the heartbeat fix (done 2026-08-04)

This was a **blocker**, not a follow-up, and shipping the interval change without
it would have failed the canary's own acceptance criteria.

`MbsyncHeartbeatFile` feeds the beat file's declared interval into
`ServiceHeartbeat.Classify`, which marks a service stale at
`StaleAfterMissedBeats` (3) x that value. The sidecar used to beat **only after
`mbsync -a` returned**, and to declare `MBSYNC_INTERVAL_SECONDS` as the cadence.
At 600s that gave a 30-minute window, comfortably wider than the 12-minute
backlog pull above — so the flaw was invisible. At 60s the window becomes 180
seconds, and **any sync longer than three minutes would report a busy sidecar as
dead** on `/health`, the tray and `mailvec doctor`, during precisely the long
pulls an operator most wants to watch.

This is the failure CLAUDE.md already documents for the .NET services ("the
liveness beat must never be emitted from the work loop"). The tempting patch —
raising `StaleAfterMissedBeats` — is the wrong one: that constant is shared with
the indexer and embedder, so it would degrade dead-worker detection everywhere
to paper over one sidecar.

The sidecar now beats on its own timer for the life of the container, mirroring
`HeartbeatService`, and the beat file's second line declares that **beat**
cadence (a 60s constant) rather than the sync interval. Verified against a stub
`mbsync` sleeping 6s at a 1s beat cadence: the old logic wrote nothing for the
whole sync, the new one beats every second through it. One beater for the whole
run rather than one per cycle, because a per-cycle beater must be killed each
time, which orphans its in-flight `sleep` onto PID 1 and leaks a zombie per sync
(1,440/day at the proposed cadence).

**Do not re-couple the beat cadence to the sync cadence.** They are unrelated
now, and joining them reintroduces the bug this removed.

### Rollout plan

1. **Capture the before window.** Retain at least 24 hours of timestamped mbsync
   and indexer container logs at the 600-second cadence. Record ordinary sync
   duration, failures, `channel is locked` occurrences, and the delay observed
   for a few uniquely identifiable test messages between receipt, mbsync
   completion, and the indexer's next completed scan. This is an observed,
   dated deployment measurement, not a value to copy into this document as a
   permanent fact.
2. **Apply only the interval override.** Set `MBSYNC_INTERVAL_SECONDS=60` in the
   deploy host's `.env`, then recreate the mbsync service. Leave the indexer,
   embedder, Maildir layout, mbsync configuration, and folder patterns
   unchanged.

   **Do not verify this from the heartbeat file.** An earlier draft of this step
   said to confirm its second line becomes `60`. That line is now the *beat*
   cadence — a 60s constant, unrelated to the sync interval — so it reads `60`
   whether or not the override took effect, and the check would confirm the
   change vacuously. Verify from the sidecar's own log instead: successive
   `mbsync -a` start timestamps should sit about `sync duration + 60s` apart.
3. **Run a 48-hour canary.** Include both quiet periods and normal working-day
   traffic. Send several uniquely identifiable messages at different points in
   the minute. Verify that Maildir delivery wakes the indexer and that keyword
   search sees each message on the following scan. Semantic availability is a
   separate downstream measurement because the embedder may add up to its
   30-second idle poll.
4. **Review load and correctness together.** Compare mbsync failures, sync
   duration, IMAP authentication/rate-limit errors, sidecar CPU/network use,
   indexer scan duration, and parse failures with the before window. Pay special
   attention to `channel is locked`: the current loop rules out self-overlap,
   so any occurrence points to another invoker, an interrupted prior run, or a
   stale lock that should be diagnosed rather than "fixed" by restoring ten
   minutes automatically.

   **Measure indexer write volume, not just scan duration** — scan duration
   alone will miss the real cost here. Scans are watcher-driven, so a shorter
   sync cadence does not add scans directly, it *unbatches* them: the same day's
   mail arrives across roughly 10x as many scans. And a scan's dominant cost is
   independent of how much new mail it carries, because `MaildirScanner`'s mtime
   fast path calls `syncState.Upsert` for **every file it walks** (80,559 rows,
   observed 2026-08-04) — a scan carrying one new message costs nearly what one
   carrying two hundred does. Expect total writer-lock occupancy to rise close to
   10x while per-scan duration looks flat. That lock is the single SQLite writer,
   contending under `BEGIN IMMEDIATE` with the embedder's guarded chunk writes
   and the OCR write-back, so watch embedder `SQLITE_BUSY` retries and OCR
   throughput alongside the indexer's own numbers.
5. **Promote or roll back.** Keep 60 seconds if the canary is clean and normal
   mail is consistently keyword-searchable within about 90 seconds of server
   receipt (one poll plus ordinary sync/index time). Restore 600 seconds by
   reverting the single `.env` value if error rate, provider throttling, or
   resource use moves materially. Rollback changes no synchronization state and
   requires no database work.
6. **Only then change repository defaults and docs.** If the production result
   is good, change the Compose default, Dockerfile commentary, launchd template,
   IMAP setup guide, tray schedule expectations, `docs/monitoring-uptime-kuma.md`,
   and this section together so the shipped configuration no longer preserves the
   superseded overlap rationale. The monitoring runbook is on that list because
   the heartbeat fix already moved mbsync's staleness window from 30 minutes to
   3, so a dead sidecar shows up roughly ten times faster.
   **That does not change when Kuma pages**, and an earlier draft of this line
   wrongly said it did: liveness never flips `/health`'s 503 — degraded is
   Ollama-unreachable, embedding-model-mismatch, or embedder-stuck only, and
   liveness rides along in `Services` for clients to render. What changes is the
   payload an operator reads, and any monitor keyed on its content rather than
   on HTTP status. Verify what the monitors actually poll before writing
   anything down — that runbook has been wrong about live state before.

   Keep platform behaviour explicit while you are in there: the Linux loop waits
   then sleeps, so its cadence is `sync duration + interval`; macOS launchd
   misses an interval firing while its job is already running.

### Acceptance criteria

- No concurrent mbsync processes are created by the sidecar.
- No increase in `channel is locked`, authentication, TLS, or provider
  throttling failures compared with the dated before window.
- Ordinary incremental syncs finish reliably; a large backlog merely extends
  the current cycle and never queues a pile of one-minute invocations.
- Under normal caught-up conditions, test mail is keyword-searchable within
  about 90 seconds of server receipt. Outliers caused by an active backlog are
  reported separately rather than folded into the steady-state number.
- The mbsync heartbeat remains known and non-stale **throughout a long backlog
  pull**, not merely between syncs. This is now a genuine test of the beat's
  independence rather than a restatement of the interval: the beat runs on its
  own 60s timer, so a sync of any duration must not move the sidecar to stale.
  A stale reading during a healthy sync means the beat has been re-coupled to
  the work loop — see the prerequisite above.
- The indexer's event-driven scans continue to fire after Maildir writes.

**Possible follow-up, only if one-minute full sync is too expensive.** Keep one
serialized runner but check INBOX every minute and retain a less frequent
full-account `mbsync -a` pass for labels, server-side filing, moves, deletions,
and flags. That is more machinery and changes freshness semantics outside
INBOX, so it is a fallback to a measured cost problem, not part of the first
rollout.

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
