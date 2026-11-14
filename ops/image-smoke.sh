#!/usr/bin/env bash
# Smoke-test built images on the architecture they were built for.
#
#   ops/image-smoke.sh <app-image> [<mbsync-image>]
#
# Run by .github/workflows/ci.yml (every PR, on an amd64 and an arm64 runner)
# and by publish-images.yml (before each architecture's image is pushed), and
# runnable by hand on a Mac against a local `docker compose build`:
#
#   ops/image-smoke.sh mailvec:local mailvec-mbsync:local
#
# The xUnit suites already run on macOS arm64 and Linux x64, but neither runs
# the IMAGE: what the Dockerfile copied, stripped and fetched for its
# TARGETARCH. A per-architecture packaging slip (a missing native, a wrong
# sqlite-vec RID, a parser library left in a privileged directory or deleted
# from parse) passes every test and breaks only in the published image. This
# script starts each binary the way compose does and checks the natives load:
#
#   1. the CLI creates a fresh archive, which loads vec0.so (the schema
#      declares a vec0 virtual table, so a missing or wrong-arch extension
#      fails here);
#   2. the MCP server boots and answers /up;
#   3. the parse service, under compose's hardening, extracts a PDF's text
#      and renders its first page (libpdfium + libSkiaSharp);
#   4. the mbsync image's two binaries run.
#
# Needs docker, curl and python3. Nothing here touches a real archive or
# mailbox; every container is removed on exit.

set -euo pipefail

app="${1:?usage: ops/image-smoke.sh <app-image> [<mbsync-image>]}"
mbsync_image="${2:-}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
fixture="${repo}/tests/Mailvec.Mcp.Tests/Fixtures/text-sample.pdf"
run_id="mailvec-smoke-$$"
containers=()

cleanup() {
    for c in "${containers[@]+"${containers[@]}"}"; do
        docker rm -f "$c" >/dev/null 2>&1 || true
    done
}
trap cleanup EXIT

fail() {
    echo "FAIL $*" >&2
    for c in "${containers[@]+"${containers[@]}"}"; do
        echo "---- docker logs $c (last 40 lines)" >&2
        docker logs --tail 40 "$c" >&2 2>&1 || true
    done
    exit 1
}

# Compose's posture for the .NET services, so a native that only loads with a
# writable rootfs or extra capabilities fails here rather than in a deployment.
hardened=(--read-only --tmpfs /tmp:rw,noexec,nosuid,size=64m --cap-drop ALL --security-opt no-new-privileges:true)

# Wait for an HTTP endpoint on a published port. Any status counts: /up on a
# server with no Ollama behind it may answer 503, which still proves the
# process is up and serving. --noproxy: the runner may export a proxy.
wait_http() {
    local url="$1" name="$2" code
    for _ in $(seq 1 60); do
        code="$(curl -s --noproxy '*' -o /dev/null -w '%{http_code}' "$url" || true)"
        if [ "$code" != "000" ]; then
            echo "$code"
            return 0
        fi
        docker inspect -f '{{.State.Running}}' "$name" 2>/dev/null | grep -q true \
            || fail "$name exited before answering $url"
        sleep 1
    done
    fail "$name did not answer $url within 60 s"
}

published_port() {
    docker port "$1" "$2/tcp" | head -n 1 | sed 's/.*://'
}

arch="$(docker image inspect -f '{{.Architecture}}' "$app")"
echo "== ${app} (linux/${arch})"
if [ -n "${MAILVEC_SMOKE_EXPECT_ARCH:-}" ] && [ "$arch" != "$MAILVEC_SMOKE_EXPECT_ARCH" ]; then
    fail "image is ${arch}, expected ${MAILVEC_SMOKE_EXPECT_ARCH}"
fi

# 1. CLI + sqlite-vec. A fresh archive in the container's /tmp; `status`
# migrates it, and the migration creates the vec0 table.
out="$(docker run --rm "${hardened[@]}" -e Archive__DatabasePath=/tmp/smoke.sqlite "$app" mailvec status 2>&1)" \
    || { echo "$out" >&2; fail "mailvec status (CLI, sqlite-vec)"; }
echo "ok   cli: mailvec status on a fresh archive (vec0 loaded)"

# 2. MCP server boots.
mcp="${run_id}-mcp"
containers+=("$mcp")
docker run -d --name "$mcp" "${hardened[@]}" -p 127.0.0.1::3333 \
    -e Archive__DatabasePath=/tmp/smoke.sqlite "$app" >/dev/null
code="$(wait_http "http://127.0.0.1:$(published_port "$mcp" 3333)/up" "$mcp")"
case "$code" in
    200|503) echo "ok   mcp: /up answered ${code}" ;;
    *) fail "mcp /up answered ${code}" ;;
esac

# 3. parse: the uid, rootfs, capability and PID posture compose gives it.
parse="${run_id}-parse"
containers+=("$parse")
docker run -d --name "$parse" "${hardened[@]}" --user 65534:65534 --pids-limit 128 \
    -e HOME=/tmp -p 127.0.0.1::3400 "$app" dotnet /app/parse/Mailvec.Parse.dll >/dev/null
parse_url="http://127.0.0.1:$(published_port "$parse" 3400)"
code="$(wait_http "${parse_url}/up" "$parse")"
[ "$code" = "200" ] || fail "parse /up answered ${code}"
python3 -I "${repo}/ops/image-smoke-parse.py" "$parse_url" "$fixture" || fail "parse probe"

# 4. mbsync sidecar binaries.
if [ -n "$mbsync_image" ]; then
    march="$(docker image inspect -f '{{.Architecture}}' "$mbsync_image")"
    echo "== ${mbsync_image} (linux/${march})"
    [ "$march" = "$arch" ] || fail "mbsync image is ${march}, app image is ${arch}"
    v="$(docker run --rm --entrypoint mbsync "$mbsync_image" --version 2>&1)" \
        || { echo "$v" >&2; fail "mbsync --version"; }
    echo "ok   mbsync: ${v}"
    # goimapnotify prints usage and exits non-zero without a config; what is
    # checked is that it executes at all (a wrong-arch binary cannot).
    h="$(docker run --rm --entrypoint goimapnotify "$mbsync_image" -h 2>&1 || true)"
    grep -qi 'usage' <<<"$h" || { echo "$h" >&2; fail "goimapnotify did not run"; }
    echo "ok   goimapnotify: runs"
fi

echo "All image smoke checks passed (linux/${arch})."
