"""Strict, offline validation of the committed MSYS2 package lock (#236, W1-R).

This runs in CI on every change and contacts nothing. It is the gate that makes
the lock a contract rather than a document: it fails closed on an unknown schema
version, an unknown field, a missing required field, a duplicate, an unresolved
dependency, or a value whose shape is wrong.

"Fail closed on unknown" is deliberate and is the opposite of the usual JSON
tolerance. A field this validator does not understand is a semantic it does not
enforce, and a lock carrying unenforced semantics would look validated while
being unvalidated.

Usage:
    python3 verify-lock.py --lock <lock.json> [--schema <schema.json>]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

import msys2db

SUPPORTED_SCHEMA_VERSIONS = {1}

LOCK_REQUIRED = {
    "schemaVersion",
    "target",
    "msystem",
    "mingwPackagePrefix",
    "ffmpeg",
    "rootProvenance",
    "roots",
    "providerOverrides",
    "repositoryDatabases",
    "packageCount",
    "compressedBytes",
    "installedBytes",
    "packages",
}
PACKAGE_REQUIRED = {
    "repository",
    "name",
    "version",
    "architecture",
    "filename",
    "sha256",
    "compressedBytes",
    "installedBytes",
    "license",
    "depends",
    "provides",
    "groups",
    "url",
    "signatureUrl",
}
OVERRIDE_REQUIRED = {"virtual", "package", "constraint", "reason"}
DATABASE_REQUIRED = {"filename", "url", "sha256", "bytes"}

SHA256 = re.compile(r"^[0-9a-f]{64}$")
ALLOWED_REPOSITORIES = {"msys", "clang64"}
ALLOWED_ARCHITECTURES = {"any", "x86_64"}
ALLOWED_URL_PREFIXES = (
    "https://repo.msys2.org/msys/x86_64/",
    "https://repo.msys2.org/mingw/clang64/",
)


def check_keys(where: str, value: dict, required: set) -> None:
    keys = set(value)
    missing = required - keys
    unknown = keys - required
    if missing:
        raise msys2db.LockError(f"{where}: missing required field(s): {sorted(missing)}")
    if unknown:
        raise msys2db.LockError(
            f"{where}: unknown field(s) {sorted(unknown)} — this validator does not "
            "implement their semantics and refuses to report the lock as validated"
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", required=True, type=Path)
    parser.add_argument(
        "--databases",
        type=Path,
        default=Path(__file__).resolve().parent / "databases",
    )
    args = parser.parse_args()

    lock = json.loads(args.lock.read_text())
    check_keys("lock", lock, LOCK_REQUIRED)

    if lock["schemaVersion"] not in SUPPORTED_SCHEMA_VERSIONS:
        raise msys2db.LockError(
            f"schemaVersion {lock['schemaVersion']} is not implemented "
            f"(supported: {sorted(SUPPORTED_SCHEMA_VERSIONS)})"
        )
    if lock["target"] != "win-x64":
        raise msys2db.LockError(f"unexpected target {lock['target']!r}")
    if lock["msystem"] != "CLANG64":
        raise msys2db.LockError(f"unexpected msystem {lock['msystem']!r}")

    # Every provider override the closure USED is transcribed into the lock, so
    # a reviewer of the lock alone can see which ambiguities were decided by
    # hand and on what grounds. An empty list is the normal case and says the
    # closure contained no ambiguity at all.
    for entry in lock["providerOverrides"]:
        check_keys("providerOverrides[]", entry, OVERRIDE_REQUIRED)
        if entry["virtual"] in {package["name"] for package in lock["packages"]}:
            raise msys2db.LockError(
                f"providerOverrides: {entry['virtual']!r} is a real package in this "
                "closure and can never need an override"
            )

    for repo, record in lock["repositoryDatabases"].items():
        check_keys(f"repositoryDatabases.{repo}", record, DATABASE_REQUIRED)
        if repo not in ALLOWED_REPOSITORIES:
            raise msys2db.LockError(f"unknown repository identity {repo!r}")
        if not SHA256.match(record["sha256"]):
            raise msys2db.LockError(f"repositoryDatabases.{repo}: malformed sha256")

        # The database the lock was resolved from is part of the reviewed
        # change and must be committed beside it, byte for byte.
        committed = args.databases / record["filename"]
        if not committed.is_file():
            raise msys2db.LockError(
                f"repositoryDatabases.{repo}: {record['filename']} is not committed "
                f"under {args.databases}"
            )
        actual = hashlib.sha256(committed.read_bytes()).hexdigest()
        if actual != record["sha256"]:
            raise msys2db.LockError(
                f"repositoryDatabases.{repo}: the committed {record['filename']} "
                f"hashes to {actual}, the lock records {record['sha256']}"
            )

    names: set = set()
    filenames: set = set()
    provided: dict = {}
    compressed = 0
    installed = 0

    for package in lock["packages"]:
        where = f"packages[{package.get('name', '?')}]"
        check_keys(where, package, PACKAGE_REQUIRED)

        if package["repository"] not in ALLOWED_REPOSITORIES:
            raise msys2db.LockError(f"{where}: unknown repository {package['repository']!r}")
        if package["architecture"] not in ALLOWED_ARCHITECTURES:
            raise msys2db.LockError(
                f"{where}: architecture {package['architecture']!r} is not a "
                "win-x64 build input"
            )
        if not SHA256.match(package["sha256"]):
            raise msys2db.LockError(f"{where}: malformed sha256 {package['sha256']!r}")
        if package["compressedBytes"] <= 0:
            raise msys2db.LockError(f"{where}: non-positive compressedBytes")

        if package["name"] in names:
            raise msys2db.LockError(f"{where}: duplicate package name")
        names.add(package["name"])
        if package["filename"] in filenames:
            raise msys2db.LockError(f"{where}: duplicate filename {package['filename']}")
        filenames.add(package["filename"])

        # The filename must actually name this package at this version. A lock
        # whose filename and version disagree would fetch the wrong bytes and
        # then fail a digest check with a misleading message.
        expected_prefix = f"{package['name']}-{package['version']}-"
        if not package["filename"].startswith(expected_prefix):
            raise msys2db.LockError(
                f"{where}: filename {package['filename']!r} does not start with "
                f"{expected_prefix!r}"
            )
        if not package["filename"].endswith(f"-{package['architecture']}.pkg.tar.zst"):
            raise msys2db.LockError(
                f"{where}: filename {package['filename']!r} does not end with the "
                f"declared architecture {package['architecture']!r}"
            )

        if not package["url"].startswith(ALLOWED_URL_PREFIXES):
            raise msys2db.LockError(f"{where}: url outside the MSYS2 repository")
        if not package["url"].endswith(package["filename"]):
            raise msys2db.LockError(f"{where}: url does not name the locked filename")
        if package["signatureUrl"] != package["url"] + ".sig":
            raise msys2db.LockError(f"{where}: signatureUrl is not the archive's .sig")

        # A path separator in a filename would let a bundle entry escape its
        # directory when extracted.
        if "/" in package["filename"] or "\\" in package["filename"]:
            raise msys2db.LockError(f"{where}: filename contains a path separator")
        if package["filename"].startswith(".") or ".." in package["filename"]:
            raise msys2db.LockError(f"{where}: unsafe filename {package['filename']!r}")

        compressed += package["compressedBytes"]
        installed += package["installedBytes"]

        provided[package["name"]] = package["name"]
        for expression in package["provides"]:
            provided.setdefault(re.split(r"[<>=]", expression)[0], package["name"])

    if len(lock["packages"]) != lock["packageCount"]:
        raise msys2db.LockError(
            f"packageCount {lock['packageCount']} but {len(lock['packages'])} packages"
        )
    if compressed != lock["compressedBytes"]:
        raise msys2db.LockError("compressedBytes disagrees with the sum of the packages")
    if installed != lock["installedBytes"]:
        raise msys2db.LockError("installedBytes disagrees with the sum of the packages")

    # Closure completeness. Every dependency of every locked package must be
    # satisfied from inside the lock — that is what makes `pacman -U` able to
    # install the set with no repository configured at all.
    groups: dict = {}
    for package in lock["packages"]:
        for group in package["groups"]:
            groups.setdefault(group, []).append(package["name"])

    unresolved = set()
    for package in lock["packages"]:
        for expression in package["depends"]:
            name = re.split(r"[<>=]", expression)[0]
            if name not in provided and name not in groups:
                unresolved.add(f"{package['name']} -> {name}")
    if unresolved:
        raise msys2db.LockError(
            "the closure is incomplete; these dependencies are not in the lock: "
            + ", ".join(sorted(unresolved))
        )

    for root in lock["roots"]:
        name = re.split(r"[<>=]", root)[0]
        if name not in provided and name not in groups:
            raise msys2db.LockError(f"root {root!r} is not satisfied by the lock")

    print(
        f"lock ok: {lock['packageCount']} packages, "
        f"{lock['compressedBytes'] / 1048576:.1f} MiB compressed, "
        f"{lock['installedBytes'] / 1048576:.1f} MiB installed, "
        "closure complete, no duplicates, no unknown fields"
    )
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except msys2db.LockError as error:
        print(f"W1-R LOCK HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
