# Embedding-model experiments (A/B against the live archive)

> Findings from past runs are at the bottom under **Experiment log** — read
> them before designing a new experiment; every caveat in this file was
> learned the hard way there.

How to benchmark a different embedding model (or chunk size) against the
current one using `mailvec eval`, without touching the live launchd services.
The trick is a **parallel database file** plus **env-var config overrides** —
env vars sit at the top of the config precedence chain (per-binary appsettings
→ shared `appsettings.Local.json` → env vars), so a one-off CLI or embedder
run can point at the experiment DB while the launchd agents keep serving the
real one.

## How model swapping works

- The vec0 vector table's dimension and the `metadata.embedding_model` /
  `embedding_dimensions` seed are substituted from `Ollama:EmbeddingModel` /
  `Ollama:EmbeddingDimensions` when a **fresh** DB is created
  (`SchemaMigrator.SubstituteEmbeddingConfig`).
- For an **existing** DB, `mailvec switch-model` is the sanctioned transition:
  it drops and recreates `chunk_embeddings` at the new dimension, clears
  `chunks`, re-queues every message (`embedded_at = NULL`), and updates the
  metadata the embedder validates on startup. All vectors are lost — that's
  inherent; vectors from different models can't be mixed.
- The embedder still refuses to start when config and metadata disagree.
  `switch-model` defaults its `--model`/`--dims` to the bound Ollama options,
  so running it under the same env vars the embedder will use makes drift
  impossible.
- `OllamaClient` L2-normalizes any vector that arrives unnormalized, so vec0's
  L2 KNN ranks cosine-equivalently for any model (no-op for mxbai, which is
  already normalized).

## Runbook

```sh
# 0. Baseline on the live DB (CLAUDE.md rule: never change ranking without one)
mailvec eval --json baselines/$(date +%F)-mxbai.json --timing

# 1. Copy the database. Checkpoint first — under WAL a naive cp can miss
#    un-checkpointed pages.
mailvec checkpoint
APPDIR="$HOME/Library/Application Support/Mailvec"
cp "$APPDIR/archive.sqlite" "$APPDIR/archive-qwen06b.sqlite"

# 2. Point this shell at the experiment DB + model. Always set model and dims
#    TOGETHER — OllamaClient validates returned vector lengths against dims
#    and fails loudly on a mismatch.
export Archive__DatabasePath="$APPDIR/archive-qwen06b.sqlite"
export Ollama__EmbeddingModel=qwen3-embedding:0.6b
export Ollama__EmbeddingDimensions=1024

# 3. Pull the model and rebuild the vector table
ollama pull qwen3-embedding:0.6b
mailvec switch-model --yes        # reads model/dims from the env vars

# 4. Re-embed. Runs the working-tree embedder under this shell's env vars;
#    the launchd embedder keeps running mxbai against the live DB.
dotnet run --project src/Mailvec.Embedder
#    Watch progress in another shell (same exports): mailvec status
#    Ctrl-C once coverage is 100%.

# 5. Eval against the same query set (queries are keyed by RFC Message-IDs,
#    so they resolve across DB copies) and diff against the baseline.
mailvec eval --baseline baselines/<date>-mxbai.json \
             --json baselines/<date>-qwen06b.json --timing
```

Repeat for other candidates — e.g. `qwen3-embedding:4b` with
`Ollama__EmbeddingDimensions=2560`. Different dimensions are fine: the vec0
table is rebuilt at whatever `--dims` says.

## Chunk size as a second knob

`Embedder__ChunkSizeTokens` (default 200) was sized for mxbai's 512-token
context. Long-context models (qwen3-embedding: 32K) make larger chunks viable
— try 384 and 512. Chunk size matters only at embed time, so a chunk-size
variant is: copy DB → `mailvec reindex --all` (same model, no dimension
change needed) → run the embedder with `Embedder__ChunkSizeTokens=512` → eval.
Change one variable per run — model and chunk size in the same experiment
can't be attributed.

## Caveats (each of these bit for real — don't skip)

- **VACUUM after the re-embed, before timing anything.** `switch-model`'s
  drop+rebuild frees ~a quarter of the file's pages; the new vectors land
  scattered into those holes and the vec0 KNN full-scan degrades to random
  I/O — observed 18.8s vs 2.7s per semantic query on a 4.4GB archive.
  `sqlite3 <db> "VACUUM INTO '<db>-vacuumed'"` and point the eval at the
  vacuumed file (or VACUUM in place with services stopped).
- **Instruction-tuned models need a query-side prefix.** qwen3-embedding
  embeds queries and documents asymmetrically: queries should be prefixed;
  documents stay plain. Skipping it measurably buries relevant documents
  (observed: q014's targets beyond top-100 unprefixed, ~rank 40 prefixed).
  Set `Ollama:QueryInstructionPrefix` (env:
  `Ollama__QueryInstructionPrefix=$'Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery: '`)
  — it's applied centrally in `VectorSearchService.SearchAsync`, so CLI,
  MCP, tray, and eval all get it. Leave it empty for symmetric models like
  mxbai. An eval of an instruction-tuned model without it *understates* the
  model badly — treat such numbers as a floor, not a verdict.
- **Date-filtered eval queries amplify any ranking loss into zeros.** The
  vector leg's filter escalation is capped at vec0's k=4096; if a model
  ranks the relevant chunks below that horizon, a filtered query returns
  nothing relevant and scores NDCG 0.0 even though the document was "only"
  moderately demoted. If every query in the eval set carries a date filter
  (they currently all do), aggregate deltas overstate quality differences
  between models. Consider adding unfiltered twins of a few queries.

- **Latency**: qwen3-embedding:4b is a 4B model — both ingest throughput and
  per-query embed latency drop. Always pass `--timing` so the eval JSON
  captures it; a quality win that doubles search latency may not be a win.
- **Model thrash**: alternating eval runs between models makes Ollama
  load/unload them (subject to `Ollama:KeepAlive`, default 30m). The first
  query after a swap pays a cold-load penalty; ignore the first run's latency
  or warm the model with a throwaway query.
- **Cleanup**: when an experiment ends, unset the env vars (or close the
  shell) and delete the `archive-*.sqlite` copy. Nothing else to undo — the
  live DB and launchd agents were never touched.
- **Switching the live DB for real**: update the shared
  `~/Library/Application Support/Mailvec/appsettings.Local.json` (Ollama
  section) *and* run `mailvec switch-model`, then `ops/redeploy.sh embedder
  mcp`. If the config still names the old model, the launchd embedder refuses
  to start and `/health` flips to degraded — that's the mismatch guard doing
  its job.

## Experiment log

### 2026-06-11 — qwen3-embedding:0.6b vs mxbai-embed-large (baseline)

**Setup**: 74,220-message archive copied to a parallel DB, `switch-model` to
qwen3-embedding:0.6b @1024d, full re-embed (~6.5h wall including an overnight
gap; ~200–465 msg/min on an M-series Mac mini, sharing Ollama with the live
stack). Eval: 44 queries, top-10, vs `baselines/2026-06-10-mxbai.json`.
Reports: `baselines/2026-06-10-mxbai.json`, `baselines/2026-06-11-qwen06b.json`.

**Headline (as run — see caveats before trusting)**:

| mode     | mxbai NDCG | qwen NDCG | Δ      |
|----------|-----------|-----------|--------|
| keyword  | 0.918     | 0.918     | =      |
| semantic | 0.843     | 0.548     | −0.296 |
| hybrid   | 0.944     | 0.820     | −0.124 |

**Three confounds were diagnosed, in order of discovery**:

1. **Fragmentation, not the model, caused a 7× semantic-latency regression**
   (p50 0.92s → 12.7s). `switch-model`'s drop+rebuild left ~164k free pages
   and the new vectors physically scattered; the vec0 KNN full scan ran at
   random-I/O speed, identically slow on repeat runs (so not a cache
   effect). `VACUUM INTO` fixed it: 18.8s → 2.7s per semantic CLI search,
   on par with the live DB. → Now step 4 in the runbook and in
   `switch-model`'s printed next-steps.
2. **Missing query-side instruction prefix buried relevant documents.**
   qwen3-embedding is trained for asymmetric retrieval; embedding the bare
   query put q014's two relevant messages beyond unfiltered top-100, while
   prepending `Instruct: Given a web search query, retrieve relevant
   passages that answer the query\nQuery: ` pulled them to ~rank 40 (mxbai:
   ranks 13/28). No re-embed needed to fix — the prefix is query-side only.
3. **All 43 scored eval queries carry date filters**, so the vec0 k=4096
   escalation ceiling converts "demoted below the horizon" into "returns
   nothing" — that's why per-query diffs show 1.0 → 0.0 cliffs rather than
   graceful degradation. Aggregate deltas therefore overstate the gap.

**Verdict (superseded same day — see the prefixed re-run below)**: initial
spot checks suggested qwen3-0.6b was simply worse; the full prefixed eval
proved otherwise.

### 2026-06-11 — qwen3-embedding:0.6b WITH query instruction prefix

Same DB (vacuumed, no re-embed — the prefix is query-side only), with
`Ollama:QueryInstructionPrefix` set to the standard qwen retrieval
instruction. Report: `baselines/2026-06-11-qwen06b-prefixed.json`.

| mode     | mxbai NDCG | qwen 0.6b unprefixed | qwen 0.6b prefixed |
|----------|-----------|----------------------|--------------------|
| semantic | 0.843     | 0.548                | **0.865** (+0.022) |
| hybrid   | 0.944     | 0.820                | 0.935 (−0.009)     |

The prefix recovered +0.32 semantic NDCG — it was essentially the entire
quality gap. **qwen3-0.6b at parity with mxbai overall** (slightly ahead on
semantic, statistical tie on hybrid), with a 64× larger context window in
reserve for chunk-size experiments. Residual concern: semantic-mode mean
latency ~8.3s (vs mxbai 1.1s) — the date-filtered escalation re-runs the
KNN scan up to ~5×; hybrid mode (what Claude uses) stays acceptable at
~1.8s vs 1.4s. Investigate before any live switch.

**Also observed during the run (unrelated but worth knowing)**: an mbsync
UID rename wave soft-deleted ~22k live-DB messages at 17:15Z and the indexer
resurrected all of them within minutes (Message-ID keying + content-hash
meant zero spurious re-embeds); the churn ballooned the live WAL to 1.9GB
until a `mailvec checkpoint`. The mass-delete + self-heal is by design, but
don't panic-restore from backup if you catch it mid-flight.

### 2026-06-12 — qwen3-embedding:4b @2560d, prefixed

Fresh copy → `switch-model` to 4b/2560 → ~25h wall re-embed (~255 msg/min,
faster than feared) → VACUUM INTO → prefixed eval.
Report: `baselines/2026-06-12-qwen4b-prefixed.json`.

| model (all prefixed where applicable) | semantic NDCG | hybrid NDCG | hybrid mean latency |
|--------------------------------------|---------------|-------------|---------------------|
| mxbai-embed-large @1024d (live)      | 0.843         | 0.944       | 1.4s                |
| qwen3-0.6b @1024d                    | 0.865         | 0.935       | 1.8s                |
| qwen3-4b @2560d                      | **0.887**     | **0.949**   | **21.5s**           |

The 4b is the first model to beat mxbai on *both* legs (semantic +0.044,
hybrid +0.005, MRR +0.059) — but search latency is unusable: the 2560-dim
vector set is ~2.8GB and every brute-force vec0 KNN scan (multiplied by the
filtered-query escalation rounds) pays for it. Even fully vacuumed, hybrid
p50 was 22s vs mxbai's 1.4s on the same hardware.

**Verdict**: mxbai stays live. qwen3-4b's quality edge is real but not
shippable at 2560d with brute-force KNN on this corpus size.

**Most promising follow-up**: qwen3 models are MRL-trained — embeddings can
be truncated to lower dimensions (e.g. 1024) and re-normalized with modest
quality loss. 4b@1024d-MRL would shrink the scan back to mxbai's size while
keeping most of the 4b's ranking gains. Requires a truncation step at the
embedding seam (`IEmbeddingClient`/`OllamaClient`) applied identically to
documents and queries — a small change now that the seam exists. Second
option: 0.6b is already at parity with 64× the context window, so chunk-size
experiments (384/512 tokens) on the retained 0.6b DB may pull it ahead of
mxbai for free.
