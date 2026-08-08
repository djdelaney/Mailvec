# Embedding Providers Phases 4–7 Review

**Date:** 2026-08-08  
**Scope:** Review the implementation of the remaining phases in
`docs/proposals/embedding-providers.md`, including provider-neutral health and
monitoring, hosted credential wiring, the Fireworks experiment, and the
production-rollout disposition.

## Outcome

Phases 4–6 are directionally sound, and the decision to reject the evaluated
Fireworks candidate is supported by the recorded results. The hosted-provider
deployment path is not ready for release yet: four findings can block a provider
switch or report a switched deployment as healthy when semantic search is
unavailable.

Phase 7 was conditional on the candidate passing the evaluation gate. The
candidate failed that gate, so treating Phase 7 as moot rather than performing a
production rollout is correct.

## Release-blocking findings

### 1. [P1] `switch-model` mistakes a provider or embedding-space change for a no-op

`src/Mailvec.Cli/Commands/SwitchModelCommand.cs:66-73` decides that no work is
needed using only the model name and dimensions.

A move from local Ollama to the same nominal model on a hosted provider can keep
both values unchanged while changing the asserted space ID or configuration
hash. Changes to transforms or other space-defining configuration have the same
problem. The command reports success without stamping the new identity or
rebuilding vectors; services started with the new profile then correctly refuse
the old database.

Determine no-op status from the complete resolved embedding-space identity,
including the result of `EmbeddingSpace.ForProfile(profile)`. Add a regression
test for identical model and dimensions with a different provider space or
configuration hash.

### 2. [P1] The Docker switching procedure runs the migration with the old environment

`docs/deploy-docker.md:295-304` tells the operator to edit `.env` and then run:

```sh
docker compose exec mcp mailvec switch-model --yes
```

`docker compose exec` starts the command in the existing MCP container. That
container retains the environment with which it was created, so the CLI resolves
the old embedding profile. It can therefore migrate—or incorrectly no-op—using
the old identity.

Use a newly created one-off container, such as an appropriately tested
`docker compose run --rm --no-deps mcp ...` flow, or document another tested
recreate-and-migrate ordering that guarantees the CLI sees the new profile. The
reverse hosted-to-Ollama procedure needs the same correction.

### 3. [P1] Health remains green after sentinel drift blocks semantic search

`src/Mailvec.Core/Health/HealthService.cs:94-103` includes model, dimensions,
space ID, and configuration hash in `modelMismatch`; the digest is added later.
It does not include the persisted `EmbeddingSpace.SentinelDriftKey`.

The worker can record standing provider-function drift and
`EmbeddingSpaceGuard` will then refuse semantic and hybrid queries. If the
provider remains reachable and the static identity fields still match,
`/health` and `/up` can nevertheless report a healthy embedding subsystem.

Fold the standing drift marker into the degraded health state, either through
the widened `modelMismatch` contract or a dedicated provider-drift field. Cover
both health endpoints with a persisted-marker test.

### 4. [P1] Existing Compose installations have an undocumented upgrade prerequisite

`compose.yml:502-510` unconditionally declares
`secrets/embedding_api_key`, and the MCP and embedder services mount it even when
the hosted profile is disabled. New setup instructions create an empty file,
but existing installations from an earlier release will not have one. Their
next Compose update can fail before the containers start.

Add an explicit upgrade and preflight step that creates the empty owner-only
file, or change the Compose shape so Ollama-only deployments do not require an
inactive hosted-provider secret.

## Additional findings

### 5. [P2] `ModelParameter=omit` cannot complete the database lifecycle

`src/Mailvec.Core/Embedding/EmbeddingRegistration.cs:269-277` allows a hosted
profile using `ModelParameter=omit` to have an empty request model. However,
`SchemaMigrator.SubstituteEmbeddingConfig` and `SwitchEmbeddingModel` require a
nonempty model identity.

The profile can pass resolver and transport tests but cannot create a fresh
database or switch an existing database. Separate the stable database model
identity from the optional wire parameter, or require a nonempty local identity
even when the provider request omits `model`. Add fresh-schema and switch tests
for the omit policy.

### 6. [P2] Documented embedding telemetry is disabled by default

`src/Mailvec.Core/Embedding/EmbeddingTelemetry.cs:30-39` emits provider usage
telemetry at Debug level, while `src/Mailvec.Embedder/appsettings.json` defaults
to Information. The deployment guide tells operators to watch these events but
does not show how to enable them.

Expose and document an explicit telemetry logging override, or emit a suitable
aggregate event at Information level. Ensure the resulting operational log does
not contain message or query content.

### 7. [P2] Every health request performs a real hosted embedding

`src/Mailvec.Core/Health/HealthService.cs:115-123` performs a real classified
embedding probe on every health evaluation. Multiple field-specific `/up`
monitors plus the container healthcheck can generate continuous paid requests,
concurrent bursts, and rate-limit-driven false alarms.

Cache and coalesce provider readiness for a short, documented interval, or
prescribe a single compound monitor for hosted deployments and document the
expected ongoing request volume.

### 8. [P2] Successful hosted migrations print Ollama-only remediation

`src/Mailvec.Cli/Commands/SwitchModelCommand.cs:97-108` always tells the operator
to run `ollama pull`, set `Ollama__*` variables, and follow the launchd-oriented
rebuild flow—even when the active profile is hosted.

Render next steps by protocol. Hosted guidance should cover credentials,
service/container recreation, coverage monitoring, and sentinel verification.

### 9. [P2] The proposal overstates same-model hosted compatibility

`docs/proposals/embedding-providers.md:905-909` says that serving the same model
through a hosted provider carries “no quality penalty at all.” Serving revision,
pooling, prompts, quantization, preprocessing, and runtime implementation can all
change vectors and rankings despite an identical nominal checkpoint.

State instead that the same-model hosted path is first class but represents a
distinct asserted embedding space and must pass the normal evaluation gate.

## What is working well

- Fresh hosted databases stamp the resolved hosted identity.
- The indexer can register provider identity without receiving provider
  credentials.
- Semantic and hybrid reads are guarded against stored identity and sentinel
  drift.
- Hosted readiness uses a real classified embedding probe.
- Provider credentials are limited to the MCP and embedder containers.
- Detailed health output avoids exposing full hosted endpoints or credentials.
- Hosted MCP failures no longer prescribe Ollama-specific remediation.
- The Phase 6 gate was recorded before the result and was applied as written.

## Verification

The implementation was reviewed at commit `05fe3ab`.

```text
dotnet build --no-restore --nologo -m:1 -nr:false
Result: succeeded, 0 warnings, 0 errors

dotnet test --no-build --no-restore --nologo
Result: 1,261 passed, 0 failed, 0 skipped
```

The Phase 6 metrics were independently recomputed from the committed result
files and match the report:

- Keyword NDCG@10: `0.905759` → `0.905759`
- Semantic NDCG@10: `0.848764` → `0.874251` (`+0.025487`)
- Hybrid NDCG@10: `0.905479` → `0.899387` (`-0.006093`)
- Four hybrid queries regressed by more than `0.2`

Those regressions fail the precommitted candidate gate. The Fireworks candidate
should remain rejected, while the provider-neutral infrastructure can proceed
after the findings above are addressed.

## Resolution (2026-08-08)

All nine findings addressed in the post-review commit:

1. **switch-model no-op** — the decision now compares the COMPLETE target
   identity (`SchemaMigrator.TargetIdentity`, the same derivation the switch
   stamps): same model+dims on a different provider space, or with changed
   transforms, proceeds. Tests: `SwitchModelIdentityNoOpTests` (hosted
   same-name proceeds; prefix change proceeds; identical identity still
   no-ops).
2. **Stale-environment migration** — `docs/deploy-docker.md` now prescribes
   `docker compose run --rm --no-deps mcp ...` with the rationale, both
   directions.
3. **Drift vs health** — a standing `SentinelDriftKey` folds into the widened
   `modelMismatch` (degrading `/health` and `/up` alike, since `/up` derives
   from the same report). Test:
   `A_standing_sentinel_drift_marker_degrades_health`.
4. **Upgrade prerequisite** — `ops/UPGRADING.md` documents the
   create-empty-secret preflight for existing compose installations.
5. **omit-policy lifecycle** — `Request:Model` is required for every hosted
   profile as the database's local model identity, wire omission unchanged.
   Test: `Omit_model_policy_still_requires_a_local_model_identity`.
6. **Telemetry visibility** — the logging observer emits at Information
   (bounded volume, identifiers/counts only), matching the documented
   operator guidance.
7. **Health probe cost** — the singleton `HealthService` caches the
   probe+digest pair for 15 s, coalescing the healthcheck and multiple
   monitors onto one real (paid, under hosted profiles) embed. Test:
   `Rapid_health_checks_coalesce_onto_one_real_probe`.
8. **Hosted next-steps** — `switch-model` renders protocol-appropriate
   guidance (key placement, service recreation, sentinel expectations) and
   drops the Ollama-only text for hosted profiles.
9. **Same-model claim** — proposal and README now state same-model hosting is
   a distinct asserted space that passes the normal gate.
