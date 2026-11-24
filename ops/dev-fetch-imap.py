#!/usr/bin/env python3
"""
Dev-only IMAP fetcher. Pulls the last N days of INBOX from Fastmail (or any
IMAP server) and writes each message as an .eml file into a Maildir layout
the Mailvec indexer can read. Intended for testing the indexer against real
mail volume *without* setting up mbsync. mbsync is still the production path.

Required env vars:
    FASTMAIL_USER          your full email address
    FASTMAIL_APP_PASSWORD  generate at https://app.fastmail.com/settings/security/devicekeys

Optional env vars:
    MAILDIR_ROOT  default: ~/mailvec-test/Mail
    SINCE_DAYS    default: 7
    IMAP_HOST     default: imap.fastmail.com
    IMAP_FOLDER   default: INBOX

Filenames use the IMAP UID, so re-running this script is idempotent — already
fetched messages are skipped.
"""
import email
import imaplib
import os
import ssl
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path


def main() -> int:
    user = os.environ.get("FASTMAIL_USER")
    password = os.environ.get("FASTMAIL_APP_PASSWORD")
    if not user or not password:
        print("Set FASTMAIL_USER and FASTMAIL_APP_PASSWORD env vars.", file=sys.stderr)
        print("App password: https://app.fastmail.com/settings/security/devicekeys", file=sys.stderr)
        return 1

    root = Path(os.environ.get("MAILDIR_ROOT", "~/mailvec-test/Mail")).expanduser()
    since_days = int(os.environ.get("SINCE_DAYS", "7"))
    host = os.environ.get("IMAP_HOST", "imap.fastmail.com")
    folder = os.environ.get("IMAP_FOLDER", "INBOX")

    # Real mail lands here: owner-only files and directories.
    os.umask(0o077)
    cur = root / folder / "cur"
    cur.mkdir(parents=True, exist_ok=True)

    cutoff = (datetime.now(timezone.utc) - timedelta(days=since_days)).strftime("%d-%b-%Y")
    print(f"Connecting to {host} as {user}")
    print(f"Fetching {folder} since {cutoff} -> {cur}")

    existing_uids = {p.name.split(".")[0] for p in cur.iterdir() if p.is_file()}

    # imaplib's default context verifies nothing (CERT_NONE, no hostname
    # check); the login below would hand the password to whoever answers.
    with imaplib.IMAP4_SSL(host, 993, ssl_context=ssl.create_default_context()) as M:
        M.login(user, password)
        M.select(folder, readonly=True)

        typ, data = M.uid("SEARCH", None, f'(SINCE "{cutoff}")')
        if typ != "OK":
            print(f"SEARCH failed: {data}", file=sys.stderr)
            return 1

        uids = data[0].split()
        # The UID becomes a file name, and it is the server's string.
        new_uids = [u for u in uids if u.isdigit() and u.decode() not in existing_uids]
        print(f"Server reports {len(uids)} messages; {len(new_uids)} new since last run.")

        for i, uid in enumerate(new_uids, 1):
            typ, msg_data = M.uid("FETCH", uid, "(RFC822)")
            if typ != "OK" or not msg_data or not msg_data[0]:
                print(f"  fetch {uid.decode()} failed: {typ}", file=sys.stderr)
                continue
            raw = msg_data[0][1]
            # "<uid>.imapfetch:2,S" mimics a Maildir cur/ filename with the Seen flag.
            (cur / f"{uid.decode()}.imapfetch:2,S").write_bytes(raw)
            if i % 25 == 0:
                print(f"  fetched {i}/{len(new_uids)}")

        print(f"Wrote {len(new_uids)} new messages to {cur}")
        print(f"Total in {folder}/cur now: {sum(1 for _ in cur.iterdir())}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
