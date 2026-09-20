# Parser isolation — status and handoff

**As of:** 2026-09-19, branch `parser-isolation-phase0`, fifteen commits ahead of `main`. **Phases
0–4 are complete and smoke-tested in Docker** (results below, dated); the branch also carries the
**non-root containers** change (`210097b`) and four rounds of independent review fixes
([review 1–3](attachment-parser-isolation-review.md), [review 4](attachment-parser-isolation-review-2026-09-19.md)).
Test state: **1,402 passing, 0 failing** across six projects. **PR #35 is open against `main`;
not merged, not released.**

## Next stage — what the next agent does

**The review is closed** — all six findings fixed on the branch (per-finding status:
[attachment-parser-isolation-review.md](attachment-parser-isolation-review.md)). Finding 1
(the parser could call mcp back over the shared network) is closed by pinning the `parse`
subnet and refusing it at the mcp origin (`Mcp:DeniedNetworks`); `Mcp:Access` is the
documented stronger layer for tunnel deployments, which the homelab is. **Deploy note:** the
subnet pin makes the first `up -d` recreate the `parse` network — seconds of parse outage the
callers ride out, and the return-path check to run afterwards is in `docs/deploy-docker.md`
"The parse service".

In order:

1. **Open a PR from `parser-isolation-phase0` to `main` and merge it.** Nine commits, all
   reviewed against `dotnet test Mailvec.slnx` and the Docker smoke below. Nothing on the
   branch is a schema migration or an MCP tool-surface change (the two viewer tools changed
   message text only; `McpSurfaceTests` is untouched), so a squash or a merge are both fine.
   CI must be green before anything else — the frozen-corpus / release-approval block check
   and the version-lockstep check both run there.
2. **Propose the release and wait.** `ops/release.sh --patch` is the right part; do **not** run
   it, bump `<Version>`, or push a `v*` tag unless the owner says so in that turn
   (`CLAUDE.md` → Releases). The tag push is what publishes the GHCR images the homelab pins.
3. **Deploy to the homelab — and this deploy is NOT a plain pull + `up -d`.** The branch also
   moves every service to uid 10001 (`210097b`), so the stack **refuses to start** until the
   mounts are chowned. Do, in this order, from
   [`docs/deploy-docker.md`](../deploy-docker.md):
   1. "Moving to non-root": `docker compose down`, then
      `sudo chown -R 10001:10001 data logs mail mbsyncrc secrets/*`, and check with
      `sudo ls -ln`. A missed path is a loud refusal naming the chown, looping under
      `restart: unless-stopped` until fixed — downtime, not corruption.
   2. "The parse service": `compose.yml` already declares the `parse` service and network and
      the image defaults to `Parser__Mode=remote`, so the pull + `up -d` that follows creates
      the service and re-attaches the three callers.
   3. Verify: `docker compose ps` (all running, none restarting),
      `docker compose exec mcp curl -s http://parse:3400/up`,
      `docker compose exec mcp mailvec doctor` (the `Parser` line), and
      `sudo ls -ln logs/*` showing fresh log files owned by 10001 (Serilog's failure is
      silent, so check). Expect `parse` to show recent start times; that is its design.
   4. **Watch the first few scans and the OCR pass** in the indexer and embedder logs for
      `ParserUnavailable` / `ParserCrashed` — the amd64 VM is the first place the real corpus
      meets the timeouts (open questions 1–2 below).

   The host-side chown on the VM is the one step nothing on the branch could validate from a
   Mac; everything else in "Moving to non-root" was reproduced on named volumes.
4. **Optional, worth doing:** script the Docker smoke below under `ops/` so the next change to
   the parser path has a repeatable end-to-end check. The procedure is written out; it is a
   shell script waiting to happen.

Things a new agent must not do: run `ops/install.sh` or any agent on the author's Mac (frozen
corpus — top of `CLAUDE.md`); read `archive.sqlite` from the macOS host while a container has it
(see "Things learned the hard way"); cut a release unasked. Merging and any version bump are the owner's call
(see `CLAUDE.md` → Releases: a release needs an explicit ask in that turn; this is a `--patch`,
there is no schema migration and no MCP tool-surface change).

The design, the measurements and the rationale live in
[`attachment-parser-isolation.md`](attachment-parser-isolation.md)
(with [`…-phase0.md`](attachment-parser-isolation-phase0.md) and the
[diagram](../security-boundaries.svg), which now lives beside `security.md` as the as-built
picture). This file is the operational summary
for whoever picks the work up: what is on the branch, how to prove it, and exactly what phase 3
has to change and why.

## The nine commits

| Commit | Phase | What it did |
| --- | --- | --- |
| `1d5f909` | 0 | The proposal, the diagram, `tools/Mailvec.ParserBench` and the measured results. |
| `3c49336` | 1 | `IMailParser` seam. `Mailvec.Parsing.Contracts` (interface + plain-data records, no packages) and `Mailvec.Parsing` (every parser; absorbed `Mailvec.Pdf`; never references Core). Core dropped MimeKit/AngleSharp/PdfPig/OpenXml. All four hosts resolve the parser through `ParserRegistration.AddMailvecParser`. |
| `a99da58` | 2 | The `parse` container. `Mailvec.Parse` host, `RemoteParser` client, `ParserWire` (routes + JSON compiled into both ends), Dockerfile strip-and-assert, compose service + internal network, `/health` `parser` section, `mailvec doctor` Parser check. |
| `2b3c1c7` | — | This handoff document. |
| `3092d8c` | 3 | Every caller branches on `ParseException.Kind`: `Unavailable` aborts and counts nothing (OCR batch, scanner walk with `ScanResult.Incomplete` and no reconciliation, CLI runs with exit 1, MCP viewers answer "retry"); `Crashed` is a strike (OCR pass with parser health evidence; scanner per `(path, mtime, size)` with `Parser:MaxCrashesPerFile` then a metadata-only parse and attachments at `failed`); `DocumentRejected` retires as before. 18 tests. |
| `2f8ea24` | 4 | `docs/security.md` acceptance rewritten around "one data-less process parses attacker bytes" with the residual stated; `docs/monitoring-uptime-kuma.md` on why `parse` is not on `/up`. |
| `0661502` | — | This document updated with the Docker smoke and the next stage. |
| `210097b` | — | **Non-root containers** (not part of the isolation proposal, but the `parse` service was its proof): mcp, indexer, embedder, mbsync as `MAILVEC_UID` (10001); `HOME=/tmp`; mbsync config at `/etc/mbsyncrc`; both entrypoints check every mounted path and refuse with the chown to run. Validated on named volumes (real ownership) against data/logs/mail/secrets populated by the old root-running image. **Changes the deploy procedure — see step 3 above.** |
| (this) | — | This document updated for the non-root deploy step. |

Test state at `210097b`: **1,342 passing, 0 failing** across six projects
(`dotnet test Mailvec.slnx`). The Docker image was built locally (linux/arm64) and smoke-tested
as described under "How to verify the branch".

## What exists now (the map)

```
src/Mailvec.Parsing.Contracts/   IMailParser, ParsedMessage, ExtractionResult + ExtractionStatus,
                                 DecodedPart, PartInfo, PdfRender, NormalizedImage,
                                 AttachmentTooLargeException, AttachmentNaming, ParserWire,
                                 ParseException + ParseFailureKind {Unavailable, DocumentRejected, Crashed}
src/Mailvec.Parsing/             InProcessParser, MimePartDecoder, MessageParser, AttachmentTextExtractor,
                                 MessageParts, HtmlToText (+helpers), PdfRenderer, ImageRenderer
src/Mailvec.Parse/               ParseHost (composition), ParseEndpoints (one route per op, timeout,
                                 error mapping), RequestBudget (exit after N / after a timeout),
                                 ParseHostOptions (Parser:* keys the HOST reads)
src/Mailvec.Core/Parsing/        ParserRegistration (the ONE place a process's parser is chosen),
                                 RemoteParser (HTTP client, failure classification)
src/Mailvec.Core/Options/        ParserOptions (Parser:Mode, :Endpoint, :RequestTimeoutSeconds — the CALLER side)
src/Mailvec.Core/Attachments/    MaildirAttachmentReader (path + containment + ReadEml only),
                                 AttachmentExtractor (Core-side facade: reader + parser)
tests/Mailvec.Parse.Tests/       real Kestrel on port 0: contract (byte-for-byte vs in-process),
                                 error mapping, timeout-then-exit, request budget
tools/Mailvec.ParserBench/       phase-0 harness (not in the solution)
```

Moved types **kept their original namespaces** (`Mailvec.Core.Parsing`, `Mailvec.Core.Attachments`,
`Mailvec.Pdf`) even though they now live in other assemblies. Deliberate: the move is a project
boundary, not a rename. A rename can be done later as a pure mechanical change.

Rules that must hold (each has a test):

- Core references only the Contracts, never a parser package or `Mailvec.Parsing`; Parsing never
  references Core. `ParserBoundaryTests` asserts the assembly graph.
- Which parser a process gets is decided only in `ParserRegistration`; `Parser:Mode=remote` without
  an absolute http(s) endpoint is fatal, never a fallback. `ParserRegistrationTests`.
- The wire is invisible: every operation answers identically remote and in-process.
  `Mailvec.Parse.Tests/ContractTests`.
- `RemoteParser` rebuilds `ArgumentOutOfRangeException` and `AttachmentTooLargeException` (the
  types callers branch on) and classifies everything else; unclassified is `Crashed`, never
  `DocumentRejected`. `ErrorMappingTests`.
- The image strips every parser library from `/app/{indexer,embedder,mcp,cli}` with a sentinel
  `test -e` before each delete (a renamed assembly fails the build). Enforced by the Dockerfile.

## How to verify the branch

```sh
git checkout parser-isolation-phase0
dotnet build Mailvec.slnx
dotnet test Mailvec.slnx                       # expect 1,342 passing
docker build -t mailvec:phase3 --target runtime .   # ~4 min; the strip assertions run here
docker run --rm mailvec:phase3 sh -c 'ls /app/indexer | grep -c MimeKit; ls /app/parse/libpdfium.so'   # 0, then the path
```

### The Docker smoke (run 2026-09-15 on Docker Desktop 29.8, linux/arm64 — observed, not scripted)

Everything below runs from a scratch directory with plain `docker run`; nothing touches compose
or the frozen corpus. **Query the database only through a container** (the `cli` function) —
never from the host; see "Things learned the hard way".

```sh
SM=$(mktemp -d)/smoke; mkdir -p "$SM"/mail/INBOX/{cur,new,tmp} "$SM"/data "$SM"/logs
chmod a+rwX "$SM"/data "$SM"/logs            # the image runs the CLI as the container's user
# write one multipart .eml with a text attachment into $SM/mail/INBOX/cur/1.host:2,S (Message-ID <one@smoke>)
docker network create mv-smoke
cli() { docker run --rm --network mv-smoke -v "$SM/data:/data" -v "$SM/mail:/mail:ro" -v "$SM/logs:/logs" \
         mailvec:phase3 dotnet /app/cli/Mailvec.Cli.dll "$@"; }
docker run -d --name mv-parse --network mv-smoke --network-alias parse --user 65534:65534 -e HOME=/tmp \
         mailvec:phase3 dotnet /app/parse/Mailvec.Parse.dll
docker run -d --name mv-indexer --network mv-smoke -v "$SM/data:/data" -v "$SM/mail:/mail:ro" -v "$SM/logs:/logs" \
         -e Indexer__ScanIntervalSeconds=10 mailvec:phase3 dotnet /app/indexer/Mailvec.Indexer.dll
docker run -d --name mv-mcp --network mv-smoke -v "$SM/data:/data" -v "$SM/mail:/mail:ro" -v "$SM/logs:/logs" mailvec:phase3
```

| Step | Observed 2026-09-15 |
| --- | --- |
| Initial scan | indexer log `POST http://parse:3400/v1/message` → 200; `seen=1 upserted=1`; `cli status` → 1 message. The indexer directory has no parser library. |
| `docker stop mv-parse`, drop a second `.eml` | every scan (watcher pulse + 10 s timer) logs `the parse service is unavailable; scan abandoned after 1 file(s) … Nothing reconciled`; `cli status` stays `1 total, 0 deleted`; no sync_state marker written. |
| Drop a third `.eml`, `docker start mv-parse` | next scan `seen=3 upserted=2 unchanged=1 softDeleted=0`; `cli status` → `3 total, 0 deleted`; `cli get '<two@smoke>'` shows the attachment `[done]`. |
| `/health` from inside `mv-mcp` (`docker exec mv-mcp curl -s 127.0.0.1:3333/health`) | `parser: {mode: remote, endpoint: http://parse:3400, reachable: true}`, then `reachable: false` with parse stopped. `status` is `degraded` throughout because there is no Ollama — expected. |
| `cli doctor` with parse stopped | `⚠ Parser  remote parse service … did not answer /up within 2s — new mail is not being indexed, OCR is paused …` |
| Return path (2026-09-18, `mailvec:netguard`): mcp on a `--internal --subnet 172.31.255.0/24` network plus a default network, `Mcp__DeniedNetworks__0=172.31.255.0/24`; a container on the parse network curls `http://mcp:3333` with `Host: mcp` | `/up` → **403**, `tools/list` POST → **403**; the same probe from the default network → 503 (served; no Ollama), loopback → 503. Startup log: `Refusing every request from 172.31.255.0/24`. |
| Churn: parse restarted with `-e Parser__MaxRequestsBeforeExit=1 --restart unless-stopped`, five new `.eml`s | each scan indexes one message, then `scan abandoned after N file(s) (upserted=1 …)`; after five parse restarts `cli status` → `8 total, 0 deleted`. |

**Not covered by the smoke, and why:** the `Crashed` classification and the scanner's
three-strikes degrade. The only known parser-hanging fixture (`e-sh-20k.pdf`, phase 0) hangs
PDFium, which the indexer never calls, and reaching the OCR pass's render step needs a vision
model. Both are unit-tested (`MaildirScannerTests.A_file_that_keeps_crashing_the_parser_…`,
`AttachmentOcrServiceTests.Parser_crash_…`). The first real exercise will be the homelab VM.

Teardown: `docker rm -f mv-parse mv-indexer mv-mcp; docker network rm mv-smoke`.

**Do not** run `ops/install.sh` or start agents on the author's Mac — see the frozen-corpus
block at the top of `CLAUDE.md`. Everything above runs from the working tree and in Docker.

## Phase 3 — done; what it was and why it was not optional

> Landed 2026-09-15. The sections below are kept as the record of the plan; every item in them
> is implemented and each named test exists. Deviations from the plan, all deliberate: the OCR pass
> shares its strike ledger between vision and parser failures (each settled with its own health
> evidence); `ScanResult` gained an `Incomplete` flag rather than a new `IngestOutcome` being
> surfaced; the CLI backfills stop with exit 1 and a `STOPPED` line on an outage; the scanner's
> degraded parse stamps every attachment `failed` (the proposal's open question 5, resolved as
> written there). `docs/deploy-docker.md`'s "While it is down" bullet already described this
> behaviour — it was written in phase 2 ahead of the code.

Phase 2 made every parse call in the container cross to the `parse` service. What it did **not**
do is teach the callers that a parse can now fail for reasons that have nothing to do with the
document. Today every caller catches `Exception` around its parser call and treats the failure as
a property of the document. In-process that was right. In remote mode it is wrong in one specific
way that matters:

> **While the parse service is down or restarting (which it does on purpose, after a timeout or
> its request budget), the embedder's OCR pass and the two CLI backfills will stamp documents
> `failed` — permanently, since nothing re-selects a failed row.**

This is the single most important thing phase 3 fixes. Until it lands, the remote mode is
correct only while the parse service is up, which is most of the time but not all of it.

The classification is already in place on the client side (`ParseFailureKind`). Phase 3 is the
four callers branching on it. In priority order:

### 1. The OCR pass — `src/Mailvec.Embedder/Services/AttachmentOcrService.cs`

Two call sites, both currently `catch (Exception) → MarkAttachmentOcrFailed(c, PreProvider)`:

- PDF pass: `parser.RenderPdfPages(...)` at line 620, catch at ~623.
- Image pass: `parser.NormalizeImage(...)` at line 839, catch just below it.

Required mapping, mirroring the vision taxonomy the class already has
(`ClassifyVisionFailure`, line 402; `SettleVisionFailures`, line 476):

| `ParseException.Kind` | Action |
| --- | --- |
| `Unavailable` | Abort the batch, count nothing — the `Backpressure` branch. The next poll is the backoff. |
| `Crashed` | A strike against the candidate's `OcrFailureKey`, settled by `SettleVisionFailures` like a vision `Transient`. The "did anything succeed this cycle" health probe needs a parser-side equivalent: `IMailParser.ProbeAsync` exists for this. |
| `DocumentRejected` | Today's behaviour: `MarkAttachmentOcrFailed(c, PreProvider)`. |
| any other exception (`ArgumentOutOfRangeException`, `AttachmentTooLargeException`, MimeKit format errors in-process) | Today's behaviour: retire, `PreProvider`. |

Keep the rule from `CLAUDE.md`: **only a verdict the provider produced may name the engine**; a
retirement caused by the parser is `PreProvider`, and a retirement must never be caused by
`Unavailable` or `Crashed`. Add tests next to the vision ones in `AttachmentOcrServiceTests`
(48 tests there today): `Parser_unavailable_never_retires_a_document`,
`Parser_crash_counts_a_strike_only_when_the_parser_is_otherwise_healthy`,
`Document_rejected_by_the_parser_retires_with_pipeline_provenance`. The fake to inject is any
`IMailParser` whose `RenderPdfPages` throws the chosen `ParseException`; the existing tests
construct the service by hand with `new InProcessParser(extractor: null)`.

### 2. The indexer's scanner — `src/Mailvec.Indexer/Services/MaildirScanner.cs`

`parser.ParseMessage(File.ReadAllBytes(filePath), extractAttachmentText: true)` at line 496; the
catch at line 523 writes the existing "retry me" marker (`content_hash` NULL, identity NULL) and
returns `IngestOutcome.Failed`. That is **safe** for any kind of failure: the file is re-parsed on
the next scan, and `observedPaths` records the path *before* ingest (line ~170), so a failed
parse never reads as a deletion. Two refinements, neither a correctness fix:

- **`Unavailable` should abort the walk early instead of writing one marker per file.** During a
  bulk ingest with the parse service down, the current code would write a marker for every new
  file and log a warning per file. Abort on the first `Unavailable`, log once, and let the next
  timer tick retry. **If you abort mid-walk you must skip deletion reconciliation and the
  rename-repair pass** — `observedPaths` is partial and a spurious soft-delete is sticky
  (`CLAUDE.md` → "Scans must never overlap"). The scanner already has the hook: an ingest
  returning `IngestOutcome.FailedAndUnrefreshed` increments `unrefreshed`, and the
  `unrefreshed > 0 || enumerationFailures > 0` veto at line 203 returns before reconciliation
  (the comment at line 551 explains why the outcome still exists). **Not** the
  `seen == 0 && stale.Count > 0` guard at line 233 — that one protects against an empty or
  vanished Maildir root and is unrelated. Reuse the veto's shape (a counter the walk bumps and the
  post-walk check reads) or add an explicit "incomplete" outcome; add
  `MaildirScannerTests.A_parser_outage_mid_scan_deletes_nothing` (30 tests there; several already
  drive `ScanAll` with staged files and assert on soft-deletes).
- **`Crashed` on the same file repeatedly** is the 958-byte shading PDF case: today it would be
  re-parsed every scan and time out the parse service every time (60 s, then a restart). The
  proposal's answer is an in-memory strike count keyed on the fast-path file identity
  `(path, mtime, size)`, and after `Parser:MaxCrashesPerFile` (3) a re-request with
  `extractAttachmentText: false` so the message is indexed with its attachments at `failed`.
  That needs `MessageParser` to stamp attachments `failed` when asked not to extract — today
  metadata-only parsing leaves `ExtractionStatus` null, which the default `extract-attachments`
  predicate would later pick up and re-parse. Decide that explicitly.

### 3. The CLI backfills — `src/Mailvec.Cli/Commands/`

- `ExtractAttachmentsCommand.cs` line 431: `parser.ExtractAttachmentText(eml, att.PartIndex)`,
  with `catch (Exception) → ExtractionStatus.Failed`. An `Unavailable`/`Crashed` here must
  **skip the message and report it** (the run summary already has `MISSING` and `REFUSED`
  counters; add a `PARSER n` or fold into stale), never stamp. `ExtractAttachmentsCommandTests`
  builds its own `ServiceCollection` (see `BuildProvider`) and registers
  `IMailParser` over the extractor — inject a throwing fake the same way.
- `BackfillInlineImagesCommand.cs` lines 153/169: same treatment. `ParseMessage` failing with
  `Unavailable` currently counts as `parseFailures` and skips the message, which is fine;
  `ExtractAttachmentText` failing is currently unguarded (it would abort the run) — decide.
- `RebuildBodiesCommand.cs` line 97: `BodyTextFromHtml` — already inside a per-message
  try/catch that counts errors and continues; an outage becomes N errors. Acceptable, or abort
  on the first `Unavailable`.

### 4. The MCP tools — `src/Mailvec.Mcp/Tools/`

`GetAttachmentPageImageTool.cs` (catch at 117) and `ViewAttachmentTool.cs` (the
`NormalizeImage` call at 189 is unguarded beyond the outer flow; note it goes through the Core-side
`AttachmentExtractor` facade, not `IMailParser` directly, so the `ParseException` arrives via that
call). Both give the generic "could
not render / summary only" answer today. Add one branch: `ParseException` with
`Unavailable` → *"Attachment parsing is temporarily unavailable; retry in a moment, or use
get_attachment_text."* This is a message-text change only; it is **not** a tool-surface change
(no names, no parameters, no response fields), so `McpSurfaceTests` is unaffected and it stays
`--patch`. Keep the native-parser message out of the response, as the existing comment explains.

### Things phase 3 must not do

- Do not probe the parser before every call to pre-empt `Unavailable`. The parse answers that
  itself; a probe per call couples availability the way a network check in every search would.
  `ProbeAsync` is for health, doctor, and the OCR pass's per-cycle settlement only.
- Do not add a `ParseException` kind that retires a document on its own. `DocumentRejected` is
  the only terminal kind and it must keep meaning "the parser opened it and said no".
- Do not touch the wire (`ParserWire`) — both ends compile against it, and phase 2's contract
  tests are the proof it did not drift. Adding a field to a record is fine; renaming is not.

## Things learned the hard way (so you don't)

- **Never open the bind-mounted `archive.sqlite` from the macOS host while a container has it.**
  Docker Desktop does not share the WAL index (`-shm`) across the VM boundary; a host-side
  python/`sqlite3` read sees a stale view and its close can checkpoint that view over the
  containers' frames. Observed 2026-09-15: two freshly indexed messages vanished and the WAL
  truncated to 0 bytes, and it looked like a scanner bug for one round. Query via the CLI in a
  container. Now in `docs/deploy-docker.md`.

- **xunit 2.9 `IAsyncLifetime` returns `Task`**, not `ValueTask`.
- **`WebApplication.StopAsync` takes a `CancellationToken`**, not a `TimeSpan`.
- **Don't use `TestServer` for the parse host tests.** `RemoteParser` uses the synchronous
  `HttpClient.Send`, which the in-memory handler doesn't support; the fixture starts real
  Kestrel on `http://127.0.0.1:0` and reads the bound address through `ParseHost.BoundAddress`.
- **Inside `namespace Mailvec.Core.Health`, `Parsing.Contracts.X` resolves to
  `Mailvec.Core.Parsing.Contracts`** (doesn't exist). Fully qualify `Mailvec.Parsing.Contracts`.
- **`Uri.TryCreate("parse:3400", Absolute)` succeeds** (scheme `parse`). The endpoint check
  requires an http(s) scheme for that reason.
- **Docker DNS resolves container names and network aliases, not image env defaults.** If you
  smoke-test by hand, start the parse container with `--network-alias parse` or the callers'
  `Parser__Endpoint=http://parse:3400` won't resolve.
- **The image build takes ~4 minutes** and must be re-run after any code change before a smoke
  test means anything — the first smoke in this work ran against a stale image.
- **`AttachmentTextExtractor` now takes a plain `long attachmentMaxBytes`**, not
  `IOptions<IndexerOptions>`, so it cannot be registered with a bare `AddSingleton<>()`; the CLI
  tests register it with a factory and put `IMailParser` over it.
- **The in-process parser has a test-shaped constructor** `new InProcessParser(extractor: null)`
  (no attachment-text extraction), mirroring the old `new MessageParser()`.

## Phase 4 — done

The proposal's phase 4 named five doc items. Three had already landed with phases 1 and 2 (the
`CHANGELOG.md` entries, the `docs/deploy-docker.md` "The parse service" section, the `CLAUDE.md`
invariants). The remaining two are done:

- `docs/security.md` — the hardening section opens with "exactly one process parses
  attacker-chosen bytes, and it holds nothing"; the acceptance bullet states the residual
  (attacker-chosen *output* for in-flight requests, nothing else) and gains a condition for
  `parse` acquiring a volume, secret or route; `parse` is recorded as the first non-root service
  and the indexer's row no longer says it parses; gVisor is listed as deferred with its trigger;
  the loopback-install section says the parsers run in-process there.
- `docs/monitoring-uptime-kuma.md` — "The `parse` service is not on `/up`, by design": its own
  compose healthcheck, why routine restarts must not page, a Docker Container monitor with
  restart-tolerant retries if paging is wanted, and a verify block.

**Release: propose `--patch` and wait.** No schema migration, no MCP tool-surface change (the two
viewer tools changed message text only; `McpSurfaceTests` is untouched). The version bump and the
tag are the owner's call — see `CLAUDE.md` → Releases.

Deferred with triggers (in the proposal): gVisor on `parse`; non-root UIDs for the other
services; persisting the crash-strike counter.

## Open questions carried forward

1. Bulk-ingest throughput over HTTP (82k messages ≈ 82k calls) — expected to be lost in the
   noise against parse time, but unmeasured on the amd64 VM. Only the initial bulk ingest and a
   `reindex` pay it; the steady state is the mtime fast path, which never calls the parser.
   Review 4's F5 adds the multiplier: the backfills ship the whole `.eml` once per candidate
   part (`ExtractAttachmentText(eml, partIndex)`), and the viewer tools twice per call. If the
   measurement says it matters, a batch part-text route (one POST, a list of part indexes) is
   the fix; correctness is unaffected either way.
2. `MaxRequestsBeforeExit` default (500) and `RequestTimeoutSeconds` (60) — both guesses with a
   measured floor; re-measure on the VM before lowering. The smoke's churn row above is what a
   too-low budget looks like in the logs: one message per scan, an abandon per restart.
3. ~~The three-strikes fallback~~ **Resolved in phase 3:** attachments go to `failed` (not NULL),
   so the default `extract-attachments` predicate never walks back into the crash;
   `--reextract-*` revisits them once the parser is fixed. Documented in `CLAUDE.md` and
   `docs/deploy-docker.md`.
4. The end-to-end smoke is a procedure, not a script (above). Worth an `ops/smoke-parse.sh`.
