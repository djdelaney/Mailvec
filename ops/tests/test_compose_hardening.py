"""
Pins the mbsync sidecar's /tmp to a tmpfs. /tmp is its HOME and holds the IMAP
IDLE watcher's generated config, which contains the IMAP password itself when
mbsyncrc uses a literal `Pass`. On the container's writable layer that file
outlived restarts and went along with any `docker commit` or `export`. Nothing
else fails if the tmpfs entry is dropped, because every script still works
with /tmp on the writable layer.

A text scan, so CI needs neither Docker nor PyYAML. `docker compose config`
confirmed the same result when this was added.

Run with: python3 -m unittest discover -s ops/tests -v
"""
import pathlib
import re
import unittest

REPO = pathlib.Path(__file__).resolve().parents[2]


def service_block(name):
    """The non-comment lines of one top-level compose service."""
    lines = (REPO / "compose.yml").read_text().splitlines()
    start = lines.index(f"  {name}:")
    block = []
    for line in lines[start + 1:]:
        if re.match(r"^ {0,2}\S", line):  # the next service, or a top-level key
            break
        if not line.lstrip().startswith("#"):
            block.append(line)
    return block


class ComposeHardeningTests(unittest.TestCase):

    def test_mbsync_keeps_tmp_in_memory_and_executes_nothing_from_it(self):
        block = service_block("mbsync")
        i = next((i for i, l in enumerate(block) if l.strip() == "tmpfs:"), None)
        self.assertIsNotNone(i, "mbsync has no tmpfs")
        entry = block[i + 1].strip()
        self.assertTrue(entry.startswith("- /tmp:"), entry)
        self.assertIn("noexec", entry)
        self.assertIn("nosuid", entry)

    def test_mbsyncs_home_is_the_tmpfs(self):
        # The IDLE config's location is derived from the wake file under /tmp,
        # and HOME=/tmp is where a PassCmd's own state lands. Moving either off
        # /tmp would leave the tmpfs guarding nothing.
        self.assertIn("HOME: /tmp", [l.strip() for l in service_block("mbsync")])
        dockerfile = (REPO / "Dockerfile").read_text()
        self.assertIn(': "${MBSYNC_WAKE_FILE:=/tmp/mbsync-wake}"', dockerfile)
        self.assertIn('IDLE_CONF="$(dirname "${WAKE}")/mbsync-idle.yaml"', dockerfile)


if __name__ == "__main__":
    unittest.main()
