"""
Tests for the mbsync sidecar's optional IMAP IDLE support: the sync loop (the
mbsync-loop heredoc in the Dockerfile), the goimapnotify config it generates
from mbsyncrc, and the supervisor that keeps goimapnotify running.

Run with: python3 -m unittest discover -s ops/tests -v

The loop is extracted from the Dockerfile and run under /bin/sh with a fake
`mbsync` (logs each start) and a fake `goimapnotify` on PATH. The fake reads
the generated config the way goimapnotify does for the fields we rely on, and
runs the generated onNewMail command through `sh -c`, as goimapnotify does,
so the wake path is exercised end to end.

What these tests do NOT cover is goimapnotify itself. 2.5.4 (the version
Alpine 3.24 packages) was checked by hand against a fake TLS IMAP server on
2026-10-03: the generated config shape connects with implicit TLS and
certificate verification, EXAMINEs (read-only) a folder whose name has a
space, runs passwordCMD, and runs onNewMail within ~1s of `* n EXISTS`. A
rejected login, a missing folder, and an unreachable server each exit 1.
Re-check those if the package version moves.
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

FAKE_GOIMAPNOTIFY = r'''#!/usr/bin/env python3
# Stand-in for goimapnotify 2.5.4. Like the real binary it runs every box's
# onNewMail once right after "connecting" (its "fake IMAP Event for first time
# sync", watch.go), concurrently, through `sh -c`. FAKE_GIN_MODE: wake (the
# default) then runs one again two seconds in, as a real arrival; idle never
# does; fail exits 1 at once, before any hook, like a rejected login.
import os, re, shutil, subprocess, sys, time
conf = sys.argv[sys.argv.index("-conf") + 1]
shutil.copy(conf, os.environ["FAKE_GIN_CAPTURE"])
mode = os.environ.get("FAKE_GIN_MODE", "wake")
if mode == "fail":
    sys.exit(1)
hooks = [h.replace("''", "'")
         for h in re.findall(r"^\s+onNewMail: '((?:[^']|'')*)'$", open(conf).read(), re.M)]
for proc in [subprocess.Popen(["sh", "-c", h]) for h in hooks]:
    proc.wait()
if mode == "wake":
    time.sleep(2)
    subprocess.run(["sh", "-c", hooks[0]], check=True)
time.sleep(600)
'''


def extract_loop():
    """The mbsync-loop script, exactly as the Dockerfile writes it."""
    text = (REPO / "Dockerfile").read_text()
    m = re.search(r"RUN cat <<'EOF' > /usr/local/bin/mbsync-loop\n(.*?)\nEOF\n", text, re.S)
    assert m, "mbsync-loop heredoc not found in the Dockerfile"
    return m.group(1)


def yaml_value(config, key):
    """The single-quoted scalar for the first `key:` line, unescaped."""
    m = re.search(rf"^\s+{key}: '((?:[^']|'')*)'$", config, re.M)
    return m.group(1).replace("''", "'") if m else None


@unittest.skipUnless(os.name == "posix", "the loop is a POSIX sh script")
class LoopTests(unittest.TestCase):

    def setUp(self):
        self.dir = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir, True)
        self.bin = self.dir / "bin"
        self.bin.mkdir()
        (self.dir / "mail").mkdir()
        self.config = self.dir / "mbsyncrc"
        shutil.copy(REPO / "ops" / "mbsyncrc.container.example", self.config)
        self.starts = self.dir / "starts"
        self.capture = self.dir / "captured.yaml"
        self.write_exe(self.bin / "mbsync", f'#!/bin/sh\ndate +%s.%N >> "{self.starts}"\nexit 0\n')
        self.write_exe(self.bin / "goimapnotify", FAKE_GOIMAPNOTIFY)
        self.loop = self.dir / "mbsync-loop"
        self.write_exe(self.loop, extract_loop())

    @staticmethod
    def write_exe(path, body):
        path.write_text(body)
        path.chmod(0o755)

    def run_loop(self, seconds, **env):
        environment = {**os.environ,
                       "PATH": f"{self.bin}{os.pathsep}{os.environ['PATH']}",
                       "MBSYNC_CONFIG": str(self.config),
                       "MBSYNC_MAILDIR": str(self.dir / "mail" / "Fastmail"),
                       "MBSYNC_WAKE_FILE": str(self.dir / "wake"),
                       "FAKE_GIN_CAPTURE": str(self.capture),
                       **env}
        # stderr to a file, not a pipe: the beater's in-flight `sleep 60`
        # outlives the loop (as it does in the container, where PID 1's exit
        # takes it down) and would hold a pipe open.
        log_path = self.dir / "stderr"
        with open(log_path, "wb") as log_file:
            proc = subprocess.Popen(["/bin/sh", str(self.loop)], env=environment,
                                    stdout=subprocess.DEVNULL, stderr=log_file,
                                    start_new_session=True)
        self.addCleanup(self.kill_group, proc)
        time.sleep(seconds)
        stopped_at = time.monotonic()
        proc.send_signal(signal.SIGTERM)
        proc.wait(timeout=20)
        self.stop_seconds = time.monotonic() - stopped_at
        self.kill_group(proc)
        starts = self.starts.read_text().split() if self.starts.exists() else []
        return [float(s) for s in starts], log_path.read_text()

    @staticmethod
    def kill_group(proc):
        # Sweeps up whatever the loop's TERM trap left mid-exit (its beater,
        # the fake watcher, an in-flight sleep). macOS answers a group signal
        # with EPERM while any member is still exiting, which is exactly the
        # moment this runs; Linux doesn't. Retry briefly, then stop: nothing
        # has been observed to survive it, and raising here turned a passing
        # test into an error on every Mac.
        for _ in range(40):
            try:
                os.killpg(proc.pid, signal.SIGKILL)
                return
            except ProcessLookupError:
                return
            except PermissionError:
                time.sleep(0.05)

    def captured(self):
        self.assertTrue(self.capture.exists(), "goimapnotify was never started")
        return self.capture.read_text()

    # Timing assertions here say "a wake, not the timer": the loop is run with
    # a 60s interval, and a bound of 30s is still half of it while leaving a
    # loaded CI runner room (each step forks sleep/date/awk, and the fake
    # watcher is a Python start-up). Tight bounds flaked.
    NOT_THE_TIMER = 30

    # ---------- waking the loop ----------

    def test_new_mail_starts_the_next_sync_early(self):
        starts, log = self.run_loop(10, MBSYNC_INTERVAL_SECONDS="60",
                                    MBSYNC_IDLE_FOLDERS="INBOX,Junk Mail")
        # Exactly two: the first sync, then the one the real wake triggered.
        # The watcher's two start-up events (one per folder) add none.
        self.assertEqual(len(starts), 2, log)
        # Wake at ~2s, held until the 5s gap floor, never the 60s interval.
        self.assertGreaterEqual(starts[1] - starts[0], 4.5, log)
        self.assertLess(starts[1] - starts[0], self.NOT_THE_TIMER, log)
        self.assertIn("IDLE enabled for: INBOX,Junk Mail", log)
        self.assertLess(self.stop_seconds, 10, "TERM must still stop the loop promptly")

    def test_the_watchers_start_up_events_do_not_trigger_a_sync(self):
        # goimapnotify runs onNewMail once per folder on every start. Taken as
        # a wake, every container start and every supervisor restart would be
        # followed by a pointless `mbsync -a` five seconds after the real one.
        starts, log = self.run_loop(10, MBSYNC_INTERVAL_SECONDS="60",
                                    MBSYNC_IDLE_FOLDERS="INBOX,Junk Mail,Archive",
                                    FAKE_GIN_MODE="idle")
        self.assertEqual(len(starts), 1, log)
        self.assertEqual(self.captured().count("onNewMail:"), 3)
        # The hooks ran (the fake runs them before idling) and each claimed its
        # token rather than touching the wake file.
        self.assertEqual(list(self.dir.glob("wake.start.*")), [])
        self.assertFalse((self.dir / "wake").exists())

    def test_mail_signalled_during_a_sync_triggers_the_next_one(self):
        # The wake file is cleared BEFORE a sync starts, so a signal that lands
        # while mbsync is running survives it. Clearing it after the sync
        # instead would lose that mail until the 60s timer.
        self.write_exe(self.bin / "mbsync",
                       f'#!/bin/sh\n[ -e "{self.starts}" ] || : > "$MBSYNC_WAKE_FILE"\n'
                       f'date +%s.%N >> "{self.starts}"\nexit 0\n')
        starts, log = self.run_loop(10, MBSYNC_INTERVAL_SECONDS="60", MBSYNC_IDLE_FOLDERS="INBOX",
                                    FAKE_GIN_MODE="idle")
        self.assertEqual(len(starts), 2, log)
        self.assertLess(starts[1] - starts[0], self.NOT_THE_TIMER, log)

    def test_without_folders_the_watcher_never_starts(self):
        starts, log = self.run_loop(4, MBSYNC_INTERVAL_SECONDS="60")
        self.assertEqual(len(starts), 1, log)
        self.assertNotIn("IDLE", log)
        self.assertFalse(self.capture.exists())
        self.assertLess(self.stop_seconds, 10)

    def test_a_failing_watcher_backs_off_and_the_timer_keeps_syncing(self):
        starts, log = self.run_loop(6, MBSYNC_INTERVAL_SECONDS="2", MBSYNC_IDLE_FOLDERS="INBOX",
                                    FAKE_GIN_MODE="fail")
        self.assertIn("IDLE watcher exited (status 1)", log)
        self.assertIn("restarting in 30s", log)
        self.assertGreaterEqual(len(starts), 3, log)

    # ---------- the generated goimapnotify config ----------

    def test_config_comes_from_the_first_imap_account_with_implicit_tls(self):
        self.run_loop(3, MBSYNC_IDLE_FOLDERS="INBOX, Junk Mail ,Bob's Mail", FAKE_GIN_MODE="idle")
        config = self.captured()
        self.assertEqual(yaml_value(config, "host"), "imap.fastmail.com")
        self.assertRegex(config, r"(?m)^    port: 993$")
        self.assertRegex(config, r"(?m)^    tls: true$")
        self.assertRegex(config, r"(?m)^      rejectUnauthorized: true$")
        self.assertRegex(config, r"(?m)^      starttls: false$")
        self.assertEqual(yaml_value(config, "username"), "you@fastmail.com")
        self.assertEqual(yaml_value(config, "passwordCMD"), "cat /run/secrets/fastmail_password")
        boxes = [b.replace("''", "'") for b in re.findall(r"(?m)^\s+mailbox: '((?:[^']|'')*)'$", config)]
        self.assertEqual(boxes, ["INBOX", "Junk Mail", "Bob's Mail"])
        self.assertEqual(config.count("onNewMail:"), 3)
        self.assertRegex(config, r"(?m)^    xoAuth2: false$")
        hook = yaml_value(config, "onNewMail")
        self.assertIn(f"touch '{self.dir / 'wake'}'", hook)
        self.assertIn(f"'{self.dir / 'wake'}'.start.*", hook, "the start-up token claim")
        # Never flag-change or deletion hooks: those wait for the timer.
        self.assertNotIn("onChangedMail", config)
        self.assertNotIn("onDeletedMail", config)
        self.assertEqual((self.dir / "mbsync-idle.yaml").stat().st_mode & 0o077, 0,
                         "the config can hold a password; it must be owner-only")

    def test_a_literal_password_and_quoted_values_survive(self):
        self.config.write_text(
            "IMAPAccount first\nHost mail.example\nPort 9993\nUser \"me@example.com\"\n"
            "Pass \"it's \\\"quoted\\\"\"\nTLSType IMAPS\n\n"
            "IMAPStore first-remote\nAccount first\n\n"
            "IMAPAccount second\nHost other.example\nUser x\nPass y\nTLSType IMAPS\n")
        self.run_loop(3, MBSYNC_IDLE_FOLDERS="INBOX", FAKE_GIN_MODE="idle")
        config = self.captured()
        self.assertEqual(yaml_value(config, "host"), "mail.example")
        self.assertRegex(config, r"(?m)^    port: 9993$")
        self.assertEqual(yaml_value(config, "username"), "me@example.com")
        self.assertEqual(yaml_value(config, "password"), 'it\'s "quoted"')
        self.assertIsNone(yaml_value(config, "passwordCMD"))

    def test_authmechs_xoauth2_turns_on_the_watchers_oauth_login(self):
        # isync hands the PassCmd output to the server as an OAuth2 token when
        # AuthMechs says so; goimapnotify only does with xoAuth2 true. Without
        # it the token goes out as a LOGIN password and the watcher exits on
        # the refusal at every backoff, with nothing in the log saying why.
        self.config.write_text("IMAPAccount g\nHost imap.gmail.com\nUser me@gmail.com\n"
                               "AuthMechs xoauth2\nPassCmd \"oauth2ms\"\nTLSType IMAPS\n")
        self.run_loop(3, MBSYNC_IDLE_FOLDERS="INBOX", FAKE_GIN_MODE="idle")
        config = self.captured()
        self.assertRegex(config, r"(?m)^    xoAuth2: true$")
        self.assertEqual(yaml_value(config, "passwordCMD"), "oauth2ms")

    def test_xoauth2_with_a_literal_pass_disables_idle_with_a_reason(self):
        self.config.write_text("IMAPAccount g\nHost imap.gmail.com\nUser me@gmail.com\n"
                               "AuthMechs XOAUTH2\nPass token\nTLSType IMAPS\n")
        starts, log = self.run_loop(3, MBSYNC_INTERVAL_SECONDS="60", MBSYNC_IDLE_FOLDERS="INBOX")
        self.assertIn("IDLE disabled: AuthMechs XOAUTH2 needs a PassCmd", log)
        self.assertFalse(self.capture.exists())
        self.assertEqual(len(starts), 1, "syncing must carry on without IDLE")

    def test_a_plus_prefixed_passcmd_loses_the_plus_however_it_is_quoted(self):
        # isync strips the + after unquoting, so `PassCmd "+gpg ..."` is the
        # form its own parser accepts; stripping before the unquote handed
        # goimapnotify the literal command `+gpg ...` and an empty password.
        for line, want in (('PassCmd "+gpg -q -d /run/secrets/pass.gpg"', "gpg -q -d /run/secrets/pass.gpg"),
                           ("PassCmd +cat /run/secrets/pass", "cat /run/secrets/pass")):
            self.config.write_text(f"IMAPAccount a\nHost h\nUser u\n{line}\nTLSType IMAPS\n")
            self.capture.unlink(missing_ok=True)
            self.run_loop(3, MBSYNC_IDLE_FOLDERS="INBOX", FAKE_GIN_MODE="idle")
            self.assertEqual(yaml_value(self.captured(), "passwordCMD"), want, line)

    def test_anything_but_implicit_tls_disables_idle(self):
        # isync's default TLSType is STARTTLS, so an absent line is refused too.
        for tls in ("TLSType STARTTLS\n", ""):
            self.config.write_text(f"IMAPAccount a\nHost h\nUser u\nPass p\n{tls}")
            self.capture.unlink(missing_ok=True)
            starts, log = self.run_loop(3, MBSYNC_INTERVAL_SECONDS="60", MBSYNC_IDLE_FOLDERS="INBOX")
            self.assertIn("IDLE disabled: it needs TLSType IMAPS", log)
            self.assertFalse(self.capture.exists())
            self.assertGreaterEqual(len(starts), 1, "syncing must carry on without IDLE")

    def test_too_many_folders_disables_idle(self):
        folders = ",".join(f"f{i}" for i in range(11))
        starts, log = self.run_loop(3, MBSYNC_IDLE_FOLDERS=folders)
        self.assertIn("names 11 folders; the limit is 10", log)
        self.assertFalse(self.capture.exists())

    def test_folder_names_are_not_globbed(self):
        self.run_loop(3, MBSYNC_IDLE_FOLDERS="*", FAKE_GIN_MODE="idle")
        self.assertEqual(yaml_value(self.captured(), "mailbox"), "*")


if __name__ == "__main__":
    unittest.main()
