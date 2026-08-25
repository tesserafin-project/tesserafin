"""Stage the retention unit from the W1-A3 proof artifacts (#236, W1-A4).

Usage:
    python3 assemble.py --delivered <dir> --evidence-a <dir> --evidence-b <dir> \
        --comparison <comparison.json> --accepted <accepted-runtime.json> \
        --out <unit-dir>

This COPIES bytes; it never produces them. Nothing here builds, rebuilds,
re-accepts or normalises the runtime: every file that lands in the unit is
compared against the committed acceptance manifest first, and a byte that does
not match is a hard stop rather than something to correct.

The staged layout is the layer layout, and it is fixed:

    delivered/…                 the 31 accepted delivered paths, unchanged
    evidence/host-a/…           the complete host-a acceptance bundle
    evidence/host-b/…           the complete host-b acceptance bundle
    evidence/comparison.json    the dual-runner comparison record
    RETENTION.md                the retention contract, generated deterministically
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

import contract
import retention

RETENTION_README = """# Retained Tesserafin FFmpeg runtime for Windows (win-x64)

This OCI artifact retains ONE accepted runtime, its COMPLETE corresponding
source and the evidence under which it was accepted. It is a production input.
It is not a release, it is not signed and nothing in it was rebuilt to produce
this unit.

## Identity

| | |
| --- | --- |
| platform | `{platform}` |
| accepted server commit | `{accepted_commit}` |
| accepted server tree | `{accepted_tree}` |
| W1-A3 proof head | `{proof_head}` |
| W1-A3 proof run | `{proof_run}` |
| FFmpeg upstream commit | `{ffmpeg_commit}` |
| build revision | `{build_revision}` |
| build inputs | `{build_inputs}` |
| runtime archive | `sha256:{runtime_sha}` |
| corresponding source | `sha256:{source_sha}` |
| corresponding source, decompressed | `sha256:{source_stream_sha}` |
| topology | `{topology}` (sameNode={same_node}, independenceClaim={independence}) |
| signed | `{signed}` |

## Licence and corresponding source — read this before redistributing

The runtime in `delivered/runtime/` is licensed **{licence}**. It is a conveyed
binary, and the corresponding source for it is `delivered/source/`, retained in
this same unit precisely so the two cannot be separated by moving, copying or
mirroring the artifact.

**If you distribute the binary, you must distribute this source with it.** The
`delivered/source/` archive is the complete corresponding source for
`delivered/runtime/`, at the exact revision it was built from — not a pointer to
an upstream repository that may move, and not a subset.

Retaining the binary while the corresponding source is absent, unreachable or
merely referenced elsewhere is not a permitted state at any point. The builder
in `ci/windows/runtime-retention/retention.py` refuses to construct a unit that
does so, and the consumer refuses to extract one.

While this package is private, it is an internal build input only. **Before any
public Windows binary built from it is distributed, this package — including
`delivered/source/` — must be publicly and anonymously available.**

## How to consume this

By exact OCI manifest digest, and only by exact OCI manifest digest:

```
{package}@sha256:<digest>
```

The digest is NOT printed in this file, and its absence here is deliberate: this
file is inside the layer the digest is computed over, so a copy of the digest
here could not be written before the digest existed. The one authoritative
record is `ci/windows/runtime-retention/accepted-runtime.json` in the server
repository at commit `{accepted_commit}`, which every consumer reads.

A tag is never an accepted identity here. An immutable convenience tag
(`{immutable_tag}`) may exist so that a human can find this package; nothing
automated resolves it, and it never moves.

## What the evidence says, and what it does not

`evidence/comparison.json` records that two native Windows runner allocations
produced identical bytes. Both allocations reported the SAME node, so this is
dual-runner reproducibility between two isolated jobs on one runner-image
generation — **not** independence between two machines. The `independenceClaim`
field says `none` for that reason, and both `runner.json` records are retained
beside the comparison so the limitation travels with the evidence rather than
living in a review comment.
"""


def _stop(message: str) -> None:
    raise retention.RetentionError(message)


def _copy_tree(source: Path, destination: Path) -> None:
    for entry in sorted(source.rglob("*")):
        if entry.is_symlink():
            _stop(f"symlink in the source evidence: {entry}")
        if not entry.is_file():
            continue
        target = destination / entry.relative_to(source)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(entry, target)




def stage_unit(
    delivered: Path,
    evidence_a: Path,
    evidence_b: Path,
    comparison: Path,
    identity: dict,
    out: Path,
) -> None:
    """Copy the accepted bytes into the fixed unit layout.

    Takes only the IDENTITY half of the acceptance manifest — never the OCI
    half. The OCI digests are computed FROM this tree, so a function that
    produced the tree from them could not be run before they existed. That
    separation is what lets `derive-accepted.py` bootstrap the manifest and
    `assemble.py` then verify against it, using one staging implementation.
    """
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    _copy_tree(delivered, out / "delivered")
    _copy_tree(evidence_a, out / "evidence" / "host-a")
    _copy_tree(evidence_b, out / "evidence" / "host-b")
    (out / "evidence").mkdir(parents=True, exist_ok=True)
    shutil.copyfile(comparison, out / "evidence" / "comparison.json")

    for host in ("host-a", "host-b"):
        if not (out / "evidence" / host / "inputs" / "runner.json").is_file():
            _stop(f"evidence bundle {host} carries no inputs/runner.json")
        if not (out / "evidence" / host / "accept-runtime.json").is_file():
            _stop(f"evidence bundle {host} carries no accept-runtime.json")

    readme = RETENTION_README.format(
        platform=identity["platform"],
        accepted_commit=identity["acceptedServerCommit"],
        accepted_tree=identity["acceptedServerTree"],
        proof_head=identity["proofHead"],
        proof_run=identity["proofRun"],
        ffmpeg_commit=identity["ffmpegUpstreamCommit"],
        build_revision=identity["ffmpegBuildRevision"],
        build_inputs=identity["buildInputsReference"],
        runtime_sha=identity["runtimeSha256"],
        source_sha=identity["correspondingSourceSha256"],
        source_stream_sha=identity["correspondingSourceStreamSha256"],
        topology=identity["topology"],
        same_node=str(identity["sameNode"]).lower(),
        independence=identity["independenceClaim"],
        signed=str(identity["signed"]).lower(),
        licence=identity["licence"],
        package=contract.CANONICAL,
        immutable_tag=identity["immutableTag"],
    )
    (out / "RETENTION.md").write_text(readme, encoding="utf-8", newline="\n")

    retention.assert_source_availability(retention.relative_paths(out))


def verify_against_accepted(out: Path, accepted: dict) -> dict:
    """The staged unit must be EXACTLY the unit the acceptance manifest pinned."""
    staged = retention.path_manifest(out)
    sizes = retention.path_sizes(out)
    pinned = accepted["unitPaths"]

    extra = sorted(set(staged) - set(pinned))
    missing = sorted(set(pinned) - set(staged))
    changed = sorted(
        p for p in set(staged) & set(pinned) if staged[p] != pinned[p]["sha256"]
    )
    resized = sorted(p for p in set(staged) & set(pinned) if sizes[p] != pinned[p]["size"])
    if extra or missing or changed or resized:
        _stop(
            "the staged unit is not the pinned unit: "
            f"added={extra} missing={missing} changed={changed} resized={resized}"
        )

    delivered_paths = [p for p in staged if p.startswith("delivered/")]
    if len(delivered_paths) != accepted["deliveredPathCount"]:
        _stop(
            f"the staged unit holds {len(delivered_paths)} delivered paths, but the "
            f"accepted manifest names {accepted['deliveredPathCount']}"
        )

    for host, key in (("host-a", "hostA"), ("host-b", "hostB")):
        recorded = accepted["evidence"][key]
        actual = retention.sha256_file(out / "evidence" / host / "inputs" / "runner.json")
        if actual != recorded["runnerJsonSha256"]:
            _stop(
                f"{host}/inputs/runner.json hashes to {actual}, but the accepted "
                f"manifest records {recorded['runnerJsonSha256']}"
            )
        actual = retention.sha256_file(out / "evidence" / host / "accept-runtime.json")
        if actual != recorded["acceptRuntimeSha256"]:
            _stop(
                f"{host}/accept-runtime.json hashes to {actual}, but the accepted "
                f"manifest records {recorded['acceptRuntimeSha256']}"
            )
    actual = retention.sha256_file(out / "evidence" / "comparison.json")
    if actual != accepted["evidence"]["comparisonSha256"]:
        _stop(
            f"comparison.json hashes to {actual}, but the accepted manifest records "
            f"{accepted['evidence']['comparisonSha256']}"
        )

    retention.assert_source_availability(sorted(staged))
    return {"stagedPaths": len(staged), "stagedBytes": sum(sizes.values())}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--delivered", required=True, type=Path)
    parser.add_argument("--evidence-a", required=True, type=Path)
    parser.add_argument("--evidence-b", required=True, type=Path)
    parser.add_argument("--comparison", required=True, type=Path)
    parser.add_argument("--accepted", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    accepted = json.loads(args.accepted.read_bytes())
    contract.validate_accepted(accepted)

    stage_unit(
        args.delivered,
        args.evidence_a,
        args.evidence_b,
        args.comparison,
        accepted,
        args.out,
    )
    summary = verify_against_accepted(args.out, accepted)
    print(retention.canonical_json(summary).decode("utf-8"), end="")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (retention.RetentionError, contract.ContractError) as error:
        print(f"W1-A4 RETENTION HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
