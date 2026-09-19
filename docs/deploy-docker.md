# Docker deployment

Running the full Mailvec pipeline as a Docker compose stack — the supported
alternative to the macOS launchd install, and the shape to use for an
always-on server. **Ollama runs outside the stack** (its own host, ideally
GPU-backed), and the MCP server is optionally exposed through a Cloudflare
tunnel behind Access ([remote-access-cloudflare.md](remote-access-cloudflare.md)).

This documents the container strategy, the deployment strategy, and a
[rollout checklist](#rollout-checklist) of the things worth verifying on a new
deployment. It deliberately records **no** live state for any particular
install — see the checklist's note on where that belongs.

```
IMAP host ◄─IMAP── mbsync ──► ./mail ──► indexer ─┐
                                                   ▼
cloudflared ──► mcp:3333 ◄────── ./data ◄── embedder ──► ollama host (GPU, LAN)
```

## Container strategy

- **One image, five binaries** ([Dockerfile](../Dockerfile)). Multi-stage
  `dotnet/sdk:10.0` → `dotnet/aspnet:10.0`, publishing Indexer / Embedder /
  Mcp / Cli / Parse to `/app/<svc>/`. Framework-dependent publish — the aspnet
  base supplies the runtime for all five. Each compose service selects its
  binary via `command:`; the image's default CMD is the MCP server. The CLI is
  on PATH as `mailvec`, so operator commands are
  `docker compose exec mcp mailvec status|doctor|eval|checkpoint ...`.
- **Only `/app/parse` carries a parser — enforced by the build, not the doc.**
  After publishing, the Dockerfile deletes MimeKit, PdfPig, OpenXml,
  AngleSharp, PDFium, SkiaSharp and LibTiff from the indexer, embedder, mcp
  and cli directories and asserts each deletion (a sentinel `test -e` before
  each `rm` fails the build if a package bump ever renames one). The image
  sets `Parser__Mode=remote` + `Parser__Endpoint=http://parse:3400`, so those
  four processes ship `.eml` bytes to the `parse` service and never parse
  anything themselves; `Parser__Mode=inprocess` inside a container fails at
  the first parse with `FileNotFoundException`, on purpose. See
  [The parse service](#the-parse-service) below and
  [docs/proposals/attachment-parser-isolation.md](proposals/attachment-parser-isolation.md).
- **Arch handling.** BuildKit's `TARGETARCH` maps to the RID (amd64 →
  `linux-x64`, arm64 → `linux-arm64`), so `--platform linux/amd64` builds an
  x86 server image from an Apple Silicon dev machine. `ops/fetch-sqlite-vec.sh`
  takes the RID as an argument and runs *inside* the build — the image never
  depends on host-fetched natives (`.dockerignore` excludes `runtimes/` for
  the same reason). The fetched `vec0.so` is copied to `./vec0.so` next to
  each binary so one arch-agnostic `Archive__SqliteVecExtensionPath` works
  for every service on either arch.
- **Native deps are NuGet-supplied on Linux.** PDFtoImage brings PDFium +
  SkiaSharp via `SkiaSharp.NativeAssets.Linux.NoDependencies` (no fontconfig
  needed — see the comment in Directory.Packages.props). Present in the
  published output, but confirm with a real OCR render on the host
  ([checklist](#rollout-checklist) item 5) — disk presence alone never proves
  the natives will load.
- **Config via env vars only.** Env vars are the highest-precedence config
  source, so the image bakes container-shaped defaults (`/data/archive.sqlite`,
  `/mail`, `Mcp__BindAddress=0.0.0.0`, `MAILVEC_LOG_DIR=/logs`) and compose
  layers deployment values (`Ollama__BaseUrl`, `Ingest__MaildirRoot=/mail/Fastmail`,
  `Fastmail__AccountId`) on top. The macOS shared-config file plays no role in
  containers. `MAILVEC_LAUNCHD` is deliberately unset — the Serilog console
  sink is what feeds `docker logs`.
- **Seeded-DB entrypoint guard.** `SchemaMigrator` silently creates a fresh
  empty schema when `Archive__DatabasePath` doesn't exist, so a bad volume
  mount would serve an empty archive that looks healthy. With
  `MAILVEC_REQUIRE_SEEDED_DB=1` (the compose default) the entrypoint refuses
  to start any service against a missing/empty archive. Set `0` only for a
  deliberate from-scratch rebuild. `docker exec` bypasses the entrypoint, so
  CLI commands still work against whatever state exists.
- **mbsync sidecar** (Dockerfile stage `mbsync`): Alpine + isync on a 60 s
  interval loop (`MBSYNC_INTERVAL_SECONDS`), replacing the
  `com.mailvec.mbsync` launchd job. **The two cadences deliberately differ**:
  the container loop sleeps *after* each sync completes and so cannot overlap
  itself at any interval, while the launchd job's `StartInterval` is an
  independent timer whose comment records a dated observation of
  `.mbsyncstate` lock failures at 300 s. That plist stays at 600 s until
  someone re-measures on a live macOS install — see the Dockerfile comment for
  why the inherited "a short schedule collides with an in-flight run"
  rationale does not describe this loop. Upstream uses `fcntl` record locks, not `flock(2)`; the distinction
  matters because those release when the process dies, so a leftover lock
  *filename* is not a held lock and should not be deleted by hand. Config is a
  bind-mounted `mbsyncrc` ([ops/mbsyncrc.container.example](../ops/mbsyncrc.container.example));
  the Fastmail app password is a compose file-secret read via `PassCmd`.
  Pull-only sync is enforced structurally: the maildir is mounted read-only
  into every service except mbsync.
- **macOS-only code degrades, by design.** `mailvec doctor` detects the
  container (`DOTNET_RUNNING_IN_CONTAINER` / `/.dockerenv`) and adapts rather
  than warning: it reports compose as the supervisor instead of a missing
  launchd, treats an absent `mbsync` binary as expected (sync runs in the
  sidecar image), and probes `/health` on loopback rather than the configured
  `0.0.0.0` bind. **Run it in the `mcp` service** — `indexer`/`embedder` share
  the image but run no server, so a doctor there correctly reports `/health`
  unreachable. No code paths block Linux startup.

## Deployment strategy

- **Where**: a Linux Docker host, as one compose project
  ([compose.yml](../compose.yml) — setup steps are in its header comment).
  Bind mounts `./data` (SQLite) and `./mail` (Maildir) must be **VM-local
  disk**: SQLite WAL needs real POSIX locking; never NFS/SMB. Multi-container
  WAL sharing on one local bind mount is the same multi-process pattern as
  the macOS launchd services. **On a developer Mac, never open the database
  from the host while containers have it** — Docker Desktop's bind mount does
  not share the WAL index (`-shm`) coherently across the VM boundary, so a
  host-side `sqlite3`/python read sees a stale view and its close can
  checkpoint that view over the containers' frames (observed 2026-09-15 during
  the parser-isolation smoke: two freshly indexed messages vanished, WAL
  truncated to 0 bytes). Query through a container instead:
  `docker run --rm -v ./data:/data <image> dotnet /app/cli/Mailvec.Cli.dll status`.
- **Ollama**: external over LAN. If you already run an instance for a macOS
  install, reuse it — its bind address, version floor, and pulled models
  (embedding + vision) are then already proven, and the compose `.env` takes
  the same `Ollama:BaseUrl`. GPU-backed OCR means `Embedder:OcrEnabled` can
  stay on from day one.
- **Seeding: snapshot, not rebuild.** One final `ops/export-db.sh` on the macOS install
  (checkpointed copy — never a live file + `-wal`), placed at
  `./data/archive.sqlite` on the VM. The embedding server/model/dimensions
  are bit-identical to what built the archive, so nothing re-embeds. After
  the first mbsync pull completes, the indexer's first full scan reconciles
  `sync_state`/`maildir_path` to the new Maildir layout via rename-repair
  (same Message-ID at a new path). Until that scan settles: expect
  `view_attachment` misses, and **do not run `purge-deleted`** — messages look
  transiently stale mid-reconciliation. Step-by-step commands below.

## Prebuilt images (GHCR)

Two deploy modes, both first-class; the compose file is identical for both
(`image:` + `build:` coexist, parametrized by `.env`):

- **Build on host (default):** `docker compose up -d --build`, tagging
  `mailvec:local` / `mailvec-mbsync:local`. Unchanged from day one.
- **Pull from GHCR:** `publish-images.yml` builds both images
  (`ghcr.io/<owner>/mailvec` at the `runtime` stage, `…/mailvec-mbsync` at
  the `mbsync` stage) on every **green** CI run on main — publishing is
  gated on CI success so `:latest` never advances on a red suite — plus
  `v*` release tags. On the host: set `MAILVEC_IMAGE` +
  `MAILVEC_MBSYNC_IMAGE` in `.env` to a **pinned** `sha-<gitsha>` or
  `v<version>` tag, then `docker compose pull && docker compose up -d`.
  (Compose builds when the tag isn't local, so the `pull` must come first;
  and don't run `--build` while the vars point at GHCR refs — it retags
  the remote name with a local build.)

Switching an existing seeded deployment to pulled images is a recreate, not
a resync: the archive and Maildir are bind mounts, so `mailvec status`
counts stay identical, nothing re-embeds (`modelMismatch` stays false —
same code, same model default), and mbsync resumes incrementally from its
`.mbsyncstate`. Take a backup first anyway: **SchemaMigrator runs against
the seeded archive on every start**, so a new image can migrate the DB in
place — which is also why this pipeline must never be wired to
Watchtower-style auto-updates. Update manually, backup-first, by bumping
the pinned tag. Old sha builds are pruned weekly by `cleanup.yml`
(keep-newest-2; `v*` and `latest` never deleted).

Note the on-host rationale still holds either way: the VM keeps the repo
clone (compose.yml, `.env`, `mbsyncrc`, `baselines/` for the parity gate) —
pulled images just mean the clone no longer needs to *build*.

### Release tags (`v*`) — what to pin in production

Two kinds of pin, with different lifetimes:

- **`sha-<gitsha>`** — published on every green-main run, **pruned to the
  newest 2** weekly by `cleanup.yml`. Fine for tracking main, but not
  durable: a stale `sha-` pin can be garbage-collected out from under a
  deployment. The *running* container survives (its image is local), but a
  re-pull, host rebuild, or rollback against a pruned tag fails.
- **`v<version>`** (and `latest`) — **never pruned**. Use `v*` for the
  production pin and for anything you may want to roll back to.
  A `v*` tag is the same image bytes as its underlying `sha-` — one
  durable, human-meaningful name for the same digest (which also protects
  that build's `sha-` tag from pruning: tags on one digest share a package
  version).

**The tag value is not free-form.** The repo-wide `<Version>` in
`Directory.Build.props` stamps all four binaries and `serverInfo.version`,
kept in lockstep with `manifest.json` by **`ops/release.sh`** (the only
sanctioned bump path; `ops/build-mcpb.sh --bump` delegates to it).
The `v*` tag must equal that version at the tagged commit, or the image's
label and what its binaries report from `mailvec status` / the MCP handshake
disagree forever — `publish-images.yml` enforces this: a `v*` push whose tag
doesn't match `<Version>` fails before building anything.

**Cutting a release** (dev machine, not the deploy host). One command does the
whole disciplined flow — push, wait for THIS commit's CI to go green, then tag —
and refuses to tag a red/cancelled run:

```sh
# --patch default; --minor for a tool-surface change or a schema migration
# (the "back up first" flag in the tag name, since a new image migrates the
# seeded archive in place). --ship needs the `gh` CLI and the main branch.
ops/release.sh --minor --ship
```

Or drive it by hand (what `--ship` automates), e.g. behind a PR:

```sh
ops/release.sh --minor          # commits the bump; must go green on main
git tag -a v0.1.30 -m "…" && git push origin v0.1.30   # only after CI is green
```

The tag push publishes `ghcr.io/<owner>/mailvec:v0.1.30` +
`…/mailvec-mbsync:v0.1.30` (plus the commit's `sha-` tag). It does **not**
move `:latest` (green-main / manual-dispatch only) — and note the `v*`
trigger is **not test-gated**, unlike the green-main path (it only checks
tag↔version agreement). That non-gating is exactly why the release rule is
"only tag commits that already passed CI on main," and why `--ship` exists to
enforce it rather than leaving it to discipline.

**Deploying it:** pin both vars in `.env` to `:v0.1.30`, then
`docker compose pull && docker compose up -d` (backup first — the
SchemaMigrator-on-start rule above), and verify the loop closes:
`/health` reports a `version` field
(`docker compose exec mcp curl -s localhost:3333/health`) that must equal
the image tag; `docker compose exec mcp mailvec status` prints the same.

## Migrating the archive from a macOS install

`ops/import-db.sh` does **not** apply here — it is the macOS destination path
(launchctl pause/resume, Application Support layout). The container
equivalent is placing the snapshot at the compose bind mount before first
start:

```sh
# 1. On the Mac — pauses the launchd writers, checkpoints, snapshots,
#    validates, resumes. The snapshot is one complete file: no -wal/-shm
#    sidecars exist for it or should ever be copied.
ops/export-db.sh --to you@docker-vm:

# 2. On the VM, from the compose directory, BEFORE the first `up`:
mkdir -p data
mv ~/mailvec-archive-snapshot.sqlite data/archive.sqlite
chmod 600 data/archive.sqlite
# The chown is REQUIRED, not tidiness: the services run as uid 10001 with
# cap_drop: [ALL], so nothing bypasses permission bits, and a 0600 file owned
# by the host user who scp'd it is unreadable to them. The entrypoint checks
# this and refuses to start with the chown to run; before the check existed
# the symptom was a bare SQLite "unable to open database file".
sudo chown -R 10001:10001 data

# 3. Bring the stack up. MAILVEC_REQUIRE_SEEDED_DB=1 (the default) makes the
#    entrypoint refuse to start if the seed didn't land where expected.
docker compose up -d --build

# 4. Verify the migrated archive is what's being served:
docker compose exec mcp mailvec status    # message/OCR counts match the Mac's
docker compose exec mcp mailvec doctor
```

- **Model identity is the hard prerequisite.** The snapshot's
  `metadata.embedding_model`/dimensions must match what the VM's embedder is
  configured for, or it refuses to start. Pointing `OLLAMA_BASE_URL` at the
  same GPU-VM Ollama that already serves the Mac satisfies this by
  construction (models already pulled, same versions).
- **Re-seeding later** (a fresher Mac snapshot over a container DB that has
  already run): `docker compose down` first, then replace
  `data/archive.sqlite` **and delete `data/archive.sqlite-wal` /
  `-shm`** — those sidecars belong to the container's previous run, and a
  stale WAL applied onto the new main file corrupts it. This is the same
  footgun `ops/import-db.sh` handles on macOS; here it's manual. **Re-do the
  `chown -R 10001:10001 data`** — the replacement file carries the copying
  user's ownership, and the first run recreates the sidecars itself.
- **After parity holds**, stop the macOS pipeline (`ops/install.sh --uninstall`)
  — its archive keeps diverging from the VM's the moment you export, so
  treat the macOS copy as a frozen rollback, not a peer. (Point your clients at
  the container first, so the macOS stdio MCP is no longer serving anything.)
- **Ranking parity gate.** After the embedder settles, run
  `docker compose exec mcp mailvec eval` against the latest baseline in
  `baselines/`. Same model + same vectors means any drift implicates the
  .NET-on-Linux platform swap specifically.
- **Exposure**: cloudflared sidecar (compose `tunnel` profile), token-based
  tunnel, ingress → `http://mcp:3333` (Streamable HTTP, stateless — no
  `Mcp-Session-Id` is issued, so no sticky routing or session affinity is
  needed at the tunnel), fronted by a Cloudflare Access self-hosted app using
  Managed OAuth. The MCP container **publishes no host port** — the tunnel is
  the only ingress, and keeping it that way is what the security model's
  accepted risks rest on. The DNS-rebinding **HostGuard**
  (src/Mailvec.Mcp/HostGuard.cs, fronts every route) 403s any Host header that
  isn't loopback or allowlisted — tunnel traffic carries the public hostname,
  so `MCP_PUBLIC_HOSTNAME` **must** be set in `.env` (compose wires it to
  `Mcp:AllowedHosts`, alongside `mcp` for in-network access) or every tunnelled
  request fails. `Mcp__BindAddress=0.0.0.0` inside the compose network is where
  the old bind-to-127.0.0.1 boundary stops applying; Access is what replaced
  it. Full model in [security.md](security.md), wiring in
  [remote-access-cloudflare.md](remote-access-cloudflare.md).
- **Health/monitoring**: compose healthcheck curls `/health` (30 s interval).
  Note `/health` returns 503 when Ollama is unreachable, so an Ollama VM
  outage shows as an *unhealthy mcp container* even though keyword search
  still works — informative, nothing restarts on it.
- **Backups are the host's**, not Mailvec's: cover the Docker host with
  whatever snapshot schedule and offsite shipping you run. That's a
  **crash-consistent** layer — a snapshot can land mid-transaction, with the
  `-wal` captured alongside the main file. SQLite is built for exactly that
  (a crash-consistent volume snapshot is equivalent to a power cut, which WAL
  recovery handles on next open), so this is a genuine backup, not a
  hopeful one — **provided `./data` and its `-wal`/`-shm` sidecars sit on one
  volume that snapshots atomically.** They do today; that's the invariant to
  preserve if the storage layout ever changes.

  An **app-consistent** copy is a stronger guarantee, and the only way to get
  one is pause-checkpoint-copy. `ops/export-db.sh` is macOS-only (it pauses
  writers via launchctl); the container equivalent is:
  `docker compose stop indexer embedder && docker compose exec mcp mailvec
  checkpoint && cp data/archive.sqlite <backup> && docker compose start
  indexer embedder` (mcp stays up — it's read-only against the DB, and the
  CLI rides inside its container). Worth running before anything that
  migrates the DB in place (a new image — see the SchemaMigrator-on-start
  warning above), and worth cronning only if VM-snapshot restores ever prove
  unsatisfying in practice. Note `ConnectionFactory`
  hardens the DB dir/files to owner-only (0700/0600) on open — on the VM
  that owner is the container's root, so run backup reads via
  `docker compose exec` or as root on the host.

## Hosted embedding provider (optional)

Off by default; Ollama serves embeddings unless `MAILVEC_EMBEDDING_PROFILE=hosted`
is set. To switch (read `docs/security.md` "Hosted embedding" FIRST — this
sends the corpus and all queries off-network):

1. Put the API key in `secrets/embedding_api_key` (owner-only, same pattern
   as `secrets/fastmail_password`; the file exists as empty from initial
   setup). `chmod 600` and verify.
2. Fill the `MAILVEC_EMBEDDING_*` block in `.env` — endpoint, model,
   dimensions, and YOUR asserted `MAILVEC_EMBEDDING_SPACE_ID` (see the
   proposal's decision 3; a wire model name is not an identity).
3. `docker compose run --rm --no-deps mcp mailvec switch-model --yes` to
   rebuild the vector table under the new space. **`run`, not `exec`,
   deliberately**: `exec` enters the EXISTING mcp container, which still
   carries the environment it was created with — the CLI would resolve the
   OLD profile and migrate to the wrong identity (or wrongly no-op). `run`
   creates a one-off container from the current `.env`. Then
   `docker compose up -d` to recreate the services (env is fixed at
   container creation — `restart` keeps old values).
4. The embedder re-embeds everything; watch `mailvec status` coverage and the
   Debug-level embedding-telemetry log lines for cost/rate-limit audit.
5. Going back to Ollama is the same dance in reverse: clear the profile in
   `.env`, then the same `docker compose run --rm --no-deps mcp mailvec
   switch-model --yes` (again `run`, for the same stale-environment reason),
   then `up -d`.

The identity guards refuse half-switched states (embedder AND semantic search)
— that refusal is the feature, not a bug to work around.

## Never edit `compose.yml` through a management UI

The live stack is managed by [Dockge](https://github.com/louislam/dockge), which
is compose-file-first — stacks are plain files on disk — **but it has an
editor, and that is the hazard.**

`compose.yml` leans on a YAML anchor (`x-mailvec: &mailvec-common`) with `<<:`
merge keys shared across mcp / indexer / embedder. **Any YAML round-trip through
a management UI expands anchors and drops comments.** Values survive — the
image digest pins and `${VAR:-default}` substitutions still resolve — but the
shared block flattens and the file stops matching the commit it came from. That
is silent at the time and shows up as an enormous unexplained diff at the next
deploy, by which point you can't tell an intentional change from UI damage.

**Edit on disk, from the repo, and verify:**

```sh
git checkout <tag-or-sha> -- compose.yml
md5sum compose.yml
git show <tag-or-sha>:compose.yml | md5sum      # must match
```

Related, same cause: **a private GHCR pull triggered inside Dockge does not see
the host-side `docker login`.** Pull from the host CLI. This bites on any
release that changes both images.

## Sizing `mem_limit` — it scales with corpus size, and overrunning it is silent

`mcp`'s `mem_limit` is 3g, and that number is **not** a constant that suits every
archive. Search latency depends on the chunk-vector working set sitting in the
OS page cache, and **a cgroup limit charges page cache to the container**, so
exceeding it doesn't OOM anything — the kernel just reclaims those pages and
search degrades from ~0.3 s to ~2–3 s, permanently, with no error, no log line,
and nothing visible to `mailvec doctor` or any `/up` monitor. It is the most
easily-missed failure mode in the stack precisely because nothing breaks.

Reference point, from one measured corpus: **76,208 messages / 292,808 chunks /
4.51 GB archive → `memory.peak` 2.0 G within 8 hours of start, against the 3 g
ceiling.** Note 8 hours is not long enough to distinguish "warmed and plateaued"
from "still climbing" — take your own reading over days, not hours.

```sh
# Peak since container start, versus the configured ceiling.
docker compose exec mcp cat /sys/fs/cgroup/memory.peak
docker inspect mailvec-mcp-1 --format 'limit={{.HostConfig.Memory}}'
```

If peak is creeping toward the ceiling, raise `mem_limit` rather than tuning it
down toward the working set — the headroom is the point. An archive several
times this size needs a proportionally larger limit and currently has no other
signal telling its operator so. See
[search-performance.md](contributing/search-performance.md).

## The parse service

Every mail-content parser — MIME, HTML, PDF text, Office, PDF rasterisation,
image decode — runs in one container, `parse`, that holds **no volumes, no
secrets and no route out** (its only network is the internal `parse` network),
runs as `nobody` (uid 65534: it owns no files, so there is nothing to chown;
the other services run as 10001 — see "Moving to non-root"), and is otherwise
hardened like the other .NET services. The indexer, the
embedder's OCR pass and the MCP viewer tools send it bytes over HTTP and get
plain data back. A memory-safety bug in PDFium, SkiaSharp or MimeKit's
`unsafe` parser core therefore lands in a process with nothing to read and
nowhere to send it; a document that hangs or exhausts a parser takes down the
parse container, not the pipeline.

What to know operationally:

- **It exits on purpose, twice over.** A parse that exceeds
  `MAILVEC_PARSER_TIMEOUT_SECONDS` (60) is answered with 504 and the process
  exits, because PDFium / PdfPig / OpenXml take no cancellation token and an
  overrunning parse can only be reclaimed by ending the process. It also exits
  cleanly after `MAILVEC_PARSER_MAX_REQUESTS` (500) requests, bounding how long
  a compromised process persists. `restart: unless-stopped` brings it back in
  seconds; `docker compose ps parse` showing a recent start time is normal.
- **It admits a bounded number of parses** (`MAILVEC_PARSER_MAX_CONCURRENT`, 4).
  A request that cannot get a slot within the request timeout is answered
  503, which the callers treat as "wait and retry", never as a fault of the
  document. Raise it only with the `mem_limit` — each slot can hold a whole
  decoded message.
- **A caller disconnecting does not restart it.** Stopping the indexer
  mid-parse, or a cancelled tool call, leaves the parse to finish within its
  own timeout; only a genuine overrun exits. A message over the request-body
  cap (`48 MB`) is refused by the *caller* before it is sent, as a property of
  the message; keep `Parser__MaxRequestBodyBytes` on the callers in step with
  the service's cap if you change either.
- **While it is down**, new mail is not indexed (the scan retries next tick),
  the OCR pass pauses, and `view_attachment` / `get_attachment_page_image`
  answer "parsing is temporarily unavailable". **Search keeps working** — it
  reads the database only. The callers classify the gap as "unavailable",
  never as a fault of any document; the CLI backfills wait for it to return
  (probing `/up` for up to `Parser:UnavailableWaitSeconds`, 60 s, which rides
  out the routine recycle below) and stop with exit 1 and a `STOPPED` line only
  if it stays down, never stamping anything.
- **A document that keeps crashing it is given up on, not the service.** A
  504-and-exit on one document is a strike against that document. The OCR
  pass retires it after five strikes counted while the service was otherwise
  answering; the indexer, after `Parser__MaxCrashesPerFile` (3) on the same
  file, parses it metadata-only and indexes the message with its attachments
  at `failed` — searchable by body, one document's text given up, and the
  service no longer restarted once a minute by it. `mailvec extract-attachments
  --reextract-*` revisits those once the parser is fixed. The counters are in
  memory, so a container restart grants another round.
- **The network it shares with mcp is one-way.** mcp joins the `parse`
  network to call the service; Docker networks are symmetric, so the service
  could call `mcp:3333` back. The network's subnet is pinned
  (`MAILVEC_PARSE_SUBNET`, default `172.31.255.0/24`) and mcp refuses every
  request from it (`Mcp__DeniedNetworks__0`, same variable) before any route,
  loopback excepted. Change the subnet in `.env` only if it collides with a
  network the host already has — and note the first `up -d` after this change
  recreates the `parse` network, which is a few seconds of parse outage the
  callers ride out. Verify from inside the stack:
  `docker compose exec parse curl -s -o /dev/null -w '%{http_code}' http://mcp:3333/up`
  must print `403`, while the same probe from `mcp` itself (loopback) prints
  `200` or `503`. If `parse` has no `curl` in your image, any container you
  attach with `--network <project>_parse` will do.
- **It never flips `/health` red.** `/health` carries a `parser` section
  (`mode`, `endpoint`, `reachable`) and `mailvec doctor` has a `Parser` check,
  both informational: a parse service outage is *its* outage, and restarting
  the mcp container for it would be wrong. Monitor `parse` with its own
  compose healthcheck (`/up`) if you want paging.
- **What its callers will accept from it is bounded too.** The client the
  indexer, embedder and mcp use never follows a redirect (a 3xx is a
  `Crashed` strike), uses no proxy, and refuses any response over
  `Parser:MaxResponseBytes` (64 MB, sized for a decoded 25 MB attachment in
  base64) while reading it — so a compromised service can neither turn its
  callers into an exfiltration path nor exhaust the process holding the
  archive. Nothing to configure; `Parser__MaxResponseBytes` exists per service
  if a larger honest answer ever appears.
- **The size gate travels with it.** `MAILVEC_ATTACHMENT_MAX_BYTES` (25 MB) is
  mirrored into the parse service so it agrees with the indexer about what
  "oversize" means.
- **Memory.** PdfPig / OpenXml / PDFium peaks now happen here (`mem_limit: 2g`),
  which is why the indexer dropped to 1 GB. Inside the cgroup .NET caps its
  managed heap at 75 %, which turns PdfPig memory bombs into a caught
  `OutOfMemoryException` and a `failed` extraction status; PDFium's native
  allocations are what the cgroup itself bounds, and an OOM kill here is a
  restart of this container only.

**Migrating a running stack to it.** Pull or build an image at or after this
change, then `docker compose up -d` (never `restart`, see below) so compose
creates the `parse` service and the `parse` network and re-attaches the three
callers. Confirm with `docker compose exec mcp mailvec doctor` (the `Parser`
line) and `docker compose exec mcp curl -s http://parse:3400/up`. Nothing in
the archive changes: no schema migration, no re-index, no re-embed.

## Applying a compose change to a running stack

Three things that are easy to get wrong and quiet when you do. All follow from
the container hardening (`cap_drop: [ALL]`, `no-new-privileges`, `mem_limit`,
`pids_limit` — see [security.md → Container hardening](security.md#container-hardening)).

**Use `docker compose up -d`, never `docker compose restart`.** `restart` reuses
each container's existing config, so it applies *none* of the hardening — the
stack comes back looking perfectly healthy with full capabilities and no
resource limits, and nothing anywhere says the change didn't take. `up -d`
recreates containers whose config changed, which is what actually applies it.

**Confirm it took**, since the failure above is invisible:

```sh
docker compose exec mcp grep CapEff /proc/1/status     # must be all zeros
docker inspect mailvec-mcp-1 --format \
  'CapDrop={{.HostConfig.CapDrop}} Mem={{.HostConfig.Memory}} Pids={{.HostConfig.PidsLimit}}'
```

**Check bind-mount ownership before recreating.** The services run as uid
10001 with `cap_drop: [ALL]`, so nothing bypasses file permission bits and
every mounted path must be owned by that uid:

```sh
sudo ls -ln data/ mail/ mbsyncrc secrets/ logs/    # expect 10001 throughout
```

Anything owned by another uid whose mode denies "other" needs
`sudo chown -R 10001:10001 <path>`. The entrypoint now checks each mounted path
at startup and refuses to start with the exact command, so these no longer
fail silently — but they used to, and the two that bit hardest are worth
knowing:

- **`data/archive.sqlite`** — a snapshot copied in at `0600` by your own user,
  or by root under the pre-non-root convention, failed the whole stack with a
  bare SQLite `unable to open database file`. The `MAILVEC_REQUIRE_SEEDED_DB`
  guard can't catch it: it uses `[ -s ]`, which stats rather than opens.
- **`mbsyncrc`** — bind-mounted to `/etc/mbsyncrc`. Unreadable meant IMAP sync
  stopped while every other service stayed green.

**Create the `./logs/<service>` bind sources yourself and chown them**, as in
the compose header. This is the reverse of the old advice: Docker creates a
missing bind source root-owned, which the container can no longer write, and
Serilog's failure there is silent — see the log-permissions note in
[logs.md](logs.md). The entrypoint catches the case, so a missed directory is a
refusal rather than a silently logless service.

## Moving to non-root

Stacks stood up before 2026-09-17 ran every service as container-root; the
image and compose now run mcp, indexer, embedder and mbsync as
`MAILVEC_UID:MAILVEC_GID` (default `10001:10001`, a fixed high number chosen to
collide with no real account on the host) and `parse` as `nobody`. The uid has
no passwd entry in the image and needs none (`HOME=/tmp` is baked in). The
one-time migration is a chown of everything the containers mount:

```sh
docker compose down
sudo chown -R 10001:10001 data logs mail mbsyncrc secrets/*
sudo ls -ln data/ mail/ mbsyncrc secrets/ logs/    # everything 10001; secrets and mbsyncrc still -rw-------
docker compose --profile tunnel up -d --build       # or pull, for a GHCR image
docker compose ps                                   # all running; none restarting
```

A path you missed is a **loud refusal at startup**, not a degraded service:
the entrypoint prints `mailvec: /data is not writable by uid 10001 … sudo chown
-R 10001:10001 ./data` and exits, and the mbsync sidecar does the same for
`./mail`, `mbsyncrc` and its secret. `restart: unless-stopped` will loop it
until the chown is done; `docker compose logs <service>` shows the line.

What changed with it, in case you have tooling around the old layout:

- `mbsyncrc` is mounted at `/etc/mbsyncrc` (the loop passes `mbsync -c`), no
  longer `/root/.mbsyncrc`.
- `./logs/<service>` and `./data` become `0700 10001:10001` (the services
  harden them on open, as before). Tailing from the host still needs `sudo`.
- `docker compose exec mcp mailvec …` runs as 10001; it could read and write
  everything it could before.
- Re-seeding the archive: chown to `10001:10001`, not `0:0`.

Validated 2026-09-17 on Docker Desktop (linux/arm64) against **named volumes**,
which have real ownership semantics unlike Docker Desktop bind mounts: a data /
logs / mail / secrets set populated by the old root-running image made every
non-root service refuse with the messages above; after the chown all four ran
under the full hardening posture (read-only rootfs, tmpfs `/tmp`, `cap_drop
ALL`) — scans, WAL writes, log files, `/health`, the mbsync loop and the CLI.
Not validated here: the host-side chown on the VM itself, which is the one
line above.

## Origin-side security posture — the knobs, and which are on by default

Everything below is enforced by the mcp container itself, so it holds
regardless of what the tunnel's ingress rules say. The full threat model is
[security.md](security.md); this table is the deployment view — what ships on,
what is recommended on, and where each is set.

| Control | Default | Set where | What it does / when to change |
| --- | --- | --- | --- |
| Non-root services (`MAILVEC_UID`, `MAILVEC_GID`) | **on** (10001) | `.env` | mcp, indexer, embedder, mbsync run unprivileged; `parse` as `nobody`. Every mount must be owned by the uid — see [Moving to non-root](#moving-to-non-root). Change only if 10001 is taken on the host. |
| Parse-network deny-list (`MAILVEC_PARSE_SUBNET` → `Mcp__DeniedNetworks__0`) | **on** (172.31.255.0/24) | `.env` (one value feeds both the network and mcp) | mcp refuses every request from the `parse` network, so the parse service — the process that eats attacker-chosen bytes — cannot call the mail tools back. Loopback never denied. Change only on a subnet collision, and never one of the two values without the other. Verify: the `curl` in [The parse service](#the-parse-service) prints 403. |
| `/health` loopback-only (`MCP_RESTRICT_HEALTH_TO_LOOPBACK`) | **on** | `.env` | The detailed body (archive path, counts, Ollama address) is served to loopback only; monitors use `/up`. Migrate monitors before relying on it. |
| Parser client limits (`Parser:MaxResponseBytes`, redirects off) | **on** (64 MB) | baked in; the byte ceiling is overridable per service via `Parser__MaxResponseBytes` | The parse service cannot redirect its callers into forwarding mail, nor exhaust them with an oversized response. No reason to change. |
| Origin validation of the Access assertion (`MCP_ACCESS_ENABLED` + team domain + audiences) | **off** | `.env` | Every caller must present a valid Cloudflare Access assertion; the origin no longer trusts anything that can reach `mcp:3333`. **Recommended on for every tunnel deployment** — it turns "one known network is refused" into "every caller proves who it is". Needs the three dashboard values; the container refuses to boot if the signing keys cannot be fetched. Setup: [remote-access-cloudflare.md → Origin validation](remote-access-cloudflare.md#origin-validation-of-the-access-assertion-mcpaccess). Not available without a tunnel. |
| Tool-surface trim (`Mcp__DisabledTools__*`) | **off** (staged, commented) | `compose.yml` | Drops the two on-demand native-parser tools. A documented accepted risk with invalidating conditions — read [security.md → What's accepted](security.md#whats-accepted) before deciding. |

The first four are what a stack with no tunnel gets. A tunnel deployment should
add the fifth; the sixth is a judgement call the security doc lays out.

## Rollout checklist

Each of these was a distinct risk when this stack was first stood up, and each
is worth confirming on a new deployment rather than assuming. Record the results
wherever you keep operational notes — deliberately not here, since a checklist
someone has ticked off is a claim about one machine.

1. **Both architectures build and run.** linux-arm64 natively, linux-x64 under
   emulation; fresh schema creates through `vec0.so` on both. Confirm the
   emulated build again on real hardware — an amd64 image that has only ever run
   under Rosetta has not been tested.
2. **Compose bring-up against your Ollama host.** `mcp` healthy, `/health` 200
   from inside the container, the three-way first-boot migration race resolving
   cleanly, workers logging to `docker logs`.
3. **The entrypoint guard.** Refuses a missing-DB start (exit 1), passes when
   seeded or explicitly disabled. The `mailvec` CLI shim works under both
   `docker run` and `docker exec`.
4. **First real mbsync run**, with the indexer's reconciliation scan completing
   behind it.
5. **OCR on Linux** — this proves the PDFium/SkiaSharp natives load at runtime,
   which their presence on disk does not.
6. **Eval parity** against a baseline captured before the move, so a .NET
   platform swap can't silently shift ranking.
7. **Tunnel go-live**, with `TUNNEL_TOKEN` + `MCP_PUBLIC_HOSTNAME` set and the
   sidecar started via `docker compose --profile tunnel up -d`.
8. **Endpoint posture.** `/up` is the endpoint external monitors poll; `/health`
   is loopback-only from 0.2.0 (`Mcp:RestrictHealthToLoopback`). See
   [security.md → `/up` and `/health`](security.md#up-and-health), and migrate
   any monitor still on `/health` **before** shipping the loopback
   restriction.
9. **The tool surface you actually want.** The `Mcp__DisabledTools__*` trim for
   `view_attachment` / `get_attachment_page_image` is staged-but-commented in
   `compose.yml` and left off by default — a documented accepted risk with
   explicit invalidating conditions, not an oversight. Read
   [security.md → What's accepted](security.md#whats-accepted) and decide for
   your own deployment before exposing the tunnel or publishing a host port.
10. **The parser boundary.** `docker compose exec mcp ls /app/mcp | grep -c MimeKit`
    prints 0 and `docker compose exec parse ls /app/parse/libpdfium.so` exists;
    `mailvec doctor` shows `Parser … answers /up`; and a real message indexed
    after bring-up (`mailvec status`) proves the round trip. See
    [The parse service](#the-parse-service).
11. **The parse network is one-way.** From a container on the `parse` network,
    `curl -s -o /dev/null -w '%{http_code}' -H 'Host: mcp' http://mcp:3333/up`
    prints **403**; from `mcp` itself (loopback) it prints 200 or 503. If it does
    not, `MAILVEC_PARSE_SUBNET` and the network's actual subnet have diverged —
    the server cannot detect that, only this probe can. See the posture table
    above.
12. **Every service is non-root.** `docker compose exec mcp id -u` prints 10001
    (or your `MAILVEC_UID`), `docker compose exec parse id -u` prints 65534, and
    `sudo ls -ln data/ logs/ mail/` shows the uid throughout. A refusal at
    startup naming a chown means a mount was missed — see
    [Moving to non-root](#moving-to-non-root).
13. **Origin validation, if you run a tunnel.** `MCP_ACCESS_ENABLED=true` with
    the team domain and both audiences set; `docker compose logs mcp` shows
    `Cloudflare Access assertion validation ENABLED` and the signing keys
    loaded; a request without an assertion from the default network gets 401
    while the loopback healthcheck stays green. Off means the origin trusts
    anything that reaches it, which the security doc accepts only while the
    tunnel is the sole ingress.
14. **Backups.** Cover the Docker VM with whatever snapshot schedule you run;
    see the backup bullet above for what that does and doesn't guarantee, and
    the one storage-layout invariant it rests on.

## Known gaps

1. **No GUI — the tray app was retired.** This gap used to read "the tray app
   has no remote story"; it was resolved by removing the tray and its
   `/tray/*` surface rather than by building auth for them. The container
   never had a consumer for either. If a GUI is ever wanted again, see
   [future-ideas.md](future-ideas.md) for what recovering it costs — the
   identity work dominates.
