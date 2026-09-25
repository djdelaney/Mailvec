# macOS install mechanics

How the launchd install is put together, and the traps in working against it.
For a first install, follow [Get started on macOS](getting-started-macos.md).
This page covers maintenance, backups, and removal.

> **Maintainers:** on a machine with `.frozen-corpus`, the install scripts refuse to run. Follow the [frozen dataset workflow](contributing/local-dev-dataset.md) instead.

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

- Re-publish the CLI after source changes; `dotnet build` does not replace the installed binary.

`~/.local/bin` isn't on the default macOS `PATH`; the [getting-started guide](getting-started-macos.md#4-check-the-first-result) covers adding it.

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

## Backup and moving machines

The expensive part of the archive is the derived data: OCR text and embeddings that took hours to compute. Use the snapshot scripts rather than copying a live SQLite file or its `-wal` sidecar:

```sh
ops/export-db.sh                         # snapshot → ~/mailvec-archive-snapshot.sqlite
ops/export-db.sh --to you@newmac         # snapshot and transfer it
ops/import-db.sh /path/snapshot.sqlite   # destination, after install + mail sync
```

Export pauses the writers and checkpoints the WAL before copying. Import removes stale `-wal` and `-shm` sidecars before installing the snapshot. Read the scripts' header comments for ordering: install Mailvec and sync mail on the destination before import. The Maildir is separate and can be pulled again from IMAP.

## Stop or uninstall

```sh
ops/stop.sh                  # stop agents until the next login
ops/install.sh --uninstall   # boot them out and remove their launchd plists
```

`--uninstall` preserves published binaries, the archive, and logs. To remove those too, after uninstalling:

```sh
rm -rf ~/.local/share/mailvec
rm -f ~/.local/bin/mailvec ~/.local/bin/mailvec-mcp-stdio
rm -rf "$HOME/Library/Application Support/Mailvec"
rm -rf ~/Library/Logs/Mailvec
```

Remove your Maildir and `~/.mbsyncrc` separately if you no longer want the local mail copy. Remove the Claude Desktop extension in Settings → Extensions.
