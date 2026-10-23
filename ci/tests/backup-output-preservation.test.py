#!/usr/bin/env python3
"""Run the actual backup CLI against a controlled Docker process boundary.

These tests protect recovery artifacts, not database/Docker restore acceptance.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
FAKE_DOCKER = '''#!/usr/bin/env python3
import json, os, pathlib, sys
with open(os.environ["DOCKER_CALLS"], "a") as log:
    log.write(json.dumps(sys.argv[1:]) + "\\n")
if sys.argv[1] == "inspect":
    print("false")
    sys.exit(0)
if sys.argv[1] == "run":
    race = os.environ.get("RACE_PATH")
    if race:
        pathlib.Path(race).write_bytes(b"other writer")
    sys.stdout.buffer.write(b"archive bytes")
    sys.exit(int(os.environ.get("ARCHIVE_EXIT", "0")))
sys.exit(80)
'''


class BackupOutputPreservationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="backup-output-")
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)
        self.archive = self.work / "state with spaces.tgz"
        self.calls = self.work / "calls.jsonl"
        docker = self.work / "docker"
        docker.write_text(FAKE_DOCKER)
        docker.chmod(0o700)
        self.env = dict(os.environ, PATH=f"{self.work}:{os.environ['PATH']}",
                        DOCKER_CALLS=str(self.calls))

    def invoke(self, **extra_env):
        return subprocess.run(
            ["bash", str(ROOT / "docker/backup.sh"), "--out", str(self.archive),
             "--config", "fixture-config", "--data", "fixture-data",
             "--container", "fixture-server"],
            env=dict(self.env, **extra_env), capture_output=True, text=True,
            timeout=10, check=False)

    def artifacts(self):
        return [Path(str(self.archive) + suffix)
                for suffix in ("", ".sha256", ".manifest.json")]

    def assert_only_inspected(self):
        calls = [json.loads(line) for line in self.calls.read_text().splitlines()]
        self.assertEqual([call[0] for call in calls], ["inspect"])

    def assert_no_work_left(self):
        self.assertFalse(Path(str(self.archive) + ".lock").exists())
        self.assertEqual(list(self.work.glob(self.archive.name + ".partial.*")), [])

    def test_existing_archive_or_each_sidecar_refuses_without_losing_any_bytes(self):
        for index in range(3):
            with self.subTest(index=index):
                artifact = self.artifacts()[index]
                artifact.write_bytes(b"previous recovery point")
                self.calls.unlink(missing_ok=True)
                result = self.invoke()
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("already exists", result.stderr)
                self.assert_only_inspected()
                self.assertEqual(artifact.read_bytes(), b"previous recovery point")
                self.assertEqual([p.exists() for p in self.artifacts()],
                                 [i == index for i in range(3)])
                self.assert_no_work_left()
                artifact.unlink()

    def test_dangling_symlink_is_an_existing_destination(self):
        for artifact in self.artifacts():
            with self.subTest(path=artifact):
                artifact.symlink_to(self.work / "missing target")
                self.calls.unlink(missing_ok=True)
                result = self.invoke()
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("already exists", result.stderr)
                self.assert_only_inspected()
                self.assertTrue(artifact.is_symlink())
                self.assert_no_work_left()
                artifact.unlink()

    def test_existing_lock_refuses_and_does_not_remove_another_invocations_lock(self):
        lock = Path(str(self.archive) + ".lock")
        lock.mkdir()
        result = self.invoke()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("locked", result.stderr)
        self.assert_only_inspected()
        self.assertTrue(lock.is_dir())
        self.assertFalse(any(p.exists() for p in self.artifacts()))

    def test_failed_snapshot_cleans_only_its_temporary_output(self):
        result = self.invoke(ARCHIVE_EXIT="79")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("archive step errored", result.stderr)
        self.assertFalse(any(p.exists() for p in self.artifacts()))
        self.assert_no_work_left()

    def test_success_publishes_private_matching_archive_and_sidecars(self):
        result = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        archive, checksum, manifest = self.artifacts()
        self.assertEqual(archive.read_bytes(), b"archive bytes")
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        self.assertEqual(checksum.read_text(), f"{digest}  {archive.name}\n")
        data = json.loads(manifest.read_text())
        self.assertEqual(data["sha256"], digest)
        self.assertEqual(data["size_bytes"], len(archive.read_bytes()))
        for artifact in self.artifacts():
            self.assertEqual(artifact.stat().st_mode & 0o777, 0o600)
        self.assert_no_work_left()

    def test_late_destination_collision_preserves_other_writer_and_cleans_own_sidecars(self):
        for index in range(3):
            with self.subTest(index=index):
                artifact = self.artifacts()[index]
                result = self.invoke(RACE_PATH=str(artifact))
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(artifact.read_bytes(), b"other writer")
                self.assertEqual([p.exists() for p in self.artifacts()],
                                 [i == index for i in range(3)])
                self.assert_no_work_left()
                artifact.unlink()


if __name__ == "__main__":
    unittest.main()
