# mbsync 60-Second Interval Review

Date: 2026-08-07

## Executive summary

The current sidecar loop does not have an inherent self-overlap or state-corruption problem when run as a single container. It waits for each `mbsync -a` process to exit before starting its delay, and upstream mbsync protects its synchronization state files against concurrent processes with advisory `fcntl` locks.

The implementation is therefore safe to operate with `MBSYNC_INTERVAL_SECONDS=60`, subject to the issues below. The most important problem is that the interval is not validated: zero, negative, and malformed values can turn the wrapper into a tight retry loop. Marker write errors are also discarded silently, and the health signal can temporarily use the previous deployment's cadence after an interval change.

The repository does **not** currently default to a 60-second sync interval. `compose.yml`, `.env.example`, and the wrapper default remain at 600 seconds. A deployment must explicitly set `MBSYNC_INTERVAL_SECONDS=60`.

## Scope reviewed

- The `mbsync-loop` shell wrapper in [`Dockerfile`](Dockerfile)
- Compose and example environment configuration
- mbsync heartbeat and last-success health-file readers
- Health and Uptime Kuma documentation
- macOS launchd behavior where it intersects with the container implementation
- Current upstream mbsync locking and exit-status behavior
- Relevant recent changes, including `6192314`, `0a35040`, and `64da926`

## Locking and concurrency verdict

### A single sidecar cannot overlap itself

The wrapper executes the sync and delay serially:

```sh
mbsync -a & child=$!
wait "$child"; rc=$?
# Record success or log the nonzero status.
sleep "${MBSYNC_INTERVAL_SECONDS}" & child=$!
wait "$child"
```

The next `mbsync -a` is not launched until both the previous sync and the subsequent delay have finished. A long sync therefore postpones the next attempt instead of overlapping it.

This also means that 60 seconds is a **completion-to-next-start delay**, not a fixed start-to-start period. If a sync takes 25 seconds, starts will be approximately 85 seconds apart. That behavior is safe and likely desirable, but it should be understood when interpreting the configured cadence.

### External concurrent invocations remain possible

Scaling the sidecar above one replica, running `docker compose exec ... mbsync -a`, or scheduling another host process can create concurrency outside the wrapper. Upstream mbsync protects sync-state files against concurrent processes; its current implementation uses advisory `fcntl(F_SETLK)` locks. Process termination releases those locks, so a leftover lock filename alone does not represent a permanently held lock.

This protection prevents concurrent state-file mutation, but it is not a global wrapper lock. Duplicate invocations may still fail, partially progress across independent channels, produce noisy logs, or withhold the last-success marker. Production deployments should continue to run exactly one scheduled sidecar and avoid ad hoc syncs while it is active.

Sources:

- [Official mbsync manual](https://isync.sourceforge.io/mbsync.html)
- [Upstream synchronization-state locking implementation](https://sourceforge.net/p/isync/isync/ci/master/tree/src/sync_state.c)

### Shutdown handling is sound

The wrapper backgrounds both mbsync and sleep so the PID 1 shell can receive its trap promptly. On `SIGTERM` or `SIGINT`, it stops the heartbeat process, forwards `SIGTERM` to the current child, waits for it, and exits. This avoids waiting through the entire configured sleep and reduces the chance of killing mbsync abruptly during state persistence.

## Findings

### 1. P2 — The interval is not validated

Location: [`Dockerfile`](Dockerfile), initialization of `MBSYNC_INTERVAL_SECONDS` and the main-loop `sleep`.

The wrapper accepts any nonempty environment value. Important cases include:

- `0`: `sleep 0` succeeds immediately and mbsync runs continuously.
- Negative or malformed values: `sleep` normally fails immediately.
- Because the wrapper uses `set -u`, not `set -e`, a failed `sleep` does not stop the loop.

The resulting tight loop could repeatedly connect to the provider, trigger throttling, consume resources, flood logs, and increase contention with any external invocation.

Recommendation: validate the value once at startup as a positive integer and fail fast. If 60 seconds is the supported lower bound, enforce `>= 60` rather than merely `> 0`.

### 2. P2 — Health-marker write failures are silent

Location: [`Dockerfile`](Dockerfile), `beat()` and `sync_ok()`.

Both atomic marker updates end with `|| true`, with write and rename errors redirected to `/dev/null`. Continuing the sync loop when observability storage is unavailable is reasonable, but suppressing every error makes diagnosis unnecessarily difficult.

For the last-success marker, the failure mode is especially confusing: mbsync can succeed while `/health` eventually reports that synchronization is stale, with no sidecar log explaining the discrepancy.

Recommendation: keep these failures nonfatal, but emit a rate-limited or at least per-attempt stderr warning when the temporary write or rename fails. Clean up a failed temporary file where practical.

### 3. P2 — A cadence change is not reflected until a sync succeeds

Location: [`Dockerfile`](Dockerfile), `sync_ok()`; [`src/Mailvec.Core/Health/MbsyncSyncFile.cs`](src/Mailvec.Core/Health/MbsyncSyncFile.cs), `StaleWindow` and `Classify`.

The current interval is stored only in the last-success marker. After changing the interval, the marker continues to declare the old cadence until the first successful sync under the new configuration.

This matters precisely when monitoring is most useful: if every sync after a configuration change fails, staleness is evaluated using the old interval. For a 600-to-60-second change, the alert window remains 40 minutes instead of falling to the 30-minute minimum. Larger previous interval values could delay the alert further.

Recommendation: separate current scheduling metadata from last-success state. For example, write the current configured interval at startup or on every attempt, while updating the success timestamp only after exit status zero.

### 4. Operational gap — A deployment that has never succeeded is not stale

Location: [`src/Mailvec.Core/Health/MbsyncSyncFile.cs`](src/Mailvec.Core/Health/MbsyncSyncFile.cs) and [`docs/monitoring-uptime-kuma.md`](docs/monitoring-uptime-kuma.md).

If the marker is absent or unreadable, health reports:

```json
{ "known": false, "syncStale": false }
```

Consequently, a fresh deployment with an invalid app password, DNS failure, or other persistent failure does not fail the default expression that checks only `mail.syncStale = false`. The documentation acknowledges this as a deliberate compromise to avoid marking fresh installs and local development red.

Recommendation: production monitoring should separately require `mail.known = true`, with enough retries or startup grace to permit the first synchronization.

### 5. P3 — Exit-status documentation is stale

Location: [`src/Mailvec.Cli/Commands/DoctorCommand.cs`](src/Mailvec.Cli/Commands/DoctorCommand.cs), comment above `InspectMbsyncStderr`.

The comment says mbsync exits zero for channel-lock, DNS, and socket errors. Current upstream source propagates synchronization errors through a nonzero process return value. The runtime wrapper's decision to update the success marker only for exit status zero is therefore appropriate, but the comment gives maintainers the wrong mental model.

Inspecting recent stderr remains useful because process exit state is ephemeral and error detail is operationally valuable. The comment should state that rationale instead.

Source: [upstream main synchronization and return-code handling](https://sourceforge.net/p/isync/isync/ci/master/tree/src/main_sync.c)

### 6. P3 — Locking documentation is imprecise

Location: [`docs/deploy-docker.md`](docs/deploy-docker.md).

The deployment guide refers to the `.mbsyncstate` "flock rationale." Current upstream mbsync uses `fcntl` record locking rather than the `flock(2)` mechanism. This does not change the safety conclusion, but using the generic phrase "state-file locking" would avoid incorrect operational assumptions, especially across filesystems.

### 7. Operational consideration — A 60-second delay increases provider load

The account configuration synchronizes all selected channels. Moving from 600 seconds to 60 seconds substantially increases connection, mailbox-listing, and no-op synchronization activity. The wrapper prevents local overlap, but it cannot prevent provider throttling or latency growth.

Recommendation: canary the shorter interval, watch mbsync stderr and sync duration, and verify that the effective completion-to-start cadence remains acceptable. A hung sync has no outer wrapper timeout; the independent heartbeat stays fresh while the last-success marker eventually becomes stale, so the condition is detected but not automatically recovered.

## Configuration observations

- `Dockerfile` default: 600 seconds
- `compose.yml` fallback: 600 seconds
- `.env.example`: 600 seconds
- Heartbeat cadence: independently fixed at 60 seconds
- Last-success stale window: four declared sync intervals, with a 30-minute minimum
- macOS launchd job: remains a separate 600-second scheduling path

Separating the 60-second liveness heartbeat from the sync cadence is correct. It prevents a long-running but healthy sync from making the sidecar appear dead.

## Recommended changes

In priority order:

1. Validate `MBSYNC_INTERVAL_SECONDS` and enforce the supported minimum.
2. Log marker write failures without terminating the sidecar.
3. Publish the current cadence independently from the last-success timestamp.
4. Require `mail.known=true` in production monitoring.
5. Correct the stale exit-code and locking terminology in comments and documentation.
6. Explicitly document that the interval is measured after completion and that the repository default remains 600 seconds unless intentionally changed.

## Verification performed

- Inspected the loop's process lifecycle and signal forwarding.
- Compared the implementation with upstream mbsync state locking and return-code handling.
- Reviewed the heartbeat and last-success readers and their monitoring integration.
- Ran the targeted mbsync-related test sets: **44 passed, 0 failed**.

No runtime code was changed as part of this review.
