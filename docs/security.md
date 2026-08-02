# Security model

Single-user. The pipeline runs as a compose stack on a homelab Docker VM
([deploy-docker.md](deploy-docker.md)), and the MCP server is **exposed to the
public internet** through a Cloudflare Tunnel gated by Cloudflare Access
Managed OAuth ([remote-access-cloudflare.md](remote-access-cloudflare.md)).

The trust boundary is therefore two-layered:

- **The Access identity gate** is the outer boundary. One identity (the owner)
  passes it; everything else is refused at Cloudflare's edge, before any
  traffic reaches the tunnel.
- **The Docker VM's compose network** is the inner boundary. Inside it the MCP
  server has no auth of its own — it trusts anything that can reach
  `mcp:3333`. The tunnel is the only ingress, so "anything that can reach it"
  means the cloudflared sidecar and the other containers.

This document captures what's exposed, what's explicitly accepted, and what's
out of scope — read it before publishing a host port, adding an identity to the
Access policy, adding a mutating tool, or changing the tunnel's ingress rules.

> **Historical note.** This model used to read "single-user, single-Mac; outside
> the macOS user account, Mailvec is unreachable." That has not been true since
> the container migration + tunnel go-live. Several accepted-risk arguments below
> originally leaned on "everything is local"; where that prop is gone, the
> reasoning has been restated rather than quietly kept.

## What's exposed

| Surface | Binding | Auth | Who can reach it |
| --- | --- | --- | --- |
| **MCP HTTP (public)** | `mailvec.<domain>` via Cloudflare Tunnel → `mcp:3333` | **Cloudflare Access Managed OAuth (OAuth 2.1 / PKCE), single-identity policy** | the owner, from any Claude surface — and Anthropic's cloud, which is what actually issues the calls |
| MCP HTTP (in-network) | `0.0.0.0:3333` inside the compose network (`Mcp__BindAddress`) | none — HostGuard only | the cloudflared sidecar and any other container on the network. **No host port is published**; publishing one exposes this unauthenticated to the LAN |
| MCP stdio | child process of the spawning agent | inherits agent's identity | dormant — retired as the Claude Desktop transport; still available for local dev |
| `/up` (minimal: status + version) | forwarded through the tunnel to `mcp:3333` | Cloudflare Access — **single layer, by design** (it's the monitoring endpoint) | the owner, plus a path-scoped Access **service token** (Uptime Kuma) — **migration pending, see below** |
| `/health` (detailed) | forwarded through the tunnel to `mcp:3333` | Cloudflare Access | the owner. Also the loopback consumers inside the container: the compose healthcheck and `mailvec doctor` |
| `/tray/*` | **not mapped in the container** (`Mcp:EnableTrayEndpoints=false`) *and* 404'd at the tunnel | served nowhere reachable | nobody — it's a local macOS-only surface |
| Ollama (outbound) | the GPU VM over the LAN (`Ollama:BaseUrl`) | none | the embedder (chunk embeddings **and** rendered attachment images sent to the vision model for OCR) + MCP query embeddings — read-only against Ollama |
| SQLite file | bind mount on the VM | unix permissions (0600, container root) | root on the VM, and every container that mounts `./data` |
| Maildir | bind mount on the VM | unix permissions; mounted **read-only** into every service except mbsync | same |

### `/up`, `/health` and `/tray/*`

All three are unauthenticated at the origin, but they carry very different data,
so they have deliberately different postures: `/up` is the internet-facing
monitoring endpoint, `/health` is its detailed sibling, and the mail-bearing
`/tray/*` is kept off the internet by two independent barriers.

**`/up` — the minimal monitoring endpoint.** Body is `{status, version}` and
nothing else; status codes are identical to `/health` (200 ok / 503 degraded),
because monitors alert on the code. Uptime Kuma polls it end-to-end *through the
tunnel*, which also catches tunnel / Access / edge / cert failures an in-network
probe can't. Single-layer Access is the accepted trade for having an external
probe — and with this body there is nothing left that would warrant
defense-in-depth.

**`/health` — detailed, for callers that have already earned it.** Status,
corpus counts, embedding model and dimensions, embedder failure detail, OCR
backlog, per-service liveness, the archive's filesystem path, and the internal
Ollama LAN URL. Its consumers are all local: the compose healthcheck (loopback,
inside the container) and `mailvec doctor`'s HTTP probe (`docker compose exec`).
Nothing outside the VM needs this body.

**Why two paths rather than one endpoint with less detail.** The origin cannot
authenticate anyone — Cloudflare Access is the entire gate — so path is the only
axis on which different callers can be served different detail, and it happens
to be the axis Access scopes on.

**The name `/up` is load-bearing.** Access path wildcards partial-match *inside*
a segment (`example.com/foo*/bar` covers `/food/bar`), so had this been
`/healthz`, an app scoped `health*` would have covered `/health` too — handing
the monitor the detailed body and quietly undoing the split. No wildcard over
"health" reaches "up". **If either path is ever renamed, keep the two names
prefix-disjoint.**

> **Pending as of 2026-08-01.** `/up` exists in the server; the Cloudflare side
> has *not* moved yet. Today Uptime Kuma still polls `/health`, and its service
> token is admitted by the root Access application's `Any Access Service Token`
> rule — so the monitoring credential currently reaches the whole MCP surface,
> i.e. the entire mailbox. Narrowing that rule and creating a path-scoped app
> for `/up` is tracked separately. Until that lands, treat the Kuma token as
> equivalent to owner access, and do not describe `/health` as protected from
> it. Verify with: Kuma token → `/up` succeeds, Kuma token → `/health` denied.

**`/tray/*` — mail-bearing, so belt-and-braces.** These return mail content
(`/tray/email/<id>` = full bodies, `/tray/folders` = folder map + counts,
`/tray/search` = full-text search, `/tray/system` = IMAP account) and accept
mutating POSTs (`/tray/control`, `/tray/attachment`), all unauthenticated at the
origin. They exist for the local macOS tray app and have **no consumer in the
container**. Two independent barriers now keep them unreachable, either
sufficient on its own:

1. **Disabled at the origin.** `Mcp:EnableTrayEndpoints=false` is baked into the
   container image (Dockerfile), so `mcp` never maps `/tray/*` — a request gets
   a plain Kestrel 404, no handler runs. Server-side and authoritative: it holds
   regardless of the tunnel config, the same reasoning as `Mcp:DisabledTools`.
   This is the load-bearing barrier.
2. **404'd at the tunnel.** The cloudflared ingress 404s the `/tray/` path
   before the catch-all that forwards to `mcp:3333`.

(Access covering the subdomain is a third barrier against anonymous callers, but
the origin disable is the one to rely on — it's server-side and independent of
any Cloudflare config.)

**Don't re-enable `/tray/*` on an internet-fronted deployment.** The surface has
no per-request auth of its own; giving it a remote story means building that
first (see [future-ideas.md](future-ideas.md)). The macOS / loopback install
keeps `EnableTrayEndpoints=true` because there the surface is loopback-only.

**Verify after any ingress or image change** (with a valid service token):
`curl -i .../tray/folders` must return **404** (origin unmapped), and
`curl -i .../up` must return `{"status":...,"version":...}`. Once the pending
Access migration lands, add the negative check that actually proves the split:
`curl -i .../health` with the **monitoring** token must be **denied**.

## Container hardening

Every acceptance above is about who can *call* Mailvec. This section is about
what happens after something is already running code inside it — a distinct
question, because the parsers eat attacker-chosen bytes by design. Anyone who
can send mail can put a PDF or image in front of PDFium/SkiaSharp, and the
embedder's OCR pass feeds them **unattended**, with no tool call and no user in
the loop. "The MCP tools are read-only" is a statement about the tool surface,
not a boundary that survives a compromised process.

All five services therefore run with:

| Control | What it buys |
| --- | --- |
| `cap_drop: [ALL]` | No Linux capabilities. Removes `DAC_OVERRIDE` (bypassing file permission bits), `FOWNER`, `NET_RAW` (raw sockets / spoofing), `SETUID`, and the rest. Nothing here needs any: the .NET services bind 3333 (unprivileged), mbsync makes outbound TLS connections, cloudflared dials out. |
| `security_opt: [no-new-privileges:true]` | A setuid binary can't raise privileges — so a dropped capability stays dropped, and this holds even after the services move to a non-root UID. |
| `mem_limit` | Caps blast radius per service (mcp 3g, indexer/embedder 2g, mbsync 512m, cloudflared 256m). A decode bomb or a parser leak kills **one container** instead of the Docker VM. |
| `pids_limit` | Bounds task count (512 .NET / 256 cloudflared / 128 mbsync) so a fork bomb can't exhaust the VM's pid space. The cgroup controller counts threads, not just processes. |

Two consequences worth knowing rather than rediscovering:

- **`cap_drop: ALL` means container-root no longer bypasses permission bits.**
  Steady state is unaffected — everything under `./data` and `./mail` is created
  by these containers, so root already owns it. The exception is a **seeded**
  `archive.sqlite` copied in by a non-root host user: without `DAC_OVERRIDE`,
  0600-owned-by-someone-else is simply unreadable, and it surfaces as a bare
  SQLite "unable to open database file". The seeding steps in
  [deploy-docker.md](deploy-docker.md#migrating-the-archive-from-the-mac) chown
  it to `0:0` for this reason.
- **`mem_limit` charges page cache to the cgroup.** mcp's is deliberately roomy
  because search latency depends on ~1.2 GB of chunk vectors sitting in the OS
  file cache (process RSS is ~22 MB — they are not in the .NET heap). Tuning it
  toward the working set doesn't OOM anything; the kernel just reclaims those
  pages, and search degrades from ~0.3 s to ~2-3 s permanently, silently. See
  [search-performance.md](contributing/search-performance.md).

**Not yet done**: non-root UIDs, read-only root filesystems, network
segmentation, and a read-only database connection for mcp (which today needs
write access because `SchemaMigrator.EnsureUpToDate` runs at startup). So a
compromised process still runs as root inside its container and can still write
`./data` — these controls narrow the exit routes, they don't remove them.

## The other shape: a loopback-only local install

`ops/install-all.sh` still produces the original single-Mac deployment — launchd services, MCP bound to `127.0.0.1:3333`, the MCPB bundle for Claude Desktop, the tray polling `/tray/*` over loopback. It remains supported and is what [`docs/clients/`](clients/README.md) documents. Its model is the one this page used to describe in full:

- **The trust boundary is the macOS user account.** Inside it, any local process can call any tool; outside, Mailvec is unreachable.
- **Loopback is per-host, not per-user.** A second account on the same Mac can `curl http://127.0.0.1:3333/` and read your mail. Accepted because the realistic adversary already has unix-level read access to `~/Mail/` and `~/Library/Application Support/Mailvec/archive.sqlite` and doesn't need MCP to extract them.
- **No inbound external traffic**, hence no inbound TLS and no auth. HostGuard is the only network-facing control, and it defends solely against browser-mediated DNS rebinding.
- **The native-parser exposure is smaller here**, because the on-demand tools are reachable only from the local machine. The embedder's unattended OCR pass is unchanged and remains the dominant surface either way.

The two shapes are not meant to be mixed. Everything else on this page describes the container + tunnel deployment; if you're running loopback-only, read the accepted-risk conditions below as *already satisfied* rather than as live constraints.

## Tools and data flow

All seven MCP tools (`search_emails`, `get_email`, `get_thread`, `list_folders`, `view_attachment`, `get_attachment_text`, `get_attachment_page_image`) are **read-only against the database** — none mutate `messages`, `chunks`, or `attachments` — and, as of the attachment-in-memory rework, **none write to the filesystem** either. `view_attachment` and `get_attachment_page_image` decode attachment bytes out of the Maildir *in memory* (inlining an image / small text file, or rasterising a PDF page) and persist nothing; `get_attachment_text` is a pure DB read of stored `extracted_text`. The only attachment writes to disk are the explicit, user-initiated download paths — the tray's Save button (`/tray/attachment`) and `mailvec extract-attachments` — which go through `AttachmentExtractor.Extract` and its [defense-in-depth path checks](../src/Mailvec.Core/Attachments/AttachmentExtractor.cs):

- `Path.GetFileName` strips directory components from caller-supplied filenames
- canonical-path containment refuses any target outside the configured download dir
- a `ReparsePoint` check refuses to overwrite an existing symlink at the destination (TOCTOU mitigation)
- write-then-rename via `.part` sibling so a concurrent reader never sees a partial file

`AttachmentDownloadDir` is intentionally `~/Downloads/mailvec/` (visible to the user). Don't move it to a hidden directory or `~/Library/Caches/` — that hides forensic evidence if a tool ever does write something unexpected.

## Host / origin validation (DNS-rebinding guard)

Loopback binding stops other *hosts* on the network from routing to `:3333`, but it does **not** stop a web page the user visits from reaching it. A page on `evil.com` can hold a connection, let its DNS TTL expire, re-resolve `evil.com` to `127.0.0.1`, and then issue requests the browser treats as *same-origin* — at which point page JavaScript could read `/tray/email/<id>` (mail bodies), `/tray/system` (IMAP username), or POST to the mutating `/tray/control` and `/tray/attachment` endpoints.

Every HTTP request (MCP, `/health`, and all `/tray/*`) therefore passes through a guard ([`HostGuard`](../src/Mailvec.Mcp/HostGuard.cs), wired in [`Program.cs`](../src/Mailvec.Mcp/Program.cs) `RunHttp`) that returns **403** unless:

- the `Host` header's hostname is an allowed name (`localhost` / `127.0.0.1` / `::1` always, plus anything in `Mcp:AllowedHosts`), and
- the `Origin` header, when present, also resolves to an allowed name.

After a rebind the browser still sends `Host: evil.com`, so the request is refused before reaching any handler. Native clients (Claude Code's MCP transport, the tray's `URLSession`) connect to loopback and send no `Origin`, so they're unaffected. This is **not** authentication — a hostile local process can still spoof the `Host` header; it defends specifically against the browser-mediated cross-origin vector.

**The tunnel depends on this.** cloudflared forwards the original public `Host` header, so `MCP_PUBLIC_HOSTNAME` must be set in the VM's `.env` (compose wires it into `Mcp:AllowedHosts`, alongside `mcp` for in-network access) or **every request through the tunnel 403s**. See [remote-access-cloudflare.md](remote-access-cloudflare.md).

The guard is defense-in-depth, **not** the auth boundary — that's Cloudflare Access. A `Host` header is trivially spoofed by anything that can already reach the origin, so HostGuard buys nothing against a caller inside the compose network or on the LAN if a port were published. It defends specifically against the browser-mediated rebinding vector. Note `/tray/*` is additionally unmapped in the container (`Mcp:EnableTrayEndpoints=false`) and 404'd at the tunnel — see [the endpoint posture above](#up-health-and-tray); `/up` and `/health` are intentionally forwarded.

## What's accepted

These are explicit decisions, not oversights:

- **The MCP origin has no auth of its own; Cloudflare Access is the entire gate.** Anything that can reach `mcp:3333` inside the compose network can call any tool. This is the deliberate division of labour — the origin stays simple, the edge does identity — and it holds precisely as long as the tunnel is the only ingress. **Publishing the mcp container's `ports:` mapping breaks it**: port 3333 then answers any host on the LAN with no OAuth at all, and several of the acceptances below stop holding.
- **No per-tool authorization.** Any caller that clears the Access gate and can invoke `search_emails` can also invoke `view_attachment`. Trivially simple while every tool is read-only and the policy admits exactly one identity — revisit if a write tool ever lands, or if a second identity is added (sending mail is out of scope, but the principle applies if anything in that direction ever gets considered).
- **Untrusted PDFs and images are parsed by native code, and the two tools that do it on demand are exposed over the tunnel.** PDFtoImage/PDFium (PDF rasterisation) and SkiaSharp (image decode) are native C++ libraries, so a malicious PDF/image is a memory-safety attack surface the managed extractors (`PdfPig` / `OpenXml`) aren't. This runs in **two** places: `get_attachment_page_image` / `view_attachment` (on demand, via MCP) and the **embedder's OCR pass**, which renders scanned PDFs and images *automatically and unattended* for every such attachment that arrives by mail.

  `Mcp:DisabledTools` (which drops tools from both tools/list and tools/call at the server) is staged-but-**commented** in compose.yml, so the on-demand pair stays reachable through the tunnel. That's a deliberate call, resting on two things:

  1. **The unattended pass dominates the on-demand one.** The embedder already feeds every scanned PDF and image that arrives by mail to PDFium/SkiaSharp, with no tool call, as a side effect of delivery. An attacker who can mail you a malicious PDF already reaches those parsers. Disabling the two tools would not close that path — it would only remove the *smaller*, attended half of the same exposure.
  2. **The gate admits one identity.** Reaching the tools requires clearing Access Managed OAuth as the owner. The "remotely-reachable native parser" concern the earlier revision of this doc raised assumed an exposed origin; with the tunnel as sole ingress and a single-identity policy, the only caller who can invoke them is the owner, on their own mail.

  **This acceptance is conditional. It stops holding if any of these change** — reinstate the `Mcp__DisabledTools__*` lines in compose.yml if so:
  - the mcp container publishes a host port (unauthenticated LAN callers, no OAuth);
  - a second identity is added to the Access policy;
  - the tunnel's ingress rules stop 404-ing the unauthenticated surfaces;
  - a mutating tool lands, changing what a parser compromise gets you.

  See [remote-access-cloudflare.md](remote-access-cloudflare.md) and [Future ideas](future-ideas.md).
- **No rate limiting.** A chatty agent can burn VM CPU on SQLite reads and GPU-VM time on embedding queries. SQLite WAL handles concurrent readers fine and Ollama is the natural bottleneck on the embedding leg, so the worst case is "the homelab slows down briefly." The Access gate bounds who can do this to one identity; Cloudflare's edge absorbs unauthenticated flood traffic before it reaches the tunnel.
- **`Mcp:LogToolCalls` is off by default.** When on, the server logs each tool call's arguments **and a summary of its results**. Both halves carry mailbox PII, and the result half is the one that surprises people:
  - `search_emails` — the free-text query and `fromContains` / `fromExact` filters, plus the **top 5 hits' sender addresses, subjects and dates**.
  - `get_email` — sender address and subject.
  - `get_thread` — the root subject and up to 5 participant addresses.
  - `view_attachment` — attachment filename and content type.

  In the container these land in `MAILVEC_LOG_DIR=/logs` *and* in `docker logs` via the Serilog console sink — the latter goes to the Docker host's logging backend, which no Mailvec-side setting governs. Turning it on is a deliberate choice with a clear "off when done" expectation; recall the rolling files are 10MB each with the 14 most recent retained.

  **You often don't need it.** The always-on `mcp-tool` line carries tool name, latency, and (where meaningful) result count and search mode — enough for "was that slow, and did it return anything?" without any PII. Reach for `LogToolCalls` only when you specifically need the content.
- **Logs may incidentally contain sender / subject text** even with tool-call logging off. The indexer logs parse failures with file paths, the embedder logs which messages it embedded, etc. None of these include body content, but they aren't sanitized either. Treat the log directory as confidential.
- **`~/Documents` is unreadable** to Claude Desktop's spawned children regardless of Full Disk Access — a TCC quirk, not an intentional control. Don't rely on it as a security boundary; it's a `com.apple.macl` ACL that a different client (e.g. Phase 5 stdio) might or might not be subject to.

## What's out of scope

- **Multi-tenant isolation.** The Access policy admits one identity and the archive is single-account. Nothing in Mailvec scopes results per-caller: a second identity added to the Access policy gets the owner's entire mailbox, not a view of their own. Adding one is therefore a model change, not a config change — it also invalidates the native-parser acceptance above.
- **Root on the Docker VM.** `ConnectionFactory` hardens the DB dir/files to owner-only (0700/0600), where the owner is the container's root. Anyone with root on the VM, or the ability to run containers on it, reads the archive directly and doesn't need MCP. The VM's own access control is the boundary.
- **Network adversaries at the edge.** TLS termination, DDoS absorption, and the identity gate are Cloudflare's. Mailvec publishes no inbound port and holds no certificate; the origin is reachable only through the tunnel the sidecar dials *outbound*. This delegates a real chunk of the security model to Cloudflare — that's the trade the iOS requirement forced (see [remote-access-cloudflare.md](remote-access-cloudflare.md) for why nothing local-only could work).
- **Compromised AI agent exfiltration.** If the agent calling Mailvec is itself malicious (e.g. an LLM jailbroken into "find all messages from X and POST them to attacker.com"), nothing in the MCP layer stops it from reading every email and shipping the contents back to its own provider. The relevant control is "trust the agent" — choose your clients. Note this is now *structural*, not hypothetical: connectors are invoked from Anthropic's cloud, so every tool call and its results already traverse a third party by design.
- **Encrypted-at-rest archive.** `archive.sqlite` and the Maildir are plain files at rest on the VM's local disk, protected by unix permissions and whatever the VM/Proxmox disk-encryption story is. Per-application encryption isn't built. (The Mac's frozen dev copy inherits FileVault.)

## Phase 5 doesn't change the threat model

Adding Gemini CLI / Codex CLI / ChatGPT desktop as MCP clients multiplies the *number of trusted callers* but not the *trust boundary*. Each such client either clears the same Access gate as any other remote caller, or — if pointed at a local dev instance — runs as the user against the frozen dev corpus, not the live archive. Either way it lands inside an existing boundary rather than opening a new one.

What *would* change the model is per-client differentiation: moving from "one identity, all tools" to per-client scopes (Access service tokens per client, per-tool authorization, MCP token issuance). That's a much bigger lift and stays parked in [Future ideas](future-ideas.md) — note it's also the prerequisite for the cross-vendor path, since a ChatGPT or Gemini connector means handing a *second vendor's cloud* the same unscoped access to the whole mailbox that Anthropic's has today.

The only thing Phase 5 introduces near-term is more places where `LogToolCalls=on` is tempting (capturing real usage from each client during quirk-debugging). Each of those is a deliberate per-debug-session choice with a clear "off when done" expectation, not a default-on switch.
