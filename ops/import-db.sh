#!/usr/bin/env bash
# Install a Mailvec archive snapshot (produced by ops/export-db.sh) on THIS
# machine, replacing the local archive. After this, the indexer re-scans the
# local Maildir and re-points each row's maildir_path to the local files
# (they're host-specific) WITHOUT re-OCRing — already-recovered PDFs are
# extraction_status='ocr', which the OCR pass skips.
#
# What it does:
#   1. Validates the snapshot (smoke-tests it, prints schema/model so you can
#      confirm it matches this machine's embedder config).
#   2. Pauses the DB writers (indexer + embedder) and MCP.
#   3. Backs up the existing archive.sqlite to a timestamped .bak, and removes
#      the stale -wal/-shm sidecars (a leftover WAL applied onto the new main
#      file would corrupt it).
#   4. Moves the snapshot into place and starts the services back up.
#
# PREREQUISITES on THIS (destination) machine, in order:
#   - Run ops/install-all.sh here FIRST. It writes this machine's
#     appsettings.Local.json with THIS machine's user paths. Do NOT copy the
#     source machine's config over — it hardcodes the source user's home dir
#     (e.g. /Users/<them>/...) so nothing would resolve on a different account.
#   - Sync your mail here (mbsync, or copy ~/Mail) BEFORE importing. The
#     snapshot carries the derived data (OCR text, embeddings) but NOT the .eml
#     files; if the Maildir is empty at import time the first indexer scan
#     soft-deletes every migrated message. This script guards against that.
#   The snapshot's stored maildir paths are RELATIVE, so a different username
#   between machines is fine — only the config + Maildir must be local.
#
# Usage:
#   ops/import-db.sh /path/to/snapshot.sqlite
#   ops/import-db.sh ~/mailvec-archive-snapshot.sqlite
#
# Env overrides:
#   MAILVEC_DB   path to archive.sqlite (default: the standard Application Support path)
set -euo pipefail

if [[ $# -ne 1 ]]; then
  sed -n '2,24p' "$0"
  exit 2
fi
SNAP="$1"

DB_DEFAULT="$HOME/Library/Application Support/Mailvec/archive.sqlite"
DB="${MAILVEC_DB:-$DB_DEFAULT}"
DB_DIR="$(dirname "$DB")"

if [[ ! -f "$SNAP" ]]; then
  echo "error: snapshot not found: $SNAP" >&2
  exit 1
fi

echo "==> Validating snapshot"
COUNT="$(sqlite3 "$SNAP" 'SELECT COUNT(*) FROM messages;' 2>/dev/null || echo 0)"
if [[ "$COUNT" -lt 1 ]]; then
  echo "error: snapshot has no messages (or isn't a readable Mailvec DB): $SNAP" >&2
  exit 1
fi
SCHEMA="$(sqlite3 "$SNAP" "SELECT value FROM metadata WHERE key='schema_version';")"
MODEL="$(sqlite3 "$SNAP" "SELECT value FROM metadata WHERE key='embedding_model';")"
DIMS="$(sqlite3 "$SNAP" "SELECT value FROM metadata WHERE key='embedding_dimensions';")"
OCR="$(sqlite3 "$SNAP" "SELECT COUNT(*) FROM attachments WHERE extraction_status='ocr';")"
echo "  messages=$COUNT  ocr_recovered=$OCR  schema=v$SCHEMA  model=$MODEL ($DIMS d)"
echo
echo "  This machine's embedder MUST be configured for $MODEL / ${DIMS}d or it"
echo "  will refuse to start. Ctrl-C now if that's not the case."
echo

# --- Guard: the Maildir must already be on THIS machine -------------------
# The snapshot carries derived data (OCR text, embeddings) but NOT the .eml
# files. If the local Maildir is missing/empty, the first indexer scan after
# import soft-deletes every migrated message (its file looks "gone"). So the
# mail must be synced here first (mbsync, or copy ~/Mail). MaildirRoot comes
# from this machine's config; override with MAILVEC_MAILDIR if needed.
CFG="$HOME/Library/Application Support/Mailvec/appsettings.Local.json"
if [[ ! -f "$CFG" ]]; then
  echo "WARN: no Mailvec config at $CFG." >&2
  echo "      Run ops/install-all.sh on THIS machine first — and do NOT copy the" >&2
  echo "      source machine's appsettings.Local.json (it hardcodes their home dir)." >&2
fi
MAILDIR="${MAILVEC_MAILDIR:-}"
if [[ -z "$MAILDIR" && -f "$CFG" ]]; then
  MAILDIR="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("Ingest",{}).get("MaildirRoot",""))' "$CFG" 2>/dev/null || true)"
fi
MAILDIR="${MAILDIR:-$HOME/Mail}"
MAILDIR="${MAILDIR/#\~/$HOME}"   # expand a leading ~
if [[ -z "$(find "$MAILDIR" \( -path '*/cur/*' -o -path '*/new/*' \) -type f -print -quit 2>/dev/null)" ]]; then
  echo "error: no mail found under the Maildir root ($MAILDIR)." >&2
  echo "       Importing now would make the indexer soft-delete every migrated" >&2
  echo "       message. Sync your mail to this machine FIRST (mbsync, or copy" >&2
  echo "       ~/Mail), then re-run this import." >&2
  echo "       (Escape hatch, if you truly know better: MAILVEC_ALLOW_EMPTY_MAILDIR=1)" >&2
  [[ "${MAILVEC_ALLOW_EMPTY_MAILDIR:-}" == "1" ]] || exit 1
fi
echo "==> Maildir check OK ($MAILDIR)"
echo

UID_NUM="$(id -u)"
DOMAIN="gui/$UID_NUM"
PLIST_DIR="$HOME/Library/LaunchAgents"
WRITERS=(com.mailvec.indexer com.mailvec.embedder com.mailvec.mcp)
PAUSED=()

agent_loaded() { launchctl print "$DOMAIN/$1" >/dev/null 2>&1; }

# bootout is ASYNCHRONOUS — the call returns before launchd finishes tearing
# the service down (install.sh's bootout_label polls for the same reason).
# Swapping the DB file and rm'ing the -wal/-shm sidecars under a still-live
# writer risks corrupting the backup mid-checkpoint or yanking sidecars a
# writer still has open. A graceful .NET shutdown can ride launchd's
# ExitTimeOut (20s default), so allow 30s — and ABORT rather than swap under
# a possibly-live process (the EXIT trap still resumes what we paused).
wait_for_exit() {
  local label="$1"
  local deadline=$(( $(date +%s) + 30 ))
  while launchctl print "$DOMAIN/$label" >/dev/null 2>&1; do
    if (( $(date +%s) >= deadline )); then
      echo "error: $label is still running 30s after bootout — aborting so the DB isn't swapped under a live writer." >&2
      exit 1
    fi
    sleep 0.2
  done
}

resume() {
  for label in "${PAUSED[@]:-}"; do
    [[ -z "$label" ]] && continue
    local plist="$PLIST_DIR/$label.plist"
    if [[ -f "$plist" ]]; then
      launchctl bootstrap "$DOMAIN" "$plist" 2>/dev/null \
        && echo "  resumed $label" \
        || echo "  WARN: could not resume $label" >&2
    fi
  done
}
trap resume EXIT

echo "==> Pausing services"
for label in "${WRITERS[@]}"; do
  if agent_loaded "$label"; then
    launchctl bootout "$DOMAIN/$label" 2>/dev/null || true
    PAUSED+=("$label")
    echo "  paused $label"
  fi
done
# Wait for the paused writers to actually exit (bootout only *requests* the
# teardown) before touching the DB file or its sidecars.
for label in "${PAUSED[@]:-}"; do
  [[ -z "$label" ]] && continue
  wait_for_exit "$label"
done

STAMP="$(date +%Y%m%d-%H%M%S)"
if [[ -f "$DB" ]]; then
  echo "==> Backing up existing archive -> $DB.bak.$STAMP"
  mv "$DB" "$DB.bak.$STAMP"
  # Each .bak is a full-PII copy; tighten perms (mv preserves the original's,
  # typically 0644) and prune so they don't pile up unbounded.
  chmod 600 "$DB.bak.$STAMP" 2>/dev/null || true
  KEEP=3
  shopt -s nullglob
  BAKS=("$DB".bak.*)   # array glob: safe with spaces in the path; timestamp names sort chronologically
  shopt -u nullglob
  if (( ${#BAKS[@]} > KEEP )); then
    PRUNE=$(( ${#BAKS[@]} - KEEP ))
    i=0
    for old in "${BAKS[@]}"; do
      (( i < PRUNE )) || break
      echo "  pruning old backup $(basename "$old")"
      rm -f "$old"
      i=$(( i + 1 ))
    done
  fi
fi
echo "==> Removing stale WAL/SHM sidecars"
rm -f "$DB-wal" "$DB-shm"

echo "==> Installing snapshot"
mkdir -p "$DB_DIR"
cp "$SNAP" "$DB"
# Restrict the freshly-copied archive to the owner (cp inherits the umask). The
# service self-heals this on next open too, but do it here so the full-PII file
# is never briefly world-readable between import and first service start.
chmod 600 "$DB" 2>/dev/null || true

# Rebuild the denormalized messages.attachment_text (the 6th messages_fts column)
# from the persisted attachments rows, which still carry OCR-recovered text. This
# is defense-in-depth: an older indexer binary on this machine re-derives
# attachment_text from a fresh .eml parse on its first re-point rescan, and a
# scanned PDF parses back as 'no_text' (empty) — silently wiping OCR text from
# keyword/FTS search. The UPDATE fires messages_au, which resyncs messages_fts.
# Harmless (idempotent) when the snapshot is already consistent.
echo "==> Rebuilding attachment_text (FTS) from persisted extraction rows"
FIXED="$(sqlite3 "$DB" "
  UPDATE messages
  SET attachment_text = (
      SELECT group_concat(extracted_text, ' ')
      FROM attachments
      WHERE attachments.message_id = messages.id
        AND attachments.extracted_text IS NOT NULL
        AND length(attachments.extracted_text) > 0
  )
  WHERE id IN (
      SELECT DISTINCT message_id FROM attachments
      WHERE extracted_text IS NOT NULL AND length(extracted_text) > 0
  );
  SELECT changes();")"
echo "  refreshed attachment_text for $FIXED message(s)"

echo
echo "Snapshot installed at: $DB"
echo "Services resuming. Then verify with:"
echo "  mailvec status     # expect ~$OCR OCR recovered, 0 pending, coverage near 100%"
echo "  mailvec doctor"
echo
echo "The first indexer pass will re-point Maildir paths to this machine's files"
echo "and embed anything genuinely new. It will NOT re-OCR the recovered PDFs."
