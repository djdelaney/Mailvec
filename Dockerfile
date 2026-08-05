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
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
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
# So 600s is a load choice against the IMAP provider, not a safety floor. A
# shorter interval is a supportable change — see docs/future-ideas.md's
# one-minute polling rollout for the canary that would justify it.
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
: "${MBSYNC_INTERVAL_SECONDS:=600}"
: "${MBSYNC_MAILDIR:=/mail/Fastmail}"
mkdir -p "${MBSYNC_MAILDIR}"

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
    printf '%s\n%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${MBSYNC_BEAT_SECONDS}" \
        > "${HEARTBEAT}.tmp" 2>/dev/null \
        && mv -f "${HEARTBEAT}.tmp" "${HEARTBEAT}" 2>/dev/null \
        || true
}

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
# interval was shortened, as a false red on /health, the tray and `mailvec
# doctor` during exactly the backlog pulls an operator most wants to watch.
#
# One beater for the whole run, rather than one per cycle: a per-cycle beater
# has to be killed each time, which orphans its in-flight `sleep` onto PID 1
# and leaks a zombie per sync. Beat once first so a fresh container isn't
# "unknown" for a full cadence.
beat
( while :; do sleep "${MBSYNC_BEAT_SECONDS}"; beat; done ) & beater=$!

while :; do
    mbsync -a & child=$!
    wait "$child" || echo "mbsync: sync failed (exit $?)" >&2
    sleep "${MBSYNC_INTERVAL_SECONDS}" & child=$!
    wait "$child"
done
EOF
RUN chmod +x /usr/local/bin/mbsync-loop
CMD ["mbsync-loop"]


FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS runtime
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
#
# Mcp__EnableTrayEndpoints=false is a SECURITY default, not just a tidy one: the
# /tray/* surface is unauthenticated at the origin and returns mail content, and
# nothing consumes it in a container (the tray is a local macOS client). Baking
# false here means the container is safe by construction — even a compose file
# that forgets to set it, and even if the tunnel's /tray/ path-404 rule is ever
# misconfigured, serves no tray data. See docs/security.md.
ENV Archive__DatabasePath=/data/archive.sqlite \
    Archive__SqliteVecExtensionPath=./vec0.so \
    Ingest__MaildirRoot=/mail \
    Mcp__BindAddress=0.0.0.0 \
    Mcp__EnableTrayEndpoints=false \
    Mcp__AttachmentDownloadDir=/data/downloads \
    MAILVEC_LOG_DIR=/logs

EXPOSE 3333
ENTRYPOINT ["/usr/local/bin/mailvec-entrypoint"]
CMD ["dotnet", "/app/mcp/Mailvec.Mcp.dll"]
