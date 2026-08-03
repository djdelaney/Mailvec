# Uptime Kuma monitoring for Mailvec

Runbook for configuring [Uptime Kuma](https://github.com/louislam/uptime-kuma)
to watch the Mailvec homelab deployment. Self-contained — hand this to an agent
or operator and they can build the monitors without further context.

## What this monitors, and why external

Mailvec runs as a Docker compose stack (indexer, embedder, mcp, mbsync) behind
a Cloudflare Tunnel + Access. The `mcp` container serves an `/up` endpoint
that reports the whole pipeline's state as JSON, including per-service
**liveness** for the three background workers (there is no `launchctl` in a
container, so liveness travels through this endpoint rather than process
inspection).

**Point Kuma at the public Cloudflare hostname, not an internal address.** An
internal probe can't see the failures that actually make Mailvec unreachable to
its clients: cloudflared crashed, the tunnel dropped, Access misconfigured,
DNS/cert problems. Polling `https://mailvec.<domain>/up` end-to-end
exercises the exact path a real client takes.

`/up` is intentionally forwarded through the tunnel for this purpose. The
mail-bearing `/tray/*` endpoints are **not** reachable (disabled at the origin
and 404'd at the tunnel) — do not try to monitor them.

## Prerequisites: a scoped Cloudflare Access service token

The endpoint is behind Cloudflare Access, so Kuma authenticates with an Access
**service token** (headers `CF-Access-Client-Id` / `CF-Access-Client-Secret`).

1. In Cloudflare Zero Trust → **Access → Service Auth → Service Tokens**, create
   a token (e.g. `uptime-kuma`). Note the Client ID and Client Secret.
2. **Scope it to `/up` only.** Create (or use) a **path-scoped Access
   application** for `mailvec.<domain>/up` with a **Service Auth** policy
   that includes this token. A more-specific path app takes precedence over the
   root identity app, so the token can reach `/up` but **not** `/health` or the MCP
   endpoint or anything else on the hostname. This matters: without path
   scoping, a token that leaks from Kuma's store could read mail via the MCP
   surface.
3. Service tokens **expire** (default 1 year). Set a reminder to rotate — an
   expired token makes every monitor go red as if the tunnel were down.

## The endpoint

| | |
|---|---|
| URL | `https://mailvec.<domain>/up` |
| Method | GET |
| Auth | headers `CF-Access-Client-Id: <id>.access`, `CF-Access-Client-Secret: <secret>` |
| Success body | JSON, HTTP **200** |
| Degraded body | same JSON shape, HTTP **503** (Ollama unreachable, embedding-model mismatch, or embedder stuck) |

In Kuma's monitor config, put the two headers in the **Headers** field as JSON:

```json
{ "CF-Access-Client-Id": "<id>.access", "CF-Access-Client-Secret": "<secret>" }
```

## The `/up` response (field reference)

> **Changed in 0.1.37.** These monitors used to poll `/health`. They now poll
> `/up`, and the Kuma token is scoped so it can no longer reach `/health` at
> all. **The JSONata queries below are unchanged** — `/up` carries the same
> paths — so the migration is a URL edit on six monitors and nothing else.
>
> Why: `/health` returns the archive's filesystem path, corpus and chunk
> counts, the embedding model and its dimensions, embedder failure detail, and
> the internal Ollama LAN URL. A credential that only needs to prove liveness
> shouldn't receive any of that, and this one lives in Kuma's config store.
> `/up` is the same signals with the values stripped: booleans yes, values no.

A healthy response:

```json
{
  "status": "ok",
  "version": "0.1.37",
  "embeddings": { "modelMismatch": false },
  "ollama": { "reachable": true },
  "embedder": { "stuck": false },
  "services": [
    { "service": "indexer",  "known": true, "stale": false },
    { "service": "embedder", "known": true, "stale": false },
    { "service": "mbsync",   "known": true, "stale": false }
  ]
}
```

Note what is deliberately **not** here versus `/health`: `ollama.baseUrl`,
`embeddings.coveragePct` / model name / dimensions, `embedder.consecutiveFailures`
and failure timestamps, `services[].expectedIntervalSeconds` and beat times, and
the whole `database` block. If a monitor ever needs one of those, that's a
conversation about the trust boundary, not a field to add — see
`docs/security.md`.

Fields the monitors use:

| Field | Meaning | Healthy value |
|---|---|---|
| `status` | overall — `"degraded"` (HTTP 503) if Ollama down / model mismatch / embedder stuck | `"ok"` |
| `services[].stale` | a worker **was** beating and stopped (dead/wedged for ≥3 missed beats) | `false` |
| `services[].known` | whether the worker has ever beaten (`false` = never started, or still starting) | `true` in steady state |
| `embedder.stuck` | embedder can't drain its embedding backlog | `false` |
| `embeddings.modelMismatch` | DB's embedding model disagrees with config (vector-space corruption) | `false` |
| `ollama.reachable` | the embedding model server answers | `true` |

**`stale` vs `known`:** a freshly-started worker reads `known:false, stale:false`
for up to one beat interval, then flips to `known:true`. `stale:true` only ever
means "was alive, now isn't." Monitor on `stale`, not on `known` — see
[the deploy-window note](#tuning) below.

## Monitors to create

Use Kuma's **HTTP(s) – JSON Query** monitor type (evaluates a JSONata
expression and string-compares the result to an expected value). Granular
monitors — one per failure mode — are recommended so the Kuma dashboard names
*which* thing broke rather than a generic "Mailvec down."

All at URL `https://mailvec.<domain>/up`, with the header block above, and
**Accepted Status Codes `200-299, 503`** (critical — see next section):

| Monitor name | JSON Query (JSONata) | Expected value |
|---|---|---|
| `mailvec-indexer` | `services[service='indexer'].stale` | `false` |
| `mailvec-embedder` | `services[service='embedder'].stale` | `false` |
| `mailvec-mbsync` | `services[service='mbsync'].stale` | `false` |
| `mailvec-embedder-stuck` | `embedder.stuck` | `false` |
| `mailvec-model-mismatch` | `embeddings.modelMismatch` | `false` |
| `mailvec-ollama` | `ollama.reachable` | `true` |

If you prefer a single monitor over granularity, use this instead (returns a
boolean; covers degraded status **and** any stale worker):

```
status = 'ok' and $count(services[stale = true]) = 0
```
Expected value `true`. Downside: the alert just says "Mailvec unhealthy" — you
curl to find out why.

## The setting that is currently biting you: Accepted Status Codes → `200-299, 503`

> **Measured 2026-08-01: this is NOT set.** All six monitors read
> `["200-299"]` in Kuma's live `monitorList`. This section used to be written as
> if it were configured; it never was. Treat what follows as a **fix to apply**,
> not a setting to preserve.

`/up` returns **503** whenever degraded. Kuma marks any non-2xx **down before
evaluating the JSONata**, so with the current `200-299` every monitor pointed at
this endpoint trips together — including `mailvec-indexer` during an Ollama
reboot, when the indexer is perfectly fine. That is present behaviour, not a
hypothetical: **every Ollama restart already reds all six.**

Accepting `503` makes each monitor's JSONata the sole decider, so they stay
independent and each reports only its own failure.

**Add a seventh monitor at the same time**, on overall status:

| Monitor name | JSON Query (JSONata) | Expected value |
|---|---|---|
| `mailvec-status` | `status` | `ok` |

This is what makes accepting 503 *safe* rather than merely quieter. Accepting
503 means a degraded response no longer trips anything by itself — only the
JSONata does. Today that loses nothing, because the three conditions that
produce a 503 are exactly the three the dedicated monitors cover:

| 503 cause (`HealthService`) | Covered by |
|---|---|
| Ollama unreachable | `mailvec-ollama` |
| embedding-model mismatch | `mailvec-model-mismatch` |
| embedder stuck | `mailvec-embedder-stuck` |

But that correspondence is a coincidence of the current code, not an invariant.
Broaden the degraded set in `HealthService` and the new condition becomes
**invisible** — 503 accepted, no query covering it, six green monitors and a
degraded server. `mailvec-status` closes that permanently: it goes red on any
degraded status, covered or not. (Note `services[].stale` is deliberately *not*
part of `status`, which is why the three stale monitors still earn their place.)

## Tuning

- **Check interval: 60s.** Matches the workers' beat cadence; faster is wasted.
  (mbsync beats every 600s but staleness scales with each service's own
  `expectedIntervalSeconds`, so a 60s poll is fine for it too.)
- **Retries: 1–2 before alerting.**
- **Deploy windows are self-quieting** with the `stale`-based monitors: a
  restarting worker reads `known:false, stale:false`, which does **not** trip a
  `stale = true` check. So you won't get paged on every `docker compose up -d`.
  (A `stale:true` reading already means the worker missed 3 beats ≈ 3 min, so
  it's a real outage by the time Kuma sees it — keep retries low for prompt
  paging.)

## Notification severity (optional but worth it)

The granular split lets you assign different urgencies. Suggested:

- **Page hard:** `mailvec-indexer`, `mailvec-embedder`, `mailvec-mbsync`,
  `mailvec-embedder-stuck`, `mailvec-model-mismatch` — these are real pipeline
  failures.
- **Notify softer:** `mailvec-ollama` — the Ollama GPU VM rebooting is routine,
  and keyword search still works while it's down. The bundled 503 conflates
  this with the serious cases; splitting it out is the main reason to prefer
  granular monitors.

## Known gap: a container that never starts

The `stale`-based monitors do **not** catch a worker that never starts at all —
it sits at `known:false` forever (no prior beat to go stale from). In practice:

- If the **whole stack** or the `mcp` container is down, `/up` is
  unreachable and every monitor goes down anyway — covered.
- If a **single worker** container fails to start while `mcp` stays up, it shows
  `known:false` indefinitely and the `stale` checks miss it.

To cover that case, either add a Kuma **Docker Container** monitor (via the
Docker socket) watching the four containers are running, or add a monitor on
e.g. `services[service='indexer'].known` expecting `true` **with 2–3 retries**
(the retries ride out the normal startup window where `known` is briefly false).

## Complementary native signal: Cloudflare tunnel health

Turn on Cloudflare's own tunnel-health notification (Zero Trust → **Settings →
Notifications**, tunnel health). It's the most direct "the tunnel died" detector,
free, and faster than an HTTP poll cycle. It does **not** catch "tunnel up but
`mcp` origin down," so it complements the Kuma checks rather than replacing them.

## Verify

After creating the monitors, confirm the endpoint answers as expected:

```bash
# Should return HTTP 200 + the /up JSON:
curl -i -s \
  -H "CF-Access-Client-Id: $CF_ID" -H "CF-Access-Client-Secret: $CF_SECRET" \
  "https://mailvec.<domain>/up"

# Should be DENIED by Access. This is the check that proves the split works:
# the monitoring token must not reach the detailed body.
curl -i -s \
  -H "CF-Access-Client-Id: $CF_ID" -H "CF-Access-Client-Secret: $CF_SECRET" \
  "https://mailvec.<domain>/health"

# Should return HTTP 404 (mail surface is closed — must NOT return JSON):
curl -i -s \
  -H "CF-Access-Client-Id: $CF_ID" -H "CF-Access-Client-Secret: $CF_SECRET" \
  "https://mailvec.<domain>/tray/folders"
```

If `/up` returns a `302`/login page instead of JSON, the service token
isn't authorized on the path-scoped Access app yet.

**If `/health` returns JSON, stop.** The token is still admitted by the root
application — most likely its Service Auth policy still reads *Any Access
Service Token* — and the monitoring credential can currently reach the whole
MCP surface, i.e. the mailbox. That's a scoping bug, not a monitoring one.

## Notes on Kuma's JSON Query monitor

- It uses **JSONata**; the query must return a **scalar** (boolean/string/number),
  not an object or array — the compound expressions above all return a boolean.
- The Expected Value is a **string comparison** against the stringified result
  (so boolean `false` matches the expected value `false`). There is no
  operator/threshold dropdown for this monitor type as of early 2026 — a single
  query + expected value, which is why compound checks are written as one
  JSONata boolean expression.
- Requires a reasonably current Kuma (the JSON Query monitor type; ≥ v1.23).

## References (Mailvec repo)

- `docs/security.md` → "`/up`, `/health` and `/tray/*`" — why `/up` is forwarded
  single-layer and `/tray/*` is closed.
- `docs/remote-access-cloudflare.md` — the tunnel + Access setup, and the
  service-token path-scoping note.
- `src/Mailvec.Core/Health/ServiceHeartbeat.cs` — the liveness contract
  (beat cadence, staleness threshold, `known` vs `stale`).
- `src/Mailvec.Core/Health/HealthService.cs` — `UpReport` is the `/up` body;
  `HealthReport` is `/health`'s.
