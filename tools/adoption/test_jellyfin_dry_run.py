import copy
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.parse import parse_qs, urlsplit

SCRIPT = Path(__file__).with_name("jellyfin_dry_run.py")
spec = importlib.util.spec_from_file_location("adoption", SCRIPT)
a = importlib.util.module_from_spec(spec)
spec.loader.exec_module(a)
EMPTY = {"IsFavorite": False, "Played": False, "PlayCount": 0, "PlaybackPositionTicks": 0}
WATCHED = {"IsFavorite": True, "Played": True, "PlayCount": 3, "PlaybackPositionTicks": 0,
           "LastPlayedDate": "2026-10-20T12:00:00Z"}


def snapshot(product, state=None):
    prefix = "s" if product == "jellyfin" else "t"
    return {"schemaVersion": 1, "product": product, "version": a.VERSIONS[product][1], "serverId": prefix + "-server",
            "users": [{"Id": prefix + "-user", "Name": "Household"}],
            "libraries": [{"Name": "Movies", "Locations": ["/media"]}],
            "items": [{"Id": prefix + "-item", "Name": "Example", "Type": "Movie", "Path": "/media/movie.mkv",
                       "RunTimeTicks": 1_000_000_000, "ProviderIds": {"Tmdb": "fixture-provider"}}],
            "userItems": {prefix + "-user": [{"Id": prefix + "-item", "UserData": copy.deepcopy(state if state is not None else EMPTY)}]}}


class PlannerTests(unittest.TestCase):
    def setUp(self):
        self.source = snapshot("jellyfin", WATCHED)
        self.target = snapshot("tesserafin")
        self.mapping = {"users": {"s-user": "t-user"}, "paths": []}

    def plan(self):
        before = copy.deepcopy((self.source, self.target, self.mapping))
        with patch("urllib.request.OpenerDirector.open", side_effect=AssertionError("planner must stay offline")):
            result = a.plan(self.source, self.target, self.mapping)
        self.assertEqual((self.source, self.target, self.mapping), before)
        self.assertEqual(result["mode"], "simulation-only")
        self.assertFalse(result["writesToServers"])
        return result

    def status(self):
        return self.plan()["records"][0]["status"]

    def test_favourite_watched_and_date_survive_as_candidate_without_mutation(self):
        result = self.plan()
        row = result["records"][0]
        self.assertEqual(row["status"], "state-candidate")
        self.assertEqual(row["sourceState"], WATCHED)
        self.assertTrue(result["users"][0]["newAccountRequiresPasswordReset"])
        self.assertEqual(result["users"][0]["policyTransfer"], "not-planned")

    def test_nonzero_resume_ticks_are_exact_and_require_matching_known_runtime(self):
        self.source["userItems"]["s-user"][0]["UserData"] = dict(EMPTY, PlaybackPositionTicks=100_000_000)
        self.assertEqual(self.plan()["records"][0]["sourceState"]["PlaybackPositionTicks"], 100_000_000)
        for runtime, status in [(None, "resume-runtime-unknown"), (50_000_000, "resume-runtime-conflict"),
                                (2_000_000_000, "resume-runtime-conflict")]:
            self.target["items"][0]["RunTimeTicks"] = runtime
            self.assertEqual(self.status(), status)

    def test_usernames_and_cross_server_ids_never_match_without_explicit_mapping(self):
        self.mapping = {}
        self.assertEqual(self.status(), "user-unmapped")

    def test_mapped_users_keep_separate_state_even_for_same_media(self):
        self.source["users"].append({"Id": "s-second", "Name": "Other"})
        self.target["users"].append({"Id": "t-second", "Name": "Other"})
        self.source["userItems"]["s-second"] = [{"Id": "s-item", "UserData": dict(EMPTY, PlaybackPositionTicks=100_000_000)}]
        self.target["userItems"]["t-second"] = [{"Id": "t-item", "UserData": dict(EMPTY)}]
        self.mapping["users"]["s-second"] = "t-second"
        rows = self.plan()["records"]
        self.assertEqual({row["targetUserId"] for row in rows}, {"t-user", "t-second"})
        self.assertEqual(next(r for r in rows if r["sourceUserId"] == "s-second")["sourceState"]["PlaybackPositionTicks"], 100_000_000)

    def test_user_merging_and_unknown_user_mapping_are_refused(self):
        for users in [{"missing": "t-user"}, {"s-user": "missing"}]:
            self.mapping["users"] = users
            with self.assertRaises(a.InvalidInput):
                self.plan()
        self.source["users"].append({"Id": "s-second", "Name": "Second"})
        self.source["userItems"]["s-second"] = []
        self.mapping["users"] = {"s-user": "t-user", "s-second": "t-user"}
        with self.assertRaises(a.InvalidInput):
            self.plan()

    def test_longest_path_prefix_with_segment_boundary_and_no_casefold(self):
        self.source["items"][0]["Path"] = "/old/movies/movie.mkv"
        self.mapping["paths"] = [{"source": "/old", "target": "/wrong"}, {"source": "/old/movies", "target": "/media"}]
        self.assertEqual(self.status(), "state-candidate")
        self.source["items"][0]["Path"] = "/old/movies2/movie.mkv"
        self.assertEqual(self.status(), "provider-review")
        self.source["items"][0]["Path"] = "/old/movies/MOVIE.mkv"
        self.assertEqual(self.status(), "provider-review")

    def test_windows_mapping_is_lexical_and_never_reads_media(self):
        self.source["items"][0]["Path"] = r"D:\Movies\movie.mkv"
        self.mapping["paths"] = [{"source": r"D:\Movies", "target": "/media"}]
        self.assertEqual(self.status(), "state-candidate")
        for path in ["../movie", "/media/../movie", "https://other.invalid/movie", "/media/./movie"]:
            self.source["items"][0]["Path"] = path
            self.assertEqual(self.status(), "unsupported-item")

    def test_duplicate_source_or_target_paths_never_merge_item_state(self):
        for which, status in [(self.source, "ambiguous-source-path"), (self.target, "ambiguous-path")]:
            extra = dict(which["items"][0], Id="duplicate")
            which["items"].append(extra)
            self.assertEqual(self.status(), status)
            which["items"].pop()

    def test_provider_only_and_conflicting_provider_matches_need_review(self):
        self.target["items"][0]["Path"] = "/different/cut.mkv"
        self.assertEqual(self.status(), "provider-review")
        self.target["items"][0]["Path"] = "/media/movie.mkv"
        self.target["items"][0]["ProviderIds"] = {"Tmdb": "different"}
        self.assertEqual(self.status(), "provider-conflict")

    def test_missing_state_is_not_false_or_zero(self):
        for value in [{}, {"IsFavorite": True}, dict(EMPTY, PlaybackPositionTicks=-1), dict(EMPTY, Played=1),
                      dict(EMPTY, PlaybackPositionTicks=2**63), dict(EMPTY, LastPlayedDate="not-a-date")]:
            self.source["userItems"]["s-user"][0]["UserData"] = value
            self.assertEqual(self.status(), "source-state-unknown")

    def test_unknown_existing_state_and_conflicts_are_never_overwritten(self):
        self.target["userItems"]["t-user"][0]["UserData"] = {}
        self.assertEqual(self.status(), "target-state-unknown")
        self.target["userItems"]["t-user"][0]["UserData"] = dict(EMPTY, PlaybackPositionTicks=2)
        self.assertEqual(self.status(), "target-state-conflict")
        self.target["userItems"]["t-user"][0]["UserData"] = dict(WATCHED)
        self.assertEqual(self.status(), "unchanged")

    def test_no_state_and_unmatched_or_unsupported_media_are_explicit(self):
        self.source["userItems"]["s-user"][0]["UserData"] = dict(EMPTY)
        self.assertEqual(self.status(), "no-source-state")
        self.source["userItems"]["s-user"][0]["UserData"] = dict(WATCHED)
        self.source["items"][0]["Type"] = "LiveTvChannel"
        self.assertEqual(self.status(), "unsupported-item")
        self.source["items"][0]["Type"] = "Movie"
        self.target["items"] = []
        self.target["userItems"]["t-user"] = []
        self.assertEqual(self.status(), "item-unmatched")

    def test_schema_versions_duplicates_and_partial_user_coverage_fail_closed(self):
        for change in [lambda s: s.update(version="10.11.0"), lambda s: s.update(schemaVersion=2),
                       lambda s: s.update(schemaVersion=True), lambda s: s["users"].append(dict(s["users"][0])),
                       lambda s: s["userItems"].clear(), lambda s: s["userItems"]["s-user"].append({"Id": "unknown"})]:
            self.source = snapshot("jellyfin", WATCHED)
            change(self.source)
            with self.assertRaises(a.InvalidInput):
                self.plan()
        for payload in [b'{"x":1,"x":2}', b'{"x":NaN}']:
            with self.assertRaises(a.InvalidInput):
                a.decode(payload)


class CaptureTests(unittest.TestCase):
    def setUp(self):
        self.calls = []
        self.fault = ""
        owner = self
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                owner.calls.append((self.command, self.path, self.headers.get("X-Emby-Token")))
                url = urlsplit(self.path)
                path = url.path.removeprefix("/jellyfin")
                if owner.fault == "redirect":
                    self.send_response(302)
                    self.send_header("Location", "/forbidden")
                    self.end_headers()
                    return
                if path == "/System/Info/Public":
                    payload = {"Id": "fixture", "ProductName": "Jellyfin Server", "Version": "10.10.7" if owner.fault != "version" else "10.11.0"}
                elif path == "/Users":
                    payload = [{"Id": "alice", "Name": "Alice", "Password": "MUST_NOT_EXPORT"}, {"Id": "bob", "Name": "Bob"}]
                elif path == "/Library/VirtualFolders":
                    payload = [{"Name": "Movies", "Locations": ["/media"]}]
                elif path == "/Items":
                    query = parse_qs(url.query)
                    user = query["userId"][0]
                    start = int(query["startIndex"][0])
                    state = WATCHED if user == "alice" else dict(EMPTY, PlaybackPositionTicks=100_000_000)
                    row = dict(snapshot("jellyfin")["items"][0], Id="movie", UserData=state)
                    if owner.fault == "missing-state":
                        row.pop("UserData")
                    payload = {"TotalRecordCount": 2 if owner.fault in {"incomplete", "repeated"} else 1,
                               "Items": [row] if start == 0 or owner.fault == "repeated" else []}
                else:
                    self.send_error(501)
                    return
                raw = json.dumps(payload).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(raw)))
                self.end_headers()
                self.wfile.write(raw)
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.reader = a.Reader(f"http://127.0.0.1:{self.server.server_port}/jellyfin", "PRIVATE_TOKEN")

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def test_real_http_capture_uses_only_get_and_keeps_two_users_separate(self):
        result = a.capture(self.reader, "jellyfin")
        self.assertEqual(result["userItems"]["alice"][0]["UserData"], WATCHED)
        self.assertEqual(result["userItems"]["bob"][0]["UserData"]["PlaybackPositionTicks"], 100_000_000)
        self.assertNotIn("PRIVATE_TOKEN", json.dumps(result))
        self.assertNotIn("MUST_NOT_EXPORT", json.dumps(result))
        self.assertTrue(all(method == "GET" and token == "PRIVATE_TOKEN" and "PRIVATE_TOKEN" not in url for method, url, token in self.calls))
        queries = [parse_qs(urlsplit(url).query) for _, url, _ in self.calls if "/Items?" in url]
        self.assertEqual([q["userId"] for q in queries], [["alice"], ["bob"]])

    def test_missing_userdata_stays_unknown(self):
        self.fault = "missing-state"
        self.assertEqual(a.capture(self.reader, "jellyfin")["userItems"]["alice"][0]["UserData"], {})

    def test_unknown_version_pagination_faults_and_redirects_are_refused(self):
        for fault in ["version", "incomplete", "repeated", "redirect"]:
            self.fault = fault
            with self.assertRaises(a.InvalidInput):
                a.capture(self.reader, "jellyfin")
        self.assertFalse(any("forbidden" in url for _, url, _ in self.calls))

    def test_request_byte_and_time_budgets_refuse(self):
        for field, value in [("requests_remaining", 0), ("bytes_remaining", 1), ("deadline", 0)]:
            reader = a.Reader(self.reader.base_url, "PRIVATE_TOKEN")
            setattr(reader, field, value)
            with self.assertRaises(a.InvalidInput):
                reader.get("/System/Info/Public")


class CommandTests(unittest.TestCase):
    def test_url_policy_and_read_allowlist(self):
        for url in ["https://u:p@host", "https://host?token=secret", "file:///tmp/a", "http://example.invalid"]:
            with self.assertRaises(a.InvalidInput):
                a.Reader(url, "token")
        reader = a.Reader("http://example.invalid", "token", allow_http=True)
        with self.assertRaises(a.InvalidInput):
            reader.get("/Videos/anything/stream")

    def test_private_exclusive_output_and_no_partial_on_serialization_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "private.json"
            a.write_private(output, {"ok": True})
            self.assertEqual(stat.S_IMODE(output.stat().st_mode), 0o600)
            with self.assertRaises(FileExistsError):
                a.write_private(output, {"ok": False})
            self.assertEqual(a.read_json(output), {"ok": True})
            with self.assertRaises(ValueError):
                a.write_private(Path(directory) / "bad.json", {"bad": float("nan")})
            self.assertEqual(sorted(p.name for p in Path(directory).iterdir()), ["private.json"])

    def test_cli_plans_without_changing_inputs_and_reports_only_redacted_errors(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, value in [("source", snapshot("jellyfin", WATCHED)), ("target", snapshot("tesserafin")),
                                ("mapping", {"users": {"s-user": "t-user"}})]:
                (root / name).write_text(json.dumps(value))
            before = {p.name: p.read_bytes() for p in root.iterdir()}
            args = [sys.executable, str(SCRIPT), "plan", "--source", str(root / "source"), "--target", str(root / "target"),
                    "--mapping", str(root / "mapping"), "--output", str(root / "report")]
            result = subprocess.run(args, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(a.read_json(root / "report")["counts"], {"state-candidate": 1})
            self.assertEqual(before, {name: (root / name).read_bytes() for name in before})
            failed = subprocess.run(args, capture_output=True, text=True, timeout=10)
            self.assertEqual(failed.returncode, 2)
            self.assertNotIn(str(root), failed.stderr)
            self.assertNotIn("Traceback", failed.stderr)


if __name__ == "__main__":
    unittest.main()
