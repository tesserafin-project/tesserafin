import copy
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.parse import parse_qs, urlsplit

import jellyfin_dry_run as dry
import jellyfin_state_apply as app
from test_jellyfin_dry_run import snapshot, EMPTY, WATCHED

USER_A = "11111111111111111111111111111111"
USER_B = "22222222222222222222222222222222"
ITEM = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
TOKEN = "private-adoption-test-token"


def inputs():
    source = snapshot("jellyfin", WATCHED)
    target = snapshot("tesserafin")
    source["users"].append({"Id": "s-b", "Name": "Other source"})
    source["userItems"]["s-b"] = [{"Id": "s-item", "UserData": dict(EMPTY, PlaybackPositionTicks=100_000_000)}]
    target["users"] = [{"Id": USER_A, "Name": "Target Alice"}, {"Id": USER_B, "Name": "Target Bob"}]
    target["items"][0]["Id"] = ITEM
    target["userItems"] = {u: [{"Id": ITEM, "UserData": dict(EMPTY)}] for u in (USER_A, USER_B)}
    mapping = {"users": {"s-user": USER_A, "s-b": USER_B}, "paths": []}
    return source, target, mapping


class ApplyTests(unittest.TestCase):
    def setUp(self):
        self.source, self.target, self.mapping = inputs()
        self.plan = app.prepare(self.source, self.target, self.mapping)
        self.states = {u: dict(EMPTY, LastPlayedDate=None, Likes=None, Rating=None, Key="stable-" + u)
                       for u in (USER_A, USER_B)}
        self.calls = []
        self.writes = []
        self.fault = ""
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def reply(self, payload, status=200):
                raw = json.dumps(payload).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(raw)))
                self.end_headers()
                self.wfile.write(raw)

            def do_GET(self):
                owner.calls.append(("GET", self.path, self.headers.get("X-Emby-Token")))
                url = urlsplit(self.path)
                query = parse_qs(url.query)
                if owner.fault == "redirect":
                    self.send_response(302)
                    self.send_header("Location", "/must-not-receive-token")
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                elif url.path == "/System/Info/Public":
                    self.reply({"ProductName": "Wrong" if owner.fault == "product" else "Tesserafin Server",
                                "Version": "1.0.0", "Id": "wrong" if owner.fault == "server" else owner.target["serverId"]})
                elif url.path == "/Users":
                    users = copy.deepcopy(owner.target["users"])
                    if owner.fault == "user":
                        users[0]["Name"] = "Renamed account"
                    self.reply(users)
                elif url.path == "/Items":
                    user = query["userId"][0]
                    item = copy.deepcopy(owner.target["items"][0])
                    if owner.fault == "path":
                        item["Path"] = "/different/cut.mkv"
                    if owner.fault == "runtime":
                        item["RunTimeTicks"] += 1
                    item["UserData"] = copy.deepcopy(owner.states[user])
                    rows = [item]
                    if owner.fault == "ambiguous" and "ids" not in query:
                        rows.append(dict(item, Id="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"))
                    self.reply({"Items": rows, "TotalRecordCount": len(rows)})
                else:
                    self.reply({}, 404)

            def do_POST(self):
                owner.calls.append(("POST", self.path, self.headers.get("X-Emby-Token")))
                url = urlsplit(self.path)
                query = parse_qs(url.query)
                if url.path != f"/UserItems/{ITEM}/UserData" or set(query) != {"userId"}:
                    self.reply({}, 400)
                    return
                value = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                owner.writes.append((query["userId"][0], value))
                if owner.fault == "before-commit":
                    owner.fault = ""
                    self.reply({}, 500)
                    return
                owner.states[query["userId"][0]].update(value)
                if owner.fault == "after-commit":
                    owner.fault = ""
                    self.reply({}, 500)
                    return
                self.reply(owner.states[query["userId"][0]])

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.url = f"http://127.0.0.1:{self.server.server_port}"
        self.temp = tempfile.TemporaryDirectory()
        self.journal = Path(self.temp.name) / "journal"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self.temp.cleanup()

    def run_plan(self, rollback=False, plan=None, approved=None):
        plan = plan or self.plan
        return app.execute(app.Api(self.url, TOKEN), plan, approved or app.digest(plan),
                           self.journal, rollback=rollback, quiescent=True)

    def test_real_http_keeps_users_distinct_exact_resume_and_idempotent_rollback(self):
        original = copy.deepcopy(self.states)
        self.run_plan()
        expected = {op["userId"]: op["after"] for op in self.plan["operations"]}
        for user in (USER_A, USER_B):
            self.assertEqual({k: self.states[user][k] for k in app.FIELDS}, expected[user])
            self.assertIsNone(self.states[user]["LastPlayedDate"])
        self.assertTrue(self.states[USER_A]["IsFavorite"])
        self.assertEqual(self.states[USER_B]["PlaybackPositionTicks"], 100_000_000)
        self.run_plan()
        self.assertEqual(len(self.writes), 2)
        self.run_plan(rollback=True)
        self.run_plan(rollback=True)
        self.assertEqual(self.states, original)
        self.assertEqual(len(self.writes), 4)
        self.assertTrue(all(set(body) == set(app.FIELDS) for _, body in self.writes))
        with self.assertRaises(dry.InvalidInput):
            self.run_plan()
        self.assertEqual(stat.S_IMODE(self.journal.stat().st_mode), 0o700)
        for entry in self.journal.iterdir():
            self.assertEqual(stat.S_IMODE(entry.stat().st_mode), 0o600)
            self.assertNotIn(TOKEN, entry.read_text())
        self.assertTrue(all(call[2] == TOKEN for call in self.calls))

    def test_lost_post_response_reconciles_committed_intent_without_double_write(self):
        self.fault = "after-commit"
        with self.assertRaises(dry.InvalidInput):
            self.run_plan()
        self.assertEqual(len(self.writes), 1)
        intents = list(self.journal.glob("*.intent.json"))
        self.assertEqual(len(intents), 1)
        self.assertFalse(list(self.journal.glob("*.applied.json")))
        self.run_plan()
        self.assertEqual(len(self.writes), 2)
        self.assertTrue(self.states[USER_A]["Played"])

    def test_post_failure_before_commit_can_resume_or_rollback_without_inventing_change(self):
        self.fault = "before-commit"
        with self.assertRaises(dry.InvalidInput):
            self.run_plan()
        self.assertTrue(all(app.state(s) == EMPTY for s in self.states.values()))
        self.run_plan(rollback=True)
        self.assertEqual(len(self.writes), 1)

    def test_intent_publication_failure_never_writes_and_published_intent_can_resume(self):
        original = app.Journal.put
        for published in (False, True):
            with self.subTest(published=published):
                self.journal = Path(self.temp.name) / f"intent-{published}"

                def fail_intent(journal, name, value):
                    if name.endswith(".intent.json"):
                        if published:
                            original(journal, name, value)
                        raise OSError("controlled journal failure")
                    return original(journal, name, value)

                with patch.object(app.Journal, "put", fail_intent):
                    with self.assertRaises(OSError):
                        self.run_plan()
                self.assertEqual(self.writes, [])
                self.assertEqual(bool(list(self.journal.glob("*.intent.json"))), published)
        # A durable intent followed by a crash before POST is safe to resume.
        self.run_plan()
        self.assertEqual(len(self.writes), 2)

    def test_applied_marker_failure_after_commit_resumes_without_duplicate_post(self):
        original = app.Journal.put

        def fail_marker(journal, name, value):
            if name.endswith(".applied.json"):
                raise OSError("controlled disk-full after commit")
            return original(journal, name, value)

        with patch.object(app.Journal, "put", fail_marker):
            with self.assertRaises(OSError):
                self.run_plan()
        self.assertEqual(len(self.writes), 1)
        self.run_plan()
        self.assertEqual(len(self.writes), 2)
        self.assertTrue(self.states[USER_A]["Played"])

    def test_journal_parent_fsync_precedes_network_and_failure_never_mutates(self):
        original = app.sync_dir
        parents = []

        def fail_parent(path):
            parents.append(path)
            if path == self.journal.parent:
                self.assertEqual(self.calls, [])
                raise OSError("controlled parent fsync failure")
            return original(path)

        with patch.object(app, "sync_dir", fail_parent):
            with self.assertRaises(OSError):
                self.run_plan()
        self.assertEqual(parents, [self.journal.parent])
        self.assertEqual(self.calls, [])
        self.assertEqual(self.writes, [])
        self.assertTrue(self.journal.is_dir())
        self.run_plan()
        self.assertEqual(len(self.writes), 2)

    def test_non_posix_execution_refuses_before_network_or_journal_creation(self):
        with patch.object(app.os, "name", "nt"):
            with self.assertRaises(dry.InvalidInput):
                self.run_plan()
        self.assertEqual(self.calls, [])
        self.assertFalse(self.journal.exists())

    def test_rollback_response_lost_after_commit_is_reconciled_without_second_rollback_write(self):
        self.run_plan()
        self.fault = "after-commit"
        with self.assertRaises(dry.InvalidInput):
            self.run_plan(rollback=True)
        self.assertEqual(len(self.writes), 3)
        self.run_plan(rollback=True)
        self.assertEqual(len(self.writes), 4)
        self.assertTrue(all(app.state(value) == EMPTY for value in self.states.values()))

    def test_detected_intervening_edits_survive_rollback_and_reapply_refusal(self):
        self.run_plan()
        self.states[USER_A]["PlaybackPositionTicks"] = 99
        changed = copy.deepcopy(self.states[USER_A])
        with self.assertRaises(dry.InvalidInput):
            self.run_plan(rollback=True)
        self.assertEqual(self.states[USER_A], changed)
        self.assertEqual(len(self.writes), 2)

    def test_applied_marker_does_not_allow_reapply_after_external_clearing(self):
        self.run_plan()
        self.states[USER_A].update(EMPTY)
        with self.assertRaises(dry.InvalidInput):
            self.run_plan()
        self.assertEqual(self.states[USER_A]["PlayCount"], 0)
        self.assertEqual(len(self.writes), 2)

    def test_guard_edit_blocks_rollback_even_when_imported_fields_are_equal(self):
        self.run_plan()
        self.states[USER_A]["Rating"] = 8
        with self.assertRaises(dry.InvalidInput):
            self.run_plan(rollback=True)
        self.assertEqual(self.states[USER_A]["Rating"], 8)
        self.assertEqual(len(self.writes), 2)

    def test_identity_product_runtime_path_or_new_ambiguity_refused_before_write(self):
        for fault in ("server", "product", "user", "path", "runtime", "ambiguous"):
            with self.subTest(fault=fault):
                self.fault = fault
                self.journal = Path(self.temp.name) / fault
                with self.assertRaises(dry.InvalidInput):
                    self.run_plan()
                self.assertEqual(self.writes, [])

    def test_target_conflict_and_preexisting_identical_state_are_not_owned(self):
        first = self.plan["operations"][0]
        self.states[first["userId"]].update(first["after"])
        with self.assertRaises(dry.InvalidInput):
            self.run_plan()
        self.assertFalse(list(self.journal.glob("*.intent.json")))
        self.assertEqual(self.writes, [])

    def test_hash_quiescence_unknown_schema_and_wrong_journal_refused(self):
        with self.assertRaises(dry.InvalidInput):
            self.run_plan(approved="0" * 64)
        self.assertEqual(self.calls, [])
        with self.assertRaises(dry.InvalidInput):
            app.execute(app.Api(self.url, TOKEN), self.plan, app.digest(self.plan), self.journal)
        other = copy.deepcopy(self.plan)
        other["schemaVersion"] = True
        with self.assertRaises(dry.InvalidInput):
            self.run_plan(plan=other)
        self.run_plan()
        other = copy.deepcopy(self.plan)
        other["mappingSha256"] = "0" * 64
        with self.assertRaises(dry.InvalidInput):
            self.run_plan(plan=other)
        self.assertEqual(len(self.writes), 2)

    def test_journal_refuses_symlink_public_directory_and_active_or_stale_lock(self):
        self.journal.mkdir(mode=0o700)
        (self.journal / ".lock").mkdir()
        with self.assertRaises(FileExistsError):
            self.run_plan()
        (self.journal / ".lock").rmdir()
        self.journal.chmod(0o755)
        with self.assertRaises(dry.InvalidInput):
            self.run_plan()
        self.journal.chmod(0o700)
        elsewhere = Path(self.temp.name) / "elsewhere"
        elsewhere.write_text("{}")
        (self.journal / "manifest.json").symlink_to(elsewhere)
        with self.assertRaises(OSError):
            self.run_plan()
        self.assertEqual(elsewhere.read_text(), "{}")
        self.assertEqual(self.calls, [])

    def test_http_redirect_is_not_followed_with_credential(self):
        self.fault = "redirect"
        with self.assertRaises(dry.InvalidInput):
            self.run_plan()
        self.assertEqual([call[1] for call in self.calls], ["/System/Info/Public"])
        self.assertEqual(self.writes, [])

    def test_offline_prepare_hash_cli_approval_and_no_input_mutation(self):
        original = copy.deepcopy((self.source, self.target, self.mapping))
        result = app.prepare(self.source, self.target, self.mapping)
        self.assertEqual((self.source, self.target, self.mapping), original)
        self.assertEqual(app.digest(result), app.digest(copy.deepcopy(result)))
        self.assertNotIn("LastPlayedDate", result["operations"][0]["after"])
        plan_file = Path(self.temp.name) / "plan.json"
        dry.write_private(plan_file, result)
        token_file = Path(self.temp.name) / "token"
        token_file.write_text(TOKEN)
        completed = subprocess.run([sys.executable, str(Path(app.__file__)), "apply", "--plan", str(plan_file),
                                    "--approve-sha256", "0" * 64, "--journal", str(self.journal),
                                    "--url", self.url, "--token-file", str(token_file), "--quiescent-target"],
                                   text=True, capture_output=True)
        self.assertEqual(completed.returncode, 2)
        self.assertNotIn(TOKEN, completed.stderr + completed.stdout)
        self.assertEqual(self.calls, [])

    def test_prepare_never_merges_nonempty_target_even_when_planner_allows_addition(self):
        self.target["userItems"][USER_A][0]["UserData"] = dict(EMPTY, IsFavorite=True)
        plan = app.prepare(self.source, self.target, self.mapping)
        self.assertEqual([op["userId"] for op in plan["operations"]], [USER_B])
        self.assertEqual(plan["simulationCounts"]["nonempty-target-not-supported"], 1)

    def test_windows_target_path_uses_lexical_normalization_without_changing_identity(self):
        self.source["items"][0]["Path"] = "C:/Media/film.mkv"
        self.target["items"][0]["Path"] = "C:\\Media\\film.mkv"
        self.plan = app.prepare(self.source, self.target, self.mapping)
        self.run_plan()
        self.assertEqual(len(self.writes), 2)


if __name__ == "__main__":
    unittest.main()
