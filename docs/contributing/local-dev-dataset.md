# Developing against a frozen mail corpus

A frozen Maildir and SQLite archive make retrieval evaluations repeatable. Keep ingest agents stopped so message counts, vectors, and eval labels stay stable. Use a separate database copy for experiments that reindex, re-embed, or change ranking inputs.

> **This repository's development Mac has a protected frozen corpus.** Its `~/Library/Application Support/Mailvec/.frozen-corpus` marker makes `ops/install.sh`, `ops/install-all.sh`, and `ops/redeploy.sh` refuse to start agents. Do not remove the marker or start mbsync there for routine development. Check `launchctl list | grep mailvec` and `~/Library/LaunchAgents/com.mailvec.*.plist` before work; both should be empty. If agents are present, use `ops/install.sh --uninstall` and report that the corpus may have moved. `ops/stop.sh` leaves plists that restart at login. See the frozen-corpus warning in [CLAUDE.md](../../CLAUDE.md).

## Prepare a frozen dataset on another machine

First take a consistent snapshot with `ops/export-db.sh` and keep a separate rollback copy. Uninstall the launchd agents, including mbsync, with `ops/install.sh --uninstall`; that preserves the database, Maildir, binaries, and logs. Retain the Maildir because attachment tools, OCR, and reindexing read the original `.eml` files. Record which database path and Maildir path belong together. A `mailvec status` check gives the actual corpus and schema state; do not rely on values written in a guide.

Use the small [test database walkthrough](../dev-walkthrough.md) for fast indexer iteration that does not need a full eval corpus. The full frozen archive is useful when labeled Message-IDs and realistic mail edge cases matter.

## Day-to-day workflow

- Run a service directly from the working tree when needed: `dotnet run --project src/Mailvec.Mcp` (or `Mailvec.Indexer` / `Mailvec.Embedder`). Do not install an agent to test service code on the protected machine.
- Use `dotnet run --project src/Mailvec.Cli -- ...` when the published `mailvec` shim may lag the working tree's schema. An older binary correctly refuses a newer database.
- Copy the archive before `switch-model`, reindexing, or chunking experiments. Set `Archive__DatabasePath` and `Ingest__MaildirRoot` **together** for the copy. A mismatched pair can make the indexer soft-delete messages whose source files it cannot find.
- Capture an eval baseline before retrieval-affecting changes, and compare only runs against the same corpus. See [baselines](../../baselines/README.md) and [embedding experiments](embedding-experiments.md).
- Run `mailvec status` against the resolved path before evaluation. A typo in `DatabasePath` can create a healthy-looking empty archive.

## Refresh a frozen corpus deliberately

Refresh the Maildir first, then run the indexer and embedder directly, and finally create a new baseline. Do this only when the change to the corpus is intentional; old eval scores cannot be compared directly with the new corpus.

```sh
mbsync -c ~/.mbsyncrc -a
dotnet run --project src/Mailvec.Indexer    # stop after the scan settles
dotnet run --project src/Mailvec.Embedder   # stop at full coverage
mailvec eval --json baselines/<date>-refresh.json
```

If importing a database snapshot from another host, sync the matching Maildir before running the indexer. Otherwise, archive rows whose `.eml` files are absent can be marked deleted. Confirm the new database path, Maildir path, counts, and embedding coverage with `mailvec status` before treating the refreshed corpus as a baseline.

For eval queries collected from another host's MCP logs, copy only the logs you need and run `mailvec eval-import` with `MAILVEC_LOG_DIR` pointing at that copy. A query about mail newer than the frozen corpus cannot be labeled until the corpus is refreshed.
