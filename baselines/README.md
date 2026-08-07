# Eval baselines

Snapshot output from `mailvec eval --json` against a real archive. Commit one
of these whenever you're about to make a change that could move retrieval
quality (chunk size, RRF k, embedding model, search-tool wiring), then
re-run after the change with `--baseline` to see the delta.

The query set itself is **not** here. Labeled queries reference Message-IDs
that only exist in your archive, so they live alongside the database at
`~/Library/Application Support/Mailvec/eval/queries.json` (see `eval/README.md`
for the format and curation flow).

## Capture a baseline

```sh
# One snapshot per code-shape change, named by the date (or PR number).
mailvec eval --json baselines/2026-05-08.json
```

Then make the change, re-run, compare:

```sh
mailvec eval --baseline baselines/2026-05-08.json --timing
```

## Why this matters before Phase 5

Phase 5 adds Gemini CLI / Codex CLI / ChatGPT desktop as MCP clients. Each
new client multiplies the surface area where a tool-shape regression can hide.
A committed baseline means changes that look like "just renaming a parameter"
or "just tweaking a description" can be evaluated against ground truth before
they ship.

## Snapshot provenance (what the JSON doesn't record)

The report format captures `ranAt`, `topK`, `querySetPath` and the per-mode
runs — **not** which archive, embedding model, or Ollama endpoint produced it.
So anything unusual about a run has to be written down here or it's lost.

- **`2026-08-07-post-tray-removal.json`** — confirmation run after the tray
  removal (−9,160 lines), the MCP SDK 2.0.0 → 2.1.0 bump, and AngleSharp
  1.7.0 → 1.7.1. **Delta vs `2026-07-10-q024-sealed.json` was exactly
  `0.000` on NDCG / MRR / Recall across all three modes** — bit-identical
  ranking, not merely "no regression". It is a *confirmation*, not a new
  reference point: the sealed 07-10 snapshot still describes the corpus.

  Two things about how it was produced, because neither is in the file:
  it ran against a **scratch copy** of the archive, not the archive itself
  (`mailvec eval` calls `SchemaMigrator.EnsureUpToDate`, and the frozen corpus
  is `schema_version = 8` against v9 code — running in place would have
  migrated it, which the copy confirmed by coming back v9); and it embedded
  queries against **localhost** Ollama rather than the LAN host the earlier
  snapshots used. The exact zeros are themselves the evidence that the second
  substitution is safe — same model, same query vectors, either endpoint.

## A note on the committed snapshots

The snapshots in this directory were captured against the **author's**
archive with the author's labeled query set. They are useful history for
changes made on that machine, but they are **not reproducible against your
archive** — `queries.json` references Message-IDs that only exist in the
archive it was curated on. If you're contributing a ranking-affecting change,
curate a small query set of your own (`mailvec eval-add`), capture your own
baseline before the change, and compare against *that* — a few minutes of
`mailvec eval --json`, and the only way to detect a quality regression
introduced by a change you didn't think affected ranking.
