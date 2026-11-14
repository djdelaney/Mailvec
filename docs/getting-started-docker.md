# Get started with Docker

Docker is how Mailvec is deployed, on either of two hosts:

- **An always-on Linux Docker host.** Ollama runs on a reachable host outside the stack. The MCP server has **no published host port**; to use it from Claude or another remote client, finish with the [Cloudflare Access setup](remote-access-cloudflare.md).
- **An Apple Silicon Mac**, with Docker Desktop or OrbStack and Ollama as the native Mac app. Follow the steps below with the changes in [On a Mac](#on-a-mac).

Either way, every mail parser runs in the separate `parse` container, with no access to the archive, your credentials or the network. The older native macOS install ran them inside the services themselves and is [deprecated](getting-started-macos.md) for that reason.

Use host-local storage for `./data` and `./mail` because SQLite WAL needs local filesystem locking. You need Docker Compose, IMAP credentials (an app-specific password for Fastmail, Gmail, or iCloud), and an Ollama server with `mxbai-embed-large` pulled. Pull `qwen2.5vl:7b` too if you want the default local OCR. The [deployment runbook](deploy-docker.md) covers resource sizing, security decisions, image pinning, migration, and verification in detail.

## 1. Prepare the compose directory

From a checkout of this repository on the Docker host, run this block in one shell. The restrictive umask keeps credential files owner-only.

```sh
umask 077
cp .env.example .env
cp ops/mbsyncrc.container.example mbsyncrc
install -d -m 700 secrets
: > secrets/embedding_api_key
```

Then store the IMAP app password. Run this line on its own, in the same shell: it waits for you to paste the password and press Enter, shows nothing while you do, and writes it without a trailing newline.

```sh
stty -echo; IFS= read -r pw; stty echo; printf '%s' "$pw" > secrets/fastmail_password; unset pw
```

No step here writes a placeholder password, on purpose. Until `secrets/fastmail_password` exists, `docker compose up` refuses to create the stack (`bind source path does not exist`), so a block pasted whole cannot start a sync with made-up credentials.

Set `OLLAMA_BASE_URL` in `.env` to the Ollama host's LAN URL, and edit `mbsyncrc` for your IMAP provider and account. The example's Maildir path, `/mail/Fastmail`, matches `compose.yml`; if you change it, change `Ingest__MaildirRoot` there too. Verify the permissions with `ls -l .env mbsyncrc secrets/*` (each file should be `-rw-------`; `chmod 600` any that is not).

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

The services run as uid 10001 by default. If a service refuses to start, `docker compose logs <service>` gives the missing ownership or configuration step. Replace the sample search phrase with something in your mail after the first sync. The first IMAP pull and embedding pass can take hours or days; message counts and embedding coverage in `status` should increase. Verify a real OCR render if OCR is enabled; the [rollout checklist](deploy-docker.md#rollout-checklist) covers the remaining checks. New mail arrives on a 60-second sync timer; to have it land within seconds, see the optional [new-mail push](deploy-docker.md#new-mail-push-imap-idle). The deployment is read-only; to let agents mark mail read, add the optional [mark-read](deploy-docker.md#optional-mark-read) component.

## 4. Connect a client

To reach this stack outside Docker, configure a Cloudflare Tunnel and Access application, then set `TUNNEL_TOKEN` and `MCP_PUBLIC_HOSTNAME` in `.env`. Follow the [remote access runbook](remote-access-cloudflare.md) for the OAuth connector, origin validation, and its identity allowlist. Start the tunnel sidecar with `docker compose --profile tunnel up -d`. Keep the MCP service's host port unpublished; see the [security model](security.md) before changing exposure.

For future updates and backups, use the [Docker deployment runbook](deploy-docker.md). Do not use macOS `ops/install*.sh` on this host.

## On a Mac

Docker on a Mac runs the same Linux images inside a lightweight VM, so the parser isolation above holds unchanged, with the VM as one more boundary around it. [`compose.macos.yml`](../compose.macos.yml) adjusts the three things a Mac does differently: storage, Ollama and client access. The images are published for linux/arm64 and linux/amd64, so Docker picks the one matching the Mac; Apple Silicon is the tested target.

**Prerequisites.** Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) or [OrbStack](https://orbstack.dev/), and give its VM at least 8 GB of memory (Docker Desktop: Settings → Resources). Install Ollama as the native app (`brew install --cask ollama-app`), pull `mxbai-embed-large` (and `qwen2.5vl:7b` for local OCR), and enable **Open at Login**. Ollama stays outside Docker so it can use the Mac's GPU; containers reach it at `host.docker.internal`.

**Changes to the steps above:**

1. **Step 1.** Do everything as written, then set these in `.env` (each key once; replace the empty `MAILVEC_UID=` / `MAILVEC_GID=` lines already there):

   ```sh
   COMPOSE_FILE=compose.yml:compose.macos.yml
   MAILVEC_UID=501   # your `id -u`
   MAILVEC_GID=20    # your `id -g`
   ```

   Leave `OLLAMA_BASE_URL` empty: the overlay defaults it to `http://host.docker.internal:11434`. The services then run as your Mac account, which owns `mbsyncrc` and `secrets/*`, so no `sudo chown` is needed.
2. **Step 3.** Skip the `mkdir` and `chown` line. The archive, Maildir and logs live in Docker volumes (`mailvec_data`, `mailvec_mail`, `mailvec_logs`) on the VM's own disk, because SQLite needs real file locking, and a one-shot `volume-init` service gives them to `MAILVEC_UID` before each start. Run `docker compose up -d --build`; the build is native arm64. To pull published images instead, see [Images and updates](deploy-docker.md#images-and-updates).
3. **Step 4.** No tunnel needed. The overlay publishes MCP on `127.0.0.1:3333` only:
   - **Claude Code:** `claude mcp add --transport http mailvec http://127.0.0.1:3333`
   - **Claude Desktop:** run the server over stdio inside the running container, with no port involved. Add this to `~/Library/Application Support/Claude/claude_desktop_config.json` and restart Claude Desktop:

     ```json
     {
       "mcpServers": {
         "mailvec": {
           "command": "/usr/local/bin/docker",
           "args": ["exec", "-i", "mailvec-mcp-1", "dotnet", "/app/mcp/Mailvec.Mcp.dll", "--stdio"]
         }
       }
     }
     ```

     Use the absolute path to `docker` (`which docker`), because Claude Desktop starts servers without your shell's `PATH`. The stack must be running.

**Check it:** `docker compose ps` shows every service running and `volume-init` exited 0; `docker compose exec mcp mailvec doctor` reports the parser as `remote parse service … answers /up` and Ollama reachable; `curl -s http://127.0.0.1:3333/up` answers. Any process on this Mac, under any account, can call the published port with no authentication, which is the posture the native install had; see [Security](security.md#docker-on-a-mac). Backups, migration from the native install, and the volume commands are in [Docker on a Mac](deploy-docker.md#docker-on-a-mac).
