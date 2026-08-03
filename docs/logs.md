# Logs

The three .NET services (indexer, embedder, MCP server) write rolling daily log files to:

```
~/Library/Logs/Mailvec/mailvec-<service>-<YYYYMMDD>.log
```

Daily rolling, 10 MB cap per file, 14 most recent files kept. Implementation is Serilog's File sink wired through [Mailvec.Core/Logging/SerilogSetup.cs](../src/Mailvec.Core/Logging/SerilogSetup.cs); rotation happens in-process so there's nothing to cron.

When you run a service via `dotnet run` in a terminal, log lines also stream to stdout for live visibility. Under launchd (production), the plists set `MAILVEC_LAUNCHD=1` to suppress that — only the rolling file gets written. To override either default during development:

```sh
export MAILVEC_LOG_DIR=/some/other/path   # change the log directory
export MAILVEC_LAUNCHD=1                  # silence stdout, even outside launchd
```

## Permissions, and why a failure here is silent

Log lines carry Maildir paths and attachment filenames, and — whenever
`Mcp:LogToolCalls` is on — raw search queries, sender addresses and subjects. So
`SerilogSetup` restricts the log **directory** to `0700` and each rolling file to
`0600` as it's opened (the hook fires on every roll, not just the first).

Both chmods are **best-effort and silently swallowed**: they run during logger
construction, when there is no logger to report a failure to. The 0700 directory
is the primary control, and the swallow exists because some bind mounts and
network filesystems don't honour POSIX modes at all.

The consequence worth knowing: if the process can't chmod the log directory, it
also probably can't *write* it, and you get no file logging with nothing
anywhere saying why.

## In containers

`MAILVEC_LOG_DIR=/logs` is baked into the image, and compose bind-mounts a
separate host directory per service (`./logs/<service>:/logs`) so the files
survive `docker compose up -d` recreating a container. Without the mount they
live in the container's writable layer and are destroyed by every recreate.

**Let Docker create those host directories.** It creates missing bind sources
root-owned, which is what the container runs as, so writes and the 0700 chmod
both succeed. A directory pre-created by your host user is one container-root
cannot write — `cap_drop: [ALL]` removed `DAC_OVERRIDE`, so root no longer
bypasses the permission bits — and per the section above, that failure is
silent. If they already exist wrong: `sudo chown 0:0 logs/*`.

Because the chmod lands on the host directory, expect `./logs/<service>` to
become `0700 root:root`; tailing from the host needs `sudo`.

Note the console sink is also live in containers (`MAILVEC_LAUNCHD` is
deliberately unset), so the same lines go to `docker logs`. That copy is
governed by the Docker daemon's logging driver, not by anything here — compose
sets no `logging:` block, so the json-file default applies and it is **not**
size-capped.

## Claude Desktop MCPB bundle

The bundled MCP binary runs in **stdio** mode, and in stdio mode it does **not** write the rolling `~/Library/Logs/Mailvec/mailvec-mcp-*.log` file — the file sink is disabled (`SerilogSetup.Configure(..., stdioMode: true)`) so multiple Claude Desktop-spawned children don't race on it. All of its output goes to **stderr**, which Claude Desktop captures at:

```
~/Library/Logs/Claude/mcp-server-mailvec.log
```

That's the file to tail when triaging a Claude Desktop / extension-install issue — the rolling `mailvec-mcp-*.log` only reflects the separate launchd HTTP MCP service (used by Claude Code), not the stdio bundle.

## mbsync

mbsync (the only non-.NET service) writes to small launchd-captured files in `~/Library/Logs/Mailvec/mailvec-mbsync.{out,err}.log`. These don't rotate — mbsync emits at most a few lines per 10-minute sync (`StartInterval` 600 in the plist), so size isn't a concern.
