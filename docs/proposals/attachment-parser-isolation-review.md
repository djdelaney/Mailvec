# Parser isolation branch review

- Initial review: 2026-09-18 (`fa2ac36`, `origin/main...HEAD`)
- Follow-up review: 2026-09-19 (`823078c`, `fa2ac36..823078c`)
- Branch: `parser-isolation-phase0`
- Latest reviewed commit: `823078c`

> **Status (2026-09-19, follow-up review). Three findings remain open: F1–F3 below.**
> Redirect protection and inline-image counters are fixed, and routine-recycle retries are
> implemented. The response byte cap addresses buffering but does not sufficiently bound
> deserialization allocations. Malformed JSON is classified, but missing response fields
> are still accepted. The network guard covers compose's direct environment configuration
> but ignores overrides applied through the options pipeline. **1,377 tests pass**; the
> additional synthetic fixtures below reproduce gaps not covered by that suite.

> **Disposition (2026-09-19, after the follow-up). All three fixed on the branch**, each
> verified against the reviewer's own reproduction: F1 by a zero-allocation shape pre-scan
> over the buffered bytes before deserialization (`ResponseShape`; the 100,000-object fixture
> is refused with under 64 KB allocated, versus ~15 MB deserialized); F2 by enforcing
> required constructor parameters and nullable annotations on the wire
> (`ParserWire.Json`), so `{}` and a null in a non-nullable member are `Crashed`, while an
> explicit `"text": null` stays a valid answer, plus a page-count invariant on
> `RenderPdfPages` and a caller-level test that `rebuild-bodies` keeps the row's body; F3 by
> reading `resolvedMcpOpts.DeniedNetworks`, with a `PostConfigure` test verified by mutation.

## Assessment

The project split is clean: Core depends on plain parser contracts, parsing libraries live in a separate project, and callers select the implementation through one registration path. Keeping Maildir path resolution and containment checks in the caller is the right separation of responsibilities. The scanner's refusal to reconcile deletions after an interrupted walk also preserves an important correctness invariant.

**Fix F1–F3 before merging.** The follow-up reviewed commits `59065b3` and `823078c`. The fixes improve the boundary, but callers can still allocate excessive memory from a response below the byte cap, accept incomplete responses as successful parses, and omit the network guard despite a resolved deny configuration. The initial findings are retained below as dated history, with their current disposition noted.

## Open follow-up findings — 2026-09-19

### F1. [P1] The byte cap does not bound deserialized memory

> **Fixed.** `ResponseShape.Check` walks the buffered response with `Utf8JsonReader` before `JsonSerializer` sees it: no array over 10,000 elements, no more than 1,000,000 tokens in the document, a violation is `Crashed`. Allocation-free by construction (stack-allocated container bookkeeping). `ResponseShapeTests.An_oversized_array_is_refused_without_materialising_it` runs the reviewer's fixture and asserts the refusal allocates under 64 KB on the calling thread; `RogueServiceTests.A_compact_response_with_an_oversized_collection_is_refused_before_deserialization` covers it end to end. `RenderPdfPages` additionally refuses more pages than it asked for.

**Location:** [ParserHttp.cs](../../src/Mailvec.Core/Parsing/ParserHttp.cs), lines 45–46; [RemoteParser.cs](../../src/Mailvec.Core/Parsing/RemoteParser.cs), line 142. Follow-up to initial finding 3.

`MaxResponseContentBufferSize` now limits the serialized response to 64 MB, but the deserializer can expand compact JSON into a much larger object graph. There is no collection-count limit before the graph is materialized. The earlier disposition that the byte ceiling alone sufficiently bounds collection sizes is not supported by the allocation measurements.

**Reproduction:** A synthetic response containing `{"attachments":[{},{},...]}` with 100,000 entries occupied **300,017 bytes** and allocated **14,898,208 bytes** during warmed deserialization through `ParserWire.Json` into `ParsedMessage`: approximately **49.7 times** the response size. A separate cold measurement was similar. These were small local experiments, not an OOM test. Scaling this accepted shape toward the 64 MB ceiling permits gigabyte-scale allocations in the caller, so parser-side memory limits still do not contain this failure.

**Suggested fix:** Enforce operation-specific collection limits while reading, before materializing the full object graph. Validate requested page counts and decoded payload limits as appropriate. Retain the serialized-byte ceiling as another limit. A count check performed only after ordinary deserialization is too late to prevent the allocation.

**Regression coverage:** Exercise compact, oversized collections below the byte ceiling and verify early rejection as a classified parser failure without allocating the whole collection.

### F2. [P1] Missing response fields are accepted as successful parsing

> **Fixed.** `ParserWire.Json` sets `RespectRequiredConstructorParameters` and `RespectNullableAnnotations`, so a missing member or a null in a non-nullable member is a `JsonException` → `Crashed`; an explicit null on a nullable member (`HtmlResponse.Text`) is still the valid answer it always was. Both ends compile against the same options, and the contract tests compare every fixture across the wire, so a host that ever produced such a null would fail there first. Tests: `RogueServiceTests.An_empty_object_is_not_a_body_text_result` / `An_explicit_null_on_a_nullable_member_is_still_a_valid_answer` / `A_decoded_part_without_its_bytes_is_Crashed` (missing and null) / `More_pages_than_were_requested_is_Crashed`; caller level, `RebuildBodiesCommandTests.A_row_the_parser_fails_on_keeps_its_body_text`.

**Location:** [RemoteParser.cs](../../src/Mailvec.Core/Parsing/RemoteParser.cs), lines 140–155; [ParserWire.cs](../../src/Mailvec.Parsing.Contracts/ParserWire.cs), serializer options. Follow-up to initial finding 4.

Catching `JsonException` rejects invalid JSON syntax and incompatible values, but the current serializer options do not require the expected fields or enforce non-null members. A non-null result object is therefore insufficient evidence of a valid response.

**Reproduction:** An HTTP 200 response containing only `{}` was accepted by `RemoteParser` as all of the following:

- `BodyTextFromHtml(...)` returned `null`.
- `DecodePart(...)` returned an object with `Bytes == null`.
- `RenderPdfPages(...)` returned an object with `Pages == null`.

The first case is silent data loss when used by `rebuild-bodies`: its existing write path replaces `body_text` with NULL after what it believes was a successful conversion. The other cases pass invalid objects into callers and can produce exceptions outside the remote client's classification boundary.

**Suggested fix:** Require the expected response fields, validate non-null members and result invariants, and convert violations to a non-document `ParseException` before returning. Preserve explicitly valid nullable values, such as an explicitly present `text: null`; distinguish them from a missing field.

**Regression coverage:** Test `{}`, missing required members, and explicit nulls in non-null members across response types. Add a caller-level test proving that an incomplete HTML response does not overwrite existing body text.

### F3. [P2] Network guard ignores resolved options

> **Fixed.** `NetworkGuard.Parse(resolvedMcpOpts.DeniedNetworks)`. `ProgramHttpTests.The_deny_list_reads_resolved_options_not_the_builder_snapshot` applies the deny-list through `PostConfigure<McpOptions>` and expects 403; verified by mutation (reverting to the snapshot fails it). The `HostGuard` allowlist still reads the snapshot under the documented exemption in `Mailvec.Mcp/CLAUDE.md`; not changed here.

**Location:** [Program.cs](../../src/Mailvec.Mcp/Program.cs), line 130. Follow-up to initial finding 1.

`NetworkGuard.Parse(mcpOpts.DeniedNetworks)` reads the builder-time configuration snapshot even though `resolvedMcpOpts` is already available. A deny-list supplied or changed by the options pipeline is ignored. This violates the existing rule in [Mailvec.Mcp/CLAUDE.md](../../src/Mailvec.Mcp/CLAUDE.md): security controls read resolved options.

**Reproduction:** A temporary fixture used the existing `RemoteCallerFactory` to simulate a caller at `172.18.0.7` and registered `PostConfigure<McpOptions>` with `DeniedNetworks = ["172.18.0.0/24"]`. The resolved options contained that subnet, but the caller's `tools/list` request still returned **HTTP 200**. Its `/up` request also reached the handler, returning 503 for the fixture's unavailable embedding service rather than the guard's 403.

Compose's direct environment configuration is covered by the existing tests; this finding concerns the resolved-options override path, not a claim that the shipped subnet is always ignored.

**Suggested fix:** Read `resolvedMcpOpts.DeniedNetworks` when constructing the guard.

**Regression coverage:** Add a `PostConfigure<McpOptions>` test asserting HTTP 403 for MCP requests from the configured subnet, while callers outside it remain unaffected.

## Follow-up validation and scope

- `dotnet test Mailvec.slnx --no-restore --verbosity quiet`: **1,377 passed, 0 failed**, across six projects at `823078c`.
- Synthetic fixtures reproduced missing-field acceptance, deserialization allocation amplification, and the resolved-options network-guard bypass.
- The memory measurements used a roughly 300 KB response; no gigabyte-sized payload or deliberate OOM was exercised.
- The network-options fixture used a temporary test archive and the in-memory test host. This follow-up did not rebuild Docker images or test the homelab deployment.
- The review left tracked files and the frozen corpus unchanged. This subsequent note update records the findings; it does not implement their fixes.

## Initial findings — 2026-09-18

The descriptions and original line references below describe `fa2ac36`. Disposition notes distinguish fixes from the remaining follow-up gaps.

### 1. [P1] The parser can call MCP and read the archive

> **Fixed for compose's direct configuration; F3 remains open.** The `parse` subnet is pinned in compose and denied by `NetworkGuard` before routing; malformed CIDRs fail startup and loopback remains exempt. Existing network-guard tests cover this path, and a Docker negative test is recorded in the status doc. The follow-up reproduced a bypass when the deny-list is applied through the resolved-options pipeline. `Mcp:Access` remains the stronger additional layer for tunnel deployments.

**Location:** [compose.yml](../../compose.yml), line 209; related configuration at lines 220, 234 and 435.

MCP joins the `parse` network while `Mcp__Access__Enabled` defaults to `false`. An internal Docker network permits communication between its members; it does not restrict connections to caller → parser. MCP listens on all interfaces, and its Host guard is not authentication.

A compromised parser can therefore call `search_emails`, `get_email`, and the other MCP tools directly. This contradicts the security acceptance that compromise exposes only documents currently being parsed. Lack of a database mount does not prevent archive access through MCP.

**Validation:** Using the existing `mailvec:uid` image, a disposable container running as UID 65534 on an internal test network successfully called `list_folders` on an MCP container with an empty temporary archive. The response was HTTP 200. The request used an allowed `Host` header; compose additionally allowlists `mcp` explicitly. No real archive was mounted.

**Suggested fix:** Prevent parser-initiated connections to MCP, or require origin authentication that the parser cannot satisfy. Preserve the return traffic needed for caller-initiated parse requests. Add a deployment-level negative test proving that the parser can answer requests but cannot invoke MCP tools.

### 2. [P1] Automatic redirects let the parser export mail through callers

> **Fixed.** `ParserHttp.CreateHandler` (`AllowAutoRedirect = false`, `UseProxy = false`); a 3xx classifies as `Crashed`. `RogueServiceTests.A_redirect_is_refused_and_the_message_bytes_go_nowhere` (301/302/307/308; the sink receives zero requests), verified by mutation.

**Location:** [ParserRegistration.cs](../../src/Mailvec.Core/Parsing/ParserRegistration.cs), lines 38–46.

The named parser `HttpClient` retains automatic redirects. A compromised parser can return HTTP 307 or 308 with an external `Location`, causing the caller to resend the POST body—the entire `.eml`—to that destination. MCP and the embedder have network access that the parser itself lacks, so the redirect bypasses the intended egress restriction.

**Validation:** A temporary harness using `RemoteParser` and two local HTTP servers confirmed that a 307 response forwarded synthetic email bytes to the second server and returned a successful parse result.

**Suggested fix:** Configure the parser client's primary handler with `AllowAutoRedirect = false`. Classify redirects as protocol failures rather than document rejection. Add a test that verifies a second endpoint receives no request after a 307/308 response.

### 3. [P1] Parser responses need a practical limit before buffering

> **Partially fixed; F1 remains open.** `Parser:MaxResponseBytes` (64 MB) now limits response buffering, with declared-length and chunked-body tests. Collection limits were not added. The follow-up measurements show that the byte ceiling alone still permits excessive deserialization allocations in the caller.

**Location:** [RemoteParser.cs](../../src/Mailvec.Core/Parsing/RemoteParser.cs), lines 110–113.

`HttpCompletionOption.ResponseContentRead` buffers the complete response before deserialization. The client has no application-sized response limit, leaving the roughly 2 GB default buffering ceiling in effect. A compromised parser can stream a large response without holding the entire payload in its own memory and exhaust the caller's memory instead.

The parser container's memory limit and request-body cap do not bound allocations in the indexer, embedder or MCP process. This leaves a path for a parser compromise to take down the archive-holding process despite the process split.

**Validation:** Source inspection. No memory-exhaustion experiment was run.

**Suggested fix:** Read headers first and enforce an operation-appropriate byte limit while consuming the response, including responses without a declared content length. Validate decoded payload sizes, collection sizes and page counts before accepting results. Test oversized declared and streamed responses without allocating gigabytes.

### 4. [P2] Protocol failures can permanently retire healthy attachments

> **The reported exception paths are fixed; F2 remains open.** `JsonException` on a 200 body and undefined 4xx responses now surface as `ParseException(Crashed)`, with rogue-service tests. However, incomplete but syntactically valid JSON can deserialize successfully and bypass that classification entirely, including returning a null body that `rebuild-bodies` writes back.

**Location:** [RemoteParser.cs](../../src/Mailvec.Core/Parsing/RemoteParser.cs), lines 138–140 and 185–188; [AttachmentOcrService.cs](../../src/Mailvec.Embedder/Services/AttachmentOcrService.cs), line 497.

Unexpected 4xx responses escape as `InvalidOperationException`, and malformed successful responses escape as `JsonException`. Neither carries a `ParseFailureKind`. OCR treats every non-`ParseException` as a deterministic document failure; `extract-attachments` also catches these exceptions and stamps the attachment `failed`.

A protocol mismatch, incorrect endpoint, or malformed service response can therefore permanently retire a healthy attachment. These failures describe the service interaction, not the document.

**Validation:** A temporary harness confirmed that malformed HTTP 200 content produces an unclassified `JsonException`, and an unexpected HTTP 404 produces an unclassified `InvalidOperationException`. The permanent-retirement consequence follows from the caller catch paths.

**Suggested fix:** Wrap protocol and deserialization failures in a non-document `ParseException`. Keep deterministic document failures explicit. Add caller-level tests proving that malformed responses and unexpected statuses leave attachments retryable.

### 5. [P2] Routine parser recycling prevents `rebuild-bodies` from finishing

> **Fixed.** It was broader than stated: at the default 500-request budget every CLI backfill stopped after ~500 messages; `rebuild-bodies` restarted from row one, the other two made progress but needed ~160 reruns on an 82k corpus. `RetryOnUnavailable` (Core) decorates the parser for the three commands: on `Unavailable` it probes `/up` every 2 s for up to `Parser:UnavailableWaitSeconds` (60), retries, naps between a failed retry and the next probe so a flapping host cannot spin it, and rethrows the original `Unavailable` only if the budget is spent — so the STOPPED path is unchanged for a service that stays down. `Crashed` is never retried. Tests: `RetryOnUnavailableTests` (six, sleeps injected), and one "spanning a recycle" test per command — `RebuildBodiesCommandTests.A_rebuild_spanning_a_parse_host_recycle_converts_every_row` is the reviewer's scenario.

**Location:** [RebuildBodiesCommand.cs](../../src/Mailvec.Cli/Commands/RebuildBodiesCommand.cs), lines 101–108 and 159–166.

The parser intentionally exits after 500 requests by default. `rebuild-bodies` stops when the ensuing restart produces `Unavailable`, then instructs the operator to rerun the command. Each rerun selects every HTML-bearing message again in ascending ID order, with no checkpoint or completed-row predicate.

For an archive larger than one parser lifetime, repeated runs can continually rebuild the same prefix without reaching the remaining rows. Concurrent parser clients can shorten that prefix further. Committing each batch protects completed writes, but does not make this command resumable.

**Validation:** With a two-request budget, a real temporary parse host served the initial requests and then produced `ParseException(Unavailable)` on a subsequent call. Source inspection confirms that the command stops on that result and restarts from the beginning on its next invocation.

**Suggested fix:** Retry routine recycling with bounded backoff while retaining the current position, or provide a durable resumable cursor. Test a rebuild that spans multiple parser lifetimes and verify every eligible row is processed.

### 6. [P2] Inline-image backfill always reports zero added rows

> **Fixed.** Dropped in `3c49336` (phase 1), not phase 3. Counters restored after the per-part loop, before the write, so an abandoned message counts nothing and a dry run counts what it would add. `BackfillInlineImagesCommandTests.The_summary_counts_the_rows_it_added` / `A_dry_run_reports_what_it_would_add`.

**Location:** [BackfillInlineImagesCommand.cs](../../src/Mailvec.Cli/Commands/BackfillInlineImagesCommand.cs), lines 205–219.

The refactor removed the increments of `rowsAdded` and `messagesWithNewRows`. Both remain zero throughout execution, even when `AddInlineAttachments` writes rows. Successful runs and dry runs consequently report zero added or proposed rows, and the follow-up message about OCR processing never appears.

**Validation:** Source inspection confirms both counters are initialized and read but never incremented.

**Suggested fix:** Restore accounting after a message has been successfully staged or committed, excluding discarded work after a crash or outage. Assert both the normal-run and dry-run summaries in the backfill tests.

## Initial validation and scope — 2026-09-18

- `dotnet test Mailvec.slnx --no-restore --verbosity quiet`: **1,342 passed, 0 failed**, across six test projects.
- Additional temporary fixtures verified MCP reachability from an internal network, redirect forwarding, unclassified response errors, and request-budget shutdown behavior.
- The Docker probe used an existing local image and an empty temporary archive; it was not a fresh image build or a homelab deployment test.
- Temporary Docker resources were removed. No real archive or Maildir was mounted into the review fixtures.
- The frozen corpus was untouched. No launchd agents, ingest services, releases or deployment changes were started on the development installation.
- This review does not implement the fixes. Passing existing tests does not cover the boundary cases identified above.
