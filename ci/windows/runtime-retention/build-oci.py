"""Build the deterministic OCI layout for the retained runtime (#236, W1-A4).

Usage:
    python3 build-oci.py --unit <unit-root> --accepted <accepted-runtime.json> \
        --out <oci-dir>

Prints the descriptor JSON on stdout. The manifest digest it reports is the
identity every consumer must pin, and the bytes it writes are the bytes the
publisher pushes unmodified.

Three refusals happen here rather than at publication time, because a layout
that cannot be built wrong cannot be pushed wrong:

  * the staged unit must be exactly the unit the acceptance manifest pinned;
  * the manifest digest must agree with the bytes actually written;
  * the digest reconstructed from the committed manifest ALONE must equal the
    digest built from the staged tree. If those two ever disagree, either the
    tree is not what was reviewed or the committed manifest no longer describes
    it, and there is no third possibility worth guessing between.
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

import assemble
import contract
import retention


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--unit", required=True, type=Path)
    parser.add_argument("--accepted", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    accepted = json.loads(args.accepted.read_bytes())
    contract.validate_accepted(accepted)

    # The tree must be the reviewed tree before a single byte is hashed into a
    # layer. Building first and comparing afterwards would report a digest for
    # something nobody accepted.
    assemble.verify_against_accepted(args.unit, accepted)

    if args.out.exists():
        shutil.rmtree(args.out)
    args.out.mkdir(parents=True)

    summary = retention.build_oci_layout(args.unit, args.out, accepted)

    recomputed = retention.read_manifest_digest(args.out)
    if recomputed != summary["manifestDigest"]:
        raise retention.RetentionError(
            f"manifest digest disagrees with its own bytes: {recomputed} vs "
            f"{summary['manifestDigest']}"
        )

    # The committed manifest must describe this layout without ever reading it.
    predicted = retention.expected_manifest_digest(accepted)
    for field in ("configDigest", "configSize", "layerDigest", "layerSize", "manifestDigest", "manifestSize"):
        if predicted[field] != summary[field]:
            raise retention.RetentionError(
                f"{field}: the committed acceptance manifest predicts "
                f"{predicted[field]!r}, but this build produced {summary[field]!r}"
            )
        if accepted[field] != summary[field]:
            raise retention.RetentionError(
                f"{field}: accepted-runtime.json records {accepted[field]!r}, but this "
                f"build produced {summary[field]!r}"
            )

    print(retention.canonical_json(summary).decode("utf-8"), end="")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (retention.RetentionError, contract.ContractError) as error:
        print(f"W1-A4 RETENTION HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
