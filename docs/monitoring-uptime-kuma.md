# Monitor Mailvec with Uptime Kuma

Use `/up` for external monitoring. It reports status, version, and liveness flags without archive paths, message counts, or model details. `/health` contains those details and is loopback-only. A check through the tunnel also detects tunnel and Access failures that an in-container healthcheck cannot.

Create a Cloudflare Access service token scoped to the exact `/up` path. It must not be authorized on the root MCP application; see [remote access](remote-access-cloudflare.md#scope-a-monitoring-credential).

## Configure a monitor

Use Kuma's **HTTP(s) – JSON Query** monitor type with:

- URL: `https://mailvec.example.com/up` (replace with your hostname).
- Headers: `CF-Access-Client-Id` and `CF-Access-Client-Secret` for the monitoring token.
- Accepted Status Codes: **`200-299, 503`**. `/up` returns 503 when degraded; accepting it lets the JSON query identify the failing condition. Also monitor `status = 'ok'` so new degraded conditions cannot go unnoticed.
- Interval: around 60 seconds, with retries appropriate to your alert policy.

Suggested queries (Kuma compares each scalar result to the expected value):

| Condition | JSONata query | Expected |
| --- | --- | --- |
| Overall status | `status` | `ok` |
| Indexer stopped | `services[service='indexer'].stale` | `false` |
| Embedder stopped | `services[service='embedder'].stale` | `false` |
| mbsync stopped | `services[service='mbsync'].stale` | `false` |
| Embedder stalled | `embedder.stuck` | `false` |
| Embedding identity mismatch | `embeddings.modelMismatch` | `false` |
| Embedding provider unavailable | `embeddingProvider.ready` | `true` |
| Mail sync failing | `mail.syncStale = false and mail.known = true` | `true` |
| OCR stalled | `ocr.stalled` | `false` |

Check the actual `/up` response before using a field: older deployed versions may differ. `ollama.reachable` is a compatibility alias for embedding-provider readiness. `ocr.stalled` is absent until OCR has run; allow startup time rather than treating an absent field as healthy.

A service that has **never** emitted a heartbeat can report `known:false, stale:false`, so `stale=false` alone does not prove it started. Add `services[service='indexer'].known` (and similar checks) expecting `true` if you need to detect failure on first boot. Likewise, `mail.syncStale=false` means no *previously successful* sync has gone stale; pair it with `mail.known=true` to catch a setup that has never synced.

For one notification per incident, leave notifications off on individual condition monitors and enable them on a combined monitor:

```jsonata
status = 'ok' and $count(services[stale = true]) = 0 and mail.syncStale = false and mail.known = true
```

An unreachable endpoint makes all monitors on that URL fail before their queries run. Alert routing is a Kuma configuration choice.

## Monitor the parser separately

The `parse` service is intentionally absent from `/up`: parser downtime pauses ingest and OCR, while archive search still works. The service has its own container healthcheck at `http://127.0.0.1:3400/up`. Use a Docker Container monitor if parser availability needs an alert, with enough retries to tolerate its routine restart after a request timeout or request limit. Check it directly with `docker compose ps parse` and `docker compose exec mcp curl -fsS http://parse:3400/up`.

## Verify the credential and endpoint

```sh
curl -i -H "CF-Access-Client-Id: $CF_ID" \
  -H "CF-Access-Client-Secret: $CF_SECRET" \
  https://mailvec.example.com/up

curl -i -X POST -H "CF-Access-Client-Id: $CF_ID" \
  -H "CF-Access-Client-Secret: $CF_SECRET" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' \
  https://mailvec.example.com/
```

The first request must return `/up` JSON; the second must be denied. A tool list means the monitoring token can access mail. Recheck Access policy scope and `MCP_ACCESS_ALLOWED_IDENTITIES`. Use `docker compose exec mcp mailvec doctor` for detailed local diagnosis.
