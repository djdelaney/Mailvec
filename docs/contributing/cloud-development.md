# Developing in Claude cloud sessions

Mailvec can be developed from [Claude Code cloud sessions](https://code.claude.com/docs/en/claude-code-on-the-web)
(Anthropic-hosted Linux VMs): build and the full .NET test suite. This page
covers how to set up an environment, how to check that it's sound, and what
stays on the development Mac or in CI.

Like the ops runbooks, this page describes how to VERIFY the environment
rather than asserting what it is. Anything observed is dated and has a command
to re-check it.

## Setting up the environment

The environment is provisioned in two steps. Each runs at a different time,
so each installs different things:

| | Runs | Installs |
|---|---|---|
| **Setup script** (claude.ai environment settings) | Before the repo is cloned. Cached as a filesystem snapshot for about a week. | The .NET 10 SDK and the `sqlite3` CLI: [`ops/claude-cloud-setup.sh`](../../ops/claude-cloud-setup.sh) |
| **SessionStart hook** ([`.claude/settings.json`](../../.claude/settings.json)) | After the clone, on every startup and resume | The sqlite-vec loadable into `runtimes/<rid>/native/`: [`ops/claude-session-start.sh`](../../ops/claude-session-start.sh) → [`ops/fetch-sqlite-vec.sh`](../../ops/fetch-sqlite-vec.sh) |

sqlite-vec can't go in the setup script. It installs into the checkout's
gitignored `runtimes/` directory, which `Directory.Build.props` copies into
every `bin/`, and the checkout doesn't exist yet when the setup script runs.
Routing it through `fetch-sqlite-vec.sh` also keeps that library's SHA-256 pin
in one place. The hook exits immediately unless `CLAUDE_CODE_REMOTE=true`, so
on the dev Mac it does nothing.

In the claude.ai environment settings for this repository:

1. **Network access: Trusted** (the default). Setup and build need the Ubuntu
   archive, nuget.org, and GitHub release downloads
   (`release-assets.githubusercontent.com`, for sqlite-vec). All are on the
   default allowlist.
2. **Environment variables:**
   ```
   DOTNET_NOLOGO=1
   DOTNET_CLI_TELEMETRY_OPTOUT=1
   ```
3. **Setup script:** paste the whole of
   [`ops/claude-cloud-setup.sh`](../../ops/claude-cloud-setup.sh).

**The file in the repo is the master copy; the settings field holds a
paste.** Change the script through a PR like any other file, then re-paste
it. Re-pasting also rebuilds the environment cache. CI checks that the script
parses, but nothing can check that the pasted copy matches the file. CI also
runs the hook after its own sqlite-vec fetch and requires it to report the
install as current (the `dotnet-linux` job in `ci.yml`).

Setup output is written live and also to `/var/log/mailvec-setup.log`, which
a session can read afterwards.

## Checking a new environment

You can't run commands on the VM yourself; the session's agent does. After
creating the environment or re-pasting the setup script, start a session on
this repo **with the Mailvec connector off** and send it the prompt below.
The prompt only asks for a report. To record the result, send "update the
table in docs/contributing/cloud-development.md with these observations and
open a PR" as a follow-up.

````text
This is a verification run of this cloud environment for Mailvec. Report
what you find; do not fix anything, edit files, commit, or push. Do not use
any Mailvec or mail connector. Run each step even if an earlier one fails.

1. Setup script. Run `tail -1 /var/log/mailvec-setup.log` (expect
   "setup complete") and `grep -nE '^(E|W):|WARNING' /var/log/mailvec-setup.log`
   (expect no output). If the log is missing, say so. That means the setup
   script in the environment settings is not the current
   ops/claude-cloud-setup.sh.

2. SessionStart hook. Quote the "Claude cloud session: sqlite-vec …" line
   from your starting context, or say it is absent. Then run
   `CLAUDE_CODE_REMOTE=true ops/claude-session-start.sh` and quote its
   output (expect "already installed"), plus
   `ls -l runtimes/linux-x64/native/` and `cat runtimes/linux-x64/native/VERSION`.

3. Build and test, as CI's Linux job does:
   `dotnet build --configuration Release`, then
   `dotnet test --configuration Release --no-build`.
   Report `dotnet --list-sdks`, then the passed/failed/skipped totals per
   test project. The pass is 0 failed. For each failure, give the test name
   and the first lines of its error. An error loading the vec0 extension
   means step 2 failed, so say so rather than diagnosing further. Note:
   three tests pass trivially as root (MaildirScannerTests.cs:360 and :398,
   AttachmentOcrServiceTests.cs:548); that is expected, not a finding.

4. Machine facts, one value per line with the command's raw output:
   `cat /etc/os-release` (PRETTY_NAME), `uname -a`, `id -u`, `nproc`,
   `free -g`, `df -hT .`, `docker info --format '{{.ServerVersion}}'`.

5. Docker image build (the Dockerfile has never been built in this
   environment). If `docker info` cannot reach the daemon, start one with
   `nohup dockerd > /tmp/dockerd.log 2>&1 &` and poll `docker info` for up
   to 60 s. If it never answers, report the last ~20 lines of
   /tmp/dockerd.log and stop this step. Once the daemon answers, run
   `docker build -t mailvec-cloud-check .` with a 30-minute limit. Report
   whether it succeeded and how long it took. On failure, report the
   failing step and its last ~20 lines, and don't try to fix it.
   Afterwards, `docker image rm mailvec-cloud-check`.

End with a summary table: step, result (pass / fail / not run), and one
line of evidence each.
````

**What passing looks like:** steps 1–3 pass with 0 failed tests. Step 5 is
informational until it has succeeded once. When it has, image changes can be
checked in a session, not only by `publish-images.yml`, and this page should
say so.

**The hook only exists on branches that contain it.** Start the session from
`main`, or from the branch under test, once `.claude/settings.json` is on it.
The first run (2026-09-25) started from a `main` that didn't have it yet. It
showed the signature of a missing hook: a clean build, then 654 of 1,470
tests failing with "sqlite-vec extension not found".

## What the machine is

Observed 2026-09-25 by a session's agent, using the prompt above. Re-check
with the commands in the right-hand column:

| Property | Observed | Re-check |
|---|---|---|
| OS | Ubuntu 24.04.4 LTS, x86_64 | `cat /etc/os-release`; `uname -a` |
| Kernel | 6.18.44 Firecracker microVM (`-fc-` build) | `uname -a` |
| User | root (uid 0) | `id -u` |
| Resources | 4 CPUs, 15 GB RAM, no swap. ext4 on `/dev/vda`, 30 GB free for the session. | `nproc`; `free -g`; `df -hT .` |
| .NET SDK | 10.0.112 (Ubuntu's archive build, `/usr/lib/dotnet`) | `dotnet --list-sdks` |
| Docker | `docker` and `dockerd` installed in `/usr/bin`, **daemon not running**: no `/var/run/docker.sock` | `docker info` |

Anthropic's [cloud environment docs](https://code.claude.com/docs/en/cloud-environments)
list Docker as preinstalled. The binaries are there, but nothing starts the
daemon. Whether `dockerd` can be started by hand in this VM, and then build
the image, is what step 5 of the prompt now finds out. Until a session has
built the image, image changes are verified by `publish-images.yml`. If
`dockerd` does work, it belongs in the SessionStart hook, not the setup
script: the environment cache keeps files, not running processes.

## Running as root

Root reads through file mode `000`, so three tests that remove read
permission to exercise the unreadable-file path `return` early when
`Environment.IsPrivilegedProcess` is true. In a cloud session they report
**passed**, not skipped. They are:

- `MaildirScannerTests.cs:360` and `:398`
- `AttachmentOcrServiceTests.cs:548`

CI is unprivileged and runs them for real, so CI remains the authority for
these three tests. The tests that check key-file mode *bits*
(`EmbeddingRegistrationTests`, `VisionRegistrationTests`,
`ConnectionFactoryTests`) inspect modes rather than attempting a read, so root
doesn't change their outcome.

## There is no mail here

A cloud VM has no archive, no Maildir and no Ollama. The unit and integration
tests build their own fixtures and fakes, so none of that matters for
`dotnet test`. It matters for running a service by hand:

- **Point it at a scratch location**, e.g. `Archive__DatabasePath=/tmp/mv/archive.sqlite
  Ingest__MaildirRoot=/tmp/mv/Mail dotnet run --project src/Mailvec.Mcp`.
  The migrator creates an empty schema on first open. The vec0 path resolves
  to `runtimes/<rid>/native/` automatically.
- **Without an embedding backend**, keyword search, `get_email`, `get_thread`
  and the attachment tools work. `hybrid` (the default search mode) and
  `semantic` answer with a "retry with mode=keyword" error, and the embedder
  cannot run.

The **frozen-corpus block** at the top of `CLAUDE.md` / `AGENTS.md` describes
the development Mac. Nothing it guards exists on a cloud VM, and the launchd
scripts it forbids are macOS-only anyway.

## Keep dev sessions away from the live mailbox

A cloud session can have claude.ai connectors enabled, including the
production Mailvec connector. That connector serves your real mail, whose
every byte is sender-controlled text, to an agent that in a dev session can
also push branches and run arbitrary commands. **Leave it off in development
sessions.** Development doesn't need it: the test suite builds its own data.

## What stays on the Mac or in CI

| Task | Where | Why not from a cloud session |
|---|---|---|
| Eval runs and `baselines/` | Dev Mac | They measure the frozen real corpus. Numbers from any other corpus aren't comparable. See [local-dev-dataset.md](local-dev-dataset.md). |
| `ops/install*.sh`, `redeploy.sh`, `stop.sh`, MCPB build and signing | Dev Mac (and refused there while frozen) | launchd and codesign are macOS-only. |
| `tests/Mailvec.CloudSmoke.Tests` | CI (`cloud-smoke.yml`) | They need hosted-provider API keys, which don't belong in a cloud environment's settings. |
| Releases (`ops/release.sh`, `v*` tags) | Dev Mac, on explicit approval | Cloud sessions push only to their own working branch. See Releases in `CLAUDE.md`. |
| Deployment, the homelab, `/health` on the live stack | Dev Mac / homelab | Needs the production network and credentials, which cloud sessions don't have and shouldn't be given. |
