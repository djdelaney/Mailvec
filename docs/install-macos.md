# macOS install mechanics

How the launchd install is put together, and the traps in working against it.
The step-by-step *how to install* lives in the [README](../README.md) ("Install"
and "Backup & moving machines"); this file is the mechanics and the rationale
behind them — the things that bite while developing, not while installing.

> ⛔ **This does not apply to the development box.** The Mac these docs were
> written on runs a frozen corpus with no agents installed, and
> `ops/install.sh` / `ops/install-all.sh` / `ops/redeploy.sh` refuse to run
> there. Read the frozen-corpus block at the top of
> [`CLAUDE.md`](../CLAUDE.md) before running anything here. Everything below
> describes a machine that *is* a deployment.

The macOS launchd install and the MCPB bundle both still build and are
supported for anyone wanting a local single-machine setup. The author's own
deployment is the Docker stack — see [`deploy-docker.md`](deploy-docker.md).

## Ops scripts

```sh
ops/install-all.sh [--no-fetch]                  # single-command bootstrap for a new machine
ops/install.sh [--uninstall]                     # write/refresh the launchd plists, or tear them down
ops/redeploy.sh [indexer|embedder|mcp|cli ...]   # republish + kickstart the agents after a code change
ops/stop.sh                                      # bootout the agents without uninstalling
ops/export-db.sh [--out path] [--to host]        # consistent DB snapshot for backup / machine migration
ops/import-db.sh <snapshot.sqlite>               # install a snapshot on this machine (read its header first)
```

`ops/stop.sh` leaves the plists in place, so the agents re-bootstrap at login —
`ops/install.sh --uninstall` is the one that actually removes them.

## Published binaries are not the working tree

`dotnet run --project src/Mailvec.<svc>` runs the working-tree code under your
terminal — useful for one-off debugging, but **the launchd agents installed by
`ops/install.sh` are separate processes** running the published binaries under
`~/.local/share/mailvec/<svc>/`. After editing service code, run
`ops/redeploy.sh` to push the new binaries and restart the agents — otherwise
the live services keep running the old code while `dotnet build` looks like it
succeeded. Use `ops/install.sh` only when plist templates change or a config
knob needs updating.

## The `mailvec` CLI shim

`ops/install.sh` publishes the CLI to `~/.local/share/mailvec/cli/` (alongside
the three .NET services) and drops a shim at `~/.local/bin/mailvec` that execs
`dotnet ~/.local/share/mailvec/cli/Mailvec.Cli.dll`. The shim sets `DOTNET_ROOT`
+ `PATH` so it works under Claude Desktop's sanitised-PATH child processes too.

- **Why a shim, not a symlink to the .dll**: .NET requires `dotnet <dll>`, not
  direct invocation. The shim hides that detail.
- **Why a shim, not `/usr/local/bin/mailvec`**: writing to `/usr/local/bin`
  needs sudo or a Homebrew tap. `~/.local/bin/mailvec` is sudo-free.
- **Re-run `ops/install.sh` (or `ops/redeploy.sh cli`) after CLI source
  changes** — the shim is generated at install time and points at a published
  .dll, not the working-tree source. `dotnet build` alone won't update it.

`~/.local/bin` isn't on the default macOS `PATH`; the README covers adding it.

## Logging

- **Log rotation is in-process (Serilog), not external.** Each
  launchd-installed service wires `SerilogSetup.Configure(...)`. Output:
  `~/Library/Logs/Mailvec/mailvec-<service>-<YYYYMMDD>.log`, daily rolling,
  also rolls if a single day exceeds 10 MB, 14 most recent files retained.
  Rolling is atomic *within a single writer per service* — true because the
  launchd HTTP MCP / indexer / embedder are each one process.
- **MCP in stdio mode does NOT write to that file.** Claude Desktop spawns one
  `Mailvec.Mcp --stdio` child per session (main chat + one per Cowork session),
  all named `Mailvec.Mcp`, concurrent with the launchd HTTP MCP. If they shared
  the rolling file they'd race on size-cap rolling and retention prune:
  `shared: false` only enforces single-writer on Windows, but POSIX `O_APPEND`
  happily admits multiple writers and Serilog swallows the resulting
  `IOException`s via `SelfLog`. Stdio output goes to stderr only; the client
  captures it (Claude Desktop → `~/Library/Logs/Claude/mcp-server-mailvec.log`).
  Source: [`SerilogSetup.Configure`](../src/Mailvec.Core/Logging/SerilogSetup.cs)
  wraps the file sink in `if (!stdioMode)`.
- **`MAILVEC_LAUNCHD=1` suppresses Serilog's Console sink.** Set in the launchd
  plist `EnvironmentVariables`. Without it, every log line writes to both the
  rolling file and stdout/stderr, where launchd captures it into
  `StandardOutPath`/`StandardErrorPath` — doubling disk usage. With the env var
  set, the launchd-captured `<service>.launchd.log` only catches things that
  bypass `ILogger`: pre-Serilog startup output, unhandled native stderr, panics.
