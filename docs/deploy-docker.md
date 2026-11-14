# Docker deployment

Mailvec runs as a Compose stack with `mbsync`, indexer, embedder, parser, and MCP services, on a Linux Docker host or under Docker on a Mac. Ollama runs at a reachable URL outside the stack. Follow [Get started with Docker](getting-started-docker.md) for a new install; this page covers migration and operations. On a Linux host the MCP service publishes no host port, and remote access, if needed, uses [Cloudflare Tunnel and Access](remote-access-cloudflare.md). A Mac adds the `compose.macos.yml` overlay; see [Docker on a Mac](#docker-on-a-mac) for what it changes.

Use Docker Engine 25.0.5 or later. The `indexer` and `parse` services sit on `internal` networks so that they have no route out, and before that release the engine's embedded DNS server still forwarded their lookups to external nameservers when the host's resolver was on a loopback address (systemd-resolved's `127.0.0.53`), which is a data path out of a container that is meant to have none (CVE-2024-29018; also fixed in 23.0.11 and in every release from 26.0.0). Check with `docker version --format '{{.Server.Version}}'`.

Store `./data` and `./mail` on host-local disk. SQLite WAL needs local filesystem locking; do not put the archive on NFS or SMB. On Docker Desktop, query a running archive from inside a container rather than opening its bind-mounted SQLite file on the host, which can corrupt its WAL state.

## Images and updates

The default `docker compose up -d --build` builds local images. To use published GHCR images, set both `MAILVEC_IMAGE` and `MAILVEC_MBSYNC_IMAGE` in `.env` to matching pinned release references, then run:

```sh
docker compose pull
docker compose up -d
```

Each tag is a multi-platform image covering linux/amd64 and linux/arm64, and Docker pulls the one matching the host, so the same reference works on an x86 Linux host and on an Apple Silicon Mac. Releases published before multi-platform support are linux/amd64 only; check a tag with `docker buildx imagetools inspect <image>:vX.Y.Z` and look for `linux/arm64`. Use `v<version>` tags for durable pins; `sha-<gitsha>` tags are pruned. Images for releases before v0.12.1 are no longer pullable: an earlier cleanup job removed their manifests. For a verified immutable pin, append each image's digest (`:vX.Y.Z@sha256:...`). Get it with `docker buildx imagetools inspect <image>:vX.Y.Z --format '{{json .Manifest.Digest}}'`; that is the digest of the multi-platform index, so the pin stays valid on either architecture. Do not use `--build` with GHCR references; it retags a local build under those names. Do not enable automatic image updates: startup can migrate the archive. Take a [backup](#backups) before updating. `docker compose restart` retains old environment and container settings; use `up -d` to apply changes.

Check the result with `docker compose ps`, `docker compose exec mcp mailvec doctor`, and `docker compose exec mcp mailvec status`. Release creation is governed by [CLAUDE.md](../CLAUDE.md#releases).

## Migrating the archive from a macOS install

Export a consistent snapshot with `ops/export-db.sh` on the source Mac. Copy the snapshot to the Docker host before starting the stack:

```sh
# On the source Mac:
ops/export-db.sh --to user@docker-host:

# On the Docker host, in the Compose directory:
mkdir -p data
mv ~/mailvec-archive-snapshot.sqlite data/archive.sqlite
chmod 600 data/archive.sqlite
sudo chown -R 10001:10001 data
docker compose up -d --build
docker compose exec mcp mailvec status
docker compose exec mcp mailvec doctor
```

Use your configured `MAILVEC_UID` and `MAILVEC_GID` instead of `10001` if changed. `MAILVEC_REQUIRE_SEEDED_DB=1` (the default) refuses a missing or empty seed. The snapshot's embedding model and dimensions must match the new configuration, or the embedder refuses it. Do not copy a live SQLite main file or its `-wal` and `-shm` files.

Let mbsync finish its first pull and the indexer reconcile message locations before running `purge-deleted`; attachment views can temporarily miss source files during reconciliation. Compare message counts and search results with the source. If you have an eval baseline for this corpus, run `docker compose exec mcp mailvec eval`. Retire the old pipeline when clients have moved to the Docker endpoint.

To replace a previously used container archive, stop the stack first, replace `archive.sqlite`, remove its old `-wal` and `-shm` sidecars, and restore ownership before starting. Those sidecars belong to the old archive and must not be applied to the replacement.

## Backups

An atomic storage snapshot of `./data` including the SQLite main file and WAL is crash-consistent. For an application-consistent standalone file, pause the writers, checkpoint, and copy only after a successful checkpoint:

```sh
docker compose stop indexer embedder
if docker compose exec -T mcp mailvec checkpoint; then
  sudo cp -p data/archive.sqlite <backup-path>
else
  echo 'Checkpoint failed; no copy made' >&2
fi
docker compose start indexer embedder
```

Always restart the writers, including when checkpoint fails. A reader can prevent WAL truncation; retry later rather than copying only the main file. Keep backups off-host and test restoration on a separate archive.

## Hosted embedding provider

Ollama is the default. A hosted embedding profile sends mail text and queries to its configured service; review [Hosted embedding](security.md#hosted-embedding-embeddingactiveprofile) before enabling it. Place the key in `secrets/embedding_api_key`, set the `MAILVEC_EMBEDDING_*` values in `.env`, including an asserted space ID, then switch the archive's vector space with a **new** container that reads the new environment:

```sh
docker compose run --rm --no-deps mcp mailvec switch-model --yes
docker compose up -d
docker compose exec mcp mailvec status
```

The embedder then re-embeds the archive. Use the same sequence to switch back to Ollama after clearing the hosted profile. `docker compose exec` would enter an existing container with its previous environment and can select the wrong profile.

The hosted client connects to the endpoint directly and ignores `HTTP(S)_PROXY`, so the `mcp` and `embedder` containers need direct outbound HTTPS to it. A profile holding a key cannot be routed through a proxy (`Proxy=environment` is refused with bearer auth; see [Security](security.md#hosted-embedding-embeddingactiveprofile)).

## Permissions and parser service

The services run as `MAILVEC_UID:MAILVEC_GID` (default `10001:10001`) with Linux capabilities dropped. Own `data`, `logs`, `mail`, `mbsyncrc`, and `secrets/*` with that uid before starting or after restoring files:

```sh
sudo chown -R 10001:10001 data logs mail mbsyncrc secrets/*
docker compose ps
docker compose logs <service>
```

The entrypoint names an unreadable or unwritable mount when it refuses to start. `./logs/<service>` must exist and be writable; see [Logs](logs.md). Change both the uid and gid in the command if configured differently.

All mail-content parsing runs in `parse`. It has no archive or Maildir mount, secret, or external network route. The other services send it `.eml` bytes and receive parsed data. A timeout or request limit can make it exit and restart normally. A sustained outage pauses new-mail indexing, OCR, and attachment viewing; existing search remains available. Check `docker compose ps parse`, `docker compose exec mcp mailvec doctor`, and `docker compose exec mcp curl -fsS http://parse:3400/up`.

The MCP service shares the internal `parse` network to call the parser and rejects calls back from that network with `Mcp:DeniedNetworks`. If `MAILVEC_PARSE_SUBNET` changes, verify the corresponding MCP setting still matches. From a container on the parse network, a request to `http://mcp:3333/up` must return 403. See [Security model](security.md#container-hardening).

## New-mail push (IMAP IDLE)

By default the `mbsync` service syncs every `MBSYNC_INTERVAL_SECONDS` (60). To have new mail land within seconds, which helps when a Claude session is waiting on a test message, list folders to watch in `.env`:

```sh
MBSYNC_IDLE_FOLDERS=INBOX,Alerts
```

The sidecar then runs [goimapnotify](https://gitlab.com/shackra/goimapnotify), which holds one IMAP IDLE connection per folder. When one reports new mail, the next sync starts within about 5 seconds (the wake is checked in 5-second steps, and never sooner than 5 seconds after the previous sync), instead of waiting out the interval. That 5 seconds is Mailvec's share only; the server's own delivery time comes first and usually dominates. Observed 2026-10-03 against Fastmail: the IDLE event arrived 21 s after the message's `Date:` header, and the sync started 4 s after that. A wake 20 to 30 s after sending is the watcher working, not failing. Only new mail triggers this. Flag changes, moves and deletions in any folder still wait for the interval. The event goimapnotify itself raises for every folder when it starts is not a wake: a container start or watcher restart adds no extra sync.

- **Names.** Use folder names exactly as the IMAP server lists them; names may contain spaces. To see them, enable IDLE with any valid folder such as `INBOX`, then run `docker compose exec mbsync goimapnotify -conf /tmp/mbsync-idle.yaml -list`. The folder must also be included by `Patterns` in `mbsyncrc`, or the sync it triggers won't pull it.
- **Limits.** Up to 10 folders, each one IMAP connection on top of mbsync's own. The example `mbsyncrc` leaves the spam folder out of the sync, so watching it does nothing unless you changed `Patterns` to include it.
- **Connection.** The watcher's config is generated at startup from the first `IMAPAccount` block of `mbsyncrc` (Host, Port, User, Pass or PassCmd). It connects with implicit TLS and certificate verification only (`TLSType IMAPS`, which the example uses) and refuses other TLS modes. `AuthMechs XOAUTH2` (Gmail, Office 365) is carried over: the watcher then sends the `PassCmd` output as the OAuth2 token, as mbsync does, so the same token command serves both; a literal `Pass` with XOAUTH2 leaves IDLE off with a reason. It opens folders read-only (`EXAMINE`), so watching changes nothing on the server.
- **Apply.** The `mbsync` image must be from the release that introduced this setting or later; an older image has no goimapnotify and ignores the variable **silently**, with no log line at all. Then recreate the service so it reads the new environment: `docker compose up -d mbsync`. Its log should show `IDLE enabled for: …`, then one `Watching mailbox` line per folder. If neither appears, check the running image with `docker inspect` before suspecting the configuration.

This is a latency optimisation only, and every failure falls back to the interval. A rejected login, a folder name the server doesn't recognise, or an unreachable server stops the watcher for **all** folders. The sidecar restarts it after 30 seconds, doubling up to 15 minutes, so a revoked app password or a typo can't hammer the account. Check `docker compose logs mbsync` for `IDLE watcher exited` lines if mail seems slow. Neither `/health` nor `/up` reports on the watcher itself. Whether syncs are succeeding is still reported by `/up`'s `mail.syncStale`, and to Claude sessions by `search_emails`' `mailSync` field.

## Optional: mark-read

The `mark_read` MCP tool lets an agent set or clear the read flag on mail, for example a scheduled agent that triages monitoring alerts and marks the routine ones read. It is off unless you add it, and a deployment without it stays read-only. It is for a Linux host: the [Mac overlay](#docker-on-a-mac) refuses to start with it. Security implications: [Security model](security.md#optional-mark-read).

To enable it:

1. In `.env`, add the overlay and name the folders it may change. Use the folder names `list_folders` reports, comma-separated; `*` means every folder and is never implied.

   ```sh
   COMPOSE_FILE=compose.yml:compose.mark-read.yml
   MBSYNC_MARK_READ_FOLDERS=Alerts
   ```

2. In `./mbsyncrc`, change `Sync Pull` to `Sync Pull PushFlags`, so flags set locally reach the server. New mail and deletions still flow down only.
3. Create the queue directory and its `inbox/`, owned by the service uid and closed to other local accounts (the request and `done/` records name folders and messages): `mkdir -p -m 700 ./mark-read/inbox && sudo chown -R ${MAILVEC_UID:-10001}:${MAILVEC_GID:-10001} ./mark-read`. Create `inbox/` yourself: Docker creates a missing mount source owned by root.
4. `docker compose up -d`.

Verify it:

- `docker compose logs mbsync` shows `mark-read: on for folders: …`. If a step was missed it shows `mark-read: OFF:` with the reason; mail keeps syncing, and `mark_read` answers `unavailable` without queueing anything.
- `cat ./mark-read/.applier`: its timestamp moves every minute while the sidecar runs.
- `mark_read` appears in the client's tool list. After a call, the sidecar logs `applied <file>` and the outcome of each line is in `./mark-read/done/` (kept 30 days).

**Upgrading from v0.12.1 or earlier.** The queue layout changed: requests now go into `./mark-read/inbox`, and mcp mounts `./mark-read` read-only. Before starting the new images, run the `mkdir` and `chown` from step 3 and take the current `compose.mark-read.yml`. Until both are done `mark_read` answers `unavailable` and says which part is missing (the sidecar log names a missing `inbox`; mcp names a queue directory it can still write to). Mail sync and search are unaffected. Request files left directly in `./mark-read` are ignored and can be deleted.

A message is refused (`forbidden_folder`) unless **every** folder it is in is allowed, because marking one copy read marks the whole email read on the server. Requests wake the sync loop, so a change usually reaches the server within about 5 seconds.

To disable it, remove both `.env` lines, put `Sync Pull` back, and run `docker compose up -d`. Requests still queued are never applied.

## Docker on a Mac

`compose.macos.yml` (enabled with `COMPOSE_FILE=compose.yml:compose.macos.yml` in `.env`) keeps every service, image, network and hardening setting from `compose.yml`, and changes three things:

- **Storage.** `/data`, `/mail` and `/logs` are the named volumes `mailvec_data`, `mailvec_mail` and `mailvec_logs` instead of `./data`, `./mail` and `./logs`. They live on the Docker VM's disk, where SQLite gets real POSIX locking; a bind mount shared from macOS does not promise that, and can present Mac files as root-owned inside the container. The one-shot `volume-init` service runs before the others on every `up`, gives the volumes to `MAILVEC_UID:MAILVEC_GID`, and creates the Maildir root so the indexer's file watcher has something to watch from its first start. If `up` fails saying `volume-init` didn't complete successfully, the reason (an unset uid, the mark-read component, a file it could not hand over) is in `docker compose logs volume-init`. `docker compose down` keeps the volumes; `down -v` **deletes the archive and the Maildir**.
- **Ollama** defaults to `http://host.docker.internal:11434`, the native Ollama app on the same Mac.
- **MCP** is published on `127.0.0.1:3333` for local clients. Nothing is published on the LAN.

The overlay needs Docker Engine 26.0 and Compose 2.26 or later, above the 25.0.5 floor for a Linux host, because the per-service log mounts are volume subpaths. Check with `docker version --format '{{.Server.Version}}'` and `docker compose version`.

The optional [mark-read](#optional-mark-read) component is not supported with the overlay yet. `compose.mark-read.yml` bind-mounts `./mark-read` from the Mac, which goes through the file sharing the overlay moves everything else off, and its mount boundary has only been tested on a Linux host. The overlay refuses the combination: with `compose.mark-read.yml` in `COMPOSE_FILE` or `MBSYNC_MARK_READ_FOLDERS` set, `volume-init` exits with an error and no service starts.

`mbsyncrc` and `secrets/*` are still bind mounts. With `MAILVEC_UID`/`MAILVEC_GID` set to your Mac account they are readable as-is. If a service refuses to start saying one of them is not readable by its uid, the Docker version is presenting shared files under a different owner; check with `docker compose run --rm --no-deps --entrypoint ls mbsync -ln /etc/mbsyncrc /run/secrets`.

**Backups.** The archive is not a file in Finder. Copy it out through a container after a checkpoint, as in [Backups](#backups):

```sh
docker compose stop indexer embedder
if docker compose exec -T mcp mailvec checkpoint; then
  docker compose cp mcp:/data/archive.sqlite <backup-path>
else
  echo 'Checkpoint failed; no copy made' >&2
fi
docker compose start indexer embedder
```

Docker Desktop keeps volumes inside its VM disk image, so a Time Machine backup of the Mac is not a usable archive backup; keep the copies this produces.

**Logs.** There is no `./logs` directory on a Mac: each service's log files are in the `mailvec_logs` volume, mounted at `/logs` in that service. `docker compose logs <service>` shows the same lines; to read the files, go through the container, for example `docker compose exec indexer ls /logs` and `docker compose exec indexer tail -n 100 /logs/<file>`. See [Logs](logs.md#in-containers).

**Restore.** To put a backup back, stop the stack and copy the file into the data volume through the `volume-init` container; the next `up` gives it to the service uid:

```sh
docker compose down
docker compose run --rm --no-deps --cap-add DAC_OVERRIDE \
  -v <backup-path>:/seed/archive.sqlite:ro volume-init \
  'rm -f /data/archive.sqlite /data/archive.sqlite-wal /data/archive.sqlite-shm && cp /seed/archive.sqlite /data/archive.sqlite'
docker compose up -d
docker compose exec mcp mailvec status
```

`<backup-path>` must be an absolute path. The old `-wal` and `-shm` files belong to the archive being replaced and are removed with it.

## Resource limits

`compose.yml` sets memory and PID limits for each service. Size them for your corpus and parser workload; do not infer a suitable limit from process RSS alone because SQLite's vector page cache is also charged to the container. Check `docker stats` and the service's cgroup memory peak after indexing and search. If the limit causes cache churn or OOM restarts, increase it and recreate with `docker compose up -d`. For search measurement, see [Search performance](contributing/search-performance.md).

## Rollout checklist

1. Confirm `./data` and `./mail` are host-local and all mounts are owned by the service uid. For a new archive set `MAILVEC_REQUIRE_SEEDED_DB=0`; for a migrated one leave the guard on.
2. Start the stack and run `docker compose exec mcp mailvec doctor` and `mailvec status`. Confirm the first IMAP sync, indexing, and embedding progress.
3. If OCR is enabled, test a real PDF or image render to verify native libraries load.
4. Confirm `parse` is healthy and that calls from the parse network to MCP get 403.
5. If `MBSYNC_IDLE_FOLDERS` is set, `docker compose logs mbsync` shows `IDLE enabled for: …` and one `Watching mailbox` line per folder, and a test message to a watched folder is keyword-searchable within seconds (`mode=keyword` or `hybrid`; semantic results follow the embedder's next pass). Compare with `mailSync.lastSyncAt` in a `search_emails` response.
6. Test `/health` on MCP loopback and `/up` through the configured remote access path. Verify that `/health` and MCP tools are unavailable to the monitoring token.
7. Confirm backups and any corpus-specific eval baseline before changing images or embedding profiles.

Record observed deployment state in your operator notes, not in this repository. The checked-in [Compose file](../compose.yml) is the source of truth for defaults and mounts; `docker compose config` shows the resolved settings for a particular host.
