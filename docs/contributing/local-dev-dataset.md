# Local development after the Docker migration

The pipeline now runs in the Proxmox compose stack, so the Mac stops being a
deployment and becomes the dev machine. The strategy: **develop against a
frozen-in-time copy of the real archive**, with no launchd agents running.
This page is the one-time teardown, the day-to-day workflow, and the refresh
procedure.

> **Status: done.** The teardown ran on **2026-07-16** — the four launchd
> agents are booted out and their plists removed, mbsync included, and the tray
> is quit. The Mac is a dev machine now; the archive below is frozen at
> **~80k messages, 100% embedded**, with every eval label still resolving. The
> teardown section is kept as a record and as the recipe if the Mac pipeline is
> ever rebuilt.

## Why frozen-real (not truncated, not artificial)

- **The eval set decides it.** The ~70 labeled queries in
  `~/Library/Application Support/Mailvec/eval/queries.json` reference real
  Message-IDs. An artificial corpus orphans all of them; a truncated one
  orphans most. Without the eval, ranking work (chunk sizes, RRF, model
  experiments) is blind — and that's exactly the work that needs a local
  corpus at all.
- **Freezing makes the eval fully deterministic.** The query set is 44
  sealed-window queries plus 26 deliberately unfiltered ones; on a growing
  corpus the unfiltered queries drift as new mail competes (see the q024
  incident in `baselines/2026-07-10-q024-sealed.json`'s commit message). On
  a frozen corpus, all 70 are stable code-change signals.
- **Real mail's weirdness is unreproducible.** Charset mojibake, `font-size:0`
  layout hacks, inline `cid:` images, scanned PDFs, under-reported attachment
  sizes — half the indexer's edge-case handling exists because real mail did
  these things. Synthetic corpora exercise none of it.

The other options keep their existing niches: the **truncated set**
(`ops/dev-fetch-imap.py` → `~/mailvec-test`, see `docs/dev-walkthrough.md`)
for fast indexer-loop iteration where scanning the full archive per change is
annoying, and **artificial data** in the unit tests, which cover the
pure-code loop with no corpus at all.

## One-time teardown (done 2026-07-16 — kept as the record + the rebuild recipe)

1. **Take the pristine rollback snapshot first** — this copy is never opened
   again; the dev corpus is a different file (step 2 makes it so):

   ```sh
   ops/export-db.sh --out ~/mailvec-rollback-$(date +%F).sqlite
   chmod 400 ~/mailvec-rollback-*.sqlite
   ```

   **Skipped in the actual teardown.** By then the VM was production and
   covered by the homelab's snapshot schedule with offsite shipping, so a
   pristine Mac copy duplicated a rollback story that already existed. Keep
   this step if you ever tear down a Mac pipeline *before* an equivalent
   backed-up copy exists elsewhere — that's the condition it was written for.

2. **Uninstall the agents — including mbsync:**

   ```sh
   ops/install.sh --uninstall
   ```

   This removes the four launchd plists while preserving the published
   binaries, the `~/.local/bin/mailvec` shim, the logs, and the database —
   which now *is* the dev corpus, frozen in place, with every eval label
   still resolving.

   The last writer closing also checkpoints and **removes the `-wal`/`-shm`
   sidecars**, so the frozen corpus settles as a single self-contained
   `archive.sqlite` — copy it around freely without the usual
   "never copy a live DB without its WAL" footgun. (`mailvec status` and any
   other CLI read re-creates a `-wal` for the duration; it's cleaned up again
   when the process exits.)

   **Why not `ops/stop.sh`:** its default deliberately leaves mbsync
   running, and `launchctl bootout` alone doesn't survive the next login —
   the plists still sit in `~/Library/LaunchAgents`, so a reboot quietly
   brings the whole pipeline back. `stop.sh` is the *pause* tool;
   `--uninstall` is the durable post-migration state. (`ops/install.sh`
   recreates everything if you ever want the Mac pipeline back; the rollback
   snapshot is the data half of that story.)

   **Why disable mbsync too:** the freeze only works if the Maildir and the
   DB stay coherent. Both mbsync instances are pull-only, so a still-running
   Mac mbsync isn't *dangerous* — but the moment you run an ad-hoc indexer
   against a Maildir that kept syncing, the dev DB silently unfreezes and
   eval drift is back. The VM sidecar is the consumer of record now. Once
   confident, you can also revoke the Mac's Fastmail app password — the
   container reads its own compose secret, not the Mac keychain.

3. **Quit the tray app** (or live with red service tiles — the launchd HTTP
   MCP it polls is gone). Its CLI buttons still work via the shim.

Keep the frozen `~/Mail`: `view_attachment`, page images, OCR experiments,
and reindex-from-source all read `.eml` bytes from it, and the disk is
already paid for.

## Day-to-day workflow

- **Services run from the working tree, on demand:**
  `dotnet run --project src/Mailvec.Mcp` (or Indexer/Embedder). They read
  the shared config and hit the frozen DB. Nothing restarts on reboot;
  nothing mutates the corpus unless you run a writer.
- **CLI:** the `mailvec` shim keeps working against the last-published
  binaries; refresh it after CLI changes with `ops/redeploy.sh cli`
  (publish-only — the CLI has no agent, so no kickstart is attempted).
- **Destructive experiments** (`switch-model`, `reindex`, chunking changes):
  copy the DB first and point the experiment at the copy with env-var
  overrides, exactly per `docs/contributing/embedding-experiments.md`. The
  frozen dev DB plays the role the live DB used to play there.
- **Ollama:** keep pointing at the GPU-VM instance — the same one the
  container uses — so dev query embeddings match production bit-for-bit and
  eval numbers stay comparable across machines.
- **Eval:** unchanged. `mailvec eval --baseline baselines/<latest>.json`
  against the frozen corpus is now deterministic end to end.

## Refreshing the corpus (when the freeze gets too stale)

The clean path keeps Maildir + DB coherent by re-running the pipeline once,
manually, then re-freezing:

```sh
mbsync -c ~/.mbsyncrc -a                     # one-shot pull (no schedule)
dotnet run --project src/Mailvec.Indexer     # Ctrl-C once the scan settles
dotnet run --project src/Mailvec.Embedder    # Ctrl-C at 100% coverage
mailvec eval --json baselines/<date>-refresh.json   # re-baseline: unfiltered queries will shift
```

**Do not** refresh by copying a VM snapshot over the dev DB without also
running the one-shot mbsync: the refreshed DB would reference messages whose
`.eml` files aren't in the frozen Maildir, and the next ad-hoc indexer scan
would soft-delete all of them as missing. If you do want the VM's copy
(e.g. to skip local embedding of the delta), `ops/import-db.sh <snapshot>`
works on the Mac even with the agents uninstalled (its pause/resume steps
no-op) — but run the one-shot mbsync **first** so the Maildir is at least as
new as the snapshot.

## Curating new eval queries post-migration

Real Claude usage now logs on the VM, and `mailvec eval-import` reads local
files. Pull the rolling logs down and point it at them:

```sh
scp you@docker-vm:/path/to/mailvec/logs/mailvec-mcp-\*.log /tmp/vm-logs/
MAILVEC_LOG_DIR=/tmp/vm-logs mailvec eval-import
```

Queries about mail newer than the freeze can't be labeled until the next
corpus refresh — their Message-IDs don't exist locally yet. Refresh first,
then label.
