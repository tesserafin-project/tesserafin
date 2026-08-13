"""Build the deterministic OCI layout from an ingested bundle (#236, W1-R).

Usage:
    python3 build-oci.py --bundle <bundle-root> --out <oci-dir>

Prints the descriptor JSON on stdout. The manifest digest it reports is the
identity every consumer must pin, and the bytes it writes are the bytes the
publisher pushes unmodified.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
from pathlib import Path

import bundle
import signing


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    lock_bytes = (args.bundle / "msys2-lock.json").read_bytes()
    lock = json.loads(lock_bytes)
    lock_sha256 = hashlib.sha256(lock_bytes).hexdigest()

    metadata = json.loads((args.bundle / "bundle.json").read_bytes())
    if metadata["lockSha256"] != lock_sha256:
        raise bundle.BundleError(
            f"bundle.json records lockSha256 {metadata['lockSha256']}, but the "
            f"bundled lock hashes to {lock_sha256}"
        )
    if metadata["bundleFormatVersion"] != bundle.BUNDLE_FORMAT_VERSION:
        raise bundle.BundleError(
            f"bundleFormatVersion {metadata['bundleFormatVersion']} is not "
            "implemented by this builder"
        )

    # The recorded path manifest must still describe the tree. A bundle edited
    # after ingestion must not be publishable.
    recorded = {}
    for line in (args.bundle / "manifest.sha256").read_text().splitlines():
        digest, _, path = line.partition("  ")
        recorded[path] = digest
    actual = bundle.path_manifest(args.bundle, exclude="manifest.sha256")
    if recorded != actual:
        extra = sorted(set(actual) - set(recorded))
        missing = sorted(set(recorded) - set(actual))
        changed = sorted(p for p in set(actual) & set(recorded) if actual[p] != recorded[p])
        raise bundle.BundleError(
            f"the bundle does not match its own manifest.sha256: extra={extra} "
            f"missing={missing} changed={changed}"
        )

    if args.out.exists():
        shutil.rmtree(args.out)
    args.out.mkdir(parents=True)

    # The trust root is read back from the BUNDLE, not from the repository, and
    # is re-hashed here: an OCI layout whose config claims a signing root the
    # layer does not carry would be unverifiable by a consumer that only has
    # the artifact.
    trust_root = signing.load_trust_root(args.bundle / "trust")
    if trust_root["_sha256"] != metadata["trustRootSha256"]:
        raise bundle.BundleError(
            f"bundle.json records trustRootSha256 {metadata['trustRootSha256']}, "
            f"but the bundled trust root hashes to {trust_root['_sha256']}"
        )

    summary = bundle.build_oci_layout(
        args.bundle, args.out, lock, lock_sha256, trust_root["_sha256"]
    )

    recomputed = bundle.read_manifest_digest(args.out)
    if recomputed != summary["manifestDigest"]:
        raise bundle.BundleError(
            f"manifest digest disagrees with its own bytes: {recomputed} vs "
            f"{summary['manifestDigest']}"
        )

    print(bundle.canonical_json(summary).decode("utf-8"), end="")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (bundle.BundleError, signing.SignatureError) as error:
        print(f"W1-R OCI HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
