"""Build a small synthetic retention unit and its acceptance manifest (#236, W1-A4).

Usage:
    python3 make-fixture.py --out <dir>

Why a fixture exists at all. The real retention unit is 260 MB and is assembled
from Actions artifacts that EXPIRE. A gate whose inputs expire is a gate that
silently stops checking — that is the W1-A3 O-1 failure mode, and building the
pull-request gate on those artifacts would reproduce it exactly.

So the gate runs on this instead: a unit with the same SHAPE as the real one —
the same layout, the same required paths, the same evidence structure, the same
GPL pairing — and a few kilobytes of content. Every property the machinery
enforces is structural, so a fixture exercises all of them. The real unit's
identity is separately guaranteed by the committed acceptance manifest, whose
expected digest is reconstructible from committed data alone.

The fixture is generated rather than committed so that it cannot drift from the
schema it is supposed to exercise.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tarfile
import tempfile
from pathlib import Path

import contract
import retention

# Fixed, fabricated identities. They are obviously not the accepted ones: a
# fixture that reused the real digests could be mistaken for the real unit in a
# log, and a control that passed against it would prove nothing.
FIXTURE_COMMIT = "1" * 40
FIXTURE_TREE = "2" * 40
FIXTURE_PROOF_HEAD = "3" * 40
FIXTURE_FFMPEG = "4" * 40
FIXTURE_RUN = 1
FIXTURE_BUILD_INPUTS = (
    "ghcr.io/tesserafin-project/windows-ffmpeg-build-inputs@sha256:" + "5" * 64
)

LICENCE_NAMES = [f"fixture-{index:02d}-COPYING" for index in range(22)]


def _write(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def build_delivered(root: Path) -> None:
    """The 31 accepted delivered paths, in miniature."""
    payload = b"fixture runtime payload, not a real FFmpeg build\n"
    zip_path = root / "runtime" / "tesserafin-ffmpeg-0.0.0-fixture-win-x64.zip"
    zip_path.parent.mkdir(parents=True, exist_ok=True)
    import zipfile

    with zipfile.ZipFile(zip_path, "w") as archive:
        # A fixed timestamp: a zip that changes between runs would make the
        # fixture itself non-deterministic and mask a real regression.
        info = zipfile.ZipInfo("bin/ffmpeg.exe", date_time=(1980, 1, 1, 0, 0, 0))
        archive.writestr(info, payload)

    source_dir = root / "source"
    source_dir.mkdir(parents=True, exist_ok=True)
    tar_path = source_dir / "fixture-source.tar"
    with tarfile.open(tar_path, "w", format=tarfile.USTAR_FORMAT) as archive:
        info = tarfile.TarInfo("ffmpeg/README")
        data = b"fixture corresponding source\n"
        info.size = len(data)
        info.mtime = 0
        info.mode = 0o644
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        import io

        archive.addfile(info, io.BytesIO(data))
    subprocess.run(
        ["zstd", "-19", "-q", "-f", str(tar_path), "-o", str(source_dir / "fixture-source.tar.zst")],
        check=True,
    )
    tar_path.unlink()

    _write(root / "SHA256SUMS", b"0000  fixture\n")
    _write(root / "THIRD-PARTY-NOTICES.md", b"# fixture notices\n")
    _write(root / "build-configuration.txt", b"--fixture\n")
    _write(root / "capability.json", retention.canonical_json({"fixture": True}))
    _write(root / "pe-closure.json", retention.canonical_json({"fixture": True}))
    _write(root / "provenance.json", retention.canonical_json({"repositorySha": FIXTURE_PROOF_HEAD}))
    _write(root / "sbom.cdx.json", retention.canonical_json({"bomFormat": "CycloneDX"}))
    for name in LICENCE_NAMES:
        _write(root / "licenses" / name, f"fixture licence {name}\n".encode())


def build_evidence(root: Path, host: str, runner: str) -> None:
    _write(
        root / "accept-runtime.json",
        retention.canonical_json({"host": host, "verdict": "PASS", "fixture": True}),
    )
    _write(
        root / "inputs" / "runner.json",
        retention.canonical_json(
            {
                "runnerName": runner,
                "node": "fixturenode",
                "imageOs": "fixture",
                "imageVersion": "0",
            }
        ),
    )
    _write(root / "inputs" / "toolchain.json", retention.canonical_json({"fixture": True}))
    _write(root / "inputs" / "consume.json", retention.canonical_json({"fixture": True}))


def build_comparison(path: Path, runtime_sha: str, source_sha: str, stream_sha: str) -> None:
    _write(
        path,
        retention.canonical_json(
            {
                "archives": {
                    "correspondingSource": {
                        "identical": True,
                        "sha256": source_sha,
                        "uncompressedIdentical": True,
                        "uncompressedSha256": stream_sha,
                    },
                    "runtime": {"identical": True, "sha256": runtime_sha},
                },
                "content": {"compared": 31, "differing": [], "identical": True},
                "distinctRunnerAllocations": True,
                "hosts": {
                    "a": {
                        "deliveredPaths": 31,
                        "imageOs": "fixture",
                        "imageVersion": "0",
                        "node": "fixturenode",
                        "runnerArch": "X64",
                        "runnerName": "Fixture Runner A",
                    },
                    "b": {
                        "deliveredPaths": 31,
                        "imageOs": "fixture",
                        "imageVersion": "0",
                        "node": "fixturenode",
                        "runnerArch": "X64",
                        "runnerName": "Fixture Runner B",
                    },
                },
                "identities": {
                    "buildInputs": FIXTURE_BUILD_INPUTS,
                    "buildRevision": "0.0.0-fixture",
                    "correspondingSourceSha256": source_sha,
                    "ffmpegCommit": FIXTURE_FFMPEG,
                    "patchesApplied": 0,
                    "repositorySha": FIXTURE_PROOF_HEAD,
                    "runtimeSha256": runtime_sha,
                },
                "independenceClaim": "none",
                "pathSet": {"count": 31, "identical": True, "onlyOnA": [], "onlyOnB": []},
                "probe": "fixture",
                "sameNode": True,
                "sameRunnerImage": True,
                "topology": "dual-runner",
                "verdict": "PASS: fixture",
            }
        ),
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    root = args.out
    if root.exists():
        import shutil

        shutil.rmtree(root)
    root.mkdir(parents=True)

    delivered = root / "delivered"
    build_delivered(delivered)
    build_evidence(root / "evidence-a", "a", "Fixture Runner A")
    build_evidence(root / "evidence-b", "b", "Fixture Runner B")

    runtime_path = next((delivered / "runtime").iterdir())
    source_path = next((delivered / "source").iterdir())
    runtime_sha = retention.sha256_file(runtime_path)
    source_sha = retention.sha256_file(source_path)
    stream = subprocess.run(
        ["zstd", "-dc", str(source_path)], check=True, capture_output=True
    ).stdout
    stream_sha = retention.sha256_bytes(stream)

    comparison = root / "comparison.json"
    build_comparison(comparison, runtime_sha, source_sha, stream_sha)

    delivered_count = len(retention.relative_paths(delivered))
    if delivered_count != retention.ACCEPTED_DELIVERED_PATHS:
        raise retention.RetentionError(
            f"the fixture delivered set holds {delivered_count} paths, not "
            f"{retention.ACCEPTED_DELIVERED_PATHS}; the fixture must have the same "
            "shape as the accepted unit or it exercises a different contract"
        )

    subprocess.run(
        [
            sys.executable,
            str(Path(__file__).parent / "derive-accepted.py"),
            "--delivered", str(delivered),
            "--evidence-a", str(root / "evidence-a"),
            "--evidence-b", str(root / "evidence-b"),
            "--comparison", str(comparison),
            "--accepted-server-commit", FIXTURE_COMMIT,
            "--accepted-server-tree", FIXTURE_TREE,
            "--proof-run", str(FIXTURE_RUN),
            "--out", str(root / "accepted-runtime.json"),
        ],
        check=True,
        stdout=subprocess.DEVNULL,
    )

    accepted = json.loads((root / "accepted-runtime.json").read_bytes())
    contract.validate_accepted(accepted)
    print(f"fixture at {root}, manifest {accepted['manifestDigest']}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (retention.RetentionError, contract.ContractError) as error:
        print(f"W1-A4 FIXTURE HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
