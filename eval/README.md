# Search-quality evaluation

Mailvec ships a small evaluation harness so you can make measured changes to
search behaviour (chunk size, RRF k, embedding model, prompt rewriting, …)
instead of eyeballing it. You hand-label a small set of "I remember an email
about X" → expected message-ids, and the `mailvec eval` command runs that set
through keyword / semantic / hybrid and reports NDCG@10, MRR, and Recall@10.

The query file stays **out of the repo**. By default it lives at
`~/.local/share/mailvec/eval/queries.json`. The sample in this directory
(`queries.sample.json`) shows the format and is the only thing that's checked
in.

## Quick start

```sh
# 1. Add a query interactively. Runs the search, you mark relevant results by
#    rank, the labeled query is appended to ~/.local/share/mailvec/eval/queries.json.
mailvec eval-add "lease renewal landlord" --mode hybrid

# 2. Run the full set across all three modes, see aggregate metrics.
mailvec eval

# 3. Save a baseline before tuning, tune, compare.
mailvec eval --json baselines/2026-04-28.json
# ...change something, e.g. chunk size or RRF k...
mailvec eval --baseline baselines/2026-04-28.json
```

## Query file format

Identity of expected results is the **RFC Message-ID header** (e.g.
`<CAEx9pT...@mail.gmail.com>`), not the SQLite row id — Message-IDs are stable
across reindexes; the row id is not.

```json
{
  "version": 1,
  "queries": [
    {
      "id": "q001",
      "query": "lease renewal landlord",
      "filters": {
        "folder": "INBOX",
        "dateFrom": "2024-01-01T00:00:00Z"
      },
      "relevant": [
        "<CAEx9pTabc...@mail.gmail.com>",
        "<20240315.abc123@example.com>"
      ],
      "notes": "Free-text reminder of why this query exists."
    },
    {
      "id": "q002",
      "query": "anthropic invoice",
      "relevant": [
        { "messageId": "<billing-2024-03@anthropic.com>", "grade": 3 },
        "<billing-2024-04@anthropic.com>"
      ]
    }
  ]
}
```

### Fields

| Field | Required | Notes |
| --- | --- | --- |
| `version` | yes | Currently `1`; schema version, bumped on breaking change. |
| `queries[].id` | yes | Stable handle, must be unique. The bootstrapper auto-generates `q001`, `q002`, … but any string works. |
| `queries[].query` | yes | The natural-language query. Same string is fed to BM25, vector, and hybrid. |
| `queries[].relevant` | yes | List of expected-relevant Message-IDs. Each entry is **either** a bare string (binary, grade=1) **or** an object `{messageId, grade}` (graded relevance, grade ≥ 1). Mixing both forms in one query is fine. |
| `queries[].filters` | no | Optional. Mirrors `Mailvec.Core.Search.SearchFilters` 1:1 — labeled queries exercise the same WHERE clause path as production. See below. |
| `queries[].notes` | no | Free-text. Ignored by the eval; useful for future-you. |

### Filters

| Field | Type | Maps to |
| --- | --- | --- |
| `folder` | string | `SearchFilters.Folder` (exact match, e.g. `"INBOX"`, `"Archive.2023"`) |
| `dateFrom` | ISO 8601 string | `SearchFilters.DateFrom` (inclusive lower bound on `messages.date_sent`) |
| `dateTo` | ISO 8601 string | `SearchFilters.DateTo` (inclusive upper bound) |
| `fromContains` | string | `SearchFilters.FromContains` (case-insensitive substring match against `from_address` OR `from_name`) |
| `fromExact` | string | `SearchFilters.FromExact` (case-insensitive exact match against `from_address`; takes precedence over `fromContains`) |

Use ISO 8601 with an explicit offset for dates (e.g. `"2024-01-01T00:00:00Z"`).
The bootstrapper accepts the looser `yyyy-MM-dd` form on its `--date-from` /
`--date-to` flags and normalizes to UTC midnight on save.

### Round-trip behaviour

The bootstrapper preserves your hand-edited form on save:

- `relevant` entries with `grade == 1.0` are written back as bare strings.
- `relevant` entries with any other grade are written as `{messageId, grade}` objects.
- Comments in your JSON are **not** preserved — the file is parsed and rewritten.
  Use the `notes` field for anything you want to remember.

## Metrics

All three are reported; the suggested headline is **NDCG@10**.

- **NDCG@k** — Normalized Discounted Cumulative Gain. Rank-aware (rewards
  relevant docs appearing early), supports graded relevance. `1.0` is the
  ideal ranking; `0.0` means no relevant doc in the top-k.
- **MRR@k** — Mean Reciprocal Rank. Per-query value is `1/rank` of the first
  relevant result; `0` if none in top-k. Simple, only cares about the first
  hit.
- **Recall@k** — Fraction of relevant docs that appeared in the top-k. Useful
  if you plan to re-rank with an LLM downstream.

Per-query values are averaged with equal weight to produce the aggregate.

## Tuning workflow

1. **Establish a baseline.** With your current build, `mailvec eval --json baselines/<date>.json`. Commit the baseline next to the change you're about to make so you can roll back easily (the report doesn't contain Message-IDs, only ids and metric values, so it is *not* sensitive).
2. **Make one change.** Tune one knob at a time — `HybridSearchService.RrfK`, `ChunkingService` chunk size, `Ollama:EmbeddingModel`, …
3. **Run `mailvec eval --baseline baselines/<date>.json`.** The diff column tells you whether the change moved the needle, and the per-query section shows which queries flipped.
4. **Grow the query set when needed.** A regression on a single query isn't always bad — sometimes the labels are wrong. Use `mailvec eval --query <id> --mode hybrid` to inspect, and `mailvec eval-add` to grow the set when you spot a bad result during real use.

## Other consumers

The query format is deliberately small. Anything that can read JSON can produce
a query set, and the metrics implementation in
`src/Mailvec.Core/Eval/EvalMetrics.cs` is pure functions over an ordered list
of result-ids and a grade map — drop-in usable from another evaluator.

The default location precedence is:

1. `--queries <path>` flag, if set.
2. `~/.local/share/mailvec/eval/queries.json` otherwise.

Tilde expansion uses `Mailvec.Core.PathExpansion` (handles `~`, `${HOME}`, `$HOME`).
