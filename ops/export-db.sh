#!/usr/bin/env bash
# Produce a consistent snapshot of the Mailvec SQLite archive so it can be
# cloned to another machine — letting the destination skip re-OCR/re-embed of
# mail it already has (the OCR'd text rides along in the copied DB).
#
# What it does, in order:
#   1. Pauses the DB *writers* (indexer + embedder) and the MCP server, so no
#      process is writing while we snapshot. mbsync is left alone — it writes
#      the Maildir, not the SQLite file.
#   2. Folds the write-ahead log into the main file (`mailvec checkpoint`), so
#      the single archive.sqlite is complete and the -wal/-shm sidecars are
#      irrelevant to the copy.
#   3. Copies *only* archive.sqlite to a snapshot path (never the live -wal —
#      a stale WAL copied onto a fresh main file corrupts it).
#   4. Smoke-tests the snapshot, then resumes whatever services it paused
#      (via an EXIT trap, so they come back even if a step fails).
#   5. Optionally scp's the snapshot to a remote host.
#
# Install the snapshot on the destination with the companion ops/import-db.sh.
#
# Usage:
#   ops/export-db.sh                          # snapshot to ~/mailvec-archive-snapshot.sqlite
#   ops/export-db.sh --out /path/snap.sqlite  # choose the snapshot path
#   ops/export-db.sh --to you@laptop          # also scp it to the host's home dir
#   ops/export-db.sh --to you@laptop:/tmp/    # ...or a specific remote path
#
# Env overrides:
#   MAILVEC_DB   path to archive.sqlite (default: the standard Application Support path)
set -euo pipefail

DB_DEFAULT="$HOME/Library/Application Support/Mailvec/archive.sqlite"
DB="${MAILVEC_DB:-$DB_DEFAULT}"
OUT="$HOME/mailvec-archive-snapshot.sqlite"
TO=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) OUT="$2"; shift 2 ;;
    --to)  TO="$2";  shift 2 ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ ! -f "$DB" ]]; then
  echo "error: archive not found at: $DB" >&2
  echo "       set MAILVEC_DB if it lives elsewhere." >&2
  exit 1
fi

UID_NUM="$(id -u)"
DOMAIN="gui/$UID_NUM"
PLIST_DIR="$HOME/Library/LaunchAgents"
# Order matters on resume only cosmetically; these are the three processes that
# open a writable connection to the archive. mbsync is intentionally excluded.
WRITERS=(com.mailvec.indexer com.mailvec.embedder com.mailvec.mcp)
PAUSED=()

mailvec_bin() {
  if command -v mailvec >/dev/null 2>&1; then echo "mailvec"; return; fi
  echo "$HOME/.local/bin/mailvec"
}

agent_loaded() { launchctl print "$DOMAIN/$1" >/dev/null 2>&1; }

resume() {
  # Bring back exactly the agents we paused (so we never bootstrap one the
  # user had intentionally unloaded). Best-effort: a failed bootstrap prints
  # but doesn't abort the resume of the others.
  for label in "${PAUSED[@]:-}"; do
    [[ -z "$label" ]] && continue
    local plist="$PLIST_DIR/$label.plist"
    if [[ -f "$plist" ]]; then
      launchctl bootstrap "$DOMAIN" "$plist" 2>/dev/null \
        && echo "  resumed $label" \
        || echo "  WARN: could not resume $label (bootstrap $plist)" >&2
    fi
  done
}
trap resume EXIT

echo "==> Pausing DB writers"
for label in "${WRITERS[@]}"; do
  if agent_loaded "$label"; then
    launchctl bootout "$DOMAIN/$label" 2>/dev/null || true
    PAUSED+=("$label")
    echo "  paused $label"
  else
    echo "  (skipped $label — not loaded)"
  fi
done
# Give in-flight writes a moment to flush before checkpointing.
sleep 1

echo "==> Checkpointing WAL into archive.sqlite"
MAILVEC="$(mailvec_bin)"
if ! "$MAILVEC" checkpoint; then
  echo "error: 'mailvec checkpoint' failed — aborting before copy." >&2
  exit 1
fi

echo "==> Copying snapshot"
echo "  from: $DB"
echo "  to:   $OUT"
cp "$DB" "$OUT"

echo "==> Validating snapshot"
# Plain SELECT on messages doesn't touch the vec0 virtual table, so it works
# without the sqlite-vec extension loaded.
COUNT="$(sqlite3 "$OUT" 'SELECT COUNT(*) FROM messages;')"
OCR="$(sqlite3 "$OUT" "SELECT COUNT(*) FROM attachments WHERE extraction_status='ocr';")"
SCHEMA="$(sqlite3 "$OUT" "SELECT value FROM metadata WHERE key='schema_version';")"
MODEL="$(sqlite3 "$OUT" "SELECT value FROM metadata WHERE key='embedding_model';")"
echo "  messages=$COUNT  ocr_recovered=$OCR  schema=v$SCHEMA  embedding_model=$MODEL"
if [[ "$COUNT" -lt 1 ]]; then
  echo "error: snapshot has no messages — something went wrong." >&2
  exit 1
fi

SNAP_BYTES="$(stat -f%z "$OUT" 2>/dev/null || echo '?')"
echo "  size=${SNAP_BYTES} bytes"

if [[ -n "$TO" ]]; then
  echo "==> Transferring to $TO"
  scp "$OUT" "$TO"
fi

echo
echo "Snapshot ready: $OUT"
echo "Next, on the destination machine:"
echo "  ops/import-db.sh <snapshot.sqlite>"
echo "  (then: mailvec status && mailvec doctor)"
echo
echo "Reminder: the destination's embedder config must use the same embedding"
echo "model + dimensions ($MODEL) or it will refuse to start."
