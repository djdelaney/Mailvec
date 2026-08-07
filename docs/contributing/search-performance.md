# Search performance — investigation notes

A playbook for diagnosing MCP search latency, plus the findings from the
first investigation (2026-06, ~74k messages / 322k chunks, 24 GB machine). Read
this before "optimising" search — most of the obvious suspects are already ruled
out below.

This is about **latency**, not retrieval **quality**. For quality regressions
(ranking, recall) use `mailvec eval` and the `baselines/` snapshots instead.

## TL;DR root cause

- `search_emails` defaults to **hybrid** mode; the CLI `mailvec search` (no
  flags) defaults to **keyword**. Hybrid embeds the query (Ollama) and runs a
  vector KNN scan; keyword is pure FTS5. Comparing MCP-hybrid to CLI-keyword is
  apples-to-oranges — the CLI looks ~10× faster because it's doing less work.
  Compare like-for-like with `mailvec search --hybrid`.
- The dominant per-search cost in hybrid/semantic is the **vector KNN scan over
  ~1.2 GB of chunk vectors** (322k × 1024 × float32), served from the OS file
  cache + SQLite page cache. Warm it's ~0.3 s. It cools *gradually* over tens of
  seconds of idle as the OS reclaims those pages: ~0.6 s at 20 s idle, ~1.5 s at
  40 s. sqlite-vec does a brute-force scan, so every query touches all vectors
  regardless of `k`/`limit` — which is also why any throwaway query warms it.
- **Not** the bottleneck, with evidence:
  - HTTP / loopback overhead — keyword search over the same endpoint is ~0.01 s.
  - Ollama embedding — a direct `POST /api/embed` is ~0.02 s, dead flat.
  - The MCP being a separate long-running process — if anything it's *faster*
    than the CLI once warm (no per-invocation process/JIT startup).
- **The big 4–6 s spikes are post-restart cold start** — a freshly spawned
  .NET process pays one-time JIT / tiered-compilation + initial cache fill over
  its first ~dozen queries. This is per `ops/redeploy.sh mcp` (or any restart),
  **not** per search. If you just redeployed, that's what you're seeing.
- It is **not** macOS App Nap / process throttling. Discriminators:
  - Keyword search stays ~0.03 s even after 12 s idle (a throttled process would
    slow this too).
  - Keeping the process busy with keyword pings (which don't touch the vectors)
    does **not** keep hybrid warm — only touching the vectors does. So the warm
    state lives in the vector data cache, not in process scheduling.

## Mitigation

**There is none right now, and that's a deliberate consequence of removing the
tray.** A `POST /tray/warm` endpoint used to run a throwaway hybrid query to pull
the vectors into cache (and warm Ollama); the tray fired it on search-pane open,
so the warm ran behind the user's typing and the first real search landed warm at
zero standing cost. Both the endpoint and its only caller are gone. An idle
server now pays the full cold penalty (~1 s at the measurements below) on the
first search.

Two things to know if you reinstate one:

- **A caller has to fire it.** The warm only worked because the tray knew the
  user was *about to* search. Nothing in the MCP path has that signal — by the
  time a `tools/call` arrives, the search is already happening. Any replacement
  needs either a client that pre-warms or a periodic tick.
- **A periodic keep-warm tick was rejected before, and the reasoning still
  holds.** It would keep the cache hot continuously, but the CPU wake-ups (every
  ~20–30 s) are a real laptop battery drain — not worth ~1 s on an occasional
  search, given the steady-state cold penalty is ~1 s, not 6 s. Less relevant on
  an always-on homelab VM than it was on a laptop, which is the one thing that
  has changed; re-measure on the target hardware before assuming either way.

## Resource facts

- MCP process RSS is ~22 MB — **the vectors are NOT in the .NET heap**; they live
  in the OS file cache (reclaimable). "Keeping warm" pins ~1.2 GB of *reclaimable*
  file cache, not a hard process allocation; the OS evicts it under pressure.
- `archive.sqlite` ~4.3 GB (+ ~2 GB WAL); raw vectors ~1.22 GB.
- The CLI spawns a fresh process per call (~0.13 s startup) so it never has a warm
  in-process connection — yet matches the warm server because the OS file cache is
  shared across processes.

## How to re-measure

Hit the MCP endpoint directly (server must be running on `127.0.0.1:3333`).
Stateless Streamable HTTP takes a bare `tools/call` with no handshake and no
session header, so a single curl is the whole measurement. curl's `time_total`
is the client-observed end-to-end time (connect + request + transfer); over
loopback that's effectively the server's wall clock.

```sh
URL=http://127.0.0.1:3333
call(){ curl -s -o /dev/null -w "%{time_total}s\n" -X POST "$URL/" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_emails",
       "arguments":{"query":"artemis","mode":"'"$1"'","limit":20}}}'; }
hy(){ call hybrid; }
kw(){ call keyword; }

# Warm vs cold: hammer, then vary idle and watch the first-call latency climb.
for i in $(seq 6); do hy >/dev/null; done; hy          # warm baseline (~0.3s)
for T in 5 20 40; do for i in 1 2 3; do hy >/dev/null; done; sleep $T; printf "idle ${T}s: "; hy; done

# Is HTTP/process the cost? Keyword over the same endpoint is the floor (~0.01s).
kw

# Is Ollama the cost? Direct embed, no Mailvec code (~0.02s, flat):
curl -s -o /dev/null -w "embed: %{time_total}s\n" http://localhost:11434/api/embed \
  -d '{"model":"mxbai-embed-large","input":["artemis"],"keep_alive":"30m"}'

# App Nap vs data cache: does keyword pay a first-after-idle penalty? (No → not throttling.)
for i in 1 2 3; do hy >/dev/null; done; sleep 12; printf "kw after idle: "; kw
```

CLI like-for-like comparison (fresh process each time):

```sh
time mailvec search --hybrid artemis   # vs the keyword default
ollama ps                              # is mxbai-embed-large resident? cold load ~2s
```

## If search gets slow again, check in this order

1. `mailvec status` — confirm message/chunk counts on the path the MCP resolved.
   A fresh empty DB at the wrong `DatabasePath` "searches fast" with zero results
   (`SchemaMigrator` silently creates one — see CLAUDE.md "Search").
2. **Mode** — is the client sending hybrid/semantic? Keyword is ~0.01 s.
3. `ollama ps` — is `mxbai-embed-large` resident? Cold model load is ~2 s
   (`Ollama:KeepAlive` defaults to 30 m; bump it or rely on the pane-open warm).
4. **Was the MCP server just restarted?** First ~dozen queries pay JIT warmup.
5. Re-run the curl sweep above to separate warm steady-state from cold-idle and
   post-restart effects before changing any code.
