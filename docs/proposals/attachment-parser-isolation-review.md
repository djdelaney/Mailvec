# Parser isolation branch review

Date: 2026-09-18  
Branch: `parser-isolation-phase0`  
Reviewed commit: `fa2ac36`  
Comparison: `origin/main...HEAD`

> **Status (2026-09-18, after the review).** Every finding was verified against the code and
> holds. Findings **2, 3, 4 and 6 are fixed** on the branch — `ParserHttp` (redirects and proxy
> off, `Parser:MaxResponseBytes` enforced by the client), `RemoteParser` classifying every
> protocol failure as `Crashed`, the inline-image counters restored — each with tests
> (`RogueServiceTests`, `ParserRegistrationTests`, `BackfillInlineImagesCommandTests`).
> Finding **5 is fixed** too (`RetryOnUnavailable`; it was broader than stated — every CLI
> backfill stopped at the parse host's request budget). Finding **1 is open** and tracked in
> the [status doc](attachment-parser-isolation-status.md) as pre-merge work: it needs a design
> decision (reject the parse subnet at the mcp origin, or enforce `Mcp:Access`).

## Assessment

The project split is clean: Core depends on plain parser contracts, parsing libraries live in a separate project, and callers select the implementation through one registration path. Keeping Maildir path resolution and containment checks in the caller is the right separation of responsibilities. The scanner's refusal to reconcile deletions after an interrupted walk also preserves an important correctness invariant.

**Fix the findings below before merging.** Two confirmed bypasses undermine the intended isolation boundary: the parser can reach the MCP origin, and it can redirect callers into forwarding mail elsewhere. Response buffering and incomplete failure classification also allow parser-side failures to affect privileged callers or permanently retire healthy documents.

## Findings

### 1. [P1] The parser can call MCP and read the archive

> **Open.** Valid against the residual `docs/security.md` states; one parse process serves every caller, so a document that arrives via the OCR pass also reaches mcp's tools. Not a regression (pre-split the parsers ran inside mcp), but the acceptance overclaims. Fix at the origin, plus a compose-level negative test.

**Location:** [compose.yml](compose.yml), line 209; related configuration at lines 220, 234 and 435.

MCP joins the `parse` network while `Mcp__Access__Enabled` defaults to `false`. An internal Docker network permits communication between its members; it does not restrict connections to caller → parser. MCP listens on all interfaces, and its Host guard is not authentication.

A compromised parser can therefore call `search_emails`, `get_email`, and the other MCP tools directly. This contradicts the security acceptance that compromise exposes only documents currently being parsed. Lack of a database mount does not prevent archive access through MCP.

**Validation:** Using the existing `mailvec:uid` image, a disposable container running as UID 65534 on an internal test network successfully called `list_folders` on an MCP container with an empty temporary archive. The response was HTTP 200. The request used an allowed `Host` header; compose additionally allowlists `mcp` explicitly. No real archive was mounted.

**Suggested fix:** Prevent parser-initiated connections to MCP, or require origin authentication that the parser cannot satisfy. Preserve the return traffic needed for caller-initiated parse requests. Add a deployment-level negative test proving that the parser can answer requests but cannot invoke MCP tools.

### 2. [P1] Automatic redirects let the parser export mail through callers

> **Fixed.** `ParserHttp.CreateHandler` (`AllowAutoRedirect = false`, `UseProxy = false`); a 3xx classifies as `Crashed`. `RogueServiceTests.A_redirect_is_refused_and_the_message_bytes_go_nowhere` (301/302/307/308; the sink receives zero requests), verified by mutation.

**Location:** [ParserRegistration.cs](src/Mailvec.Core/Parsing/ParserRegistration.cs), lines 38–46.

The named parser `HttpClient` retains automatic redirects. A compromised parser can return HTTP 307 or 308 with an external `Location`, causing the caller to resend the POST body—the entire `.eml`—to that destination. MCP and the embedder have network access that the parser itself lacks, so the redirect bypasses the intended egress restriction.

**Validation:** A temporary harness using `RemoteParser` and two local HTTP servers confirmed that a 307 response forwarded synthetic email bytes to the second server and returned a successful parse result.

**Suggested fix:** Configure the parser client's primary handler with `AllowAutoRedirect = false`. Classify redirects as protocol failures rather than document rejection. Add a test that verifies a second endpoint receives no request after a 307/308 response.

### 3. [P1] Parser responses need a practical limit before buffering

> **Fixed.** `Parser:MaxResponseBytes` (64 MB) → `HttpClient.MaxResponseContentBufferSize`, enforced while reading: a declared length over it is refused before the body, a chunked body is cut off at it. `RogueServiceTests.A_response_over_the_ceiling_is_refused_while_it_is_read` / `A_declared_length_over_the_ceiling_is_refused_before_the_body_is_read`. Collection/page-count sanity checks not added: the byte ceiling bounds them.

**Location:** [RemoteParser.cs](src/Mailvec.Core/Parsing/RemoteParser.cs), lines 110–113.

`HttpCompletionOption.ResponseContentRead` buffers the complete response before deserialization. The client has no application-sized response limit, leaving the roughly 2 GB default buffering ceiling in effect. A compromised parser can stream a large response without holding the entire payload in its own memory and exhaust the caller's memory instead.

The parser container's memory limit and request-body cap do not bound allocations in the indexer, embedder or MCP process. This leaves a path for a parser compromise to take down the archive-holding process despite the process split.

**Validation:** Source inspection. No memory-exhaustion experiment was run.

**Suggested fix:** Read headers first and enforce an operation-appropriate byte limit while consuming the response, including responses without a declared content length. Validate decoded payload sizes, collection sizes and page counts before accepting results. Test oversized declared and streamed responses without allocating gigabytes.

### 4. [P2] Protocol failures can permanently retire healthy attachments

> **Fixed.** `JsonException` on a 200 body and any undefined 4xx now surface as `ParseException(Crashed)`. `RogueServiceTests.A_success_body_that_is_not_a_parse_result_is_Crashed_not_a_document_verdict` / `An_unexpected_status_is_Crashed_not_a_document_verdict`; the callers' `Crashed` handling is already pinned by their phase 3 tests.

**Location:** [RemoteParser.cs](src/Mailvec.Core/Parsing/RemoteParser.cs), lines 138–140 and 185–188; [AttachmentOcrService.cs](src/Mailvec.Embedder/Services/AttachmentOcrService.cs), line 497.

Unexpected 4xx responses escape as `InvalidOperationException`, and malformed successful responses escape as `JsonException`. Neither carries a `ParseFailureKind`. OCR treats every non-`ParseException` as a deterministic document failure; `extract-attachments` also catches these exceptions and stamps the attachment `failed`.

A protocol mismatch, incorrect endpoint, or malformed service response can therefore permanently retire a healthy attachment. These failures describe the service interaction, not the document.

**Validation:** A temporary harness confirmed that malformed HTTP 200 content produces an unclassified `JsonException`, and an unexpected HTTP 404 produces an unclassified `InvalidOperationException`. The permanent-retirement consequence follows from the caller catch paths.

**Suggested fix:** Wrap protocol and deserialization failures in a non-document `ParseException`. Keep deterministic document failures explicit. Add caller-level tests proving that malformed responses and unexpected statuses leave attachments retryable.

### 5. [P2] Routine parser recycling prevents `rebuild-bodies` from finishing

> **Fixed.** It was broader than stated: at the default 500-request budget every CLI backfill stopped after ~500 messages; `rebuild-bodies` restarted from row one, the other two made progress but needed ~160 reruns on an 82k corpus. `RetryOnUnavailable` (Core) decorates the parser for the three commands: on `Unavailable` it probes `/up` every 2 s for up to `Parser:UnavailableWaitSeconds` (60), retries, naps between a failed retry and the next probe so a flapping host cannot spin it, and rethrows the original `Unavailable` only if the budget is spent — so the STOPPED path is unchanged for a service that stays down. `Crashed` is never retried. Tests: `RetryOnUnavailableTests` (six, sleeps injected), and one "spanning a recycle" test per command — `RebuildBodiesCommandTests.A_rebuild_spanning_a_parse_host_recycle_converts_every_row` is the reviewer's scenario.

**Location:** [RebuildBodiesCommand.cs](src/Mailvec.Cli/Commands/RebuildBodiesCommand.cs), lines 101–108 and 159–166.

The parser intentionally exits after 500 requests by default. `rebuild-bodies` stops when the ensuing restart produces `Unavailable`, then instructs the operator to rerun the command. Each rerun selects every HTML-bearing message again in ascending ID order, with no checkpoint or completed-row predicate.

For an archive larger than one parser lifetime, repeated runs can continually rebuild the same prefix without reaching the remaining rows. Concurrent parser clients can shorten that prefix further. Committing each batch protects completed writes, but does not make this command resumable.

**Validation:** With a two-request budget, a real temporary parse host served the initial requests and then produced `ParseException(Unavailable)` on a subsequent call. Source inspection confirms that the command stops on that result and restarts from the beginning on its next invocation.

**Suggested fix:** Retry routine recycling with bounded backoff while retaining the current position, or provide a durable resumable cursor. Test a rebuild that spans multiple parser lifetimes and verify every eligible row is processed.

### 6. [P2] Inline-image backfill always reports zero added rows

> **Fixed.** Dropped in `3c49336` (phase 1), not phase 3. Counters restored after the per-part loop, before the write, so an abandoned message counts nothing and a dry run counts what it would add. `BackfillInlineImagesCommandTests.The_summary_counts_the_rows_it_added` / `A_dry_run_reports_what_it_would_add`.

**Location:** [BackfillInlineImagesCommand.cs](src/Mailvec.Cli/Commands/BackfillInlineImagesCommand.cs), lines 205–219.

The refactor removed the increments of `rowsAdded` and `messagesWithNewRows`. Both remain zero throughout execution, even when `AddInlineAttachments` writes rows. Successful runs and dry runs consequently report zero added or proposed rows, and the follow-up message about OCR processing never appears.

**Validation:** Source inspection confirms both counters are initialized and read but never incremented.

**Suggested fix:** Restore accounting after a message has been successfully staged or committed, excluding discarded work after a crash or outage. Assert both the normal-run and dry-run summaries in the backfill tests.

## Validation and scope

- `dotnet test Mailvec.slnx --no-restore --verbosity quiet`: **1,342 passed, 0 failed**, across six test projects.
- Additional temporary fixtures verified MCP reachability from an internal network, redirect forwarding, unclassified response errors, and request-budget shutdown behavior.
- The Docker probe used an existing local image and an empty temporary archive; it was not a fresh image build or a homelab deployment test.
- Temporary Docker resources were removed. No real archive or Maildir was mounted into the review fixtures.
- The frozen corpus was untouched. No launchd agents, ingest services, releases or deployment changes were started on the development installation.
- This review does not implement the fixes. Passing existing tests does not cover the boundary cases identified above.
