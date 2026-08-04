# Remote MCP access via Cloudflare (as-built)

**Status: live.** The MCP server is exposed at a public hostname through a
Cloudflare Tunnel, gated by a Cloudflare Access self-hosted application using
Access **Managed OAuth**. Every Claude surface — iOS, Desktop, Claude Code,
claude.ai — now reaches Mailvec through this one remote connector. The Mac's
local MCPB/stdio path is **retired** as the Desktop transport.

This doc was originally a plan (portal-first, Worker-fallback). It now records
what was actually built, which differs from that plan in one structural way:
**there is no MCP Server Portal.** Managed OAuth sits directly on a self-hosted
Access app in front of the tunnel hostname. See
[What changed from the plan](#what-changed-from-the-plan).

---

## Why this shape (the hard constraint)

Claude **custom connectors are called from Anthropic's cloud, not from the phone**
— across every client: claude.ai, Desktop, Cowork, mobile. Anthropic's outbound
traffic originates from **`160.79.104.0/21`** (verified 2026-07-10 against
[platform.claude.com/docs/en/api/ip-addresses](https://platform.claude.com/docs/en/api/ip-addresses);
don't confuse with the *inbound* `/23`), and both the MCP server *and* its
authorization server must be reachable from that range — discovery requests to
the auth server come from the same IPs.

Additional hard constraint (documented in Anthropic's
[troubleshooting page](https://claude.com/docs/connectors/building/troubleshooting)):
**connectors are IPv4-only.** Every resolved A record must be globally routable;
an AAAA-only hostname fails before any HTTP request leaves Anthropic. A
Cloudflare-proxied hostname publishes A records, so this is satisfied here —
just never switch the hostname to AAAA-only.

Consequences that drove the design:
- **Tailscale / localhost / LAN are non-starters** for the iOS path — they're invisible
  to Anthropic's backend even when the phone is on the home network.
- iOS can only use a **remote connector**; it cannot run a local server.
- The endpoint must therefore be **publicly reachable + OAuth-gated**. Cloudflare Tunnel
  gives reachability without opening a port; Cloudflare Access supplies the OAuth gate.

---

## Architecture (as-built)

```
Every Claude client (iOS, Desktop, Claude Code, claude.ai)
        │
        ▼
  Anthropic cloud (160.79.104.0/21, IPv4-only)
        │ OAuth 2.1 (PKCE/S256) via Access Managed OAuth
        ▼
  Cloudflare edge ──► Access self-hosted app (identity policy)
        │ Cloudflare Tunnel (no open ports)
        ▼
  cloudflared sidecar (compose `tunnel` profile) ──► http://mcp:3333
        ▼
  ./data/archive.sqlite  (Docker host — see deploy-docker.md)
```

The Mac keeps a frozen copy of the archive as the local dev corpus
([local-dev-dataset.md](contributing/local-dev-dataset.md)); it is no longer a
serving path.

---

## Cloudflare products in use

| Product | Role |
|---|---|
| **Cloudflare account + DNS zone** | Public hostname (`mailvec.<domain>`), proxied (A records — IPv4 requirement). |
| **Cloudflare One / Zero Trust** | Umbrella. Free tier covers up to 50 users. |
| **Cloudflare Tunnel** (`cloudflared`) | Exposes `mcp:3333` with no inbound ports. Compose sidecar, token-based, **remotely-managed** (ingress lives in the dashboard/API, no config.yml). |
| **Cloudflare Access** (self-hosted app) | Identity gate + **Managed OAuth**, presenting the OAuth 2.1 flow to Claude and issuing tokens. This is the auth front. |
| **An IdP** | One-time-PIN email / Google / GitHub, configured in Zero Trust. |

Not used: the **MCP Server Portal** (see below), Cloudflare Workers +
`workers-oauth-provider` (the fallback that never became necessary), and Argo /
Smart Shield (pricing undocumented, no confirmation it applies to Tunnel
traffic at all).

---

## What changed from the plan

Three deviations worth remembering, because they each closed a question the
plan had left open:

1. **No MCP Server Portal.** The plan routed through a portal because that was
   Cloudflare's MCP-aware front. In practice Managed OAuth on a plain
   self-hosted Access app completes Claude's handshake directly, so the portal
   added a beta dependency for nothing.
2. **The Worker contingency never arose.** It existed solely as a fallback for
   "portal OAuth can't complete claude.ai's flow." No portal, no failure mode.
3. **Code Mode is moot.** Collapsing upstream tools into a single
   code-execution tool is a *portal* behaviour. Without a portal, nothing
   rewrites the tool surface: all seven tools present individually, and the
   locked tool-name contract (CLAUDE.md "MCP API stability") — `search_emails`,
   `partIndex` round-trips, the server-built `webmailLink` — reaches clients as
   written. **If a portal is ever reintroduced, re-open this**: Code Mode
   defaults ON and would clobber that contract.

**The unblocker was the redirect-URI allowlist** — the same
[#478](https://github.com/anthropics/claude-ai-mcp/issues/478) fix the plan had
identified. In Zero Trust's MCP OAuth / dynamic client registration settings,
`https://claude.ai/api/mcp/auth_callback` must be on the **allowed redirect
URIs**; without it DCR rejects claude.ai with
`400 invalid_client_metadata: redirect_uri is not allowed by the account
configuration`. This was the entire cause of the June failure
([#410](https://github.com/anthropics/claude-ai-mcp/issues/410), closed "not
planned", diagnosed properly in #478). **Don't remove that allowlist entry** —
the connector breaks at registration, not at request time, so the failure
surfaces only when the connector is re-added.

---

## What Claude requires of the endpoint

Kept as a checklist because these are the things that break silently if the
Cloudflare side is reconfigured. All of them are satisfied by the Access layer,
**not** by `Mailvec.Mcp` — the origin has no auth of its own.

- Reachable from `160.79.104.0/21`, IPv4 A records only.
- OAuth 2.1 with **PKCE / S256** (`code_challenge_methods_supported` must
  advertise S256); exact redirect-URI matching against
  `https://claude.ai/api/mcp/auth_callback` — the single callback for **all**
  hosted surfaces including iOS.
- Authorization-server discovery: RFC 8414
  `/.well-known/oauth-authorization-server`, or `/.well-known/openid-configuration`
  as fallback — one must answer.
- Protected-resource metadata: `401` + `WWW-Authenticate: ...resource_metadata`.
  Cloudflare points this at a **nonstandard PRM path**
  (`/.well-known/cloudflare-access-protected-resource/`), which works only
  because clients follow the header pointer rather than guessing the path.
  (Claude also probes `/.well-known/oauth-protected-resource[/<path>]` when the
  header is absent.)
- Client registration via **DCR** (what Managed OAuth provides — public clients
  only; there is no way to pre-register a static client id against Managed
  OAuth, and Access **service tokens** are for headless clients, not the
  connector flow).
- Latency budget: **10 s** on discovery/registration/token, 30 s on refresh;
  token endpoint accepts `application/x-www-form-urlencoded`; no cross-host 3xx
  on authenticated requests (the Authorization header gets dropped).

---

## Origin-side wiring (this repo)

Two things in the compose stack are load-bearing for the tunnel (a third,
[origin validation of the Access assertion](#origin-validation-of-the-access-assertion-mcpaccess),
is optional and off by default):

- **`MCP_PUBLIC_HOSTNAME` must be set in `.env`.** cloudflared forwards the
  original public `Host` header, and [`HostGuard`](../src/Mailvec.Mcp/HostGuard.cs)
  returns 403 for any hostname that isn't loopback or allowlisted. Compose wires
  this into `Mcp:AllowedHosts`; without it **every request through the tunnel
  fails**. See [security.md](security.md#host--origin-validation-dns-rebinding-guard).
- **`TUNNEL_TOKEN` in `.env`**, with the sidecar started via
  `docker compose --profile tunnel up -d`. The tunnel is remotely-managed:
  `tunnel --no-autoupdate run`, no `cloudflared tunnel login`, no config.yml.

**Ingress: forward MCP + `/health`, 404 the mail-bearing `/tray/`.** MCP is
mounted at the root `/` (there is no dedicated "MCP path" to allow-list), so the
shape is path-differentiated on the same hostname, in this order:

| # | Hostname | Path | Service |
|---|---|---|---|
| 1 | `mailvec.<domain>` | `tray/*` | `http_status:404` |
| 2 | `mailvec.<domain>` | `^/health$` | `http_status:404` |
| 3 | `mailvec.<domain>` | *(empty)* | `http://mcp:3333` |
| 4 | *(catch-all)* | | `http_status:404` |

> ### `path` is an unanchored regular expression, not a prefix
>
> Cloudflare's docs are explicit: "Rules can match the request's path to a
> regular expression", parsed with Go's `regexp` syntax — which anchors nothing
> by default. Their own example, `\.(jpg|png|css|js)$`, matches anywhere in the
> path. So a bare `health` rule matches **any path containing "health"**, and
> the existing `tray/*` rule is a regex whose `/*` means "zero or more slashes"
> — it works, but by accident rather than by prefix semantics.
>
> **Anchor deliberately.** `^/health$` matches that path and nothing else. Same
> reasoning that made the minimal endpoint `/up` rather than `/healthz`: a loose
> pattern over "health" silently widens what it covers, and the failure is
> invisible until someone probes for it. (Rule 1 predates this and is left
> alone here — retightening a working rule is its own change, but `^/tray/`
> would be the correct form.)

> **Rule 2 lives in the Cloudflare dashboard, not in this repo**, so nothing in
> a commit can apply or confirm it — check the dashboard, not this file. The
> origin already refuses off-box `/health` on its own
> (`Mcp:RestrictHealthToLoopback`, default true, which is the load-bearing
> barrier); this rule is the outer of the two, exactly like `/tray/`.
>
> Its prerequisite: **migrate any external monitor to `/up` first**, since the
> rule blinds anything still polling `/health` — and a blind monitor looks
> exactly like a healthy one. Check where yours point before shipping either. See
> [monitoring-uptime-kuma.md](monitoring-uptime-kuma.md#migrating-existing-monitors-from-health-to-up).

**`/up` is the forwarded monitoring endpoint; `/health` is not.** Uptime Kuma
polls `/up` end-to-end through the tunnel, which detects tunnel / Access / edge
failures an in-network probe can't. `/health` is its detailed sibling and
discloses the archive path, corpus counts, embedding model identity and the
internal Ollama LAN address — `/up` exists precisely so nothing external needs
any of that. Its real consumers are all loopback (the compose healthcheck,
`mailvec doctor` under `docker compose exec`, the tray on local installs), so
keeping it off-box costs nothing. See
[security.md → `/up`, `/health` and `/tray/*`](security.md#up-health-and-tray).

**Verify** rule 2 after adding it (as the owner, from outside):

```bash
curl -i https://mailvec.<domain>/health   # 404
curl -i https://mailvec.<domain>/up       # 200 or 503, with the boolean body
```

```bash
# And that the loopback consumers are unaffected:
docker compose exec mcp curl -fsS http://127.0.0.1:3333/health   # full report
```

**`/tray/*` has two independent barriers**, and the origin one is load-bearing —
do not rely on this ingress rule alone:

1. **Origin:** `Mcp:EnableTrayEndpoints=false` (container image) — `mcp` never
   maps `/tray/*`; a request 404s from Kestrel with no handler. Holds regardless
   of tunnel config.
2. **Tunnel:** rule 1 above 404s `/tray/` before the catch-all.

**Verify after any ingress or image change**: `curl -i .../tray/folders` →
**404**, `curl -i .../up` → the boolean status body. From 0.2.0 `curl -i
.../health` → **404** as well (the origin serves it to loopback only); the
compose healthcheck curls it from inside the container and is unaffected.
Belt-and-braces third option if the rules get fragile: a zone-level WAF rule
blocking URI path `/tray/*`.

**Scope the monitoring service token to `/up`.** The Uptime Kuma service token
passes Access; if it's authorized on the whole-subdomain app it can reach MCP
(i.e. read mail) should it leak from Kuma's store. Put it on a **path-scoped
Access app for `/up`** (a more-specific path app takes precedence over the root
identity app, and does not inherit the parent's policies), so the monitoring
credential can only ever hit the minimal endpoint.

> ⚠️ **This is a target. Nothing in this repo can tell you whether your
> deployment has hit it** — Access applications live in Cloudflare's control
> plane. An earlier revision of this document asserted the path-scoped app
> existed when it did not, which is exactly why no revision should make that
> claim again. Verify in the dashboard, not here.
>
> **Verify** (with the monitoring token, from outside the network): `/up`
> returns the status JSON, and the MCP root returns **404 or a login page, never
> a tool list**. A tool list means the token is admitted by the root application
> and can read mail — [monitoring-uptime-kuma.md](monitoring-uptime-kuma.md)
> carries the full check.
>
> **Sequencing, when building or rebuilding these credentials: narrow the root
> policy last.** An any-token rule is what makes a zero-downtime migration
> possible — mint a scoped token → repoint the monitors → create the path-scoped
> app → *then* narrow the root. Tightening first locks out the monitors and your
> agent clients at once.

Two things that are easy to get wrong here:

- **Scope the app to the exact path `up` — never a wildcard.** Access path
  wildcards partial-match inside a segment (`example.com/foo*/bar` covers
  `/food/bar`), so a wildcard is how a monitoring app accidentally grows to
  cover paths you didn't intend. This is also why the minimal endpoint is `/up`
  rather than `/healthz`: no wildcard over "health" can reach it.
- **`/health` (the detailed body) must NOT be reachable by the monitoring
  token.** It carries the archive path, corpus counts, model config and the
  internal Ollama LAN URL. Its real consumers are loopback-only, so it needs no
  service-token access at all.

**Verify the scoping rather than assuming it** — it lives in Cloudflare's
dashboard, not in this repo, so nothing here can enforce it. With the monitoring
token, from outside the network: `/up` returns the status JSON, and **`/health`
and `/` must both be denied**. If either returns content, the token is being
admitted by the **root** application and the monitor can read mail.

The usual cause is the root app's Service Auth policy set to **`Any Access
Service Token`** rather than a named one. That rule admits every service token
in the account — including any created later for something unrelated — so the
monitoring credential, and anything else headless you ever add, silently gets
the whole mailbox. Before changing it, list Zero Trust → **Access → Service
Auth**: that inventory is the de facto access list to the archive, and it's the
only place the widening is visible.

The mcp container publishes **no host port** — the tunnel is the only ingress.
Keep it that way: a published `ports:` mapping is reachable from the LAN
without any OAuth, bypassing the Access front entirely, and the
[accepted-risk rationale in security.md](security.md#whats-accepted) depends on
that not being true.

---

## Origin validation of the Access assertion (`Mcp:Access`)

Optional second layer, **off by default**. With it configured, the MCP server
validates the `Cf-Access-Jwt-Assertion` header itself instead of trusting
anything that can reach `mcp:3333`. Rationale and threat model:
[security.md → Origin authentication](security.md#origin-authentication-mcpaccess).
The short version: the Access policy lives in Cloudflare's dashboard, unversioned
and untested, and this makes the origin's half of the gate checkable in CI — plus
it enforces the `/up` monitoring split at the origin rather than trusting the
edge path scoping.

**Values you need**, both from the Zero Trust dashboard:

| `.env` key | Where |
|---|---|
| `MCP_ACCESS_TEAM_DOMAIN` | Settings → your team domain, as a full `https://` URL |
| `MCP_ACCESS_AUDIENCE` | Access → Applications → *the Mailvec app* → **Additional settings → AUD tag** |
| `MCP_ACCESS_MONITORING_AUDIENCE` | the AUD tag of the separate path-scoped `up` application. Leave empty if you have no such app — but note origin validation then can't distinguish the monitor from the mailbox, which is the whole point of the split |

> **The AUD tag is not on the application's Overview/Details tab.** In the
> current Cloudflare One dashboard it lives under **Additional settings → AUD
> tag** — noted because looking for it in the obvious place costs a round of
> searching.
>
> ⚠️ **The same panel has a "Revoke existing tokens" button, which rotates the
> AUD.** Harmless today. Once `Mcp:Access` is live it is a foot-gun: rotating
> invalidates every issued JWT *and* changes the value the origin is configured
> to expect, so it 401s every Claude surface at once and stays broken until
> `MCP_ACCESS_AUDIENCE` is updated and the container restarted.

Then set `MCP_ACCESS_ENABLED=true` (literal `true`, not `1` — .NET's binder only
understands `true`/`false`, and an unbindable value reads as false) and
`docker compose up -d mcp`.

**A half configuration refuses to start**, naming the missing knob — deliberately,
since `Enabled` without an audience would validate signature and issuer while
admitting every application in the account. So would a `MonitoringAudience` equal
to `Audience`, which reads like a restriction and grants the whole mailbox; that
also refuses to start.

**Verify after enabling** (from outside the network — a browser session that has
cleared Access, plus the monitoring token):

```bash
# Owner, through the tunnel: normal MCP, and /up as the health signal.
curl -i https://mailvec.<domain>/up              # 200 or 503, boolean body

# /health is NOT the owner's check — the origin serves it to loopback callers
# only (Mcp:RestrictHealthToLoopback, default true from 0.2.0), so clearing
# Access as the owner still gets a 404. That's the endpoint filter, not a
# misconfigured assertion; don't go hunting for one.
curl -i https://mailvec.<domain>/health          # 404

# The detailed body, from where it's actually served:
docker compose exec mcp curl -fsS http://127.0.0.1:3333/health   # full report

# Monitoring token: /up yes, mailbox no. THIS is the check worth having —
# it now fails at the origin even if the Access policy is wrong.
curl -i -H "CF-Access-Client-Id: <id>" -H "CF-Access-Client-Secret: <secret>" \
  https://mailvec.<domain>/up                    # 200 or 503
curl -i -H "CF-Access-Client-Id: <id>" -H "CF-Access-Client-Secret: <secret>" \
  https://mailvec.<domain>/health                # 403 — audience not permitted here
```

`/health` has two independent barriers for the monitoring token, and the 403
above is the *outer* one: authorization middleware runs before the endpoint
filter, so origin auth refuses the assertion before the loopback check is
reached. Were `Mcp:Access` off, the same request would 404 from the filter
instead. Either way the body never leaves the box — but a 403 here is what
tells you origin validation is actually live.

```bash
# From the VM, bypassing the tunnel: no assertion, no access.
docker compose exec cloudflared wget -qS -O- http://mcp:3333/health   # 401
```

The last one is the acceptance that matters most — it's the shape a published
host port or a compromised sibling container would take, and before this it
returned the mailbox.

**Don't remove the loopback exemption** (`Mcp__Access__AllowLoopback`). The
compose healthcheck curls `127.0.0.1:3333/health` from inside the mcp container
and has no assertion; turning the exemption off marks the container permanently
unhealthy. Loopback is not reachable from off-box — cloudflared and every
sibling arrive over the compose network with a real address and are never
exempt, which is exactly what the `docker compose exec` check above proves.

## Gotchas

- **The full tool surface is exposed, deliberately.** `Mcp:DisabledTools` is
  staged-but-commented in compose.yml; `view_attachment` and
  `get_attachment_page_image` remain reachable over the tunnel. This is an
  **accepted risk with a specific rationale and specific conditions** — read
  [security.md → What's accepted](security.md#whats-accepted) before adding an
  identity to the Access policy or publishing the LAN port, either of which
  invalidates it.

- **`serverInfo.name = "mailvec"` is shared** with the (now dormant) local
  stdio path. Only matters if a second Mailvec endpoint is ever registered
  alongside this one — Claude's connector dedup fires only on an **exact
  endpoint match**, and a stdio command ≠ an `https://` URL, so two Mailvecs
  would *not* dedupe. Disambiguate by display name if that ever happens.

- **Latency is fine and not worth optimising.** Cloudflare overhead is ~30–70 ms
  on top of client RTT, and `search_emails` is Ollama-bound anyway. Now that
  Desktop routes through the tunnel too, this is the only path — the old
  "don't route desktop through it" advice no longer applies.

- **Anthropic's "MCP tunnels" research preview is not a substitute.** Outbound-only
  cloudflared-based tunnels for private MCP servers, but explicitly **not
  available as claude.ai connectors** (Managed Agents + Messages API only).

---

## Still open

1. **Is the connector enable/disable toggle per-device or account-synced?**
   Undocumented as of 2026-07; the only documented granularity is
   **per-conversation** (`+` → Connectors). Now largely academic — every
   surface intentionally uses the same remote connector — but it would matter
   again if a second, differently-scoped Mailvec endpoint were ever added.
2. **Do the dashboard path rules match exactly or as regex?** Undocumented for
   remotely-managed tunnels. The external `curl -i` checks confirm the current
   rules behave; the semantics are still unpinned, so prefer the
   tunnel-configurations API over the dashboard field when editing them, and
   re-run the checks. The stakes are bounded for the sensitive surface: even if
   the `/tray/` 404 rule silently stopped matching, `/tray/*` is *also* disabled
   at the origin (`Mcp:EnableTrayEndpoints=false`), so no mail data is served.
   The ingress rule is the outer of two barriers, not the only one.
