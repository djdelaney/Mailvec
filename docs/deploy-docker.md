# Docker deployment (Proxmox homelab)

**Status: live.** The full Mailvec pipeline runs as a compose stack on the
Docker VM on Proxmox, with **Ollama external** (the GPU-passthrough VM) and the
MCP server exposed through a Cloudflare tunnel behind Access Managed OAuth
([remote-access-cloudflare.md](remote-access-cloudflare.md)). The archive was
seeded from a Mac snapshot; mbsync, OCR-on-Linux, and eval parity are all
verified, and every Claude client now talks to the tunnel rather than the Mac.
Nothing depends on the Mac being online.

This documents the container strategy, the deployment strategy, and the
remaining work (see [What's left](#whats-left) — the rollout is done; backups
and the Mac decommission are not).

```
fastmail ◄─IMAP── mbsync ──► ./mail ──► indexer ─┐
                                                  ▼
cloudflared ──► mcp:3333 ◄────── ./data ◄── embedder ──► ollama VM (GPU, LAN)
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
  `linux-x64`, arm64 → `linux-arm64`), so `--platform linux/amd64` builds the
  Proxmox image from an Apple Silicon dev machine. `ops/fetch-sqlite-vec.sh`
  takes the RID as an argument and runs *inside* the build — the image never
  depends on host-fetched natives (`.dockerignore` excludes `runtimes/` for
  the same reason). The fetched `vec0.so` is copied to `./vec0.so` next to
  each binary so one arch-agnostic `Archive__SqliteVecExtensionPath` works
  for every service on either arch.
- **Native deps are NuGet-supplied on Linux.** PDFtoImage brings PDFium +
  SkiaSharp via `SkiaSharp.NativeAssets.Linux.NoDependencies` (no fontconfig
  needed — see the comment in Directory.Packages.props). Verified present in
  the published output; a real OCR render on the VM is still on the checklist.
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
  returns "unloaded" without `launchctl`; `mailvec doctor` skips launchd
  checks off-macOS. No code paths block Linux startup.

## Deployment strategy

- **Where**: the existing Docker VM on Proxmox, as one compose project
  ([compose.yml](../compose.yml) — setup steps are in its header comment).
  Bind mounts `./data` (SQLite) and `./mail` (Maildir) must be **VM-local
  disk**: SQLite WAL needs real POSIX locking; never NFS/SMB. Multi-container
  WAL sharing on one local bind mount is the same multi-process pattern as
  the Mac's launchd services.
- **Ollama**: external over LAN. The same instance already serves the Mac, so
  bind address, version floor, and pulled models (embedding + vision) are
  proven — the compose `.env` reuses the same `Ollama:BaseUrl`. GPU-backed
  OCR means `Embedder:OcrEnabled` stays on from day one.
- **Seeding: snapshot, not rebuild.** One final `ops/export-db.sh` on the Mac
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
  homelab's production pin and for anything you may want to roll back to.
  A `v*` tag is the same image bytes as its underlying `sha-` — one
  durable, human-meaningful name for the same digest (which also protects
  that build's `sha-` tag from pruning: tags on one digest share a package
  version).

**The tag value is not free-form.** The repo-wide `<Version>` in
`Directory.Build.props` stamps all four binaries and `serverInfo.version`,
kept in lockstep with `manifest.json` and the tray by
`ops/build-mcpb.sh --bump` (the only sanctioned bump path — see
`ops/mcpb-release.md`). The `v*` tag must equal that version at the tagged
commit, or the image's label and what its binaries report from
`mailvec status` / the MCP handshake disagree forever.

**Cutting a release** (dev machine, not the deploy host):

```sh
# 1. If the current <Version> has already been released, bump first:
ops/build-mcpb.sh --bump      # commit the bump; must be green on main
# 2. Tag the green bump commit with the MATCHING version and push:
git tag -a v0.1.29 -m "…" && git push origin v0.1.29
```

The tag push publishes `ghcr.io/<owner>/mailvec:v0.1.29` +
`…/mailvec-mbsync:v0.1.29` (plus the commit's `sha-` tag). It does **not**
move `:latest` (green-main / manual-dispatch only) — and note the `v*`
trigger is **not CI-gated**, unlike the green-main path, so the release
rule is: only tag commits that already passed CI on main.

**Deploying it:** pin both vars in `.env` to `:v0.1.29`, then
`docker compose pull && docker compose up -d` (backup first — the
SchemaMigrator-on-start rule above), and verify the loop closes:
`docker compose exec mcp mailvec status` must print the same version as
the image tag.

## Migrating the archive from the Mac

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
  footgun `ops/import-db.sh` handles on macOS; here it's manual.
- **After parity holds**, stop the Mac pipeline ([What's left](#whats-left)
  #1) — its archive keeps diverging from the VM's the moment you export, so
  treat the Mac copy as a frozen rollback, not a peer. (Clients have already
  switched over, so the Mac's stdio MCP is no longer serving anything.)
- **Ranking parity gate.** After the embedder settles, run
  `docker compose exec mcp mailvec eval` against the latest baseline in
  `baselines/`. Same model + same vectors means any drift implicates the
  .NET-on-Linux platform swap specifically.
- **Exposure**: cloudflared sidecar (compose `tunnel` profile), token-based
  tunnel, ingress → `http://mcp:3333` (Streamable HTTP; `Mcp-Session-Id`
  passes through), fronted by a Cloudflare Access self-hosted app using
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
- **Backups are the VM's**, not Mailvec's: the Docker VM is covered by the
  homelab's existing snapshot schedule with offsite shipping. That's a
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

## Verified so far

- linux-arm64 (native) and linux-x64 (under emulation) images build; fresh
  schema v8 creates through `vec0.so` on both; `vec0.so` confirmed ELF x86-64
  in the amd64 image.
- Compose bring-up of mcp/indexer/embedder against the real Ollama VM: mcp
  healthy, `/health` 200, three-way first-boot migration race fine, workers
  log cleanly to `docker logs`.
- Entrypoint guard refuses missing-DB start (exit 1) and passes when seeded
  or disabled. `mailvec` CLI shim works via `docker run`/`exec`.
- macOS side unaffected: `dotnet build` clean, vec0-touching tests pass with
  the `runtimes/**` glob.

## Done

The rollout itself is complete. Kept as a record of what was verified, since
each item was a distinct risk:

1. ✅ **Deployed on the VM.** Repo cloned, compose.yml header steps followed
   (`.env`, `mbsyncrc`, password secret, seeded DB, `up -d`). The real x86
   build passed — the pre-deploy amd64 test had only ever run under Rosetta.
2. ✅ **First real mbsync run**, with the indexer's reconciliation scan
   completing behind it.
3. ✅ **OCR on Linux**, proving the PDFium/SkiaSharp natives at runtime rather
   than just on disk.
4. ✅ **Eval parity** against the baseline — no drift from the .NET-on-Linux
   platform swap.
5. ✅ **Cloudflared go-live**, with `TUNNEL_TOKEN` + `MCP_PUBLIC_HOSTNAME` set
   and the sidecar started via `docker compose --profile tunnel up -d`. The
   auth front is a Cloudflare Access self-hosted app with Managed OAuth — **not**
   the MCP Server Portal the plan had assumed
   ([remote-access-cloudflare.md](remote-access-cloudflare.md) records why).
   The `/health` + `/tray/*` 404s were verified from outside.
   **Deviation:** the `Mcp__DisabledTools__*` tool-surface trim was **not**
   applied — `view_attachment` and `get_attachment_page_image` remain exposed.
   That's now a documented accepted risk with explicit invalidating
   conditions, not an oversight; read
   [security.md → What's accepted](security.md#whats-accepted) before changing
   the Access policy or publishing a host port.
6. ✅ **Client switch-over.** Every Claude surface (Code, Desktop, iOS,
   claude.ai) uses the remote connector. The Claude Desktop MCPB/stdio bundle
   is retired as a transport.
7. ✅ **Mac pipeline decommissioned** (2026-07-16), via
   `ops/install.sh --uninstall` — all four agents booted out and their plists
   removed, so nothing re-bootstraps at login. Binaries, logs, the
   `~/.local/bin/mailvec` shim, `~/Mail`, and the archive were all preserved;
   the Mac is now a **development machine** running against that archive as a
   frozen corpus ([local-dev-dataset.md](contributing/local-dev-dataset.md)).
   The rollback snapshot from that doc's step 1 was **deliberately skipped** —
   the VM is the production copy and carries the homelab's offsite backups, so
   a pristine Mac copy would duplicate a rollback story that already exists.

## What's left

1. **The tray app has no remote story — open, deliberately parked.** It polls
   `/tray/status`, which is in-network only and 404'd at the tunnel, so the
   tray has no path to the live deployment. Three ways out, none chosen yet:
   keep it as a local-dev-only tool against the frozen corpus; give `/tray/*`
   an authenticated remote path (which means designing auth for the origin —
   today it has none, by design); or retire it. Not urgent — nothing else
   depends on it — but it shouldn't drift as unowned code indefinitely.

Backups are **not** on this list: the Docker VM is covered by the homelab's
existing snapshot schedule with offsite shipping. See the backup bullet above
for what that does and doesn't guarantee, and the one storage-layout invariant
it rests on.
