#!/usr/bin/env python3
"""
IMAP IDLE watcher for the mbsync sidecar (container deployment only).

Holds one IDLE connection per folder named in MBSYNC_IDLE_FOLDERS and, when
the server reports new mail, creates a wake file that tells mbsync-loop to
start its next sync now instead of when the interval runs out.

THIS NEVER RUNS mbsync ITSELF, and that is the design, not a shortcut. Two
mbsync processes against one Maildir race on .mbsyncstate and its lock; the
loop is the single serialized runner and this only shortens its sleep. The
wake is a file rather than a signal because the loop's `wait` would be
interrupted by a trapped signal mid-sync and read the interruption as a
failed sync.

Only new mail (EXISTS) wakes the loop. Flag changes, moves and deletions wait
for the timer: they are what make a busy shared mailbox chatty, and nobody is
waiting on them.

Everything here is a latency optimisation. If this process dies, cannot log
in, or never starts, mail still arrives on the MBSYNC_INTERVAL_SECONDS timer,
and search_emails' mailSync field still tells clients how fresh the mirror
is. That is why every failure below logs and retries rather than taking the
sidecar down.

Connection settings come from the first IMAPAccount block of the mbsync config
(Host, Port, User, Pass or PassCmd, TLSType), so there is one place for them.
Only TLSType IMAPS is supported: this process holds the password, and implicit
TLS is the only mode where nothing is ever sent in the clear.

Environment:
    MBSYNC_IDLE_FOLDERS  comma-separated folder names, as mbsync names them
                         (with `Subfolders Verbatim`, the path under the
                         Maildir root: `INBOX`, `Junk Mail`, `Lab/Alerts`).
                         A `/` is translated to the server's hierarchy
                         delimiter. Required; the loop only starts this when
                         it is set.
    MBSYNC_CONFIG        mbsync config path (default /etc/mbsyncrc).
    MBSYNC_WAKE_FILE     file to create on new mail (default /tmp/mbsync-wake).

Exit status 2 means configuration that a retry cannot fix (no usable account,
no folders, a server without IDLE, every folder missing); the loop stops
restarting the watcher and keeps syncing on its timer. Any other exit is
restarted.
"""
import base64
import os
import re
import socket
import ssl
import subprocess
import sys
import threading
import time

# Each folder holds its own IMAP connection, and each is a thread counted
# against the container's pids_limit. Providers also cap concurrent
# connections per account (mbsync needs some of its own), so refuse a list
# long enough to start failing in confusing ways.
MAX_FOLDERS = 10

# RFC 2177: re-issue IDLE at least every 29 minutes. Shorter than that so a
# connection that died without a FIN (NAT timeout, VM migration) is noticed in
# minutes; TCP keepalive below is the other half of that.
IDLE_RENEW_SECONDS = 10 * 60
COMMAND_TIMEOUT_SECONDS = 60
BACKOFF_START_SECONDS = 5
BACKOFF_MAX_SECONDS = 300
# A rejected login is retried slowly: a revoked app password retried every few
# seconds across several folders is how an account gets locked.
AUTH_FAILURE_RETRY_SECONDS = 15 * 60

EXIT_CONFIG = 2


def log(message):
    print(f"mbsync-idle: {message}", file=sys.stderr, flush=True)


class ConfigError(Exception):
    """Configuration a retry cannot fix."""


class PermanentFolderError(Exception):
    """This folder cannot be watched until the configuration changes."""


class AuthError(Exception):
    pass


class Timeout(Exception):
    pass


# ---------- mbsync config ----------

def _unquote(value):
    value = value.strip()
    if value.startswith("+"):
        # isync's `PassCmd +"..."` marks an interactive command; the + is not
        # part of the command.
        value = value[1:].lstrip()
    if len(value) >= 2 and value[0] == value[-1] == '"':
        value = re.sub(r'\\(.)', r'\1', value[1:-1])
    return value


def read_account(path):
    """Returns the settings of the first IMAPAccount block in an mbsync config."""
    try:
        with open(path, encoding="utf-8") as f:
            lines = f.readlines()
    except OSError as e:
        raise ConfigError(f"cannot read {path}: {e.strerror}")

    account = None
    for raw in lines:
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        key, _, value = line.partition(" ")
        key = key.lower()
        value = value.strip()
        if key == "imapaccount":
            if account is not None:
                break
            account = {"name": value}
            continue
        if account is None:
            continue
        if key in ("imapstore", "maildirstore", "channel", "group"):
            break
        if key in ("host", "port", "user", "pass", "passcmd", "tlstype", "ssltype"):
            account[key] = _unquote(value)

    if account is None:
        raise ConfigError(f"no IMAPAccount block in {path}")
    if not account.get("host") or not account.get("user"):
        raise ConfigError(f"IMAPAccount {account['name']} in {path} needs Host and User")
    if "pass" not in account and "passcmd" not in account:
        raise ConfigError(f"IMAPAccount {account['name']} in {path} needs Pass or PassCmd")
    tls = (account.get("tlstype") or account.get("ssltype") or "STARTTLS").upper()
    if tls != "IMAPS":
        raise ConfigError(
            f"IMAPAccount {account['name']} uses TLSType {tls}; the IDLE watcher supports IMAPS only")
    try:
        account["port"] = int(account.get("port") or 993)
    except ValueError:
        raise ConfigError(f"IMAPAccount {account['name']} has a non-numeric Port")
    return account


def password_for(account):
    if "pass" in account:
        return account["pass"]
    try:
        result = subprocess.run(
            ["/bin/sh", "-c", account["passcmd"]],
            capture_output=True, text=True, timeout=30, check=False)
    except subprocess.TimeoutExpired:
        raise AuthError("PassCmd timed out")
    if result.returncode != 0:
        # Never echo the command's output: it may be the password.
        raise AuthError(f"PassCmd exited {result.returncode}")
    return result.stdout.rstrip("\r\n")


def parse_folders(value):
    folders = [f.strip() for f in value.split(",")]
    folders = [f for f in folders if f]
    if not folders:
        raise ConfigError("MBSYNC_IDLE_FOLDERS names no folders")
    if len(folders) > MAX_FOLDERS:
        raise ConfigError(
            f"MBSYNC_IDLE_FOLDERS names {len(folders)} folders; the limit is {MAX_FOLDERS} "
            "(each holds an IMAP connection)")
    return list(dict.fromkeys(folders))


# ---------- IMAP encoding ----------

def quote(text):
    """An IMAP quoted string, or ConfigError for text one can't carry."""
    if any(c in text for c in "\r\n\0"):
        raise ConfigError("credential or folder name contains a line break or NUL")
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def mutf7(name):
    """RFC 3501 modified UTF-7, the encoding IMAP uses for mailbox names."""
    out, run = [], []

    def flush():
        if run:
            raw = "".join(run).encode("utf-16-be")
            out.append("&" + base64.b64encode(raw).decode("ascii").rstrip("=").replace("/", ",") + "-")
            run.clear()

    for ch in name:
        if 0x20 <= ord(ch) <= 0x7E:
            flush()
            out.append("&-" if ch == "&" else ch)
        else:
            run.append(ch)
    flush()
    return "".join(out)


# ---------- protocol ----------

class Connection:
    """The minimum of IMAP4rev1 an IDLE watch needs, over a connected socket.

    Reads with its own buffer rather than socket.makefile: a buffered socket
    file is unusable after its first timeout, and an IDLE wait times out by
    design every IDLE_RENEW_SECONDS.
    """

    def __init__(self, sock):
        self.sock = sock
        self.buf = b""
        self.tag = 0

    def readline(self, deadline):
        while b"\r\n" not in self.buf:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise Timeout()
            self.sock.settimeout(remaining)
            try:
                data = self.sock.recv(65536)
            except (socket.timeout, ssl.SSLWantReadError):
                raise Timeout()
            if not data:
                raise ConnectionError("server closed the connection")
            self.buf += data
        line, _, self.buf = self.buf.partition(b"\r\n")
        # A literal ({n}) carries n raw bytes and then the rest of the line.
        # Nothing an IDLE watch asks for should send one, but skipping it
        # keeps a surprising response from desynchronising the stream.
        m = re.search(rb"\{(\d+)\}$", line)
        if m:
            need = int(m.group(1))
            while len(self.buf) < need:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise Timeout()
                self.sock.settimeout(remaining)
                data = self.sock.recv(65536)
                if not data:
                    raise ConnectionError("server closed the connection")
                self.buf += data
            literal, self.buf = self.buf[:need], self.buf[need:]
            line += b"\r\n" + literal + self.readline(deadline).encode("utf-8", "replace")
        return line.decode("utf-8", "replace")

    def send(self, line):
        self.sock.sendall(line.encode("utf-8") + b"\r\n")

    def next_tag(self):
        self.tag += 1
        return f"a{self.tag}"

    def command(self, text):
        """Sends a command; returns (status, untagged lines, tagged text)."""
        tag = self.next_tag()
        self.send(f"{tag} {text}")
        deadline = time.monotonic() + COMMAND_TIMEOUT_SECONDS
        untagged = []
        while True:
            line = self.readline(deadline)
            if line.startswith(tag + " "):
                rest = line[len(tag) + 1:]
                return rest.split(" ", 1)[0].upper(), untagged, rest
            if line.startswith("* BYE"):
                raise ConnectionError(line)
            untagged.append(line)


EXISTS = re.compile(r"^\* (\d+) EXISTS$", re.IGNORECASE)
EXPUNGE = re.compile(r"^\* (\d+) EXPUNGE$", re.IGNORECASE)


class Watch:
    """One folder's IDLE session. `wake` is called on new mail."""

    def __init__(self, conn, account, password, folder, wake):
        self.conn = conn
        self.account = account
        self.password = password
        self.folder = folder
        self.wake = wake
        self.count = 0

    def open(self):
        greeting = self.conn.readline(time.monotonic() + COMMAND_TIMEOUT_SECONDS)
        if not greeting.startswith("* OK"):
            raise ConnectionError(f"unexpected greeting: {greeting[:80]}")

        status, untagged, _ = self.conn.command("CAPABILITY")
        caps = " ".join(l for l in untagged if l.upper().startswith("* CAPABILITY")).upper().split()
        if status != "OK":
            raise ConnectionError("CAPABILITY failed")
        if "IDLE" not in caps:
            raise ConfigError(f"{self.account['host']} does not support IMAP IDLE")

        status, _, _ = self.conn.command(
            f"LOGIN {quote(self.account['user'])} {quote(self.password)}")
        if status != "OK":
            raise AuthError(f"server rejected the login for {self.account['user']}")

        # mbsync names nested folders with `/`; the server may use another
        # delimiter. `LIST "" ""` reports it without listing anything.
        name = self.folder
        status, untagged, _ = self.conn.command('LIST "" ""')
        m = next((re.match(r'^\* LIST \([^)]*\) "(.)"', l) for l in untagged
                  if l.upper().startswith("* LIST")), None)
        if status == "OK" and m and m.group(1) != "/":
            name = name.replace("/", m.group(1))

        # EXAMINE, not SELECT: read-only, so watching never changes \Recent or
        # anything else another client relies on.
        status, untagged, rest = self.conn.command(f"EXAMINE {quote(mutf7(name))}")
        if status != "OK":
            raise PermanentFolderError(
                f"folder {self.folder!r} could not be opened ({rest[:80]}); check MBSYNC_IDLE_FOLDERS")
        for line in untagged:
            m = EXISTS.match(line)
            if m:
                self.count = int(m.group(1))

    def idle_once(self):
        """One IDLE round: wait up to IDLE_RENEW_SECONDS for news, then DONE."""
        tag = self.conn.next_tag()
        self.conn.send(f"{tag} IDLE")
        deadline = time.monotonic() + COMMAND_TIMEOUT_SECONDS
        while True:
            line = self.conn.readline(deadline)
            if line.startswith("+"):
                break
            if line.startswith(tag + " "):
                raise ConnectionError(f"IDLE refused: {line[len(tag) + 1:][:80]}")
            self.observe(line)

        deadline = time.monotonic() + IDLE_RENEW_SECONDS
        try:
            while True:
                self.observe(self.conn.readline(deadline))
        except Timeout:
            pass

        self.conn.send("DONE")
        deadline = time.monotonic() + COMMAND_TIMEOUT_SECONDS
        while True:
            line = self.conn.readline(deadline)
            if line.startswith(tag + " "):
                if not line[len(tag) + 1:].upper().startswith("OK"):
                    raise ConnectionError(f"IDLE ended badly: {line[:80]}")
                return
            self.observe(line)

    def observe(self, line):
        if line.startswith("* BYE"):
            raise ConnectionError(line)
        m = EXISTS.match(line)
        if m:
            n = int(m.group(1))
            if n > self.count:
                self.wake(self.folder)
            self.count = n
            return
        if EXPUNGE.match(line) and self.count > 0:
            self.count -= 1


# ---------- running ----------

def connect(account):
    sock = socket.create_connection((account["host"], account["port"]), timeout=COMMAND_TIMEOUT_SECONDS)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
    for opt, value in (("TCP_KEEPIDLE", 120), ("TCP_KEEPINTVL", 30), ("TCP_KEEPCNT", 4)):
        if hasattr(socket, opt):
            sock.setsockopt(socket.IPPROTO_TCP, getattr(socket, opt), value)
    context = ssl.create_default_context()
    return context.wrap_socket(sock, server_hostname=account["host"])


class Waker:
    def __init__(self, path):
        self.path = path

    def __call__(self, folder):
        log(f"new mail in {folder!r}; waking the sync loop")
        try:
            with open(self.path, "a", encoding="utf-8"):
                pass
        except OSError as e:
            log(f"could not create wake file {self.path}: {e.strerror} (mail will wait for the timer)")


def watch_forever(account, folder, wake, connector=connect, sleep=time.sleep):
    """Watches one folder until a permanent error. Returns the reason it stopped."""
    backoff = BACKOFF_START_SECONDS
    sessions = 0
    while True:
        conn = None
        try:
            conn = Connection(connector(account))
            watch = Watch(conn, account, password_for(account), folder, wake)
            watch.open()
            sessions += 1
            log(f"watching {folder!r}" + (" (reconnected)" if sessions > 1 else ""))
            if sessions > 1:
                # Mail that arrived while we were disconnected raised no EXISTS
                # we could see. One extra sync is cheaper than reasoning about it.
                wake(folder)
            backoff = BACKOFF_START_SECONDS
            while True:
                watch.idle_once()
        except (ConfigError, PermanentFolderError) as e:
            log(str(e))
            return str(e)
        except AuthError as e:
            log(f"{folder!r}: {e}; retrying in {AUTH_FAILURE_RETRY_SECONDS // 60} min")
            delay = AUTH_FAILURE_RETRY_SECONDS
        except Exception as e:  # noqa: BLE001 — anything else is a lost session, never a reason to stop watching
            log(f"{folder!r}: connection lost ({type(e).__name__}: {str(e)[:120]}); retrying in {backoff}s")
            delay = backoff
            backoff = min(backoff * 2, BACKOFF_MAX_SECONDS)
        finally:
            if conn is not None:
                try:
                    conn.sock.close()
                except OSError:
                    pass
        sleep(delay)


def main():
    try:
        folders = parse_folders(os.environ.get("MBSYNC_IDLE_FOLDERS", ""))
        account = read_account(os.environ.get("MBSYNC_CONFIG", "/etc/mbsyncrc"))
    except ConfigError as e:
        log(str(e))
        return EXIT_CONFIG

    wake = Waker(os.environ.get("MBSYNC_WAKE_FILE", "/tmp/mbsync-wake"))
    log(f"account {account['name']} at {account['host']}:{account['port']}; folders: "
        + ", ".join(repr(f) for f in folders))

    threads = [threading.Thread(target=watch_forever, args=(account, f, wake), daemon=True, name=f)
               for f in folders]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    # Every thread returned, which only a permanent error does.
    log("no folder can be watched; IDLE is off until the configuration changes")
    return EXIT_CONFIG


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
