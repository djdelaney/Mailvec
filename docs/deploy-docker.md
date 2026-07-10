# Docker deployment (Proxmox homelab)

Status as of 2026-07-06: image + compose stack built and smoke-tested locally
(steps 1–6 below the fold); not yet deployed to the homelab. This documents
the container strategy, the deployment strategy, and what's left.

Target: the full Mailvec pipeline runs in a compose stack on the existing
Docker VM on Proxmox, with **Ollama external** (the GPU-passthrough VM,
already serving today's Mac deployment) and the MCP server exposed through a
Cloudflare tunnel. Nothing depends on the Mac being online; the Mac becomes a
pure MCP client.

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
- **After parity holds**, stop the Mac pipeline (checklist item 7) — its
  archive keeps diverging from the VM's the moment you export, so treat the
  Mac copy as a frozen rollback, not a peer. The Mac's Claude Desktop stdio
  MCP keeps serving the Mac's local copy until you switch clients over
  (checklist item 6).
- **Ranking parity gate.** After the embedder settles, run
  `docker compose exec mcp mailvec eval` against the latest baseline in
  `baselines/`. Same model + same vectors means any drift implicates the
  .NET-on-Linux platform swap specifically.
- **Exposure**: cloudflared sidecar, token-based tunnel, ingress →
  `http://mcp:3333` (Streamable HTTP; `Mcp-Session-Id` passes through). The
  MCP container publishes no host port by default. The DNS-rebinding
  **HostGuard** (src/Mailvec.Mcp/HostGuard.cs, fronts every route) 403s any
  Host header that isn't loopback or allowlisted — tunnel traffic carries the
  public hostname, so `MCP_PUBLIC_HOSTNAME` in `.env` must be set when the
  tunnel goes live (compose wires it to `Mcp:AllowedHosts`, alongside `mcp`
  for in-network access). **Auth in front of the tunnel is tracked
  separately** (see docs/security.md and future-ideas "cross-vendor access")
  — `Mcp__BindAddress=0.0.0.0` inside the compose network is where the
  README's bind-to-127.0.0.1 boundary stops applying, so the tunnel must not
  go live before that work lands.
- **Health/monitoring**: compose healthcheck curls `/health` (30 s interval).
  Note `/health` returns 503 when Ollama is unreachable, so an Ollama VM
  outage shows as an *unhealthy mcp container* even though keyword search
  still works — informative, nothing restarts on it.
- **Backups move to the VM**: cron the checkpoint-then-copy *flow* — the
  `ops/export-db.sh` script itself is macOS-only (it pauses writers via
  launchctl). The container equivalent:
  `docker compose stop indexer embedder && docker compose exec mcp mailvec
  checkpoint && cp data/archive.sqlite <backup> && docker compose start
  indexer embedder` (mcp stays up — it's read-only against the DB, and the
  CLI rides inside its container). Only a pause-checkpoint-copy sequence is
  guaranteed-consistent; Proxmox/PBS snapshots are the crash-consistent
  outer layer. Note `ConnectionFactory`
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

## What's left

1. **Deploy on the VM — LAN phase (no tunnel needed)**: clone repo, follow
   the compose.yml header steps (`.env`, `mbsyncrc`, password secret, seed
   DB, `up -d --build`). cloudflared sits behind the `tunnel` compose
   profile, so plain `up -d` runs just the pipeline. For LAN clients,
   uncomment the mcp `ports:` mapping and set `MCP_LAN_HOSTNAME` to the VM's
   address (HostGuard 403s it otherwise), then point Claude Code at
   `http://<vm-ip>:3333/` over HTTP transport. Everything below except
   item 5 is testable in this phase. First `docker build` on real x86
   hardware is the true amd64 test (local amd64 ran under Rosetta
   emulation).
2. **First real mbsync run** (not live-tested locally — needs IMAP
   credentials): watch `docker compose logs mbsync` for the initial pull,
   then confirm the indexer's reconciliation scan completes.
3. **OCR spot-check on Linux**: render one scanned PDF end-to-end
   (`get_attachment_page_image` or an embedder OCR cycle) to prove the
   PDFium/SkiaSharp natives at runtime, not just their presence on disk.
4. **Eval parity run** against the latest baseline (see above).
5. **Cloudflared go-live**: blocked on the separate security work (tunnel
   Access policy / auth). When it lands: set `TUNNEL_TOKEN` +
   `MCP_PUBLIC_HOSTNAME` in `.env` (without the hostname, HostGuard 403s all
   tunnel traffic) and start with `docker compose --profile tunnel up -d`.
6. **Client switch-over**: Claude Code → tunnel URL (HTTP transport); Claude
   Desktop → remote connector instead of the MCPB stdio bundle. The tray app
   has no remote story yet (it polls `/tray/status`; future-ideas material).
7. **Decommission the Mac pipeline** once parity holds: `ops/stop.sh` (keeps
   launchd plists installed but booted out), retire the mbsync launchd job,
   keep the Mac's DB copy as a rollback for a while.
8. **Backup cron on the VM** (export-db flow) + PBS schedule.
