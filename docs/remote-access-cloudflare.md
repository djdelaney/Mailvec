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

Two things in the compose stack are load-bearing for the tunnel:

- **`MCP_PUBLIC_HOSTNAME` must be set in `.env`.** cloudflared forwards the
  original public `Host` header, and [`HostGuard`](../src/Mailvec.Mcp/HostGuard.cs)
  returns 403 for any hostname that isn't loopback or allowlisted. Compose wires
  this into `Mcp:AllowedHosts`; without it **every request through the tunnel
  fails**. See [security.md](security.md#host--origin-validation-dns-rebinding-guard).
- **`TUNNEL_TOKEN` in `.env`**, with the sidecar started via
  `docker compose --profile tunnel up -d`. The tunnel is remotely-managed:
  `tunnel --no-autoupdate run`, no `cloudflared tunnel login`, no config.yml.

**Unauthenticated surfaces are 404'd at the tunnel, before the catch-all.** MCP
is mounted at the root `/` (there is no dedicated "MCP path" to allow-list), so
the shape is deny-then-forward — same hostname, path-differentiated, in this
order:

| # | Hostname | Path | Service |
|---|---|---|---|
| 1 | `mailvec.<domain>` | `health` | `http_status:404` |
| 2 | `mailvec.<domain>` | `tray/` | `http_status:404` |
| 3 | `mailvec.<domain>` | *(empty)* | `http://mcp:3333` |
| 4 | *(catch-all)* | | `http_status:404` |

These 404s were verified from outside at go-live and re-checked 2026-07-16.
**Re-verify with `curl -i https://mailvec.<domain>/health` and `/tray/status`
after any change to the tunnel's public-hostname rules** — `/health` and
`/tray/*` are unauthenticated at the origin (the security model assumed
loopback/compose-network isolation), so a rule reordering exposes mail bodies
via `/tray/email/<id>` and the IMAP username via `/tray/system`. Expect a
**404**, not an Access login redirect: the 404 means the ingress rule fired
before the origin was ever consulted.

**The Access app is scoped to the whole subdomain**, so those endpoints are
OAuth-gated *as well as* 404'd — two independent layers. That redundancy is
load-bearing, because this layer is the weaker one (path-matching semantics are
undocumented, see [Still open](#still-open)). **Don't narrow the Access app to a
path subset** on the theory that MCP is the only thing that needs gating; that
silently makes the ingress rules the sole protection for mail bodies. The
compose healthcheck curls `/health` from inside the network and is unaffected by
any of this. Belt-and-braces third option if the rules ever get fragile: a
zone-level WAF custom rule blocking URI paths `/health` and `/tray/*`.

The mcp container publishes **no host port** — the tunnel is the only ingress.
Keep it that way: a published `ports:` mapping is reachable from the LAN
without any OAuth, bypassing the Access front entirely, and the
[accepted-risk rationale in security.md](security.md#whats-accepted) depends on
that not being true.

---

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
   re-run the checks. Lower stakes than it looks — Access covers the whole
   subdomain, so a path rule that silently stopped matching would expose these
   endpoints to *authenticated* callers (i.e. you), not the internet. That's the
   entire reason not to narrow the Access scope.
