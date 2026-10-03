# Docker deployment

Mailvec runs as a Compose stack with `mbsync`, indexer, embedder, parser, and MCP services. Ollama runs at a reachable URL outside the stack. Follow [Get started with Docker](getting-started-docker.md) for a new install; this page covers migration and operations. The MCP service publishes no host port. Remote access, if needed, uses [Cloudflare Tunnel and Access](remote-access-cloudflare.md).

Store `./data` and `./mail` on host-local disk. SQLite WAL needs local filesystem locking; do not put the archive on NFS or SMB. On Docker Desktop, query a running archive from inside a container rather than opening its bind-mounted SQLite file on the host, which can corrupt its WAL state.

## Images and updates

The default `docker compose up -d --build` builds local images. To use published GHCR images, set both `MAILVEC_IMAGE` and `MAILVEC_MBSYNC_IMAGE` in `.env` to matching pinned release references, then run:

```sh
docker compose pull
docker compose up -d
```

Use `v<version>` tags for durable pins; `sha-<gitsha>` tags are pruned. For a verified immutable pin, append each image's digest (`:vX.Y.Z@sha256:...`). Get it with `docker buildx imagetools inspect <image>:vX.Y.Z --format '{{json .Manifest.Digest}}'`. Do not use `--build` with GHCR references; it retags a local build under those names. Do not enable automatic image updates: startup can migrate the archive. Take a [backup](#backups) before updating. `docker compose restart` retains old environment and container settings; use `up -d` to apply changes.

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
MBSYNC_IDLE_FOLDERS=INBOX,Junk Mail,homelab
```

The sidecar then runs [goimapnotify](https://gitlab.com/shackra/goimapnotify), which holds one IMAP IDLE connection per folder. When one reports new mail, the next sync starts within about 5 seconds (the wake is checked in 5-second steps, and never sooner than 5 seconds after the previous sync), instead of waiting out the interval. That 5 seconds is Mailvec's share only; the server's own delivery time comes first and usually dominates. Observed 2026-10-03 against Fastmail: the IDLE event arrived 21 s after the message's `Date:` header, and the sync started 4 s after that. A wake 20 to 30 s after sending is the watcher working, not failing. Only new mail triggers this. Flag changes, moves and deletions in any folder still wait for the interval. The event goimapnotify itself raises for every folder when it starts is not a wake: a container start or watcher restart adds no extra sync.

- **Names.** Use folder names exactly as the IMAP server lists them; names may contain spaces. To see them, enable IDLE with any valid folder such as `INBOX`, then run `docker compose exec mbsync goimapnotify -conf /tmp/mbsync-idle.yaml -list`. The folder must also be included by `Patterns` in `mbsyncrc`, or the sync it triggers won't pull it.
- **Limits.** Up to 10 folders, each one IMAP connection on top of mbsync's own. Mail that fails SPF, DKIM or DMARC usually lands in Junk Mail, so watch it too if you test mail paths.
- **Connection.** The watcher's config is generated at startup from the first `IMAPAccount` block of `mbsyncrc` (Host, Port, User, Pass or PassCmd). It connects with implicit TLS and certificate verification only (`TLSType IMAPS`, which the example uses) and refuses other TLS modes. `AuthMechs XOAUTH2` (Gmail, Office 365) is carried over: the watcher then sends the `PassCmd` output as the OAuth2 token, as mbsync does, so the same token command serves both; a literal `Pass` with XOAUTH2 leaves IDLE off with a reason. It opens folders read-only (`EXAMINE`), so watching changes nothing on the server.
- **Apply.** The `mbsync` image must be from the release that introduced this setting or later; an older image has no goimapnotify and ignores the variable **silently**, with no log line at all. Then recreate the service so it reads the new environment: `docker compose up -d mbsync`. Its log should show `IDLE enabled for: …`, then one `Watching mailbox` line per folder. If neither appears, check the running image with `docker inspect` before suspecting the configuration.

This is a latency optimisation only, and every failure falls back to the interval. A rejected login, a folder name the server doesn't recognise, or an unreachable server stops the watcher for **all** folders. The sidecar restarts it after 30 seconds, doubling up to 15 minutes, so a revoked app password or a typo can't hammer the account. Check `docker compose logs mbsync` for `IDLE watcher exited` lines if mail seems slow. Neither `/health` nor `/up` reports on the watcher itself. Whether syncs are succeeding is still reported by `/up`'s `mail.syncStale`, and to Claude sessions by `search_emails`' `mailSync` field.

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
