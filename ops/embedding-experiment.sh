#!/usr/bin/env bash
# Run one embedding-model experiment end to end against a COPY of an archive:
#
#   copy → baseline eval → switch-model → re-embed → VACUUM → eval vs baseline
#
# The runbook in docs/contributing/embedding-experiments.md used to be eight
# manual steps, and most of its "caveats" section was steps someone had
# forgotten: model and dims set separately, the query prefix left off, timing
# measured before the VACUUM, and someone watching `mailvec status` to Ctrl-C
# the embedder. This does them in order, unattended, and can be re-run to
# resume a run that was interrupted (a 4B-model re-embed takes many hours).
#
# Usage:
#   ops/embedding-experiment.sh --name gemma2-768 \
#       --profile ops/embedding-profiles/embeddinggemma-2-768.env \
#       --source "$HOME/Library/Application Support/Mailvec/archive.sqlite"
#
# Options:
#   --name NAME        Experiment label; also the work directory's name.
#   --profile FILE     Embedding profile as shell assignments (see
#                      ops/embedding-profiles/). Sourced for every step after
#                      the baseline; may also set Embedder__ChunkSizeTokens etc.
#   --source DB        Archive to copy. Never opened by SQLite: it is cloned
#                      at the file level (APFS clonefile, falling back to a
#                      plain copy) and only the clone is read.
#   --baseline FILE    Diff against an existing eval report instead of
#                      measuring one. Default: measure one on the fresh copy
#                      BEFORE switching models — same corpus, same binaries,
#                      same query set, same machine, so the diff isolates the
#                      model. Requires the shared config to describe the
#                      source database's own model (it refuses otherwise).
#   --queries FILE     Query set (default: mailvec eval's default path).
#   --workdir DIR      Default: ~/Library/Application Support/Mailvec/experiments/NAME
#
# Safety. The only thing this writes is the work directory. It refuses to run
# while any Mailvec launchd agent is loaded (the source might be live, and a
# file-level copy of a database being written is not a database), scrubs
# every inherited NAME__KEY .NET config variable so a stray export can't
# point a step at the real archive, and sets Archive__DatabasePath for every
# step itself. It installs nothing, so it is safe on a frozen-corpus machine
# — running experiments there is the point.
#
# Disk: one copy of the archive plus, transiently, a second during each VACUUM.

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APPDIR="$HOME/Library/Application Support/Mailvec"

NAME="" PROFILE="" SOURCE="" BASELINE="" QUERIES="" WORKDIR=""

die() { echo "embedding-experiment: $*" >&2; exit 1; }
usage() { sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d; s/^# \{0,1\}//'; exit "${1:-0}"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --name)     NAME="${2:?}"; shift 2 ;;
        --profile)  PROFILE="${2:?}"; shift 2 ;;
        --source)   SOURCE="${2:?}"; shift 2 ;;
        --baseline) BASELINE="${2:?}"; shift 2 ;;
        --queries)  QUERIES="${2:?}"; shift 2 ;;
        --workdir)  WORKDIR="${2:?}"; shift 2 ;;
        -h|--help)  usage 0 ;;
        *)          echo "Unknown option: $1" >&2; usage 2 ;;
    esac
done

[[ -n "$NAME" ]] || die "--name is required."
[[ "$NAME" =~ ^[A-Za-z0-9._-]+$ ]] || die "--name may contain only letters, digits, '.', '_' and '-'."
WORKDIR="${WORKDIR:-$APPDIR/experiments/$NAME}"
DB="$WORKDIR/archive.sqlite"
[[ "$DB" != *"'"* ]] || die "The work directory path may not contain a single quote (it is spliced into SQL)."
SAVED_PROFILE="$WORKDIR/profile.env"

# --- preflight -------------------------------------------------------------

command -v dotnet >/dev/null || die "dotnet not found."
command -v sqlite3 >/dev/null || die "sqlite3 not found."

if launchctl list 2>/dev/null | grep -q 'com\.mailvec\.'; then
    die "Mailvec launchd agents are loaded (launchctl list | grep mailvec). The source may be live, and a
file-level copy of a database mid-write is not a database. On a frozen-corpus machine they should not
exist at all — remove them with ops/install.sh --uninstall and say so: the corpus has moved."
fi

# Resume: the work directory remembers its profile; a different one now
# would mix two experiments in one database.
if [[ -f "$SAVED_PROFILE" ]]; then
    if [[ -n "$PROFILE" ]] && ! cmp -s "$PROFILE" "$SAVED_PROFILE"; then
        die "$WORKDIR was started with a different profile. Use another --name, or rm -rf the directory."
    fi
    PROFILE="$SAVED_PROFILE"
    echo "Resuming $NAME in $WORKDIR"
else
    [[ -n "$PROFILE" ]] || die "--profile is required."
    [[ -n "$SOURCE" ]] || die "--source is required."
fi
[[ -f "$PROFILE" ]] || die "Profile $PROFILE not found."
if grep -Eq '^[[:space:]]*(export[[:space:]]+)?Archive__' "$PROFILE"; then
    die "$PROFILE sets Archive__*; the script owns the database path for every step."
fi
grep -Eq '^[[:space:]]*(export[[:space:]]+)?(Embedding__ActiveProfile|Ollama__EmbeddingModel)=' "$PROFILE" \
    || die "$PROFILE sets neither Embedding__ActiveProfile nor Ollama__EmbeddingModel — it would re-embed with the current model."

# Inherited .NET config overrides (NAME__KEY) would silently apply to every
# step — including the baseline, which must see only the shared config.
scrubbed=()
for var in $(compgen -e); do
    # .NET config shape (Section__Key); not macOS's own __CF* variables.
    if [[ "$var" =~ ^[A-Za-z][A-Za-z0-9]*__ ]]; then scrubbed+=("$var"); unset "$var"; fi
done
if [[ ${#scrubbed[@]} -gt 0 ]]; then
    echo "Ignoring inherited config overrides: ${scrubbed[*]}"
fi

mkdir -p "$WORKDIR"
[[ -f "$SAVED_PROFILE" ]] || cp "$PROFILE" "$SAVED_PROFILE"

done_stage() { [[ -f "$WORKDIR/.stage-$1" ]]; }
mark_stage() { date -u +%FT%TZ > "$WORKDIR/.stage-$1"; }
banner() { printf '\n== %s  (%s)\n' "$1" "$(date '+%F %T')"; }

# Every step runs in a subshell with exactly the variables it needs.
CLI_DLL="$WORKDIR/bin/cli/Mailvec.Cli.dll"
EMBEDDER_DIR="$WORKDIR/bin/embedder"

cli_shared() {  # the shared config describes the database (baseline)
    ( export Archive__DatabasePath="$DB"; dotnet "$CLI_DLL" "$@" )
}
cli_profile() {  # the experiment profile describes the database
    ( set -a; source "$SAVED_PROFILE"; set +a
      export Archive__DatabasePath="$DB"; dotnet "$CLI_DLL" "$@" )
}

unembedded() {
    sqlite3 "$DB" "SELECT COUNT(*) FROM messages WHERE embedded_at IS NULL AND deleted_at IS NULL"
}

# --- 1. build --------------------------------------------------------------
# Into the work directory, so a long run keeps the binaries it started with
# whatever happens to the checkout meanwhile.

if ! done_stage build; then
    banner "Building CLI and embedder"
    dotnet build "$REPO/src/Mailvec.Cli" -c Release -o "$WORKDIR/bin/cli" --nologo -v quiet
    dotnet build "$REPO/src/Mailvec.Embedder" -c Release -o "$EMBEDDER_DIR" --nologo -v quiet
    # A developer's per-binary appsettings.Local.json is copied on build; any
    # Ollama:/Embedding: key in it would silently apply to every step here.
    rm -f "$WORKDIR/bin/cli/appsettings.Local.json" "$EMBEDDER_DIR/appsettings.Local.json"
    {
        echo "commit: $(git -C "$REPO" rev-parse HEAD)"
        git -C "$REPO" diff --quiet HEAD 2>/dev/null || echo "dirty: yes (uncommitted changes were built)"
    } > "$WORKDIR/build.txt"
    mark_stage build
fi
[[ -f "$CLI_DLL" ]] || die "Expected $CLI_DLL after build — has the CLI's assembly name changed?"

# --- 2. copy ---------------------------------------------------------------
# Clone (or copy) the main file and any -wal beside it, never touching the
# source with SQLite, then VACUUM INTO the real copy: compact, so baseline
# timing isn't measured on a fragmented file while the experiment's is
# measured on a vacuumed one.

if ! done_stage copy; then
    banner "Copying $SOURCE"
    [[ -f "$SOURCE" ]] || die "Source $SOURCE not found."
    [[ "$(cd "$(dirname "$SOURCE")" && pwd)/$(basename "$SOURCE")" != "$DB" ]] || die "Source and work database are the same file."
    stage="$WORKDIR/stage"
    rm -rf "$stage" "$DB" "$DB-wal" "$DB-shm"
    mkdir -p "$stage"
    for suffix in "" "-wal"; do
        [[ -f "$SOURCE$suffix" ]] || continue
        cp -c "$SOURCE$suffix" "$stage/archive.sqlite$suffix" 2>/dev/null \
            || cp "$SOURCE$suffix" "$stage/archive.sqlite$suffix"
    done
    sqlite3 "$stage/archive.sqlite" "VACUUM INTO '$DB'"
    rm -rf "$stage"
    echo "Copied: $(sqlite3 "$DB" "SELECT COUNT(*) FROM messages WHERE deleted_at IS NULL") messages, $(sqlite3 "$DB" "SELECT value FROM metadata WHERE key = 'embedding_space_id'") ."
    echo "$SOURCE" > "$WORKDIR/source.txt"
    mark_stage copy
fi

# --- 3. baseline -----------------------------------------------------------

eval_args=(--timing)
[[ -n "$QUERIES" ]] && eval_args+=(--queries "$QUERIES")

if [[ -n "$BASELINE" ]]; then
    [[ -f "$BASELINE" ]] || die "Baseline $BASELINE not found."
    BASELINE_REPORT="$BASELINE"
else
    BASELINE_REPORT="$WORKDIR/baseline.json"
    if ! done_stage baseline; then
        banner "Baseline eval on the unswitched copy (shared config's model)"
        cli_shared search --semantic -n 1 -t "warm up the embedding model" >/dev/null \
            || die "Baseline warm-up failed. The shared config must describe the source database's model — or pass --baseline FILE."
        cli_shared eval "${eval_args[@]}" --json "$BASELINE_REPORT" | tee "$WORKDIR/baseline.txt"
        mark_stage baseline
    fi
fi

# --- 4. switch-model -------------------------------------------------------

if ! done_stage switch; then
    banner "switch-model to the experiment profile"
    cli_profile switch-model --yes
    mark_stage switch
fi

# --- 5. re-embed -----------------------------------------------------------
# OCR is off: it would add text the baseline never saw (and call the vision
# model), so the comparison would no longer isolate the embedding model.

if ! done_stage embed; then
    banner "Re-embedding ($(unembedded) messages queued) — log: $WORKDIR/embed.log"
    set +e
    (
        set -a; source "$SAVED_PROFILE"; set +a
        export Archive__DatabasePath="$DB"
        export Embedder__ExitWhenDrained=true Embedder__OcrEnabled=false Embedder__ImageOcrEnabled=false
        # The host's content root is the working directory: run from the
        # build output so it finds its appsettings.json.
        cd "$EMBEDDER_DIR"
        dotnet Mailvec.Embedder.dll
    ) 2>&1 | tee -a "$WORKDIR/embed.log" \
        | grep --line-buffered -vE 'HTTP request|HTTP response|Execution attempt'
    # (The terminal gets progress lines only; embed.log keeps every line.)
    embedder_status="${PIPESTATUS[0]}"
    set -e
    [[ "$embedder_status" == 0 ]] || die "The embedder exited $embedder_status (it gives up after repeated failures —
is the model pulled and Ollama up?). See $WORKDIR/embed.log; re-run to resume."
    left="$(unembedded)"
    if [[ "$left" != "0" ]]; then
        echo "WARNING: $left message(s) still unembedded (quarantined — see embed.log). The eval will say so."
    fi
    mark_stage embed
fi

# --- 6. VACUUM -------------------------------------------------------------
# switch-model's drop + rebuild scatters the new vectors into freed pages;
# unvacuumed, the vec0 scan ran at random-I/O speed (18.8s vs 2.7s per query).

if ! done_stage vacuum; then
    banner "VACUUM"
    rm -f "$DB.vacuumed"
    sqlite3 "$DB" "VACUUM INTO '$DB.vacuumed'"
    rm -f "$DB-wal" "$DB-shm"
    mv "$DB.vacuumed" "$DB"
    mark_stage vacuum
fi

# --- 7. eval ---------------------------------------------------------------

banner "Eval vs $(basename "$BASELINE_REPORT")"
# One throwaway semantic query first: the model load would otherwise land on
# the first measured query's latency.
cli_profile search --semantic -n 1 -t "warm up the embedding model" >/dev/null
cli_profile eval "${eval_args[@]}" --baseline "$BASELINE_REPORT" --json "$WORKDIR/report.json" \
    | tee "$WORKDIR/eval.txt"
mark_stage eval

cat <<EOF

Done. In $WORKDIR:
  report.json   this run (provenance included)     eval.txt   the diff above
  baseline.json the unswitched copy (if measured)  embed.log  embedder output
Delete the directory when finished — it holds a full copy of the archive.
EOF
