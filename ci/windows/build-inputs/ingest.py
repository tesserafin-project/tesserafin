"""Admit the locked MSYS2 archives into a deterministic bundle (#236, W1-R).

INGRESS, not consumption. This is the only place in W1 that is allowed to talk
to the live MSYS2 repository, and it may do exactly one thing there: fetch the
exact filenames a reviewed, committed lock already names. It resolves nothing,
expands no group, and follows no dependency — the closure was decided when the
lock was reviewed.

Everything is fail-closed:

  * the repository databases come from the repository TREE, not from a fetch,
    and their SHA-256 must equal what the lock recorded. MSYS2 republishes
    `msys.db` and `clang64.db` whenever any package in the repository changes,
    which is several times a day, and it keeps no immutable snapshot of an
    older one. Re-fetching them therefore made the lock expire within hours of
    being reviewed, and put bytes into the bundle that nobody had reviewed.
    Committing them keeps the digest gate a live fail-closed check and makes
    the bundle reproducible for as long as the lock stands;
  * every archive's SHA-256 must equal the locked value BEFORE it is admitted;
  * a response that redirects to a different filename is rejected — a mirror
    quietly serving `…-2-any.pkg.tar.zst` for a `…-1-…` request would otherwise
    be admitted under the wrong identity;
  * every archive's detached signature must VERIFY cryptographically against
    the committed trust root before the archive enters the bundle. Not "a .sig
    was present"; not "the .sig hashed to something". See signing.py;
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
import urllib.request
from pathlib import Path

import bundle
import signing

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


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument(
        "--databases",
        type=Path,
        default=Path(__file__).resolve().parent / "databases",
        help="the committed repository databases the lock was resolved from",
    )
    parser.add_argument(
        "--trust",
        type=Path,
        default=Path(__file__).resolve().parent / "trust",
        help="the committed signing root of trust",
    )
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
    for sub in ("packages", "signatures", "databases", "licenses", "trust"):
        (root / sub).mkdir(parents=True)

    # 1. The trust root, copied from the tree and validated before it is used.
    #    Nothing is admitted until this holds, because the whole point is that
    #    admission is gated on it.
    trust_root = signing.load_trust_root(args.trust)
    for name in sorted({trust_root["keyFile"], "trust-root.json"}):
        shutil.copyfile(args.trust / name, root / "trust" / name)

    # 2. The databases the lock was resolved from, taken from the TREE.
    databases = {}
    for repo, record in sorted(lock["repositoryDatabases"].items()):
        source = args.databases / record["filename"]
        if not source.is_file():
            raise IngestError(
                f"the lock names {record['filename']}, which is not committed under "
                f"{args.databases}. The database a lock was resolved from is part of "
                "the reviewed change; it is not re-fetched."
            )
        raw = source.read_bytes()
        actual = hashlib.sha256(raw).hexdigest()
        if actual != record["sha256"]:
            raise IngestError(
                f"{record['filename']}: the committed database hashes to {actual}, "
                f"the lock records {record['sha256']}. Regenerate and re-review the "
                "lock and the database together; they are one change."
            )
        (root / "databases" / record["filename"]).write_bytes(raw)
        databases[repo] = {
            "filename": record["filename"],
            "sha256": actual,
            "bytes": len(raw),
        }

    # 3. The archives themselves, by exact filename, with no resolution — and
    #    with the signature verified BEFORE the archive reaches the bundle.
    seen_filenames: set = set()
    signatures = {}
    licenses = {}
    staging = root / ".staging"
    staging.mkdir()
    with signing.Verifier(args.trust) as verifier:
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

            signature = fetch(package["signatureUrl"], filename + ".sig")

            archive_staged = staging / filename
            signature_staged = staging / (filename + ".sig")
            archive_staged.write_bytes(raw)
            signature_staged.write_bytes(signature)
            fingerprint = verifier.verify(archive_staged, signature_staged)

            archive_staged.replace(root / "packages" / filename)
            signature_staged.replace(root / "signatures" / (filename + ".sig"))
            signatures[filename] = {
                "sha256": hashlib.sha256(signature).hexdigest(),
                "bytes": len(signature),
                "signer": fingerprint,
            }

            licenses[package["name"]] = {
                "version": package["version"],
                "repository": package["repository"],
                "license": package["license"],
            }
            print(f"admitted {filename} (signed by {fingerprint})", file=sys.stderr)
    staging.rmdir()

    # 4. The lock and the derived metadata travel WITH the bytes, so a consumer
    #    that has only the artifact can still verify everything.
    (root / "msys2-lock.json").write_bytes(lock_bytes)
    (root / "licenses" / "licenses.json").write_bytes(bundle.canonical_json(licenses))
    (root / "signatures" / "signatures.json").write_bytes(
        bundle.canonical_json(
            {
                "trustRootSha256": trust_root["_sha256"],
                "acceptedFingerprints": sorted(trust_root["acceptedFingerprints"]),
                "packages": signatures,
            }
        )
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

    lock_sha256 = bundle.write_bundle_metadata(
        root, lock_bytes, databases, trust_root["_sha256"]
    )
    print(
        f"bundle: {len(seen_filenames)} packages, "
        f"{len(signatures)} verified signatures, "
        f"lock sha256 {lock_sha256}, trust root {trust_root['_sha256']}",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (
        IngestError,
        bundle.BundleError,
        signing.SignatureError,
    ) as error:
        print(f"W1-R INGEST HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
