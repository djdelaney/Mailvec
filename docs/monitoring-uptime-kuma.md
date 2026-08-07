# Uptime Kuma monitoring for Mailvec

Runbook for configuring [Uptime Kuma](https://github.com/louislam/uptime-kuma)
to watch a Mailvec deployment behind a Cloudflare tunnel. Self-contained — hand
this to an agent or operator and they can build the monitors without further
context.

> ## ⚠️ This document never records what your deployment is currently doing
>
> **It describes a target configuration and how to check whether you've hit
> it.** Kuma and Cloudflare config lives in their own control planes, not in
> this repo, so any sentence here claiming to know your live state would be a
> guess that ages badly.
>
> That isn't a stylistic preference. Two earlier revisions of this file asserted
> live state that had drifted, and the second nearly took monitoring down during
> a release: it said the monitors polled `/up` when they were all still on
> `/health`, and a change that 404s `/health` was about to ship on the strength
> of that sentence. A blind monitor looks exactly like a healthy one.
>
> **Four things must be true, and only your dashboards can confirm them:**
>
> | Thing | Target |
> |---|---|
> | Every Mailvec monitor's URL | `https://mailvec.<your-domain>/up` — never `/health` |
> | Accepted Status Codes on every monitor | `200-299, 503` (see [the section below](#the-setting-that-will-bite-you-accepted-status-codes--200-299-503)) |
> | Access application fronting `/up` | a **path-scoped** app for the exact path `up` |
> | Root Access app service-auth policy | names a **specific** token — never `Any Access Service Token` |
>
> Read the live config before relying on any of it: Kuma's monitor list (its
> API, UI, or `monitorList` in its DB) for the first two, the Zero Trust
> dashboard for the last two, and [Verify](#verify) for the end-to-end check
> that catches a wrong answer on any of them.
>
> **If you edit this file, write *how to check* rather than *what the answer
> was.*** A verification command ages correctly; a table of last quarter's
> config does not. Track your own deployment's state wherever you keep
> operational notes — not here.

## What this monitors, and why external

Mailvec runs as a Docker compose stack (indexer, embedder, mcp, mbsync) behind
a Cloudflare Tunnel + Access. The `mcp` container serves an `/up` endpoint
that reports the whole pipeline's state as JSON, including per-service
**liveness** for the three background workers (there is no `launchctl` in a
container, so liveness travels through this endpoint rather than process
inspection) and whether IMAP sync is still **succeeding**, which liveness
alone can't tell you.

**Point Kuma at the public Cloudflare hostname, not an internal address.** An
internal probe can't see the failures that actually make Mailvec unreachable to
its clients: cloudflared crashed, the tunnel dropped, Access misconfigured,
DNS/cert problems. Polling `https://mailvec.<domain>/up` end-to-end
exercises the exact path a real client takes.

`/up` is intentionally forwarded through the tunnel for this purpose. The
only other mail-bearing surface is the MCP root itself, which is gated by
Access — do not point a monitor at it.

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

> **Monitor `/up`, not `/health`** (`/up` exists from 0.1.37). `/health`
> returns the archive's filesystem path, corpus and chunk counts, the embedding
> model and its dimensions, embedder failure detail, and the internal Ollama LAN
> URL. A credential that only needs to prove liveness should receive none of
> that — and this one sits in Kuma's config store, which makes it the likeliest
> of the deployment's credentials to leak. `/up` carries the same signals with
> the values stripped: booleans yes, values no.
>
> The JSONata paths are identical on both endpoints by design, so a monitor
> written against one works against the other — moving an existing setup is a
> URL edit and nothing else.
>
> **Since 0.2.0 this is enforced, not just advised**: the origin serves
> `/health` to loopback callers only and 404s everything else
> (`Mcp:RestrictHealthToLoopback`). A monitor still pointed at `/health` through
> the tunnel will go permanently down — which is the migration signal, but check
> here first if that's what you're seeing.

A healthy response:

```json
{
  "status": "ok",
  "version": "0.1.37",
  "embeddings": { "modelMismatch": false },
  "ollama": { "reachable": true },
  "embedder": { "stuck": false },
  "mail": { "known": true, "syncStale": false },
  "services": [
    { "service": "indexer",  "known": true, "stale": false },
    { "service": "embedder", "known": true, "stale": false },
    { "service": "mbsync",   "known": true, "stale": false }
  ]
}
```

Note what is deliberately **not** here versus `/health`: `ollama.baseUrl`,
`embeddings.coveragePct` / model name / dimensions, `embedder.consecutiveFailures`
and failure timestamps, `services[].expectedIntervalSeconds` and beat times,
`mail.lastSyncAt`, and the whole `database` block. If a monitor ever needs one
of those, that's a conversation about the trust boundary, not a field to add —
see `docs/security.md`.

`mail.lastSyncAt` is worth calling out because it is the one omission that's
about the **user** rather than the deployment: a monitor polling every 60s and
retaining history builds a log of when this person's mail arrives, i.e. when
they're awake and active. The boolean answers the monitoring question
completely, so there's no trade being made.

Fields the monitors use:

| Field | Meaning | Healthy value |
|---|---|---|
| `status` | overall — `"degraded"` (HTTP 503) if Ollama down / model mismatch / embedder stuck | `"ok"` |
| `services[].stale` | a worker **was** beating and stopped (dead/wedged for ≥3 missed beats) | `false` |
| `services[].known` | whether the worker has ever beaten (`false` = never started, or still starting) | `true` in steady state |
| `embedder.stuck` | embedder can't drain its embedding backlog | `false` |
| `embeddings.modelMismatch` | DB's embedding model disagrees with config (vector-space corruption) | `false` |
| `ollama.reachable` | the embedding model server answers | `true` |
| `mail.syncStale` | mbsync is alive but its syncs keep failing — no successful pull in 4x the sync interval (min 30 min) | `false` |
| `mail.known` | whether any successful sync is on record (`false` = fresh deploy, or a local install with no sidecar) | `true` in steady state |

> **Pair `mail.known` with `mail.syncStale`, don't check `syncStale` alone.**
> A deployment that has NEVER synced successfully reports
> `{ "known": false, "syncStale": false }` — because staleness is measured from
> the last success, and with no success there is nothing to measure. So a fresh
> container with an expired app password, a `Patterns` typo or broken DNS
> passes a `syncStale = false` check indefinitely, which is exactly the
> deployment most likely to be misconfigured. The `known=false` reading is
> deliberate (it keeps fresh installs and sidecar-less local dev out of the
> red), which is precisely why production monitoring has to require
> `known = true` as well. Give it enough retries or startup grace to let the
> first sync land.

**`mail.syncStale` vs `services[service='mbsync'].stale` — these answer
different questions and you want both.** The mbsync beat is written on its own
timer whether or not `mbsync -a` succeeded, deliberately: a loop retrying
against a dead IMAP server *is* alive, and calling it dead sends you hunting a
stopped container that's running fine. The cost is a blind spot that
`mail.syncStale` exists to close — a sidecar whose every sync fails (expired
Fastmail app password, a `Patterns` typo, DNS gone) beats happily forever while
no mail arrives. Nothing else in the pipeline can tell: the indexer's own
timestamps only advance when new mail is actually ingested, so "quiet mailbox"
and "sync broken" look identical there. `stale` means the sidecar is gone;
`syncStale` means it's there and failing.

The same verdict is available without Kuma: `mailvec doctor` inside the stack
emits an `mbsync sync` row under **services**, reading the marker directly off
the Maildir mount (no network, so it works under `--skip-net` too).

```bash
docker compose exec mcp mailvec doctor
```

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
| `mailvec-mail-sync` | `mail.syncStale = false and mail.known = true` | `true` |

If you prefer a single monitor over granularity, use this instead (returns a
boolean; covers degraded status, any stale worker, **and** a sidecar whose
syncs keep failing):

```
status = 'ok' and $count(services[stale = true]) = 0 and mail.syncStale = false and mail.known = true
```
Expected value `true`. Downside: the alert just says "Mailvec unhealthy" — you
curl to find out why.

**Consolidating alerts without going blind.** These two shapes aren't
exclusive, and the combination is usually what you want: create all the
granular monitors with **notifications off**, so the dashboard still names
which thing broke, plus one monitor on the compound expression above with
notifications **on**. That's one notification per incident instead of eight,
and you keep the diagnosis. Uptime Kuma has no parent/child alert suppression —
its Monitor Group type is organizational only, and children alert
independently — so per-monitor notification checkboxes are the lever.

Worth knowing before you rely on it: when `/up` is unreachable at all (tunnel
down, DNS, cloudflared crashed), Kuma marks a monitor down *before* evaluating
any JSONata, so every monitor pointed at this endpoint fires regardless of what
it asks. Consolidating fixes the notification count for that case; it isn't a
query-logic problem.

## Migrating existing monitors from `/health` to `/up`

Only relevant if your monitors predate `/up` (added in 0.1.37) or were built
from an older template — check their URLs rather than assuming either way.

Do this **before** deploying a build with `Mcp:RestrictHealthToLoopback=true`
(the default from 0.2.0), which makes `/health` return 404 to anything off-box.
Sequenced so that a mistake is a one-monitor rollback rather than an outage.

**Do it in two passes, not one.** The URL change and the Accepted-Status-Codes
change are independent, and bundling them means three suspects when something
goes red.

**Pass 1 — repoint the URL, while `/health` still answers.**

1. Pick one monitor as a canary (`mailvec-ollama` is a good choice — a single
   boolean, easy to reason about). Change only its URL to
   `https://mailvec.<domain>/up`.
2. Confirm it stays green through at least two poll intervals. The JSONata does
   not need editing: **the paths are identical on both endpoints by design**, so
   a monitor written against `/health` works unchanged against `/up`.
3. Repoint the remaining five.

**Pass 2 — fix Accepted Status Codes** (see the section below for why). Add
`503` to every Mailvec monitor: `200-299, 503`.

> ⚠️ This has always been wrong, on `/health` too — it is not a regression
> introduced by the migration, and fixing it is not optional cleanup. With
> `200-299` only, an Ollama outage turns **every** Mailvec monitor red regardless of
> what each one's JSONata asks, because Kuma marks any non-2xx down before
> evaluating the query. The migration is simply the natural moment to fix it.

**Only then** deploy the build that restricts `/health`, and add the tunnel's
`^/health$` → 404 rule in the same window.

## The setting that will bite you: Accepted Status Codes → `200-299, 503`

**Set this on every monitor, and check it on any you inherited** — Kuma's
default is `200-299`, so this is wrong until someone makes it right, and the
symptom looks like something else entirely.

`/up` returns **503** whenever degraded. Kuma marks any non-2xx **down before
evaluating the JSONata**, so on the default every monitor pointed at this
endpoint trips together — `mailvec-indexer` goes red during an Ollama reboot,
when the indexer is perfectly fine. The tell is *all* the Mailvec monitors
alerting simultaneously for something only one of them describes; Kuma's
incident history will show them going down in lockstep.

Accepting `503` makes each monitor's JSONata the sole decider, so they stay
independent and each reports only its own failure.

**Add an eighth monitor at the same time**, on overall status:

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
**invisible** — 503 accepted, no query covering it, seven green monitors and a
degraded server. `mailvec-status` closes that permanently: it goes red on any
degraded status, covered or not.

Note `services[].stale` and `mail.syncStale` are deliberately *not* part of
`status`, which is why those monitors still earn their place. That exclusion is
load-bearing and documented at the `Status` switch in `HealthService`: `/health`
is the **mcp** container's own compose healthcheck, so folding a sibling
container's failure into `Status` would mark MCP unhealthy — and restart-loop
it — because the *indexer* or *mbsync* died. Wrong, and actively misleading
when triaging.

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
  `mailvec-embedder-stuck`, `mailvec-model-mismatch`, `mailvec-mail-sync` —
  these are real pipeline failures. `mailvec-mail-sync` in particular is the
  only one that catches a credential problem (an expired Fastmail app
  password), which fails closed and silently and never self-heals.
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

`mail.known` has the identical shape and the same accepted gap: a deployment
whose sync has **never** succeeded (wrong app password from day one, a
`Patterns` typo that matches nothing) reads `known:false, syncStale:false`
rather than broken. `syncStale` only ever means "it worked before and has
stopped." Same remedy if you want it covered — monitor `mail.known` expecting
`true`, with retries generous enough to ride out a first deploy. Both gaps
exist because reporting absence-of-signal as failure puts every fresh install
and every local dev machine permanently red, which is what teaches an operator
to ignore the indicator.

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

# Should NOT return the detailed body. Since 0.2.0 the origin serves /health to
# loopback callers only (Mcp:RestrictHealthToLoopback), so this is a 404 from
# Mailvec itself whether or not Access denies it first — which means it no
# longer distinguishes "Access is scoped correctly" from "Access is wide open
# but the origin saved us". Keep it as a disclosure check; use the next one to
# test the Access split.
curl -i -s \
  -H "CF-Access-Client-Id: $CF_ID" -H "CF-Access-Client-Secret: $CF_SECRET" \
  "https://mailvec.<domain>/health"

# THE check that proves the Access split: the MCP root is the mail surface and
# is gated by Access alone. A monitoring token must be denied here. If this
# returns a tool list, the token is admitted by the root application and can
# read the whole mailbox.
curl -i -s -X POST \
  -H "CF-Access-Client-Id: $CF_ID" -H "CF-Access-Client-Secret: $CF_SECRET" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' \
  "https://mailvec.<domain>/"
```

If `/up` returns a `302`/login page instead of JSON, the service token
isn't authorized on the path-scoped Access app yet.

**If the MCP root returns a tool list, stop.** The token is still admitted by
the root application — most likely its Service Auth policy still reads *Any
Access Service Token* — and the monitoring credential can currently reach the
whole MCP surface, i.e. the mailbox. That's a scoping bug, not a monitoring one.
(Since 0.2.0 `Mcp:Access` can enforce this at the origin too, by audience: see
[security.md → Origin authentication](security.md#origin-authentication-mcpaccess).
It's off by default, so until you enable it the Access policy is the only thing
standing here.)

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

- `docs/security.md` → "`/up` and `/health`" — why `/up` is forwarded
  single-layer and `/health` is loopback-only.
- `docs/remote-access-cloudflare.md` — the tunnel + Access setup, and the
  service-token path-scoping note.
- `src/Mailvec.Core/Health/ServiceHeartbeat.cs` — the liveness contract
  (beat cadence, staleness threshold, `known` vs `stale`).
- `src/Mailvec.Core/Health/HealthService.cs` — `UpReport` is the `/up` body;
  `HealthReport` is `/health`'s.
