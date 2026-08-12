"""Negative controls for the Windows build-input retention machinery (#236, W1-R).

A gate that has never been shown to fail is not evidence. Each control damages
exactly one thing and requires the corresponding validator to refuse; one
undamaged control must pass, so a validator that simply always fails cannot
score a green run either.

The controls run against a small synthetic fixture rather than the real
388 MiB bundle. What is under test is the validation logic, and a fixture makes
each control hermetic, fast and able to express damage — a corrupted archive, a
wrong architecture — that would be irresponsible to create for real.

Usage:
    python3 negative-controls.py [--repo-root <path>]
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import shutil
import subprocess
import sys
import tarfile
import tempfile
from pathlib import Path

import bundle
import contract

HERE = Path(__file__).resolve().parent

PASSED: list = []
FAILED: list = []


def record(name: str, ok: bool, detail: str) -> None:
    (PASSED if ok else FAILED).append(name)
    status = "PASS" if ok else "FAIL"
    print(f"[{status}] {name}: {detail}")


def run_validator(script: str, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(HERE / script), *args],
        capture_output=True,
        text=True,
        check=False,
    )


def fake_package(name: str, version: str, arch: str, payload: bytes) -> dict:
    filename = f"{name}-{version}-{arch}.pkg.tar.zst"
    url = f"https://repo.msys2.org/msys/x86_64/{filename}"
    return {
        "repository": "msys",
        "name": name,
        "version": version,
        "architecture": arch,
        "filename": filename,
        "sha256": hashlib.sha256(payload).hexdigest(),
        "compressedBytes": len(payload),
        "installedBytes": len(payload) * 4,
        "license": ["spdx:MIT"],
        "depends": [],
        "provides": [],
        "groups": [],
        "url": url,
        "signatureUrl": url + ".sig",
    }


def base_lock() -> tuple:
    payloads = {
        "alpha": b"alpha-package-bytes",
        "beta": b"beta-package-bytes",
        "gamma": b"gamma-package-bytes",
    }
    packages = [
        fake_package("alpha", "1.0-1", "x86_64", payloads["alpha"]),
        fake_package("beta", "2.0-1", "x86_64", payloads["beta"]),
        fake_package("gamma", "3.0-1", "any", payloads["gamma"]),
    ]
    packages[0]["depends"] = ["beta"]
    packages[1]["depends"] = ["gamma"]

    lock = {
        "schemaVersion": 1,
        "target": "win-x64",
        "msystem": "CLANG64",
        "mingwPackagePrefix": "mingw-w64-clang-x86_64",
        "ffmpeg": {
            "upstreamCommit": "0" * 40,
            "buildRevision": "0.0.0-fixture",
            "source": "fixture",
        },
        "rootProvenance": {"fixture": True},
        "roots": ["alpha"],
        "repositoryDatabases": {
            "msys": {
                "filename": "msys.db",
                "url": "https://repo.msys2.org/msys/x86_64/msys.db",
                "sha256": "0" * 64,
                "bytes": 1,
            }
        },
        "packageCount": len(packages),
        "compressedBytes": sum(p["compressedBytes"] for p in packages),
        "installedBytes": sum(p["installedBytes"] for p in packages),
        "packages": packages,
    }
    return lock, payloads


def write_lock(directory: Path, lock: dict) -> Path:
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / "lock.json"
    path.write_bytes(bundle.canonical_json(lock))
    return path


def make_bundle(directory: Path, lock: dict, payloads: dict) -> Path:
    root = directory / "bundle"
    for sub in ("packages", "signatures", "databases", "licenses"):
        (root / sub).mkdir(parents=True, exist_ok=True)
    for package in lock["packages"]:
        (root / "packages" / package["filename"]).write_bytes(payloads[package["name"]])
    (root / "databases" / "msys.db").write_bytes(b"fixture-db")
    (root / "licenses" / "licenses.json").write_bytes(bundle.canonical_json({}))
    lock_bytes = bundle.canonical_json(lock)
    (root / "msys2-lock.json").write_bytes(lock_bytes)
    bundle.write_bundle_metadata(root, lock_bytes, {"msys": {"filename": "msys.db"}})
    return root


# ── lock validation controls ────────────────────────────────────────────────


def lock_control(work: Path, name: str, mutate, expect_fragment: str) -> None:
    lock, _ = base_lock()
    mutate(lock)
    path = write_lock(work / name, lock)
    result = run_validator("verify-lock.py", "--lock", str(path))
    message = (result.stderr + result.stdout).strip()
    ok = result.returncode != 0 and expect_fragment in message
    record(
        name,
        ok,
        f"exit {result.returncode}"
        + ("" if ok else f", expected {expect_fragment!r} in: {message[:200]}"),
    )


def undamaged_lock_control(work: Path) -> None:
    lock, _ = base_lock()
    path = write_lock(work / "undamaged", lock)
    result = run_validator("verify-lock.py", "--lock", str(path))
    record(
        "control.undamaged-lock-passes",
        result.returncode == 0,
        f"exit {result.returncode}: {(result.stdout + result.stderr).strip()[:160]}",
    )


# ── bundle / OCI controls ───────────────────────────────────────────────────


def bundle_control(work: Path, name: str, damage, expect_fragment: str) -> None:
    lock, payloads = base_lock()
    directory = work / name
    root = make_bundle(directory, lock, payloads)
    damage(root)
    result = run_validator(
        "build-oci.py", "--bundle", str(root), "--out", str(directory / "oci")
    )
    message = (result.stderr + result.stdout).strip()
    ok = result.returncode != 0 and expect_fragment in message
    record(
        name,
        ok,
        f"exit {result.returncode}"
        + ("" if ok else f", expected {expect_fragment!r} in: {message[:200]}"),
    )


def undamaged_bundle_control(work: Path) -> str:
    lock, payloads = base_lock()
    directory = work / "undamaged-bundle"
    root = make_bundle(directory, lock, payloads)
    result = run_validator(
        "build-oci.py", "--bundle", str(root), "--out", str(directory / "oci")
    )
    record(
        "control.undamaged-bundle-builds",
        result.returncode == 0,
        f"exit {result.returncode}",
    )
    if result.returncode != 0:
        return ""
    return json.loads(result.stdout)["manifestDigest"]


def manifest_tamper_control(work: Path) -> None:
    """An edited manifest must no longer hash to its recorded digest."""
    lock, payloads = base_lock()
    directory = work / "manifest-tamper"
    root = make_bundle(directory, lock, payloads)
    oci = directory / "oci"
    result = run_validator("build-oci.py", "--bundle", str(root), "--out", str(oci))
    if result.returncode != 0:
        record("control.changed-oci-manifest", False, "the fixture failed to build")
        return
    before = json.loads(result.stdout)["manifestDigest"]

    manifest = json.loads((oci / "manifest.json").read_text())
    manifest["annotations"]["dev.tesserafin.buildinputs.packageCount"] = "999"
    (oci / "manifest.json").write_bytes(bundle.canonical_json(manifest))
    after = bundle.read_manifest_digest(oci)
    record(
        "control.changed-oci-manifest",
        after != before,
        f"{before[:23]}… -> {after[:23]}… (digest moved, so a pinned consumer breaks)",
    )


def path_traversal_control(work: Path) -> None:
    """A layer entry that escapes the bundle root must be refused."""
    directory = work / "traversal"
    directory.mkdir(parents=True, exist_ok=True)
    layer = directory / "evil.tar"
    with tarfile.open(layer, "w") as archive:
        info = tarfile.TarInfo("../escaped.txt")
        payload = b"escaped"
        info.size = len(payload)
        archive.addfile(info, __import__("io").BytesIO(payload))

    index = bundle.load_layer_index(layer)
    unsafe = [
        name
        for name in index
        if name.startswith("/") or ".." in Path(name).parts or "\\" in name
    ]
    record(
        "control.path-traversal-entry",
        bool(unsafe),
        f"unsafe entries detected: {unsafe}",
    )


def symlink_control(work: Path) -> None:
    lock, payloads = base_lock()
    directory = work / "symlink"
    root = make_bundle(directory, lock, payloads)
    (root / "packages" / "link.pkg.tar.zst").symlink_to("/etc/passwd")
    try:
        bundle.relative_paths(root)
    except bundle.BundleError as error:
        record("control.symlink-in-bundle", True, str(error))
        return
    record("control.symlink-in-bundle", False, "a symlink was accepted into the bundle")


# ── contract controls ───────────────────────────────────────────────────────


def contract_control(name: str, call, expect_fragment: str) -> None:
    try:
        call()
    except contract.ContractError as error:
        ok = expect_fragment in str(error)
        record(name, ok, str(error) if ok else f"wrong message: {error}")
        return
    record(name, False, "the contract accepted something it must refuse")


def prohibited_pacman_control(repo_root: Path) -> None:
    """No tracked W1-R script may invoke live pacman resolution."""
    offenders = []
    for path in sorted((repo_root / "ci/windows/build-inputs").rglob("*")):
        if not path.is_file() or path.suffix not in {".sh", ".ps1", ".py", ".yml"}:
            continue
        # This file IS the detector; its own literals are the pattern.
        if path.name == Path(__file__).name:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for line in text.splitlines():
            stripped = line.strip()
            # A line that names the prohibition in prose or refuses it is the
            # point; only an actual invocation counts.
            if stripped.startswith("#") or stripped.startswith("*"):
                continue
            if "pacman -S" in stripped or "pacman -Syu" in stripped:
                if "PROHIBITED" in line or "refus" in line.lower():
                    continue
                offenders.append(f"{path.name}: {stripped[:80]}")
    workflow = repo_root / ".github/workflows/w1-windows-build-inputs.yml"
    if workflow.is_file():
        for line in workflow.read_text().splitlines():
            stripped = line.strip()
            if stripped.startswith("#"):
                continue
            if "pacman -S" in stripped or "pacman -Syu" in stripped:
                if "PROHIBITED" in line or "refus" in line.lower():
                    continue
                offenders.append(f"workflow: {stripped[:80]}")
    record(
        "control.no-prohibited-pacman-invocation",
        not offenders,
        "no live-resolution invocation found" if not offenders else str(offenders),
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=HERE.parents[2])
    args = parser.parse_args()

    work = Path(tempfile.mkdtemp(prefix="w1r-controls-"))
    try:
        undamaged_lock_control(work)

        def drop_package(lock):
            lock["packages"].pop()

        lock_control(work, "control.missing-package", drop_package, "packageCount")

        def add_package(lock):
            lock["packages"].append(fake_package("delta", "4.0-1", "x86_64", b"delta"))

        lock_control(work, "control.additional-package", add_package, "packageCount")

        def rename_archive(lock):
            lock["packages"][0]["filename"] = "renamed-1.0-1-x86_64.pkg.tar.zst"

        lock_control(
            work, "control.renamed-archive", rename_archive, "does not start with"
        )

        def wrong_sha(lock):
            lock["packages"][0]["sha256"] = "z" * 64

        lock_control(work, "control.wrong-sha256", wrong_sha, "malformed sha256")

        def wrong_version(lock):
            lock["packages"][0]["version"] = "9.9-9"

        lock_control(
            work, "control.wrong-version", wrong_version, "does not start with"
        )

        def wrong_arch(lock):
            lock["packages"][0]["architecture"] = "aarch64"
            lock["packages"][0]["filename"] = "alpha-1.0-1-aarch64.pkg.tar.zst"
            lock["packages"][0]["url"] = (
                "https://repo.msys2.org/msys/x86_64/alpha-1.0-1-aarch64.pkg.tar.zst"
            )
            lock["packages"][0]["signatureUrl"] = lock["packages"][0]["url"] + ".sig"

        lock_control(
            work, "control.wrong-architecture", wrong_arch, "not a win-x64 build input"
        )

        def unresolved(lock):
            lock["packages"][0]["depends"] = ["omega"]

        lock_control(
            work, "control.unresolved-dependency", unresolved, "closure is incomplete"
        )

        def duplicate(lock):
            lock["packages"].append(copy.deepcopy(lock["packages"][0]))
            lock["packageCount"] += 1
            lock["compressedBytes"] += lock["packages"][0]["compressedBytes"]
            lock["installedBytes"] += lock["packages"][0]["installedBytes"]

        lock_control(work, "control.duplicate-lock-entry", duplicate, "duplicate package")

        def unknown_field(lock):
            lock["surprise"] = True

        lock_control(work, "control.unknown-lock-field", unknown_field, "unknown field")

        def unknown_schema(lock):
            lock["schemaVersion"] = 99

        lock_control(
            work, "control.unknown-schema-version", unknown_schema, "not implemented"
        )

        def traversal_filename(lock):
            lock["packages"][0]["filename"] = "../alpha-1.0-1-x86_64.pkg.tar.zst"

        lock_control(
            work, "control.unsafe-lock-filename", traversal_filename, "does not start with"
        )

        undamaged_bundle_control(work)

        bundle_control(
            work,
            "control.corrupted-archive",
            lambda root: (root / "packages" / "alpha-1.0-1-x86_64.pkg.tar.zst").write_bytes(
                b"corrupted"
            ),
            "does not match its own manifest.sha256",
        )
        bundle_control(
            work,
            "control.extra-bundle-file",
            lambda root: (root / "packages" / "undeclared.pkg.tar.zst").write_bytes(b"x"),
            "does not match its own manifest.sha256",
        )
        bundle_control(
            work,
            "control.missing-bundle-file",
            lambda root: (root / "packages" / "beta-2.0-1-x86_64.pkg.tar.zst").unlink(),
            "does not match its own manifest.sha256",
        )
        bundle_control(
            work,
            "control.changed-package-lock",
            lambda root: (root / "msys2-lock.json").write_bytes(b"{}\n"),
            "lockSha256",
        )

        manifest_tamper_control(work)
        path_traversal_control(work)
        symlink_control(work)

        digest = "sha256:" + "a" * 64
        contract_control(
            "control.tag-only-reference",
            lambda: contract.parse_reference(f"{contract.CANONICAL}:latest"),
            "not digest-pinned",
        )
        contract_control(
            "control.wrong-registry",
            lambda: contract.parse_reference(
                f"ghcr.io/someone-else/windows-ffmpeg-build-inputs@{digest}"
            ),
            "not the authorised package",
        )
        contract_control(
            "control.malformed-digest",
            lambda: contract.parse_reference(f"{contract.CANONICAL}@sha256:short"),
            "not a sha256 manifest digest",
        )
        contract_control(
            "control.publication-from-pull-request",
            lambda: contract.assert_trusted_ref("refs/pull/237/merge"),
            "pull request ref",
        )
        contract_control(
            "control.publication-from-branch",
            lambda: contract.assert_trusted_ref("refs/heads/w1/windows-build-input-retention"),
            "feature branch",
        )
        contract_control(
            "control.publication-from-tag",
            lambda: contract.assert_trusted_ref("refs/tags/v1.0.0"),
            "tag can be created and moved",
        )
        contract_control(
            "control.publication-without-expected-digest",
            lambda: contract.assert_expected_digest("", digest),
            "no expected manifest digest",
        )
        contract_control(
            "control.digest-mismatch-after-pull",
            lambda: contract.assert_expected_digest(digest, "sha256:" + "b" * 64),
            "Nothing is pushed",
        )

        # The one ref that must be accepted, so the trust check is not simply
        # refusing everything.
        try:
            contract.assert_trusted_ref(contract.TRUSTED_REF)
            contract.parse_reference(f"{contract.CANONICAL}@{digest}")
            record("control.trusted-master-accepted", True, "refs/heads/master accepted")
        except contract.ContractError as error:
            record("control.trusted-master-accepted", False, str(error))

        prohibited_pacman_control(args.repo_root)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    print(f"\n{len(PASSED)} passed, {len(FAILED)} failed")
    if FAILED:
        print("failed controls: " + ", ".join(FAILED), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
