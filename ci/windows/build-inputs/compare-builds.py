"""Compare two independent OCI builds (#236, W1-R).

Digests are compared LAST. A comparison that started with the manifest digest
would report "differs" and stop, telling a reader nothing about which of several
hundred paths moved. So the complete relative-path inventories are compared
first, then every per-path digest, then the layer, config and manifest — and the
first difference found is reported in the most specific terms available.

Usage:
    python3 compare-builds.py --a <oci-a> --b <oci-b> \
        --bundle-a <bundle-a> --bundle-b <bundle-b>
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import bundle


class ComparisonError(Exception):
    """Fail-closed condition. Never caught to continue."""


def compare_inventories(a: Path, b: Path) -> int:
    paths_a = bundle.relative_paths(a)
    paths_b = bundle.relative_paths(b)
    only_a = sorted(set(paths_a) - set(paths_b))
    only_b = sorted(set(paths_b) - set(paths_a))
    if only_a or only_b:
        raise ComparisonError(
            f"the two bundles deliver different paths: only in A={only_a[:10]} "
            f"only in B={only_b[:10]}"
        )
    print(f"path inventory  : identical, {len(paths_a)} delivered paths")
    return len(paths_a)


def compare_per_path(a: Path, b: Path) -> None:
    digests_a = bundle.path_manifest(a, exclude="")
    digests_b = bundle.path_manifest(b, exclude="")
    differing = sorted(p for p in digests_a if digests_a[p] != digests_b[p])
    if differing:
        sample = differing[:10]
        detail = ", ".join(
            f"{p} ({digests_a[p][:12]}… vs {digests_b[p][:12]}…)" for p in sample
        )
        raise ComparisonError(
            f"{len(differing)} delivered path(s) differ between the two builds: {detail}"
        )
    print(f"per-path digests: identical, {len(digests_a)} paths")


def descriptor(directory: Path) -> dict:
    return json.loads((directory / "descriptor.json").read_text())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--a", required=True, type=Path)
    parser.add_argument("--b", required=True, type=Path)
    parser.add_argument("--bundle-a", required=True, type=Path)
    parser.add_argument("--bundle-b", required=True, type=Path)
    parser.add_argument("--expect-digest", default="")
    args = parser.parse_args()

    compare_inventories(args.bundle_a, args.bundle_b)
    compare_per_path(args.bundle_a, args.bundle_b)

    left = descriptor(args.a)
    right = descriptor(args.b)

    for field in ("layerSize", "layerDigest", "configSize", "configDigest", "lockSha256"):
        if left[field] != right[field]:
            raise ComparisonError(
                f"{field} differs: A={left[field]} B={right[field]}"
            )
        print(f"{field:16}: {left[field]}")

    bytes_a = (args.a / "manifest.json").read_bytes()
    bytes_b = (args.b / "manifest.json").read_bytes()
    if bytes_a != bytes_b:
        raise ComparisonError(
            "the manifest BYTES differ even though the descriptors agree — "
            "something is serialising the manifest nondeterministically"
        )
    print(f"manifest bytes  : identical, {len(bytes_a)} bytes")

    recomputed_a = bundle.read_manifest_digest(args.a)
    recomputed_b = bundle.read_manifest_digest(args.b)
    if recomputed_a != recomputed_b:
        raise ComparisonError(f"manifest digest differs: {recomputed_a} vs {recomputed_b}")
    if recomputed_a != left["manifestDigest"]:
        raise ComparisonError(
            f"descriptor claims {left['manifestDigest']} but the bytes hash to "
            f"{recomputed_a}"
        )

    if args.expect_digest and args.expect_digest != recomputed_a:
        raise ComparisonError(
            f"both builds agree on {recomputed_a}, but the reviewed digest recorded "
            f"in the repository is {args.expect_digest}"
        )

    print(f"manifest digest : {recomputed_a}")
    print("REPRODUCIBLE: two independent builds produced byte-identical artifacts")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ComparisonError, bundle.BundleError) as error:
        print(f"W1-R COMPARISON HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
