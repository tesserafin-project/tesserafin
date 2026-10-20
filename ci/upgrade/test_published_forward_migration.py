"""Preflight rejection controls; mock Docker, never migration evidence."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


class PublishedImagePreflightTests(unittest.TestCase):
    def run_fixture(self, mode):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "ci/upgrade").mkdir(parents=True)
            (root / "docker").mkdir()
            (root / "bin").mkdir()
            source = Path(__file__).with_name("published-forward-migration.sh")
            shutil.copyfile(source, root / "ci/upgrade/published-forward-migration.sh")
            (root / "docker/upgrade-roundtrip.sh").write_text(
                '#!/bin/bash\nprintf "%s\\n" "$@" > harness-args\n'
                'if [[ "$FIXTURE_MODE" == harness-failure ]]; then exit 7; fi\n'
            )
            git = root / "bin/git"
            git.write_text('#!/bin/sh\nprintf "%040d\\n" 0\n')
            git.chmod(0o755)
            docker = root / "bin/docker"
            docker.write_text('''#!/usr/bin/env python3
import json, os, sys
mode = os.environ['FIXTURE_MODE']
args = sys.argv[1:]
if args[0] == 'manifest':
    role = 'baseline' if '@sha256:' in args[-1] else 'candidate'
    if mode == 'missing-' + role:
        print('manifest unknown: synthetic fixture', file=sys.stderr)
        sys.exit(1)
    print('{}')
elif args[0] == 'pull':
    pass
elif args[:2] == ['image', 'inspect']:
    repo = 'ghcr.io/tesserafin-project/tesserafin-server@sha256:'
    images = []
    for revision, digest in [
        ('44f5ab62b522684b4fa58ed10de80b8c6a7bb392', 'f' * 64),
        ('bb2234aebbf0ff2c1cbe291df48f2ffa62beea46', 'a' * 64),
    ]:
        images.append({'Architecture':'amd64', 'Os':'linux',
            'Config':{'Labels':{'org.opencontainers.image.revision':revision,
                               'org.opencontainers.image.version':'1.0.0'}},
            'RepoDigests':[repo + digest]})
    candidate = images[1]
    if mode == 'wrong-revision':
        candidate['Config']['Labels']['org.opencontainers.image.revision'] = '0' * 40
    if mode == 'wrong-architecture': candidate['Architecture'] = 'arm64'
    if mode == 'missing-digest': candidate['RepoDigests'] = []
    if mode == 'ambiguous-digest': candidate['RepoDigests'].append(repo + 'b' * 64)
    print(json.dumps(images))
else:
    sys.exit('unexpected mock Docker invocation')
''')
            docker.chmod(0o755)
            env = dict(os.environ, PATH=str(root / "bin") + os.pathsep + os.environ["PATH"],
                       FIXTURE_MODE=mode)
            proc = subprocess.run(["bash", "ci/upgrade/published-forward-migration.sh"],
                                  cwd=root, env=env, capture_output=True, text=True, timeout=10)
            args_path = root / "harness-args"
            args = args_path.read_text().splitlines() if args_path.exists() else None
            report = root / "artifacts/forward-migration/images.json"
            identity = json.loads(report.read_text()) if report.exists() else None
            return proc, args, identity

    def test_unreadable_manifests_never_start_the_harness(self):
        for mode in ("missing-baseline", "missing-candidate"):
            with self.subTest(mode=mode):
                proc, args, report = self.run_fixture(mode)
                self.assertNotEqual(proc.returncode, 0)
                self.assertIn("BLOCKED:", proc.stderr)
                self.assertIsNone(args)
                self.assertIsNone(report)

    def test_unverified_image_identity_never_starts_the_harness(self):
        for mode in ("wrong-revision", "wrong-architecture", "missing-digest", "ambiguous-digest"):
            with self.subTest(mode=mode):
                proc, args, report = self.run_fixture(mode)
                self.assertNotEqual(proc.returncode, 0)
                self.assertIsNone(args)
                self.assertIsNone(report)

    def test_verified_candidate_is_passed_by_digest_with_migration_required(self):
        proc, args, report = self.run_fixture("valid")
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(args, ["--baseline",
            "ghcr.io/tesserafin-project/tesserafin-server@sha256:fd1fa9e0f5a28a07e5872cc5ff13257a92d988717a33519f62c4b26c6ab36249",
            "--candidate", "ghcr.io/tesserafin-project/tesserafin-server@sha256:" + "a" * 64,
            "--require-migration"])
        self.assertEqual(report["candidate"]["architecture"], "linux/amd64")

    def test_original_harness_failure_is_preserved(self):
        proc, args, _ = self.run_fixture("harness-failure")
        self.assertEqual(proc.returncode, 7)
        self.assertIsNotNone(args)


if __name__ == "__main__":
    unittest.main()
