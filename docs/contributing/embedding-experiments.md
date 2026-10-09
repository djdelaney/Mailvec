# Embedding-model experiments (A/B against a copy of the archive)

> Findings from past runs are at the bottom under **Experiment log** — read
> them before designing a new experiment; every caveat in this file was
> learned the hard way there.

How to measure a different embedding model, dimension or chunk size against
the current one with `mailvec eval`, on a **copy** of an archive, without
touching the archive itself or anything serving it.

## Quick start

```sh
ollama pull qwen3-embedding:0.6b
ops/embedding-experiment.sh --name qwen06b \
    --profile ops/embedding-profiles/qwen3-embedding-0.6b.env \
    --source "$HOME/Library/Application Support/Mailvec/archive.sqlite"
```

[`ops/embedding-experiment.sh`](../../ops/embedding-experiment.sh) does the
whole run unattended, in a work directory
(`~/Library/Application Support/Mailvec/experiments/<name>/` by default):

1. **build** the CLI and embedder into the work directory, so a long run
   keeps its binaries whatever happens to the checkout (`build.txt` records
   the commit);
2. **copy** the source: a file-level clone (APFS `clonefile`, falling back to
   a plain copy) of the main file and its `-wal`, then `VACUUM INTO` the work
   copy. The source is never opened by SQLite;
3. **baseline**: eval on the unswitched copy under the shared config — same
   corpus, binaries, query set and machine as what follows, so the diff
   isolates the model. Skip it with `--baseline <report.json>`;
4. **switch-model --force** to the profile (always forced: a chunk-size-only
   profile is "already on this model" and would otherwise rebuild nothing);
5. **re-embed** with `Embedder:ExitWhenDrained=true` (the embedder exits when
   the queue is empty, or with code 1 after five consecutive failed cycles)
   and OCR off (it would add text the baseline never saw);
6. **VACUUM** (see the caveats);
7. **eval** against the baseline after one warm-up query, writing
   `report.json` and `eval.txt`.

Each stage is marked done in the work directory, so **re-running the same
`--name` resumes** — a 4B-model re-embed of the full archive took 25 h on the
Mac mini. The work directory remembers its profile and refuses a different
one. Delete the directory when you are finished; it holds a full copy of the
archive (and needs room for a second one during each VACUUM).

The script refuses to run while any Mailvec launchd agent is loaded (a
file-level copy of a database being written is not a database), ignores
inherited `Section__Key` environment overrides, and strips per-binary
`appsettings.Local.json` from its build output — so the only configuration
in play is the shared `appsettings.Local.json` plus the profile file. It
installs nothing, so it is safe on the frozen-corpus machine.

## Profiles

A profile file is shell assignments of .NET config overrides, sourced for
every step after the baseline. The candidates in
[`ops/embedding-profiles/`](../../ops/embedding-profiles/):

| File | What it tests | Status |
|---|---|---|
| `qwen3-embedding-0.6b.env` | qwen3 0.6b @1024 + query instruction | measured 2026-06-11 (tie with mxbai) |
| `qwen3-embedding-0.6b-chunk512.env` | the same with 512-token chunks | never run; compare against the 0.6b run |
| `qwen3-embedding-4b-1024.env` | qwen3 4b truncated 2560 → 1024 | never run; needs Matryoshka support (below) |
| `embeddinggemma-2-768.env` | embeddinggemma-2 @768, task prefixes | never run; prefixes confirmed from the model card, text-only tag not — read the file |
| `embeddinggemma-2-512.env` | the same truncated 768 → 512 | never run; half mxbai's scan width |

The keys a profile sets:

- `Embedding__ActiveProfile` and `Embedding__Profiles__<name>__*`: protocol,
  `Request__Model`, `OutputDimensions`, optionally `NativeDimensions`, and
  the four text transforms `Text__QueryPrefix` / `QuerySuffix` /
  `DocumentPrefix` / `DocumentSuffix`. Use a profile name without `-` (an
  environment variable name can't hold one); the shipped files all use
  `experiment`.
- `Embedder__ChunkSizeTokens` / `Embedder__ChunkOverlapTokens` for chunk-size
  runs. Overlap must stay at or below half the chunk size — the embedder
  refuses otherwise, because the chunker slides by `size - overlap` and a
  too-large overlap emits near-duplicate chunks.
- Anything else the embedder or CLI reads, e.g. `Ollama__BaseUrl` to embed
  on another host. **Not** `Archive__*`: the script owns the database path
  and refuses a profile that sets it.

Model and dimensions live in one file, so the old failure — setting one
without the other and getting a width mismatch hours into a run — can't
happen; a wrong width still fails loudly on the first batch.

### Matryoshka truncation (`NativeDimensions`)

Models trained with Matryoshka representation learning (qwen3-embedding,
embeddinggemma, mxbai-embed-large) front-load information, so the first N
values of a vector are a usable N-dimensional embedding. Set
`OutputDimensions` to the width to keep and `NativeDimensions` to the width
the model returns: `EmbeddingService` then requires vectors exactly
`NativeDimensions` wide, keeps the first `OutputDimensions` values, and
re-normalizes — once, for documents and queries alike. The vec0 table and
the space id use the kept width; the config hash records the truncation.

Two things no check can catch: truncating a model that **wasn't** trained for
it silently produces a worse space, and the quality cost of a given cut is
model-specific — measure it, don't assume it. A hosted profile can't combine
`NativeDimensions` with `Request__DimensionsParameter=send` (the provider
would already return the cut width).

## Reading the result

`eval.txt` holds the aggregate table, latency, and the baseline diff. Above
the deltas, the diff header lists what changed underneath the numbers —
vector space, config hash, model digest, message and chunk counts — and
warns if any message was left unembedded. `report.json` carries the same
provenance. **Hybrid decides**: production serves hybrid, and the 2026-08-08
Fireworks run showed a better vector leg can still make the fused ranking
worse.

## Caveats (each of these bit for real)

The script handles the first three; they stay here because they explain its
steps, and because they apply to any run done by hand.

- **VACUUM after the re-embed, before timing anything.** `switch-model`'s
  drop+rebuild frees ~a quarter of the file's pages; the new vectors land
  scattered into those holes and the vec0 KNN full-scan degrades to random
  I/O — observed 18.8s vs 2.7s per semantic query on a 4.4GB archive.
  *(Script: stage 6, and the copy itself is vacuumed so the baseline is timed
  on a compact file too.)*
- **Model thrash**: the first query after Ollama loads a model pays the
  cold-load penalty. *(Script: one warm-up query before each eval.)*
- **OCR during a re-embed changes the corpus.** The embedder's OCR pass would
  add text the baseline never saw. *(Script: OCR off for the re-embed.)*
- **Instruction-tuned models need a query-side prefix.** qwen3-embedding
  embeds queries and documents asymmetrically: queries should be prefixed;
  documents stay plain. Skipping it measurably buries relevant documents
  (observed: q014's targets beyond top-100 unprefixed, ~rank 40 prefixed).
  The prefix is applied centrally in `EmbeddingService.EmbedQueryAsync`, so
  CLI, MCP, and eval all get it. Some models (embeddinggemma) want a
  document prefix too. An eval of an instruction-tuned model without its
  prefixes *understates* the model badly — treat such numbers as a floor,
  not a verdict.
- **Date-filtered eval queries amplify any ranking loss into zeros.** The
  vector leg's filter escalation is capped at vec0's k=4096; if a model
  ranks the relevant chunks below that horizon, a filtered query returns
  nothing relevant and scores NDCG 0.0 even though the document was "only"
  moderately demoted. If every query in the eval set carries a date filter
  (all 43 scored queries did at the time of the 2026-06 experiment; unfiltered
  twins have since been added), aggregate deltas overstate quality differences
  between models.
- **Latency scales with dimensions, not just model size.** Every semantic
  query brute-force scans every stored vector, multiplied by the
  filtered-query escalation rounds: qwen3-4b at 2560d took hybrid search
  from 1.4s to 21.5s on the full archive. A faster embedding host shortens
  the query embed and the re-embed, not that scan, which runs wherever the
  database is. `--timing` is always on in the script.
- **The baseline scans soft-deleted mail; the experiment doesn't.** The
  archive keeps chunks for soft-deleted messages until `purge-deleted`, and
  every KNN scan reads them before the `deleted_at` filter drops them.
  `switch-model` re-embeds live mail only, so the experiment's vector set is
  smaller: on the frozen corpus 344,084 → 284,463 chunks (54,555 belonged to
  the 6,318 soft-deleted messages). Quality is unaffected — deleted mail is
  filtered out of results either way — but the latency delta flatters every
  candidate by roughly that 16%. For a fair latency comparison, re-embed an
  mxbai control through the same script.
- **A small corpus says little.** The 662-message subset corpus is right for
  checking that a run works; 70 queries over it can't separate models a few
  hundredths apart. Decide on the full archive.

## Switching the live database for real

Do it only after a winning experiment, and not on the frozen-corpus machine.
Every process that embeds or searches — embedder, MCP, CLI — must resolve the
same profile, so put it wherever that deployment's configuration lives (the
shared `appsettings.Local.json` for the launchd install; the compose
environment for the container, see `docs/deploy-docker.md`), run
`mailvec switch-model`, re-embed, then VACUUM. Until config and database
agree, the embedder refuses to write and semantic search refuses to serve —
that refusal is the identity guard doing its job, not a fault.

### Doing it by hand

The script is the runbook; to run a stage yourself, work on a copy, export
`Archive__DatabasePath=<copy>` plus the profile's variables in one shell, and
run the same commands the script does (`mailvec switch-model --yes --force`,
the embedder with `Embedder__ExitWhenDrained=true`, `sqlite3 <copy> "VACUUM
INTO '<file>'"`, `mailvec eval --baseline … --timing`). Never point
`Archive__DatabasePath` at the frozen corpus itself: `eval`, like most CLI
commands, runs schema migration on open.

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
   on par with the live DB. → Now the first caveat above and step 4 of
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

### 2026-08-08 — Fireworks qwen3-embedding-8b @1024 (hosted)

Recorded in [`baselines/subset-ocr/README.md`](../../baselines/subset-ocr/README.md):
semantic improved (+0.026 NDCG) but hybrid regressed (−0.006) and four
queries dropped by more than 0.2, failing the pre-registered gate. The
archive stayed on mxbai.

### 2026-10-07 — script verification on the subset corpus; mxbai 1024 → 512

First runs of `ops/embedding-experiment.sh`, against the 662-message subset
corpus (70 queries) with local Ollama 0.35.0. These check the machinery; the
corpus is too small to rank models.

- **The harness reproduces the committed baseline exactly**: the script's
  baseline stage scored keyword 0.9058 / semantic 0.8488 / hybrid 0.9055,
  identical to `baselines/subset-ocr/2026-08-07.json`. The source file's
  hash was unchanged afterwards.
- **mxbai-embed-large truncated 1024 → 512** (`NativeDimensions=1024`,
  `OutputDimensions=512`): semantic 0.858 (+0.009), hybrid 0.917 (+0.012),
  semantic latency −4 ms. Within noise at this size — but no loss from
  halving the vectors, which is what the scan-cost constraint above cares
  about. Worth repeating on the full archive before believing either way.
- A model that was never pulled stopped the run after five failed embed
  cycles (exit 1), and re-running resumed at the embed stage.
- **embeddinggemma-2:270m @768** (the shipped profile, Ollama 0.40.0):
  semantic 0.851 (+0.003), hybrid 0.909 (+0.004), semantic latency −5 ms
  (−20%); re-embed time about the same as mxbai. Parity on the aggregates,
  but heavy per-query churn underneath — hybrid had 4 queries gain more than
  0.2 and 3 drop more than 0.2 (q063 −0.43, q028 −0.37, q072 −0.37) — so the
  two models get *different* queries right. That is exactly where the
  Fireworks run failed its tail gate (4 drops), so the full-archive run
  should be judged on the tail, not the means.


### 2026-10-09 — full archive: embeddinggemma-2 @768 and @512, qwen3-embedding:4b @1024

Three runs of `ops/embedding-experiment.sh` on the full frozen corpus
(75,414 live messages, 70 queries, top-10), on a Mac Studio (M5 Max, 128 GB)
with Ollama 0.40.1 and commit `f6fc09e`. All three diff against the one
baseline measured in the first run. **That baseline reproduced
`baselines/2026-08-07-post-tray-removal.json` exactly** (keyword 0.8590 /
semantic 0.8141 / hybrid 0.9165), so the copy is the corpus the committed
baselines describe.

| profile | semantic NDCG | hybrid NDCG | hybrid drops > 0.2 | hybrid gains > 0.2 | re-embed |
|---|---|---|---|---|---|
| mxbai-embed-large @1024 (baseline) | 0.814 | 0.916 | — | — | — |
| embeddinggemma-2:270m @768 | 0.822 (+0.008) | 0.905 (−0.011) | 5 | 4 | 61 min |
| embeddinggemma-2:270m @512 (MRL) | 0.814 (=0.000) | 0.898 (−0.019) | 6 | 3 | 62 min |
| qwen3-embedding:4b @1024 (MRL) | **0.842 (+0.028)** | 0.897 (−0.019) | 6 | 2 | 5 h 21 min |

Against the gate (semantic ≥ +0.02, hybrid ≥ +0.01, at most 3 hybrid drops
over 0.2), **all three fail**. qwen3-4b@1024 passes the semantic leg and
fails the other two.

- **Tails.** gemma@768 drops q072 −0.61, q054 −0.50, q063 −0.43,
  q028 −0.37, q055 −0.29; @512 adds q047 −0.24. qwen drops q028, q066,
  q069 (−0.37 each), q055 −0.29, q065 −0.24, q047 −0.20. **q028 and q055
  drop under all three models**, and q063/q028/q072 were already the drops
  in the 2026-10-07 subset run of embeddinggemma-2. These queries are where
  mxbai's ranking is hard to replace; inspect them before the next
  candidate.
- **Truncation is cheap; the starting point isn't.** Diffing @512 directly
  against @768: −0.008 NDCG on both legs, and only one hybrid query moving
  more than 0.2 (q047 −0.61). Halving the vectors costs little, but @768 is
  already below mxbai on hybrid.
- **qwen3-4b truncated to 1024 kept its semantic edge and lost its hybrid
  one.** At 2560d (2026-06-12) it was +0.044 semantic / +0.005 hybrid; at
  1024d it is +0.028 / −0.019. Like the Fireworks 8b run, a better vector
  leg made the fused ranking worse.
- **Latency** (semantic / hybrid mean): baseline 442 / 673 ms; gemma@768
  299 / 491; gemma@512 266 / 430; qwen@1024 386 / 588. Every one of those
  improvements is partly the soft-deleted-chunks caveat above, not the model.
- **Re-embed throughput** on this machine: gemma ~77 chunks/s, qwen3-4b
  ~12.6 chunks/s (~235 msg/min). That is about the ~255 msg/min the
  2026-06-12 entry records for the 4b on the Mac mini, a rate that implies
  ~5 h for that corpus. Its 25 h wall time was therefore lost to something
  other than embedding speed. The entry doesn't say what; the 0.6b run
  before it records sharing Ollama with the live stack and an overnight gap.
  Plan a 4b re-embed at ~5–6 h, not overnight.

**Verdict**: mxbai-embed-large stays live. No candidate so far beats it on
hybrid on the full archive.
