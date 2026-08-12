"""Admit the locked MSYS2 archives into a deterministic bundle (#236, W1-R).

INGRESS, not consumption. This is the only place in W1 that is allowed to talk
to the live MSYS2 repository, and it may do exactly one thing there: fetch the
exact filenames a reviewed, committed lock already names. It resolves nothing,
expands no group, and follows no dependency — the closure was decided when the
lock was reviewed.

Everything is fail-closed:

  * the repository databases are re-fetched and their SHA-256 must equal what
    the lock recorded, so a database that moved under the lock is a stop rather
    than a silent re-resolution;
  * every archive's SHA-256 must equal the locked value BEFORE it is admitted;
  * a response that redirects to a different filename is rejected — a mirror
    quietly serving `…-2-any.pkg.tar.zst` for a `…-1-…` request would otherwise
    be admitted under the wrong identity;
  * a detached signature is fetched where MSYS2 publishes one, and whether it
    exists is recorded per package rather than assumed;
  * nothing undeclared may end up in the bundle.

Usage:
    python3 ingest.py --lock <lock.json> --out <bundle-root>
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import urllib.error
import urllib.request
from pathlib import Path

import bundle

USER_AGENT = "tesserafin-w1r-ingest"
TIMEOUT = 300


class IngestError(Exception):
    """Fail-closed condition. Never caught to continue."""


def fetch(url: str, expect_filename: str) -> bytes:
    """Fetch `url`, refusing a response whose final URL names a different file.

    A digest check alone would catch corrupted bytes but not a mirror that
    answers a superseded filename with the current one: that would fail the
    digest with a confusing message, or — if the lock were ever regenerated
    against the redirected file — pass while meaning something else.
    """
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
        if response.status != 200:
            raise IngestError(f"{url}: HTTP {response.status}")
        final = response.geturl()
        if final.rsplit("/", 1)[-1] != expect_filename:
            raise IngestError(
                f"{url}: the response resolved to {final!r}, which does not name "
                f"{expect_filename!r}"
            )
        return response.read()


def try_fetch(url: str, expect_filename: str):
    """Fetch an optional resource. Returns bytes, or None on a 404."""
    try:
        return fetch(url, expect_filename)
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise IngestError(f"{url}: HTTP {error.code}") from error


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    lock_bytes = args.lock.read_bytes()
    lock = json.loads(lock_bytes)

    if lock["schemaVersion"] != 1:
        raise IngestError(
            f"lock schemaVersion {lock['schemaVersion']} is not implemented by this "
            "ingest; refusing rather than guessing its semantics"
        )

    root = args.out
    if root.exists():
        shutil.rmtree(root)
    for sub in ("packages", "signatures", "databases", "licenses"):
        (root / sub).mkdir(parents=True)

    # 1. The databases the lock was resolved from, re-fetched and re-verified.
    databases = {}
    for repo, record in sorted(lock["repositoryDatabases"].items()):
        raw = fetch(record["url"], record["filename"])
        actual = hashlib.sha256(raw).hexdigest()
        if actual != record["sha256"]:
            raise IngestError(
                f"{record['url']}: the repository database has moved since the lock "
                f"was reviewed (locked {record['sha256']}, now {actual}). Regenerate "
                "and re-review the lock; do not ingest against a moved database."
            )
        (root / "databases" / record["filename"]).write_bytes(raw)
        databases[repo] = {
            "filename": record["filename"],
            "sha256": actual,
            "bytes": len(raw),
        }

    # 2. The archives themselves, by exact filename, with no resolution.
    seen_filenames: set = set()
    signatures = {}
    licenses = {}
    for package in lock["packages"]:
        filename = package["filename"]
        if filename in seen_filenames:
            raise IngestError(f"the lock declares {filename} more than once")
        seen_filenames.add(filename)

        raw = fetch(package["url"], filename)
        actual = hashlib.sha256(raw).hexdigest()
        if actual != package["sha256"]:
            raise IngestError(
                f"{filename}: sha256 {actual} does not match the locked "
                f"{package['sha256']}"
            )
        if len(raw) != package["compressedBytes"]:
            raise IngestError(
                f"{filename}: {len(raw)} bytes, the lock records "
                f"{package['compressedBytes']}"
            )
        (root / "packages" / filename).write_bytes(raw)

        signature = try_fetch(package["signatureUrl"], filename + ".sig")
        if signature is None:
            signatures[filename] = {"present": False}
        else:
            (root / "signatures" / (filename + ".sig")).write_bytes(signature)
            signatures[filename] = {
                "present": True,
                "sha256": hashlib.sha256(signature).hexdigest(),
                "bytes": len(signature),
            }

        licenses[package["name"]] = {
            "version": package["version"],
            "repository": package["repository"],
            "license": package["license"],
        }
        print(f"admitted {filename}", file=sys.stderr)

    # 3. The lock and the derived metadata travel WITH the bytes, so a consumer
    #    that has only the artifact can still verify everything.
    (root / "msys2-lock.json").write_bytes(lock_bytes)
    (root / "licenses" / "licenses.json").write_bytes(bundle.canonical_json(licenses))
    (root / "signatures" / "signatures.json").write_bytes(
        bundle.canonical_json(signatures)
    )

    # 4. Nothing undeclared. Checked against the bundle as it actually is, not
    #    against what the loop above believes it wrote.
    expected = {f"packages/{name}" for name in seen_filenames}
    actual_packages = {
        path for path in bundle.relative_paths(root) if path.startswith("packages/")
    }
    if actual_packages != expected:
        extra = sorted(actual_packages - expected)
        missing = sorted(expected - actual_packages)
        raise IngestError(
            f"bundle package set disagrees with the lock: extra={extra} missing={missing}"
        )

    lock_sha256 = bundle.write_bundle_metadata(root, lock_bytes, databases)
    print(
        f"bundle: {len(seen_filenames)} packages, "
        f"{sum(1 for s in signatures.values() if s['present'])} signatures, "
        f"lock sha256 {lock_sha256}",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (IngestError, bundle.BundleError) as error:
        print(f"W1-R INGEST HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
