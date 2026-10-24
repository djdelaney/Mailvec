"""
Tests for the sidecar half of the optional mark-read component: the
mbsync-mark-read script and its wiring into mbsync-loop (both heredocs in the
Dockerfile). The mcp half, and the request/marker formats it writes, are
covered by Mailvec.Mcp.Tests (MarkReadToolTests); the files written here are
in exactly that shape.

Run with: python3 -m unittest discover -s ops/tests -v

What these tests do NOT cover is isync itself. That was checked by hand on
2026-10-24 by running the built mbsync image (Alpine 3.24, isync 1.5.1)
against a local Dovecot 2.4.5 server:
- `Sync Pull PushFlags` pushes a renamed `S`, and later pulls don't revert it;
- `unread` clears it on the server;
- a read made on the server still pulls down;
- plain `Sync Pull` leaves the local `S` in place and never pushes it;
- the real mbsync-loop applied and pushed a queued request in 4 s against a
  600 s interval.

That run is also where isync's `new/<base>:2,` shape for unseen mail turned
up; see the test for it below. Still unchecked: that a provider keeping read
state per email (Fastmail) spreads the flag to the message's other labels.
Re-check all of this when the isync package version moves.
"""
import os
import pathlib
import re
import shutil
import signal
import subprocess
import tempfile
import time
import unittest

REPO = pathlib.Path(__file__).resolve().parents[2]
HEADER = "mailvec-mark-read v1"


def extract(name):
    """A /usr/local/bin script, exactly as the Dockerfile writes it."""
    text = (REPO / "Dockerfile").read_text()
    m = re.search(rf"RUN cat <<'EOF' > /usr/local/bin/{name}\n(.*?)\nEOF\n", text, re.S)
    assert m, f"{name} heredoc not found in the Dockerfile"
    return m.group(1)


def write_exe(path, body):
    path.write_text(body)
    path.chmod(0o755)


@unittest.skipUnless(os.name == "posix", "the scripts are POSIX sh")
class MarkReadScriptTests(unittest.TestCase):

    def setUp(self):
        self.dir = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir, True)
        self.root = self.dir / "mail" / "Fastmail"
        self.queue = self.dir / "mark-read"
        self.queue.mkdir()
        self.config = self.dir / "mbsyncrc"
        self.config.write_text("Channel fastmail\nSync Pull PushFlags\n")
        self.script = self.dir / "mbsync-mark-read"
        write_exe(self.script, extract("mbsync-mark-read"))
        self.folders = "Alerts"

    def run_script(self, *args, **env):
        environment = {**os.environ,
                       "MBSYNC_MARK_READ_QUEUE": str(self.queue),
                       "MBSYNC_MAILDIR": str(self.root),
                       "MBSYNC_CONFIG": str(self.config),
                       "MBSYNC_MARK_READ_FOLDERS": self.folders,
                       **env}
        return subprocess.run(["/bin/sh", str(self.script), *args], env=environment,
                              capture_output=True, text=True, timeout=30)

    def message(self, folder, name, leaf=None):
        """A message file at <root>/<folder>/<cur|new>/<name>; returns its path."""
        leaf = leaf or ("cur" if ":2," in name else "new")
        base = self.root if folder == "." else self.root / folder
        for d in ("cur", "new", "tmp"):
            (base / d).mkdir(parents=True, exist_ok=True)
        path = base / leaf / name
        path.write_text("Subject: x\n\nbody\n")
        return path

    def request(self, *lines, name="20261024T070000000Z-a.req", header=HEADER):
        path = self.queue / name
        path.write_text("".join(f"{l}\n" for l in [header, *lines]))
        return path

    def files(self, folder):
        base = self.root if folder == "." else self.root / folder
        return sorted(p.relative_to(base).as_posix() for p in base.rglob("*") if p.is_file())

    def outcomes(self, name="20261024T070000000Z-a.req"):
        return [l.split("\t")[0] for l in (self.queue / "done" / name).read_text().splitlines()]

    # ---------- preflight ----------

    def test_preflight_passes_with_folders_a_writable_queue_and_pushflags(self):
        r = self.run_script("preflight")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("on for folders: Alerts", r.stderr)

    def test_preflight_requires_an_allowlist_and_never_implies_every_folder(self):
        self.folders = " , "
        (self.queue / ".applier").write_text("stale\n")
        r = self.run_script("preflight")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("MBSYNC_MARK_READ_FOLDERS is empty", r.stderr)
        # A marker left by an earlier run would keep mcp queueing for an
        # applier that is now off.
        self.assertFalse((self.queue / ".applier").exists())

    def test_preflight_refuses_a_missing_queue_directory(self):
        self.queue.rmdir()
        r = self.run_script("preflight")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("not writable", r.stderr)

    def test_preflight_reads_whether_the_sync_line_pushes_flags(self):
        cases = {
            "Sync Pull PushFlags": True,
            "Sync All": True,
            "Sync Full": True,
            "Sync Push": True,          # every push operation
            "Sync Flags": True,         # flags, both directions
            "Sync Push Flags": True,
            "  sync pull pushflags": True,
            "Sync Pull": False,         # the read-only default this component changes
            "Sync Pull Flags": False,   # pulls flags only
            "Sync Pull PushNew": False,
            "# Sync All": False,        # a comment is not a Sync line; the next line decides
        }
        for line, pushes in cases.items():
            with self.subTest(line=line):
                self.config.write_text(f"Channel fastmail\n{line}\nSync Pull\n" if line.startswith("#")
                                       else f"Channel fastmail\n{line}\n")
                r = self.run_script("preflight")
                self.assertEqual(r.returncode == 0, pushes, r.stderr)
                if not pushes:
                    self.assertIn("PushFlags", r.stderr)

    def test_no_sync_line_is_isyncs_default_which_pushes(self):
        self.config.write_text("Channel fastmail\n")
        self.assertEqual(self.run_script("preflight").returncode, 0)

    # ---------- marker ----------

    def test_the_marker_carries_header_time_cadence_and_the_allowlist(self):
        self.folders = "Alerts, Cron Jobs ,*"
        self.assertEqual(self.run_script("marker", "60").returncode, 0)
        lines = (self.queue / ".applier").read_text().splitlines()
        self.assertEqual(lines[0], "mailvec-mark-read-applier v1")
        self.assertRegex(lines[1], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")
        self.assertEqual(lines[2:], ["60", "Alerts", "Cron Jobs", "*"])

    # ---------- apply ----------

    def test_read_adds_only_S_keeps_other_flags_and_sorts_them(self):
        self.message("Alerts", "1.host,U=1:2,RT")
        self.message("Alerts", "2.host,U=2:2,F")
        self.message("Alerts", "3.host,U=3")  # new/
        self.request("read\tAlerts\t1.host,U=1", "read\tAlerts\t2.host,U=2", "read\tAlerts\t3.host,U=3")

        self.assertEqual(self.run_script("apply").returncode, 0)

        self.assertEqual(self.files("Alerts"),
                         ["cur/1.host,U=1:2,RST", "cur/2.host,U=2:2,FS", "cur/3.host,U=3:2,S"])
        self.assertEqual(self.outcomes(), ["ok", "ok", "ok"])
        self.assertFalse((self.queue / "20261024T070000000Z-a.req").exists())

    def test_isyncs_new_files_carry_an_empty_info_suffix_and_are_still_found(self):
        # isync 1.5.1 delivers unseen mail as new/<base>:2, — not the spec's
        # bare new/<base>. Looking only for the bare name made every read of
        # fresh mail `not_found` (caught by the Dovecot round trip).
        self.message("Alerts", "1.host,U=1:2,", leaf="new")
        self.request("read\tAlerts\t1.host,U=1")

        self.run_script("apply")

        self.assertEqual(self.outcomes(), ["ok"])
        self.assertEqual(self.files("Alerts"), ["cur/1.host,U=1:2,S"])

    def test_unread_removes_only_S_and_repeats_are_unchanged(self):
        self.message("Alerts", "1.host,U=1:2,FST")
        self.message("Alerts", "2.host,U=2:2,F")
        self.message("Alerts", "3.host,U=3")
        self.request("unread\tAlerts\t1.host,U=1", "unread\tAlerts\t2.host,U=2", "unread\tAlerts\t3.host,U=3")

        self.run_script("apply")

        self.assertEqual(self.files("Alerts"), ["cur/1.host,U=1:2,FT", "cur/2.host,U=2:2,F", "new/3.host,U=3"])
        self.assertEqual(self.outcomes(), ["ok", "unchanged", "unchanged"])

    def test_requests_apply_in_name_order_so_a_later_unread_wins(self):
        self.message("Alerts", "1.host,U=1:2,")
        self.request("read\tAlerts\t1.host,U=1", name="20261024T070000000Z-a.req")
        self.request("unread\tAlerts\t1.host,U=1", name="20261024T070000001Z-b.req")

        self.run_script("apply")

        self.assertEqual(self.files("Alerts"), ["cur/1.host,U=1:2,"])

    def test_the_sidecars_own_allowlist_decides_not_the_marker(self):
        # mcp can write the marker directory, so a forged marker naming INBOX
        # must change nothing here.
        (self.queue / ".applier").write_text("mailvec-mark-read-applier v1\n2026-10-24T07:00:00Z\n60\nINBOX\n")
        self.message("INBOX", "1.host,U=1:2,")
        self.request("read\tINBOX\t1.host,U=1")

        self.run_script("apply")

        self.assertEqual(self.files("INBOX"), ["cur/1.host,U=1:2,"])
        self.assertEqual(self.outcomes(), ["forbidden_folder"])

    def test_the_maildir_root_is_the_INBOX_folder(self):
        self.folders = "INBOX"
        self.message(".", "1.host,U=1:2,")
        self.request("read\t.\t1.host,U=1")

        self.run_script("apply")

        self.assertEqual(self.outcomes(), ["ok"])
        self.assertTrue((self.root / "cur" / "1.host,U=1:2,S").exists())

    def test_traversal_and_odd_names_are_rejected(self):
        self.folders = "*"
        outside = self.dir / "outside"
        for d in ("cur", "new"):
            (outside / d).mkdir(parents=True)
        (outside / "cur" / "1.host,U=1:2,").write_text("x")
        self.message("Alerts", "1.host,U=1:2,")
        bad = ["read\t../../outside\t1.host,U=1", "read\t/etc\t1.host,U=1", "read\tAlerts/..\t1.host,U=1",
               "read\tAlerts\t../1.host,U=1", "read\tAlerts\t*", "read\tAlerts\t.hidden",
               "delete\tAlerts\t1.host,U=1", "read\tAlerts//\t1.host,U=1"]
        self.request(*bad)

        self.run_script("apply")

        self.assertEqual(self.outcomes(), ["rejected"] * len(bad))
        self.assertTrue((outside / "cur" / "1.host,U=1:2,").exists())
        self.assertEqual(self.files("Alerts"), ["cur/1.host,U=1:2,"])

    def test_a_symlinked_folder_below_the_root_is_not_followed(self):
        self.folders = "*"
        real = self.dir / "elsewhere"
        for d in ("cur", "new"):
            (real / d).mkdir(parents=True)
        (real / "cur" / "1.host,U=1:2,").write_text("x")
        self.root.mkdir(parents=True)
        (self.root / "Linked").symlink_to(real)
        self.request("read\tLinked\t1.host,U=1")

        self.run_script("apply")

        self.assertEqual(self.outcomes(), ["rejected"])
        self.assertTrue((real / "cur" / "1.host,U=1:2,").exists())

    def test_a_malformed_line_rejects_the_whole_file(self):
        self.message("Alerts", "1.host,U=1:2,")
        self.request("read\tAlerts\t1.host,U=1", "read\tAlerts")

        self.run_script("apply")

        self.assertEqual(self.files("Alerts"), ["cur/1.host,U=1:2,"])
        self.assertEqual(self.outcomes(), ["rejected"])

    def test_a_bad_header_rejects_the_whole_file(self):
        self.message("Alerts", "1.host,U=1:2,")
        self.request("read\tAlerts\t1.host,U=1", header="mailvec-mark-read v2")

        self.run_script("apply")

        self.assertEqual(self.files("Alerts"), ["cur/1.host,U=1:2,"])
        self.assertEqual(self.outcomes(), ["rejected"])

    def test_an_expired_request_is_recorded_and_not_applied(self):
        self.message("Alerts", "1.host,U=1:2,")
        req = self.request("read\tAlerts\t1.host,U=1")
        old = time.time() - 16 * 60
        os.utime(req, (old, old))

        self.run_script("apply")

        self.assertEqual(self.files("Alerts"), ["cur/1.host,U=1:2,"])
        self.assertEqual(self.outcomes(), ["expired"])

    def test_a_message_mbsync_has_since_moved_is_not_found(self):
        self.root.joinpath("Alerts", "cur").mkdir(parents=True)
        self.request("read\tAlerts\t1.host,U=1")

        self.run_script("apply")

        self.assertEqual(self.outcomes(), ["not_found"])

    def test_pending_reports_queued_requests_only(self):
        self.assertNotEqual(self.run_script("pending").returncode, 0)
        (self.queue / ".x.tmp").write_text(HEADER + "\n")  # mcp's in-progress write
        self.assertNotEqual(self.run_script("pending").returncode, 0)
        self.request("read\tAlerts\t1.host,U=1")
        self.assertEqual(self.run_script("pending").returncode, 0)


@unittest.skipUnless(os.name == "posix", "the loop is a POSIX sh script")
class LoopWiringTests(unittest.TestCase):
    """mbsync-loop with the component off (the default) and on."""

    def setUp(self):
        self.dir = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir, True)
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        self.root = self.dir / "mail" / "Fastmail"
        self.root.mkdir(parents=True)
        self.queue = self.dir / "mark-read"
        self.queue.mkdir()
        self.config = self.dir / "mbsyncrc"
        shutil.copy(REPO / "ops" / "mbsyncrc.container.example", self.config)
        self.starts = self.dir / "starts"
        self.loop = self.dir / "mbsync-loop"
        write_exe(self.loop, extract("mbsync-loop"))

    def run_loop(self, seconds, **env):
        environment = {**os.environ,
                       "PATH": f"{self.bin}{os.pathsep}{os.environ['PATH']}",
                       "MBSYNC_CONFIG": str(self.config),
                       "MBSYNC_MAILDIR": str(self.root),
                       "MBSYNC_WAKE_FILE": str(self.dir / "wake"),
                       "MBSYNC_INTERVAL_SECONDS": "600",
                       **env}
        log_path = self.dir / "stderr"
        with open(log_path, "wb") as log_file:
            proc = subprocess.Popen(["/bin/sh", str(self.loop)], env=environment,
                                    stdout=subprocess.DEVNULL, stderr=log_file, start_new_session=True)
        self.addCleanup(self.kill_group, proc)
        time.sleep(seconds)
        proc.send_signal(signal.SIGTERM)
        proc.wait(timeout=20)
        self.kill_group(proc)
        starts = self.starts.read_text().splitlines() if self.starts.exists() else []
        return starts, log_path.read_text()

    @staticmethod
    def kill_group(proc):
        for _ in range(40):
            try:
                os.killpg(proc.pid, signal.SIGKILL)
                return
            except ProcessLookupError:
                return
            except PermissionError:
                time.sleep(0.05)

    def test_with_the_queue_unset_the_loop_never_runs_the_component(self):
        calls = self.dir / "calls"
        write_exe(self.bin / "mbsync-mark-read", f'#!/bin/sh\necho "$@" >> "{calls}"\nexit 0\n')
        write_exe(self.bin / "mbsync", f'#!/bin/sh\necho sync >> "{self.starts}"\nexit 0\n')

        starts, log = self.run_loop(3)

        self.assertEqual(len(starts), 1, log)
        self.assertFalse(calls.exists(), "the off path must not call mbsync-mark-read at all")
        self.assertNotIn("mark-read", log)

    def test_a_queued_request_wakes_the_loop_and_is_applied_before_the_sync(self):
        write_exe(self.bin / "mbsync-mark-read", extract("mbsync-mark-read"))
        msg = self.root / "Alerts" / "cur" / "1.host,U=1:2,"
        for d in ("cur", "new", "tmp"):
            (self.root / "Alerts" / d).mkdir(parents=True, exist_ok=True)
        msg.write_text("x")
        self.config.write_text(self.config.read_text().replace("Sync    Pull", "Sync    Pull PushFlags"))
        # Each sync records what it found on disk: the flag must already be
        # there when the sync that pushes it runs.
        write_exe(self.bin / "mbsync",
                  f'#!/bin/sh\nls "{self.root}/Alerts/cur" >> "{self.starts}"\n'
                  f'[ -e "{self.dir}/queued" ] || {{ : > "{self.dir}/queued"; '
                  f'printf "{HEADER}\\nread\\tAlerts\\t1.host,U=1\\n" > "{self.queue}/20261024T070000000Z-a.req"; }}\n'
                  'exit 0\n')

        starts, log = self.run_loop(12, MBSYNC_MARK_READ_QUEUE=str(self.queue),
                                    MBSYNC_MARK_READ_FOLDERS="Alerts")

        # First sync queues the request; the queue wakes the loop well inside
        # the 600 s interval; the second sync sees the renamed file.
        self.assertGreaterEqual(len(starts), 2, log)
        self.assertEqual(starts[0], "1.host,U=1:2,", log)
        self.assertEqual(starts[1], "1.host,U=1:2,S", log)
        self.assertIn("mark-read request queued; syncing now", log)
        self.assertTrue((self.queue / ".applier").exists())

    def test_a_failed_preflight_leaves_mail_syncing_and_says_why(self):
        write_exe(self.bin / "mbsync-mark-read", extract("mbsync-mark-read"))
        write_exe(self.bin / "mbsync", f'#!/bin/sh\necho sync >> "{self.starts}"\nexit 0\n')

        # The example mbsyncrc is `Sync Pull`, which never pushes a flag.
        starts, log = self.run_loop(3, MBSYNC_MARK_READ_QUEUE=str(self.queue),
                                    MBSYNC_MARK_READ_FOLDERS="Alerts")

        self.assertEqual(len(starts), 1, log)
        self.assertIn("mark-read: OFF", log)
        self.assertIn("mark-read is OFF", log)
        self.assertFalse((self.queue / ".applier").exists())


if __name__ == "__main__":
    unittest.main()
