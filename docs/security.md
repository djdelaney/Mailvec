# Security model

Single-user. The pipeline runs as a compose stack on a homelab Docker VM
([deploy-docker.md](deploy-docker.md)), and the MCP server is **exposed to the
public internet** through a Cloudflare Tunnel gated by Cloudflare Access
Managed OAuth ([remote-access-cloudflare.md](remote-access-cloudflare.md)).

The trust boundary is therefore two-layered:

- **The Access identity gate** is the outer boundary. One identity (the owner)
  passes it; everything else is refused at Cloudflare's edge, before any
  traffic reaches the tunnel.
- **The Docker VM's compose network** is the inner boundary. By default the MCP
  server has no auth of its own inside it — it trusts anything that can reach
  `mcp:3333`. The tunnel is the only ingress, so "anything that can reach it"
  means the cloudflared sidecar and the other containers. Configuring
  [`Mcp:Access`](#origin-authentication-mcpaccess) turns this inner boundary
  into a real one: the origin then validates the Access assertion itself and
  refuses callers that don't carry a valid one.

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
| MCP HTTP (in-network) | `0.0.0.0:3333` inside the compose network (`Mcp__BindAddress`) | none — HostGuard only, **unless `Mcp:Access` is configured** ([below](#origin-authentication-mcpaccess)), which makes the origin validate the Access assertion itself | the cloudflared sidecar and any other container on the network. **No host port is published**; publishing one exposes this to the LAN — unauthenticated, unless origin validation is on |
| MCP stdio | child process of the spawning agent | inherits agent's identity | dormant — retired as the Claude Desktop transport; still available for local dev |
| `/up` (minimal: status/version + liveness booleans) | forwarded through the tunnel to `mcp:3333` | Cloudflare Access — **single layer, by design** (it's the monitoring endpoint) | the owner, plus a **path-scoped** Access service token for the external monitor (see below — the scoping is a requirement, and one worth verifying rather than assuming) |
| `/health` (detailed) | forwarded through the tunnel to `mcp:3333` | Cloudflare Access | the owner. Also the loopback consumers inside the container: the compose healthcheck and `mailvec doctor` |
| `/tray/*` | **not mapped in the container** (`Mcp:EnableTrayEndpoints=false`) *and* 404'd at the tunnel | served nowhere reachable | nobody — it's a local macOS-only surface |
| Ollama (outbound) | the GPU VM over the LAN (`Ollama:BaseUrl`) | none | the embedder (chunk embeddings **and** rendered attachment images sent to the vision model for OCR) + MCP query embeddings — read-only against Ollama |
| SQLite file | bind mount on the VM | unix permissions (0600, container root) | root on the VM, and every container that mounts `./data` |
| Maildir | bind mount on the VM | unix permissions; mounted **read-only** into every service except mbsync | same |

### `/up`, `/health` and `/tray/*`

All three are unauthenticated at the origin by default (see
[origin validation](#origin-authentication-mcpaccess) for the exception, which
covers `/up` and `/health` but not `/tray/*`), and they carry very different
data, so they have deliberately different postures: `/up` is the internet-facing
monitoring endpoint, `/health` is its detailed sibling, and the mail-bearing
`/tray/*` is kept off the internet by two independent barriers.

**`/up` — the minimal monitoring endpoint.** The rule for its body is
**booleans yes, values no**: `status`, `version`, and the flags the six Uptime
Kuma monitors query — `ollama.reachable`, `embedder.stuck`,
`embeddings.modelMismatch`, and per-service `stale`/`known`. Enough to say
*something is wrong and which thing*, with nothing that says what anything **is**
— no archive path, no corpus counts, no model identity, no Ollama address.
Status codes are identical to `/health` (200 ok / 503 degraded), because
monitors alert on the code. Uptime Kuma polls it end-to-end *through the
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

**Why two paths rather than one endpoint with less detail.** Path is the axis
Access scopes on, so it's the axis on which different callers can be served
different detail. That was originally the *only* axis, because the origin could
not authenticate anyone. It still stands with
[origin validation](#origin-authentication-mcpaccess) on — that adds an audience
check per endpoint, but the two Access applications are still distinguished by
path at the edge, and the trimmed `/up` body is what limits the damage when the
edge scoping is wrong. Two layers, not one replacing the other.

**The name `/up` is load-bearing.** Access path wildcards partial-match *inside*
a segment (`example.com/foo*/bar` covers `/food/bar`), so had this been
`/healthz`, an app scoped `health*` would have covered `/health` too — handing
the monitor the detailed body and quietly undoing the split. No wildcard over
"health" reaches "up". **If either path is ever renamed, keep the two names
prefix-disjoint.**

**The split only holds if the monitoring credential is actually scoped**, and
that lives in Cloudflare's control plane rather than in this repo — so it is a
requirement to verify, never a property to assume. Two rules and a check:

- The monitor's service token belongs on a **path-scoped Access application for
  the exact path `up`** — never a wildcard, since Access path wildcards
  partial-match inside a segment (`example.com/foo*/bar` covers `/food/bar`), so
  `up*` or `health*` quietly widens what the token reaches.
- **Never `Any Access Service Token` on the root application.** That rule admits
  every service token in the account — including ones created later, for
  unrelated things — and the root app is the whole MCP surface, i.e. the whole
  mailbox. Name the specific token instead.

**Verify** (with the monitoring token, from outside the network): `/up` returns
the status JSON, and **`/health` and `/` are both denied**. If either returns
content, the token is being admitted by the root application — almost always the
`Any Access Service Token` rule above — and the monitoring credential can read
mail. Treat it as owner-equivalent until that's fixed.

**`/tray/*` — mail-bearing, so belt-and-braces.** These return mail content
(`/tray/email/<id>` = full bodies, `/tray/folders` = folder map + counts,
`/tray/search` = full-text search, `/tray/system` = IMAP account) and accept
mutating POSTs (`/tray/control`, `/tray/attachment`), all unauthenticated at the
origin. They exist for the local macOS tray app and have **no consumer in the
container**. Two independent barriers now keep them unreachable, either
sufficient on its own:

1. **Disabled at the origin**, twice over. `Mcp:EnableTrayEndpoints=false` is
   baked into the container image (Dockerfile) *and* set explicitly in
   `compose.yml`. Either alone is sufficient — `mcp` never maps `/tray/*`, so a
   request gets a plain Kestrel 404 with no handler run. Server-side and
   authoritative: it holds regardless of the tunnel config, the same reasoning
   as `Mcp:DisabledTools`. This is the load-bearing barrier. The redundancy is
   deliberate: the image default covers a compose file that forgets, and the
   compose line states the deployment's posture where an operator will actually
   read it, without needing to know what the image bakes in.
2. **The combination is unrepresentable.** `TrayExposureGuard` makes the server
   **refuse to start** if the tray is enabled on anything but a loopback-only
   deployment. So re-enabling it in a container isn't a risky setting — it's a
   crash on boot with a message saying which knob to change. A default only
   protects until someone overrides it; this holds even then.
3. **404'd at the tunnel.** The cloudflared ingress 404s the `/tray/` path
   before the catch-all that forwards to `mcp:3333`.

**What the guard treats as "exposed"** — and this is the part worth
understanding, because the intuitive answer is wrong. The trigger is a
non-loopback `Mcp:BindAddress`, or a non-loopback name in `Mcp:AllowedHosts`.
It is deliberately **not** "is a public hostname configured", because HostGuard
always admits the loopback Host names whatever `AllowedHosts` says: a server
bound to `0.0.0.0` with an entirely empty `AllowedHosts` is still readable by
anything that can route to the port — it just sends `Host: localhost`. Keying
the guard off a configured hostname would miss exactly that shape.

(Access covering the subdomain is a third barrier against anonymous callers, but
the origin disable is the one to rely on — it's server-side and independent of
any Cloudflare config.)

**Don't re-enable `/tray/*` on an internet-fronted deployment.** The surface has
no per-request auth of its own; giving it a remote story means building that
first (see [future-ideas.md](future-ideas.md)). The macOS / loopback install
keeps `EnableTrayEndpoints=true` because there the surface is loopback-only.

**Verify after any ingress or image change** (with a valid service token):
`curl -i .../tray/folders` must return **404** (origin unmapped), and
`curl -i .../up` must return the status/version/liveness-boolean body. Once the pending
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

## Executable supply chain

Everything above assumes the code we run is the code we reviewed. Three inputs
arrive from outside the repo and execute, so each is pinned to something that
can't be re-pointed under us:

| Input | Pin | Why this one |
| --- | --- | --- |
| **sqlite-vec** (`vec0.dylib` / `.so`) | SHA-256 per version+RID in `ops/fetch-sqlite-vec.sh`, verified **before** `tar` runs | Loaded into every Mailvec process via SQLite's extension API — arbitrary code execution by design, with the services' full mailbox and DB access. A release asset can be replaced after its tag exists, so the tag alone doesn't identify what we reviewed. No bypass flag; an unrecorded version fails closed. Bump procedure in [`ops/UPGRADING.md`](../ops/UPGRADING.md#sqlite-vec-dylib) |
| **cloudflared** | image digest in `compose.yml` (version tag kept alongside for humans) | Holds the tunnel credential and is the only thing that can reach the unauthenticated mcp origin — the highest-value container in the stack. Was `:latest`, i.e. every `compose pull` could swap it silently |
| **Dockerfile bases** (`dotnet/sdk`, `dotnet/aspnet`, `alpine`) | image digests, version tag in the comment | Same reasoning one step earlier in the chain: `10.0` is a moving pointer, so it isn't what a reproducible build should resolve |
| **GitHub Actions** | full commit SHAs, version in a trailing comment | A major tag like `v7` is repointable by the action's owner, and these run with the repo's token — `publish-images.yml` grants `packages: write` |

**A pin with nothing bumping it is its own failure mode**: it trades supply-chain
risk for running a known-vulnerable version forever, and that risk is sharpest
for the Dockerfile bases, whose moving tags are how .NET servicing patches
arrive. `.github/dependabot.yml` therefore covers the `docker` and
`github-actions` ecosystems as well as NuGet, and Dependabot understands both
pin forms (it rewrites digest and comment together). **If Dependabot is ever
turned off, revert the base images to tags** rather than sitting on a frozen
base — the pin is only safe because something is bumping it. sqlite-vec is the
exception either way: it's fetched by a shell script no ecosystem parses, so its
bump is the manual loop in `ops/UPGRADING.md`.

CI additionally fails on any known-vulnerable NuGet package
(`dotnet list package --vulnerable --include-transitive`, transitive included
because that's where they land). This catches the window between Dependabot's
weekly runs — an advisory published against an already-pinned version produces
no PR until the next run, and nothing else in CI would notice. Published images
carry SLSA provenance and an SBOM as OCI attestations, so "which commit produced
this digest?" is answerable from the registry.

**Deliberately not done**: container image/filesystem scanning, and
environment/approval gates on publishing. Both are artifacts for a team with
someone to show them to; on a single-owner homelab where the operator builds and
deploys the image themselves, they generate review work with no reviewer. The
NuGet gate above is the exception because it's a real automated check with a
real failure mode, not a report.

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

## Origin authentication (`Mcp:Access`)

The origin can validate Cloudflare Access's `Cf-Access-Jwt-Assertion` itself
rather than trusting whatever reaches `mcp:3333`. Source:
[`AccessAuth.cs`](../src/Mailvec.Mcp/AccessAuth.cs) +
[`AccessOptions.cs`](../src/Mailvec.Core/Options/AccessOptions.cs).

**Why bother, when Access already gates the tunnel.** Three reasons, in order of
how likely they are to matter:

1. **The edge policy isn't in this repo.** It's Cloudflare dashboard state — no
   version control, no review, no test. "The gate is correct" has been an
   assumption verified by remembering to go and look. Origin validation makes
   the origin's half checkable in CI.
2. **It makes the `/up` split real.** The section above says the monitoring
   token must reach `/up` and nothing else, and openly concedes that this "lives
   in Cloudflare's control plane rather than in this repo — so it is a
   requirement to verify, never a property to assume." With `MonitoringAudience`
   set, a token minted for the path-scoped monitoring app is **rejected at the
   origin** on `/` and `/health` regardless of what the Access policy says.
   Pinned by `AccessAuthTests`.
3. **It survives a published port.** The single most dangerous config change in
   this stack — uncommenting the mcp `ports:` mapping — currently hands the
   whole mailbox to the LAN with no OAuth. With validation on, those callers
   carry no assertion and get a 401.

**What is validated**: signature (against the team's JWKS, fetched and refreshed
by the framework's `ConfigurationManager`), issuer, `exp`/`nbf` with a 60s skew,
and audience — coarse at the scheme (is this token for this deployment) then
narrow per endpoint (for *this* endpoint). Unsigned `alg:none`, wrong-issuer,
wrong-audience, expired, and malformed assertions all 401; a valid assertion for
the wrong application 403s.

**What is deliberately NOT trusted**: the `Cf-Access-Authenticated-User-Email`
header on its own — trivially forged by anything that can reach the origin, and
meaningful only when covered by a validated assertion. Nor the `Authorization`
header: on a claude.ai connector request that carries the connector's own OAuth
token, a different credential entirely, and JwtBearer's default fallback to it
is explicitly suppressed.

**Loopback is exempt** (`AllowLoopback`, default true). The compose healthcheck
curls `127.0.0.1:3333/health` from inside the mcp container and `mailvec doctor`
does the same under `docker compose exec`; neither has an assertion. Not a hole:
cloudflared and every sibling container connect to `mcp:3333` over the compose
network and arrive with a real address, so they are never exempt.

**Off by default, and that's not the same as a gap.** The loopback/launchd
install has no Cloudflare in front of it, no team domain and no assertion on any
request — defaulting this on would break that shape at startup. Fail-closed here
means *once configured, never silently degrade to allowing*: an `Enabled` with a
missing team domain or audience **refuses to start** (naming the missing knob),
a monitoring audience equal to the owner's refuses to start, and an unreachable
JWKS endpoint yields 401s rather than falling back to open. A non-loopback bind
with validation off logs a warning every boot rather than being quietly fine.

Enable procedure and the verification curls:
[remote-access-cloudflare.md](remote-access-cloudflare.md).

## Hostile mail content (indirect prompt injection)

Every field Mailvec returns is written by whoever sent the message: subject,
sender name and address, body text, HTML, attachment filenames, extracted
document text, and OCR'd text off scanned pages. A sender who can put a PDF in
front of the OCR pass can also put a *sentence* in front of the model.

**Read-only is not the boundary here, and saying it is gets the threat model
backwards.** "Mailvec can't send mail" is a statement about Mailvec. The agent
holding this connector generally also holds tools that can send, post, write, or
fetch — and mail content is the classic way an attacker reaches those. The
exposure is the *other* connectors in the session, which is exactly the surface
Mailvec has no control over.

What's built, and what each part is actually worth:

- **`ServerInstructions`** (`Program.cs::ConfigureServerInfo`) states the trust
  model once, as standing context: mail is data, never instruction; a sender
  address is not proof of identity; and — the part no tool description can carry
  — read-only bounds *Mailvec*, not the agent, so outward actions whose target
  or justification came out of the mailbox need explicit user confirmation.
- **Every mail-bearing tool description** repeats the classification
  (`ToolText.UntrustedContent`). Not redundancy: a client folds
  ServerInstructions into a system prompt once, but the model re-reads a tool
  description at the moment it decides to call — which is when the framing has
  to be in front of it. `view_attachment` and `get_attachment_page_image` add
  that it covers text read *off the pixels*; `search_emails` adds that a query
  taken from mail content lets one sender choose what you read next.
- **Tool annotations** (`ReadOnly = true`, `OpenWorld = false` on all seven) are
  the machine-readable half, for clients that gate confirmation on them.
- Pinned by `McpSurfaceTests` at the wire, because all of the above is free text
  that an edit can silently drop.

**This is framing, not enforcement, and the distinction matters.** None of it
stops a crafted message from reaching the model; it gives the model grounds to
refuse and a client grounds to ask. The residual risk is unchanged in kind —
see "Compromised AI agent exfiltration" below, which this does not address.
**Don't add regex "injection detection" and treat it as a boundary**: it fails
open on anything it doesn't match while reading like a control that works.

**Its efficacy is untested** — the tests prove the framing reaches the client,
not that a model obeys it. See the call-out under
[What's out of scope](#whats-out-of-scope) and
[future-ideas.md](future-ideas.md#adversarial-testing-of-the-prompt-injection-framing).

## Response bounds

Not a confidentiality control — a blast-radius one, and only partial (there is
still no rate limiting; see below).

- **Search** is bounded by `Mcp:SearchMaxLimit`, **attachment text** by its
  `maxChars`/`offset` window — both caller-supplied.
- **`get_thread` is the one whose size the caller doesn't choose**: a thread is
  as long as whoever replied to it made it. It's therefore capped by
  `Mcp:ThreadMaxMessages` (100) and, for `includeBodies=true`, an aggregate
  `Mcp:ThreadMaxBodyChars` (200k) budget spent oldest-first. Truncation is
  always reported (`truncated`, `totalCount`, per-entry `bodyTruncated`) —
  silent truncation is worse than none, because the model summarises half a
  thread as if it saw all of it.

## What's accepted

These are explicit decisions, not oversights:

- **The MCP origin has no auth of its own *unless `Mcp:Access` is configured*; otherwise Cloudflare Access is the entire gate.** With it unset — the default, and how this stack has always run — anything that can reach `mcp:3333` inside the compose network can call any tool. That was a deliberate division of labour (the origin stays simple, the edge does identity) and it holds precisely as long as the tunnel is the only ingress. **Publishing the mcp container's `ports:` mapping breaks it**: port 3333 then answers any host on the LAN with no OAuth at all, and several of the acceptances below stop holding. Turning on origin validation ([below](#origin-authentication-mcpaccess)) removes this acceptance rather than mitigating it — the server then refuses anything without a valid assertion, LAN callers included.
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
- **User-facing data policy** — retention, deletion, export, consent-at-onboarding, breach response. These presuppose data subjects other than the operator. Mailvec has exactly one user, who is also the person who runs it; a privacy policy addressed to yourself is paperwork, not a control. This becomes in scope the moment a second identity is admitted — at which point it arrives together with the multi-tenancy work above, not before it.
- **Container image / filesystem scanning and publish-approval gates in CI.** Both produce artifacts whose value is having someone to show them to: a scan report gated on severity needs a reviewer with authority to accept an exception, and an environment approval needs a second person to approve. On a single-owner homelab, the operator builds, reviews, and deploys — so these add ceremony without adding a decision-maker. The NuGet vulnerability gate above is deliberately *not* in this category: it's an automated check with a real pass/fail, not a report.
- **An external penetration test.** Disproportionate for one mailbox behind a single-identity Access policy, and the likely finding set is what's already written down here — no rate limiting, root containers, native parsers fed attacker bytes. Revisit if a second identity is ever admitted, which is the same trigger as the data-policy item.

> **What is *not* out of scope, and is genuinely untested: whether the
> hostile-content framing works.** [The framing above](#hostile-mail-content-indirect-prompt-injection)
> is pinned at the wire by `McpSurfaceTests` — but that asserts the text *reaches
> the client*, not that a model *acts on it*. A crafted message that talked an
> agent into chaining Mailvec output into another connector would pass every test
> in this repo today. Closing that means adversarial fixtures run against a real
> model with a second observable tool attached — closer in shape to the
> `baselines/` eval harness than to a unit test. Until it exists, treat the
> framing as a mitigation of unmeasured strength, not a control. Tracked, with
> what it would take and when to un-defer, in
> [future-ideas.md → Adversarial testing of the prompt-injection framing](future-ideas.md#adversarial-testing-of-the-prompt-injection-framing).

## Phase 5 doesn't change the threat model

Adding Gemini CLI / Codex CLI / ChatGPT desktop as MCP clients multiplies the *number of trusted callers* but not the *trust boundary*. Each such client either clears the same Access gate as any other remote caller, or — if pointed at a local dev instance — runs as the user against the frozen dev corpus, not the live archive. Either way it lands inside an existing boundary rather than opening a new one.

What *would* change the model is per-client differentiation: moving from "one identity, all tools" to per-client scopes (Access service tokens per client, per-tool authorization, MCP token issuance). That's a much bigger lift and stays parked in [Future ideas](future-ideas.md) — note it's also the prerequisite for the cross-vendor path, since a ChatGPT or Gemini connector means handing a *second vendor's cloud* the same unscoped access to the whole mailbox that Anthropic's has today.

The only thing Phase 5 introduces near-term is more places where `LogToolCalls=on` is tempting (capturing real usage from each client during quirk-debugging). Each of those is a deliberate per-debug-session choice with a clear "off when done" expectation, not a default-on switch.
