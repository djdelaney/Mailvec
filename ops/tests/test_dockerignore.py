"""
The image build stage runs `COPY . .` from the compose directory, which on the
deploy host is this repo clone with the live bind mounts beside it. Anything
compose mounts from the host (mail, the archive, logs, the mark-read queue,
secrets) must therefore be in .dockerignore, or an on-host build copies it into
the build context and the build-stage layers. Each was added by hand once a
review noticed; logs/ waited two reviews. This derives the set from the
compose files instead, so a new bind mount without an exclusion fails here.

Run with: python3 -m unittest discover -s ops/tests -v
"""
import pathlib
import re
import unittest

REPO = pathlib.Path(__file__).resolve().parents[2]


def host_paths():
    """Top-level host paths compose binds or reads secrets from, e.g. {'data', 'logs'}."""
    found = set()
    for compose in REPO.glob("compose*.yml"):
        for line in compose.read_text().splitlines():
            if line.lstrip().startswith("#"):
                continue
            m = re.match(r"\s*(?:-\s*|file:\s*)\./([^:/\s]+)", line)
            if m:
                found.add(m.group(1))
    return found


def ignored():
    lines = (REPO / ".dockerignore").read_text().splitlines()
    return {l.strip().rstrip("/") for l in lines if l.strip() and not l.startswith("#")}


class DockerignoreTests(unittest.TestCase):

    def test_the_compose_files_bind_what_this_test_expects_to_find(self):
        # Guards the parser: an empty or shrunken set would pass the real
        # test below vacuously.
        self.assertTrue({"data", "logs", "mail", "mark-read", "mbsyncrc", "secrets"} <= host_paths())

    def test_every_host_path_compose_mounts_is_kept_out_of_the_build_context(self):
        missing = sorted(host_paths() - ignored())
        self.assertEqual(missing, [], f"add to .dockerignore: {missing}")


if __name__ == "__main__":
    unittest.main()
