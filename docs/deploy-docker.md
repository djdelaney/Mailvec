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

- **One image, four binaries** ([Dockerfile](../Dockerfile)). Multi-stage
  `dotnet/sdk:10.0` → `dotnet/aspnet:10.0`, publishing Indexer / Embedder /
  Mcp / Cli to `/app/<svc>/`. Framework-dependent publish — the aspnet base
  supplies the runtime for all four. Each compose service selects its binary
  via `command:`; the image's default CMD is the MCP server. The CLI is on
  PATH as `mailvec`, so operator commands are
  `docker compose exec mcp mailvec status|doctor|eval|checkpoint ...`.
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
- **mbsync sidecar** (Dockerfile stage `mbsync`): Alpine + isync on a 600 s
  interval loop, replacing the `com.mailvec.mbsync` launchd job (same cadence,
  same `.mbsyncstate` flock rationale — see the plist comment). Config is a
  bind-mounted `mbsyncrc` ([ops/mbsyncrc.container.example](../ops/mbsyncrc.container.example));
  the Fastmail app password is a compose file-secret read via `PassCmd`.
  Pull-only sync is enforced structurally: the maildir is mounted read-only
  into every service except mbsync.
- **macOS-only code degrades, by design.** The `/tray/*` launchd inspector
  returns "unloaded" without `launchctl`. `mailvec doctor` detects the
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
  the macOS launchd services.
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
kept in lockstep with `manifest.json` and the tray by **`ops/release.sh`**
(the only sanctioned bump path; `ops/build-mcpb.sh --bump` delegates to it).
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
# The chown is REQUIRED, not tidiness: the services run cap_drop: [ALL], so
# container-root has no DAC_OVERRIDE and cannot read a 0600 file owned by the
# host user who scp'd it. Skipping this fails at startup with a bare SQLite
# "unable to open database file" that names no permission problem.
sudo chown 0:0 data/archive.sqlite

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
  `chown 0:0`** — the replacement file carries the copying user's ownership,
  and the first run recreates the sidecars itself.
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

**Check bind-mount ownership before recreating.** `cap_drop: [ALL]` removes
`DAC_OVERRIDE`, so container-root no longer bypasses file permission bits.
Anything under `./data`, `./mail` or `./logs` created *by the containers* is
root-owned and fine; anything copied in by a host user is not:

```sh
sudo ls -ln data/ mail/ mbsyncrc secrets/ logs/
```

Anything with a non-zero UID/GID whose mode denies "other" needs
`sudo chown 0:0 <path>`. Two bite hardest, and neither says "permission":

- **`data/archive.sqlite`** — a snapshot copied in at `0600` by your own user
  fails the whole stack with a bare SQLite `unable to open database file`. The
  `MAILVEC_REQUIRE_SEEDED_DB` guard can't catch it either: it uses `[ -s ]`,
  which stats rather than opens, so an unreadable-but-present file passes.
- **`mbsyncrc`** — bind-mounted to `/root/.mbsyncrc`. Unreadable means IMAP sync
  stops while every other service stays green.

Let Docker create the `./logs/<service>` bind sources rather than pre-creating
them: Docker makes them root-owned, which container-root can write and chmod to
0700. A directory you created is one the container cannot write, and Serilog's
failure there is silent — see the log-permissions note in
[logs.md](logs.md).

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
   is loopback-only from 0.2.0 (`Mcp:RestrictHealthToLoopback`); `/tray/*` is
   disabled at the origin (`Mcp:EnableTrayEndpoints=false`, baked into the
   image) *and* 404'd at the tunnel, because it returns mail content and has no
   container consumer. See
   [security.md → `/up`, `/health` and `/tray/*`](security.md#up-health-and-tray),
   and migrate any monitor still on `/health` **before** shipping the loopback
   restriction.
9. **The tool surface you actually want.** The `Mcp__DisabledTools__*` trim for
   `view_attachment` / `get_attachment_page_image` is staged-but-commented in
   `compose.yml` and left off by default — a documented accepted risk with
   explicit invalidating conditions, not an oversight. Read
   [security.md → What's accepted](security.md#whats-accepted) and decide for
   your own deployment before exposing the tunnel or publishing a host port.
10. **Backups.** Cover the Docker VM with whatever snapshot schedule you run;
    see the backup bullet above for what that does and doesn't guarantee, and
    the one storage-layout invariant it rests on.

## Known gaps

1. **The tray app has no remote story — open, deliberately parked.** It polls
   `/tray/*`, which the container disables outright
   (`Mcp:EnableTrayEndpoints=false`) *and* the tunnel 404s — the surface returns
   mail content with no per-request auth, so it stays off any internet-fronted
   deployment. Three ways out, none chosen: keep it as a local-only tool against
   a local install; give `/tray/*` an authenticated remote path (which means
   designing auth for the origin — today it has none, by design — before
   re-enabling the flag); or retire it. Nothing else depends on it, but it
   shouldn't drift as unowned code indefinitely.
