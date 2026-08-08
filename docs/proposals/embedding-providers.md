# Design proposal — pluggable embedding providers (Fireworks first)

**Status:** phases 0–3 implemented (see "Phased implementation" for commit
references); phases 4+ proposed.  
**Date:** 2026-08-07; phase status updated 2026-08-08.  
**Default remains:** local Ollama. Hosted providers are explicit opt-ins because
they send mail content and semantic-search queries off-network.

## Summary

Make text embeddings pluggable through two defined protocols: Ollama and the
OpenAI-compatible embeddings API. Add Fireworks.ai as the first hosted profile;
OpenAI, Baseten BEI, and compatible custom deployments then use the same
transport rather than acquiring near-duplicate clients. Provider selection
remains independent from the OCR vision provider. The document-embedding
worker, MCP query embeddings, CLI search/eval, readiness checks, and model
migration must all resolve the same configured embedding profile.

This is not a base-URL substitution between Ollama and a hosted API. Ollama and
OpenAI-compatible embeddings use different paths, authentication, request
fields, response shapes, model diagnostics, and failure semantics. Within the
OpenAI-compatible family, vendors still differ in endpoint shape,
authentication, required request fields, fixed versus request-selectable
dimensions, and what the wire `model` value means. The HTTP adapters are small;
the production work is separating protocol, provider/deployment identity, and
mathematical embedding-space identity while keeping credentials, health
reporting, retries, and database guards honest.

Recommended first target:

- Fireworks serverless `accounts/fireworks/models/qwen3-embedding-8b`.
- Explicitly request 1024 output dimensions.
- Keep the existing query-only Qwen instruction prefix.
- Preserve Ollama as the default and as a separately selectable OCR provider.

Estimated effort is one focused day for a Fireworks proof of concept and five to
eight focused engineering days for the provider-agnostic production
implementation, tests, deployment wiring, documentation, and evaluation. A
schema migration would make this a minor release under the repository's release
policy; no release is authorized by this proposal.

The work lands as ordered, independently reviewable phases (see "Phased
implementation"). The embedding-space-identity migration is phase 1 and stands
on its own: it hardens the existing Ollama-only deployment against silent
vector-space drift whether or not a hosted provider ever ships.

## Goals

1. Allow Ollama or an OpenAI-compatible endpoint to provide all text embeddings.
2. Make OpenAI, Fireworks, Baseten BEI, and a custom OpenAI-compatible deployment
   configuration profiles over one hosted transport, not separate clients.
3. Use the same provider profile, embedding-space identity, dimensions, and text
   formatting in the embedder, MCP server, and CLI/eval paths.
4. Preserve the invariant that one database contains exactly one embedding
   space.
5. Fail loudly and diagnostically on protocol, provider, deployment, model,
   credential, dimension, or response-shape errors without leaking mail content
   into logs or MCP errors.
6. Handle hosted-service rate limiting and overload without quarantining valid
   mail as poison input.
7. Keep existing Ollama installations working without an immediate config
   migration.
8. Keep OCR provider selection independent from embedding provider selection.
9. Capture hosted usage and rate-limit telemetry without making it mandatory for
   providers that do not return it.

## Non-goals

- Replacing attachment OCR with a hosted embedding provider. The existing
  `IVisionClient` path remains Ollama or Mistral.
- Adding reranking in the first implementation. Some providers expose
  rerankers, but reranking changes retrieval architecture, latency, cost, and
  eval expectations; it deserves a separate proposal.
- Changing chunk size or overlap as part of the provider change. Model,
  dimension, and provider should be evaluated before adding another variable.
- Making Fireworks the default.
- Mutating the frozen corpus in place during development or evaluation.
- Supporting arbitrary configurable JSON request/response templates or arbitrary
  custom headers. A materially different API gets a typed protocol adapter;
  configuration must not become a miniature HTTP-programming language.
- Supporting arbitrary Baseten Truss `/predict` schemas. Baseten BEI's
  OpenAI-compatible embeddings route is in scope; custom prediction contracts
  are separate protocols.

## Pre-implementation call graph (historical)

This section describes the architecture BEFORE phase 2 landed and is kept as
design rationale. As of `5a45148` consumers depend on `IEmbeddingService`,
registration is centralized in `EmbeddingRegistration.AddMailvecEmbedding`,
and the read path is guarded by `EmbeddingSpaceGuard`.

There were two distinct uses of Ollama:

| Caller | Operation | Purpose | Pluggable design |
|---|---|---|---|
| `Mailvec.Embedder` | `POST /api/embed` | Embed document and attachment chunks | Replaceable |
| `Mailvec.Mcp` | `POST /api/embed` | Embed semantic/hybrid search queries | Must switch with the embedder |
| `Mailvec.Cli` | `POST /api/embed` | Search, eval, doctor, and model experiments | Must switch with the embedder |
| MCP/CLI health | Real embed, then `GET /api/tags` on failure | Readiness and missing-model diagnosis | Provider-neutral probe with optional protocol diagnostics |
| Embedder OCR | `POST /api/generate` with images | OCR scanned PDFs and image attachments | Unchanged |

The indexer does not call Ollama.

The consumer seam is already in the right place. `EmbeddingWorker`,
`VectorSearchService`, `HybridSearchService`, and `HealthService` depend on
`IEmbeddingClient`. The coupling that remains is outside the main embed call:

- `OllamaOptions` owns generic embedding settings as well as Ollama-specific
  settings.
- Each executable separately registers `OllamaClient`.
- `IEmbeddingClient.IsModelAvailableAsync` assumes a locally pulled model.
- `/health`, `/up`, doctor, status, installer output, and MCP recovery hints all
  name Ollama.
- database metadata records model and dimensions, but not protocol, deployment,
  text transforms, or an immutable embedding-space identity.

## Supported protocol families

The implementation should support protocols, not one class per vendor.
Provider names select configuration defaults and diagnostics; they do not define
the wire contract.

### Ollama

```http
POST /api/embed
Content-Type: application/json

{
  "model": "mxbai-embed-large",
  "input": ["..."],
  "keep_alive": "30m",
  "truncate": true
}
```

```json
{
  "model": "mxbai-embed-large",
  "embeddings": [[0.1, 0.2]]
}
```

### OpenAI-compatible embeddings

```http
POST https://api.fireworks.ai/inference/v1/embeddings
Authorization: Bearer <key>
Content-Type: application/json

{
  "model": "accounts/fireworks/models/qwen3-embedding-8b",
  "input": ["..."],
  "dimensions": 1024
}
```

```json
{
  "object": "list",
  "model": "accounts/fireworks/models/qwen3-embedding-8b",
  "data": [
    { "object": "embedding", "index": 0, "embedding": [0.1, 0.2] }
  ],
  "usage": { "prompt_tokens": 123, "total_tokens": 123 }
}
```

Verified live 2026-08-07 with a test key: the wire model requires the full
`accounts/fireworks/models/...` path (the short form 404s), `dimensions:
1024` is honored, usage is reported, and **returned vectors are not
L2-normalized** (observed norm ~65) — the mandatory
`NormalizeInPlaceIfNeeded` pass is load-bearing for this provider.

Fireworks documents this surface as OpenAI-compatible. It accepts a string or
array of strings, supports an optional `dimensions` parameter on compatible
models, and authenticates with a bearer API key.

OpenAI, Fireworks, and Baseten BEI share the `POST /v1/embeddings`-style request
and indexed `data[].embedding` response envelope. They are not identical in all
operational details:

| Profile | Endpoint shape | Wire `model` | `dimensions` | Identity concern |
|---|---|---|---|---|
| OpenAI | Fixed account API endpoint | Required model ID | Supported only by models that expose it | Public model plus provider revision behavior |
| Fireworks | Fixed serverless or deployment endpoint | Required model/deployment ID | Supported by compatible embedding models | Serverless alias may move |
| Baseten BEI | Deployment/environment-specific base URL | May be a required placeholder such as `not-required` | Model/deployment dependent | Actual deployment, checkpoint, pooling, and quantization are outside the wire value |
| Custom compatible | Operator-supplied full URL | Required, placeholder, or omitted by declared policy | Sent or omitted by declared policy | Must be supplied explicitly |

Those variations belong in a small capability policy attached to a profile.
They do not justify separate OpenAI, Fireworks, and Baseten HTTP clients.
Baseten's arbitrary Truss `/predict` contracts are not OpenAI-compatible and
would require a separate typed protocol adapter.

References, verified 2026-08-07:

- [OpenAI create-embeddings API reference](https://developers.openai.com/api/reference/resources/embeddings/methods/create)
- [OpenAI `text-embedding-3-large` model reference](https://developers.openai.com/api/docs/models/text-embedding-3-large)
- [Fireworks embeddings and reranking guide](https://docs.fireworks.ai/guides/querying-embeddings-models)
- [Fireworks create-embeddings API reference](https://docs.fireworks.ai/api-reference/creates-an-embedding-vector-representing-the-input-text)
- [Fireworks serverless rate limits](https://docs.fireworks.ai/serverless/rate-limits)
- [Fireworks serverless lifecycle and data-handling overview](https://docs.fireworks.ai/serverless/overview)
- [Baseten Embedding Inference overview](https://docs.baseten.co/engines/bei/overview)
- [Baseten inference API overview](https://docs.baseten.co/inference/overview)
- [Qwen3 Embedding 8B model card](https://huggingface.co/Qwen/Qwen3-Embedding-8B)

## Proposed configuration

Introduce named embedding profiles. Separate the protocol from the diagnostic
provider ID, use a full endpoint URL, and make the embedding-space ID explicit:

```json
{
  "Embedding": {
    "ActiveProfile": "fireworks-qwen",
    "Profiles": {
      "fireworks-qwen": {
        "Protocol": "openai-compatible",
        "ProviderId": "fireworks",
        "Endpoint": "https://api.fireworks.ai/inference/v1/embeddings",
        "Request": {
          "Model": "accounts/fireworks/models/qwen3-embedding-8b",
          "ModelParameter": "required",
          "DimensionsParameter": "send",
          "EncodingFormat": "float"
        },
        "OutputDimensions": 1024,
        "SpaceId": "fireworks:qwen3-embedding-8b:1024:<revision-or-fingerprint>",
        "Text": {
          "QueryPrefix": "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery: ",
          "QuerySuffix": "",
          "DocumentPrefix": "",
          "DocumentSuffix": ""
        },
        "Auth": {
          "Scheme": "bearer",
          "ApiKeyFile": "/run/secrets/mailvec_embedding_api_key"
        },
        "MaxBatchSize": 16,
        "RequestTimeoutSeconds": 60,
        "MaxRetries": 3
      }
    }
  }
}
```

The policy values are deliberately narrow:

- `Protocol` selects a typed Ollama or OpenAI-compatible serializer.
- `ProviderId` is a stable diagnostic and telemetry label; it must not change
  request serialization or stand in for `SpaceId`.
- `Endpoint` is the complete embeddings URL. This accommodates fixed OpenAI and
  Fireworks routes as well as Baseten deployment/environment URLs without
  fragile base-address path composition.
- `ModelParameter` is `required`, `placeholder`, or `omit`. A placeholder still
  emits `Request:Model` but declares that it is not the served model's identity.
- `DimensionsParameter` is `send` or `omit`. `OutputDimensions` is always
  required and always validated, even when dimensions cannot be requested.
- `Auth:Scheme` initially permits only `none` or `bearer`. New auth behavior is
  code, not arbitrary configured headers.
- `SpaceId` identifies vector compatibility and is intentionally distinct from
  the model string sent over the wire.

OpenAI, Fireworks, and Baseten are documented profile examples over
`openai-compatible`. A custom compatible endpoint uses the same schema.
Provider-specific defaults may reduce boilerplate, but the resolved profile
shown by `mailvec doctor` must contain every effective value.

`Ollama:BaseUrl` can remain the shared endpoint for Ollama embedding and Ollama
vision, avoiding two settings for the same local server. Ollama-only vision
settings also remain under `Ollama`.

For backward compatibility, when the `Embedding` section is absent, resolve
the current values from:

- `Ollama:EmbeddingModel`
- `Ollama:EmbeddingDimensions`
- `Ollama:MaxBatchSize`
- `Ollama:RequestTimeoutSeconds`
- `Ollama:QueryInstructionPrefix`
- `Ollama:KeepAlive`

Existing installations therefore continue to select Ollama without any
operator action. New deployments and provider changes use `Embedding:*`.
Unknown protocols must fail at startup; they must never silently fall back to
Ollama. `ProviderId` may be a validated free-form label for a custom compatible
host, but it receives no built-in diagnostics or configuration defaults.

The exact API-key mechanism should prefer `ApiKeyFile` for long-running
services. `Auth:ApiKey` plus standard .NET configuration/environment overrides
remain useful for CI stubs and ephemeral shell runs, but secrets must not be
written to the shared `appsettings.Local.json`.

## Service and transport boundaries

Add one `EmbeddingRegistration.AddMailvecEmbedding(...)` method, mirroring
`VisionRegistration`. It must:

1. Bind, resolve, and validate the active profile once.
2. Register the selected protocol transport and its `HttpClient`.
3. Configure identical provider selection in Embedder, MCP, and CLI.
4. Apply provider-appropriate authentication, redirect policy, timeout, retry,
   and circuit-breaker settings.
5. Register only the purpose-aware embedding service with consumers.

This replaces the three hand-written `AddHttpClient<OllamaClient>` blocks. One
registration point prevents the embedder from writing one vector space while
MCP search queries use another.

Split the existing client abstraction into two layers:

```text
IEmbeddingService
  EmbedQueryAsync(text)
  EmbedDocumentsAsync(texts)
  ProbeAsync()
          |
          v
IEmbeddingTransport
  OllamaEmbeddingTransport
  OpenAiCompatibleEmbeddingTransport
```

`IEmbeddingService` applies purpose-specific text transforms, batching,
normalization, output validation, and space-identity checks. A transport only
serializes raw inputs, performs one protocol request, classifies the response,
and returns indexed vectors plus optional telemetry. This prevents vendor
semantics from leaking into `EmbeddingWorker`, `VectorSearchService`, or CLI
commands.

## OpenAI-compatible transport behavior

Add `OpenAiCompatibleEmbeddingTransport` using `HttpClient` directly; no SDK
dependency is needed. Fireworks, OpenAI, Baseten BEI, and compatible custom
profiles use this transport.

### Request rules

- Post only to the validated full `Endpoint`; do not append a provider-specific
  path.
- Apply the declared auth scheme and JSON content type.
- Send `model` and `dimensions` according to the resolved capability policy.
- Send `encoding_format=float` when configured; reject unsupported encodings
  rather than adding a second vector-decoding path accidentally.
- Do not send Ollama-only `keep_alive` or `truncate` fields.
- Reject empty strings before sending them. The current chunker normally
  prevents them, but the provider contract disallows them.
- Keep the existing default batch size of 16 initially, while allowing a
  profile to lower it for provider limits.
- Disable automatic redirects. No legitimate inference call requires one, and
  a redirect must not receive an API credential or mail payload accidentally.

### Response rules

- Require exactly one `data` item per input.
- Validate every `index` is unique and in range.
- Reorder results by `index`; do not trust response array order.
- Require each vector to have exactly the resolved `OutputDimensions` elements.
- Reject NaN and infinity before serialization to sqlite-vec.
- L2-normalize through the existing `VectorMath.NormalizeInPlaceIfNeeded`.
- Never place an upstream response body in an exception message or ordinary
  log property. Provider errors can echo input, and inputs are mail content.
- Return optional input-token usage, response model, request ID, and rate-limit
  observations in a provider-neutral telemetry value. Missing telemetry is not
  an invalid response.

### Purpose-aware text policy

The service API must distinguish queries from documents. The active profile
applies query/document prefixes and suffixes centrally before batching. This is
needed for Qwen instruction prefixes today and leaves room for models that use
different query and passage prompts later without adding vendor branches to
callers.

These transforms are part of the mathematical embedding space. Their resolved
values, along with normalization policy, model/deployment identity,
quantization/pooling where known, and output dimensions, must be covered by
`SpaceId` and any sentinel fingerprint. Changing a transform requires a full
re-embed even if endpoint and model strings are unchanged.

### Context overflow

The current chunks are small: the configured 200-token heuristic caps them at
roughly 800 characters, and the observed maximum stored chunk is 930
characters. Qwen3 Embedding has a much larger context window, so document
overflow should not occur under current chunking.

The service should still distinguish an input-length rejection from other
400s. Only a positively identified length error may use the existing split and
progressive-truncation fallback. Authentication, model, malformed-request, or
unsupported-parameter failures must surface immediately rather than being
misdiagnosed as long input.

## Failure model and retries

Introduce a provider-neutral `EmbeddingException` with a small stable failure
classification, similar to `VisionException`:

- `AuthOrConfig`
- `ModelUnavailable`
- `Backpressure`
- `InputTooLong`
- `InvalidResponse`
- `Transient`
- `SpaceMismatch` (added post-review: the read-side guard's refusal — the
  active profile describes a different vector space than the stored vectors;
  configuration-level, never message evidence)

Use the following default mapping for hosted OpenAI-compatible profiles, with
small provider-specific refinements only where an official contract warrants
them:

| Response | Classification | Behavior |
|---|---|---|
| 401/403 | `AuthOrConfig` | No retry; fail clearly |
| 404 or explicit model error | `ModelUnavailable` | No blind retry |
| 429 | `Backpressure` | Honor `Retry-After`; bounded exponential backoff |
| 503 | `Backpressure` or `Transient` | Bounded retry, then leave work queued |
| Other 5xx/network timeout | `Transient` | Bounded retry |
| Confirmed context overflow | `InputTooLong` | Split/truncate fallback |
| Wrong count/index/dimensions | `InvalidResponse` | Fail loudly |

Fireworks serverless explicitly documents both 429 rate limiting and 503 load
shedding and recommends exponential backoff. Other hosted profiles expose the
same broad conditions with different error bodies and headers. Resilience must
be consistent across the embedder, MCP, and CLI, with a tighter total budget
for interactive query embedding than for background ingestion.

The embedder's poison-message isolation must not count provider-wide
backpressure, authentication failure, or outage as evidence against a message.
Its existing health-probe rule is directionally correct, but classified errors
make that conclusion explicit instead of inferring it from whether another
message happened to work.

## Provider-neutral readiness probe

`IEmbeddingClient.IsModelAvailableAsync` encodes an Ollama-only question:
whether a model is pulled locally. Replace `PingAsync` plus that tri-state with
a single provider-neutral probe result, for example:

```text
EmbeddingProbe
  Status: Available | AuthFailed | ModelMissing | Backpressure |
          Unreachable | InvalidResponse
  Detail: sanitized provider/model information only
```

Every profile should perform a real one-string embed for readiness. For Ollama,
a failed real embed can still use `/api/tags` to refine the status to
`ModelMissing`. Hosted profiles use status code and sanitized structured error
type. Model-list discovery is an optional protocol/provider diagnostic, not a
requirement: a deployment-scoped Baseten endpoint, for example, need not offer
a meaningful model catalog.

The probe remains bounded to fit the MCP container's ten-second healthcheck
budget. A rate-limited health probe should not imply that credentials or the
model are missing.

## Embedding-space identity

### Problem

The database currently stamps only:

- `metadata.embedding_model`
- `metadata.embedding_dimensions`

That protects against most Ollama model switches, but it is not sufficient for
multiple providers. Two backends can expose the same nominal checkpoint with
different pooling, prompt templates, quantization, or revisions. A hosted
serverless model can also change behind a stable public name. Mixing old
document vectors with new query or document vectors produces plausible but
meaningless rankings — exactly the silent-corruption class the existing guard
is meant to prevent.

### Proposal

Add an explicit `metadata.embedding_space_id`, supplied by the operator for new
hosted profiles and validated as a non-secret stable identifier, for example:

```text
ollama:mxbai-embed-large:1024
fireworks:accounts/fireworks/models/qwen3-embedding-8b:1024:<revision-or-fingerprint>
openai:text-embedding-3-large:1024:<revision-or-fingerprint>
baseten:<deployment-or-checkpoint>:<pooling>:<quantization>:1024:<fingerprint>
```

Do not automatically derive this value from `ProviderId` and `Request:Model`.
Baseten demonstrates why: its OpenAI-compatible request may send
`model=not-required`, while vector compatibility actually depends on the
deployment, checkpoint, pooling, quantization, text policy, normalization, and
dimensions. Provider and wire model are useful metadata but are not a proof of
compatibility.

Also persist `metadata.embedding_config_hash`, a deterministic hash of a
canonical, versioned serialization of every locally known vector-affecting
setting: the asserted `SpaceId`, wire-model policy/value, output dimensions,
query and document transforms, and normalization policy. Secrets, retry limits,
timeouts, batch size, and endpoint URL are excluded. This gives the database an
enforceable guard when a text transform changes while `SpaceId` is mistakenly
left untouched. The explicit space ID describes the remote semantic identity;
the config hash proves how Mailvec invoked it.

One of these stability policies must be selected before production rollout:

1. **Pinned deployment:** use a deployment whose model revision and serving
   parameters are controlled by the operator, and include those facts in
   `embedding_space_id`.
2. **Sentinel fingerprint:** store embeddings for fixed non-mail sentinel texts
   and compare them at startup within a documented tolerance. Refuse to write
   or search when the provider's output moves unexpectedly.
3. **Accepted hosted mutability:** stamp provider and public model only, monitor
   deprecation/update notices, and accept that an unannounced serving change
   may require a full rebuild. This is the smallest implementation and the
   weakest corruption guard; it is not recommended for this repository.
4. **Hybrid — artifact digest locally, sentinel fingerprint hosted
   (DECIDED 2026-08-07):** match the check to what each provider makes
   observable. Ollama tags resolve to content-addressed digests (`/api/tags`
   reports them), so the local provider gets true artifact pinning: the
   embedder records `metadata.embedding_model_digest` the first time it
   embeds, verifies it per poll, and refuses on a change — deterministic,
   no tolerance to tune, catches a re-pulled tag exactly. Hosted serverless
   weights are opaque, so hosted profiles get behavioral sentinel probing
   (option 2), implemented alongside the OpenAI-compatible transport in
   phase 3 — which confines the tolerance-tuning problem to the only
   provider class that needs it. Both checks feed the same
   refuse-on-mismatch path as the config hash.

Digest scope note: the digest is an *observation* keyed to runtime state, so
it lives in its own metadata key, is cleared by `switch-model` in the same
transaction as the identity rewrite, and needs no schema migration (absent
means "not yet observed", the same unknown-is-not-stale rule the heartbeats
follow). A digest mismatch's remedy is `mailvec switch-model --force` — the
model name is unchanged, so the non-forced form would report nothing to do.

### Enforcement points

`embedding_space_id` and `embedding_config_hash` must participate in:

- fresh-schema metadata seeding;
- embedder startup and per-poll verification;
- health mismatch reporting;
- `mailvec status` and `mailvec doctor`;
- `mailvec switch-model`;
- `ChunkRepository.ReplaceChunksForMessage`'s transactional mid-batch guard;
- **query-time read enforcement** (added post-review — the original list
  omitted it): `EmbeddingSpaceGuard` refuses semantic/hybrid search before
  query embedding/KNN when stored model, dimensions, space id, or config hash
  disagree with the active profile. Metadata-only by design: digest and
  hosted-sentinel checks stay on the embedder/health cadence, because a
  network probe inside every search would couple availability and
  unknown-is-never-drift forbids failing on an unreachable listing.

The last item is essential. A provider/model switch that lands during an HTTP
request must not commit stale-space vectors after the migration transaction.

Existing databases can have their current metadata mapped to the legacy
Ollama identity and canonical legacy config hash during migration without
touching vectors:

```text
ollama:<embedding_model>:<embedding_dimensions>
```

Any actual switch to a different embedding space still requires
`mailvec switch-model`, which drops the vector table, clears chunks, re-queues
every live message, and stamps the new identity atomically.

## Health, doctor, and monitoring compatibility

The detailed `/health` report and CLI should become profile-aware:

- report profile name, protocol, provider ID, endpoint host, configured wire
  model, dimensions, space ID, readiness, and sanitized failure classification;
- never report an API key;
- replace `ollama pull` advice with provider-specific remediation;
- keep keyword-search fallback advice for semantic/hybrid failures.

The public `/up` endpoint is constrained by an existing Uptime Kuma wire
contract. **As-built note for the phase-4 implementer (2026-08-08):** the
semantic shift described below has already de facto begun — since phase 2b,
`ollama.reachable` is computed from the provider-neutral classified probe
(`EmbeddingService.ProbeAsync`), so it already means "the configured
embedding profile's readiness probe succeeded" even when the profile is
hosted. Phase 4's job is to add the neutral field, document the alias
semantics, and migrate the monitors — the underlying signal is done.
Monitors currently read `ollama.reachable`; renaming it in place would
silently break monitoring. Roll out an additive provider-neutral field while
retaining the old one as a compatibility alias:

```json
{
  "embeddingProvider": { "ready": true },
  "ollama": { "reachable": true }
}
```

While the alias exists, `ollama.reachable` means "the configured embedding
profile completed its real-embed readiness probe," even when the profile is
hosted. Document the temporary semantic mismatch, migrate every monitor to
`embeddingProvider.ready`, then remove the alias only in a separately approved
wire-contract change.

The MCP `search_emails` error translation must also become provider-neutral.
It should say that semantic ranking is unavailable and suggest `mode=keyword`,
then give sanitized remediation based on the classified failure. It must not
leak a hosted endpoint, API response body, or any submitted query.

## Credentials and threat model

A hosted embedding profile changes the data boundary substantially:

- the embedder sends every indexed body and extracted attachment chunk;
- MCP and CLI send every semantic/hybrid search query;
- re-embedding sends the historical corpus in bulk without a user present for
  each request.

Retention, training, residency, and abuse-monitoring terms differ by provider,
account tier, and deployment mode. Fireworks' documented serverless terms can
be evaluated for the first experiment, but the security documentation must
require a fresh review for OpenAI, Baseten, or a custom host. Every hosted
provider is an off-network processor of mail content, not merely a compute
dependency.

Credential placement:

- **Embedder:** required for chunk embeddings.
- **MCP:** required for query embeddings and readiness.
- **CLI:** required for search, eval, doctor, and migration verification.
- **Indexer and mbsync:** must not receive the key.

For the Docker deployment, mount one owner-only secret into the MCP and
embedder containers. The CLI normally runs through `docker compose exec mcp`,
so it can read the MCP container's secret without broadening access to the
indexer. Do not put the key in the shared `x-mailvec-env` anchor.

For macOS launchd, do not write the key into the shared
`appsettings.Local.json` or a world-readable plist. Read it from an owner-only
file whose path may be placed in ordinary config.

Operational guidance should recommend a dedicated provider key/account
boundary and a conservative monthly spend limit. A compromise of the
internet-facing MCP container otherwise grants a reusable hosted-inference
credential in addition to mail-search access.

## First experiment: model and dimension recommendation

Start with `accounts/fireworks/models/qwen3-embedding-8b` at 1024 dimensions.

Rationale:

- It is currently documented as available on Fireworks serverless.
- Fireworks supports explicitly requesting reduced dimensions.
- Qwen3 Embedding 8B supports Matryoshka dimensions from 32 through 4096.
- 1024 dimensions keeps vector storage comparable to the current mxbai index.
- The repository already applies a query-only instruction prefix at the correct
  shared layer.

Use the existing Qwen query prefix:

```text
Instruct: Given a web search query, retrieve relevant passages that answer the query
Query: 
```

Documents remain unprefixed. The Qwen model card reports a retrieval-quality
loss when the query instruction is omitted.

Do not start at 4096 dimensions merely because it is the model maximum. On the
current corpus, raw float storage for 339,943 chunks is approximately:

- 1024 dimensions: 1.30 GiB of float data before vec0/SQLite overhead.
- 4096 dimensions: 5.19 GiB before overhead.

That is about 3.89 GiB of additional raw vector data, plus larger caches and
more expensive KNN scans. Evaluate whether the quality gain justifies it.

## Current corpus migration estimate

Read-only measurements from the frozen corpus on 2026-08-07:

| Metric | Value |
|---|---:|
| Live messages | 75,086 |
| Embedded live messages | 75,086 |
| Stored chunks | 339,943 |
| Stored chunk characters | 203,326,637 |
| Stored token heuristic | 50,726,357 |
| Average chunk length | 598 characters |
| Maximum stored chunk length | 930 characters |

At Fireworks' observed 2026-08-07 Qwen3 8B embedding price of $0.10 per million
input tokens, a full corpus re-embed is roughly $5.07. Actual billing depends
on the provider tokenizer rather than Mailvec's character heuristic.

At batch size 16, 339,943 chunks require approximately 21,247 embedding
requests. Fireworks' documented adaptive token limits, account-wide request
limits, retries, and transient 503 load shedding determine wall time. The
documented 900,000 uncached prompt TPM starting rate makes the token-volume
floor roughly 56 minutes; operationally, plan for an hour or more and measure
the actual account headers rather than treating that estimate as a guarantee.

Ongoing cost is the embeddings for new mail plus query embeddings. Query cost
should be small relative to the one-time corpus migration, but should still be
measured and included in the monthly budget.

## Phased implementation

**Status (2026-08-08):** phase 0 complete (subset baseline, see note in phase
0 below); phase 1 complete — `7c815aa` (space identity v11), `50f3888`
(artifact digest); phase 2 complete — `ff21281` (profiles + registration),
`5a45148` (purpose-aware service, classified failures, neutral probe), plus
the post-review hardening commit (read-side guard, digest tag resolution,
full four-transform hash coverage). Each landing verified ranking-neutral:
eval per-query results bit-identical to the committed subset baseline.
Phase 3 complete (2026-08-08): the mathematical contract moved into
EmbeddingService, `IEmbeddingTransport` extracted, the PostConfigure bridge
retired, `OpenAiCompatibleTransport` landed stub-tested with hosted profile
activation (explicit SpaceId enforced per decision 3; bearer key material
resolved once into the HttpClient closure, fatal in every process), and
sentinel fingerprints implemented. Sentinel as-built deviations from this
proposal's sketch: storage is versioned metadata KEYS, not a table — the
fingerprints are observations like the digest (stamped by the embedder on
first sight, cleared by switch-model, absent = not yet observed), so no
schema migration exists; and stamping happens on the embedder's first
successful sentinel embed rather than inside switch-model, which has no
provider access. Threshold 0.999 was set from measurement, not guesswork:
8 repeated embeds against Fireworks qwen3-embedding-8b (2026-08-08)
returned cosine 1.00000000 every time. Live E2E verified the read-side
guard refuses SpaceMismatch with the Fireworks profile active against an
Ollama-space database before any query leaves the machine.

Post-review hardening (2026-08-08, phase-3 review): fresh-schema creation
and `switch-model` stamp the resolved profile's asserted identity (the
indexer carries a credential-free identity registration so a hosted-profile
fresh database is possible without the key ever reaching it; hosted
`--model`/`--dims` overrides diverging from the active profile are
rejected); detected sentinel drift persists a marker that the read-side
guard refuses until resolved (auto-cleared on a healthy re-observation or
by `switch-model`); the readiness probe enforces the full mathematical
contract so a provider-wide shape regression reads as an outage, never as
per-message quarantine evidence; remote stability checks (digest +
sentinels) run on a bounded five-minute cadence rather than once per
drain batch; hosted responses surface optional usage/model/request-id/
rate-limit telemetry through a provider-neutral observer (Debug log
lines); and interactive hosted requests retry backpressure once within a
~10s budget, honoring Retry-After, while auth/model errors still fail
fast. **Accepted as-built deviation:** the hosted transport implements NO
context-overflow split/truncate fallback — a positively identified length
400 classifies `InputTooLong` and surfaces loudly, because current chunks
cap at ~930 characters against a 32k-token window, so a genuine overflow
means an upstream bug that silent truncation would hide. The
identification is substring-based (`context`, `maximum length`, `too
long`, `max_tokens`); revisit if a provider's structured error format
makes stricter matching possible.

The work lands as ordered phases. Each phase leaves the repository releasable
and behavior-compatible on the existing Ollama path; no phase requires the next
one to ship. Mail content first reaches a hosted provider in phase 6, and only
from a database copy.

### Phase 0 — baseline capture (no code)

Capture `mailvec eval --json baselines/<date>.json` with the current
Ollama/mxbai configuration before any phase below merges — including the
"pure refactor" phases. A baseline taken after a refactor cannot prove the
refactor was neutral.

**As executed:** the baseline was captured against the 662-message
`~/MailvecSubsetOCR` corpus (not the frozen full corpus) at
`baselines/subset-ocr/2026-08-07.json`; its README records corpus identity,
binary provenance, and the rule that subset numbers are only comparable to
subset numbers.

### Phase 1 — embedding-space identity (schema migration + guards)

The starting point, and valuable with no hosted provider at all: it closes the
existing gap where re-pulling an Ollama tag whose artifact changed, or editing
`Ollama:QueryInstructionPrefix`, mixes vector spaces that
`metadata.embedding_model` + `metadata.embedding_dimensions` cannot
distinguish.

- Schema migration adding `metadata.embedding_space_id` and
  `metadata.embedding_config_hash`. Existing databases are stamped with the
  legacy identity `ollama:<embedding_model>:<embedding_dimensions>` and the
  canonical legacy config hash, without touching vectors. Repository
  invariant: bump `SchemaMigrator.LatestSchemaVersion` and the
  `schema_version` literal in `001_initial.sql` together
  (`AssertBaselineStampsLatest` enforces the pairing).
- All enforcement points listed under "Enforcement points": fresh-schema
  seeding, embedder startup and per-poll verification, health mismatch
  reporting, `mailvec status` / `mailvec doctor`, `mailvec switch-model`, and
  the transactional guard in `ChunkRepository.ReplaceChunksForMessage`.
- The config hash canonicalization covers the currently resolved
  query/document transforms even though they still live under `Ollama:*` at
  this point; the legacy resolver defines the canonical serialization.
- Stability policy, per the decided hybrid (decision 2): Ollama
  artifact-digest verification lands here — the embedder observes
  `metadata.embedding_model_digest` from `/api/tags`, stamps it when absent,
  verifies per poll, refuses on change (`switch-model --force` rebuilds
  under new weights; plain `switch-model` clears the digest with the
  identity rewrite). No schema migration needed: the digest is an observed
  runtime fact and absent means "not yet observed". Hosted sentinel storage
  is deferred to phase 3 with the transport that needs it.
- This phase carries the schema migration and is therefore the minor-bump
  candidate under the release policy; the bump happens only when a release is
  explicitly approved.

Gated on decisions 2 and 3 — the migration's shape depends on both.

### Phase 2 — profiles, registration, and service/transport split (no behavior change)

- The `Embedding:*` profile schema, validation, and the legacy resolver
  (absent section → current `Ollama:*` values), with `ollama` as the only
  protocol.
- `EmbeddingRegistration.AddMailvecEmbedding(...)` replacing the three
  hand-written `AddHttpClient<OllamaClient>` blocks in Embedder, MCP, and CLI.
- The `IEmbeddingService` / `IEmbeddingTransport` split, with
  `OllamaEmbeddingTransport` as the existing client refactored into transport
  shape, and the purpose-aware query/document text policy relocated from
  `VectorSearchService`'s prefix handling into the service layer.
- The provider-neutral `EmbeddingProbe` replacing `PingAsync` +
  `IsModelAvailableAsync`, and the `EmbeddingException` classification adopted
  by the embedder's poison-message isolation.

Wire behavior against Ollama is unchanged. Re-run the eval after this phase;
the results must be identical to the phase 0 baseline.

### Phase 3 — OpenAI-compatible transport (stub-tested, no live traffic)

`OpenAiCompatibleEmbeddingTransport` with the request/response rules, failure
classification, retry/backoff behavior, and telemetry capture defined above;
Fireworks, OpenAI, Baseten, and custom-compatible profile examples. Everything
is exercised against stub HTTP fixtures; CI must not require a live hosted key.

Also carries the hosted half of the decided stability hybrid: **sentinel
fingerprinting** — fixed non-mail sentinel texts embedded at `switch-model`
time, stored (small additive migration), and re-compared at startup within a
documented tolerance derived from measured provider jitter. Drift refuses
vector writes and semantic search (keyword degrades gracefully) until a
rebuild. Tolerance tuning is confined here, to the only provider class whose
weights are unobservable.

### Phase 4 — health, doctor, monitoring, and MCP error neutrality

The additive `/up` `embeddingProvider.ready` field with the documented
`ollama.reachable` compatibility alias; profile-aware `/health`, `doctor`, and
`status` reporting; provider-neutral MCP `search_emails` error translation;
the Uptime Kuma runbook update (dated as observed, per the ops-doc rule).

**Complete (2026-08-08):** `embeddingProvider.ready` rides `/up` beside the
alias (same value; the allowlist test admits and pins it); `/health` carries
an additive `profile` block (name, protocol, provider id, endpoint HOST only,
wire model, dimensions, space id, probe classification — never credentials);
doctor prints the resolved profile as a config check; MCP `search_emails`
gives hosted profiles kind-specific provider remediation (never
`ollama pull`; the remaining Ollama-named doctor advice paths are provably
unreachable under hosted profiles, since only the Ollama transport can
report a definite missing model). Live Kuma monitor migration is a
post-release operator step recorded in the runbook.

### Phase 5 — credentials and deployment wiring

`ApiKeyFile` support, the Docker secret mount for MCP + embedder (never
indexer/mbsync), `compose.yml` / `.env.example` changes, the launchd
owner-only key file, and the `docs/security.md` data-flow and credential
updates. Gated on decision 4.

### Phase 6 — Fireworks experiment on a database copy

The experiment procedure detailed under "Rollout and evaluation" steps 4–9.
Ends at a decision gate: quality against the eval threshold (decision 5),
cost, latency, and security posture are reviewed before any live-deployment
proposal.

### Phase 7 — production rollout (separate approval)

Only after phase 6 acceptance: coordinated configuration for embedder, MCP,
and CLI plus the atomic `switch-model` migration on the live archive; monitor
migration to `embeddingProvider.ready` and eventual alias retirement
(decision 6) as a separately approved wire-contract change.

## Rollout and evaluation

This is a retrieval-affecting change and must follow the frozen-corpus and eval
rules. The sequence below is realized by the phases above; steps 4–9 are the
phase 6 experiment.

1. Capture a current Ollama/mxbai eval baseline before any implementation
   phase merges (phase 0).
2. Add the space-identity migration and all transactional guards (phase 1).
3. Implement the two protocol transports, purpose-aware service, and centralized
   registration with stub HTTP tests; CI must not require a live hosted key
   (phases 2–3).
4. Work against a consistent database copy. Do not run `switch-model` against
   the frozen source corpus.
5. On the database copy, configure Fireworks Qwen3 8B at 1024 dimensions and
   run `mailvec switch-model`.
6. Run the embedder directly against the copy until coverage reaches 100%.
7. VACUUM or `VACUUM INTO` before timing evals; the existing experiment guide
   documents the severe KNN latency penalty from post-rebuild fragmentation.
8. Run the identical eval query set with timing and compare keyword, semantic,
   and hybrid results to baseline.
9. Audit provider request counts, billed tokens, 429/503 frequency, retries,
   full-drain duration, database size, and semantic query latency.
10. Only after quality, cost, security posture, and monitoring changes are
    accepted should a live deployment be proposed (phase 7).

Changing the provider in a live database requires coordinated configuration
for the embedder, MCP, and CLI plus the atomic `switch-model` migration. Do not
allow a rolling interval where document vectors and query vectors come from
different providers.

## Test plan

### OpenAI-compatible transport unit tests

- Fireworks and OpenAI fixtures send bearer auth, model, inputs, and configured
  dimensions;
- a Baseten fixture sends its required placeholder model and omits dimensions
  when the profile says to omit them;
- a custom-compatible fixture uses its exact full endpoint;
- required, placeholder, and omitted model policies serialize as declared;
- `send` and `omit` dimension policies serialize as declared while both still
  validate the returned vector width;
- does not send Ollama-only fields;
- returns vectors in input order using response indexes;
- rejects duplicate, missing, negative, and out-of-range indexes;
- rejects wrong vector count or dimension;
- rejects NaN/infinity;
- normalizes unnormalized vectors and preserves already-normalized vectors;
- empty input list short-circuits without HTTP;
- empty individual input is rejected before HTTP;
- upstream bodies never appear in exception messages or log output;
- 401/403, model errors, 429, 503, other 5xx, timeout, and malformed JSON are
  classified correctly;
- `Retry-After` and bounded backoff are honored;
- confirmed context overflow splits/truncates while unrelated 400s do not;
- optional usage, response model, request ID, and rate-limit metadata are
  captured without becoming response requirements.

### Purpose-aware service tests

- query and document transforms are applied exactly once and never swapped;
- batching occurs after transforms and preserves input/result order;
- normalization and dimensions are enforced identically for both transports;
- the config hash covers the resolved text and normalization policy;
- changing query or document transforms produces a config-hash mismatch even
  when the asserted space ID is unchanged;
- callers cannot issue an untyped embed that bypasses purpose policy.

### Registration/config tests

- absent `Embedding` section preserves current Ollama behavior;
- one `ActiveProfile` resolves identically in Embedder, MCP, and CLI;
- Ollama selects the Ollama transport; OpenAI, Fireworks, and Baseten examples
  select the shared OpenAI-compatible transport;
- unknown protocol fails startup; unknown provider IDs remain allowed for
  custom compatible endpoints but receive no provider-specific diagnostics;
- invalid endpoint, model policy, dimensions policy, or output dimensions fail
  startup;
- arbitrary headers and arbitrary JSON templates are rejected as unsupported;
- incomplete bearer credentials fail the processes that perform embeddings;
- HTTPS is required except for an explicit loopback test endpoint;
- secret-file and environment precedence is deterministic;
- API key is never included in provider descriptions or health JSON.

### Database invariant tests

- fresh schema stamps the full space identity and canonical config hash;
- legacy database migration stamps its existing Ollama space and canonical
  legacy config hash without clearing vectors;
- space mismatch is detected even when provider, model, and dimensions match;
- `switch-model` changes provider/model/dimensions/space/config hash atomically;
- a provider switch during an in-flight embed causes the guarded write to skip;
- health and status report provider-space mismatch;
- sentinel drift, if selected, prevents queries and writes until migration.

### Health and MCP tests

- every profile's readiness probe performs a real embed;
- auth, model missing, backpressure, and network outage yield distinct sanitized
  doctor hints;
- MCP semantic failure still recommends `mode=keyword`;
- `/up` retains the existing `ollama.reachable` path during compatibility;
- the new provider-neutral readiness path matches the compatibility alias;
- no endpoint, key, or upstream response body reaches the public minimal
  health projection.

### Integration/eval checks

- full re-embed reaches 100% without quarantine caused by provider-wide errors;
- stored dimensions and normalized-vector contract match sqlite-vec;
- semantic and hybrid eval quality meet an agreed threshold against baseline;
- interactive query latency is acceptable warm and after idle;
- retry behavior does not create a request storm during 429/503 periods;
- measured billed tokens are consistent with response usage totals where the
  provider supplies them.

## Documentation and deployment changes

At minimum, update:

- `README.md` quickstart and provider-selection guidance;
- `CLAUDE.md` embedding invariants and configuration description;
- `docs/security.md` outbound data-flow and credential table;
- `docs/deploy-docker.md` provider setup and secret mounting;
- `docs/contributing/embedding-experiments.md` provider-aware experiment steps;
- `ops/UPGRADING.md` compatibility and migration guidance;
- `compose.yml` and `.env.example`, without exposing the key to indexer/mbsync;
- launchd installation templates or secure key-file setup;
- `mailvec doctor`, `mailvec status`, installer warnings, and MCP recovery text;
- the Uptime Kuma runbook when the provider-neutral `/up` field is added.

## Work breakdown

| Work item | Phase | Estimate |
|---|---|---:|
| Provider-aware metadata/space identity, migration, guarded writes | 1 | 1–1.5 days |
| Profile schema, legacy resolver, centralized service/registration | 2 | 1–1.5 days |
| Purpose-aware text policy and usage/rate-limit telemetry | 2–3 | 0.5–1 day |
| OpenAI-compatible transport, profile examples, errors, retries | 3 | 1–1.5 days |
| Health, doctor, status, MCP errors, monitoring compatibility | 4 | 0.5–1 day |
| Compose/launchd secrets, security and operator documentation | 5 | 0.5–1 day |
| Integration testing, corpus re-embed, eval, and tuning | 6 | 1–2 days plus drain time |

Allow five to eight focused engineering days overall; some work items overlap.
The lower end assumes the provider/model public name is accepted as the space
identity. Selecting and implementing sentinel fingerprinting pushes toward the
upper end but better matches this repository's silent-corruption posture. The
provider-neutral structure adds roughly half to one day over a Fireworks-only
implementation and avoids duplicating that work for OpenAI or Baseten later.

## Risks

- **Silent vector-space drift.** Highest-impact risk. Mitigate with a pinned
  deployment or sentinel fingerprint and transactional space guards.
- **Data disclosure.** All indexed mail chunks and semantic queries leave the
  local network under a hosted profile. Keep hosted profiles opt-in and
  document each provider's boundary prominently.
- **Credential exposure.** MCP must hold a reusable key. Use a scoped secret
  file and conservative spend limits; never place it in shared ordinary config.
- **Hosted availability.** 429 and 503 are normal serverless conditions. Use
  bounded backoff and retain keyword search as graceful degradation.
- **Monitoring breakage.** `/up` has locked JSONata paths. Add fields; do not
  rename existing ones in place.
- **Quality regression.** A larger/newer model is not automatically better on
  this mail corpus. Preserve the query instruction, baseline, and evaluate.
- **Storage/latency growth.** 4096 dimensions quadruple raw vector data and KNN
  work. Start at 1024.
- **Operational split-brain.** The embedder and query-serving processes can
  accidentally select different profiles if registration or config drifts.
  Central registration and metadata verification are mandatory.
- **False compatibility.** An endpoint can accept the OpenAI envelope while
  interpreting model, dimensions, prompts, or normalization differently. Keep
  capability policies explicit and enforce `SpaceId`, the config hash, and
  sentinels.
- **Abstraction creep.** Arbitrary headers and JSON templates would turn config
  into executable protocol logic that is hard to validate and sanitize. Add a
  typed transport when a future provider is materially incompatible.
- **Cost spikes.** Reindex commands or repeated migrations can resubmit the
  whole corpus. Surface usage, configure budget limits, and require deliberate
  model switching.

## Decisions required before implementation

1. Accept the recommended Fireworks Qwen3 8B @1024 first experiment, or test a
   different model/dimension. *(Gates phase 6.)*
2. ~~Choose embedding-space stability policy.~~ **Decided 2026-08-07: the
   hybrid** — Ollama artifact-digest verification (phase 1, implemented with
   the space-identity work), sentinel fingerprinting for hosted profiles
   (lands with the phase 3 transport, storage via a small additive
   migration then).
3. ~~Confirm that new hosted profiles must provide an explicit `SpaceId`.~~
   **Decided 2026-08-08: yes.** Confirmed after empirically checking the
   Fireworks models API: it exposes no revision or weights identity to derive
   from, so derivation would launder the serverless alias into looking like
   an identity. Ollama profiles are the inverse: they may NOT assert a
   SpaceId (derived + digest-enforced), which registration validation
   enforces.
4. Choose the persistent local secret-file location and Docker secret name.
   *(Gates phase 5.)*
5. Define the semantic/hybrid eval threshold required for rollout. *(Gates the
   phase 6 accept/reject decision; should be fixed before results exist to
   argue about.)*
6. Decide how long to retain the `/up` `ollama.reachable` compatibility alias
   after monitors migrate. *(Gates only the phase 7 alias retirement; not
   needed earlier.)*

## Recommendation

Proceed with two typed protocol transports (Ollama and OpenAI-compatible),
named provider profiles, and a purpose-aware embedding service. Run Fireworks
`qwen3-embedding-8b` @1024 as the first experiment on a database copy; OpenAI,
Baseten BEI, and compatible custom deployments should then require only profile
configuration and provider-specific diagnostics, not new clients. Keep Ollama
as the default, use a secure key file, require an explicit embedding-space ID,
and use either a pinned deployment or a sentinel fingerprint before any hosted
profile writes into a long-lived production archive.
