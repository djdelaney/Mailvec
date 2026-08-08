# Review note — embedding providers phases 0–2

**Reviewed:** 2026-08-08  
**Proposal:** [`docs/proposals/embedding-providers.md`](docs/proposals/embedding-providers.md)  
**Implementation reviewed through:** `5a45148` (`Add the purpose-aware embedding service, classified failures, neutral probe`)

## Overall assessment

Phase 1's database and write-side identity protection is strong. The migration,
config-hash provenance rules, transactional chunk-write guard, atomic
`switch-model` identity rewrite, and Ollama artifact-digest observation directly
address the repository's silent-corruption risks.

Phase 2 delivers centralized profile resolution, shared registration, a
purpose-aware consumer API, classified failures, and a provider-neutral probe.
The existing Ollama behavior remains covered by the full test suite. However,
phase 2 has not yet reached the complete service/transport boundary described by
the proposal, and the semantic-search read path still lacks a mandatory
embedding-space compatibility check.

Before beginning or merging phase 3, address the two priority-1 findings below
and settle the remaining phase-2 text-policy and transport contract. This is
particularly valuable before schema v11 is released, while the config-hash
format can still be completed without creating another migration or requiring
an avoidable vector rebuild.

## Findings

### P1 — semantic search does not enforce embedding-space identity

`VectorSearchService.SearchAsync` embeds the query and immediately runs KNN:

- [`src/Mailvec.Core/Search/VectorSearchService.cs`](src/Mailvec.Core/Search/VectorSearchService.cs#L58)
- [`src/Mailvec.Core/Embedding/EmbeddingService.cs`](src/Mailvec.Core/Embedding/EmbeddingService.cs#L22)

Neither path compares the active profile with the database's stored model,
dimensions, `embedding_space_id`, `embedding_config_hash`, or observed model
artifact digest before producing semantic results.

The write side is protected: the embedder refuses a mismatch, health degrades,
and transactional writes are abandoned. The read side is not. For example,
editing `Ollama:QueryInstructionPrefix` changes query vectors without changing
their dimensions. MCP and CLI semantic/hybrid search can therefore return
plausible but meaningless rankings against document vectors created under the
old prefix. A repulled same-dimension Ollama tag has the same failure mode.

The proposal's phase-1 enforcement list also omits query-time enforcement, so
this requires both a design clarification and an implementation change.

Recommended resolution:

1. Add one reusable embedding-space compatibility verifier over stored metadata
   and the resolved profile.
2. Invoke it before query embedding/KNN and before document writes.
3. Include artifact-digest or hosted-sentinel verification, with sensible
   caching so interactive search does not add an unbounded network round trip.
4. Throw a classified, sanitized exception so MCP can recommend keyword mode
   instead of returning invalid semantic results or a generic SDK error.
5. Add semantic and hybrid tests proving that model, config-hash, space, digest,
   and future sentinel drift all refuse the vector leg.

### P1 — Ollama digest lookup can select the wrong tag

`GetModelDigestAsync` uses the first `/api/tags` item accepted by `Matches`:

- [`src/Mailvec.Core/Ollama/OllamaModelProbe.cs`](src/Mailvec.Core/Ollama/OllamaModelProbe.cs#L42)

For tagless configuration, `Matches` accepts `name:latest` but also any tag with
the same base name. If both `model:old` and `model:latest` are installed, the
array's ordering determines which digest is stamped. This can:

- report false drift when list ordering changes;
- keep comparing an unused tag while `:latest` changes, missing real drift; or
- stamp a digest that never produced the stored vectors.

Recommended resolution:

- Resolve an explicitly tagged model by exact tag.
- Resolve a tagless model as exact name, then `name:latest`.
- Do not fall back to an arbitrary tag for artifact identity.
- Retain broad same-base matching only for the weaker model-availability
  diagnostic if that compatibility behavior is still desired.
- Add a test where `model:old` precedes `model:latest` and assert that the latest
  digest wins.

### P2 — phase 2 stops short of the proposed service/transport boundary

The proposal says `IEmbeddingService` owns purpose transforms, batching,
normalization, output validation, and embedding-space checks, while a transport
only serializes and classifies one protocol request.

The current implementation instead has:

- `IEmbeddingClient` serving as the transport abstraction rather than the
  proposed `IEmbeddingTransport`;
- batching remaining in
  [`EmbeddingWorker`](src/Mailvec.Embedder/Services/EmbeddingWorker.cs#L556);
- normalization and output validation remaining in
  [`OllamaClient`](src/Mailvec.Core/Ollama/OllamaClient.cs#L227);
- no space-identity check in
  [`EmbeddingService`](src/Mailvec.Core/Embedding/EmbeddingService.cs);
- only query/document prefixes represented by `ResolvedEmbeddingProfile`; and
- query/document suffixes and document prefixes still rejected by
  [`EmbeddingRegistration`](src/Mailvec.Core/Embedding/EmbeddingRegistration.cs#L189).

This is behavior-compatible for today's Ollama configuration, but it leaves the
shared semantics likely to drift when the OpenAI-compatible transport is added.
It also means the config hash currently covers prefixes only even though the
proposal defines all four text transforms as vector-affecting.

Recommended resolution before phase 3:

1. Decide whether the code will follow the proposed service/transport split or
   revise the proposal to make validation/normalization a transport contract.
2. Carry query prefix/suffix and document prefix/suffix in the resolved profile.
3. Apply all four transforms centrally and exactly once.
4. Include all four in the canonical config hash and its tests.
5. Decide where provider-aware batching lives and ensure every caller receives
   the same enforcement, not only `EmbeddingWorker`.

### P3 — the proposal's status and current-state sections are stale

The proposal still says `proposed; not implemented`, while phases 0–2 have
landed. Its "Current call graph" says consumers depend on `IEmbeddingClient` and
each executable separately registers `OllamaClient`; neither statement remains
true. Decision 3 also remains presented as unresolved even though the options
and registration code already adopt explicit hosted `SpaceId` as the policy.

Phase 0's wording says to capture against the frozen corpus, while the new
baseline is explicitly the 662-message subset OCR corpus. The baseline README
documents this honestly and records that its current file came from a
working-tree v11 build, with an A/B against pre-phase main reported as
bit-identical:

- [`baselines/subset-ocr/README.md`](baselines/subset-ocr/README.md#L24)

Recommended documentation update:

- mark phases 0, 1, and the completed portion of 2 with commit IDs and dates;
- distinguish "pre-implementation call graph" from current architecture;
- mark decision 3 as decided;
- name the actual phase-0 corpus and baseline artifact;
- record the phase-2 post-refactor eval result or durable comparison output; and
- make the partial service/transport work explicit until the P2 finding above is
  resolved.

## Phase assessment

### Phase 0 — baseline capture

Substantially complete, with a documented procedural deviation.

- A 70-query subset baseline is committed at
  [`baselines/subset-ocr/2026-08-07.json`](baselines/subset-ocr/2026-08-07.json).
- The README records corpus identity, binary provenance, and the fact that the
  subset family cannot be compared with full-corpus baselines.
- The current baseline was captured with working-tree v11 code rather than
  strictly before the phase-1 implementation, but the README records an A/B
  against `5baaf6d` as bit-identical.
- Phase-2 commits state that the eval remained bit-identical, but no separate
  post-phase comparison artifact is committed.

### Phase 1 — embedding-space identity

Complete on the intended Ollama write/diagnostic paths, subject to the read-side
P1 finding.

Implemented strengths:

- schema v11 adds `embedding_space_id` and `embedding_config_hash`;
- migration 011 derives legacy identity from database metadata, not current
  configuration;
- code-side hash stamping occurs only when configuration agrees with stored
  identity and never overwrites an existing stamp;
- fresh schema, migration, worker verification, health, doctor, status,
  `switch-model`, and transactional chunk writes carry the new identity;
- `switch-model` rewrites model, dimensions, space ID, and config hash together;
- the prior artifact digest is cleared in that same transaction;
- Ollama `/api/tags` digest observation is stamped once and refuses later drift;
  and
- null/unobservable digest remains unknown rather than becoming a false
  mismatch.

### Phase 2 — profiles, registration, and service split

Functionally useful and Ollama-compatible, but only partially complete against
the proposal's final abstraction.

Implemented strengths:

- named `Embedding:*` profiles with a legacy Ollama resolver;
- fatal unknown-protocol and missing-profile validation;
- centralized registration in Embedder, MCP, and CLI;
- preserved interactive versus background HTTP timeout/resilience roles;
- `IEmbeddingService` adopted by production consumers;
- query-prefix application moved out of `VectorSearchService`;
- document embedding routed through the purpose-aware method;
- provider-neutral classified readiness probe;
- classified `EmbeddingException` mapping; and
- provider-wide backpressure/auth/model failures excluded from message
  quarantine strikes.

Remaining work is described in the P1 and P2 findings above.

## Verification performed

Read-only review covered the proposal, phase commits, schema and migration,
identity implementation, registration and profile resolution, embedding
service, Ollama transport, worker isolation and write guards, health, CLI
commands, MCP search translation, and the associated tests.

The complete repository test suite passed on 2026-08-08:

| Test project | Passed | Failed |
|---|---:|---:|
| `Mailvec.Core.Tests` | 654 | 0 |
| `Mailvec.Mcp.Tests` | 208 | 0 |
| `Mailvec.Cli.Tests` | 240 | 0 |
| `Mailvec.Embedder.Tests` | 84 | 0 |
| `Mailvec.Indexer.Tests` | 41 | 0 |
| **Total** | **1,227** | **0** |

The worktree was clean at review time. No launchd Mailvec jobs or
`com.mailvec.*.plist` files were present, and the frozen corpus was not mutated.

## Recommended gate before phase 3

Do not treat phase 2 as closed until:

1. semantic/hybrid search refuses every known form of embedding-space drift;
2. Ollama digest lookup identifies the artifact actually selected by an embed
   request;
3. the ownership of batching, normalization, validation, and identity checking
   is made consistent between the proposal and code;
4. the full query/document prefix/suffix policy is either implemented and
   hashed or explicitly deferred in both code and proposal; and
5. the proposal is updated with actual phase status, decisions, baseline
   provenance, and commit references.


## Resolution (2026-08-08)

Addressed in the post-review hardening commit on `main`:

- **P1 read-side enforcement** — `EmbeddingSpaceGuard` refuses semantic and
  hybrid search before query embedding/KNN on any model/dimensions/space-id/
  config-hash disagreement, throwing the new classified `SpaceMismatch` kind;
  the MCP translation recommends `mode=keyword` with revert/`switch-model`
  remediation. Metadata-only per query, per the availability rationale now
  recorded in the proposal's enforcement-points list; digest/sentinel checks
  stay on the embedder//health cadence. Tests:
  `VectorSearchServiceTests.Semantic_search_refuses_every_form_of_metadata_identity_drift`,
  `Hybrid_search_refuses_through_its_vector_leg`,
  `Absent_identity_metadata_is_unknown_and_passes_the_guard`.
- **P1 digest tag resolution** — `GetModelDigestAsync` resolves exact tag,
  then `name:latest` for tagless config; no arbitrary same-base fallback.
  Broad matching is retained only for the availability diagnostic. Test:
  `OllamaClientTests.Digest_resolves_latest_never_an_arbitrary_same_base_tag`.
- **P2 transforms + hash coverage** — all four query/document
  prefix/suffix transforms are carried by `ResolvedEmbeddingProfile`, applied
  exactly once in `EmbeddingService`, and covered by the canonical config
  hash (serialization bumped v1→v2 inside the pre-release window; the only
  v11 databases were development machines, which self-heal by deleting the
  stored key — vectors unaffected since every added field was empty).
  Identity consumers compute from the profile (`EmbeddingSpace.ForProfile`),
  never from `OllamaOptions`, so profile-only transforms cannot be silently
  ignored. Boundary ownership (validation/normalization moving up into the
  service at transport extraction) is recorded in the proposal for phase 3.
- **P3 documentation** — proposal status, pre-implementation call graph
  marking, decision 3 recorded as decided, phase commit references, and the
  phase-0 subset-corpus deviation are all updated in
  `docs/proposals/embedding-providers.md`.
