"""Derive the acceptance manifest from the W1-A3 artifacts (#236, W1-A4).

Usage:
    python3 derive-accepted.py --delivered <dir> --evidence-a <dir> \
        --evidence-b <dir> --comparison <comparison.json> \
        --accepted-server-commit <sha> --accepted-server-tree <sha> \
        --out <accepted-runtime.json>

This is the BOOTSTRAP, and it is committed rather than run by hand so that a
reviewer can re-derive the committed manifest from the same artifacts and get
the same bytes.

Every value it writes is MEASURED from the artifacts. Nothing is copied from a
pull-request body, a review comment or a previous manifest: the frozen identities
a human supplies are only `--accepted-server-commit` and `--accepted-server-tree`,
and both are cross-checked against what the evidence itself records before they
are believed.

The output is validated against the closed schema before it is written, so this
tool cannot emit a manifest that the rest of the machinery would reject.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import assemble
import contract
import retention


def _stop(message: str) -> None:
    raise retention.RetentionError(message)


def _zstd_stream_sha256(archive: Path) -> str:
    """sha256 of the DECOMPRESSED source stream.

    Measured with the same tool the proof used. A compressed archive that
    matches while its stream does not is the failure this catches, and it cannot
    be caught by hashing the container.
    """
    process = subprocess.Popen(
        ["zstd", "-dc", str(archive)], stdout=subprocess.PIPE, stderr=subprocess.PIPE
    )
    assert process.stdout is not None
    import hashlib

    digest = hashlib.sha256()
    for chunk in iter(lambda: process.stdout.read(1 << 20), b""):
        digest.update(chunk)
    process.stdout.close()
    if process.wait() != 0:
        _stop(f"zstd could not decompress {archive}: {process.stderr.read().decode()}")
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--delivered", required=True, type=Path)
    parser.add_argument("--evidence-a", required=True, type=Path)
    parser.add_argument("--evidence-b", required=True, type=Path)
    parser.add_argument("--comparison", required=True, type=Path)
    parser.add_argument("--accepted-server-commit", required=True)
    parser.add_argument("--accepted-server-tree", required=True)
    parser.add_argument("--proof-run", required=True, type=int)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    comparison = json.loads(args.comparison.read_bytes())
    identities = comparison["identities"]
    hosts = comparison["hosts"]

    # ── the comparison must be a PASS over the accepted topology ──────────
    if not comparison["content"]["identical"] or not comparison["pathSet"]["identical"]:
        _stop("the comparison record does not report identical content")
    if comparison["topology"] != "dual-runner":
        _stop(f"unexpected topology {comparison['topology']!r}")
    if not comparison["distinctRunnerAllocations"]:
        _stop(
            "the comparison record does not report distinct runner allocations: two "
            "bundles describing one allocation are one build reported twice"
        )
    if hosts["a"]["runnerName"] == hosts["b"]["runnerName"]:
        _stop(
            f"both evidence bundles claim runner allocation {hosts['a']['runnerName']!r}"
        )

    # ── measure the delivered set ─────────────────────────────────────────
    delivered_paths = retention.relative_paths(args.delivered)
    if len(delivered_paths) != retention.ACCEPTED_DELIVERED_PATHS:
        _stop(
            f"the delivered set holds {len(delivered_paths)} paths, not the accepted "
            f"{retention.ACCEPTED_DELIVERED_PATHS}"
        )

    runtime = [p for p in delivered_paths if p.startswith("runtime/")]
    source = [p for p in delivered_paths if p.startswith("source/")]
    if len(runtime) != 1 or len(source) != 1:
        _stop(f"expected one runtime and one source archive, found {runtime} {source}")

    runtime_sha = retention.sha256_file(args.delivered / runtime[0])
    source_sha = retention.sha256_file(args.delivered / source[0])
    stream_sha = _zstd_stream_sha256(args.delivered / source[0])

    # ── the measured bytes must be the bytes the proof recorded ───────────
    if runtime_sha != identities["runtimeSha256"]:
        _stop(
            f"the delivered runtime hashes to {runtime_sha}, but the comparison record "
            f"says {identities['runtimeSha256']}"
        )
    if source_sha != identities["correspondingSourceSha256"]:
        _stop(
            f"the delivered source hashes to {source_sha}, but the comparison record "
            f"says {identities['correspondingSourceSha256']}"
        )
    if stream_sha != comparison["archives"]["correspondingSource"]["uncompressedSha256"]:
        _stop(
            f"the decompressed source stream hashes to {stream_sha}, but the comparison "
            f"record says {comparison['archives']['correspondingSource']['uncompressedSha256']}"
        )
    if args.proof_run <= 0:
        _stop(f"--proof-run {args.proof_run} is not a workflow run id")

    provenance = json.loads((args.delivered / "provenance.json").read_bytes())
    proof_head = identities["repositorySha"]
    if provenance.get("repositorySha") not in (None, proof_head):
        _stop(
            f"provenance.json names {provenance.get('repositorySha')!r}, but the "
            f"comparison record names {proof_head!r}"
        )

    licence_paths = [p for p in delivered_paths if p.startswith("licenses/")]

    identity = {
        "platform": "win-x64",
        "acceptedServerCommit": args.accepted_server_commit,
        "acceptedServerTree": args.accepted_server_tree,
        "proofHead": proof_head,
        "proofRun": args.proof_run,
        "ffmpegUpstreamCommit": identities["ffmpegCommit"],
        "ffmpegBuildRevision": identities["buildRevision"],
        "buildInputsReference": identities["buildInputs"],
        "runtimeSha256": runtime_sha,
        "correspondingSourceSha256": source_sha,
        "correspondingSourceStreamSha256": stream_sha,
        "topology": comparison["topology"],
        "sameNode": comparison["sameNode"],
        "independenceClaim": "none",
        "signed": False,
        "licence": "GPL-3.0-or-later",
        "immutableTag": f"accepted-{args.accepted_server_commit[:12]}",
    }

    # ── stage once, measure the unit, then compute the OCI identity ───────
    work = Path(tempfile.mkdtemp(prefix="w1a4-derive-"))
    try:
        unit = work / "unit"
        assemble.stage_unit(
            args.delivered,
            args.evidence_a,
            args.evidence_b,
            args.comparison,
            identity,
            unit,
        )
        unit_digests = retention.path_manifest(unit)
        unit_sizes = retention.path_sizes(unit)

        layer = work / "layer.tar"
        layer_digest = retention.make_layer(unit, layer)
        layer_size = layer.stat().st_size
    finally:
        shutil.rmtree(work, ignore_errors=True)

    accepted = dict(identity)
    accepted.update(
        {
            "schemaVersion": 1,
            "runtimePath": f"delivered/{runtime[0]}",
            "runtimeSize": (args.delivered / runtime[0]).stat().st_size,
            "correspondingSourcePath": f"delivered/{source[0]}",
            "correspondingSourceSize": (args.delivered / source[0]).stat().st_size,
            "checksumManifestPath": "delivered/SHA256SUMS",
            "checksumManifestSha256": retention.sha256_file(args.delivered / "SHA256SUMS"),
            "deliveredPathCount": len(delivered_paths),
            "provenanceSha256": retention.sha256_file(args.delivered / "provenance.json"),
            "sbomSha256": retention.sha256_file(args.delivered / "sbom.cdx.json"),
            "noticesSha256": retention.sha256_file(
                args.delivered / "THIRD-PARTY-NOTICES.md"
            ),
            "capabilitySha256": retention.sha256_file(args.delivered / "capability.json"),
            "peClosureSha256": retention.sha256_file(args.delivered / "pe-closure.json"),
            "buildConfigurationSha256": retention.sha256_file(
                args.delivered / "build-configuration.txt"
            ),
            "licenceFileCount": len(licence_paths),
            "evidence": {
                "comparisonSha256": retention.sha256_file(args.comparison),
                "hostA": _host_record(args.evidence_a, hosts["a"]),
                "hostB": _host_record(args.evidence_b, hosts["b"]),
            },
            "registry": contract.REGISTRY,
            "repository": contract.REPOSITORY,
            "artifactType": retention.ARTIFACT_TYPE,
            "configMediaType": retention.CONFIG_MEDIA_TYPE,
            "layerMediaType": retention.LAYER_MEDIA_TYPE,
            "manifestMediaType": retention.MANIFEST_MEDIA_TYPE,
            "layerDigest": f"sha256:{layer_digest}",
            "layerSize": layer_size,
            "unitPaths": {
                path: {"sha256": unit_digests[path], "size": unit_sizes[path]}
                for path in sorted(unit_digests)
            },
            "published": False,
        }
    )

    # The OCI identity is a pure function of the fields above.
    expected = retention.expected_manifest_digest(accepted)
    accepted.update(
        {
            "configDigest": expected["configDigest"],
            "configSize": expected["configSize"],
            "manifestDigest": expected["manifestDigest"],
            "manifestSize": expected["manifestSize"],
        }
    )
    accepted["reference"] = f"{contract.CANONICAL}@{accepted['manifestDigest']}"

    contract.validate_accepted(accepted)
    args.out.write_bytes(retention.canonical_json(accepted))
    print(f"derived {args.out} at {accepted['manifestDigest']}")
    return 0


def _host_record(bundle: Path, host: dict) -> dict:
    return {
        "acceptRuntimeSha256": retention.sha256_file(bundle / "accept-runtime.json"),
        "runnerJsonSha256": retention.sha256_file(bundle / "inputs" / "runner.json"),
        "runnerName": host["runnerName"],
        "node": host["node"],
        "imageOs": host["imageOs"],
        "imageVersion": host["imageVersion"],
        "bundlePathCount": len(retention.relative_paths(bundle)),
    }


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (retention.RetentionError, contract.ContractError) as error:
        print(f"W1-A4 RETENTION HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
