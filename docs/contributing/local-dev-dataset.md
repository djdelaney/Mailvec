# Local development after the Docker migration

The pipeline now runs in the Proxmox compose stack, so the Mac stops being a
deployment and becomes the dev machine. The strategy: **develop against a
frozen-in-time copy of the real archive**, with no launchd agents running.
This page is the one-time teardown, the day-to-day workflow, and the refresh
procedure.

> **Status: done.** The teardown ran on **2026-07-16** — the four launchd
> agents are booted out and their plists removed, mbsync included. The Mac is a
> dev machine now, and its archive is frozen with full embedding coverage and
> every eval label still resolving. The teardown section is kept as a record and
> as the recipe if a Mac pipeline is ever rebuilt.
>
> Deliberately not recorded here: which corpus is currently active, where it
> lives, or what schema version it is at. Those are properties of one machine at
> one moment; an earlier revision of this page asserted them and was wrong about
> every one within a few weeks. Read them from the machine
> (`mailvec status`), never from this file.
>
> **The freeze is now enforced, not just documented.** `ops/install.sh`,
> `ops/install-all.sh` and `ops/redeploy.sh` refuse to run while
> `~/Library/Application Support/Mailvec/.frozen-corpus` exists — see
> [`ops/frozen-corpus-guard.sh`](../../ops/frozen-corpus-guard.sh) for why a
> written warning turned out not to be enough (it was ignored twice, once by an
> agent that never read it). `--uninstall` is deliberately still allowed; it is
> the remedy, not the hazard. To un-freeze deliberately, delete the marker.

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

   The last writer closing also checkpoints and removes the `-wal`/`-shm`
   sidecars, so with the agents gone the corpus settles into a single
   self-contained `archive.sqlite`.

   A later CLI read (`mailvec status`, `doctor`, …) re-creates the sidecars and
   **leaves them there** — but at **0 bytes**, because a reader has nothing to
   checkpoint. That's the state to expect, and it's why copying the frozen
   `archive.sqlite` on its own is safe here despite the usual "never copy a live
   DB without its WAL" rule: an empty WAL means the main file is already
   complete. Check `ls -l archive.sqlite-wal` before trusting a copy — the rule
   is suspended by the WAL being empty, not by the pipeline being stopped.

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

Keep the frozen Maildir (whatever `Ingest:MaildirRoot` points at):
`view_attachment`, page images, OCR experiments, and reindex-from-source all
read `.eml` bytes from it, and the disk is already paid for.

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
- **Ollama:** keep pointing at the same instance the container uses, so dev
  query embeddings match production bit-for-bit and eval numbers stay
  comparable across machines. A second Ollama host is a second embedding space
  in practice even on an identical model tag.
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


## Keeping more than one local corpus straight

A dev machine usually ends up with more than one: the frozen full corpus above,
and smaller purpose-built ones (an OCR-heavy subset for vision work, the
truncated set for indexer iteration). Which one is active is just two config
keys, and that is exactly what makes the following traps easy to walk into.
These are properties of the tooling, so they hold on any machine — unlike the
paths, versions and corpus sizes an earlier revision of this section recorded for
one Mac, which were wrong within weeks and are not worth restating.

- **Switch `Archive:DatabasePath` and `Ingest:MaildirRoot` as a PAIR, always.**
  A mixed pair points the indexer at a Maildir that doesn't contain the
  database's messages, and the next scan soft-deletes every message whose
  `.eml` it can't find — which is all of them. Nothing warns you; the corpus
  just empties. The two keys live together in the shared
  `appsettings.Local.json` for this reason, and a one-off override should use
  env vars for both (`Archive__DatabasePath`, `Ingest__MaildirRoot`) so they
  can't be half-applied.
- **A baseline family belongs to the corpus it was measured on.** Diffing an
  eval run against a baseline captured on a *different* corpus produces a
  confident, meaningless delta — the query set resolves different Message-IDs.
  Check which corpus is active before `mailvec eval --baseline`, and keep
  per-corpus baselines in their own subdirectory (as `baselines/subset-ocr/`
  does) rather than mixing them into the top-level family.
- **A missing `DatabasePath` is created, not refused.**
  `SchemaMigrator.EnsureUpToDate` silently creates a fresh empty schema at
  whatever path it resolves, so a typo or a stale path in a switched pair reads
  as a healthy but empty archive. `mailvec status` on the path you *think* is
  active is the cheap check.
- **The published CLI shim can lag the working tree's schema version.**
  `~/.local/bin/mailvec` points at the last-published binaries; run anything
  from the working tree that migrates a database forward and the shim's own
  downgrade guard then refuses that database — correctly, since an older binary
  lacks the newer invariants. Use `dotnet run --project src/Mailvec.Cli` rather
  than weakening the guard. Note `ops/redeploy.sh cli` would refresh the shim
  but is blocked by the frozen-corpus guard; that is deliberate, not a bug.
- **Hosted-provider experiments need a key the repo never holds.** Hosted
  embedding or OCR profiles read their credential from the owner-only file named
  by `Auth:ApiKeyFile` (see `docs/proposals/embedding-providers.md`). Nothing
  else references it, and a rotated or absent key surfaces as a classified
  `AuthOrConfig` failure rather than a crash.
- **Ranking-neutrality gate.** For a change that should not move retrieval at
  all, run `mailvec eval --json <scratch>.json` against the active corpus and
  diff per-query results against that corpus's baseline with the timing fields
  stripped: bit-identical, or the change is not neutral. `baselines/README.md`
  records the binary-provenance rules that make such a diff trustworthy.
