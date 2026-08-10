# Cloud smoke tests (Fireworks embeddings, mistral-ocr)

This deployment runs Ollama only — nothing in day-to-day use ever calls the
hosted embedding (`OpenAiCompatibleTransport`, i.e. Fireworks/OpenAI/Baseten
profiles) or hosted OCR (`MistralOcrClient`) code paths. `ci.yml` unit-tests
Mailvec's own parsing of those providers' response shapes
(`OpenAiCompatibleTransportTests`, `MistralOcrClientTests`), but only against
hand-authored HTTP stub fixtures — it never makes a real call. Between manual
experiments (the 2026-06 embedding A/B, the 2026-08-06 OCR bake-off), a real
break — Fireworks changing its response shape, Mistral moving the Azure
route, a key expiring — would be invisible until someone happened to run a
manual check.

`tests/Mailvec.CloudSmoke.Tests` closes that gap: it calls the **real**
Fireworks and Mistral APIs through the exact production registration code
(`EmbeddingRegistration.AddMailvecEmbedding`,
`VisionRegistration.AddMailvecVision`) that the embedder, MCP and CLI all use,
and `.github/workflows/cloud-smoke.yml` runs it weekly (plus on demand via
`workflow_dispatch`).

## Why this project is not in `Mailvec.slnx`

`tests/Mailvec.CloudSmoke.Tests.csproj` is deliberately **not** listed in
`Mailvec.slnx` and is not referenced by `ci.yml`. `dotnet build`/`dotnet
test` at the repo root (what `ci.yml`, `ops/coverage.sh`, and a plain local
`dotnet test` all do) resolves the solution file and therefore never touches
this project — it cannot accidentally run, and fail on missing credentials,
during normal PR CI or a contributor's local test run. Only
`cloud-smoke.yml` invokes it, by explicit path.

## Test data is entirely fabricated

Both smoke tests use synthetic, non-mail content — no real email, no data
pulled from anyone's archive:

- **Embedding**: reuses `EmbeddingSpace.SentinelTexts`
  (`src/Mailvec.Core/Embedding/EmbeddingSpace.cs`) — the same fixed,
  non-mail sentinel strings production already embeds every poll cycle for
  drift detection against a hosted provider.
- **OCR**: a fabricated document image,
  `tests/Mailvec.CloudSmoke.Tests/Assets/ocr-smoke-sample.png` — placeholder
  invoice text plus a fixed unique token (`SENTINEL-4F2A9C`) that the test
  asserts appears in the transcription. Generated with a throwaway SkiaSharp
  script; regenerate it the same way if the sample text ever needs to change
  (draw text on a canvas, encode PNG — see git history for the original
  script if needed).

This matters because the repo is public: nothing here is real correspondence
or PII, so there's nothing to scrub and nothing sensitive committed.

## What each test asserts

Both tests are intentionally **shape-only**, not long-term drift detection —
production already owns that (the sentinel-drift mechanism in
`EmbeddingSpace`, exercised live by the embedder against whatever hosted
profile is actually configured). These tests answer a narrower question:
"does the wire integration still work at all" — auth accepted, request shape
accepted, response parsed, output sane.

- `Embedding/FireworksSmokeTests.cs`: embeds the sentinel texts, asserts the
  call succeeds, vector count matches input count, each vector's width
  matches the configured `OutputDimensions`, every component is finite, and
  no vector is all-zero.
- `Vision/MistralSmokeTests.cs`: OCRs the fabricated sample, asserts the
  transcription contains the fixed sentinel token.

Both skip (log and return, not fail) when their API key env var isn't set —
so running the suite locally without credentials, or a future accidental
inclusion somewhere, degrades safely instead of failing red.

## Running locally

Export the same env vars `cloud-smoke.yml` sets (your own keys, in your own
shell — never share them in chat or commit them):

```sh
export Embedding__ActiveProfile=fireworks-smoke
export Embedding__Profiles__fireworks-smoke__Protocol=openai-compatible
export Embedding__Profiles__fireworks-smoke__ProviderId=fireworks
export Embedding__Profiles__fireworks-smoke__Endpoint=https://api.fireworks.ai/inference/v1/embeddings
export Embedding__Profiles__fireworks-smoke__Request__Model=accounts/fireworks/models/qwen3-embedding-8b
export Embedding__Profiles__fireworks-smoke__Request__EncodingFormat=float
export Embedding__Profiles__fireworks-smoke__OutputDimensions=4096
export Embedding__Profiles__fireworks-smoke__SpaceId=fireworks:qwen3-embedding-8b:4096:cloud-smoke
export Embedding__Profiles__fireworks-smoke__Auth__Scheme=bearer
export Embedding__Profiles__fireworks-smoke__Auth__ApiKey=<your Fireworks key>

export Vision__Provider=mistral
export Vision__Mistral__Endpoint=<your Azure AI Foundry resource URL>
export Vision__Mistral__Model=mistral-ocr-4-0
export Vision__Mistral__ApiKey=<your Mistral/Azure key>

dotnet test tests/Mailvec.CloudSmoke.Tests
```

Confirm the Fireworks values above (endpoint, wire model, dimensions) against
whatever was actually used for the 2026-06 embedding A/B / phase-6 sentinel
measurement — the ones checked into `cloud-smoke.yml` are best-effort
defaults sourced from a code comment, not a verified deployment.

## GitHub Actions setup (one-time)

Under **Settings → Secrets and variables → Actions**:

- **Secrets**: `FIREWORKS_SMOKE_API_KEY`, `MISTRAL_SMOKE_API_KEY`. A
  separate, low-quota key scoped just to this workflow is safer than reusing
  a production one.
- **Variables**: `CLOUD_SMOKE_MISTRAL_ENDPOINT` — the Azure AI Foundry
  resource URL (e.g. `https://<resource>.services.ai.azure.com`). Not a
  secret, but resource-specific, so it's a repo variable rather than a
  hardcoded value in the workflow.

After adding both, trigger the workflow once via **Actions → Cloud smoke →
Run workflow** to confirm it's green before relying on the weekly schedule.
