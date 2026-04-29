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

## Bootstrapping queries with `eval-add`

`mailvec eval-add` runs a candidate query, prints numbered results, and lets you
mark which ones are relevant by rank — the labeled query is appended to your
query set. Adding a query while the search is in front of you is much cheaper
than curating Message-IDs by hand.

If you've installed `mailvec` on `PATH`, use it directly. From a source checkout
the equivalent is `dotnet run --project src/Mailvec.Cli -- eval-add ...`.

### Common patterns

```sh
# Plain — default --mode hybrid, top 10 candidates, binary relevance.
mailvec eval-add "lease renewal landlord"

# Pick the mode that's most likely to surface the canonical hit. Useful when
# you want to debug one mode in particular ("hybrid is missing this; can
# semantic find it on its own?").
mailvec eval-add "anthropic invoice"        --mode semantic
mailvec eval-add "exact subject line text"  --mode keyword

# More candidates to label from. Helpful when the obvious top-3 aren't the
# emails you remember.
mailvec eval-add "tokyo trip" --top-k 20

# Free-text note saved alongside the query. Future-you will thank you.
mailvec eval-add "lease renewal" \
    --notes "March 2024 back-and-forth with David about renewing"

# Skip the y/N confirmation (useful when scripting a batch of additions).
mailvec eval-add "..." --yes
```

### Filters

The filters mirror `SearchFilters` 1:1 — what you label here exercises the same
WHERE-clause path as production search.

```sh
# Restrict to one folder (exact match).
mailvec eval-add "team standup notes" --folder "Archive.2024"

# From-substring (case-insensitive against from_address OR from_name) — useful
# when you remember "it was from someone at United" but not the exact address.
mailvec eval-add "flight confirmation tokyo" --from-contains united

# From-exact — strictly narrower; matches against from_address only.
mailvec eval-add "billing alert" --from-exact "invoice@anthropic.com"

# Date range. Accepts ISO 8601 or yyyy-MM-dd; date-only is normalized to UTC midnight.
mailvec eval-add "lease renewal" --date-from 2024-01-01 --date-to 2024-06-30
mailvec eval-add "kickoff email"  --date-from 2024-03-15T00:00:00Z

# Filters compose; all are AND-ed (fromExact wins over fromContains if both set).
mailvec eval-add "Q1 invoice" \
    --folder INBOX \
    --from-contains anthropic \
    --date-from 2024-01-01 --date-to 2024-03-31
```

### Picking results

After the candidate list prints, you'll see:

```
Enter ranks of relevant results (e.g., '1 3 5'). Use 'N=G' for graded relevance.
Empty line to abort.
>
```

Syntax:

| Input | Meaning |
| --- | --- |
| *(empty)* | Abort — nothing is saved. |
| `1 3 5` | Ranks 1, 3, 5 are relevant (binary, grade=1). |
| `1=3 3 5` | Rank 1 has grade 3 (the canonical hit); ranks 3, 5 have grade 1. |
| `2,4,7` | Commas, spaces, tabs all work as separators. |

Out-of-range ranks and duplicates are skipped with a warning; the rest go through.

### Custom ids and locations

```sh
# Override the auto-generated id. Default is q001, q002, … incrementing past
# the highest existing q### in the file.
mailvec eval-add "..." --id "lease-renewal-david"

# Point at a different query file (e.g., a per-experiment set).
mailvec eval-add "..." --queries ~/work/mailvec-eval/finance-queries.json
```

The default location is `~/.local/share/mailvec/eval/queries.json`. The directory
is created on first save.

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
