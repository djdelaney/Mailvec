"""
Offline tests for ops/record-provider-trace.py: everything except the live
Gmail session and the real mbsync run, which only a recording exercises (see
docs/contributing/provider-traces.md).

Run with: python3 -m unittest discover -s ops/tests -v
"""
import importlib.util
import json
import pathlib
import re
import shutil
import tempfile
import unittest

REPO = pathlib.Path(__file__).resolve().parents[2]
_spec = importlib.util.spec_from_file_location("record_provider_trace", REPO / "ops" / "record-provider-trace.py")
rpt = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(rpt)

NO_CONFIG = pathlib.Path("/nonexistent/mailvec-no-shared-config.json")


class TargetGuards(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp()).resolve()

    def test_a_new_directory_is_accepted(self):
        self.assertEqual(rpt.check_target(self.tmp / "out", NO_CONFIG), self.tmp / "out")

    def test_a_non_empty_directory_is_refused(self):
        (self.tmp / "out").mkdir()
        (self.tmp / "out" / "x").write_text("")
        with self.assertRaises(rpt.Refused):
            rpt.check_target(self.tmp / "out", NO_CONFIG)

    def test_inside_a_maildir_root_is_refused(self):
        (self.tmp / "Mail" / "INBOX" / "cur").mkdir(parents=True)
        with self.assertRaises(rpt.Refused):
            rpt.check_target(self.tmp / "Mail" / "out", NO_CONFIG)

    def test_under_the_frozen_corpus_marker_is_refused(self):
        (self.tmp / ".frozen-corpus").write_text("")
        with self.assertRaises(rpt.Refused):
            rpt.check_target(self.tmp / "out", NO_CONFIG)

    def test_inside_a_location_the_shared_config_names_is_refused(self):
        live = self.tmp / "live"
        live.mkdir()
        cfg = self.tmp / "cfgdir" / "appsettings.Local.json"
        cfg.parent.mkdir()
        cfg.write_text(json.dumps({"Ingest": {"MaildirRoot": str(live)}}))
        with self.assertRaises(rpt.Refused):
            rpt.check_target(live / "out", cfg)

    def test_an_unreadable_shared_config_is_a_refusal_not_a_pass(self):
        cfg = self.tmp / "appsettings.Local.json"
        cfg.write_text("{ not json")
        with self.assertRaises(rpt.Refused):
            rpt.check_target(self.tmp / "out", cfg)


class Parsing(unittest.TestCase):
    def test_list_lines_with_quoted_system_names(self):
        self.assertEqual(rpt.parse_list_line(r'(\All \HasNoChildren) "/" "[Gmail]/All Mail"'),
                         ([r"\All", r"\HasNoChildren"], "[Gmail]/All Mail"))
        self.assertEqual(rpt.parse_list_line(r'(\HasChildren \Noselect) "/" "[Gmail]"')[1], "[Gmail]")
        self.assertEqual(rpt.parse_list_line(r'(\HasNoChildren) "/" INBOX')[1], "INBOX")

    def test_fetch_with_flags_before_and_after_the_literal(self):
        data = [
            (b'1 (UID 7 FLAGS (\\Seen) BODY[HEADER.FIELDS (MESSAGE-ID)] {32}', b"Message-ID: <a@trace.example>\r\n\r\n"),
            b")",
            (b"2 (UID 9 BODY[HEADER.FIELDS (MESSAGE-ID)] {32}", b"Message-ID: <b@trace.example>\r\n\r\n"),
            b" FLAGS ())",
        ]
        self.assertEqual(rpt.parse_fetch(data), [(7, [r"\Seen"], "a@trace.example"), (9, [], "b@trace.example")])

    def test_a_folded_message_id_is_unfolded(self):
        self.assertEqual(rpt.message_id_of(b"Subject: x\r\nMessage-ID:\r\n <c@trace.example>\r\n\r\nbody"), "c@trace.example")


class Snapshot(unittest.TestCase):
    def test_only_seeded_messages_are_recorded(self):
        mail = pathlib.Path(tempfile.mkdtemp())
        (mail / "[Gmail]" / "All Mail" / "cur").mkdir(parents=True)
        (mail / "INBOX" / "new").mkdir(parents=True)
        (mail / "INBOX" / "new" / "1.1_1.x,U=1:2,").write_bytes(rpt.seed_bytes("m1"))
        (mail / "[Gmail]" / "All Mail" / "cur" / "2.1_2.x,U=2:2,S").write_bytes(rpt.seed_bytes("m1"))
        (mail / "[Gmail]" / "All Mail" / "cur" / "3.1_3.x,U=3:2,S").write_bytes(b"Message-ID: <welcome@google.example>\n\nhi")
        (mail / "INBOX" / ".mbsyncstate").write_text("state")
        files, foreign = rpt.snapshot(mail, {rpt.seed_id("m1")})
        self.assertEqual(sorted(files), ["INBOX/new/1.1_1.host,U=1:2,", "[Gmail]/All Mail/cur/2.1_2.host,U=2:2,S"])
        self.assertEqual(foreign, 1)


class Hostname(unittest.TestCase):
    def test_the_hostname_is_replaced_in_every_isync_name_shape(self):
        cases = {
            "INBOX/new/1792964873.77058_1.Mac,U=1:2,": "INBOX/new/1792964873.77058_1.host,U=1:2,",
            "[Gmail]/All Mail/cur/1792964874.77058_2.Janes-MacBook-Pro.local,U=12:2,FS":
                "[Gmail]/All Mail/cur/1792964874.77058_2.host,U=12:2,FS",
            "Receipts/new/1792964920.77229_1.box.example.org": "Receipts/new/1792964920.77229_1.host",
            "Receipts/cur/1792964920.77229_1.box:2,S": "Receipts/cur/1792964920.77229_1.host:2,S",
        }
        for recorded, expected in cases.items():
            self.assertEqual(rpt.without_hostname(recorded), expected, recorded)

    def test_a_name_that_is_not_isyncs_is_refused_rather_than_kept(self):
        with self.assertRaises(rpt.Refused):
            rpt.without_hostname("INBOX/cur/janes-laptop-message:2,S")

    def test_a_snapshot_never_keeps_the_hostname(self):
        mail = pathlib.Path(tempfile.mkdtemp())
        (mail / "INBOX" / "cur").mkdir(parents=True)
        (mail / "INBOX" / "cur" / "1.2_3.Janes-MacBook,U=1:2,S").write_bytes(rpt.seed_bytes("m1"))
        files, _ = rpt.snapshot(mail, {rpt.seed_id("m1")})
        self.assertEqual(list(files), ["INBOX/cur/1.2_3.host,U=1:2,S"])


class BlobPrivacy(unittest.TestCase):
    def test_the_seeded_bytes_pass_whatever_their_line_endings(self):
        seeds = {rpt.seed_id("m1"): rpt.seed_bytes("m1")}
        rpt.check_blob("p", rpt.seed_bytes("m1").replace(b"\r\n", b"\n"), seeds)

    def test_isyncs_own_tracking_header_is_allowed(self):
        seeds = {rpt.seed_id("m1"): rpt.seed_bytes("m1")}
        stored = rpt.seed_bytes("m1").replace(b"\r\n", b"\n").replace(b"\n\n", b"\nX-TUID: 9vXpoRk3eNXQ\n\n", 1)
        rpt.check_blob("p", stored, seeds)

    def test_an_x_tuid_lookalike_in_the_body_is_still_a_difference(self):
        seeds = {rpt.seed_id("m1"): rpt.seed_bytes("m1")}
        with self.assertRaises(rpt.Refused):
            rpt.check_blob("p", rpt.seed_bytes("m1") + b"X-TUID: x\r\n", seeds)

    def test_an_added_header_is_refused_and_named_without_its_value(self):
        seeds = {rpt.seed_id("m1"): rpt.seed_bytes("m1")}
        leaked = b"Delivered-To: someone@gmail.com\r\n" + rpt.seed_bytes("m1")
        with self.assertRaises(rpt.Refused) as ctx:
            rpt.check_blob("p", leaked, seeds)
        self.assertIn("Delivered-To", str(ctx.exception))
        self.assertNotIn("someone@gmail.com", str(ctx.exception))

    def test_seeds_are_invented_and_on_the_seed_domain(self):
        for key in rpt.SEEDS:
            self.assertTrue(rpt.seed_id(key).endswith("@" + rpt.SEED_DOMAIN))
            self.assertNotIn(b"gmail", rpt.seed_bytes(key).lower())


class Converge(unittest.TestCase):
    def run_converge(self, snapshots, servers):
        snaps, srv = iter(snapshots), iter(servers)
        return rpt.converge(lambda: None, lambda: next(snaps), lambda: next(srv), settle=lambda: None)

    def test_two_agreeing_runs_settle(self):
        first, final, server = self.run_converge([{"a": b"1"}, {"a": b"1"}], ["s", "s"])
        self.assertEqual(first, final)
        self.assertEqual(server, "s")

    def test_a_first_run_that_differs_is_kept_apart(self):
        first, final, _ = self.run_converge([{"a": b"1"}, {"b": b"2"}, {"b": b"2"}], ["s", "s", "s"])
        self.assertEqual(first, {"a": b"1"})
        self.assertEqual(final, {"b": b"2"})

    def test_a_server_that_keeps_moving_never_settles(self):
        with self.assertRaises(RuntimeError):
            self.run_converge([{"a": b"1"}] * 5, ["s1", "s2", "s3", "s4", "s5"])


class ShapeComparison(unittest.TestCase):
    COMMITTED = REPO / "tests" / "Mailvec.Indexer.Tests" / "ProviderTraces" / "gmail" / "label-lifecycle"

    def copy_of_committed(self):
        d = pathlib.Path(tempfile.mkdtemp()) / "t"
        shutil.copytree(self.COMMITTED, d)
        return d

    def test_a_trace_has_the_same_shape_as_itself(self):
        self.assertEqual(rpt.shape_differences(self.COMMITTED, self.COMMITTED), [])

    def test_file_and_blob_names_are_not_part_of_the_shape(self):
        d = self.copy_of_committed()
        trace = json.loads((d / "trace.json").read_text())
        for step in trace["steps"]:
            renamed = {}
            for path, blob in step["maildir"].items():
                folder, _, name = path.rpartition("/")
                shutil.copyfile(d / "blobs" / blob, d / "blobs" / ("renamed-" + blob))
                renamed[f"{folder}/9999999999.1_1.host{name[name.index(','):]}"] = "renamed-" + blob
            step["maildir"] = renamed
        (d / "trace.json").write_text(json.dumps(trace))
        self.assertEqual(rpt.shape_differences(d, self.COMMITTED), [])

    def test_a_flag_or_server_change_is_a_difference(self):
        d = self.copy_of_committed()
        trace = json.loads((d / "trace.json").read_text())
        step = trace["steps"][0]
        mid = next(iter(step["server"]))
        step["server"][mid]["mailboxes"].append("[Gmail]/Important")
        (d / "trace.json").write_text(json.dumps(trace))
        diffs = rpt.shape_differences(d, self.COMMITTED)
        self.assertEqual(len(diffs), 1)
        self.assertIn("server differs", diffs[0])


class MbsyncRetries(unittest.TestCase):
    class Result:
        def __init__(self, rc, err=""):
            self.returncode, self.stderr = rc, err

    def attempt(self, results, attempts=3):
        results, calls, waits = iter(results), [], []
        def run(*a, **kw):
            calls.append(kw["env"])
            return next(results)
        rpt.run_mbsync("cfg", "pw", attempts=attempts, wait=7, run=run, sleep=waits.append)
        return calls, waits

    def test_a_socket_timeout_is_retried_and_then_succeeds(self):
        calls, waits = self.attempt([
            self.Result(1, "Socket error on imap.gmail.com (192.0.2.1:993): timeout."),
            self.Result(0)])
        self.assertEqual(len(calls), 2)
        self.assertEqual(waits, [7])

    def test_a_refused_login_is_never_retried(self):
        with self.assertRaises(RuntimeError) as ctx:
            self.attempt([self.Result(1, "IMAP command 'LOGIN <user> <pass>' returned an error: NO [AUTHENTICATIONFAILED] Invalid credentials")])
        self.assertIn("not retried", str(ctx.exception))

    def test_a_network_failure_that_persists_gives_up(self):
        with self.assertRaises(RuntimeError) as ctx:
            self.attempt([self.Result(1, "Socket error: Connection reset by peer")] * 3)
        self.assertIn("out of retries", str(ctx.exception))

    def test_the_password_reaches_mbsync_only_under_its_own_name(self):
        import os
        os.environ["GMAIL_TRACE_PASSWORD"] = "outer"
        try:
            calls, _ = self.attempt([self.Result(0)])
        finally:
            del os.environ["GMAIL_TRACE_PASSWORD"]
        self.assertEqual(calls[0][rpt.PASS_ENV], "pw")
        self.assertNotIn("GMAIL_TRACE_PASSWORD", calls[0])


class Workflow(unittest.TestCase):
    """The CI workflow must never publish the work directory: it holds the
    generated mbsyncrc (which names the account) and the synced Maildir. On
    2026-10-26 a failed run tried to, and only a permission error stopped it."""
    TEXT = (REPO / ".github" / "workflows" / "gmail-traces.yml").read_text()

    def test_only_the_output_directory_is_uploaded(self):
        paths = re.findall(r"^\s*path: (.+)$", self.TEXT, re.M)
        self.assertEqual(paths, ["${{ runner.temp }}/trace-out/"])

    def test_the_work_directory_is_outside_what_is_uploaded(self):
        self.assertIn('"$RUNNER_TEMP/trace-work', self.TEXT)
        self.assertNotIn("trace-out/trace-work", self.TEXT)

    def test_the_work_directory_is_removed_however_the_recording_ends(self):
        self.assertRegex(self.TEXT, r"trap '[^']*rm -rf \"\$RUNNER_TEMP/trace-work\"[^']*' EXIT")
        self.assertRegex(self.TEXT, r"if: always\(\)\n\s*run: sudo rm -rf \"\$RUNNER_TEMP/trace-work\"")

    def test_no_trigger_a_pull_request_can_reach(self):
        triggers = self.TEXT.split("\non:\n", 1)[1].split("\n\n", 1)[0]
        self.assertNotIn("pull_request", triggers)
        self.assertNotIn("workflow_run", triggers)


class TraceShape(unittest.TestCase):
    def test_the_written_trace_has_what_the_replay_loader_requires(self):
        trace = rpt.build_trace("label-lifecycle", rpt.TRACES["label-lifecycle"],
                                [{"id": "x", "serverAction": "y", "maildir": {}, "server": {}}], "1.5.1", "2026-10-25")
        self.assertEqual(trace["version"], 1)
        self.assertEqual(trace["source"], "recorded")
        self.assertEqual(set(trace["recorded"]), {"at", "mbsync", "notes"})
        # Exactly the fields ProviderTrace accepts: unknown members are refused there.
        self.assertTrue(set(trace) <= {"version", "provider", "source", "description", "steps", "unsynced", "recorded"})

    def test_the_mbsyncrc_never_holds_the_password(self):
        cfg = rpt.mbsyncrc("user@gmail.com", pathlib.Path("/tmp/mail dir"))
        self.assertIn(f'PassCmd "printenv {rpt.PASS_ENV}"', cfg)
        self.assertIn(f"Timeout {rpt.NETWORK_TIMEOUT_SECONDS}", cfg)
        self.assertIn('Path "/tmp/mail dir/"', cfg)
        self.assertNotIn("Pass ", cfg)


if __name__ == "__main__":
    unittest.main()
