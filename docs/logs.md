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

## Claude Desktop MCPB bundle

The bundled MCP binary runs in **stdio** mode, and in stdio mode it does **not** write the rolling `~/Library/Logs/Mailvec/mailvec-mcp-*.log` file — the file sink is disabled (`SerilogSetup.Configure(..., stdioMode: true)`) so multiple Claude Desktop-spawned children don't race on it. All of its output goes to **stderr**, which Claude Desktop captures at:

```
~/Library/Logs/Claude/mcp-server-mailvec.log
```

That's the file to tail when triaging a Claude Desktop / extension-install issue — the rolling `mailvec-mcp-*.log` only reflects the separate launchd HTTP MCP service (used by Claude Code), not the stdio bundle.

## mbsync

mbsync (the only non-.NET service) writes to small launchd-captured files in `~/Library/Logs/Mailvec/mailvec-mbsync.{out,err}.log`. These don't rotate — mbsync emits at most a few lines per 10-minute sync (`StartInterval` 600 in the plist), so size isn't a concern.
