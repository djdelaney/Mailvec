"""
Tests for the mbsync sidecar's IMAP IDLE watcher (ops/mbsync-idle.py) and the
loop that it wakes (the mbsync-loop heredoc in the Dockerfile).

Run with: python3 -m unittest discover -s ops/tests -v

Standard library only, like the watcher itself. The protocol tests drive a
scripted IMAP server over a socketpair, so they cover everything except the
TLS wrap; the loop tests run the real heredoc from the Dockerfile with a fake
`mbsync` and a fake watcher on PATH.
"""
import importlib.util
import os
import pathlib
import re
import shutil
import signal
import socket
import subprocess
import tempfile
import threading
import time
import unittest

REPO = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("mbsync_idle", REPO / "ops" / "mbsync-idle.py")
idle = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(idle)


class FakeImapServer(threading.Thread):
    """Plays the server side of one connection. Records every line it receives."""

    def __init__(self, sock, *, idle_supported=True, login_ok=True, delimiter="/",
                 folders=None, during_idle=(), bye_during_idle=False):
        super().__init__(daemon=True)
        self.sock = sock
        self.idle_supported = idle_supported
        self.login_ok = login_ok
        self.delimiter = delimiter
        self.folders = {"INBOX": 3} if folders is None else folders
        self.during_idle = list(during_idle)
        self.bye_during_idle = bye_during_idle
        self.received = []
        self.idles = 0
        self.file = sock.makefile("rb")

    def send(self, line):
        try:
            self.sock.sendall(line.encode() + b"\r\n")
        except OSError:
            pass

    def run(self):
        self.send("* OK fake server ready")
        try:
            for raw in self.file:
                line = raw.decode().rstrip("\r\n")
                self.received.append(line)
                if line == "DONE":
                    self.send(f"{self.idle_tag} OK IDLE terminated")
                    continue
                tag, _, rest = line.partition(" ")
                cmd = rest.split(" ", 1)[0].upper()
                if cmd == "CAPABILITY":
                    self.send("* CAPABILITY IMAP4rev1" + (" IDLE" if self.idle_supported else ""))
                    self.send(f"{tag} OK done")
                elif cmd == "LOGIN":
                    self.send(f"{tag} OK logged in" if self.login_ok
                              else f"{tag} NO [AUTHENTICATIONFAILED] bad credentials")
                elif cmd == "LIST":
                    self.send(f'* LIST (\\Noselect) "{self.delimiter}" ""')
                    self.send(f"{tag} OK done")
                elif cmd == "EXAMINE":
                    name = re.match(r'EXAMINE "(.*)"$', rest).group(1)
                    if name in self.folders:
                        self.send(f"* {self.folders[name]} EXISTS")
                        self.send(f"{tag} OK [READ-ONLY] done")
                    else:
                        self.send(f"{tag} NO no such mailbox")
                elif cmd == "IDLE":
                    self.idles += 1
                    self.idle_tag = tag
                    self.send("+ idling")
                    for event in self.during_idle:
                        self.send(event)
                    self.during_idle = []
                    if self.bye_during_idle:
                        self.send("* BYE going away")
                        self.sock.close()
                        return
                else:
                    self.send(f"{tag} BAD unknown")
        except (OSError, ValueError):
            pass


def connected_pair(**server_options):
    client, server_sock = socket.socketpair()
    server = FakeImapServer(server_sock, **server_options)
    server.start()
    return client, server


ACCOUNT = {"name": "fastmail", "host": "imap.example", "port": 993, "user": "you@example.com",
           "pass": 'pa"ss\\word'}


class ShortRenew:
    """Makes one IDLE round last a fraction of a second instead of ten minutes."""

    def __enter__(self):
        self.saved = idle.IDLE_RENEW_SECONDS
        idle.IDLE_RENEW_SECONDS = 0.3

    def __exit__(self, *exc):
        idle.IDLE_RENEW_SECONDS = self.saved


class ConfigTests(unittest.TestCase):
    def test_reads_the_shipped_container_example(self):
        account = idle.read_account(REPO / "ops" / "mbsyncrc.container.example")
        self.assertEqual(account["host"], "imap.fastmail.com")
        self.assertEqual(account["port"], 993)
        self.assertEqual(account["user"], "you@fastmail.com")
        self.assertEqual(account["passcmd"], "cat /run/secrets/fastmail_password")

    def write_config(self, body):
        f = tempfile.NamedTemporaryFile("w", suffix=".mbsyncrc", delete=False)
        f.write(body)
        f.close()
        self.addCleanup(os.unlink, f.name)
        return f.name

    def test_only_the_first_account_block_is_read(self):
        path = self.write_config(
            "IMAPAccount one\nHost a.example\nUser u1\nPass p1\nTLSType IMAPS\n\n"
            "IMAPStore one-remote\nAccount one\n\n"
            "IMAPAccount two\nHost b.example\nUser u2\nPass p2\nTLSType IMAPS\n")
        account = idle.read_account(path)
        self.assertEqual((account["host"], account["user"]), ("a.example", "u1"))

    def test_anything_but_implicit_tls_is_refused(self):
        # isync's default TLSType is STARTTLS, so an absent line is refused too.
        for tls in ("TLSType STARTTLS\n", "TLSType None\n", ""):
            path = self.write_config(f"IMAPAccount a\nHost h\nUser u\nPass p\n{tls}")
            with self.assertRaises(idle.ConfigError):
                idle.read_account(path)

    def test_folder_list_allows_spaces_and_drops_duplicates(self):
        self.assertEqual(idle.parse_folders(" INBOX , Junk Mail,homelab,INBOX,"),
                         ["INBOX", "Junk Mail", "homelab"])

    def test_folder_list_is_capped(self):
        with self.assertRaises(idle.ConfigError):
            idle.parse_folders(",".join(f"f{i}" for i in range(idle.MAX_FOLDERS + 1)))
        with self.assertRaises(idle.ConfigError):
            idle.parse_folders(" , ")

    def test_passcmd_output_is_the_password_without_its_newline(self):
        self.assertEqual(idle.password_for({"passcmd": "printf 'secret\\n'"}), "secret")
        with self.assertRaises(idle.AuthError):
            idle.password_for({"passcmd": "exit 3"})

    def test_mailbox_names_use_modified_utf7(self):
        self.assertEqual(idle.mutf7("INBOX"), "INBOX")
        self.assertEqual(idle.mutf7("R&D"), "R&-D")
        self.assertEqual(idle.mutf7("Entwürfe"), "Entw&APw-rfe")
        # RFC 3501's own example.
        self.assertEqual(idle.mutf7("~peter/mail/台北/日本語"), "~peter/mail/&U,BTFw-/&ZeVnLIqe-")


class ProtocolTests(unittest.TestCase):
    def test_new_mail_wakes_once_and_other_changes_do_not(self):
        woken = []
        client, server = connected_pair(during_idle=[
            "* 4 EXISTS",                 # new mail: wake
            "* 2 FETCH (FLAGS (\\Seen))",  # someone read a message: no wake
            "* 1 EXPUNGE",                # a deletion: no wake...
            "* 3 EXISTS",                 # ...and the count it reports: no wake
            "* 4 EXISTS",                 # new mail after the deletion: wake
        ])
        conn = idle.Connection(client)
        watch = idle.Watch(conn, ACCOUNT, ACCOUNT["pass"], "INBOX", woken.append)
        watch.open()
        with ShortRenew():
            watch.idle_once()

        self.assertEqual(woken, ["INBOX", "INBOX"])
        # Read-only, and the password survives IMAP quoting.
        self.assertTrue(any(l.endswith('EXAMINE "INBOX"') for l in server.received))
        self.assertIn('LOGIN "you@example.com" "pa\\"ss\\\\word"',
                      [l.split(" ", 1)[1] for l in server.received if " LOGIN " in l])
        self.assertIn("DONE", server.received)
        client.close()

    def test_nested_folders_use_the_server_delimiter(self):
        client, server = connected_pair(delimiter=".", folders={"Lab.Alerts": 0})
        idle.Watch(idle.Connection(client), ACCOUNT, "p", "Lab/Alerts", lambda f: None).open()
        self.assertTrue(any(l.endswith('EXAMINE "Lab.Alerts"') for l in server.received))
        client.close()

    def test_a_server_without_idle_is_a_configuration_error(self):
        client, _ = connected_pair(idle_supported=False)
        with self.assertRaises(idle.ConfigError):
            idle.Watch(idle.Connection(client), ACCOUNT, "p", "INBOX", lambda f: None).open()
        client.close()

    def test_a_rejected_login_is_an_auth_error(self):
        client, _ = connected_pair(login_ok=False)
        with self.assertRaises(idle.AuthError):
            idle.Watch(idle.Connection(client), ACCOUNT, "p", "INBOX", lambda f: None).open()
        client.close()

    def test_a_missing_folder_is_permanent(self):
        client, _ = connected_pair(folders={"INBOX": 1})
        with self.assertRaises(idle.PermanentFolderError):
            idle.Watch(idle.Connection(client), ACCOUNT, "p", "homelab", lambda f: None).open()
        client.close()


class WatchForeverTests(unittest.TestCase):
    def test_reconnects_with_backoff_and_wakes_after_a_reconnect(self):
        # Session 1 drops mid-IDLE, then one connect attempt fails outright,
        # session 2 comes back, and session 3 finds the folder gone, which ends
        # the watch. The wake after session 2's open covers mail that arrived
        # while nothing was listening.
        attempts = iter(["drop", "refuse", "ok-then-drop", "folder-gone"])
        sleeps, woken = [], []

        def connector(account):
            what = next(attempts)
            if what == "refuse":
                raise ConnectionRefusedError("refused")
            if what == "folder-gone":
                client, _ = connected_pair(folders={})
                return client
            client, _ = connected_pair(bye_during_idle=True)
            return client

        with ShortRenew():
            reason = idle.watch_forever(ACCOUNT, "INBOX", woken.append,
                                        connector=connector, sleep=sleeps.append)

        self.assertIn("could not be opened", reason)
        self.assertEqual(woken, ["INBOX"])
        self.assertEqual(sleeps, [idle.BACKOFF_START_SECONDS,
                                  idle.BACKOFF_START_SECONDS * 2,
                                  idle.BACKOFF_START_SECONDS])

    def test_a_rejected_login_is_retried_slowly(self):
        attempts = iter([True, False])
        sleeps = []

        def connector(account):
            if next(attempts):
                client, _ = connected_pair(login_ok=False)
            else:
                client, _ = connected_pair(idle_supported=False)
            return client

        idle.watch_forever(ACCOUNT, "INBOX", lambda f: None, connector=connector, sleep=sleeps.append)
        self.assertEqual(sleeps, [idle.AUTH_FAILURE_RETRY_SECONDS])

    def test_the_wake_file_is_created(self):
        path = pathlib.Path(tempfile.mkdtemp()) / "wake"
        self.addCleanup(shutil.rmtree, path.parent)
        idle.Waker(str(path))("INBOX")
        self.assertTrue(path.exists())


def extract_loop():
    """The mbsync-loop script, exactly as the Dockerfile writes it."""
    text = (REPO / "Dockerfile").read_text()
    m = re.search(r"RUN cat <<'EOF' > /usr/local/bin/mbsync-loop\n(.*?)\nEOF\n", text, re.S)
    assert m, "mbsync-loop heredoc not found in the Dockerfile"
    return m.group(1)


@unittest.skipUnless(os.name == "posix", "the loop is a POSIX sh script")
class LoopTests(unittest.TestCase):
    """Runs the real loop with a fake mbsync (logs each start) and a fake watcher."""

    def setUp(self):
        self.dir = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir, True)
        bin_dir = self.dir / "bin"
        bin_dir.mkdir()
        (self.dir / "mail").mkdir()
        self.config = self.dir / "mbsyncrc"
        self.config.write_text("IMAPAccount a\n")
        self.starts = self.dir / "starts"
        self.write_exe(bin_dir / "mbsync", f'#!/bin/sh\ndate +%s.%N >> "{self.starts}"\nexit 0\n')
        # The fake watcher signals new mail once, two seconds in, and then
        # stays up like the real one.
        self.write_exe(bin_dir / "mbsync-idle",
                       '#!/bin/sh\nsleep 2\n: > "$MBSYNC_WAKE_FILE"\nexec sleep 600\n')
        self.loop = self.dir / "mbsync-loop"
        self.write_exe(self.loop, extract_loop())
        self.path = f"{bin_dir}{os.pathsep}{os.environ['PATH']}"

    @staticmethod
    def write_exe(path, body):
        path.write_text(body)
        path.chmod(0o755)

    def run_loop(self, seconds, **env):
        environment = {**os.environ, "PATH": self.path, "MBSYNC_CONFIG": str(self.config),
                       "MBSYNC_MAILDIR": str(self.dir / "mail" / "Fastmail"),
                       "MBSYNC_WAKE_FILE": str(self.dir / "wake"), **env}
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
        proc.wait(timeout=10)
        self.stop_seconds = time.monotonic() - stopped_at
        self.kill_group(proc)
        starts = self.starts.read_text().split() if self.starts.exists() else []
        return [float(s) for s in starts], log_path.read_text()

    @staticmethod
    def kill_group(proc):
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass

    def test_new_mail_starts_the_next_sync_early(self):
        starts, log = self.run_loop(9, MBSYNC_INTERVAL_SECONDS="60",
                                    MBSYNC_IDLE_FOLDERS="INBOX,Junk Mail")
        self.assertEqual(len(starts), 2, log)
        # Wake at ~2s, held until the 5s gap floor, never the 60s interval.
        self.assertGreaterEqual(starts[1] - starts[0], 4.5, log)
        self.assertLess(starts[1] - starts[0], 8, log)
        self.assertIn("IDLE enabled for: INBOX,Junk Mail", log)
        self.assertLess(self.stop_seconds, 5, "TERM must still stop the loop promptly")

    def test_mail_signalled_during_a_sync_triggers_the_next_one(self):
        # The wake file is cleared BEFORE a sync starts, so a signal that lands
        # while mbsync is running survives it. Clearing it after the sync
        # instead would lose that mail until the 60s timer.
        self.write_exe(self.dir / "bin" / "mbsync",
                       f'#!/bin/sh\n[ -e "{self.starts}" ] || : > "$MBSYNC_WAKE_FILE"\n'
                       f'date +%s.%N >> "{self.starts}"\nexit 0\n')
        self.write_exe(self.dir / "bin" / "mbsync-idle", "#!/bin/sh\nexec sleep 600\n")
        starts, log = self.run_loop(8, MBSYNC_INTERVAL_SECONDS="60", MBSYNC_IDLE_FOLDERS="INBOX")
        self.assertEqual(len(starts), 2, log)
        self.assertLess(starts[1] - starts[0], 7, log)

    def test_without_folders_the_watcher_never_starts(self):
        starts, log = self.run_loop(4, MBSYNC_INTERVAL_SECONDS="60")
        self.assertEqual(len(starts), 1, log)
        self.assertNotIn("IDLE", log)
        self.assertLess(self.stop_seconds, 5)

    def test_a_watcher_config_error_leaves_the_timer_running(self):
        self.write_exe(self.dir / "bin" / "mbsync-idle", "#!/bin/sh\nexit 2\n")
        starts, log = self.run_loop(6, MBSYNC_INTERVAL_SECONDS="2", MBSYNC_IDLE_FOLDERS="INBOX")
        self.assertIn("IDLE watcher stopped on its configuration", log)
        self.assertGreaterEqual(len(starts), 3, log)


if __name__ == "__main__":
    unittest.main()
