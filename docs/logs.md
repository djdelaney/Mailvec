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

The bundled MCP binary writes to the same rolling file (it's the same binary). It additionally emits to stderr, which Claude Desktop's own log capture preserves at:

```
~/Library/Logs/Claude/mcp-server-mailvec.log
```

Handy when triaging extension-install issues, since that's the file Claude Desktop's UI will surface in error toasts.

## mbsync

mbsync (the only non-.NET service) writes to small launchd-captured files in `~/Library/Logs/Mailvec/mailvec-mbsync.{out,err}.log`. These don't rotate — mbsync emits at most a few lines per 5-minute sync, so size isn't a concern.
