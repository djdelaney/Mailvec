"""
export-db.sh and import-db.sh copy the whole archive: every message, OCR'd
document and embedding, unencrypted. Each `cp` used to create its file at the
umask (0644 on most hosts) and `chmod 600` it on the next line, so a full copy
of the mailbox was world-readable in between. The scripts now set `umask 077`
before creating anything.

This checks the scripts' text rather than running them, because they stop and
start the services. On the frozen-corpus dev box that is exactly what must not
happen (see the frozen-corpus block in CLAUDE.md).

Run with: python3 -m unittest discover -s ops/tests -v
"""
import pathlib
import re
import unittest

REPO = pathlib.Path(__file__).resolve().parents[2]
SCRIPTS = ("ops/export-db.sh", "ops/import-db.sh")
# Commands that create a file or directory in these scripts.
CREATES = re.compile(r"^\s*(cp|mv|mkdir|scp|sqlite3)\b|>\s*\"?\$")


class SnapshotScriptTests(unittest.TestCase):

    def test_the_umask_is_owner_only_before_anything_is_created(self):
        for name in SCRIPTS:
            with self.subTest(script=name):
                lines = [l for l in (REPO / name).read_text().splitlines() if not l.lstrip().startswith("#")]
                umask = next((i for i, l in enumerate(lines) if l.strip() == "umask 077"), None)
                self.assertIsNotNone(umask, f"{name} sets no umask 077")
                first = next(i for i, l in enumerate(lines) if CREATES.search(l))
                self.assertLess(umask, first, f"{name} creates a file before its umask: {lines[first].strip()}")


if __name__ == "__main__":
    unittest.main()
