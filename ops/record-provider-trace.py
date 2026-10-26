#!/usr/bin/env python3
"""
Records a provider trace (docs/contributing/provider-traces.md) from a live
Gmail TEST account. It seeds invented messages, drives one server-side
operation per step over IMAP, runs real mbsync into a scratch Maildir after
each step, and writes trace.json + blobs/ in the format
tests/Mailvec.Indexer.Tests/ProviderTraces/TraceReplayTests reads.

    GMAIL_TRACE_USER=<test address> \\
    GMAIL_TRACE_PASSWORD="op://<vault>/<item uuid>/<field>" \\
    op run -- python3 ops/record-provider-trace.py label-lifecycle --work <dir> --out <dir>

Add --compare <committed trace dir> to exit 3 when the recording's shape
(steps, copies, flags, server state; not file names) differs from it. That is
what .github/workflows/gmail-traces.yml runs weekly.

or, without 1Password (`read -rs` keeps the password off the screen and out
of shell history):

    read -rs GMAIL_TRACE_PASSWORD && export GMAIL_TRACE_PASSWORD
    GMAIL_TRACE_USER=<test address> python3 ops/record-provider-trace.py label-lifecycle --work <dir> --out <dir>

Only the two environment variables matter; how they are set is up to you.
The password reaches mbsync through its environment (PassCmd printenv),
never through a file or an argument.

Use your OWN throwaway Gmail account: the maintainer's is not shared, and
contributors never need one to run the replay tests.

Only for an account that holds nothing but test mail. It refuses an account
with more than a handful of messages it did not seed, and it purges every
message it seeds (by Message-ID) before recording. It never sends mail, and it
fails loudly if [Gmail]/Sent Mail gains anything during the run: the
credential can send, and that check is the canary for its misuse.

Both directories must be new or empty, and must not be anywhere a real
Mailvec pipeline would ingest them (same rules as tools/Mailvec.DevCorpus).
Nothing is written into the repository: copy the output in after reviewing it.
"""
import argparse
import datetime
import hashlib
import imaplib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import time

HOST = "imap.gmail.com"
ALL_MAIL = "[Gmail]/All Mail"
TRASH = "[Gmail]/Trash"
SPAM = "[Gmail]/Spam"
SENT = "[Gmail]/Sent Mail"
SEED_DOMAIN = "trace.example"

# More unseeded messages than this, and it is not a test account. A new Gmail
# account arrives holding one (the welcome email).
MAX_FOREIGN_MESSAGES = 5

# mbsync runs per step until two consecutive Maildir snapshots agree, at most
# this many times. A first run that differs from the converged state is kept
# as an unsettled step: that difference is itself something worth recording.
MAX_SYNC_RUNS = 4
SETTLE_SECONDS = 3

# Network patience. isync's default connect Timeout is 20 s, and one slow
# connect from a CI runner (observed 2026-10-26: "Socket error on
# imap.gmail.com (…:993): timeout.") used to fail the whole run. A sync that
# fails on the network is retried; any other failure (a refused login, a bad
# config) is not, because retrying it only repeats the refusal.
NETWORK_TIMEOUT_SECONDS = 60
MBSYNC_ATTEMPTS = 3
MBSYNC_RETRY_WAIT_SECONDS = 30
TRANSIENT_MBSYNC = re.compile(
    r"Socket error|timed? ?out|Connection (?:reset|refused|closed)|unexpected EOF|"
    r"Temporary failure in name resolution|Name or service not known|Network is unreachable",
    re.I)

MARKERS = (".mbsyncstate", ".mailvec-mbsync-heartbeat", ".mailvec-mbsync-sync", "archive.sqlite", ".frozen-corpus")
SHARED_CONFIG = pathlib.Path.home() / "Library" / "Application Support" / "Mailvec" / "appsettings.Local.json"


class Refused(Exception):
    pass


# ── Invented messages ───────────────────────────────────────────────────────

def message(mid, subject, body, date):
    """An RFC 5322 message with CRLF line endings, as IMAP APPEND wants it."""
    lines = [
        f"Message-ID: <{mid}>",
        f"Date: {date}",
        "From: Ada Example <ada@example.com>",
        "To: Sam Rivera <sam@example.org>",
        f"Subject: {subject}",
        "MIME-Version: 1.0",
        "Content-Type: text/plain; charset=utf-8",
        "",
        body,
        "",
    ]
    return "\r\n".join(lines).encode()


SEEDS = {
    "m1": ("m1-receipt@trace.example", "Your receipt from Harbor Hardware",
           "Item: garden twine x2. Total: 7.40 GBP.", "Mon, 05 Jan 2026 09:00:00 +0000"),
    "m2": ("m2-newsletter@trace.example", "The Weekly Sprout: raised beds",
           "This week we look at raised beds and what to fill them with.", "Sun, 04 Jan 2026 06:00:00 +0000"),
}


def seed_bytes(key):
    mid, subject, body, date = SEEDS[key]
    return message(mid, subject, body, date)


def seed_id(key):
    return SEEDS[key][0]


# ── IMAP helpers ────────────────────────────────────────────────────────────

def quote(mailbox):
    return '"' + mailbox.replace("\\", "\\\\").replace('"', '\\"') + '"'


LIST_LINE = re.compile(r'^\((?P<flags>[^)]*)\) (?:"(?:[^"\\]|\\.)*"|NIL) (?P<name>.+)$')


def parse_list_line(line):
    """(flags, name) from one LIST response line."""
    m = LIST_LINE.match(line)
    if not m:
        raise ValueError(f"unparseable LIST line: {line!r}")
    name = m.group("name")
    if name.startswith('"') and name.endswith('"'):
        name = re.sub(r"\\(.)", r"\1", name[1:-1])
    return m.group("flags").split(), name


FETCH_META = re.compile(rb"UID (\d+)|FLAGS \(([^)]*)\)")


def parse_fetch(data):
    """
    [(uid, flags, message_id)] from a UID FETCH (UID FLAGS BODY.PEEK[HEADER.FIELDS (MESSAGE-ID)])
    response. imaplib hands back a (meta, literal) tuple per message, and
    whatever the server sent after the literal (Gmail may put FLAGS or UID
    there) as the bytes element that follows it.
    """
    out = []
    for item in data:
        if isinstance(item, tuple):
            meta, header = item
            out.append({"uid": None, "flags": [], "mid": message_id_of(header)})
        elif isinstance(item, bytes) and out:
            meta = item
        else:
            continue
        for m in FETCH_META.finditer(meta):
            if m.group(1):
                out[-1]["uid"] = int(m.group(1))
            else:
                out[-1]["flags"] = m.group(2).decode().split()
    return [(r["uid"], r["flags"], r["mid"]) for r in out]


def message_id_of(raw):
    """The Message-ID without angle brackets, or None. Header block only; unfolds continuations."""
    text = raw.decode("utf-8", "replace").replace("\r\n", "\n")
    end = text.find("\n\n")
    headers = (text if end < 0 else text[:end]).replace("\n ", " ").replace("\n\t", " ")
    for line in headers.split("\n"):
        name, sep, value = line.partition(":")
        if sep and name.strip().lower() == "message-id":
            return value.strip().strip("<>")
    return None


class Account:
    """One IMAP session against the test account."""

    def __init__(self, user, password):
        self.imap = imaplib.IMAP4_SSL(HOST, 993, timeout=NETWORK_TIMEOUT_SECONDS)
        self.imap.login(user, password)

    def close(self):
        try:
            self.imap.logout()
        except Exception:
            pass

    def _ok(self, typ, data, what):
        if typ != "OK":
            raise RuntimeError(f"IMAP {what} failed: {data!r}")
        return data

    def mailboxes(self):
        """Every selectable mailbox."""
        data = self._ok(*self.imap.list(), "LIST")
        boxes = []
        for line in data:
            flags, name = parse_list_line(line.decode())
            if "\\Noselect" not in flags and "\\NonExistent" not in flags:
                boxes.append(name)
        return sorted(boxes)

    def select(self, mailbox, readonly=False):
        data = self._ok(*self.imap.select(quote(mailbox), readonly=readonly), f"SELECT {mailbox}")
        return int(data[0])

    def messages(self, mailbox):
        """[(uid, flags, message_id)] for every message in the mailbox."""
        if self.select(mailbox, readonly=True) == 0:
            return []
        data = self._ok(*self.imap.uid("FETCH", "1:*", "(UID FLAGS BODY.PEEK[HEADER.FIELDS (MESSAGE-ID)])"), f"FETCH {mailbox}")
        return parse_fetch(data)

    def uids_of(self, mailbox, mid):
        return [uid for uid, _, m in self.messages(mailbox) if m == mid]

    def _one(self, mailbox, key):
        uids = self.uids_of(mailbox, seed_id(key))
        if len(uids) != 1:
            raise RuntimeError(f"expected one copy of {key} in {mailbox}, found {len(uids)}")
        self.select(mailbox)
        return str(uids[0])

    def append(self, mailbox, key, seen):
        mid, _, _, date = SEEDS[key]
        when = imaplib.Time2Internaldate(datetime.datetime.strptime(date, "%a, %d %b %Y %H:%M:%S %z"))
        self._ok(*self.imap.append(quote(mailbox), "(\\Seen)" if seen else None, when, seed_bytes(key)), f"APPEND {key}")

    def create(self, mailbox):
        self._ok(*self.imap.create(quote(mailbox)), f"CREATE {mailbox}")

    def delete_mailbox(self, mailbox):
        self._ok(*self.imap.delete(quote(mailbox)), f"DELETE {mailbox}")

    def store_seen(self, mailbox, key):
        uid = self._one(mailbox, key)
        self._ok(*self.imap.uid("STORE", uid, "+FLAGS", "(\\Seen)"), "STORE \\Seen")

    def copy(self, mailbox, key, target):
        uid = self._one(mailbox, key)
        self._ok(*self.imap.uid("COPY", uid, quote(target)), f"COPY to {target}")

    def move(self, mailbox, key, target):
        uid = self._one(mailbox, key)
        self._ok(*self.imap.uid("MOVE", uid, quote(target)), f"MOVE to {target}")

    def expunge(self, mailbox, key):
        """Removes the message from this mailbox: in Gmail, removes the label."""
        uid = self._one(mailbox, key)
        self._ok(*self.imap.uid("STORE", uid, "+FLAGS", "(\\Deleted)"), "STORE \\Deleted")
        self._ok(*self.imap.expunge(), f"EXPUNGE {mailbox}")

    def purge_seeded(self, labels):
        """Deletes every message from SEED_DOMAIN permanently, and the trace's own labels."""
        for mailbox in (ALL_MAIL, SPAM):
            uids = [str(uid) for uid, _, mid in self.messages(mailbox) if mid and mid.endswith("@" + SEED_DOMAIN)]
            if uids:
                self.select(mailbox)
                self._ok(*self.imap.uid("MOVE", ",".join(uids), quote(TRASH)), f"MOVE {mailbox} to Trash")
        uids = [str(uid) for uid, _, mid in self.messages(TRASH) if mid and mid.endswith("@" + SEED_DOMAIN)]
        if uids:
            self.select(TRASH)
            self._ok(*self.imap.uid("STORE", ",".join(uids), "+FLAGS", "(\\Deleted)"), "STORE \\Deleted in Trash")
            self._ok(*self.imap.expunge(), "EXPUNGE Trash")
        existing = self.mailboxes()
        for label in labels:
            if label in existing:
                self.delete_mailbox(label)

    def foreign_count(self):
        return sum(1 for _, _, mid in self.messages(ALL_MAIL) if not (mid and mid.endswith("@" + SEED_DOMAIN)))

    def sent_count(self):
        return len(self.messages(SENT))

    def server_state(self, seeded):
        """{message_id: {"mailboxes": [...], "seen": bool}} for the seeded messages the server holds."""
        state = {}
        for mailbox in self.mailboxes():
            for _, flags, mid in self.messages(mailbox):
                if mid not in seeded:
                    continue
                entry = state.setdefault(mid, {"mailboxes": [], "seen": None, "_flags": set()})
                entry["mailboxes"].append(mailbox)
                entry["_flags"].add("\\Seen" in flags)
        for mid, entry in state.items():
            if len(entry["_flags"]) != 1:
                raise RuntimeError(f"{mid}: \\Seen differs between its copies on the server: {entry['mailboxes']}")
            entry["seen"] = entry.pop("_flags").pop()
            entry["mailboxes"].sort()
        return dict(sorted(state.items()))


# ── Traces ──────────────────────────────────────────────────────────────────

class Step:
    def __init__(self, id, action, run):
        self.id, self.action, self.run = id, action, run


TRACES = {
    "label-lifecycle": {
        "description": "One message through its whole Gmail life: arrives, read elsewhere, labelled, archived, "
                       "unlabelled, trashed, deleted forever.",
        "seeds": ["m1", "m2"],
        "labels": ["Receipts"],
        "steps": [
            Step("arrives-unread", "m1 is appended to INBOX, unread. m2 is appended straight to All Mail (archived), seen.",
                 lambda a: (a.append(ALL_MAIL, "m2", seen=True), a.append("INBOX", "m1", seen=False))),
            Step("read-in-webmail", "m1 gets \\Seen on the server (as if opened in the web UI), set through its INBOX copy.",
                 lambda a: a.store_seen("INBOX", "m1")),
            Step("labelled", "The label Receipts is created and applied to m1 (IMAP COPY from INBOX).",
                 lambda a: (a.create("Receipts"), a.copy("INBOX", "m1", "Receipts"))),
            Step("archived", "m1 is archived: \\Deleted + EXPUNGE in INBOX, which removes the Inbox label.",
                 lambda a: a.expunge("INBOX", "m1")),
            Step("unlabelled", "The Receipts label is removed from m1: \\Deleted + EXPUNGE in Receipts.",
                 lambda a: a.expunge("Receipts", "m1")),
            Step("trashed", "m1 is moved to the bin: UID MOVE from All Mail to [Gmail]/Trash.",
                 lambda a: a.move(ALL_MAIL, "m1", TRASH)),
            Step("deleted-forever", "The bin is emptied of m1: \\Deleted + EXPUNGE in [Gmail]/Trash.",
                 lambda a: a.expunge(TRASH, "m1")),
        ],
    },
}


# ── Where it may write ──────────────────────────────────────────────────────

def is_maildir_folder(d):
    return (d / "cur").is_dir() or (d / "new").is_dir()


def live_locations(shared_config):
    """The shared config's directory, its Ingest:MaildirRoot and its database directory."""
    if not shared_config.exists():
        return []
    try:
        cfg = json.loads(shared_config.read_text())
    except (OSError, ValueError) as e:
        raise Refused(f"cannot read {shared_config} ({e}), so cannot tell where the live Maildir is")
    out = [shared_config.parent.resolve()]
    root = (cfg.get("Ingest") or {}).get("MaildirRoot")
    db = (cfg.get("Archive") or {}).get("DatabasePath")
    if isinstance(root, str):
        out.append(pathlib.Path(root).expanduser().resolve())
    if isinstance(db, str):
        out.append(pathlib.Path(db).expanduser().resolve().parent)
    return out


def check_target(target, shared_config=SHARED_CONFIG):
    """Refuses any directory a real pipeline could ingest; returns it resolved."""
    path = pathlib.Path(target).expanduser().absolute()
    if path.is_file():
        raise Refused(f"{path} is a file")
    if path.is_dir() and any(path.iterdir()):
        raise Refused(f"{path} is not empty")
    if not path.parent.is_dir():
        raise Refused(f"the parent directory {path.parent} does not exist")
    resolved = path.parent.resolve() / path.name
    for d in resolved.parents:
        if is_maildir_folder(d):
            raise Refused(f"{d} is a Maildir folder")
        try:
            children = [c for c in d.iterdir() if c.is_dir()]
        except OSError:
            children = []
        if any(is_maildir_folder(c) for c in children):
            raise Refused(f"{d} is a Maildir root")
        for marker in MARKERS:
            if (d / marker).exists():
                raise Refused(f"{d} contains {marker}: it belongs to a real mail setup")
    for live in live_locations(shared_config):
        if resolved == live or live in resolved.parents:
            raise Refused(f"{resolved} is inside {live}, which the Mailvec shared config names as a live location")
    return resolved


# ── mbsync and the Maildir ──────────────────────────────────────────────────

PASS_ENV = "MAILVEC_TRACE_IMAP_PASSWORD"


def mbsyncrc(user, mail):
    """The shipped container config's settings, pointed at Gmail and the scratch Maildir."""
    return "\n".join([
        "IMAPAccount trace",
        f"Host {HOST}",
        "Port 993",
        f"User {user}",
        f'PassCmd "printenv {PASS_ENV}"',
        "TLSType IMAPS",
        "AuthMechs LOGIN",
        f"Timeout {NETWORK_TIMEOUT_SECONDS}",
        "",
        "IMAPStore trace-remote",
        "Account trace",
        "",
        "MaildirStore trace-local",
        "Subfolders Verbatim",
        f'Path "{mail}/"',
        f'Inbox "{mail}/INBOX"',
        "",
        "Channel trace",
        "Far :trace-remote:",
        "Near :trace-local:",
        "Patterns *",
        "Create Near",
        "SyncState *",
        "Sync Pull",
        "Expunge Near",
        "",
    ])


def run_mbsync(config, password, attempts=MBSYNC_ATTEMPTS, wait=MBSYNC_RETRY_WAIT_SECONDS,
               run=subprocess.run, sleep=time.sleep):
    """One mbsync -a, retried on network failures only. Pull-only, so a retry is idempotent."""
    env = dict(os.environ)
    env[PASS_ENV] = password
    env.pop("GMAIL_TRACE_PASSWORD", None)
    for attempt in range(1, attempts + 1):
        r = run(["mbsync", "-c", str(config), "-a"], env=env, capture_output=True, text=True)
        if r.returncode == 0:
            return
        transient = TRANSIENT_MBSYNC.search(r.stderr or "")
        if not transient or attempt == attempts:
            kind = "network failure, out of retries" if transient else "not a network failure, not retried"
            raise RuntimeError(f"mbsync exited {r.returncode} on attempt {attempt}/{attempts} ({kind}):\n{r.stderr.strip()}")
        print(f"    mbsync network failure on attempt {attempt}/{attempts}; retrying in {wait} s", flush=True)
        sleep(wait)


# isync names a file <time>.<pid>_<n>.<hostname>[,U=<uid>][:2,<flags>]. The
# hostname is the recording machine's, often a person's name ("Janes-MacBook"),
# and a committed trace is public, so it is replaced. Nothing reads it: the
# scanner and the replay treat the name as opaque apart from the flags.
ISYNC_NAME = re.compile(r"^(?P<stem>\d+\.\d+_\d+)\.(?P<host>.+?)(?P<rest>(?:,U=\d+)?(?::2,[A-Za-z]*)?)$")
RECORDED_HOST = "host"


def without_hostname(rel_path):
    """The Maildir-relative path with the hostname in its file name replaced."""
    folder, _, name = rel_path.rpartition("/")
    m = ISYNC_NAME.match(name)
    if not m:
        raise Refused(f"{rel_path}: not an isync file name, so the hostname in it cannot be found and removed")
    return f"{folder}/{m['stem']}.{RECORDED_HOST}{m['rest']}"


def snapshot(mail, seeded):
    """({relative path: bytes}, foreign file count) for the cur/new files holding seeded messages."""
    files, foreign = {}, 0
    for path in sorted(mail.rglob("*")):
        if not path.is_file() or path.parent.name not in ("cur", "new"):
            continue
        data = path.read_bytes()
        if message_id_of(data) in seeded:
            rel = without_hostname(path.relative_to(mail).as_posix())
            if rel in files:
                raise Refused(f"{rel}: two files share a name once the hostname is removed")
            files[rel] = data
        else:
            foreign += 1
    return files, foreign


def normalized(data):
    return data.replace(b"\r\n", b"\n")


# isync writes an X-TUID header into each message it stores, a random token it
# uses to find the message again after an upload. It is isync's, not the
# account's, so it is the one header a recorded file may add to the seed.
ISYNC_HEADER = re.compile(rb"^X-TUID: [^\n]*\n", re.M)


def without_isync_header(data):
    head, sep, body = normalized(data).partition(b"\n\n")
    return ISYNC_HEADER.sub(b"", head + b"\n").rstrip(b"\n") + sep + body


def header_names(data):
    text = normalized(data).decode("utf-8", "replace")
    head = text.split("\n\n", 1)[0]
    return [line.split(":", 1)[0] for line in head.split("\n") if line and not line[0].isspace() and ":" in line]


def check_blob(path, data, seeds_by_id):
    """A recorded file must be the seeded bytes. Anything else could carry the account's identity."""
    mid = message_id_of(data)
    seed = seeds_by_id[mid]
    if without_isync_header(data) != normalized(seed):
        added = [h for h in header_names(data) if h not in header_names(seed)]
        raise Refused(f"{path}: the recorded message is not the seeded one (headers added: {added or 'none; body differs'}). "
                      "Not writing it: it may carry the account's address.")


def blob_name(data):
    return hashlib.sha256(data).hexdigest()[:16] + ".eml"


# ── Recording ───────────────────────────────────────────────────────────────

def converge(sync, take_snapshot, read_server, max_runs=MAX_SYNC_RUNS, settle=lambda: time.sleep(SETTLE_SECONDS)):
    """
    Runs mbsync until two consecutive snapshots agree and the server did not
    move meanwhile. Returns (first_snapshot, final_snapshot, server_state).
    """
    settle()
    before = read_server()
    sync()
    first = previous = take_snapshot()
    for _ in range(max_runs - 1):
        sync()
        current = take_snapshot()
        after = read_server()
        if current == previous and after == before:
            return first, current, after
        previous, before = current, after
        settle()
    raise RuntimeError(f"the Maildir or the server did not settle after {max_runs} mbsync runs")


def build_trace(name, definition, recorded_steps, mbsync_version, today):
    return {
        "version": 1,
        "provider": "gmail",
        "source": "recorded",
        "description": definition["description"] + " Recorded from a Gmail test account by ops/record-provider-trace.py.",
        "recorded": {
            "at": today,
            "mbsync": mbsync_version,
            "notes": "Gmail IMAP settings left at the account defaults. mbsync: Patterns *, Subfolders Verbatim, "
                     "Sync Pull, Expunge Near, Create Near, no Remove line.",
        },
        "steps": recorded_steps,
    }


def record(name, user, password, work, out):
    definition = TRACES[name]
    seeded = {seed_id(k) for k in definition["seeds"]}
    seeds_by_id = {seed_id(k): seed_bytes(k) for k in definition["seeds"]}
    mail = work / "Mail"
    mail.mkdir(parents=True)
    config = work / "mbsyncrc"
    config.write_text(mbsyncrc(user, mail))
    config.chmod(0o600)
    version = subprocess.run(["mbsync", "--version"], capture_output=True, text=True).stdout.strip().removeprefix("isync ")

    account = Account(user, password)
    try:
        foreign = account.foreign_count()
        if foreign > MAX_FOREIGN_MESSAGES:
            raise Refused(f"All Mail holds {foreign} messages this recorder did not seed. This does not look like a test account.")
        sent_before = account.sent_count()
        account.purge_seeded(definition["labels"])

        steps = []
        for step in definition["steps"]:
            print(f"  {step.id}: {step.action}", flush=True)
            step.run(account)
            first, final, server = converge(
                lambda: run_mbsync(config, password),
                lambda: snapshot(mail, seeded)[0],
                lambda: account.server_state(seeded))
            if first != final:
                steps.append({"id": f"{step.id}-first-sync", "settled": False,
                              "serverAction": f"{step.action} The state after the FIRST mbsync run, which a scan can see: "
                                              "a later run changed it.",
                              "maildir": first})
            steps.append({"id": step.id, "serverAction": step.action, "maildir": final,
                          "server": {mid: {"mailboxes": s["mailboxes"], "seen": s["seen"]} for mid, s in server.items()}})

        if account.sent_count() != sent_before:
            raise Refused("[Gmail]/Sent Mail changed during the run. The recorder never sends: something else is using this "
                          "credential. Revoke the app password.")
    finally:
        account.close()

    blobs = out / "blobs"
    blobs.mkdir(parents=True)
    for step in steps:
        named = {}
        for path, data in step["maildir"].items():
            check_blob(path, data, seeds_by_id)
            blob = blob_name(data)
            (blobs / blob).write_bytes(data)
            named[path] = blob
        step["maildir"] = named
    trace = build_trace(name, definition, steps, version, datetime.date.today().isoformat())
    (out / "trace.json").write_text(json.dumps(trace, indent=2, ensure_ascii=False) + "\n")
    return trace


# ── Comparing a recording with the committed trace ──────────────────────────

def shape(trace_dir):
    """
    What a trace says, minus what legitimately differs between recordings:
    file names (times, pids, UIDs) and blob names (isync's X-TUID is random).
    Per step: its id, whether it settled, each copy as (message id, folder,
    cur|new, flags), and the server's state.
    """
    trace_dir = pathlib.Path(trace_dir)
    trace = json.loads((trace_dir / "trace.json").read_text())
    steps = []
    for step in trace["steps"]:
        copies = []
        for path, blob in step["maildir"].items():
            folder, sub, name = path.rsplit("/", 2)
            flags = name.partition(":2,")[2]
            copies.append((message_id_of((trace_dir / "blobs" / blob).read_bytes()), folder, sub, flags))
        steps.append({"id": step["id"], "settled": step.get("settled", True), "copies": sorted(copies),
                      "server": step.get("server")})
    return steps


def shape_differences(recorded_dir, committed_dir):
    """Human-readable differences between two traces' shapes; empty when they agree."""
    a, b = shape(recorded_dir), shape(committed_dir)
    diffs = []
    if [s["id"] for s in a] != [s["id"] for s in b]:
        diffs.append(f"steps: recorded {[s['id'] for s in a]}, committed {[s['id'] for s in b]}")
    for ra, rb in zip(a, b):
        for key in ("settled", "copies", "server"):
            if ra["id"] == rb["id"] and ra[key] != rb[key]:
                diffs.append(f"{ra['id']}: {key} differs\n    recorded:  {ra[key]}\n    committed: {rb[key]}")
    return diffs


def main(argv=None):
    p = argparse.ArgumentParser(description="Record a provider trace from a Gmail test account.")
    p.add_argument("trace", choices=sorted(TRACES))
    p.add_argument("--work", required=True, help="scratch directory for the Maildir and mbsyncrc (new or empty)")
    p.add_argument("--out", required=True, help="where trace.json and blobs/ go (new or empty)")
    p.add_argument("--compare", metavar="DIR",
                   help="a committed trace directory: exit 3 if the recording's shape differs from it")
    args = p.parse_args(argv)

    user = os.environ.get("GMAIL_TRACE_USER", "")
    password = os.environ.get("GMAIL_TRACE_PASSWORD", "").replace(" ", "")
    if not user.endswith("@gmail.com"):
        print("Set GMAIL_TRACE_USER to the test account's @gmail.com address.", file=sys.stderr)
        return 2
    if not password or password.startswith("op://"):
        print("GMAIL_TRACE_PASSWORD is empty or an unresolved op:// reference. Set it to the test account's "
              "app password (see this script's docstring). Not authenticating.", file=sys.stderr)
        return 2
    if shutil.which("mbsync") is None:
        print("mbsync (isync) is not on PATH.", file=sys.stderr)
        return 2
    try:
        work = check_target(args.work)
        out = check_target(args.out)
        if work == out or out in work.parents or work in out.parents:
            raise Refused("--work and --out must be separate directories")
        work.mkdir(exist_ok=True)
        out.mkdir(exist_ok=True)
        print(f"Recording {args.trace} into {out}", flush=True)
        trace = record(args.trace, user, password, work, out)
    except Refused as e:
        print(f"refusing: {e}", file=sys.stderr)
        return 1
    finally:
        password = None
    print(f"Wrote {len(trace['steps'])} steps to {out / 'trace.json'}. Review it, then copy it into "
          f"tests/Mailvec.Indexer.Tests/ProviderTraces/gmail/{args.trace}/.")
    if args.compare:
        diffs = shape_differences(out, args.compare)
        if diffs:
            print(f"The recording differs from {args.compare}: Gmail or isync behaves differently now.", file=sys.stderr)
            for d in diffs:
                print("  " + d, file=sys.stderr)
            return 3
        print(f"Same shape as {args.compare}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
