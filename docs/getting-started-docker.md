# Get started with Docker

This path runs the mail pipeline on an always-on Linux Docker host. Ollama runs on a reachable host outside the stack. The MCP server has **no published host port**; to use it from Claude or another remote client, finish with the [Cloudflare Access setup](remote-access-cloudflare.md).

Use host-local storage for `./data` and `./mail` because SQLite WAL needs local filesystem locking. You need Docker Compose, IMAP credentials (an app-specific password for Fastmail, Gmail, or iCloud), and an Ollama server with `mxbai-embed-large` pulled. Pull `qwen2.5vl:7b` too if you want the default local OCR. The [deployment runbook](deploy-docker.md) covers resource sizing, security decisions, image pinning, migration, and verification in detail.

## 1. Prepare the compose directory

From a checkout of this repository on the Docker host, run this block in one shell. The restrictive umask keeps credential files owner-only.

```sh
umask 077
cp .env.example .env
cp ops/mbsyncrc.container.example mbsyncrc
install -d -m 700 secrets
printf '%s' '<IMAP app password>' > secrets/fastmail_password
: > secrets/embedding_api_key
chmod 600 .env mbsyncrc secrets/fastmail_password secrets/embedding_api_key
```

Replace the password placeholder. Set `OLLAMA_BASE_URL` in `.env` to the Ollama host's LAN URL, and edit `mbsyncrc` for your IMAP provider and account. The example's Maildir path, `/mail/Fastmail`, matches `compose.yml`; if you change it, change `Ingest__MaildirRoot` there too. Verify the permissions with `ls -l .env mbsyncrc secrets/*` (each file should be `-rw-------`). The [compose file header](../compose.yml) explains each setup step and why it matters.

## 2. Choose a database start

For a **new archive**, set `MAILVEC_REQUIRE_SEEDED_DB=0` in `.env`. The default `1` deliberately refuses to start without an existing database, so a missing mount cannot silently create an empty archive.

To **move an existing macOS archive**, leave the guard at `1` and follow [Migrating the archive from a macOS install](deploy-docker.md#migrating-the-archive-from-a-macos-install) before starting the stack. Use the snapshot script; copying a live SQLite file is unsafe.

## 3. Give the containers access and start

```sh
mkdir -p data logs/mcp logs/indexer logs/embedder mail
sudo chown -R 10001:10001 data logs mail mbsyncrc secrets/*
docker compose up -d --build
docker compose exec mcp mailvec doctor
docker compose exec mcp mailvec status
docker compose exec mcp mailvec search "a phrase from a known email"
```

The services run as uid 10001 by default. If a service refuses to start, `docker compose logs <service>` gives the missing ownership or configuration step. Replace the sample search phrase with something in your mail after the first sync. The first IMAP pull and embedding pass can take hours or days; message counts and embedding coverage in `status` should increase. Verify a real OCR render if OCR is enabled; the [rollout checklist](deploy-docker.md#rollout-checklist) covers the remaining checks.

## 4. Connect a client

To reach this stack outside Docker, configure a Cloudflare Tunnel and Access application, then set `TUNNEL_TOKEN` and `MCP_PUBLIC_HOSTNAME` in `.env`. Follow the [remote access runbook](remote-access-cloudflare.md) for the OAuth connector, origin validation, and its identity allowlist. Start the tunnel sidecar with `docker compose --profile tunnel up -d`. Keep the MCP service's host port unpublished; see the [security model](security.md) before changing exposure.

For future updates and backups, use the [Docker deployment runbook](deploy-docker.md). Do not use macOS `ops/install*.sh` on this host.
