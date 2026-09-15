# Design proposal — one process for untrusted bytes

**Status:** phases 0, 1 and 2 done (2026-09-13, branch `parser-isolation-phase0`); phases 3–4 proposed.
**Date:** 2026-09-13 (replaces an earlier review that was lost; restructured the
same day after verifying what "managed" actually covers).
**Scope:** the container deployment (`compose.yml`). The macOS launchd / MCPB
path keeps today's in-process behaviour untouched.

## Summary

Today four processes parse attacker-chosen bytes, and the two most privileged
of them do it with native C++ libraries. This proposal moves **every parser
that touches mail content** — MIME, HTML, PDF text, Office, PDF rasterisation,
image decode — into one new compose service, `parse`, that holds **no volumes,
no secrets, no egress and no database**. The indexer, embedder and MCP server
send it bytes and get back plain data: a parsed message record, extracted text,
a JPEG, or decoded part bytes. After the change:

> **Exactly one process ever touches attacker-chosen bytes, and it holds
> nothing.** The four services that hold the mailbox, the archive, the API
> keys and the tunnel contain no parsing code at all, and the image enforces
> that by not shipping the parser libraries into their directories.

A second benefit is at least as valuable as the first and is independent of
any exploit: **a poison document stops being able to wedge the pipeline.**
Phase 0 measured this ([results](attachment-parser-isolation-phase0.md)): a
**958-byte** PDF passes the indexer as `no_text` in 186 ms and then keeps
PDFium rendering page 1 for as long as its author likes (34 ms per operator,
linear, uncancellable, serialised); a 100 KB PDF gets the process
**OOM-killed by the cgroup**. Both stall the embedder's OCR pass — and
therefore *all* embedding, since OCR runs first each cycle — and both are
re-selected first after a restart. Out of process, both become a catchable
failure the existing retirement machinery can count. (PdfPig's own memory
bombs, by contrast, turned out to be caught by the runtime's container heap
limit; see phase 0 for what that does and doesn't change.)

**Recommendation: do it, in the four phases below.** Feasible with seams the
codebase already has (`VisionRegistration` / `IVisionClient` is the template,
applied a third time), no schema change, no MCP tool-surface change, roughly
a week of work, and a `--patch` release when it ships.

## What runs where today

| Library | Native? | Memory-safe? | Process | Trigger | What the process holds |
| --- | --- | --- | --- | --- | --- |
| PDFium (`PDFtoImage`) | **yes** (C++) | no | embedder (OCR pass), mcp (`get_attachment_page_image`) | unattended for every scanned PDF that arrives; on demand | `/data` rw (whole archive), `/mail` ro, `embedding_api_key`, `Vision__Mistral__ApiKey`, default network (Ollama, hosted APIs, internet); mcp is also the tunnel's only origin |
| SkiaSharp | **yes** (C++) | no | embedder (image OCR), mcp (`view_attachment` on images), embedder (`_probeJpeg`) | unattended for every image attachment ≥ 50 KB; on demand | as above |
| MimeKit (`MimeMessage.Load`) | no | **no** — the parser core and every content codec are `unsafe` pointer code | **all four**: indexer (`MessageParser`), embedder + mcp + cli (`MaildirAttachmentReader`) | unattended in indexer and embedder; on demand in mcp | as above for embedder/mcp; indexer below |
| PdfPig, DocumentFormat.OpenXml | no | mostly — but both inflate attacker bytes through the runtime's **native zlib**; PdfPig also runs OpenSSL AES/MD5 for encrypted PDFs (lenient open tries the empty password) | indexer (`AttachmentTextExtractor`), cli | unattended, every attachment; no page limit, timeout or cancellation on the PdfPig parse | indexer: `/data` rw, `/mail` ro, **`isolated` network (no egress), no secrets** |
| AngleSharp (`HtmlToText`) | no | yes | indexer, cli (`rebuild-bodies`) | unattended, every HTML body | as indexer |
| BitMiracle.LibTiff.NET | no | yes | embedder (TIFF only) | unattended | as embedder |

Sources (paths as of phase 1; before it, the parsers lived in `Mailvec.Core` and
`Mailvec.Pdf`): [`compose.yml`](../../compose.yml),
[`PdfRenderer.cs`](../../src/Mailvec.Parsing/PdfRenderer.cs), [`ImageRenderer.cs`](../../src/Mailvec.Parsing/ImageRenderer.cs),
[`MessageParser.cs`](../../src/Mailvec.Parsing/MessageParser.cs),
[`AttachmentTextExtractor.cs`](../../src/Mailvec.Parsing/AttachmentTextExtractor.cs),
[`MaildirAttachmentReader.cs`](../../src/Mailvec.Core/Attachments/MaildirAttachmentReader.cs).

### What "managed" actually means here (verified 2026-09-13)

Checked by reading the assembly metadata of each parser and its transitive
dependencies (ModuleRef table, `PinvokeImpl` methods, pointer-typed
signatures, `localloc` in IL) and the packages' `runtimes/` folders, with a
throwaway `System.Reflection.Metadata` scanner. Re-run it after a major
version bump of any of these.

| Assembly | P/Invoke / native module refs | `unsafe` (pointer-typed methods) | Native code reached through the BCL |
| --- | --- | --- | --- |
| MimeKit 4.17.0 | none | **94** — 61 in `MimeParser` / `MimeReader`, the rest in the base64 / quoted-printable / uuencode / yEnc codecs. That is the entire path every attacker byte takes. | none in our usage (S/MIME and OpenPGP references are never called; we only parse) |
| PdfPig 0.1.16 (7 assemblies) | none | 2, both irrelevant (an annotation, a TrueType header table) | **zlib** via `DeflateStream` for every `FlateDecode` stream; OpenSSL for encrypted PDFs |
| DocumentFormat.OpenXml 3.5.1 + Framework + System.IO.Packaging | none | none | **zlib** via `ZipArchive` — the OOXML container is attacker-controlled |
| AngleSharp 1.8.1 | none | 2 (a DOM mouse-event type) | none |
| BitMiracle.LibTiff.NET 2.4.660 | none | 1 (display-conversion helper) | none |
| BouncyCastle 2.6.2 (MimeKit transitive) | none | 4 (post-quantum code, never called) | not reached |

So "not native" holds for all of them, and "memory-safe by construction" holds
for AngleSharp, OpenXml, LibTiff.NET and PdfPig's own code — but **not** for
MimeKit's parser core, and not for the zlib underneath PdfPig and OpenXml
(`libSystem.IO.Compression.Native`; zlib-ng on Linux). Both are mature and
without PDFium's CVE history; they are second-order next to the rasterisers.
But two facts from this inventory shaped the design:

1. **The MIME decode runs in every service, not just the indexer.** The
   embedder and both MCP viewer tools call `MimeMessage.Load` on the whole
   `.eml` before any rendering happens. Sandboxing the rasteriser alone would
   leave the largest block of `unsafe` code in the tree inside the two most
   privileged processes.
2. **The indexer's parse has no bound of its own — but the runtime supplies
   one for memory, inside a cgroup.** PdfPig runs with no page limit, timeout
   or cancellation, and extraction happens before any row is written. The
   wedge this proposal first hypothesised (a memory bomb OOM-kills the
   container, it restarts and parses the same file forever) **was not
   reproduced**: inside `--memory=2g` .NET caps its managed heap at 75 % of
   the limit and throws a managed `OutOfMemoryException`, which
   `AttachmentTextExtractor` catches and stamps `failed`; the process survived
   three in a row. What remains is that nothing bounds PdfPig's *time* (no CPU
   bomb was found in five shapes, none searched exhaustively) and that the
   memory guard is a container default the macOS install doesn't have.

## Why the indexer is included

A compromised indexer is a much smaller prize than the embedder or MCP: it has
no egress (alone on the `internal: true` network), no credentials, a read-only
`/mail`, a read-only root filesystem and no capabilities. What it gets is
integrity and availability of the archive — plant text, poison vectors, or
wipe the database (a rebuild from `/mail`, days on the CPU-only Ollama host,
but not data loss). Most of the integrity attacks are available more cheaply by
just sending mail. If exploit containment were the only goal, leaving the
indexer alone would be defensible.

The availability argument was the original reason to include it, and phase 0
weakened it: PdfPig's memory bombs are converted into `failed` rows by the
runtime's container heap limit, and no CPU bomb was found. What is left is
narrower but real — nothing bounds PdfPig's *time* (a parse that never
returns still stops indexing for everyone, and only an out-of-process parse
can turn that into a per-message failure), the memory guard exists only
inside a cgroup, and the containment and uniform-enforcement arguments above
are untouched. Once the `parse` service exists for the rasterisers, routing
the indexer's parse through it costs one more implementation of a seam that
already exists, and it buys the clean sentence in the summary. **It is
therefore kept in the end state but moved to the last phase, and that phase
can be deferred without invalidating anything before it.**

## Design

### The seam: one interface, two implementations, three layers

```
Mailvec.Parsing.Contracts/      NEW, tiny. DTOs + interface + exception + status constants.
                                No package references. Referenced by Core.
  IMailParser.cs
    ParseMessage(eml, ParseOptions)              → ParsedMessage    (indexer; cli backfill-inline-images)
    ExtractAttachmentText(eml, part)             → ExtractionResult (cli extract-attachments --reextract-*)
    DecodePart(eml, part, maxBytes)              → DecodedPart      (mcp view_attachment; cli extract-attachments)
    PdfPageCount(eml, part)                      → int              (embedder OCR; mcp page image)
    RenderPdfPages(eml, part, first, count)      → byte[][] JPEG    (embedder OCR batch; mcp page image, count=1)
    NormalizeImage(eml, part)                    → NormalizedImage? (embedder image OCR; mcp view_attachment)
    HtmlToText(html)                             → string           (cli rebuild-bodies)
    ProbeAsync()                                 → mode + reachable
  ParseException.cs             Kind: Unavailable | DocumentRejected | Crashed
  ParsedMessage.cs, ParsedAttachment.cs, EmailAddress.cs, ExtractionResult.cs,
  DecodedPart.cs, NormalizedImage.cs, ExtractionStatus.cs (the seven constants)

Mailvec.Parsing/                the implementations. Absorbs today's Mailvec.Pdf.
                                References Contracts + MimeKit, PdfPig, OpenXml, AngleSharp,
                                PDFtoImage, SkiaSharp, LibTiff.NET. Never references Core.
  InProcessParser.cs            IMailParser over the moved code
  MessageParser.cs, MessageBodyHasher.cs, MessageParts.cs, HtmlToText.cs,
  ReplyTrimmer.cs, BoilerplateFilter.cs, TextNormalize.cs   (moved from Core/Parsing)
  AttachmentTextExtractor.cs, MimePartDecoder.cs             (moved/split from Core/Attachments)
  PdfRenderer.cs, ImageRenderer.cs                            (moved from Mailvec.Pdf)

Mailvec.Core/Parsing/
  RemoteParser.cs               IMailParser over HttpClient to Parser:Endpoint
  ParserRegistration.cs         AddMailvecParser(config, inProcessFactory):
                                Parser:Mode = inprocess | remote. Unknown mode is fatal.

Mailvec.Parse/                  NEW host: minimal Kestrel app. References Mailvec.Parsing only.
                                One endpoint per IMailParser operation, plus GET /up.
```

Rules that make the layering mean something:

- **`Mailvec.Parsing` never references Core.** No SQLite, no shared-config
  loader, no options classes that know where secrets live. Its whole point is
  that the `parse` host can reference it while holding nothing else.
- **Core references only `Contracts`.** After the move, Core drops MimeKit,
  PdfPig, OpenXml and AngleSharp from its package list entirely. `Core.csproj`
  losing those four lines is the checkable artefact of this rule.
- **Every host passes its own in-process factory** to `ParserRegistration`
  (`sp => new InProcessParser(...)`), so mode resolution is central (the
  `VisionRegistration` rule: the four binaries cannot drift onto different
  parsers) while Core never names the implementation type. In remote mode the
  lambda is never invoked, so the parser assemblies are never loaded.
- **`MaildirAttachmentReader` stays in Core and keeps only path work**:
  `ResolveWithinRoot`, `EnsureSourceExists`, the containment guard, and now a
  `ReadEml(message)` that returns the file bytes. Which file is a caller
  decision; what is inside it is the parser's. The bounded-write-stream
  `maxBytes` enforcement moves into `MimePartDecoder`, and the `.eml` size
  itself is bounded by the request cap below.
- **`MessageParts.Indexable` moves with the parser.** The writer
  (`MessageParser`) and the reader (`MimePartDecoder`) still share one
  enumeration, from one project. The existing `MessagePartsTests` are the
  proof it did not fork in the move.
- **`ParsedMessage` is already plain data** (strings, a `DateTimeOffset`,
  address lists, attachment metadata + text). It serialises as-is; no DTO
  translation layer.

### Failure classification

`ParseException.Kind` is decided in `RemoteParser` from the HTTP outcome and
mapped by each caller onto the branch it already has:

| Kind | Meaning | Indexer (`MaildirScanner.TryIngest`) | Embedder OCR pass | MCP tools |
| --- | --- | --- | --- | --- |
| `DocumentRejected` (4xx) | the parser opened it and said no: encrypted, corrupt, not an image, over a ceiling | never happens for `ParseMessage` (a message that fails MIME parse still yields a record with an empty body — today's behaviour); for extraction it arrives inside the record as `extraction_status` | mark failed / no_text with `OcrProvenance.PreProvider` — today's branch for a managed exception | today's "Could not render …" message |
| `Crashed` (connection reset mid-request, 5xx, 504) | the service died, hung or errored *on this document* | write the existing "last ingest failed" marker (`content_hash` NULL) so the next scan retries; after `Parser:MaxCrashesPerFile` (3) strikes on the same file identity, re-request with `ParseOptions.ExtractText = false` and record the attachments as `failed`. The message is indexed; one document's text is not; nothing wedges | a strike against the candidate's `OcrFailureKey`, settled by the existing `SettleVisionFailures` rule with a parser health probe standing in for the vision probe | "Could not render …" |
| `Unavailable` (connect refused, `/up` failing, timeout before first byte) | the service is down or restarting | **stop ingesting and mark the scan incomplete** — see below | abort the batch, count nothing — the `Backpressure` branch | "Parsing is temporarily unavailable; retry, or use get_attachment_text" |

Rules carried over from the vision taxonomy: an unclassified HTTP failure
falls through to `Crashed`, never to `DocumentRejected` — a bug in the new
classification retries a document, it never retires one. `Unavailable` never
counts as a strike against anything.

**An incomplete scan must not reconcile deletions.** The scanner diffs tracked
paths against the in-memory set of paths *this walk* enumerated
(`observedPaths` → `EntriesNotObserved`), and the rename-repair pass keys on
the same set. If `Unavailable` aborts the walk partway, the set is partial and
reconciliation would soft-delete every message the walk never reached — a
spurious soft-delete is sticky (CLAUDE.md, "Scans must never overlap"). So on
`Unavailable` the scanner finishes nothing: it returns without running the
deletion pass or the repair pass, logs once, and the next timer tick retries.
The mtime fast path means an unchanged corpus never calls the parser at all,
so a parser outage costs exactly the new mail that arrived during it, and that
mail is picked up on the first scan after it returns. Test:
`MaildirScannerTests.A_parser_outage_mid_scan_deletes_nothing`.

The crash-strike counter is in-memory (keyed on the same `(path, mtime, size)`
identity the fast path uses), so a container restart resets it and the file
gets up to three more attempts. Bounded, and it avoids a schema change for a
counter nothing else reads.

### The `parse` service

- **Host** `src/Mailvec.Parse`: Kestrel, one endpoint per operation, request
  body = the `.eml` bytes, everything else in query parameters. Body cap sized
  for a whole `.eml` carrying a 25 MB attachment (base64 inflates it to ~34 MB;
  allow 48 MB). Concurrency: managed parses in parallel, PDFium serialised (as
  PDFtoImage already does), Kestrel's queue capped rather than unbounded.
- **Per-request timeout is what finally bounds PDFium — and PdfPig, which
  has no bound of its own either.** `Parser:RequestTimeout` (60 s; phase 0
  measured legitimate renders at 25–750 ms and the first hostile one at 69 s).
  PDFium, PdfPig and OpenXml all take no cancellation token, so a runaway
  parse cannot be stopped, only abandoned. On timeout the service returns 504
  **and exits** (compose restarts it in seconds); the caller sees `Crashed`
  for that document. An abandoned thread that keeps spinning inside a process
  we keep alive would otherwise eat the container's CPU forever — which is
  exactly what the 958-byte shading PDF does to the embedder today. Exiting
  is the honest option and it is cheap: the service is stateless.
- **Bounded lifetime.** `Parser:MaxRequestsBeforeExit` (a few hundred): the
  process exits cleanly after N requests. A compromised process persists for
  at most N documents. The only control here that limits *persistence*
  rather than *reach*.
- **Compose:**

  ```yaml
  parse:
    <<: *mailvec-common          # cap_drop ALL, no-new-privileges, read_only, tmpfs /tmp noexec, pids_limit
    command: ["dotnet", "/app/parse/Mailvec.Parse.dll"]
    user: "65534:65534"          # first non-root service; it owns nothing, so nothing to chown
    networks: [parse]            # internal: true — no egress
    environment:                 # NOT *mailvec-env: no profile config, no ApiKeyFile path
      HOME: /tmp
      Parser__RequestTimeoutSeconds: 60
      Parser__MaxRequestsBeforeExit: 500
    mem_limit: 2g                # PdfPig/OpenXml peaks move here from the indexer
    pids_limit: 128
    # no volumes, no secrets
    healthcheck: curl -fsS http://127.0.0.1:3400/up
  ```

  `indexer` joins `[isolated, parse]` and drops to `mem_limit: 1g` (it no
  longer parses). `embedder` and `mcp` join `[default, parse]`. All three get
  `Parser__Mode: remote` and `Parser__Endpoint: http://parse:3400`.
- **Enforcement, not documentation.** After publish, the Dockerfile deletes
  from `/app/indexer`, `/app/embedder`, `/app/mcp` and `/app/cli`:
  `MimeKit.dll`, `BouncyCastle.Cryptography.dll`, `UglyToad.PdfPig.*.dll`,
  `DocumentFormat.OpenXml*.dll`, `System.IO.Packaging.dll`, `AngleSharp.dll`,
  `BitMiracle.LibTiff.NET.dll`, `PDFtoImage.dll`, `SkiaSharp.dll`,
  `libpdfium.so`, `libSkiaSharp.so` — and asserts each deletion with
  `test ! -e` so a renamed assembly in a future package bump fails the build
  rather than silently surviving. `Mailvec.Parsing.dll` itself stays (the
  factory lambda references it), but with its dependencies gone an accidental
  `Parser__Mode=inprocess` in the container fails loudly at the first parse
  with `FileNotFoundException` instead of silently widening the surface back
  to today. Same philosophy as the frozen-corpus guard: the guard is the
  enforcement, the doc is the explanation. `/app/parse` is the one directory
  in the image that carries a parser.
- **Health.** `/health` (loopback-only) and `mailvec doctor` report the
  parser mode and reachability. `/up` is wire-locked and unchanged. Parser-down
  never flips `/health`'s 503, for the reason liveness doesn't: it is the
  `parse` container's outage, and restarting mcp for it would be wrong.
  Search keeps working throughout; only the two viewer tools and new-mail
  ingest wait.
- **The CLI.** `extract-attachments`, `backfill-inline-images` and
  `rebuild-bodies` go through `IMailParser` like everything else. In the
  container, `docker exec` them from any of the three parser-network
  containers. They are operator-invoked, never unattended, so a parser outage
  during one is a visible error, not a hazard.
- **macOS / launchd / MCPB:** `Parser:Mode` defaults to `inprocess`, which is
  byte-for-byte today's code path. No change there.

### What a compromise of `parse` still gets — the residual

The service still eats attacker bytes; that is its job. Code execution inside
it yields: the document being parsed and any others in flight; the ability to
return **attacker-chosen output** for those requests — a lying `ParsedMessage`
(wrong sender, planted body text, fabricated attachment text), a JPEG the
vision model or Claude then sees, or wrong page counts; and nothing else. No
file, no database, no credential, no network route, and a lifetime bounded by
`MaxRequestsBeforeExit`. The lying-output channel is real but is the same
channel an attacker has today by simply writing the email that way: the
parser's output is already treated as untrusted content by every tool
description and by the indexer's storage. What changes is that the lie can no
longer be *written into the archive directly* or *sent anywhere*.

### Optional hardening on top, not in scope

- `runtime: runsc` (gVisor) on the one `parse` service. Strongest available
  isolation, but needs gVisor installed on the Docker VM — an ops change
  outside this repo. The design makes it a one-line addition later.
- Landlock / seccomp self-restriction via P/Invoke: rejected for now. Landlock
  network rules need kernel 6.7+, a seccomp allowlist for the .NET runtime is
  fragile, and the container boundary already gives the same properties with
  primitives this repo uses.

## Alternatives considered

- **Rasteriser-only sandbox (the first draft of this proposal).** Removes
  PDFium and Skia from the privileged processes but leaves MimeKit's `unsafe`
  parser in all four and PdfPig's unbounded parse in the indexer. Strictly
  less for nearly the same plumbing.
- **Subprocess in the same container.** Crash containment only. Same mounts,
  secrets and network; can't change UID (`SETUID` dropped) and Docker's
  default seccomp blocks `unshare`, so it cannot become a boundary from
  inside. Could be a later add-on for the macOS path.
- **Disable the two MCP tools.** Already analysed in `docs/security.md`: the
  unattended passes are the larger half and stay.
- **A separate, smaller image for `parse`.** Cleaner in principle, but a
  second publish pipeline and a second Dependabot surface for no isolation
  gain — the container has no volumes either way. Same image, different
  `command:`, strip the libraries from the *other* directories instead.
- **Bounding PdfPig in-process instead** (page cap, a watchdog thread). Fixes
  the wedge partially but not the exposure, and a watchdog cannot stop a
  parse that ignores cancellation — it can only abandon it inside a process
  that also holds the archive.

## Phased plan

Each phase leaves `main` shippable; nothing releases until asked.

**Ordering after phase 0.** The measured wedge is in PDFium, so the
rasteriser and MIME-decode moves (embedder + MCP) ship first and carry the
whole availability win. The indexer's `ParseMessage` move and its scanner
failure handling are the last slice of phases 1 and 3 respectively; do them
last, and if they slip they slip alone — every rule in "The seam" still holds
for a `parse` service the indexer doesn't yet call.

**Phase 0 — measure. Done 2026-09-13:**
[attachment-parser-isolation-phase0.md](attachment-parser-isolation-phase0.md),
harness in [`tools/Mailvec.ParserBench`](../../tools/Mailvec.ParserBench).
In one line each: image bombs are harmless to PDFium (it downsamples);
malformed input raises managed exceptions in both parsers, no crash found;
PDFium is OOM-killed by the cgroup on a 100 KB PDF and rendered a 958-byte
shading PDF past the 180 s cut-off while PdfPig had already filed it as an
OCR candidate; PdfPig's memory bombs are caught by the runtime heap limit and
the process survives them; no PdfPig CPU bomb found. Phase 3's OCR-pass tests
get a real fixture (`e-sh-20k.pdf` is 958 bytes and checks in); the scanner
tests use fakes.

**Phase 1 — the project split, in-process only. Done 2026-09-13** (branch `parser-isolation-phase0`; see the CHANGELOG entry). Two deviations from the plan below, both deliberate: moved types keep their original namespaces so the move is a project boundary rather than a rename (a later mechanical rename is cheap; a diff that mixed the two was not), and "every existing test runs unchanged" became "every existing test's intent is unchanged" — constructor call sites that now take an `IMailParser` or a plain `AttachmentMaxBytes` were updated, nothing else. Also folded in: `ParserBoundaryTests` asserts the assembly graph (Core references no parser and not Parsing; Parsing references no Core), which is the artefact that cannot drift quietly.

Original plan:
`Mailvec.Parsing.Contracts` and `Mailvec.Parsing` created; `Mailvec.Pdf`
folded in; the parser, hasher, `MessageParts`, HTML/text helpers and
`AttachmentTextExtractor` moved; `MaildirAttachmentReader` reduced to path
work + `ReadEml`; `MimePartDecoder` split out; `ExtractionStatus` constants
moved to Contracts (19 uses in `MessageRepository`, 8 in
`GetAttachmentTextTool`, 1 in the CLI); `IMailParser` + `InProcessParser` +
`ParserRegistration`; every call site converted; the OCR pass switched to
batch page render; the probe JPEG made an embedded resource. **Behaviour
identical.** Every existing test in `Core.Tests/Parsing`,
`Core.Tests/Attachments`, `AttachmentOcrServiceTests`, the two MCP viewer
tool suites and `MaildirScannerTests` runs unchanged against
`InProcessParser` — that is the proof of "identical". New: 
`ParserRegistrationTests` (mode resolution, unknown mode fatal, remote mode
never invokes the in-process factory) and `Core.csproj` no longer listing the
four parser packages.

**Phase 2 — `Mailvec.Parse` + `RemoteParser`. Done 2026-09-13** (see the CHANGELOG entry). As planned, plus: the host answers 504 *and exits* on a timeout (an abandoned parse thread can only be reclaimed by ending the process); the request-body cap surfaces to callers as `DocumentRejected`; verified on the built image that an indexer with no parser library on disk indexes through the service and that `Parser__Mode=inprocess` in a container fails loudly. Not yet done from this phase's original list: nothing.

Original plan:
The host, `WebApplicationFactory` tests against the existing fixtures
(`tests/Mailvec.Mcp.Tests/Fixtures/*.pdf`, `Core.Tests/Parsing/Fixtures`),
the HTTP client, and a **contract test** that runs every fixture through both
implementations and asserts identical `ParsedMessage` records, extraction
results, page counts and image dimensions (JPEG bytes may differ per encoder
run; compare decoded dimensions, not bytes). The timeout-then-exit behaviour
and `MaxRequestsBeforeExit` tested at the host level. Dockerfile publish + the
strip-and-assert block; compose service and network; `.env.example`;
health/doctor reporting.

**Phase 3 — failure handling in the callers. Done 2026-09-15** (see the CHANGELOG entry and [the status doc](attachment-parser-isolation-status.md)). As planned, with these decisions made concrete: the scanner's crash strikes are keyed on the fast path's `(path, mtime, size)` and the degraded parse stamps every attachment `failed` (open question 5 below, resolved as written there); the OCR pass shares one strike ledger between vision and parser failures but settles each with its own health evidence; the CLI backfills stop with exit 1 on an outage and report crashes as `PARSER n`; `ScanResult` gained `Incomplete`. Nothing from the original list below was skipped.

Original plan:
The three classification tables above, with fakes: the OCR pass
(`Unavailable` never retires, `Crashed` counts only when the probe passes,
`DocumentRejected` retires with `PreProvider` provenance); the scanner
(`A_parser_outage_mid_scan_deletes_nothing`, the marker-then-retry path, the
three-strikes degrade to a text-less parse, and the existing
`An_unchanged_rescan_writes_nothing` family still green — the fast path must
still never call the parser); the MCP tools' two messages.

**Phase 4 — docs and the acceptance rewrite. Done 2026-09-15.** `docs/security.md` rewritten as below (the hardening section now opens with "exactly one process parses attacker-chosen bytes, and it holds nothing", the residual is stated in the acceptance, `parse` is the first non-root service and the indexer's row no longer says it parses; gVisor recorded as deferred with its trigger); `docs/monitoring-uptime-kuma.md` gained "The `parse` service is not on `/up`, by design" (its own healthcheck, a Docker Container monitor with restart-tolerant retries if paging is wanted). The CLAUDE.md invariants and the deploy-doc migration steps had already landed with phases 1–3. Release: proposed as `--patch`, not cut.

Original plan:
`docs/security.md`: the "native parsers" acceptance becomes "one process
parses untrusted bytes and holds nothing", with the residual above stated;
`parse` added to the hardening table as the first non-root service; the
indexer's row updated (it no longer parses). `docs/deploy-docker.md`: the
migration steps. `CLAUDE.md`: two invariants — *all parsing of mail content
goes through `IMailParser`; `Mailvec.Parsing` never references Core and Core
never references a parser package* — and *in the container, parser libraries
exist only under `/app/parse`; `Parser:Mode=inprocess` there is a loud failure
by design.* `CHANGELOG.md` entry. Then propose a `--patch` release and wait.

**Deferred, with triggers.**
- gVisor on `parse`. Trigger: the Docker VM gets gVisor for any other reason.
- Non-root UIDs for the other four services. `parse` is the proof the image
  runs as `nobody`; the others need a `chown` story for `./data` and `./mail`
  first.
- Persisting the crash-strike counter. Trigger: a poison file observed
  surviving restarts often enough that three-per-restart matters.

## Open questions

1. ~~**Docker VM kernel.**~~ Recorded 2026-09-13: Debian 13, kernel 6.12,
   cgroup v2, apparmor + builtin seccomp. Landlock network rules are
   available if the optional hardening is ever wanted.
2. **Bulk-ingest throughput.** The initial 82k-message ingest becomes 82k HTTP
   calls. A few ms each on the bridge network, and the scan is single-threaded
   anyway, so expected to be lost in the noise against PdfPig time — but
   measure it on a corpus copy in phase 2 before assuming. The steady state
   (unchanged corpus, mtime fast path) makes zero calls.
3. **`MaxRequestsBeforeExit` default.** A few hundred is a guess. Too low and
   the bulk ingest spends noticeable time restarting the service; too high and
   it stops meaning anything. Measure the restart cost in phase 2.
4. **`RequestTimeout` default.** Phase 0 put every legitimate fixture at
   25–750 ms and the first hostile one at 69 s, so 60 s has a measured floor.
   A too-low value turns big-but-honest scanned PDFs into `failed` text with
   nothing to re-trigger them, so err high; re-measure on the amd64 VM before
   lowering.
5. **Three strikes then text-less parse.** The degraded record indexes the
   message with attachments at `failed`, which `mailvec extract-attachments`
   never revisits (its default predicate is `extraction_status IS NULL`).
   That is the correct terminal state for a document the parser cannot
   survive, but it means an operator who later fixes the parser needs
   `--reextract-*` to pick them up. Acceptable; note it in the run summary
   the way `REFUSED n` is.
