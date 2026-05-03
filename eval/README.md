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
mailvec eval --baseline baselines/2026-04-28.json --timing
```

### Three ways to add a labeled query

| Path | When to use it |
| --- | --- |
| [`eval-add`](#bootstrapping-queries-with-eval-add) (interactive) | You have a query in mind but don't know which messages are the right answer — let the search show candidates and pick by rank. |
| [`eval-import`](#importing-queries-from-real-claude-usage-eval-import) | You've been using Claude, noticed a search you'd like to lock in as a regression test, and want to capture *Claude's exact phrasing* without retyping it. |
| [`eval-add --pin-relevant`](#pinning-known-relevant-message-ids) | You already know which messages are the answer (e.g. testing several paraphrases against the same target email). Skips the search-and-pick step entirely. |

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

### Pinning known-relevant Message-IDs

When you already know which messages are the right answer — typically when
testing **paraphrase or synonym variants of the same intent** — `--pin-relevant`
skips the search-and-pick prompt entirely. Pass one or more Message-IDs and
they're saved as the relevant set directly.

```sh
# Three differently-phrased queries against the same target message — fast
# regression coverage that semantic search isn't winning purely on shared keywords.
# Replace CAFP6Zzo7q@mail.gmail.com with the actual Message-ID (see "Finding
# Message-IDs" below for sources).
mailvec eval-add "tree work bartlett quote" \
    --pin-relevant "CAFP6Zzo7q@mail.gmail.com" --notes "keyword phrasing" --yes
mailvec eval-add "what did the arborist quote me for the trees" \
    --pin-relevant "CAFP6Zzo7q@mail.gmail.com" --notes "natural language" --yes
mailvec eval-add "tree removal estimate" \
    --pin-relevant "CAFP6Zzo7q@mail.gmail.com" --notes "synonym set" --yes
```

Notes:

- IDs are validated against the `messages` table. A typo aborts the save
  rather than silently writing a query whose "expected" answer is missing
  from the corpus (which would forever score 0).
- Repeat the flag, or pass space-separated IDs after a single flag (the option
  accepts multiple arguments per token).
- Graded relevance uses `<id>=<grade>`. Example:
  `--pin-relevant "primary@mail.gmail.com"=3 "secondary@mail.gmail.com"`
  marks the first as graded-relevance 3, the second as the default grade 1.
- Stored ids are bracket-less, but pasting a header value with surrounding
  `<>` brackets works — they're stripped before the lookup.

#### Finding Message-IDs to pin

| Where | How |
| --- | --- |
| `mailvec search` | Add `-i` / `--with-id` — every result gets an extra `id: <Message-ID>` line. |
| The MCP tool-call log | With `Mcp:LogToolCalls=true` set on the server, every `mcp-result` line includes the message ids Claude saw. Tail `~/Library/Logs/Mailvec/mailvec-mcp-*.log`. |
| Inside `eval-add` | The interactive candidate list shows each id even when you don't end up picking it. |
| Direct DB query | `sqlite3 ~/Library/Application\ Support/Mailvec/archive.sqlite "SELECT message_id, subject FROM messages WHERE subject LIKE '%foo%';"` for header-field grepping. |

## Importing queries from real Claude usage (`eval-import`)

Watching how Claude actually phrases queries against your archive is the best
source of realistic eval material — Claude's wording isn't yours, and queries
that look clever in isolation often fail in ways your hand-written set won't
catch.

`mailvec eval-import` reads the rolling MCP log, lists recent
`search_emails` calls (deduped), lets you pick one, and drops you into the
same labeling flow as `eval-add` with the **query and filters pre-filled**.

```sh
# One-time: turn on tool-call logging on the MCP server. Without this, the
# log only records timing, not the args/results that eval-import needs.
echo '{ "Mcp": { "LogToolCalls": true } }' > src/Mailvec.Mcp/appsettings.Local.json
# Restart the MCP server (or rebuild the MCPB bundle and toggle the extension).

# Use Claude as you normally would for a few days. Each search_emails call
# lands in ~/Library/Logs/Mailvec/mailvec-mcp-*.log as a structured line.

# Then:
mailvec eval-import
```

Output looks like:

```
Recent search_emails calls (3):

   1.  10m ago    plumbing leak under kitchen sink
        dateFrom=2026-01-01  ·  fromContains=bartlett
   2.  2h ago     vacation flight booking confirmation
   3.  yesterday  HOA dues 2026
        folder=INBOX  ·  dateFrom=2026-01-01

Pick a call by number (1–3), empty to abort:
```

Pick a number → label results from the candidate list → save. The query and
filters from Claude's call are used verbatim, so the resulting eval entry
exercises *Claude's actual phrasing*, not your reconstruction of it.

### Flags

```sh
# Look back further (default 20 most recent unique calls).
mailvec eval-import --limit 50

# Override the log directory (default: $MAILVEC_LOG_DIR or ~/Library/Logs/Mailvec).
mailvec eval-import --log-dir /some/other/path

# Combine with --pin-relevant when you know the target message and don't need
# the candidate-pick step. The query + filters come from the log; relevance
# from your IDs.
mailvec eval-import --pin-relevant "CAFP6Zzo7q@mail.gmail.com" --yes
```

### Caveats

- Requires `Mcp:LogToolCalls = true` (off by default — enabling it logs both
  the args sent and the result summary). If the flag isn't on, `mcp-call`
  lines aren't emitted and `eval-import` finds nothing to import.
- Only handles ranked queries. Browse-mode calls (no `query` string,
  date/folder filters only) are skipped — there's no relevance signal to
  capture for those.
- Identical retries collapse to one entry. Claude calling the same query
  twice in a row only shows up once in the picker.

## Query file format

Identity of expected results is the **RFC Message-ID** (e.g.
`CAEx9pTabc@mail.gmail.com`) — *without* the surrounding `<>` brackets that
appear on the wire. The schema stores message-ids bracket-stripped, so that's
the form the JSON file uses too. Tools that accept Message-IDs on the CLI
(`eval-add --pin-relevant`, `eval-import --pin-relevant`) strip a leading `<`
and trailing `>` automatically, so paste-from-a-header still works.

Message-IDs are stable across reindexes; the SQLite row id is not — never use
the row id here.

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
        "CAEx9pTabc@mail.gmail.com",
        "20240315.abc123@example.com"
      ],
      "notes": "Free-text reminder of why this query exists."
    },
    {
      "id": "q002",
      "query": "anthropic invoice",
      "relevant": [
        { "messageId": "billing-2024-03@anthropic.com", "grade": 3 },
        "billing-2024-04@anthropic.com"
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

1. **Establish a baseline.** With your current build, `mailvec eval --json baselines/<date>.json`. Commit the baseline next to the change you're about to make so you can roll back easily (the report doesn't contain Message-IDs, only ids and metric values, so it is *not* sensitive). Latency (mean / p50 / p95 per mode) is recorded automatically — no flag needed.
2. **Make one change.** Tune one knob at a time — `HybridSearchService.RrfK`, `ChunkingService` chunk size, `Ollama:EmbeddingModel`, …
3. **Run `mailvec eval --baseline baselines/<date>.json --timing`.** The quality columns (ΔNDCG / ΔMRR / ΔRecall) show whether the change moved the needle, and `--timing` adds a Δlatency table so you can see whether you traded quality for speed (or vice versa). The per-query section shows which queries flipped.
4. **Grow the query set when needed.** A regression on a single query isn't always bad — sometimes the labels are wrong. Use `mailvec eval --query <id> --mode hybrid` to inspect, and `mailvec eval-add` (or `eval-import` for real Claude phrasings) to grow the set when you spot a bad result during real use.

### Latency tracking

When scaling the corpus (e.g. importing a multi-year archive after testing on
a months-long subset), the brute-force vec0 kNN scan is the first thing to
slow down — it's linear in vector count. `--timing` makes this visible:

```sh
# Capture before:
mailvec eval --json baselines/pre-import.json
# ...do the import...
# Capture after, with diff:
mailvec eval --baseline baselines/pre-import.json --timing --json baselines/post-import.json
```

The Δp95 column for `semantic` and `hybrid` is the one to watch — if it grows
super-linearly with corpus size, the vec0 leg is the bottleneck and the next
move is either bumping `HybridSearchService`'s candidate-inflation factor or
moving to a partitioned vec0 index. Latency is recorded in the JSON report
regardless of whether `--timing` was passed, so old baselines stay
diff-comparable as long as they were captured with a build that has timing
support.

## Color output

`mailvec eval` colorizes its terminal output: query IDs in bold cyan, NDCG/MRR/Recall scored green (≥0.9) / yellow (≥0.7) / red (<0.7), ✓/✗ markers, and ↑/↓ regression arrows. Colors are auto-disabled when stdout is redirected (so `mailvec eval | tee` and `--json` writes stay clean) or when `NO_COLOR` is set ([no-color.org](https://no-color.org)). Force colors back on with `FORCE_COLOR=1` — useful for piping through `less -R` or capturing demo output.

## Other consumers

The query format is deliberately small. Anything that can read JSON can produce
a query set, and the metrics implementation in
`src/Mailvec.Core/Eval/EvalMetrics.cs` is pure functions over an ordered list
of result-ids and a grade map — drop-in usable from another evaluator.

The default location precedence is:

1. `--queries <path>` flag, if set.
2. `~/.local/share/mailvec/eval/queries.json` otherwise.

Tilde expansion uses `Mailvec.Core.PathExpansion` (handles `~`, `${HOME}`, `$HOME`).
