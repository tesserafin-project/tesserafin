#!/usr/bin/env python3
"""Exercise backup/restore's container-state refusal through their real entry points.

Docker is replaced at the process boundary. These are CLI orchestration tests,
not a database, Docker daemon or restore round-trip certification.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
EXISTING_CHECKSUM = f"{hashlib.sha256(b'existing archive').hexdigest()}  state.tgz\n".encode()
FAKE_DOCKER = '''#!/usr/bin/env python3
import json, os, sys
with open(os.environ["DOCKER_CALLS"], "a") as log:
    log.write(json.dumps(sys.argv[1:]) + "\\n")
if sys.argv[1] == "inspect":
    print(os.environ["INSPECT_STATE"], end="")
    sys.exit(int(os.environ["INSPECT_EXIT"]))
if sys.argv[1] == "run":
    print("test boundary: volume helper reached", file=sys.stderr)
    sys.exit(79)
if sys.argv[1] in ("stop", "start"):
    sys.exit(0)
sys.exit(80)
'''


class ContainerPreflightTests(unittest.TestCase):
    def invoke(self, script, state, inspect_exit=0, force=False):
        with tempfile.TemporaryDirectory(prefix="backup-preflight-") as directory:
            work = Path(directory)
            docker = work / "docker"
            docker.write_text(FAKE_DOCKER)
            docker.chmod(0o700)
            archive = work / "state.tgz"
            # Existing backup artifacts must survive a rejected preflight. No real
            # archive is needed: inspection must refuse before any archive access.
            archive.write_bytes(b"existing archive")
            manifest = work / "state.tgz.manifest.json"
            manifest.write_bytes(b"existing manifest")
            checksum = work / "state.tgz.sha256"
            checksum.write_bytes(EXISTING_CHECKSUM)
            calls = work / "calls.jsonl"
            option = "--out" if script == "backup" else "--archive"
            args = ["bash", str(ROOT / "docker" / f"{script}.sh"), option,
                    str(archive), "--config", "fixture-config", "--data",
                    "fixture-data", "--container", "fixture-server"]
            if force:
                args.append("--force")
            env = dict(os.environ, PATH=f"{work}:{os.environ['PATH']}",
                       DOCKER_CALLS=str(calls), INSPECT_STATE=state,
                       INSPECT_EXIT=str(inspect_exit))
            # Happy preflight controls need to reach the helper, without an
            # unrelated checksum failure intercepting restore first.
            if state in ("false", "true") and inspect_exit == 0:
                checksum.unlink()
            result = subprocess.run(args, env=env, capture_output=True, text=True,
                                    timeout=10, check=False)
            recorded = [json.loads(line) for line in calls.read_text().splitlines()] if calls.exists() else []
            artifacts = [path.read_bytes() if path.exists() else None
                         for path in (archive, manifest, checksum)]
            return result, recorded, artifacts

    def test_unobservable_state_refuses_before_any_volume_or_container_mutation(self):
        invalid = [("", 1), ("false", 1), ("", 0), ("unknown", 0), ("false\ntrue", 0)]
        for script, force in (("backup", False), ("restore", False), ("restore", True)):
            for state, code in invalid:
                with self.subTest(script=script, force=force, state=state, exit=code):
                    result, calls, artifacts = self.invoke(script, state, code, force)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertEqual([call[0] for call in calls], ["inspect"])
                    self.assertIn("cannot determine", result.stderr.lower())
                    self.assertEqual(artifacts, [b"existing archive", b"existing manifest", EXISTING_CHECKSUM])

    def test_observed_stopped_container_reaches_volume_helper(self):
        for script in ("backup", "restore"):
            with self.subTest(script=script):
                result, calls, _ = self.invoke(script, "false")
                self.assertIn("test boundary: volume helper reached", result.stderr)
                self.assertEqual([call[0] for call in calls], ["inspect", "run"])
                self.assertIn("--type", calls[0])
                self.assertIn("container", calls[0])

    def test_running_backup_stops_container_before_snapshot(self):
        result, calls, _ = self.invoke("backup", "true")
        self.assertIn("test boundary: volume helper reached", result.stderr)
        self.assertEqual([call[0] for call in calls[:3]], ["inspect", "stop", "run"])


if __name__ == "__main__":
    unittest.main()
