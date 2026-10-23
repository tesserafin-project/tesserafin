#!/usr/bin/env python3
"""Check that an empty/failed rehearsal cannot pass or retain arbitrary output."""
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("evidence", ROOT / "ci/recovery-evidence.py")
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)


class RecoveryEvidenceTests(unittest.TestCase):
    def complete_log(self):
        return "\n".join(["== 2a. extended ==", evidence.SEED_MARKER,
                          "== 5. restored ==", evidence.RESTORE_MARKER, evidence.COMPLETE_MARKER])

    def test_complete_success_requires_all_independent_oracles(self):
        data = evidence.summarize(self.complete_log(), 0)
        self.assertTrue(data["accepted"])
        self.assertEqual(data["stages"], ["extended-seed", "restored-state"])
        for marker in (evidence.SEED_MARKER, evidence.RESTORE_MARKER, evidence.COMPLETE_MARKER):
            with self.subTest(marker=marker):
                self.assertFalse(evidence.summarize(self.complete_log().replace(marker, ""), 0)["accepted"])

    def test_split_phases_require_their_own_markers(self):
        source = "\n".join(["== 2a. extended ==", evidence.SEED_MARKER, evidence.SOURCE_MARKER])
        recover = "\n".join([evidence.ABSENT_MARKER, "== 3d. off-host ==", evidence.OFFHOST_MARKER,
                              "== 5. restored ==", evidence.RESTORE_MARKER, evidence.COMPLETE_MARKER])
        self.assertTrue(evidence.summarize(source, 0, "source")["accepted"])
        self.assertEqual(evidence.summarize(recover, 0, "recover")["stages"],
                         ["off-host-verification", "restored-state"])
        for phase, log in (("source", source), ("recover", recover)):
            self.assertTrue(evidence.summarize(log, 0, phase)["accepted"])
            self.assertFalse(evidence.summarize(log, 0, "all")["accepted"])
            for marker in evidence.REQUIRED[phase]:
                with self.subTest(phase=phase, marker=marker):
                    self.assertFalse(evidence.summarize(log.replace(marker, ""), 0, phase)["accepted"])
        # A one-host run cannot stand in for either half of the disk-loss split.
        self.assertFalse(evidence.summarize(self.complete_log(), 0, "recover")["accepted"])
        self.assertFalse(evidence.summarize(self.complete_log(), 0, "source")["accepted"])

    def test_failure_and_empty_log_cannot_pass(self):
        self.assertFalse(evidence.summarize("", 0)["accepted"])
        self.assertFalse(evidence.summarize(self.complete_log(), 1)["accepted"])
        self.assertFalse(evidence.summarize(self.complete_log() + "\n  FAIL  anything", 0)["accepted"])

    def test_arbitrary_output_and_secrets_do_not_enter_retained_evidence(self):
        raw = '\n'.join([self.complete_log(), 'AccessToken=private-secret',
                         '  PASS  private-secret', '  FAIL  private-secret',
                         '== 5. private-secret ==',
                         'urllib.error.HTTPError: HTTP Error 403: private-secret',
                         '  File "/repo/docker/recovery-state.py", line 78, in capture',
                         'ValueError: private-secret'])
        data = evidence.summarize(raw, 1)
        self.assertNotIn("private-secret", json.dumps(data))
        self.assertEqual(data["http_error_codes"], ["403"])
        self.assertEqual(data["oracle_error_lines"], [78])
        self.assertFalse(data["accepted"])

    def test_only_allowlisted_diagnostics_are_retained(self):
        raw = "\n".join([
            "DIAG restored-ready restart_count=1 status=running oom_killed=false exit_code=0"
            " health_http=200 startup_config_http=401 users_me_http=503",
            "DIAG restored-users-me-window interval_s=2 users_me_http=503,503,200",
            "DIAG restored-ready status=private-secret",
            "DIAG restored-ready health_http=200 token=private-secret",
            "DIAG private-secret health_http=200",
            "DIAG restored-ready users_me_http=503,private-secret",
            "DIAG restored-ready health_http=200 health_http=503"])
        data = evidence.summarize(raw, 1)
        self.assertNotIn("private-secret", json.dumps(data))
        self.assertEqual(data["diagnostics"], [
            {"label": "restored-ready", "restart_count": "1", "status": "running",
             "oom_killed": "false", "exit_code": "0", "health_http": "200",
             "startup_config_http": "401", "users_me_http": "503"},
            {"label": "restored-users-me-window", "interval_s": "2",
             "users_me_http": "503,503,200"}])
        self.assertFalse(data["accepted"])


if __name__ == "__main__":
    unittest.main()
