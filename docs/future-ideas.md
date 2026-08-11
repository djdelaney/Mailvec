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

## Date-ordering index for `datetime(date_sent)`

> **Status 2026-08-10: SHIPPED as schema v12** —
> `schema/migrations/012_messages_date_sort_index.sql`, and the index is in
> `001_initial.sql` for fresh databases. This section is kept because it is the
> only record of what was measured and, more importantly, of the two index shapes
> that were tried and rejected; CLAUDE.md's invariant links here. Not a proposal
> any more — read it before touching the browse `ORDER BY` or the index.

**The mechanism.** `date_sent` holds `DateTimeOffset.ToString("O")`, so one
column mixes UTC `Z` and `+HH:mm` offsets. Sorted as text, `…07:13:20-05:00`
(12:13Z) lands below `…11:00:00+00:00` (11:00Z) — exactly inverted. Every
date-ordered or date-filtered query therefore wraps the column in SQLite's
`datetime()`, which is load-bearing for correctness and fatal for
`idx_messages_date_sent`: a plain-column index cannot satisfy an expression, so
these paths full-scan `messages` instead. Affected: query-less browse
(`BrowseByFilters`), `FolderStats` / `list_folders`, the `dateFrom`/`dateTo`
filters in `SearchFilterSql`, `reocr` candidate ordering, `purge-deleted`'s
cutoff. Ranked search is unaffected — BM25 and KNN order by relevance.

**Measured 2026-08-10** (observed, not a permanent fact — re-measure before
acting). Copy of the frozen dev corpus: 81,732 messages, 75,414 live, 4.5 GiB,
zero NULL `date_sent`, no `sqlite_stat1`. Numbers below are **through the app's
own stack** — `MessageRepository.BrowseByFilters` / `.FolderStats()` called via
`ConnectionFactory`, so Microsoft.Data.Sqlite over
`SQLitePCLRaw.bundle_e_sqlite3` (reported `sqlite_version()` **3.53.4**) — not a
CLI. Best of 3 warm:

| index | browse | +dateFrom | +folder | list_folders |
|---|---|---|---|---|
| baseline (as shipped) | 215 ms | 162 ms | 106 ms | 499 ms |
| `(datetime(date_sent))` | 219 ms | 189 ms | 106 ms | 485 ms |
| `(date_sent IS NULL, datetime(date_sent))` | **9,173 ms** | 275 ms | 144 ms | 482 ms |
| `(date_sent IS NULL, datetime(date_sent) DESC)` | **<1 ms** | **<1 ms** | 144 ms | 490 ms |

Cross-checked against `sqlite3` CLI 3.51.0 and Python's 3.50.4 on the same copy:
**every ratio and every query plan reproduced on all three builds**, which is
the part worth trusting. Absolute times did not — `list_folders` reads 490 ms
here, 594 ms via the CLI, 998 ms via Python, and 325 ms in `FolderStats`'s own
recorded measurement. Quote ratios from this entry, never the milliseconds.
An additional `(folder, message_id)` index was measured too and changed nothing
(see below), so it is omitted from the table.

Four things that table says, in rough order of how expensive it would be to
learn them the other way:

1. **The obvious index does nothing.** `ON messages(datetime(date_sent))` — what
   this entry prescribed for months — leaves the plan at `SCAN m` and the time
   unmoved. The real `ORDER BY` leads with `m.date_sent IS NULL` (the explicit
   key that keeps undated mail sorting last in both directions, since SQLite puts
   NULLs first ascending), and an index whose first column is a *different*
   expression cannot satisfy that ordering.
2. **The naive repair is ~43x worse than doing nothing.** Adding the `IS NULL`
   key but leaving both columns ASC gets the index adopted and browse takes **9.2
   seconds**: the `ORDER BY` is mixed-direction (`IS NULL` ascending,
   `datetime(...)` descending), so SQLite walks the whole index and still needs a
   `TEMP B-TREE FOR LAST TERM OF ORDER BY`. **The second column must be declared
   `DESC`.** This is the shape of mistake that ships looking principled.
3. **The correct index takes browse from 215 ms to under a millisecond** — below
   `Stopwatch` resolution, plan `SCAN m USING INDEX` with no sort step at all, so
   >200x rather than the ~40x an earlier CLI measurement suggested (that figure
   was inflated by process startup). Costs 102 ms to build and grew the file by
   0 KiB, fitting in existing free pages.
4. **It regresses folder-filtered browse by ~36%** (106 → 144 ms), reproducibly
   on all three SQLite builds, because the planner takes the new ordering index
   and then evaluates the folder `EXISTS` per row. `ANALYZE` does not fix it. Net
   across the paths is overwhelmingly positive, but this is a real cost, not a
   rounding error — and it is why "just add the index" is not the whole design.

**`list_folders` is a different problem and this index is not it.** Its ~490 ms
does not move under any variant. The cost is the `membership` CTE's
`UNION`/`TEMP B-TREE` over both folder sources, not the date ordering. Note also
that `FolderStats`'s own remarks propose "a covering index on
`messages(folder, message_id)`" if its cost ever matters: **measured, that index
changes nothing here** (586 vs 594 ms, inside noise). Treat that comment as an
untested hypothesis, not a plan. **The ~490 ms was reviewed and accepted as-is
by the owner 2026-08-10** — `list_folders` is called once before a folder-scoped
search rather than per search, so this is not a live follow-up. Reopen it only
if that call pattern changes.

**The argument for un-parking is stronger than the old "tens of ms" claim.**
Browse is ~200 ms warm and was 2.1 s cold on first touch, on the author's own
corpus — not obviously below perception. And the scan reads most of a 4.5 GiB
table, so it competes for exactly the page cache that
[search-performance.md](contributing/search-performance.md) documents search
latency as depending on (~1.2 GB of chunk vectors resident, where the container's
`mem_limit` charges page cache to the cgroup). A cheap index that stops
full-scanning the widest table in the database is also cache hygiene for the
search path.

**Both costs were measured and accepted before it shipped**, and both are
recorded here rather than in a commit message so a later reader finds the price
next to the win:

- ~~**Decide about the folder-filter regression.**~~ **Accepted by the owner
  2026-08-10**: folder-filtered browse going 106 → 144 ms is worth unfiltered
  browse going 215 ms → sub-millisecond. Recorded so a later reader doesn't
  "discover" the regression and treat it as an oversight — it is a priced
  trade, and the price is in the table above.
- ~~**Cost the write side.**~~ **Measured 2026-08-10 — it is a non-issue.** Same
  app stack, 5,000-row workloads inside rolled-back transactions so the copy
  stayed clean:

  | write workload | no index | +index | delta |
  |---|---|---|---|
  | INSERT 5,000 new messages | 184 ms | 303 ms | +23.8 us/row |
  | UPDATE 5,000 reassigning `date_sent` (the Upsert branch) | 115 ms | 127 ms | +2.4 us/row |
  | UPDATE 5,000 with a genuinely changed `date_sent` | 170 ms | 180 ms | +2 us/row |
  | UPDATE 5,000 re-queue only (`embedded_at`, no `date_sent`) | 164 ms | 161 ms | none |

  Two results matter. **The bulk re-queue paths pay nothing** — `reocr`,
  `extract-attachments` and `rebuild-bodies` clear `embedded_at` / bump
  `embed_epoch` without assigning `date_sent`, and SQLite skips an index whose
  columns no `SET` clause touches, so the highest-volume `messages` writers are
  unaffected. And **against a real write the cost vanishes**: 300 fresh messages
  through `MessageRepository.Upsert` (own transaction each, FTS triggers, real
  connection open) ran 55.0 ms/msg without the index and 54.9 ms/msg with it —
  the 24 us of index maintenance is ~0.04% of a message write and does not rise
  above measurement noise.

  Storage is 2.25 MiB (577 pages, per `dbstat`) for 81,732 rows — 0.05% of the
  4.5 GiB file, and on this database the file did not grow at all because the
  index was absorbed by the existing freelist (2,095 → 1,518 free pages). Build
  cost 0.1-0.7 s depending on cache state.
- ~~**Write down the silent-regression invariant.**~~ **Done** — it is in
  CLAUDE.md's schema invariants, and it is enforced rather than merely described:
  the clause lives once as `MessageRepository.BrowseOrderBy` and
  `SchemaMigratorTests.The_date_sort_index_resolves_the_browse_ordering_with_no_sort_step`
  asserts the query PLAN for that const. Both failure shapes were verified by
  mutation — dropping `DESC` from the index, and dropping the `IS NULL` key from
  the clause — and each fails that test with a message naming the cause.

**Shipped as v12**, and the DDL is the one measured above — note the `DESC`,
which is the whole difference between a 200x win and a 43x regression:

```sql
CREATE INDEX idx_messages_date_sort
    ON messages(date_sent IS NULL, datetime(date_sent) DESC);
```

Per CLAUDE.md's migration rule, both carriers moved (`LatestSchemaVersion` and
the `schema_version` literal in `001_initial.sql`) and the index is declared in
`001_initial.sql` too, so the fresh-install and migrated paths converge — the
v1-forward walk test asserts the index exists at the end, because a divergence
there would leave migrated databases full-scanning while new installs don't.

**One caveat on the measurement, unresolved on purpose:** every number here was
taken on a schema **v8** copy (the frozen dev corpus) while main is v12. v9-v12
add no indexes on `messages` and touch none of these queries' columns, so the
plans transfer — but nothing has re-measured this against a v12 database with
real data, and the only honest confirmation is doing so on the next corpus
refresh.

## Still open (small)

Carried forward from the original design doc — none are committed work, all gated on a problem actually being observed:

- ~~**Date-ordering index.**~~ Shipped as schema v12 — measured, and the
  rejected alternatives are recorded in
  [its own section above](#date-ordering-index-for-datetimedate_sent).
- **Thread reconstruction.** Today's `In-Reply-To` / `References` heuristic is acceptable; revisit if mismatches with Fastmail's JMAP threading become a usability issue.
- **JMAP-specific metadata.** IMAP flags are available via mbsync, but JMAP-only fields (masked email, server-side labels) would require a separate JMAP path. Not currently planned.
- **WAL checkpointing strategy.** No periodic auto-checkpoint configured beyond SQLite's default (every 1000 frames). For one-off cleanup after a bulk embed, `mailvec checkpoint` runs `PRAGMA wal_checkpoint(TRUNCATE)`. Worth measuring `-wal` file growth on a long-running install before deciding whether automatic periodic checkpoints are needed.

## Out of scope entirely

Sending mail, modifying server-side state (marking read, moving, deleting), multi-account support, calendar/contacts/files (even though Fastmail offers these via CalDAV/CardDAV/WebDAV — this project is mail-only), a web UI, and real-time push notifications (mbsync is timer-driven, not IDLE/JMAP push).

(OCR for image-only PDFs and images was originally out of scope; it now ships in the embedder via a local Ollama vision model — see [contributing/attachment-ocr.md](contributing/attachment-ocr.md).)
