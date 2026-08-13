"""Require the build prefix to hold EXACTLY the locked packages (#236, W1-R-B §2.3).

"Everything the lock names is installed" is the weaker half of the property that
matters. The half that carries the provenance guarantee is the other one:
NOTHING ELSE is installed. An undeclared package in the build prefix is a tool
that can influence the FFmpeg build — a `sed`, a `perl`, a stray compiler on
PATH — while appearing in no lock, no bundle and no published provenance. A
future runner image that quietly gains a package must therefore FAIL this gate,
even when the package looks harmless, because "looks harmless" is not a property
this repository can check and "is in the reviewed lock" is.

Pre-existing packages are not an exception to that rule. They are acceptable
only when they are members of the lock AT THE LOCKED VERSION — which, after
`install-locked.ps1` has run `pacman -U` over every locked archive, is a
statement about what the transaction actually achieved rather than a courtesy
extended to the base image.

The decision lives here, in Python, rather than in the PowerShell that collects
the observation, for one reason: it can then be exercised on an ordinary Linux
runner against synthetic observations, including the failures that a hosted
Windows job cannot be asked to stage on demand — a package at the wrong version,
an unexpected architecture, an extra package appearing after installation.
`install-locked.ps1` and `consume.ps1` collect `name|version|architecture` from
`pacman -Qi` and hand it here; the observation is data, the ruling is code.

Usage:
    python3 installed-set.py --lock <lock.json> --observed <listing> [--json <out>]
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Dict, List, Tuple


class InstalledSetError(Exception):
    """Fail-closed condition. Never caught to continue."""


def parse_observation(text: str) -> Dict[str, Tuple[str, str]]:
    """Parse `name|version|architecture` lines into {name: (version, arch)}."""
    observed: Dict[str, Tuple[str, str]] = {}
    for number, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if not line:
            continue
        fields = line.split("|")
        if len(fields) != 3 or not all(field.strip() for field in fields):
            raise InstalledSetError(
                f"line {number} of the observation is not `name|version|architecture`: "
                f"{raw!r}. A listing this gate cannot parse is not a listing it may "
                "declare equal to the lock."
            )
        name, version, architecture = (field.strip() for field in fields)
        if name in observed:
            raise InstalledSetError(
                f"{name} is listed twice in the observation ({observed[name][0]} and "
                f"{version}); the prefix cannot hold one package at two versions"
            )
        observed[name] = (version, architecture)
    if not observed:
        raise InstalledSetError(
            "the observation is empty. An empty listing would make set equality "
            "vacuous for an empty lock and is never a real query result."
        )
    return observed


def compare(lock: dict, observed: Dict[str, Tuple[str, str]]) -> dict:
    """Return a summary, or raise on the first way the sets differ."""
    locked: Dict[str, Tuple[str, str]] = {}
    for package in lock["packages"]:
        name = package["name"]
        if name in locked:
            raise InstalledSetError(f"the lock declares {name} twice")
        locked[name] = (package["version"], package["architecture"])

    if len(locked) != lock["packageCount"]:
        raise InstalledSetError(
            f"the lock lists {len(locked)} packages and declares "
            f"packageCount {lock['packageCount']}"
        )

    missing = sorted(set(locked) - set(observed))
    if missing:
        raise InstalledSetError(
            f"{len(missing)} locked package(s) are not installed: {', '.join(missing)}. "
            "The transaction did not achieve the reviewed toolchain."
        )

    undeclared = sorted(set(observed) - set(locked))
    if undeclared:
        raise InstalledSetError(
            f"{len(undeclared)} package(s) are installed that the lock does not name: "
            f"{', '.join(undeclared)}. An undeclared package can influence the build "
            "without appearing in provenance; being harmless is not a property this "
            "gate can verify, and being reviewed is."
        )

    wrong_version: List[str] = []
    wrong_architecture: List[str] = []
    for name, (version, architecture) in sorted(observed.items()):
        locked_version, locked_architecture = locked[name]
        if version != locked_version:
            wrong_version.append(f"{name} {version} (locked {locked_version})")
        if architecture != locked_architecture:
            wrong_architecture.append(
                f"{name} {architecture} (locked {locked_architecture})"
            )
    if wrong_version:
        raise InstalledSetError(
            f"{len(wrong_version)} package(s) are installed at a version the lock does "
            f"not name: {', '.join(wrong_version)}. A locked NAME at an unlocked "
            "VERSION is different bytes and a different toolchain."
        )
    if wrong_architecture:
        raise InstalledSetError(
            f"{len(wrong_architecture)} package(s) are installed for an unexpected "
            f"architecture: {', '.join(wrong_architecture)}"
        )

    architectures: Dict[str, int] = {}
    for _, architecture in locked.values():
        architectures[architecture] = architectures.get(architecture, 0) + 1

    return {
        "probe": "w1r-installed-set",
        "equal": True,
        "lockedPackages": len(locked),
        "installedPackages": len(observed),
        "architectures": dict(sorted(architectures.items())),
        "undeclaredPackages": 0,
        "missingPackages": 0,
        "versionMismatches": 0,
        "architectureMismatches": 0,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", required=True, type=Path)
    parser.add_argument(
        "--observed",
        required=True,
        type=Path,
        help="`name|version|architecture` lines, one per installed package",
    )
    parser.add_argument("--json", type=Path, help="write the summary here as well")
    args = parser.parse_args()

    lock = json.loads(args.lock.read_text(encoding="utf-8"))
    observed = parse_observation(args.observed.read_text(encoding="utf-8"))
    summary = compare(lock, observed)
    text = json.dumps(summary, indent=2, sort_keys=True)
    if args.json:
        args.json.write_text(text + "\n", encoding="utf-8")
    print(text)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except InstalledSetError as error:
        print(f"W1-R INSTALLED SET HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
