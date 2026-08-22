# syntax=docker/dockerfile:1
# One image containing all four Mailvec .NET services (indexer, embedder, mcp,
# cli). Each compose service picks its binary via `command:`; the default CMD
# runs the MCP server. The CLI is on PATH as `mailvec`, so operator commands
# work as `docker exec <container> mailvec status|doctor|eval|checkpoint ...`.
#
#   docker build -t mailvec .                          # build host's arch
#   docker build --platform linux/amd64 -t mailvec .   # x86_64 Proxmox target
#
# Publish is framework-dependent: the aspnet base image supplies the runtime
# for all four binaries (the workers need only the subset it includes).
# sqlite-vec is fetched inside the build for the image's platform, so the
# image never depends on a host-side ops/fetch-sqlite-vec.sh run.
#
# A separate lightweight `mbsync` stage (compose target: mbsync) replaces the
# com.mailvec.mbsync launchd interval job for container deployments.

# Bases are pinned by DIGEST; the version tag stays in the comment for humans.
# A tag like `10.0` is a moving pointer that picks up .NET servicing patches —
# which is exactly why it must not be what a reproducible build resolves. The
# tradeoff is real and worth stating: a digest pin freezes those patches too, so
# it is only safe because .github/dependabot.yml has the `docker` ecosystem
# enabled and bumps these weekly. If Dependabot is ever turned off, go back to
# tags rather than sitting on a frozen base.
#   docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN set -eux; \
    case "${TARGETARCH}" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    ./ops/fetch-sqlite-vec.sh "${RID}"; \
    for svc in Indexer Embedder Mcp Cli; do \
        out="/app/$(echo "${svc}" | tr '[:upper:]' '[:lower:]')"; \
        dotnet publish "src/Mailvec.${svc}/Mailvec.${svc}.csproj" \
            -c Release -r "${RID}" --self-contained false -o "${out}"; \
        # Arch-agnostic extension path: Archive__SqliteVecExtensionPath below
        # says ./vec0.so regardless of RID, resolved against each binary's dir.
        cp "${out}/runtimes/${RID}/native/vec0.so" "${out}/vec0.so"; \
    done


# Pull-only IMAP sync sidecar. Config comes from a bind-mounted /root/.mbsyncrc
# (see ops/mbsyncrc.container.example); the Fastmail app password from a
# compose file-secret the config's PassCmd cats.
FROM alpine:3.24@sha256:28bd5fe8b56d1bd048e5babf5b10710ebe0bae67db86916198a6eec434943f8b AS mbsync
RUN apk add --no-cache isync ca-certificates
RUN cat <<'EOF' > /usr/local/bin/mbsync-loop
#!/bin/sh
# Interval loop replacing the launchd StartInterval job.
#
# THIS LOOP CANNOT OVERLAP ITS OWN RUNS, and the interval is a delay AFTER
# completion rather than an independent timer: it starts `mbsync -a`, waits for
# that exact child, and only then sleeps. A backlog pull that takes 12 minutes
# does not queue anything behind it; the next run starts one interval after it
# finishes. An earlier version of this comment claimed the opposite — that a
# tighter schedule would collide with an in-flight run and fail with "channel
# is locked" — which is inherited from the launchd plist's StartInterval and
# does not describe this loop. (The plist records a real, dated observation of
# lock failures at 300s; it is left alone here because launchd's own
# skip-while-running behaviour means those had some other cause, and replacing
# a measurement with an inference is how a runbook goes quietly wrong.)
#
# So the interval is a load choice against the IMAP provider, not a safety
# floor. The default was 600s until the author's deployment ran a 60s canary
# and it was promoted; the macOS launchd plist deliberately stayed at 600s,
# because its comment records a dated measurement from a path this deployment
# no longer exercises. Raise it if your provider throttles — and note the real
# cost of a short interval is unbatched indexer scans contending for SQLite's
# single writer, not the syncs themselves (see .env.example).
#
# This runs as PID 1, which gets no default SIGTERM handler — without the
# trap, every `docker stop` burned the full grace period and SIGKILLed the
# loop (potentially mid-IMAP-sync, leaving the next run to hit the state
# flock). The trap forwards TERM to the in-flight child so mbsync can
# journal and exit, and the child always runs backgrounded + wait'ed
# because POSIX sh delivers traps only after a *foreground* command
# completes — a foreground sleep would defer the stop by up to the full
# interval.
set -u
: "${MBSYNC_INTERVAL_SECONDS:=60}"
: "${MBSYNC_MAILDIR:=/mail/Fastmail}"
mkdir -p "${MBSYNC_MAILDIR}"

# Validate the interval before it can be used as a sleep duration.
#
# This loop runs `set -u` but NOT `set -e`, so a failing `sleep` does not stop
# it. Without this check, `MBSYNC_INTERVAL_SECONDS=0` (sleep returns instantly)
# or any malformed value like `60s`, `-5` or a stray character (sleep errors
# out) turns the sidecar into a tight loop that reconnects to the IMAP provider
# as fast as it can, floods the log, and invites throttling or a ban. A typo in
# the deployment's .env is the whole distance between the intended cadence and
# that, and nothing downstream would flag it: the heartbeat stays fresh and
# syncs keep succeeding, so it reads as healthy while hammering the provider.
#
# The glob rejects empty, negatives (the sign is a non-digit), decimals, and
# unit suffixes; the -lt 1 test then rejects 0 and 000.
case "${MBSYNC_INTERVAL_SECONDS}" in
    ''|*[!0-9]*)
        echo "mbsync: MBSYNC_INTERVAL_SECONDS must be a whole number of seconds, got '${MBSYNC_INTERVAL_SECONDS}'." >&2
        exit 1 ;;
esac
if [ "${MBSYNC_INTERVAL_SECONDS}" -lt 1 ]; then
    echo "mbsync: MBSYNC_INTERVAL_SECONDS must be at least 1, got '${MBSYNC_INTERVAL_SECONDS}'." >&2
    exit 1
fi
# Warn rather than refuse below 60 (the shipped default). The interval is a
# load choice against the IMAP provider, not a safety floor — the loop cannot
# overlap itself at any value — so a hard minimum would block legitimate
# experimentation below the default. Loud, but not fatal.
if [ "${MBSYNC_INTERVAL_SECONDS}" -lt 60 ]; then
    echo "mbsync: MBSYNC_INTERVAL_SECONDS=${MBSYNC_INTERVAL_SECONDS} is below 60s; this is a load choice against your IMAP provider, watch for throttling." >&2
fi

# Cadence of the liveness beat below. A constant, not an env var, and
# deliberately NOT MBSYNC_INTERVAL_SECONDS: it mirrors
# ServiceHeartbeat.BeatInterval (60s) so every service in the stack is judged
# stale on the same scale, and there is nothing deployment-specific to tune.
# Wiring it to the sync interval is the bug this fixed — see below.
MBSYNC_BEAT_SECONDS=60

# Liveness beat, read by the MCP server's HealthService via
# MbsyncHeartbeatFile (Mailvec.Core). This sidecar is the one service that
# can't write the metadata table the others beat into — it's POSIX sh with no
# SQLite — so it uses the Maildir bind mount it already shares with everything
# else.
#
# Location: the PARENT of MBSYNC_MAILDIR, never inside it. MaildirScanner
# walks the Maildir root, and Maildir++ names folders with a leading dot, so a
# dotfile in the tree risks being parsed as a folder. Outside the root the
# scanner never sees it.
#
# Format: ISO-8601 UTC, then the BEAT cadence — the reader shouldn't have to
# know this container's env to judge staleness, and it judges at
# StaleAfterMissedBeats (3) x whatever this line says. That line used to carry
# MBSYNC_INTERVAL_SECONDS, which made the reader's window a multiple of the
# SYNC cadence rather than the beat's; the two are unrelated now that the beat
# has its own timer, and conflating them is what let the 600s default hide the
# bug described below.
HEARTBEAT="$(dirname "${MBSYNC_MAILDIR}")/.mailvec-mbsync-heartbeat"
beat() {
    # Nonfatal, but no longer silent. A read-only or full Maildir mount used to
    # fail here with every error sent to /dev/null, so the beat simply stopped
    # appearing and the sidecar read as dead while running perfectly. Volume
    # is bounded by the beat cadence itself.
    if ! { printf '%s\n%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${MBSYNC_BEAT_SECONDS}" \
           > "${HEARTBEAT}.tmp" && mv -f "${HEARTBEAT}.tmp" "${HEARTBEAT}"; }; then
        echo "mbsync: could not write heartbeat ${HEARTBEAT} (liveness will read as stale)" >&2
        rm -f "${HEARTBEAT}.tmp" 2>/dev/null || true
    fi
}

# Last-SUCCESSFUL-sync marker, read by MbsyncSyncFile. A third signal, and
# deliberately a second file rather than more lines in the beat: `beat()` runs
# inside the backgrounded subshell below, which forked before any of this
# loop's assignments and so cannot see them.
#
# The beat above is written on its own timer whether or not `mbsync -a`
# succeeded — correct, because a loop retrying against a dead IMAP server is
# alive, and calling it dead sends an operator hunting a stopped container that
# is running fine. The cost is a blind spot this closes: a sidecar whose every
# sync fails (expired app password, a Patterns typo, DNS gone) beats happily
# forever while no mail arrives, and nothing downstream can tell — the
# indexer's own timestamps only move when new mail is actually ingested, so
# "quiet mailbox" and "sync broken" look identical there.
#
# Same location rule as the beat: the PARENT of MBSYNC_MAILDIR, never inside
# it. Written ONLY on exit 0. Line 2 is the SYNC interval — and unlike the beat
# file, that is the right cadence to declare here, because how stale this may
# get genuinely is a multiple of how often a sync is attempted.
SYNCFILE="$(dirname "${MBSYNC_MAILDIR}")/.mailvec-mbsync-sync"
sync_ok() {
    # Same treatment, and the confusion here was worse: a failed write meant
    # mbsync SUCCEEDED while /health eventually reported sync stale, with
    # nothing in this log to explain the contradiction.
    if ! { printf '%s\n%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${MBSYNC_INTERVAL_SECONDS}" \
           > "${SYNCFILE}.tmp" && mv -f "${SYNCFILE}.tmp" "${SYNCFILE}"; }; then
        echo "mbsync: sync succeeded but could not write ${SYNCFILE} (health will report sync stale)" >&2
        rm -f "${SYNCFILE}.tmp" 2>/dev/null || true
    fi
}

# --- finding 3: publish the CURRENT cadence without waiting for a success ---
#
# The interval line lives in the success marker, written only on exit 0, so
# after a cadence change the marker kept declaring the OLD interval until the
# next successful sync. MbsyncSyncFile judges staleness at 4x whatever that line
# says (min 30 min), so if every sync failed after the change, alerting used the
# old window -- for a 600->60 change, 40 minutes instead of 30. Precisely when
# the alert matters most.
#
# Rewrite line 2 at startup while preserving line 1: the declared cadence
# becomes current immediately, and the last-SUCCESS timestamp is untouched
# (republishing it would fabricate a success that never happened). No marker
# yet means nothing to correct -- absent is reported as known=false, which is
# the honest reading for a deployment that has never synced.
republish_cadence() {
    [ -f "${SYNCFILE}" ] || return 0
    last="$(head -n 1 "${SYNCFILE}" 2>/dev/null)" || return 0
    [ -n "${last}" ] || return 0
    if ! { printf '%s\n%s\n' "${last}" "${MBSYNC_INTERVAL_SECONDS}" \
           > "${SYNCFILE}.tmp" && mv -f "${SYNCFILE}.tmp" "${SYNCFILE}"; }; then
        echo "mbsync: could not republish sync cadence into ${SYNCFILE}" >&2
        rm -f "${SYNCFILE}.tmp" 2>/dev/null || true
    fi
}
republish_cadence

child=
beater=
trap 'if [ -n "$beater" ]; then kill "$beater" 2>/dev/null; fi; if [ -n "$child" ]; then kill -TERM "$child" 2>/dev/null; wait "$child"; fi; exit 0' TERM INT

# The beat runs on its own timer for the life of the container, NOT after each
# sync. This is the same rule the .NET services follow (HeartbeatService is a
# separate BackgroundService with its own PeriodicTimer, precisely so a long
# Ollama batch can't fake a dead worker), and mbsync needs it for the same
# reason: beating only on completion means any sync longer than
# StaleAfterMissedBeats x the declared cadence reports a BUSY sidecar as dead.
# At the old 600s that window was 30 minutes and a 12-minute backlog pull fit
# inside it, so the flaw was invisible — it would have surfaced the moment the
# interval was shortened, as a false red on /health and `mailvec doctor` during
# exactly the backlog pulls an operator most wants to watch.
#
# One beater for the whole run, rather than one per cycle: a per-cycle beater
# has to be killed each time, which orphans its in-flight `sleep` onto PID 1
# and leaks a zombie per sync. Beat once first so a fresh container isn't
# "unknown" for a full cadence.
beat
( while :; do sleep "${MBSYNC_BEAT_SECONDS}"; beat; done ) & beater=$!

while :; do
    mbsync -a & child=$!
    # Capture the status explicitly rather than reading $? inside a branch.
    # It happens to survive both `|| cmd` and an if/else today, but it is one
    # inserted command away from silently reporting the wrong exit code, and a
    # wrong code here is the difference between marking a sync successful and
    # not.
    wait "$child"; rc=$?
    if [ "$rc" -eq 0 ]; then
        sync_ok
    else
        echo "mbsync: sync failed (exit $rc)" >&2
    fi
    sleep "${MBSYNC_INTERVAL_SECONDS}" & child=$!
    wait "$child"
done
EOF
RUN chmod +x /usr/local/bin/mbsync-loop
CMD ["mbsync-loop"]


FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS runtime
# curl is for the compose healthcheck against /health.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app /app
RUN printf '#!/bin/sh\nexec dotnet /app/cli/Mailvec.Cli.dll "$@"\n' > /usr/local/bin/mailvec \
    && chmod +x /usr/local/bin/mailvec \
    && mkdir -p /data /mail /logs
RUN cat <<'EOF' > /usr/local/bin/mailvec-entrypoint
#!/bin/sh
# Guard against the silent-fresh-DB trap: with a wrong/empty volume mount,
# SchemaMigrator happily creates a fresh empty schema at Archive__DatabasePath
# and the stack serves an empty archive that looks perfectly healthy. When the
# operator declares the DB should already be seeded, refuse to start instead.
db="${Archive__DatabasePath:-/data/archive.sqlite}"
if [ "${MAILVEC_REQUIRE_SEEDED_DB:-0}" = "1" ] && [ ! -s "${db}" ]; then
    echo "mailvec: MAILVEC_REQUIRE_SEEDED_DB=1 but ${db} is missing or empty." >&2
    echo "mailvec: seed the data volume from an ops/export-db.sh snapshot, or set MAILVEC_REQUIRE_SEEDED_DB=0 to allow a fresh empty archive." >&2
    exit 1
fi
exec "$@"
EOF
RUN chmod +x /usr/local/bin/mailvec-entrypoint

# Container-shaped defaults; override per-service in compose. Env vars are the
# highest-precedence config source, so these beat the appsettings.json values
# published alongside each binary. MAILVEC_LAUNCHD is deliberately NOT set:
# the Serilog console sink is what feeds `docker logs`.
ENV Archive__DatabasePath=/data/archive.sqlite \
    Archive__SqliteVecExtensionPath=./vec0.so \
    Ingest__MaildirRoot=/mail \
    Mcp__BindAddress=0.0.0.0 \
    Mcp__AttachmentDownloadDir=/data/downloads \
    MAILVEC_LOG_DIR=/logs

EXPOSE 3333
ENTRYPOINT ["/usr/local/bin/mailvec-entrypoint"]
CMD ["dotnet", "/app/mcp/Mailvec.Mcp.dll"]
