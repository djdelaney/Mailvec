# Parser isolation branch review — 2026-09-19 (fourth pass)

- Branch: `parser-isolation-phase0` at `1e7fa34` (14 commits ahead of `main`)
- Scope: full diff `ccfc806..HEAD`, all four caller classes, the `parse` host, compose/Dockerfile
- Prior reviews: [`attachment-parser-isolation-review.md`](attachment-parser-isolation-review.md) — all six findings there are verifiably closed; nothing below is a re-report.

> **Disposition (2026-09-19, after this pass).** F1–F4 and F6 fixed on the branch; F5 deferred to
> the status doc's open question 1 (measure first); the accepted asymmetry is now stated in
> `ParseEndpoints`' catch-all comment. Per-finding notes below. Test state after: 1,404 passing.

## Validation

- `dotnet build` clean — 0 warnings under `TreatWarningsAsErrors`.
- `dotnet test` — **1,396 passing, 0 failing** across six projects (status doc still says 1,342; see finding 6).
- F1 reproduced empirically with a throwaway harness (real `ParseHost` + `RemoteParser` on loopback, in `/tmp`, removed afterwards). No agents, no install scripts, frozen corpus untouched.

## Overall

The architecture is clean: Contracts has zero dependencies, `Mailvec.Parsing` never references Core, the `parse` host references nothing but `Mailvec.Parsing`, and `ParserBoundaryTests` pin the graph. The failure taxonomy (`Unavailable` / `Crashed` / `DocumentRejected`; unclassified → `Crashed`, never `DocumentRejected`) is the right shape and is applied consistently across the scanner, OCR pass, CLI backfills and MCP tools. The reconciliation veto on an abandoned scan, the redirect/proxy refusal in `ParserHttp`, the allocation-free `ResponseShape` pre-scan, and `NetworkGuard` reading resolved options are all correct.

Finding 1 is the only one I would call merge-blocking: a healthy, ordinary document gets a permanent wrong verdict, and the test that appears to cover the path proves the opposite of what production does.

---

## F1. [P1] Over-cap messages are misclassified `Crashed`, never `DocumentRejected` — reproduced

> **Fixed, both ways.** `Parser:MaxRequestBodyBytes` mirrors the host's cap on the caller (same 48 MB default) and `RemoteParser.PostEml` refuses an over-cap message as `DocumentRejected` before a byte is sent; and every send carries `Expect: 100-continue`, so a drifted mirror still gets the host's 413 at the headers rather than a mid-body reset. Tests: `ErrorMappingTests.A_message_over_the_request_cap_is_DocumentRejected_without_being_sent` (pointed at a closed port, so a network call would read `Unavailable`) and `A_message_over_the_host_cap_but_under_the_mirror_is_still_DocumentRejected` — the reviewer's 8 MB against a 4 MB host cap, with a deliberately too-generous mirror, which is the case only `Expect` fixes. The old 16 KB test is gone.

**Location:** `src/Mailvec.Core/Parsing/RemoteParser.cs` (`MapError` case 413, `Classify`); `src/Mailvec.Parse/ParseEndpoints.cs` (`RunWithEml`); cap at `ParseHostOptions.MaxRequestBodyBytes` (48 MB).

The `request_too_large` → `DocumentRejected` mapping only fires if the client actually receives the 413. Kestrel answers 413 as soon as the request body exceeds `MaxRequestBodySize`, while the upload is still in flight — and `SocketsHttpHandler` fails the send instead of reading the early response. Reproduced against a real host with a 4 MB cap:

```
body 8 MB  → ParseException kind=Crashed  ("failed mid-request: Error while copying content to a stream")
body 40 MB → ParseException kind=Crashed  (same)
```

`ErrorMappingTests.A_message_over_the_request_cap_is_DocumentRejected` (16 KB against a 2 KB cap) passes precisely because a small body lands in the socket buffer before the 413 arrives. In production the cap is 48 MB, so *every* violating message is ≥48 MB and takes the `Crashed` path. The `DocumentRejected` mapping is dead code in practice.

**Consequence** for a large-but-legitimate message (two 20 MB attachments; one ~36 MB attachment):

- **Indexer** (`MaildirScanner`): a `Crashed` strike per scan — each attempt uploading ~48 MB — then after `Parser:MaxCrashesPerFile` (3) the message is degraded-indexed with **all attachments stamped `failed`**. The poison-document machinery triggered by a message that is merely big, and the log line ("crashed the parser") points the operator in the wrong direction.
- **OCR pass** (`AttachmentOcrService`): a strike against an innocent document, counted with health evidence (the probe passes — the service is fine), retiring it `PreProvider` after the threshold. Permanent: nothing re-selects a failed row.

**Suggested fix (either or both):**

1. `request.Headers.ExpectContinue = true` in `RemoteParser.PostEml` — Kestrel rejects at the headers, so the 413 arrives before a byte of body is sent. Fixes both the classification and the wasted upload.
2. A client-side pre-flight: `RemoteParser` knows `eml.Length`; a `Parser:MaxRequestBodyBytes` mirror in `ParserOptions` turns an over-cap send into `DocumentRejected` without a network call.

**Regression coverage:** the test needs a body larger than the cap *and* larger than a socket buffer (a few MB), not 16 KB — otherwise it keeps proving the send-buffer case.

## F2. [P2] A client disconnect recycles the parse host

> **Fixed.** The timeout wait is no longer tied to `RequestAborted`: a caller that disconnects leaves the parse to finish within its own timeout (slot released, result discarded, counted toward the budget), and only a genuine overrun exits — the log line says when the caller had already gone. `ErrorMappingTests.A_caller_that_disconnects_mid_parse_does_not_take_the_host_down`, verified by mutation (re-tying the wait to the abort token fails it).

**Location:** `src/Mailvec.Parse/ParseEndpoints.cs` (`Execute`): `Task.WhenAny(task, Task.Delay(options.RequestTimeout, ctx.RequestAborted))`.

`RequestAborted` completing takes the same branch as a timeout: 504 (to a dead connection) **and process exit**. So `docker compose stop indexer` mid-parse, or a cancelled MCP tool call, takes the parse service down for every other caller. Impact is bounded (restart is seconds; other callers ride the gap out as `Unavailable`), and the exit is arguably defensible — the orphaned parse thread can only be reclaimed by ending the process — but then the log line ("exceeded the {Timeout}s request timeout") misdescribes half the cases it fires in, which matters when someone is debugging why `parse` keeps restarting.

**Suggested fix:** split the cases (a client abort logs and exits for what it is), or document "caller disconnect = host exit" as deliberate.

## F3. [P2] No admission control on the parse host

> **Fixed.** `ParseGate` (a semaphore, `Parser:MaxConcurrentParses`, default 4, compose `MAILVEC_PARSER_MAX_CONCURRENT`): a request waits for a slot up to the request timeout and is answered 503 `busy` otherwise, which the client already classifies as `Unavailable` — wait and retry, never a strike. The slot is released when the parse finishes, even after its caller left. Tests: `ErrorMappingTests.Parses_beyond_the_concurrency_limit_wait_for_a_slot` / `A_parse_that_cannot_get_a_slot_within_the_timeout_is_Unavailable_not_a_strike`.

**Location:** `src/Mailvec.Parse/ParseEndpoints.cs` (`Task.Run(work)` per request); compose `mem_limit: 2g`, `pids_limit: 128`.

Kestrel accepts as many concurrent connections as arrive and every one starts an unbounded parse. A bulk-ingest scan + an OCR render batch + a `view_attachment` call can concurrently hold decoded PDFs and page JPEGs in the 2 GB container; an OOM kill mid-request hands every in-flight caller a `Crashed` strike against an innocent document. The strike-settlement probe mitigates this only while the container stays down — a service flapping under memory pressure passes `/up` between kills. `pids_limit` bounds threads, not memory per parse.

**Suggested fix:** a small concurrency semaphore (PDFium is effectively serialized already, per the proposal), or a stated acceptance in `docs/security.md`.

## F4. [P2] Three-strikes degrade doesn't cover a poison MIME structure

> **Fixed (in-memory; persistence stays on the proposal's deferred list).** After `MaxCrashesPerFile` crashes on the metadata-only parse as well — 2× the setting in total — the scanner adds the file identity to a bounded give-up set and never sends it again in this process: no parser call, no marker write, an error-level log naming the file. A restart or an edit to the file grants another round. `MaildirScannerTests.A_file_that_crashes_the_degraded_parse_too_is_given_up_on`.

**Location:** `src/Mailvec.Indexer/Services/MaildirScanner.cs` (degraded path: `parser.ParseMessage(eml, extractAttachmentText: false)`).

The degraded retry is still a full MimeKit parse. If what hangs the host is the MIME structure itself rather than attachment-text extraction, the metadata-only parse also 504s, `RecordParserCrash` counts past the max forever, and the file takes the parse service down every scan interval indefinitely. Narrow — MimeKit hangs are rarer than PDFium/PdfPig ones, and in-process this file was *worse* (it hung the indexer itself) — but the degrade as written only de-risks the extraction half.

**Suggested fix:** after N crashes on the metadata-only parse, a permanent-failure marker that never calls the parser again.

## F5. [P3] Whole-`.eml` re-upload per part on the backfill paths

> **Deferred, recorded.** Folded into the status doc's open question 1 (bulk throughput, unmeasured on the VM): a batch part-text route is the fix if the measurement says it matters. Correctness is unaffected.

**Location:** `ExtractAttachmentsCommand` / `BackfillInlineImagesCommand` (`parser.ExtractAttachmentText(eml, partIndex)` per candidate); `GetAttachmentPageImageTool` (Describe + RenderPdfPages); `ViewAttachmentTool` (DecodePart + NormalizeImage).

`ExtractAttachmentText(eml, partIndex)` and `DescribePart(eml, partIndex)` ship the whole message per call: `extract-attachments` sends N× the message for N candidate parts of the same `.eml` (pre-branch: one `MimeMessage.Load` per message); the page-image tool ships it twice per render; `view_attachment` up to twice. Not correctness, but this is exactly the bulk-backfill cost the status doc's open question 1 flags as unmeasured. A batch variant (one POST, a list of part indexes) collapses it if the measurement says it matters.

## F6. [P3] Minor drift

> **Fixed.** Status header re-counted; `IMailParser`'s comment no longer speaks of phase 2 in the future tense; the two `cref="Read"` pointers now say `ReadEml`.

- `docs/proposals/attachment-parser-isolation-status.md` header: "nine commits ahead of `main`" (now 14), "1,342 passing" (now 1,396).
- `IMailParser`'s doc comment still describes the remote client as future tense ("from phase 2 … a remote client").
- `MaildirAttachmentReader` doc comments still `cref="Read"`, a method that no longer exists (inert only because XML doc generation isn't enabled — a stale pointer for readers).

## Accepted asymmetry worth stating in a comment

> **Stated.** `ParseEndpoints.Execute`'s catch-all now carries the acknowledgement, with the reasoning (the in-process precedent, and no honest allowlist of "document fault" exception types across the parser libraries) and the remedy (`--reextract-*` / `reocr` once a parser bug is fixed).

The host's catch-all maps *every* parser exception to 422 `DocumentRejected` (`ParseEndpoints.Execute`), while the client carefully maps unclassified failures to `Crashed`. A parser-code bug (e.g. a `NullReferenceException` in new extraction code) therefore permanently retires a healthy document, where the same class of failure on the wire is deliberately never terminal. This matches the in-process precedent (any exception was a document fault), so it is defensible — but it slightly contradicts the enum's own comment that `DocumentRejected` means "opened the document and refused it", and it is the one place the "a bug must retry, not destroy" rule does not hold. A sentence in `ParseEndpoints`' comment acknowledging the trade would do.

---

*Method note: read-only review plus a local loopback reproduction of F1. No files in the repo were modified by the review; no Docker build or homelab deployment was performed.*
