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

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
FROM alpine:3.21 AS mbsync
RUN apk add --no-cache isync ca-certificates
RUN cat <<'EOF' > /usr/local/bin/mbsync-loop
#!/bin/sh
# Interval loop replacing the launchd StartInterval job. 600s default matches
# the plist (tighter schedules hit mbsync's .mbsyncstate flock and fail with
# "channel is locked" when a backlog pull overruns the interval).
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
# Format: ISO-8601 UTC, then the interval — the reader shouldn't have to know
# this container's env to judge staleness. Written after every attempt,
# including a failed sync: a loop retrying against a dead IMAP server is
# alive, and that failure belongs in the log below, not in a fake death.
HEARTBEAT="$(dirname "${MBSYNC_MAILDIR}")/.mailvec-mbsync-heartbeat"
beat() {
    printf '%s\n%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${MBSYNC_INTERVAL_SECONDS}" \
        > "${HEARTBEAT}.tmp" 2>/dev/null \
        && mv -f "${HEARTBEAT}.tmp" "${HEARTBEAT}" 2>/dev/null \
        || true
}

child=
trap 'if [ -n "$child" ]; then kill -TERM "$child" 2>/dev/null; wait "$child"; fi; exit 0' TERM INT
while :; do
    mbsync -a & child=$!
    wait "$child" || echo "mbsync: sync failed (exit $?)" >&2
    beat
    sleep "${MBSYNC_INTERVAL_SECONDS}" & child=$!
    wait "$child"
done
EOF
RUN chmod +x /usr/local/bin/mbsync-loop
CMD ["mbsync-loop"]


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
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
