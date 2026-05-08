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

If `baselines/` is empty when you read this, capture one before opening the
Phase 5 branch — it's a few minutes of `mailvec eval --json` against your
archive, and it's the only way to detect a quality regression introduced by a
change you didn't think affected ranking.
