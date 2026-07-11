# Remote MCP access via Cloudflare (desktop local + iOS remote)

Planning note. Goal: keep the **local** MCP path on the desktop (MCPB/stdio)
exactly as-is, while exposing a **public, authenticated** endpoint so the
**Claude iOS app** can reach Mailvec from anywhere.

Status: plan decided 2026-07-10; every claim below re-verified against the live
Cloudflare + Anthropic docs the same day. Not yet built. Two things changed on
verification: (1) the pipeline now runs in the **Docker host** (see
[deploy-docker.md](deploy-docker.md)) — the tunnel is the compose `cloudflared`
sidecar behind the `tunnel` profile, not a Mac daemon; the "Local side" and
"Steps" sections below reflect that. (2) The portal-OAuth question that gated
the plan is **resolved** — the June claude.ai failure had a confirmed root
cause and fix (see the ★ gotcha), so the sequence is now: portal with the
redirect-URI allowlist fix applied (expected to pass), 30-minute test gate to
confirm, Worker fallback retained only as contingency. The server-side
tool-surface trim (`Mcp:DisabledTools`) is implemented and staged in
compose.yml — uncomment it before the tunnel goes live.

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

Consequences:
- **Tailscale / localhost / LAN are non-starters** for the iOS path — they're invisible
  to Anthropic's backend even when the phone is on the home network.
- iOS can only use a **remote connector**; it cannot run a local server.
- The endpoint must therefore be **publicly reachable + OAuth-gated**. Cloudflare Tunnel
  gives reachability without opening a port; Cloudflare Access supplies the OAuth gate.

(Anthropic shipped an "MCP tunnels" research preview — outbound-only
cloudflared-based tunnels for private MCP servers — but it's explicitly **not
available as claude.ai connectors** (Managed Agents + Messages API only), so it
is not a substitute for this plan today.)

---

## Target architecture

```
Desktop  (unchanged until the deliberate client switch-over — deploy-docker.md "What's left" #6)
  Claude Desktop ──stdio──► local MCPB ──► the Mac's local archive copy

iOS (new path)
  Claude iOS ──► Anthropic cloud (160.79.104.0/21, IPv4-only)
                     │ OAuth (PKCE/S256)
                     ▼
            Cloudflare edge ──► MCP portal (Access Managed OAuth + policy)
                     │ Cloudflare Tunnel (no open ports)
                     ▼
            cloudflared sidecar (compose `tunnel` profile) ──► http://mcp:3333
                     ▼
                ./data/archive.sqlite  (Docker host)
```

Once the client switch-over lands, Desktop also uses the remote connector and
the Mac's MCPB copy becomes the frozen local-dev corpus
([local-dev-dataset.md](contributing/local-dev-dataset.md)).

---

## Exact Cloudflare products

| Product | Role | Notes |
|---|---|---|
| **Cloudflare account + a DNS zone** | Public hostname (`mailvec.<domain>`) | Need a domain on Cloudflare DNS. |
| **Cloudflare One / Zero Trust** | Umbrella for the below | Free tier covers up to **50 users**; AI controls/portals included. |
| **Cloudflare Tunnel** (`cloudflared`) | Exposes `mcp:3333` with **no inbound ports** | Already staged: the compose sidecar (token-based, **remotely-managed** — ingress lives in the dashboard/API, no config.yml). Matches Cloudflare's current Docker guidance (`tunnel --no-autoupdate run` + `TUNNEL_TOKEN`). |
| **Cloudflare Access** | Identity gate + browser OAuth flow | Needs an IdP: one-time-PIN email, Google, or GitHub. |
| **MCP Server Portal** (Zero Trust → AI controls) | MCP-aware front that presents OAuth to Claude | Primary auth approach. Still **Open Beta** (launched 2025-08-26; no GA as of 2026-07). See ★ gotcha and the Code Mode gotcha. |
| **Cloudflare Worker + `workers-oauth-provider`** | *Contingency* OAuth front if the portal still fails | v0.8.1 (2026-06-19), actively maintained. |

(An earlier revision listed Argo Smart Routing as an optional latency trim.
Dropped: Argo was folded into **Smart Shield**, pricing is no longer publicly
documented, and no current doc confirms it applies to Tunnel traffic at all.)

---

## Requirements

**Claude side** (canonical docs moved to
[claude.com/docs/connectors/building/](https://claude.com/docs/connectors/building/authentication)
— the old support articles are stubs)
- **Any plan** — custom connectors are now available on Free, Pro, Max, Team,
  and Enterprise (the old "paid plan" requirement is gone).
- Connector endpoint reachable from `160.79.104.0/21`, IPv4 A records only.
- OAuth 2.1 with **PKCE / S256** (`code_challenge_methods_supported` must
  advertise S256), exact redirect-URI matching against
  `https://claude.ai/api/mcp/auth_callback` — the single callback for **all**
  hosted surfaces including iOS. (Claude Code is the odd one out: RFC 8252
  loopback ports + CIMD; irrelevant here since Claude Code can also just take
  the URL directly.)
- Authorization-server discovery metadata: RFC 8414
  `/.well-known/oauth-authorization-server` first, `/.well-known/openid-configuration`
  fallback — one of the two must answer.
- Protected-resource metadata: `401` + `WWW-Authenticate: ...resource_metadata`,
  **or** — now explicitly documented — Claude probes
  `/.well-known/oauth-protected-resource[/<path>]` as a fallback when the
  header is missing. (The 401 status itself is required; the header is ignored
  on a 200.)
- A client-registration approach: **DCR**, **CIMD**, or manually entered client
  credentials in the connector UI (secret optional).
  → All of these are provided by the Cloudflare auth layer, **not** by `Mailvec.Mcp`.
- Latency budget: **10 s** on discovery/registration/token endpoints, 30 s on
  refresh; token endpoint must accept `application/x-www-form-urlencoded`; no
  cross-host 3xx on authenticated requests (the Authorization header is dropped).

**Local side**
- The compose stack live on the Docker host ([deploy-docker.md](deploy-docker.md)),
  `mcp` serving Streamable HTTP at `mcp:3333`. No cloudflared install needed —
  the sidecar is already staged in compose behind the `tunnel` profile.
- Domain added to Cloudflare; an IdP configured in Zero Trust.

---

## Steps

1. **Leave the desktop alone.** Local MCPB stays as configured until the
   deliberate client switch-over (deploy-docker.md "What's left" #6).
2. **Create a remotely-managed tunnel** in the Zero Trust dashboard
   (Networks → Tunnels → Create → cloudflared), and put its token in `.env` as
   `TUNNEL_TOKEN`. The compose sidecar (`docker compose --profile tunnel up -d`)
   runs `tunnel --no-autoupdate run` against it — no `cloudflared tunnel login`,
   no config.yml; ingress is managed in the dashboard/API.
3. **Add public-hostname routes that 404 the unauthenticated surfaces before
   the catch-all.** MCP is mounted at the root `/` (there is no dedicated "MCP
   path" to allow-list), so the shape is deny-then-forward — same hostname,
   path-differentiated, in this order:

   | # | Hostname | Path | Service |
   |---|---|---|---|
   | 1 | `mailvec.<domain>` | `health` | `http_status:404` |
   | 2 | `mailvec.<domain>` | `tray/` | `http_status:404` |
   | 3 | `mailvec.<domain>` | *(empty)* | `http://mcp:3333` |
   | 4 | *(catch-all)* | | `http_status:404` |

   `http_status` services and per-path routes are supported on remotely-managed
   tunnels (dashboard or the tunnel-configurations API). **Caveat:** path-as-regex
   is only documented for locally-managed config files; the dashboard path
   field's matching semantics are undocumented. Prefer setting the rules via the
   configurations API, and either way **verify from outside with
   `curl -i https://mailvec.<domain>/health` and `/tray/status` (expect 404)**
   before go-live. Belt-and-braces alternative: a WAF custom rule blocking URI
   paths `/health` and `/tray/*` at the zone.
4. **Set `MCP_PUBLIC_HOSTNAME=mailvec.<domain>` in `.env`.** cloudflared forwards
   the original public `Host` header, and
   [`HostGuard`](../src/Mailvec.Mcp/HostGuard.cs) returns 403 for any hostname
   that isn't loopback or allowlisted — compose wires this env var into
   `Mcp:AllowedHosts`, and without it **every request through the tunnel fails**.
   See [security.md](security.md#host--origin-validation-dns-rebinding-guard).
5. **Reduce the cloud tool surface — server-side first.** Uncomment the
   `Mcp__DisabledTools__*` lines in compose.yml (drops `view_attachment` and
   `get_attachment_page_image` from tools/list AND tools/call at the server).
   Server-side is the authoritative layer: it holds identically under the
   portal, the Worker, or the direct LAN port — which bypasses the OAuth
   front entirely. Portal per-tool toggles are the optional second layer, not
   the enforcement. Then `docker compose --profile tunnel up -d`.
6. **Configure an IdP** in Zero Trust (one-time PIN email is simplest for a
   single user).
7. **Stand up the portal (★):**
   1. **Apply the #478 fix first**: in Zero Trust's MCP OAuth / dynamic client
      registration settings, add `https://claude.ai/api/mcp/auth_callback` to
      the **allowed redirect URIs**. Without it, DCR rejects claude.ai with
      `400 invalid_client_metadata` — this was the entire June failure.
   2. Create the MCP Server Portal (Zero Trust → AI controls), add the server
      with the **public tunnel hostname** as the upstream (portals only accept
      remote HTTP upstreams — a tunnel-published public hostname qualifies;
      private-network tunnel upstreams do not). Upstream auth: `unauthenticated`
      (the origin has no auth of its own; the tunnel + HostGuard are the
      transport gate).
   3. Attach an Access policy restricting to your identity.
   4. **Check Code Mode** (see gotcha) — disable it or verify the tool surface
      survives.
   5. Client URL is `https://<sub>.<domain>/mcp`.
   *Contingency if the handshake still fails:* deploy a Worker with
   `workers-oauth-provider` that proxies to the tunnel origin (details in the
   ★ gotcha).
8. **Register the connector on claude.ai (web)** with a **distinct name:
   "Mailvec (Cloud)"** and the `https://<sub>.<domain>/mcp` URL. Complete the
   OAuth flow once.
9. **Verify on iOS** — the connector syncs down; confirm tools load and a search
   works.
10. **Disable "Mailvec (Cloud)" on Desktop** so the desktop keeps using the fast
    local path (see gotcha on duplicates).

---

## Gotchas

- **★ Portal OAuth vs claude.ai — RESOLVED (2026-07-10 research).** The June
  failure ([anthropics/claude-ai-mcp#410](https://github.com/anthropics/claude-ai-mcp/issues/410),
  closed "not planned") looked like a discovery-header problem; the follow-up
  [#478](https://github.com/anthropics/claude-ai-mcp/issues/478) (closed
  2026-06-25) got the real diagnosis from Anthropic: the flow died at **DCR —
  Cloudflare Zero Trust returned `400 invalid_client_metadata: redirect_uri is
  not allowed by the account configuration`**. Fix: allowlist
  `https://claude.ai/api/mcp/auth_callback` in Zero Trust's MCP OAuth client
  registration settings, then remove/re-add the connector — **confirmed working
  by a reporter on 2026-06-29**. Both discovery-side concerns also resolved
  independently: Anthropic now documents claude.ai's fallback probe of
  `/.well-known/oauth-protected-resource[/<path>]`, and Cloudflare documents
  emitting `WWW-Authenticate: Bearer ... resource_metadata=...` on 401 —
  pointing at a **nonstandard PRM path**
  (`/.well-known/cloudflare-access-protected-resource/`), which is fine only
  because clients follow the header pointer rather than guessing the path.
  Keep the 30-minute test gate anyway (portals are still beta). *Contingency:*
  Worker + [`workers-oauth-provider`](https://github.com/cloudflare/workers-oauth-provider)
  (v0.8.1, 2026-06-19) implements the full Anthropic checklist — 401+WWW-Authenticate,
  RFC 9728 PRM, RFC 8414, DCR, CIMD (opt-in, default off — DCR suffices), S256
  (set `allowPlainPKCE: false`), refresh-token rotation. The library only
  dispatches to Worker handlers, but the authenticated `apiHandler` can simply
  be a small proxy that `fetch()`es the tunnel origin and streams the response
  back (`Mcp-Session-Id` passes through as an ordinary header; the forwarded
  Host is the allowlisted public hostname, so HostGuard is satisfied). Put
  Access in front of just the `/authorize` route for the human login; the
  Worker issues the tokens.

- **The "pre-registered client id" lever is a dead end on the portal path.**
  Claude's connector UI does accept a manual client id + optional secret, but
  Access Managed OAuth has no documented way to pre-register a client — DCR
  yields public clients only. It no longer matters (the #478 fix unblocks DCR).
  Cloudflare's non-OAuth alternative is **Access service tokens** (supported by
  portals since 2026-06-26) — useful for headless/machine clients, not for the
  claude.ai connector flow.

- **Portal "Code Mode" is ON by default and rewrites the tool surface.** Since
  ~June 2026 portals default to collapsing upstream tools into a single
  code-execution tool (the agent scripts against typed methods in an isolated
  Worker). That collides with this repo's **locked MCP tool-name contract**
  (CLAUDE.md "MCP API stability": `search_emails`, `partIndex` round-trips, the
  server-built `webmailLink` the tool descriptions tell clients to render, …) —
  the client may never see the tool descriptions as written. There are
  per-connection query params (`?codemode=search_and_execute`,
  `?optimize_context=minimize_tools`); during the test gate, confirm Code Mode
  can be disabled per portal (or that the plain tool surface is intact), and
  re-check `webmailLink` rendering end-to-end on iOS.

- **The account-level connector also appears on Desktop.** Custom connectors are
  account-scoped and sync to **every** client (web, Desktop, Cowork, mobile).
  Registering "Mailvec (Cloud)" for iOS means it also shows up on Desktop next
  to the local MCPB → **two identical tool sets**.

- **No automatic "prefer local / lower-latency" routing.** Claude exposes the union of all
  enabled tools and picks nondeterministically. The dedup rule only fires on an **exact
  endpoint match**; a **stdio command ≠ an `https://` URL**, so the local and cloud Mailvecs
  are *not* deduped. → Must disambiguate manually:
  - Distinct display name ("Mailvec" vs "Mailvec (Cloud)").
  - Disable the cloud connector on Desktop (per-device if the toggle is per-surface;
    otherwise per-conversation via `+` → Connectors).

- **Per-device vs synced toggle is unconfirmed.** Still undocumented as of
  2026-07-10; the only documented granularity is **per-conversation** (`+` →
  Connectors). If the on/off state turns out to be account-synced, use the
  per-conversation toggle on Desktop as the deterministic fallback.

- **Exposure of `view_attachment` / `get_attachment_page_image`.** They feed
  mail bytes (attacker-controlled by definition) to native parsers
  (PDFium/SkiaSharp) and return whole raw documents. Once the endpoint is
  internet-reachable (even gated), that's the highest-risk pair — dropped
  server-side via `Mcp:DisabledTools` (staged in compose.yml), which also
  covers the direct LAN port that bypasses the OAuth front. The Mac's local
  stdio MCPB is a separate process with its own config, so local usage keeps
  the full surface.

- **Keep `/health` and `/tray/*` private.** The security model leans on
  loopback/compose-network isolation; those endpoints are unauthenticated. The
  tunnel's public-hostname rules must 404 them *before* the catch-all route
  that forwards to `mcp:3333` (step 3) — MCP lives at `/`, so there is no
  narrower path to allow-list instead. The compose healthcheck curls `/health`
  from inside the network and is unaffected. Verify the 404s from outside
  before go-live.

- **`serverInfo.name = "mailvec"` is shared** between local and cloud. Fine for the
  connector list (display name is separate), but it's an identical protocol identity — keep
  it in mind if Claude Code's by-name dedup ever enters the picture.

- **Latency is not the deciding factor.** Cloudflare overhead is ~30–70 ms on top of mobile
  RTT, and `search_emails` is Ollama-bound anyway. Fine for iOS. (Don't route desktop
  traffic through it, though — that's the whole point of step 10.)

---

## Open questions to resolve by testing

1. ~~Does the MCP Server Portal's Access OAuth complete claude.ai's connector
   handshake?~~ Expected **yes** with the #478 redirect-URI allowlist applied
   (confirmed working by a third party 2026-06-29) — the 30-minute test gate
   confirms it on this account. The static-client-id variant is off the table
   (not registrable against Managed OAuth).
2. Is the connector enable/disable toggle **per-device** on Desktop, or account-synced?
   Determines whether step 10 is permanent or per-conversation.
3. ~~Does per-tool disable on the portal cover `view_attachment`?~~ Resolved
   server-side: `Mcp:DisabledTools` enforces the trim regardless of the front;
   portal per-tool toggles (confirmed to exist, with alias/description
   overrides) are an optional second layer.
4. Can **Code Mode** be disabled per portal, and does the plain tool surface
   (names, descriptions, `webmailLink` rendering) survive the portal
   unmodified?
5. Do the dashboard path rules match exactly or as regex? (Undocumented for
   remotely-managed tunnels.) Settle it with the external `curl -i` checks in
   step 3, or push the rules via the configurations API.
