# Review note — embedding providers phase 3

**Reviewed:** 2026-08-08  
**Proposal:** [`docs/proposals/embedding-providers.md`](docs/proposals/embedding-providers.md)  
**Implementation reviewed through:** `c22635a` (`Add sentinel fingerprints — the stability hybrid's hosted half`)

## Overall assessment

Phase 3 establishes a sound provider-neutral transport boundary. The shared
OpenAI-compatible transport has explicit capability policies instead of
vendor branches, bearer credentials stay out of the displayable profile,
redirects are disabled, response indexes are validated and reordered, upstream
bodies are sanitized, and the provider-independent mathematical contract now
lives in `EmbeddingService`. The existing Ollama path remains green.

The phase should not yet be considered complete, however. The normal database
lifecycle cannot establish the asserted identity of a hosted profile: both
fresh-schema creation and `mailvec switch-model` still stamp an Ollama-derived
space. Even after that is fixed, known sentinel drift stops new vector writes
but is not communicated to the semantic read guard. A readiness-contract bug
can also turn a provider-wide response-shape failure into quarantine evidence
against valid messages.

There are also material proposal gaps: hosted usage/rate-limit telemetry is not
represented, interactive requests have no bounded retry policy, and the
context-overflow fallback remains deliberately unimplemented without being
recorded as an as-built deviation. The current green suite does not exercise a
fresh hosted database, an Ollama-to-hosted switch, query refusal after sentinel
drift, or the full readiness mathematical contract.

## Findings

### P1 — hosted profiles cannot create or migrate a usable database

Fresh-schema creation still sources its model and dimensions from
`OllamaOptions` and substitutes an Ollama-derived space ID:

- [`src/Mailvec.Core/Data/SchemaMigrator.cs`](src/Mailvec.Core/Data/SchemaMigrator.cs#L98)
- [`src/Mailvec.Core/Data/SchemaMigrator.cs`](src/Mailvec.Core/Data/SchemaMigrator.cs#L148)
- [`schema/001_initial.sql`](schema/001_initial.sql#L204)

This remains true even when `SchemaMigrator` receives a hosted
`ResolvedEmbeddingProfile`. `StampConfigHashIfMissing` first compares stored
model/dimensions with `OllamaOptions`, then declines to stamp when the stored
Ollama space differs from the profile's asserted hosted `SpaceId`. The indexer
does not register an embedding profile at all, so it has no provider-neutral
identity available if it is the first process to create the database.

The sanctioned migration path has the same failure independently:

- [`src/Mailvec.Core/Data/SchemaMigrator.cs`](src/Mailvec.Core/Data/SchemaMigrator.cs#L440)
- [`src/Mailvec.Cli/Commands/SwitchModelCommand.cs`](src/Mailvec.Cli/Commands/SwitchModelCommand.cs#L47)

`SwitchEmbeddingModel` always calls `EmbeddingSpace.LegacySpaceId(model,
dimensions)`. With a Fireworks profile active, for example, the CLI defaults
the target model and dimensions from that profile but writes a space such as
`ollama:accounts/fireworks/models/qwen3-embedding-8b:1024`. On startup the
embedder computes the profile's asserted `fireworks:...` space, sees the
mismatch, and correctly refuses to write. The config hash is also computed from
the wrong space ID.

The live E2E recorded in the proposal only proves the negative path: a
Fireworks profile is refused against an existing Ollama database before a query
leaves the machine. It does not prove that the sanctioned hosted migration can
produce a database the embedder accepts.

Recommended resolution:

1. Make fresh-schema identity and vec0 dimensions derive from a resolved,
   non-secret embedding profile in every process that can create the schema,
   including the indexer.
2. Make `switch-model` atomically stamp the selected profile's wire model,
   dimensions, asserted `SpaceId`, and full config hash.
3. Define hosted behavior for `--model` / `--dims` overrides. Either reject
   overrides that no longer describe the active profile or require a complete
   target identity rather than silently retaining/deriving the wrong space.
4. Add lifecycle tests for a fresh hosted database and an Ollama-to-hosted
   forced switch, followed by the actual embedder/read guards.

### P1 — known sentinel drift does not stop semantic search

The hosted worker correctly re-embeds fixed non-mail sentinels and throws when
their cosine similarity falls below the measured threshold:

- [`src/Mailvec.Embedder/Services/EmbeddingWorker.cs`](src/Mailvec.Embedder/Services/EmbeddingWorker.cs#L767)

That exception stops the worker's current and later write cycles, but no
known-drift state is persisted. The semantic read guard remains metadata-only
and checks only model, dimensions, space ID, and config hash:

- [`src/Mailvec.Core/Embedding/EmbeddingSpaceGuard.cs`](src/Mailvec.Core/Embedding/EmbeddingSpaceGuard.cs#L26)

Those values remain unchanged when a hosted provider silently changes weights
behind a stable alias. MCP and CLI can therefore continue embedding queries
with the changed function and rank them against old document vectors. The
results look plausible but are mathematically invalid. This contradicts the
phase-3 requirement that detected sentinel drift refuse both writes and
semantic search.

The fix does not require a network probe on the search hot path. Persist a
versioned "known sentinel drift" observation when the worker detects drift;
have `EmbeddingSpaceGuard` refuse that state; and clear the marker in the same
`switch-model` transaction that clears sentinel fingerprints. Provider
unreachability should remain unknown and must not set the marker.

Add an end-to-end invariant test proving that detected drift blocks semantic
and hybrid search while keyword mode remains available.

### P1 — readiness bypasses the shared mathematical contract

`EmbeddingService.ProbeAsync` calls the raw transport and reports `Available`
when the first returned vector is merely nonempty:

- [`src/Mailvec.Core/Embedding/EmbeddingService.cs`](src/Mailvec.Core/Embedding/EmbeddingService.cs#L76)

It does not run `ValidateAndNormalize`, so a response with the wrong vector
count, wrong width, NaN, or infinity can pass readiness even though every real
query/document embedding is rejected as `InvalidResponse`.

This is more than a misleading health result. Isolation mode calls this probe
when every candidate failed. A provider-wide width or finiteness regression can
therefore be judged healthy; the worker then treats the failures as
message-specific and accrues quarantine strikes against valid mail.

Run the readiness vector through the same count/width/finiteness contract with
an expected count of one before returning `Available`. Add probe tests for
wrong count, wrong dimensions, and non-finite values, plus an isolation test
proving those provider-wide failures never quarantine a message.

### P2 — sentinel checks run per batch during a drain

The worker invokes artifact and sentinel verification at the top of every loop
iteration:

- [`src/Mailvec.Embedder/Services/EmbeddingWorker.cs`](src/Mailvec.Embedder/Services/EmbeddingWorker.cs#L114)

When a batch processes work, the loop does not wait for `PollInterval`; it
immediately begins the next batch. The sentinel request is consequently made
once per batch, despite comments describing it as once per poll cycle. With a
16-message batch size, rebuilding 75,000 messages adds roughly 4,700 extra
hosted requests and 18,800 sentinel input embeddings before accounting for the
normal document calls. This increases cost, consumes rate-limit capacity, and
can delay or destabilize the rebuild the sentinels are intended to protect.

Run remote stability checks once before the first write, then on a real
time-based cadence independent of backlog-drain iterations. Preserve the local
transactional identity check on every write/batch.

### P2 — provider-neutral usage and rate-limit telemetry is absent

The proposal requires optional input-token usage, response model, request ID,
and rate-limit observations. The implementation currently returns only raw
vectors from `IEmbeddingTransport`:

- [`src/Mailvec.Core/Embedding/IEmbeddingTransport.cs`](src/Mailvec.Core/Embedding/IEmbeddingTransport.cs#L12)
- [`src/Mailvec.Core/Embedding/OpenAiCompatibleTransport.cs`](src/Mailvec.Core/Embedding/OpenAiCompatibleTransport.cs#L53)

`OpenAiCompatibleTransport` defines `EmbedResponse.Model` but discards it,
does not deserialize `usage`, and never reads request-ID or rate-limit headers.
The test fixture includes a `usage` object without asserting it because no
telemetry value exists to receive it.

Introduce a provider-neutral batch result or telemetry observer that keeps
these fields optional and never includes mail content or credentials. Add stub
tests for present, partial, and absent telemetry before relying on hosted cost
or throttling audits.

### P2 — interactive hosted requests do not retry transient failures

The standard resilience handler is registered only for
`BackgroundIngestion`:

- [`src/Mailvec.Core/Embedding/EmbeddingRegistration.cs`](src/Mailvec.Core/Embedding/EmbeddingRegistration.cs#L111)

MCP and CLI requests therefore fail immediately on 429, 503, and transient 5xx
responses and do not honor `Retry-After`. This differs from the proposal's
requirement that resilience be consistent across embedder, MCP, and CLI with a
tighter total budget for interactive requests, not no resilience at all.

Add a small interactive retry/total-time budget appropriate to a waiting user,
and exercise both policies against stub handlers. Tests should prove
`Retry-After` handling, bounded attempts, no retry for auth/config/model errors,
and no request storm under sustained backpressure.

### P3 — phase status omits an intentional context-overflow deviation

The proposal's transport test plan requires confirmed context overflow to use
split/progressive-truncation fallback. `OpenAiCompatibleTransport` instead
classifies a suspected length-related 400 as `InputTooLong` and explicitly
declines to split or truncate because current chunks are much smaller than the
hosted model's context window:

- [`src/Mailvec.Core/Embedding/OpenAiCompatibleTransport.cs`](src/Mailvec.Core/Embedding/OpenAiCompatibleTransport.cs#L107)

That may be a defensible operational choice, but it is not the behavior the
proposal marks complete. Either implement and test the fallback or record it as
an accepted as-built deviation alongside the sentinel storage/stamping
deviations. The current positive identification is also broad substring
matching (`context`, `maximum length`, `too long`, `max_tokens`); if it remains,
structured provider errors or stricter patterns would better support the claim
that unrelated 400s are never misclassified.

## Implemented strengths

The findings above do not diminish several strong parts of the phase:

- Fireworks, OpenAI, Baseten-style, and custom endpoints are profiles over one
  `openai-compatible` protocol rather than vendor subclasses.
- Hosted profiles must assert a `SpaceId`; it is never inferred from provider
  or wire-model aliases.
- Required/placeholder/omitted model and send/omit dimensions policies are
  explicit and validated.
- The exact full endpoint is used; HTTPS is required except for loopback test
  servers; redirects are disabled before bearer credentials or mail can move.
- API keys are resolved once, stay outside `ResolvedEmbeddingProfile`, and are
  fatal when bearer auth is incomplete.
- Response array order is not trusted; count, index range, and uniqueness are
  checked before vectors are reassembled.
- Provider response bodies are excluded from ordinary exception messages.
- Vector count, width, finiteness, and normalization are centralized in
  `EmbeddingService` for both transports on the normal query/document paths.
- All four query/document prefix/suffix transforms are applied centrally and
  included in the canonical config hash.
- Classified provider-wide failures do not count as poison-message evidence.
- Hosted sentinels use fixed non-mail inputs and a documented threshold derived
  from measured Fireworks stability.
- Sentinel observations are cleared in the same transaction as a model switch.
- The existing Ollama behavior and retrieval baseline remain reported as
  unchanged.

## Verification performed

The review covered the updated proposal and the Phase 3 commits, profile
resolution and registration, both transports, the purpose-aware service,
schema creation and `switch-model`, embedder write/isolation paths, sentinel
storage and comparison, query-time space enforcement, health integration, CLI
flow, and the associated tests.

Build verification on 2026-08-08:

```text
dotnet build --no-restore --nologo -m:1 -nr:false
Build succeeded. 0 warnings, 0 errors.
```

The complete repository test suite passed:

| Test project | Passed | Failed |
|---|---:|---:|
| `Mailvec.Core.Tests` | 671 | 0 |
| `Mailvec.Mcp.Tests` | 208 | 0 |
| `Mailvec.Cli.Tests` | 240 | 0 |
| `Mailvec.Embedder.Tests` | 88 | 0 |
| `Mailvec.Indexer.Tests` | 41 | 0 |
| **Total** | **1,248** | **0** |

The worktree was clean before this review note was added. No launchd Mailvec
jobs or `com.mailvec.*.plist` files were present, and the frozen corpus was not
mutated. The review made no live hosted-provider calls.

## Recommended gate before phase 4

Do not treat phase 3 as closed until:

1. fresh-schema creation and `switch-model` can stamp a complete hosted
   profile identity that the embedder and semantic read guard accept;
2. known sentinel drift is persisted and blocks semantic/hybrid reads as well
   as vector writes;
3. readiness enforces the same mathematical contract as real embeddings and
   cannot turn provider-wide shape failures into message quarantine;
4. sentinel verification runs on a true bounded cadence rather than once per
   backlog batch;
5. the required usage, response-model, request-ID, and rate-limit telemetry is
   represented and tested;
6. interactive retry behavior is bounded, honors backpressure, and is covered
   by stub tests; and
7. the proposal either gains its context-overflow fallback or explicitly
   records the implementation's no-fallback decision.

## Resolution (2026-08-08)

All seven gate items addressed in the post-review hardening commit:

1. **Hosted lifecycle** — fresh-schema creation and `switch-model` stamp the
   resolved profile's identity (`SchemaMigrator.FreshIdentity`, profile-aware
   `SwitchEmbeddingModel`); the indexer registers credential-free identity
   (`AddMailvecEmbeddingIdentity` — resolution never touches key material);
   hosted `--model`/`--dims` overrides diverging from the profile are
   rejected. Tests: `EmbeddingSpaceIdentityTests`
   (`A_fresh_database_created_under_a_hosted_profile_stamps_the_asserted_identity`,
   `An_ollama_to_hosted_switch_stamps_the_profile_identity_the_guards_accept`).
2. **Drift blocks reads** — detected drift persists
   `embedding_sentinel_v1.drift_detected_at`; `EmbeddingSpaceGuard` refuses it
   (classified `SpaceMismatch`); cleared by `switch-model`'s sentinel
   LIKE-clear or automatically on a healthy re-observation (safe: the drift
   itself stopped all writes). Never set on unreachability.
3. **Probe contract** — `ProbeAsync` runs `ValidateAndNormalize` on the
   readiness vector; a provider-wide shape regression now reads as an outage
   and quarantines nothing
   (`A_provider_wide_shape_regression_never_quarantines_messages`).
4. **Bounded cadence** — `RunRemoteStabilityChecksIfDueAsync` gates digest +
   sentinel checks to once per five minutes regardless of drain batching,
   always before the first write; refusals retry immediately by design.
5. **Telemetry** — `EmbeddingTelemetry` + `IEmbeddingTelemetryObserver`
   (Debug-logging sink registered by default); usage, response model,
   request id, rate-limit headroom; all optional, observer failures never
   fail the embed.
6. **Interactive retry** — hosted Interactive role gets one retry within a
   ~10s budget honoring Retry-After (verified end-to-end against a loopback
   listener); auth/model errors fail fast; Ollama interactive deliberately
   keeps no retry pipeline.
7. **Deviation recorded** — the proposal now documents the no-fallback
   context-overflow decision and its substring identification as an accepted
   as-built deviation.
