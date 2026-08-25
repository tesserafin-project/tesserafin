#!/usr/bin/env python3
"""Compare what two native Windows hosts produced (W1-A3, issue #236).

The order of the checks is the finding. A reproducibility failure that is
reported only as "the archives differ" costs a full rebuild to diagnose, so this
answers three questions in sequence and stops at the first one that fails:

  1. did both hosts deliver the SAME SET OF PATHS? A missing or extra file is a
     packaging divergence, not a compiler one, and looks nothing like a
     miscompilation once you know which it is;
  2. does every path have the same CONTENT? Digests are recomputed from the
     bytes here rather than read from either host's SHA256SUMS — a comparator
     that compares two self-reported manifests proves only that both hosts can
     hash their own output;
  3. are the final ARCHIVES byte-identical? For the corresponding-source archive
     the DECOMPRESSED stream is compared as well as the container, because a
     zstd container can differ between two identical trees while the content is
     the same. That is measured behaviour on this project, not a precaution.

The runner image version of each host is reported alongside. Two hosts on
DIFFERENT images that still agree byte for byte is a stronger result than two
identical images agreeing; a divergence between different images names its own
most likely cause.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def tree(root: Path) -> dict[str, Path]:
    return {p.relative_to(root).as_posix(): p
            for p in sorted(root.rglob("*")) if p.is_file()}


def read_json(path: Path) -> dict:
    try:
        return json.loads(path.read_text())
    except (OSError, json.JSONDecodeError):
        return {}


def zstd_stream_digest(path: Path) -> str | None:
    """SHA-256 of the DECOMPRESSED bytes, or None if zstd is unavailable."""
    try:
        proc = subprocess.run(["zstd", "-dc", str(path)], capture_output=True,
                              check=True)
    except (OSError, subprocess.CalledProcessError):
        return None
    return hashlib.sha256(proc.stdout).hexdigest()


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host-a", required=True)
    ap.add_argument("--host-b", required=True)
    ap.add_argument("--evidence-a")
    ap.add_argument("--evidence-b")
    ap.add_argument("--report", default="comparison.json")
    args = ap.parse_args(argv)

    a_root, b_root = Path(args.host_a), Path(args.host_b)
    a, b = tree(a_root), tree(b_root)

    report: dict = {
        "probe": "winx64-dual-runner-comparison",
        "hosts": {},
        "pathSet": {},
        "content": {},
        "archives": {},
        "verdict": "PENDING",
    }

    for label, evidence in (("a", args.evidence_a), ("b", args.evidence_b)):
        runner = read_json(Path(evidence) / "inputs/runner.json") if evidence else {}
        report["hosts"][label] = {
            "imageOs": runner.get("imageOs", "unknown"),
            "imageVersion": runner.get("imageVersion", "unknown"),
            "runnerArch": runner.get("runnerArch", "unknown"),
            # The two identities that decide what this proof may be CALLED.
            # runnerName is the allocation; node is the machine GitHub put it on,
            # which nothing in this repository can choose.
            "runnerName": runner.get("runnerName", "unknown"),
            "node": runner.get("node", "unknown"),
            "deliveredPaths": len(a if label == "a" else b),
        }

    print("== runner identities")
    for label, info in report["hosts"].items():
        print(f"  allocation {label}: image {info['imageOs']} {info['imageVersion']}, "
              f"runner {info['runnerName']}, node {info['node']}, "
              f"{info['deliveredPaths']} delivered paths")
    same_image = (report["hosts"]["a"]["imageVersion"]
                  == report["hosts"]["b"]["imageVersion"])
    same_node = report["hosts"]["a"]["node"] == report["hosts"]["b"]["node"]
    distinct_allocations = (report["hosts"]["a"]["runnerName"]
                            != report["hosts"]["b"]["runnerName"])
    report["sameRunnerImage"] = same_image
    report["sameNode"] = same_node
    report["distinctRunnerAllocations"] = distinct_allocations

    # What the evidence may claim, decided from what was measured rather than
    # from what the workflow is called. Two allocations on one node is
    # dual-runner reproducibility; it is not host independence, and naming it
    # "two-host" would assert a hardware separation nobody arranged.
    if same_node:
        report["topology"] = "dual-runner"
        report["independenceClaim"] = (
            "none: both allocations reported the same node "
            f"({report['hosts']['a']['node']}), so this proves reproducibility "
            "between two isolated jobs on one runner-image generation, not "
            "independence between two machines")
    elif same_image:
        report["topology"] = "two-host-same-image"
        report["independenceClaim"] = (
            "two distinct nodes on one runner-image generation")
    else:
        report["topology"] = "two-host-distinct-images"
        report["independenceClaim"] = (
            "two distinct nodes on two runner-image generations")
    print(f"  topology: {report['topology']} — {report['independenceClaim']}")

    if not distinct_allocations:
        print("  FAIL: both evidence bundles name the same runner allocation; "
              "this is one job compared against itself", file=sys.stderr)
        return 1
    if not same_image:
        print("  note: the two allocations ran DIFFERENT runner images. A "
              "byte-identical result across them is stronger evidence, not weaker.")

    # ── 1. the complete delivered path set ─────────────────────────────────
    print("== the delivered path set")
    only_a = sorted(set(a) - set(b))
    only_b = sorted(set(b) - set(a))
    report["pathSet"] = {
        "count": len(a),
        "onlyOnA": only_a,
        "onlyOnB": only_b,
        "identical": not only_a and not only_b,
    }
    if only_a or only_b:
        for p in only_a:
            print(f"  FAIL: {p} was delivered by host a only")
        for p in only_b:
            print(f"  FAIL: {p} was delivered by host b only")
        report["verdict"] = "FAIL: the two hosts delivered different sets of paths"
        Path(args.report).write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
        print(f"\n{report['verdict']}", file=sys.stderr)
        return 1
    print(f"  ok  : both hosts delivered the same {len(a)} paths")

    # ── 2. every path's content, recomputed here ───────────────────────────
    print("== every delivered path's content")
    differing = []
    for rel in sorted(a):
        da, db = digest(a[rel]), digest(b[rel])
        if da != db:
            differing.append({"path": rel, "a": da, "b": db,
                              "sizeA": a[rel].stat().st_size,
                              "sizeB": b[rel].stat().st_size})
    report["content"] = {
        "compared": len(a),
        "differing": differing,
        "identical": not differing,
    }
    if differing:
        for d in differing:
            print(f"  FAIL: {d['path']}\n        a {d['a']} ({d['sizeA']} bytes)"
                  f"\n        b {d['b']} ({d['sizeB']} bytes)")
        report["verdict"] = (f"FAIL: {len(differing)} of {len(a)} delivered paths "
                             "differ between the two hosts")
        Path(args.report).write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
        print(f"\n{report['verdict']}", file=sys.stderr)
        return 1
    print(f"  ok  : all {len(a)} paths are byte-identical")

    # ── 3. the final archives ──────────────────────────────────────────────
    print("== the final archives")
    archives = {}
    failed = False
    for rel in sorted(a):
        if rel.startswith("runtime/") and rel.endswith(".zip"):
            archives["runtime"] = rel
        elif rel.startswith("source/") and rel.endswith(".tar.zst"):
            archives["correspondingSource"] = rel
    for kind, rel in ("runtime", archives.get("runtime")), \
                     ("correspondingSource", archives.get("correspondingSource")):
        if rel is None:
            print(f"  FAIL: no {kind} archive was delivered")
            report["archives"][kind] = {"present": False}
            failed = True
            continue
        da, db = digest(a[rel]), digest(b[rel])
        entry = {"present": True, "path": rel, "sha256": da, "identical": da == db}
        if da != db:
            print(f"  FAIL: {rel} differs: a {da}, b {db}")
            failed = True
        else:
            print(f"  ok  : {rel} is byte-identical ({da})")
        if rel.endswith(".tar.zst"):
            sa, sb = zstd_stream_digest(a[rel]), zstd_stream_digest(b[rel])
            entry["uncompressedSha256"] = sa
            entry["uncompressedIdentical"] = (sa is not None and sa == sb)
            if sa is None:
                print("  note: zstd is unavailable here, so only the container "
                      "was compared for the source archive")
            elif sa != sb:
                print(f"  FAIL: the DECOMPRESSED source stream differs: a {sa}, b {sb}")
                failed = True
            else:
                print(f"  ok  : the decompressed source stream is identical ({sa})")
        report["archives"][kind] = entry

    provenance = read_json(a_root / "provenance.json")
    report["identities"] = {
        "repositorySha": provenance.get("repositorySha"),
        "buildRevision": provenance.get("buildRevision"),
        "buildInputs": provenance.get("buildInputs", {}).get("reference"),
        "ffmpegCommit": provenance.get("ffmpeg", {}).get("commit"),
        "patchesApplied": provenance.get("patches", {}).get("applied"),
        "runtimeSha256": provenance.get("runtime", {}).get("sha256"),
        "correspondingSourceSha256":
            provenance.get("correspondingSource", {}).get("sha256"),
    }

    report["verdict"] = (
        "FAIL: the two runner allocations disagree on an archive" if failed else
        "PASS: two native Windows runner allocations produced identical bytes "
        f"({report['independenceClaim']})")
    Path(args.report).write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")

    print()
    print("== identities bound by this proof")
    for k, v in report["identities"].items():
        print(f"  {k}: {v}")
    print()
    if failed:
        print(report["verdict"], file=sys.stderr)
        return 1
    print("WIN-X64 DUAL-RUNNER REPRODUCIBILITY: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
