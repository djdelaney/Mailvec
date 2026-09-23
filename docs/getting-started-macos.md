# Get started on macOS

This path installs Mailvec on one Apple Silicon Mac: `mbsync` pulls mail, launchd runs the services, and clients connect locally. It requires macOS 14+, the .NET 10 SDK, and a reachable Ollama instance. Intel Macs are not supported by the install scripts or MCPB bundle.

> **Maintainers:** the Mac used for this repository's frozen eval corpus must not run `ops/install.sh`, `ops/install-all.sh`, or `ops/redeploy.sh`. Follow [the local development workflow](contributing/local-dev-dataset.md) there. The steps below are for a machine that will actually serve mail.

## 1. Install prerequisites

```sh
brew install --cask dotnet-sdk   # .NET 10; the tested runtime location is /usr/local/share/dotnet
brew install isync jq            # isync provides mbsync; jq is used by the health example
brew install --cask ollama-app   # use the app, not the Homebrew ollama formula
open -a Ollama
ollama pull mxbai-embed-large
ollama pull qwen2.5vl:7b         # OCR of scanned PDFs and images; about 6 GB
```

Keep Ollama running and enable **Open at Login** if this machine should serve mail after reboot. The `ollama-app` cask is the tested build; some Homebrew formula builds have lacked the `llama-server` binary needed by the default embedding model. See [upgrading dependencies](../ops/UPGRADING.md) for the Ollama version floor.

## 2. Configure IMAP and make the first sync

The included mbsync example uses Fastmail. For another provider, change its `Host`, `User`, and `PassCmd` values; [IMAP setup](imap-setup.md) has provider pointers and explains folder filtering.

```sh
mkdir -p ~/Mail/Fastmail
security add-generic-password -a you@fastmail.com -s mbsync -w
cp ops/mbsyncrc.example ~/.mbsyncrc
chmod 600 ~/.mbsyncrc
$EDITOR ~/.mbsyncrc                   # set User and PassCmd for your account
mbsync -aV                            # first sync can take hours
```

The Keychain `-a` account and `-s` service must match the `PassCmd` in `~/.mbsyncrc`. Fastmail, Gmail, and iCloud require an app-specific password. For a large archive, run the first sync in `tmux` or `screen` so a closed terminal does not stop it.

## 3. Install the services and connect a client

```sh
./ops/install-all.sh
```

The installer fetches sqlite-vec, publishes the services and CLI, asks for the Maildir/database/Ollama settings, and installs launchd agents. Next, use the [local client guide](clients/README.md) to connect Claude Desktop, Claude Code, or another MCP client.

## 4. Check the first result

The installer places `mailvec` in `~/.local/bin`. If your shell cannot find it, add that directory to `PATH` (for example, `echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.zshrc`, then open a new shell).

```sh
mailvec doctor
mailvec status
mailvec search "a phrase from a known email"
curl -s http://127.0.0.1:3333/health | jq .
```

`doctor` checks the database, Maildir, sqlite-vec, Ollama, launchd, and HTTP service. It exits 1 when a check fails; `--no-net` skips network probes and `--json` produces a report. Replace the sample search phrase with something in your mail, then ask your MCP client for the same message. Keyword search is ready before embedding completes; `mailvec status` shows coverage increasing. OCR runs after the initial embedding pass. Budget roughly 4–5 GB of SQLite archive for about 75,000 messages, in addition to the Maildir.

For backup, machine migration, uninstall, and launchd mechanics, see [macOS operations](install-macos.md). For logs and troubleshooting, see [logs](logs.md).
