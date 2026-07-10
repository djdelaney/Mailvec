# Remote MCP access via Cloudflare (desktop local + iOS remote)

Planning note. Goal: keep the **local** MCP path on the desktop (MCPB/stdio + the
launchd HTTP server on `127.0.0.1:3333`) exactly as-is, while exposing a **public,
authenticated** endpoint so the **Claude iOS app** can reach Mailvec from anywhere.

Status: plan decided 2026-07-10 (research summary in the ★ gotcha below);
not yet built. The sequence is: portal-first 30-minute test gate, Worker
fallback if the portal fails claude.ai's handshake. The server-side
tool-surface trim (`Mcp:DisabledTools`) is implemented and staged in
compose.yml — uncomment it before the tunnel goes live.

---

## Why this shape (the hard constraint)

Claude **custom connectors are called from Anthropic's cloud, not from the phone.**
Anthropic's outbound traffic originates from **`160.79.104.0/21`**, and both the MCP
server *and* its authorization server must be reachable from that range.

Consequences:
- **Tailscale / localhost / LAN are non-starters** for the iOS path — they're invisible
  to Anthropic's backend even when the phone is on the home network.
- iOS can only use a **remote connector**; it cannot run a local server.
- The endpoint must therefore be **publicly reachable + OAuth-gated**. Cloudflare Tunnel
  gives reachability without opening a port; Cloudflare Access supplies the OAuth gate.

---

## Target architecture

```
Desktop  (unchanged)
  Claude Desktop ──stdio──► local MCPB  ─┐
  Claude Desktop ──HTTP──►  127.0.0.1:3333 (launchd MCP)
                                          │  local, device-only, never leaves the Mac
                                          ▼
                                    archive.sqlite

iOS (new path)
  Claude iOS ──► Anthropic cloud (160.79.104.0/21)
                     │ OAuth (PKCE/S256)
                     ▼
            Cloudflare edge ──► Access gate ──► MCP portal / Worker
                     │ Cloudflare Tunnel (no open ports)
                     ▼
            cloudflared on the Mac ──► 127.0.0.1:3333  (same launchd MCP)
```

Both clients ultimately hit the **same** `Mailvec.Mcp` HTTP server. The desktop reaches
it directly; iOS reaches it through the Cloudflare-gated tunnel.

---

## Exact Cloudflare products

| Product | Role | Notes |
|---|---|---|
| **Cloudflare account + a DNS zone** | Public hostname (`mailvec.<domain>`) | Need a domain on Cloudflare DNS. |
| **Cloudflare One / Zero Trust** | Umbrella for the below | Free tier covers a single-user setup. |
| **Cloudflare Tunnel** (`cloudflared`) | Exposes `127.0.0.1:3333` with **no inbound ports** | Runs as a daemon on the Mac (launchd/`brew services`). |
| **Cloudflare Access** | Identity gate + browser OAuth flow | Needs an IdP: one-time-PIN email, Google, or GitHub. |
| **MCP Server Portal** (Zero Trust → AI controls) | MCP-aware front that presents OAuth to Claude | Primary auth approach. See ★ gotcha. |
| **Cloudflare Worker + `workers-oauth-provider`** | *Fallback* OAuth front if the portal isn't Claude-compatible | The clearly spec-compliant path; more setup. |
| **Argo Smart Routing** *(optional)* | Trims tunnel backbone latency ~20–30% | A few $/mo; only if latency annoys. |

---

## Requirements

**Claude side**
- Paid plan that supports custom connectors.
- Connector endpoint reachable from `160.79.104.0/21`.
- OAuth 2.1 with **PKCE / S256**, exact redirect-URI matching.
- Authorization-server discovery metadata at `/.well-known/...` (RFC 8414 or OIDC).
- Protected-resource metadata: `401` + `WWW-Authenticate: ...resource_metadata`, or
  `/.well-known/oauth-protected-resource[/<path>]`.
- A client-registration approach: **DCR**, **CIMD**, or Anthropic-held credentials.
  → All of these are provided by the Cloudflare auth layer, **not** by `Mailvec.Mcp`.

**Local side**
- `cloudflared` installed on the Mac running the launchd MCP service.
- The launchd HTTP MCP already serving Streamable HTTP at `127.0.0.1:3333/` (it does).
- Domain added to Cloudflare; an IdP configured in Zero Trust.

---

## Steps

1. **Leave the desktop alone.** Local MCPB + `127.0.0.1:3333` stay as configured. They
   are device-local and never appear on iOS.
2. **Install + auth `cloudflared`** on the Mac (`cloudflared tunnel login`).
3. **Create a named tunnel** with ingress rules that **404 the unauthenticated surfaces
   before the catch-all**. MCP is mounted at the root `/` (there is no dedicated "MCP
   path" to allow-list), so the shape is deny-then-forward:

   ```yaml
   ingress:
     - hostname: mailvec.<domain>
       path: ^/health$
       service: http_status:404
     - hostname: mailvec.<domain>
       path: ^/tray/.*
       service: http_status:404
     - hostname: mailvec.<domain>
       service: http://127.0.0.1:3333
     - service: http_status:404
   ```

4. **Add `mailvec.<domain>` to `Mcp:AllowedHosts`** in the shared config
   (`~/Library/Application Support/Mailvec/appsettings.Local.json`) and restart the MCP
   service. cloudflared forwards the original public `Host` header, and
   [`HostGuard`](../src/Mailvec.Mcp/HostGuard.cs) returns 403 for any hostname that isn't
   loopback or allowlisted — without this step **every request through the tunnel fails**.
   See [security.md](security.md#host--origin-validation-dns-rebinding-guard).
5. **Run the tunnel as a service** (launchd / `cloudflared service install`).
6. **Configure an IdP** in Zero Trust (one-time PIN email is simplest for a single user).
7. **Stand up the auth front (★):**
   - *Try first:* MCP Server Portal — add the tunneled server, attach an Access policy
     restricting to your identity, connect clients to `https://<sub>.<domain>/mcp`.
   - *If that fails Claude's OAuth:* deploy a Worker with `workers-oauth-provider` that
     proxies to the tunnel origin.
8. **Reduce the cloud tool surface — server-side first.** Uncomment the
   `Mcp__DisabledTools__*` lines in compose.yml (drops `view_attachment` and
   `get_attachment_page_image` from tools/list AND tools/call at the server).
   Server-side is the authoritative layer: it holds identically under the
   portal, the Worker, or the direct LAN port — which bypasses the OAuth
   front entirely. Portal per-tool toggles (or a Worker filter) are the
   optional second layer, not the enforcement.
9. **Register the connector on claude.ai (web)** with a **distinct name: "Mailvec (Cloud)"**
   and the `https://<sub>.<domain>/mcp` URL. Complete the OAuth flow once.
10. **Verify on iOS** — the connector syncs down; confirm tools load and a search works.
11. **Disable "Mailvec (Cloud)" on Desktop** so the desktop keeps using the fast local
    path (see gotcha on duplicates).

---

## Gotchas

- **★ Portal OAuth may not satisfy Claude's connector — evidence as of 2026-07-10.**
  Portals **do not add OAuth to the upstream server** — they gate the *client-facing*
  leg via Access Managed OAuth (GA 2026-03-20: DCR, PKCE, RFC 8414/9728 discovery on
  the app hostname). Research findings:
  - *Against:* anthropics/claude-ai-mcp#410 (2026-06-07, closed "not planned"):
    claude.ai **web/mobile** failed instantly against a Managed OAuth portal
    (401 without a `WWW-Authenticate: Bearer resource_metadata=...` header, and
    claude.ai didn't probe the well-known fallback) while **Claude Code connected
    to the identical URL** via fallback discovery.
  - *For:* Anthropic's current connector-auth docs describe claude.ai falling back
    to probing `/.well-known/oauth-protected-resource[/<path>]` when the header is
    missing — the June gap may be closed from Anthropic's side.
  - *Extra lever:* the custom-connector UI accepts a pre-registered client id
    (+ optional secret), which removes DCR from the equation if Access supports a
    manually registered client.
  Net: the 30-minute throwaway test connection decides it. If it still fails, fall
  back to the Worker + `workers-oauth-provider` path (v0.8.1, 2026-06-19, actively
  maintained; implements 401+WWW-Authenticate, PRM, RFC 8414, DCR, CIMD, S256, and
  refresh-token rotation — the full Anthropic checklist). The library only
  dispatches to Worker handlers, but the authenticated `apiHandler` can simply be a
  small proxy that `fetch()`es the tunnel origin and streams the response back
  (`Mcp-Session-Id` passes through as an ordinary header; the forwarded Host is the
  allowlisted public hostname, so HostGuard is satisfied). Put Access in front of
  just the `/authorize` route for the human login; the Worker issues the tokens.
  Claude requirements to hold either path to: redirect URI
  `https://claude.ai/api/mcp/auth_callback`, S256 advertised in
  `code_challenge_methods_supported`, PRM `resource` matching the entered URL
  exactly, form-urlencoded token endpoint, 10s OAuth-endpoint latency budget, and
  reachability from `160.79.104.0/21` for the MCP host AND the authorization host.

- **The account-level connector also appears on Desktop.** Custom connectors are
  account-scoped and sync to **every** client. Registering "Mailvec (Cloud)" for iOS means
  it also shows up on Desktop next to the local MCPB → **two identical tool sets**.

- **No automatic "prefer local / lower-latency" routing.** Claude exposes the union of all
  enabled tools and picks nondeterministically. The dedup rule only fires on an **exact
  endpoint match**; a **stdio command ≠ an `https://` URL**, so the local and cloud Mailvecs
  are *not* deduped. → Must disambiguate manually:
  - Distinct display name ("Mailvec" vs "Mailvec (Cloud)").
  - Disable the cloud connector on Desktop (per-device if the toggle is per-surface;
    otherwise per-conversation via `+` → Connectors).

- **Per-device vs synced toggle is unconfirmed.** If the connector on/off state turns out
  to be account-synced rather than per-device, use the **per-conversation** toggle on
  Desktop as the deterministic fallback.

- **Exposure of `view_attachment` / `get_attachment_page_image`.** They feed
  mail bytes (attacker-controlled by definition) to native parsers
  (PDFium/SkiaSharp) and return whole raw documents. Once the endpoint is
  internet-reachable (even gated), that's the highest-risk pair — dropped
  server-side via `Mcp:DisabledTools` (staged in compose.yml), which also
  covers the direct LAN port that bypasses the OAuth front. The Mac's local
  stdio MCPB is a separate process with its own config, so local usage keeps
  the full surface.

- **Keep `/health` and `/tray/*` private.** The security model leans on "bind to
  `127.0.0.1`"; those endpoints are unauthenticated. The tunnel ingress must 404 them
  *before* the catch-all rule that forwards to `127.0.0.1:3333` (step 3) — MCP lives at
  `/`, so there is no narrower path to allow-list instead.

- **`serverInfo.name = "mailvec"` is shared** between local and cloud. Fine for the
  connector list (display name is separate), but it's an identical protocol identity — keep
  it in mind if Claude Code's by-name dedup ever enters the picture.

- **Latency is not the deciding factor.** Cloudflare overhead is ~30–70 ms on top of mobile
  RTT, and `search_emails` is Ollama-bound anyway. Fine for iOS. (Don't route desktop
  traffic through it, though — that's the whole point of step 11.)

---

## Open questions to resolve by testing

1. Does the MCP Server Portal's Access OAuth complete claude.ai **web**'s connector
   handshake today? (Claude Code demonstrably works; web/mobile failed in June —
   see the ★ gotcha for both sides of the evidence.) If not → Worker fallback.
   Also try the static-client-id variant before giving up on the portal.
2. Is the connector enable/disable toggle **per-device** on Desktop, or account-synced?
   Determines whether step 11 is permanent or per-conversation.
3. ~~Does per-tool disable on the portal cover `view_attachment`?~~ Resolved
   server-side: `Mcp:DisabledTools` enforces the trim regardless of the front;
   portal toggles are an optional second layer.
