#!/usr/bin/env bash
# Refuse to install or redeploy the launchd agents on a machine whose archive
# is a FROZEN CORPUS for eval work.
#
# Why this is a script and not a paragraph. CLAUDE.md has warned about this
# since the 2026-07-16 decommission, and the agents have been reinstalled
# twice anyway — once on 2026-08-04 by an agent "just testing that it works"
# (~136 messages ingested before it was caught), and again by a non-Claude
# agent that never read CLAUDE.md at all, because nothing told it to. A
# warning only reaches a reader who reads it. This reaches anyone who runs
# the command.
#
# What it protects. Installing the agents starts mbsync and the indexer, so
# the corpus stops being the one `baselines/` was measured against. Every
# subsequent eval comparison then drifts against a moving target, silently —
# no error, no log line, just numbers that no longer mean what they say.
# There is no "I'll put it back afterwards": the ingest IS the damage.
#
# The marker is MACHINE-LOCAL, deliberately, and must never be committed.
# This same repo is cloned on the Proxmox deployment VM, where installing is
# the whole point. "This machine holds a frozen corpus" is a fact about a
# machine, not about the source tree, so a tracked flag would be wrong in
# both directions — it would brick the real install and would not survive a
# fresh clone here anyway.
#
# Uninstall is deliberately NOT guarded. `ops/install.sh --uninstall` is the
# documented REMEDY when agents are found running; blocking it would leave
# the caller holding the damage with the fix refused.

MAILVEC_FROZEN_MARKER="$HOME/Library/Application Support/Mailvec/.frozen-corpus"

# require_unfrozen <command-description>
#
# Exits 1 with an operator-facing explanation when the marker is present.
# Callers pass what they were about to do so the message names it.
require_unfrozen() {
    local what="${1:-This command}"

    [[ -f "$MAILVEC_FROZEN_MARKER" ]] || return 0

    # An explicit, loud override rather than "delete the marker to proceed":
    # deleting it is permanent and easy to forget to undo, while an env var
    # lasts exactly one command and leaves the protection in place.
    if [[ "${MAILVEC_ALLOW_FROZEN:-0}" == "1" ]]; then
        echo "frozen-corpus guard: OVERRIDDEN via MAILVEC_ALLOW_FROZEN=1 — proceeding with $what." >&2
        echo "  The archive will start moving. Re-read the marker file before you rely on any eval number." >&2
        return 0
    fi

    cat >&2 <<EOF

⛔ REFUSING: $what

This machine holds a FROZEN CORPUS. Marker:
  $MAILVEC_FROZEN_MARKER

Installing or redeploying the agents starts mbsync and the indexer, so the
archive stops being the one baselines/ was measured against — and every eval
comparison after that drifts silently against a moving target. The ingest is
the damage; there is no undo that restores the measurement.

To run service code here WITHOUT agents (this is the supported path):
  dotnet run --project src/Mailvec.<svc>

If agents are already installed and you are cleaning up, uninstall is allowed
and is the right move (it is not blocked by this guard):
  ops/install.sh --uninstall

If you genuinely intend to un-freeze this machine, read the marker file first
(it says why it exists), then either delete it or run once with:
  MAILVEC_ALLOW_FROZEN=1 <your command>

Background: CLAUDE.md / AGENTS.md "frozen corpus", and
docs/contributing/local-dev-dataset.md.

EOF
    exit 1
}
