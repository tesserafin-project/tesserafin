"""Generate the MSYS2 package lock for the native Windows FFmpeg build (#236).

THIS IS THE GENERATOR, NOT A CONSUMER. It contacts the live MSYS2 repository and
the pinned upstream tree in order to *propose* a lock; a human reviews the
result and commits it. Once committed, nothing else in W1 resolves anything
dynamically — `verify-lock.py` and `ingest.sh` read the committed file and fail
closed on any disagreement with it.

Every root comes from authoritative metadata rather than from this file:

  * the FFmpeg upstream commit is read from `ci/ffmpeg/components.json` and
    asserted equal to `F0_UPSTREAM_COMMIT` in `ci/package/pins.env`. The Linux
    pins are consumed, never forked;
  * the interactive install set is read from upstream's own
    `.github/workflows/_meta_win_clang_portable.yaml` at that commit, CLANG64
    row;
  * the dependency build tools are read from the `depends`/`makedepends` of
    upstream's 36 `msys2/PKGBUILD/*/PKGBUILD` recipes at that commit;
  * `base-devel` is added because `msys2/build.sh` drives every recipe through
    `makepkg-mingw`, which the `pacman` package ships and `base-devel` pulls in.

Upstream's build script invokes `makepkg-mingw -sLfi`, and `-s` resolves
makedepends against the live repository. W1 cannot do that (#236 forbids live
resolution), so the makedepends are hoisted into this closure and installed from
the locked set beforehand. That substitution is the reason this generator has to
read the recipes at all.

Usage:
    python3 resolve-closure.py --repo-root <path> --out <lock.json>
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
import urllib.request
from pathlib import Path

import msys2db

UPSTREAM_RAW = "https://raw.githubusercontent.com/jellyfin/jellyfin-ffmpeg"
UPSTREAM_API = "https://api.github.com/repos/jellyfin/jellyfin-ffmpeg/contents"
MSYS2_BASE = {
    "msys": "https://repo.msys2.org/msys/x86_64/",
    "clang64": "https://repo.msys2.org/mingw/clang64/",
}
DATABASES = {"msys": "msys.db", "clang64": "clang64.db"}

# The CLANG64 matrix row of upstream's meta workflow. `win-arm64` is explicitly
# out of scope for 1.1, so the CLANGARM64 row is not resolved.
MINGW_PACKAGE_PREFIX = "mingw-w64-clang-x86_64"
LOCK_SCHEMA_VERSION = 1
TARGET = "win-x64"
MSYSTEM = "CLANG64"


def fetch(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "tesserafin-w1r"})
    with urllib.request.urlopen(request, timeout=180) as response:
        if response.status != 200:
            raise msys2db.LockError(f"{url}: HTTP {response.status}")
        return response.read()


def read_pins(repo_root: Path) -> tuple:
    """Read the authoritative FFmpeg pins and assert the two files agree."""
    components = json.loads((repo_root / "ci/ffmpeg/components.json").read_text())
    commit = components["ffmpeg"]["commit"]
    revision = components["buildRevision"]

    pins = {}
    for line in (repo_root / "ci/package/pins.env").read_text().splitlines():
        if line.startswith("F0_") and "=" in line:
            key, _, value = line.partition("=")
            pins[key] = value.strip()

    if pins.get("F0_UPSTREAM_COMMIT") != commit:
        raise msys2db.LockError(
            "ci/package/pins.env F0_UPSTREAM_COMMIT "
            f"({pins.get('F0_UPSTREAM_COMMIT')}) disagrees with "
            f"ci/ffmpeg/components.json ffmpeg.commit ({commit})"
        )
    if pins.get("F0_RUNTIME_REVISION") != revision:
        raise msys2db.LockError(
            "ci/package/pins.env F0_RUNTIME_REVISION "
            f"({pins.get('F0_RUNTIME_REVISION')}) disagrees with "
            f"ci/ffmpeg/components.json buildRevision ({revision})"
        )
    return commit, revision


def workflow_install_set(commit: str) -> tuple:
    """Read upstream's CLANG64 `setup-msys2` install list at the pinned commit."""
    path = ".github/workflows/_meta_win_clang_portable.yaml"
    raw = fetch(f"{UPSTREAM_RAW}/{commit}/{path}")
    text = raw.decode("utf-8")

    block = re.search(r"install:\s*>-\n((?:\s{12}\S.*\n)+)", text)
    if block is None:
        raise msys2db.LockError(f"{path}: no `install:` block found")

    entries = []
    for line in block.group(1).splitlines():
        entry = line.strip()
        if not entry:
            continue
        # The matrix substitutes the toolchain and nasm package names.
        if entry == "${{ matrix.os.toolchain }}":
            entry = f"{MINGW_PACKAGE_PREFIX}-toolchain"
        elif entry == "${{ matrix.os.nasm }}":
            entry = f"{MINGW_PACKAGE_PREFIX}-nasm"
        elif "${{" in entry:
            raise msys2db.LockError(
                f"{path}: unrecognised matrix expression in the install list: {entry}"
            )
        entries.append(entry)
    if not entries:
        raise msys2db.LockError(f"{path}: the `install:` block is empty")
    return sorted(set(entries)), hashlib.sha256(raw).hexdigest(), path


# A PKGBUILD is shell, and upstream uses shell to vary its dependencies:
# `20-mingw-w64-fftw` appends `-gcc-fortran` only when the prefix is NOT a clang
# one. A regular expression cannot evaluate that, and guessing would either add
# a package that does not exist in CLANG64 or silently drop one that does. So
# the arrays are read the way makepkg reads them: bash sources the recipe with
# the CLANG64 environment set and prints the resulting arrays. Sourcing defines
# the recipe's functions without running them, exactly as makepkg's own metadata
# pass does.
# `set -u` is deliberately NOT used: a PKGBUILD legitimately reads variables
# makepkg would have defined, and aborting on the first of them would make this
# read nothing rather than read correctly.
_READ_ARRAYS = r"""
set -e
export MINGW_PACKAGE_PREFIX='%s'
export MINGW_ARCH='clang64'
export MINGW_PREFIX='/clang64'
export MINGW_CHOST='x86_64-w64-mingw32'
export CARCH='x86_64'
export CHOST='x86_64-pc-msys'
# shellcheck disable=SC1090
source "$1" >/dev/null
printf '%%s\n' "${depends[@]-}" "${makedepends[@]-}" "${checkdepends[@]-}"
""" % MINGW_PACKAGE_PREFIX


def recipe_build_tools(commit: str, workdir: Path) -> tuple:
    """Read `depends`/`makedepends`/`checkdepends` from upstream's recipes.

    Packages the recipes build themselves — the `jellyfin-` prefixed ones — are
    produced locally by `makepkg-mingw` and must NOT enter the lock: they do not
    exist in any MSYS2 repository.
    """
    listing = json.loads(fetch(f"{UPSTREAM_API}/msys2/PKGBUILD?ref={commit}").decode())
    names = sorted(item["name"] for item in listing if item["type"] == "dir")
    if not names:
        raise msys2db.LockError("upstream msys2/PKGBUILD contains no recipe directories")

    workdir.mkdir(parents=True, exist_ok=True)
    wanted: set = set()
    digests = {}
    for recipe in names:
        path = f"msys2/PKGBUILD/{recipe}/PKGBUILD"
        raw = fetch(f"{UPSTREAM_RAW}/{commit}/{path}")
        digests[recipe] = hashlib.sha256(raw).hexdigest()

        recipe_file = workdir / f"{recipe}.PKGBUILD"
        recipe_file.write_bytes(raw)
        result = subprocess.run(
            ["bash", "-c", _READ_ARRAYS, "read-arrays", str(recipe_file)],
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode != 0:
            raise msys2db.LockError(
                f"{path}: could not read its dependency arrays: "
                f"{result.stderr.strip() or 'bash exited ' + str(result.returncode)}"
            )
        for value in result.stdout.split():
            # An `optdepends`-style "pkg: description" entry never appears in
            # these three arrays, but a stray one must not become a root.
            if ":" in value:
                continue
            if not value.startswith(MINGW_PACKAGE_PREFIX):
                continue
            if value.startswith(f"{MINGW_PACKAGE_PREFIX}-jellyfin-"):
                continue
            wanted.add(value)
    return sorted(wanted), names, digests


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    commit, revision = read_pins(args.repo_root)

    install_set, workflow_digest, workflow_path = workflow_install_set(commit)
    build_tools, recipes, recipe_digests = recipe_build_tools(
        commit, Path(args.out).parent / "recipes"
    )

    roots = sorted(set(install_set) | set(build_tools) | {"base-devel"})

    databases = {}
    parsed = {}
    for repo, filename in DATABASES.items():
        raw = fetch(MSYS2_BASE[repo] + filename)
        databases[repo] = {
            "filename": filename,
            "url": MSYS2_BASE[repo] + filename,
            "sha256": hashlib.sha256(raw).hexdigest(),
            "bytes": len(raw),
        }
        parsed[repo] = msys2db.parse(raw, repo)

    packages, provides, groups = msys2db.index(parsed["msys"], parsed["clang64"])
    closure = msys2db.resolve(roots, packages, provides, groups)

    entries = []
    for name in closure:
        record = dict(packages[name])
        record["url"] = MSYS2_BASE[record["repository"]] + record["filename"]
        # The signature is admitted separately by ingest.sh, which records
        # whether MSYS2 actually publishes one for this exact filename.
        record["signatureUrl"] = record["url"] + ".sig"
        entries.append(record)

    lock = {
        "schemaVersion": LOCK_SCHEMA_VERSION,
        "target": TARGET,
        "msystem": MSYSTEM,
        "mingwPackagePrefix": MINGW_PACKAGE_PREFIX,
        "ffmpeg": {
            "upstreamCommit": commit,
            "buildRevision": revision,
            "source": "ci/ffmpeg/components.json, asserted equal to ci/package/pins.env",
        },
        "rootProvenance": {
            "workflowInstallSet": {
                "path": workflow_path,
                "sha256": workflow_digest,
                "entries": install_set,
            },
            "recipeBuildTools": {
                "recipeCount": len(recipes),
                "recipes": recipe_digests,
                "entries": build_tools,
            },
            "explicit": {
                "base-devel": (
                    "msys2/build.sh drives every recipe through makepkg-mingw, "
                    "which the `pacman` package ships and base-devel pulls in"
                )
            },
        },
        "roots": roots,
        "repositoryDatabases": databases,
        "packageCount": len(entries),
        "compressedBytes": sum(entry["compressedBytes"] for entry in entries),
        "installedBytes": sum(entry["installedBytes"] for entry in entries),
        "packages": entries,
    }

    args.out.write_text(json.dumps(lock, indent=2, sort_keys=True) + "\n")
    print(
        f"roots {len(roots)} -> {lock['packageCount']} packages, "
        f"{lock['compressedBytes'] / 1048576:.1f} MiB compressed, "
        f"{lock['installedBytes'] / 1048576:.1f} MiB installed",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
