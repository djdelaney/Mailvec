# syntax=docker/dockerfile:1.26.0@sha256:ecfaec9ed6d810b56388c508f4121597bfbba70d41a6dfeee4d8cad5f295fc32
# One image containing all five Mailvec .NET binaries (indexer, embedder, mcp,
# cli, parse). Each compose service picks its binary via `command:`; the default CMD
# runs the MCP server. The CLI is on PATH as `mailvec`, so operator commands
# work as `docker exec <container> mailvec status|doctor|eval|checkpoint ...`.
#
#   docker build -t mailvec .                          # build host's arch
#   docker build --platform linux/amd64 -t mailvec .   # x86_64 Proxmox target
#
# Publish is framework-dependent: the aspnet base image supplies the runtime
# for all five binaries (the workers need only the subset it includes).
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
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
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
    for svc in Indexer Embedder Mcp Cli Parse; do \
        out="/app/$(echo "${svc}" | tr '[:upper:]' '[:lower:]')"; \
        dotnet publish "src/Mailvec.${svc}/Mailvec.${svc}.csproj" \
            -c Release -r "${RID}" --self-contained false -o "${out}"; \
        # Arch-agnostic extension path: Archive__SqliteVecExtensionPath below
        # says ./vec0.so regardless of RID, resolved against each binary's dir.
        # (Not for parse: it references no Core, so no SQLite and no vec0.)
        if [ -f "${out}/runtimes/${RID}/native/vec0.so" ]; then \
            cp "${out}/runtimes/${RID}/native/vec0.so" "${out}/vec0.so"; \
        fi; \
    done; \
    # ------------------------------------------------------------------
    # The isolation boundary, enforced in the image rather than described.
    # Only /app/parse may carry a parser. The indexer, embedder, mcp and cli
    # binaries still LINK Mailvec.Parsing.dll (for Parser:Mode=inprocess on a
    # macOS install), so every library it depends on is deleted from their
    # directories: MimeKit, PdfPig, OpenXml, AngleSharp, PDFium, SkiaSharp,
    # LibTiff. With those gone, Parser:Mode=inprocess in a container fails
    # loudly (FileNotFoundException at the first parse) instead of silently
    # widening the attack surface back to every privileged process. The
    # sentinel `test -e` BEFORE each delete is the guard against a package
    # bump renaming an assembly: a rename would leave a parser in a
    # privileged directory with nothing failing, so the build fails instead.
    # ------------------------------------------------------------------
    for svc in indexer embedder mcp cli; do \
        for sentinel in MimeKit.dll UglyToad.PdfPig.dll DocumentFormat.OpenXml.dll AngleSharp.dll libpdfium.so libSkiaSharp.so Mailvec.Parsing.dll; do \
            test -e "/app/${svc}/${sentinel}" || { echo "expected /app/${svc}/${sentinel} before stripping; a package bump may have renamed it" >&2; exit 1; }; \
        done; \
        rm -f "/app/${svc}/MimeKit.dll" "/app/${svc}/BouncyCastle.Cryptography.dll" \
              "/app/${svc}"/UglyToad.PdfPig*.dll \
              "/app/${svc}"/DocumentFormat.OpenXml*.dll "/app/${svc}/System.IO.Packaging.dll" \
              "/app/${svc}/AngleSharp.dll" \
              "/app/${svc}/PDFtoImage.dll" "/app/${svc}/SkiaSharp.dll" "/app/${svc}/BitMiracle.LibTiff.NET.dll" \
              "/app/${svc}/libpdfium.so" "/app/${svc}/libSkiaSharp.so"; \
        for gone in MimeKit.dll UglyToad.PdfPig.dll DocumentFormat.OpenXml.dll AngleSharp.dll libpdfium.so libSkiaSharp.so; do \
            test ! -e "/app/${svc}/${gone}"; \
        done; \
    done; \
    # And the one directory that must still carry them.
    for keep in MimeKit.dll UglyToad.PdfPig.dll DocumentFormat.OpenXml.dll AngleSharp.dll libpdfium.so libSkiaSharp.so; do \
        test -e "/app/parse/${keep}" || { echo "/app/parse/${keep} missing" >&2; exit 1; }; \
    done


# Pull-only IMAP sync sidecar. Config comes from a bind-mounted /etc/mbsyncrc
# (see ops/mbsyncrc.container.example); the Fastmail app password from a
# compose file-secret the config's PassCmd cats.
FROM alpine:3.24@sha256:294b683cb724975bec92580e1e685676bd4b50bda910ddb8c51d4cabeaec77e6 AS mbsync
# goimapnotify is the optional IMAP IDLE watcher (MBSYNC_IDLE_FOLDERS; see the
# loop below). Installed unconditionally so turning IDLE on is an env change,
# not an image rebuild. A single Go binary (~13 MB), packaged by Alpine.
RUN apk add --no-cache isync ca-certificates goimapnotify
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
: "${MBSYNC_CONFIG:=/etc/mbsyncrc}"

# Permission preflight. This sidecar runs as a non-root uid (compose
# MAILVEC_UID, default 10001) with cap_drop ALL, so nothing bypasses the
# permission bits: a Maildir, config or secret the host user chowned to root
# (the pre-non-root convention) is simply unreadable/unwritable, and the
# symptoms are far from the cause — a config "not found", a sync that fails
# every interval while the heartbeat stays fresh. Say what is wrong and how to
# fix it, then refuse to loop.
uid="$(id -u)"; gid="$(id -g)"; fail=0
say() { echo "mbsync: $*" >&2; }
[ -d /mail ] && [ ! -w /mail ] && { say "/mail is not writable by uid ${uid}. On the host: sudo chown -R ${uid}:${gid} ./mail"; fail=1; }
[ -r "${MBSYNC_CONFIG}" ] || { say "${MBSYNC_CONFIG} is missing or not readable by uid ${uid}. On the host: sudo chown ${uid}:${gid} ./mbsyncrc (keep it 0600)"; fail=1; }
for s in /run/secrets/*; do
    [ -e "$s" ] || continue
    [ -r "$s" ] || { say "$s is not readable by uid ${uid}. On the host: sudo chown ${uid}:${gid} ./secrets/$(basename "$s") (keep it 0600)"; fail=1; }
done
[ "$fail" -eq 0 ] || exit 1

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

# --- optional IMAP IDLE: wake the loop early when new mail arrives ---
#
# MBSYNC_IDLE_FOLDERS (comma-separated; unset = off, the default) runs
# goimapnotify, which holds one IDLE connection per folder and, on new mail,
# runs `touch ${WAKE}`. The sleep below then ends early and THIS loop runs the
# next `mbsync -a`.
#
# THE WATCHER NEVER RUNS mbsync ITSELF. This loop stays the single serialized
# runner: two mbsync processes race on .mbsyncstate and its lock, which is the
# failure the "Polling below one minute" note in docs/future-ideas.md warns
# about. A file rather than a signal because a trapped signal interrupts
# `wait "$child"` mid-sync, and the interrupted status reads as a failed sync.
# Only onNewMail is configured: flag changes and deletions wait for the timer.
#
# goimapnotify also runs onNewMail once per folder right after it connects (the
# "fake IMAP Event for first time sync" in its watch.go) -- on every start, so
# on every supervisor restart too. Taken as a wake, that is an unconditional
# extra `mbsync -a` seconds after the one that just ran. The supervisor
# therefore drops one start-up token per folder (${WAKE}.start.N) before each
# start, and the hook consumes a token instead of touching the wake file while
# any remain. `rm` is the atomic claim: hooks for different folders run
# concurrently, and exactly one of them can remove a given token. The start-up
# event precedes any real IDLE event on its connection, so a token never
# swallows real mail; a watcher that dies before using its tokens gets a fresh
# set on the next start, and tokens still unused 30s after a start expire, so a
# folder goimapnotify skipped rather than failed on cannot leave one behind to
# eat a later wake.
#
# The wake file is removed just BEFORE each sync starts, never after: mail
# signalled while a sync is running must survive it and trigger the next one.
#
# Its config is generated from the first IMAPAccount block of mbsyncrc, so the
# host, user and PassCmd live in one place. Implicit TLS only (TLSType IMAPS):
# the watcher holds the password, and anything else is refused here.
#
# Everything here is latency only: every failure degrades to the timer. Note
# what goimapnotify 2.5 does on failure (observed against a fake server): a
# rejected login, a folder that doesn't exist, or a server unreachable for
# ~15s each make the WHOLE process exit 1, all folders' watches with it. The
# supervisor therefore backs off 30s, doubling to 15 minutes, and resets only
# after a run of 10 minutes: without that, a revoked app password or a
# misspelled folder would log in every 30 seconds forever.
: "${MBSYNC_IDLE_FOLDERS:=}"
# Overridable only so the tests (ops/tests) can run loops side by side.
: "${MBSYNC_WAKE_FILE:=/tmp/mbsync-wake}"
WAKE="${MBSYNC_WAKE_FILE}"
IDLE_CONF="$(dirname "${WAKE}")/mbsync-idle.yaml"
# Floor between the end of one sync and a wake-triggered start of the next, so
# a burst of arrivals is one sync rather than several back to back.
MBSYNC_IDLE_MIN_GAP_SECONDS=5
# Each folder holds its own IMAP connection, on top of mbsync's own, and
# providers cap concurrent connections per account.
MBSYNC_IDLE_MAX_FOLDERS=10

# One setting from the first IMAPAccount block of mbsyncrc, unquoted the way
# isync reads it (surrounding quotes removed, backslash escapes resolved, and a
# leading + on PassCmd dropped). isync strips that + AFTER unquoting
# (drv_imap.c, cred_from_cmd), so `PassCmd "+gpg ..."` is the form its parser
# accepts and the strip here must follow the unquote too: done before it, the
# + stayed inside the quotes and goimapnotify was handed `+gpg ...` to run.
# The + only means "flush the progress line before running", nothing the
# container needs. Prints nothing when the key is absent.
imap_setting() {
    awk -v want="$1" '
        { line = $0; sub(/^[ \t]+/, "", line); sub(/[ \t\r]+$/, "", line) }
        line == "" || line ~ /^#/ { next }
        { key = tolower(line); sub(/[ \t].*$/, "", key)
          val = line; sub(/^[^ \t]+[ \t]*/, "", val) }
        key == "imapaccount" { if (seen) exit; seen = 1; next }
        !seen { next }
        key == "imapstore" || key == "maildirstore" || key == "channel" || key == "group" { exit }
        key == want {
            sub(/^\+[ \t]*/, "", val)
            if (length(val) >= 2 && substr(val, 1, 1) == "\"" && substr(val, length(val), 1) == "\"") {
                inner = substr(val, 2, length(val) - 2); val = ""
                for (i = 1; i <= length(inner); i++) {
                    c = substr(inner, i, 1)
                    if (c == "\\" && i < length(inner)) { i++; c = substr(inner, i, 1) }
                    val = val c
                }
            }
            sub(/^\+[ \t]*/, "", val)
            print val; exit
        }' "${MBSYNC_CONFIG}"
}

# A YAML single-quoted scalar: the only escape is '' for '.
yaml_quote() {
    printf "'%s'" "$(printf '%s' "$1" | sed "s/'/''/g")"
}

# Writes ${IDLE_CONF} (mode 0600: it may hold a literal Pass) or explains, in
# one line, why IDLE stays off.
write_idle_config() {
    tls="$(imap_setting tlstype)"
    [ -n "${tls}" ] || tls="$(imap_setting ssltype)"
    case "$(printf '%s' "${tls:-STARTTLS}" | tr '[:lower:]' '[:upper:]')" in
        IMAPS) ;;
        # No apostrophe inside the ${...} default: bash (macOS /bin/sh) reads a
        # quote inside a parameter expansion within double quotes as syntax and
        # refuses the whole script. ash and dash accept it, so only a Mac run
        # of ops/tests ever saw the failure.
        *) say "IDLE disabled: it needs TLSType IMAPS in ${MBSYNC_CONFIG} (found ${tls:-none; isync defaults to STARTTLS})"; return 1 ;;
    esac
    host="$(imap_setting host)"; user="$(imap_setting user)"; port="$(imap_setting port)"
    passcmd="$(imap_setting passcmd)"; pass="$(imap_setting pass)"
    if [ -z "${host}" ] || [ -z "${user}" ]; then
        say "IDLE disabled: the first IMAPAccount in ${MBSYNC_CONFIG} needs Host and User"; return 1
    fi
    if [ -z "${passcmd}" ] && [ -z "${pass}" ]; then
        say "IDLE disabled: the first IMAPAccount in ${MBSYNC_CONFIG} needs Pass or PassCmd"; return 1
    fi
    case "${passcmd}" in
        # goimapnotify printf-formats any command containing %s.
        *%s*) say "IDLE disabled: goimapnotify cannot run a PassCmd containing %s"; return 1 ;;
    esac
    case "${port:=993}" in
        *[!0-9]*) say "IDLE disabled: Port '${port}' in ${MBSYNC_CONFIG} is not a number"; return 1 ;;
    esac
    # isync feeds the PassCmd output to the server as the OAuth2 access token
    # when AuthMechs names XOAUTH2 (Gmail, Office 365); goimapnotify does the
    # same only with xoAuth2 true. Without it the watcher sends the token as a
    # LOGIN password, is refused, and exits: an "IDLE watcher exited (status 1)"
    # at every backoff, forever, with nothing saying why.
    xoauth2=false
    case " $(imap_setting authmechs | tr '[:lower:]' '[:upper:]') " in
        *" XOAUTH2 "*)
            if [ -z "${passcmd}" ]; then
                say "IDLE disabled: AuthMechs XOAUTH2 needs a PassCmd that prints the access token, not a literal Pass"; return 1
            fi
            xoauth2=true ;;
    esac

    # The hook: claim a start-up token if any remain (see the comment above),
    # else signal the loop. goimapnotify runs it through `sh -c`.
    hook="for t in '${WAKE}'.start.*; do rm \"\$t\" 2>/dev/null && exit 0; done; touch '${WAKE}'"
    boxes=""; count=0
    set -f; old_ifs="${IFS}"; IFS=,
    for folder in ${MBSYNC_IDLE_FOLDERS}; do
        folder="$(printf '%s' "${folder}" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
        [ -n "${folder}" ] || continue
        count=$((count + 1))
        boxes="${boxes}      -
        mailbox: $(yaml_quote "${folder}")
        onNewMail: $(yaml_quote "${hook}")
"
    done
    IFS="${old_ifs}"; set +f
    if [ "${count}" -eq 0 ]; then
        say "IDLE disabled: MBSYNC_IDLE_FOLDERS names no folders"; return 1
    fi
    if [ "${count}" -gt "${MBSYNC_IDLE_MAX_FOLDERS}" ]; then
        say "IDLE disabled: MBSYNC_IDLE_FOLDERS names ${count} folders; the limit is ${MBSYNC_IDLE_MAX_FOLDERS} (each holds an IMAP connection)"; return 1
    fi
    IDLE_FOLDER_COUNT="${count}"

    if [ -n "${passcmd}" ]; then
        secret="    passwordCMD: $(yaml_quote "${passcmd}")"
    else
        secret="    password: $(yaml_quote "${pass}")"
    fi
    if ! ( umask 077
           printf '%s\n' "configurations:" "  -" \
               "    host: $(yaml_quote "${host}")" "    port: ${port}" "    tls: true" \
               "    tlsOptions:" "      rejectUnauthorized: true" "      starttls: false" \
               "    username: $(yaml_quote "${user}")" "${secret}" "    xoAuth2: ${xoauth2}" "    boxes:" > "${IDLE_CONF}.tmp" &&
           printf '%s' "${boxes}" >> "${IDLE_CONF}.tmp" &&
           mv -f "${IDLE_CONF}.tmp" "${IDLE_CONF}" ); then
        say "IDLE disabled: could not write ${IDLE_CONF}"; rm -f "${IDLE_CONF}.tmp"; return 1
    fi
}

idler=
if [ -n "${MBSYNC_IDLE_FOLDERS}" ] && write_idle_config; then
    rm -f "${WAKE}"
    (
        gin=
        trap 'if [ -n "$gin" ]; then kill "$gin" 2>/dev/null; fi; exit 0' TERM INT
        delay=30
        while :; do
            started="$(date +%s)"
            # One start-up token per folder for this start (see above); the
            # sweeper expires whatever this start never used.
            rm -f "${WAKE}".start.*
            n=1; while [ "${n}" -le "${IDLE_FOLDER_COUNT}" ]; do : > "${WAKE}.start.${n}"; n=$((n + 1)); done
            goimapnotify -conf "${IDLE_CONF}" & gin=$!
            ( sleep 30; rm -f "${WAKE}".start.* ) &
            wait "$gin"; rc=$?; gin=
            ran=$(( $(date +%s) - started ))
            [ "${ran}" -lt 600 ] || delay=30
            echo "mbsync: IDLE watcher exited (status ${rc}) after ${ran}s; restarting in ${delay}s" >&2
            sleep "${delay}" & gin=$!
            wait "$gin"; gin=
            delay=$((delay * 2)); [ "${delay}" -le 900 ] || delay=900
        done
    ) & idler=$!
    echo "mbsync: IDLE enabled for: ${MBSYNC_IDLE_FOLDERS}" >&2
fi

# The pause between syncs. Without IDLE, one sleep for the whole interval,
# exactly as before IDLE existed. With it, steps of the gap floor that end
# early once a wake is pending: a wake costs at most one step of latency on
# top of the floor, and the number of sleep forks per interval is bounded by
# the step, not the interval (one-second steps were 600 forks per cycle at a
# 600s interval, for nothing the floor didn't already delay).
nap() {
    if [ -z "${idler}" ] && [ -z "${mark_read}" ]; then
        sleep "${MBSYNC_INTERVAL_SECONDS}" & child=$!
        wait "$child"
        return
    fi
    waited=0
    while [ "${waited}" -lt "${MBSYNC_INTERVAL_SECONDS}" ]; do
        if [ "${waited}" -ge "${MBSYNC_IDLE_MIN_GAP_SECONDS}" ]; then
            if [ -e "${WAKE}" ]; then
                echo "mbsync: new mail signalled by IDLE; syncing now" >&2
                return
            fi
            # A queued mark_read request is a wake too, so a read flag
            # reaches the server in seconds rather than one full interval.
            if [ -n "${mark_read}" ] && mbsync-mark-read pending; then
                echo "mbsync: mark-read request queued; syncing now" >&2
                return
            fi
        fi
        step=$((MBSYNC_INTERVAL_SECONDS - waited))
        [ "${step}" -le "${MBSYNC_IDLE_MIN_GAP_SECONDS}" ] || step="${MBSYNC_IDLE_MIN_GAP_SECONDS}"
        sleep "${step}" & child=$!
        wait "$child"
        waited=$((waited + step))
    done
}

child=
beater=
trap 'for p in "$beater" "$idler"; do if [ -n "$p" ]; then kill "$p" 2>/dev/null; fi; done; if [ -n "$child" ]; then kill -TERM "$child" 2>/dev/null; wait "$child"; fi; exit 0' TERM INT

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
#
# The optional mark-read component (MBSYNC_MARK_READ_QUEUE; unset = off, the
# default) rides the same timer: its applier marker is how mcp knows requests
# will be applied, judged stale on the same beat scale. It is decided here,
# BEFORE the beater forks, because the subshell cannot see a later assignment.
# With the variable unset nothing below calls mbsync-mark-read at all.
mark_read=
if [ -n "${MBSYNC_MARK_READ_QUEUE:-}" ] && mbsync-mark-read preflight; then
    mark_read=1
fi
publish() {
    beat
    if [ -n "${mark_read}" ]; then mbsync-mark-read marker "${MBSYNC_BEAT_SECONDS}"; fi
}
publish
( while :; do sleep "${MBSYNC_BEAT_SECONDS}"; publish; done ) & beater=$!

while :; do
    rm -f "${WAKE}"
    # Queued read flags are applied BEFORE the sync, by this loop, so the
    # rename can't race mbsync and the sync that follows pushes it.
    if [ -n "${mark_read}" ]; then
        mbsync-mark-read apply
    elif [ -n "${MBSYNC_MARK_READ_QUEUE:-}" ]; then
        echo "mbsync: mark-read is OFF (see the preflight message at startup); queued requests are not applied" >&2
    fi
    mbsync -c "${MBSYNC_CONFIG}" -a & child=$!
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
    nap
done
EOF
RUN chmod +x /usr/local/bin/mbsync-loop
RUN cat <<'EOF' > /usr/local/bin/mbsync-mark-read
#!/bin/sh
# The sidecar half of the OPTIONAL mark-read component
# (docs/proposals/read-state.md). mcp writes requests into
# MBSYNC_MARK_READ_QUEUE; this applies them to the Maildir, and mbsync's
# PushFlags carries the change upstream on the sync that follows.
#
# Called only by mbsync-loop, and only when MBSYNC_MARK_READ_QUEUE is set: a
# deployment without the component never runs a line of this file.
#
#   mbsync-mark-read preflight      once at startup; non-zero means stay off
#   mbsync-mark-read marker <secs>  publish the applier marker (beat timer)
#   mbsync-mark-read apply          apply every queued request (before a sync)
#   mbsync-mark-read pending        exit 0 when a request is waiting
#
# TRUST: mcp is the internet-facing process and this one holds the credential,
# so nothing in a request file is trusted. Every line is re-validated here,
# against THIS process's own MBSYNC_MARK_READ_FOLDERS (never the marker, which
# mcp can write), and the only change ever made is adding or removing the `S`
# in a Maildir filename. Never touch another flag: with PushFlags a stray `T`
# becomes \Deleted on the server.
#
# Runs between syncs, from the loop itself, so a rename can never race an
# `mbsync -a` (two writers on .mbsyncstate and the Maildir is the hazard the
# loop's single-runner rule exists for).
set -u
# The done/ records name a folder and a Maildir filename per line and are kept
# for MBSYNC_MARK_READ_KEEP_DAYS: read-state PII, on a host-visible bind
# mount. Owner-only, like every other PII-bearing file the stack writes
# (the archive, the logs, export snapshots). mcp runs as the same uid, so it
# still reads the marker; renames keep each message file's own mode.
umask 077
Q="${MBSYNC_MARK_READ_QUEUE:?MBSYNC_MARK_READ_QUEUE is not set}"
: "${MBSYNC_MAILDIR:=/mail/Fastmail}"
: "${MBSYNC_CONFIG:=/etc/mbsyncrc}"
: "${MBSYNC_MARK_READ_FOLDERS:=}"
# A request that waited out an applier outage must not overwrite what the user
# did in another client meanwhile. Overridable only for ops/tests.
: "${MBSYNC_MARK_READ_EXPIRE_MINUTES:=15}"
: "${MBSYNC_MARK_READ_KEEP_DAYS:=30}"
ROOT="${MBSYNC_MAILDIR}"
MARKER="${Q}/.applier"
NL='
'
say() { echo "mbsync: mark-read: $*" >&2; }
trim() { printf '%s' "$1" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//'; }

# Whether any Sync line in mbsyncrc pushes flags. isync takes a list of
# directions (Pull, Push) and operations (New, Old, Upgrade, Gone, Flags) and
# applies their cross product; All/Full and PushFlags push flags outright, a
# bare Push with no operation pushes everything, and Flags pushes when paired
# with Push or given with no direction at all. No Sync line is isync's default,
# Sync All. Approximate by design: this is a preflight that stops a renamed
# flag from never reaching the server, not a parser for isync.
pushes_flags() {
    lines="$(grep -iE '^[[:space:]]*Sync([[:space:]]|$)' "${MBSYNC_CONFIG}" 2>/dev/null)" || return 0
    old_ifs="$IFS"; IFS="$NL"
    for line in $lines; do
        IFS="$old_ifs"
        push=0; dir=0; op=0; flags=0
        set -f; set -- $line; set +f; shift
        for t in "$@"; do
            case "$(printf '%s' "$t" | tr 'A-Z' 'a-z')" in
                all|full|pushflags) IFS="$old_ifs"; return 0 ;;
                push) push=1; dir=1 ;;
                pull|push*|pull*) dir=1 ;;
                flags) flags=1; op=1 ;;
                new|old|upgrade|gone) op=1 ;;
            esac
        done
        [ "$push" -eq 1 ] && [ "$op" -eq 0 ] && { IFS="$old_ifs"; return 0; }
        [ "$flags" -eq 1 ] && { [ "$push" -eq 1 ] || [ "$dir" -eq 0 ]; } && { IFS="$old_ifs"; return 0; }
        IFS="$NL"
    done
    IFS="$old_ifs"
    return 1
}

# The allowlist, one folder per line, blanks dropped. `*` is every folder and
# is never implied: an empty list keeps the component off.
folders() {
    set -f; old_ifs="$IFS"; IFS=,
    for f in ${MBSYNC_MARK_READ_FOLDERS}; do
        IFS="$old_ifs"; f="$(trim "$f")"; [ -z "$f" ] || printf '%s\n' "$f"; IFS=,
    done
    IFS="$old_ifs"; set +f
}

allowed() {
    old_ifs="$IFS"; IFS="$NL"; set -f
    for f in $(folders); do
        if [ "$f" = '*' ] || [ "$f" = "$1" ]; then IFS="$old_ifs"; set +f; return 0; fi
    done
    IFS="$old_ifs"; set +f
    return 1
}

# No symlink anywhere below the root on the way to this folder's cur/new — the
# indexer's rule (it follows none), and the containment this rename relies on.
# The root itself may be a link.
no_links() {
    p="${ROOT}"
    if [ "$1" != . ]; then
        old_ifs="$IFS"; IFS=/; set -f
        for c in $1; do
            p="${p}/${c}"
            if [ -L "$p" ]; then IFS="$old_ifs"; set +f; return 1; fi
        done
        IFS="$old_ifs"; set +f
    fi
    [ -d "$p" ] && [ ! -L "$p/cur" ] && [ ! -L "$p/new" ]
}

# One request line -> one outcome word on stdout:
# ok | unchanged | not_found | forbidden_folder | rejected | error
apply_one() {
    action="$1"; folder="$2"; base="$3"
    case "$action" in read|unread) ;; *) echo rejected; return ;; esac
    case "$folder" in
        .) ;;
        ''|/*|*/|*//*|..|../*|*/..|*/../*|./*|*/./*|*/.) echo rejected; return ;;
    esac
    case "$base" in ''|.*|*[!A-Za-z0-9._,=-]*) echo rejected; return ;; esac

    # Folder names follow MaildirScanner.FolderNameFor: the root itself is INBOX.
    if [ "$folder" = . ]; then name=INBOX; dir="${ROOT}"; else name="$folder"; dir="${ROOT}/${folder}"; fi
    allowed "$name" || { echo forbidden_folder; return; }
    no_links "$folder" || { echo rejected; return; }

    # Three shapes: the spec's bare `new/<base>`, isync's `new/<base>:2,`
    # (isync 1.5 delivers unseen mail into new/ WITH an empty info suffix;
    # observed against Dovecot, 2026-10-24), and `cur/<base>:2,<flags>`.
    file=; count=0
    for f in "${dir}/new/${base}" "${dir}/new/${base}:2,"* "${dir}/cur/${base}:2,"*; do
        { [ -f "$f" ] && [ ! -L "$f" ]; } || continue
        file="$f"; count=$((count + 1))
    done
    # None: mbsync moved or expunged it since mcp looked. More than one is not a
    # state mbsync produces, and guessing between them is how the wrong file
    # gets renamed.
    [ "$count" -eq 1 ] || { echo not_found; return; }

    case "$file" in *:2,*) flags="${file##*:2,}" ;; *) flags= ;; esac
    case "$flags" in *S*) seen=1 ;; *) seen=0 ;; esac
    if [ "$action" = read ]; then
        [ "$seen" -eq 0 ] || { echo unchanged; return; }
        # The Maildir spec wants flags in ASCII order.
        new="$(printf '%sS' "$flags" | fold -w 1 | LC_ALL=C sort | tr -d '\n')"
    else
        [ "$seen" -eq 1 ] || { echo unchanged; return; }
        new="$(printf '%s' "$flags" | tr -d S)"
    fi
    # Always into cur/: a message that has been looked at is no longer new,
    # and isync tracks it by the ,U= in its name wherever it sits.
    target="${dir}/cur/${base}:2,${new}"
    [ ! -e "$target" ] || { echo not_found; return; }
    if mv "$file" "$target"; then echo ok; else echo error; fi
}

# Field count and control characters, for every line, before anything is
# applied: a request that is malformed anywhere is not trusted anywhere.
well_formed() {
    [ "$(head -n 1 "$1")" = "mailvec-mark-read v1" ] || return 1
    awk -F '\t' 'NR > 1 { if (NF != 3) bad = 1; for (i = 1; i <= NF; i++) if ($i ~ /[[:cntrl:]]/) bad = 1 } END { exit bad }' "$1"
}

apply_all() {
    # chmod as well as the umask: a done/ created before the umask landed is
    # 0755, and its records stay readable until they expire.
    { mkdir -p "${Q}/done" && chmod 700 "${Q}/done"; } || { say "could not create ${Q}/done"; return 1; }
    for req in "${Q}"/*.req; do
        [ -e "$req" ] || continue
        name="$(basename "$req")"
        if [ -L "$req" ] || [ ! -f "$req" ]; then rm -f "$req"; say "removed ${name}: not a regular file"; continue; fi
        out="${Q}/done/${name}"
        if [ -n "$(find "$req" -mmin +"${MBSYNC_MARK_READ_EXPIRE_MINUTES}" 2>/dev/null)" ]; then
            tail -n +2 "$req" | while IFS= read -r line; do printf 'expired\t%s\n' "$line"; done > "${out}.tmp"
        elif ! well_formed "$req"; then
            printf 'rejected\t(malformed request file; nothing applied)\n' > "${out}.tmp"
        else
            tail -n +2 "$req" | while IFS='	' read -r action folder base; do
                printf '%s\t%s\t%s\t%s\n' "$(apply_one "$action" "$folder" "$base")" "$action" "$folder" "$base"
            done > "${out}.tmp"
        fi
        mv -f "${out}.tmp" "$out" && rm -f "$req"
        say "applied ${name}: $(cut -f 1 "$out" | LC_ALL=C sort | uniq -c | tr -s ' ' | tr '\n' ' ')"
    done
    find "${Q}/done" -type f -mtime +"${MBSYNC_MARK_READ_KEEP_DAYS}" -exec rm -f {} \; 2>/dev/null
    return 0
}

case "${1:-}" in
    preflight)
        fail=
        if [ ! -d "$Q" ] || [ ! -w "$Q" ]; then
            fail="${Q} is missing or not writable by uid $(id -u). On the host: mkdir -p -m 700 ./mark-read && sudo chown $(id -u):$(id -g) ./mark-read"
        elif [ -z "$(folders)" ]; then
            fail="MBSYNC_MARK_READ_FOLDERS is empty. Name the folders mark_read may change (comma-separated), or * for every folder."
        elif ! pushes_flags; then
            fail="no Sync line in ${MBSYNC_CONFIG} pushes flags, so a change made here would never reach the server. Use 'Sync Pull PushFlags'."
        fi
        if [ -n "$fail" ]; then
            # mcp reads a missing marker as "unavailable" and queues nothing.
            rm -f "$MARKER" 2>/dev/null
            say "OFF: ${fail}"
            exit 1
        fi
        say "on for folders: $(folders | tr '\n' ' ')"
        ;;
    marker)
        if ! { { printf '%s\n%s\n%s\n' "mailvec-mark-read-applier v1" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${2:?cadence}"; folders; } \
               > "${MARKER}.tmp" && mv -f "${MARKER}.tmp" "$MARKER"; }; then
            say "could not write ${MARKER} (mcp will report mark_read unavailable)"
            rm -f "${MARKER}.tmp" 2>/dev/null
        fi
        ;;
    apply) apply_all ;;
    pending)
        for req in "${Q}"/*.req; do [ -e "$req" ] && exit 0; done
        exit 1
        ;;
    *) echo "usage: mbsync-mark-read preflight|marker <seconds>|apply|pending" >&2; exit 2 ;;
esac
EOF
RUN chmod +x /usr/local/bin/mbsync-mark-read
# Non-root by default, not only when compose says so. compose.yml sets
# `user:` on every service, but a plain `docker run` of the published image,
# or a compose override that drops the line, used to run as root. The uid
# matches compose's MAILVEC_UID default; compose still overrides it.
USER 10001:10001
CMD ["mbsync-loop"]


FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca AS runtime
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

# Permission preflight. The services run as a non-root uid (compose
# MAILVEC_UID, default 10001) with cap_drop ALL, so nothing bypasses the
# permission bits. Bind sources created by an older, root-running stack are
# root-owned and the failures they cause name no permission problem: SQLite
# says "unable to open database file", Serilog fails SILENTLY (the mail
# pipeline runs on with no log files), an unreadable secret reads as an
# empty key. Check each mounted path for the running uid and say exactly
# what to chown. Only mounted paths are checked, so a service that mounts
# nothing (parse) passes trivially.
uid="$(id -u)"; gid="$(id -g)"; fail=0
say() { echo "mailvec: $*" >&2; }
mounted() { grep -qs " $1 " /proc/mounts; }
fix() { echo "On the host, from the compose directory: sudo chown -R ${uid}:${gid} $1"; }
if mounted /data; then
    [ -w /data ] || { say "/data is not writable by uid ${uid} (SQLite needs to create -wal/-shm beside the archive). $(fix ./data)"; fail=1; }
    if [ -e "${db}" ] && { [ ! -r "${db}" ] || [ ! -w "${db}" ]; }; then
        say "${db} is not readable and writable by uid ${uid} — a seeded snapshot keeps the copying user's ownership. $(fix ./data)"; fail=1
    fi
fi
logdir="${MAILVEC_LOG_DIR:-/logs}"
if mounted "${logdir}" && [ ! -w "${logdir}" ]; then
    say "${logdir} is not writable by uid ${uid}; Serilog would fail silently and this service would run with no log files. $(fix './logs/<service>')"; fail=1
fi
mailroot="${Ingest__MaildirRoot:-/mail}"
if mounted /mail && [ -e "${mailroot}" ] && { [ ! -r "${mailroot}" ] || [ ! -x "${mailroot}" ]; }; then
    say "${mailroot} is not readable by uid ${uid}. $(fix ./mail)"; fail=1
fi
for s in /run/secrets/*; do
    [ -e "$s" ] || continue
    [ -r "$s" ] || { say "$s is not readable by uid ${uid}; the key would read as empty. On the host: sudo chown ${uid}:${gid} ./secrets/$(basename "$s") (keep it 0600)"; fail=1; }
done
[ "$fail" -eq 0 ] || exit 1
exec "$@"
EOF
RUN chmod +x /usr/local/bin/mailvec-entrypoint

# Container-shaped defaults; override per-service in compose. Env vars are the
# highest-precedence config source, so these beat the appsettings.json values
# published alongside each binary. MAILVEC_LAUNCHD is deliberately NOT set:
# the Serilog console sink is what feeds `docker logs`.
#
# Parser__Mode=remote is the IMAGE default, not just compose's: the parser
# libraries were stripped from every directory but /app/parse above, so an
# in-process default here would be a container that fails at its first parse.
# HOME=/tmp: the services run as a uid with no passwd entry, so nothing else
# sets HOME, and .NET probes $HOME for per-user state (ASP.NET's DataProtection
# key fallback, $HOME/.dotnet). /tmp is the compose tmpfs — writable, ephemeral.
ENV HOME=/tmp \
    Archive__DatabasePath=/data/archive.sqlite \
    Archive__SqliteVecExtensionPath=./vec0.so \
    Ingest__MaildirRoot=/mail \
    Parser__Mode=remote \
    Parser__Endpoint=http://parse:3400 \
    Mcp__BindAddress=0.0.0.0 \
    Mcp__AttachmentDownloadDir=/data/downloads \
    MAILVEC_LOG_DIR=/logs

EXPOSE 3333
# Non-root by default — see the mbsync stage. The parse service overrides this
# to 65534 in compose; everything else runs as this uid.
USER 10001:10001
ENTRYPOINT ["/usr/local/bin/mailvec-entrypoint"]
CMD ["dotnet", "/app/mcp/Mailvec.Mcp.dll"]
