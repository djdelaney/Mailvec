# AGENTS.md

Entry point for coding agents. **[`CLAUDE.md`](CLAUDE.md) is the full guide** —
architecture, build conventions, and the silent-corruption invariants that make
this codebase easy to break quietly. Read it before changing anything.

The block below is duplicated verbatim from CLAUDE.md because it is the one
thing you must not miss, and a pointer is not good enough for it: this repo has
twice had its eval corpus destroyed by an agent that never opened the other
file. CI fails if the two copies drift.

<!-- BEGIN frozen-corpus -->
> ⛔ **This machine is a development box with a FROZEN CORPUS — do not install or start the agents.**
>
> No launchd agents, no mbsync, nothing on `127.0.0.1:3333` (decommissioned 2026-07-16). The `archive.sqlite` here is frozen for eval / ranking work: the `baselines/` numbers were measured against exactly this corpus.
>
> **Do not run `ops/install.sh` / `ops/install-all.sh` / `ops/redeploy.sh` here — not even "just to test that it works."** Installing the agents restarts mbsync and the indexer, the corpus starts moving, and every eval comparison after that drifts silently against a moving target. **The ingest IS the damage** — there is no version of "I'll put it back afterwards" that restores the measurement. This has now happened twice: on 2026-08-04 an agent reinstalled all four agents while testing (~136 messages ingested before it was caught), and again later by an agent that never read these warnings at all.
>
> Those three scripts now **refuse to run** while `~/Library/Application Support/Mailvec/.frozen-corpus` exists — see [`ops/frozen-corpus-guard.sh`](ops/frozen-corpus-guard.sh). The guard is the enforcement; this text is only the explanation. `ops/install.sh --uninstall` is deliberately **not** blocked: it is the remedy if agents are ever found running.
>
> **To exercise service code here, run it directly — no agents needed:**
> `dotnet run --project src/Mailvec.<svc>`
>
> **Verify before you touch anything:** `launchctl list | grep mailvec` should print nothing, and `~/Library/LaunchAgents/com.mailvec.*.plist` should not exist. If they are back, tear them down with `ops/install.sh --uninstall` (**not** `ops/stop.sh` — that leaves the plists, so they re-bootstrap at login) and say so, because the corpus has moved.
>
> Workflow and refresh procedure: [`docs/contributing/local-dev-dataset.md`](docs/contributing/local-dev-dataset.md).
<!-- END frozen-corpus -->

## Orientation

Four .NET services sharing a SQLite archive and a Maildir, plus an MCP server
exposing the archive to Claude. `Mailvec.slnx` (not `.sln`) is the solution.

```sh
./ops/fetch-sqlite-vec.sh    # one-time: native sqlite-vec loadable
dotnet build                 # TreatWarningsAsErrors=true — a warning fails the build
dotnet test                  # full suite, no Ollama required
```

<!-- BEGIN release-approval -->
> 🚦 **Never cut a release unless you were asked to, in that turn.**
>
> Approval is "ship it", "cut a release", "tag v0.4.1", or an explicit yes to a bump you proposed. It is **not** finishing a feature, a green CI run, a passing eval, or an instruction to commit or push. Committing is not releasing.
>
> A release means any of: bumping `<Version>`, pushing a `v*` tag, or running `ops/release.sh --ship`. The tag push is the consequential one — it publishes durable GHCR images (`mailvec`, `mailvec-mbsync`) that the homelab pins by tag, so it is the only routine action here that reaches a running deployment. Tags and published images are not cleanly retractable; a wrong one burns the version number.
>
> **`ops/release.sh` is the only sanctioned channel.** It is what keeps `<Version>` and `manifest.json` in lockstep, and `publish-images.yml` refuses a `v*` tag that disagrees with `<Version>`. Never hand-edit a version, never tag without a matching bump commit, and never reach for `--ship` unprompted — it pushes and tags on its own.
>
> Propose the release **and** the part to bump, then wait. `--patch` for anything; `--minor` for an MCP tool-surface change or a schema migration, where the version is the "back up first" signal in the tag name.
<!-- END release-approval -->

## Before you change anything

- **[`CLAUDE.md`](CLAUDE.md)** — read it. Most of this codebase's failure modes
  are silent: FTS drifting out of sync with stored text, vectors written against
  a body that has since changed, an OCR result landing on a recycled row id.
  The invariants that prevent those are written down, and are not guessable from
  the code.
- **NuGet versions live in `Directory.Packages.props`** (Central Package
  Management). `csproj` files carry no `Version=` attribute.
- **Retrieval-affecting changes need an eval baseline first** (`mailvec eval`)
  — chunk size, RRF k, the embedding model, tool shapes. See `baselines/README.md`.
  This is what the frozen corpus above exists for.
