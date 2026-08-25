"""Hostile controls for the excluded-subtree ownership contract (#236, W1-A4-R1).

Each control puts something into the tree that the path-filter exclusion would
otherwise hide from the dual-runner proof, and requires `boundary.check_all` to
refuse it BY NAME. A control that merely produces some finding is INERT: the
question is never "did the gate complain" but "did it complain about this".

Every mutation is reverted and the tree is compared byte for byte afterwards,
tracked and newly-added alike, because a control that leaves a file behind
quietly changes what the NEXT control is measuring.

The suite refuses to run at all on a dirty working tree. It reverts with
`git checkout -- .`, which would destroy uncommitted work, and doing that to
someone's in-flight edit is a far worse outcome than declining to run.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path

import boundary

SUBTREE = boundary.SUBTREE
W1 = boundary.W1_WORKFLOW
RETENTION = boundary.RETENTION_WORKFLOW

CHECKOUT_STEP = """    steps:
      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7.0.0
        with:
          ref: ${{ env.EVIDENCE_SHA }}
          persist-credentials: false
"""


class Inert(Exception):
    """The control could not reach its assertion."""


def _tree_state(root: Path) -> str:
    digest = hashlib.sha256()
    for path in boundary.tracked(root, "."):
        target = root / path
        digest.update(path.encode())
        digest.update(b"\0")
        digest.update(target.read_bytes() if target.is_file() else b"<absent>")
        digest.update(b"\0")
    return digest.hexdigest()


def _write(root: Path, relative: str, body: str) -> None:
    target = root / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(body, encoding="utf-8")


def _patch(root: Path, relative: str, old: str, new: str) -> None:
    target = root / relative
    body = target.read_text(encoding="utf-8")
    if old not in body:
        raise Inert(f"the anchor {old[:40]!r} is not in {relative}")
    target.write_text(body.replace(old, new, 1), encoding="utf-8")


# ── the controls ────────────────────────────────────────────────────────────
def control_a(root: Path) -> None:
    """A build script for the component itself, hidden behind the exclusion."""
    _write(root, f"{SUBTREE}/ffmpeg/build.sh",
           "#!/usr/bin/env bash\nset -eu\n./configure --enable-gpl\nmake -j\n")


def control_b(root: Path) -> None:
    """A toolchain lock — what compiles the binary — behind the exclusion."""
    _write(root, f"{SUBTREE}/msys2-toolchain.lock",
           json.dumps({"mingw-w64-gcc": "14.2.0-1"}, indent=2) + "\n")


def control_c(root: Path) -> None:
    """A W1 build script reads a PERMITTED retention file.

    The file it reads is legitimate; the dependency is not. Once a build script
    sources anything under the subtree, a change there changes the build, and the
    exclusion stops being safe even though every file under it is classified.
    """
    _patch(root, "ci/ffmpeg/lib.sh", "#!/usr/bin/env bash\n",
           "#!/usr/bin/env bash\n. ci/windows/runtime-retention/scan-secrets.sh\n")


def control_d(root: Path) -> None:
    """The W1 workflow copies the subtree into its build context."""
    _patch(root, W1, CHECKOUT_STEP, CHECKOUT_STEP +
           "\n      - name: Seed the build context\n        shell: bash\n"
           '        run: cp -r ci/windows/runtime-retention "${RUNNER_TEMP}/context"\n')


def control_e(root: Path) -> None:
    """A broad recursive glob in the build workflow that can traverse the subtree."""
    _patch(root, W1, CHECKOUT_STEP, CHECKOUT_STEP +
           "\n      - name: Collect the build inputs\n        shell: bash\n"
           "        run: tar -cf inputs.tar ci/windows/**\n")


def control_f(root: Path) -> None:
    """A LEGITIMATE retention fixture, classified. The boundary must accept it."""
    _write(root, f"{SUBTREE}/fixtures/tag-corpus.json",
           json.dumps({"tags": ["accepted-000000000000"]}, indent=2) + "\n")
    boundary.INVENTORY["fixtures/tag-corpus.json"] = "tests-and-fixtures"


def control_g(root: Path) -> None:
    """The gate is unpinned from the retention workflow. Its absence must be seen."""
    body = (root / RETENTION).read_text(encoding="utf-8")
    stripped = "\n".join(
        line for line in body.splitlines()
        if "boundary-controls.py" not in line and "boundary.py" not in line
    ) + "\n"
    if stripped == body:
        raise Inert("the retention workflow does not pin the gate, so it cannot be unpinned")
    (root / RETENTION).write_text(stripped, encoding="utf-8")


CONTROLS: list[tuple[str, str, str | None, object]] = [
    ("A-build-script-under-subtree",
     "a component build script behind the exclusion is refused as a build role",
     "boundary.forbidden-build-role", control_a),
    ("B-toolchain-lock-under-subtree",
     "a toolchain lock behind the exclusion is refused as a build role",
     "boundary.forbidden-build-role", control_b),
    ("C-build-script-sources-retention-file",
     "a W1 build script reading from the subtree is a cross-boundary dependency",
     "boundary.cross-boundary-dependency", control_c),
    ("D-workflow-copies-subtree",
     "the W1 workflow copying the subtree into its build context is refused",
     "boundary.build-closure-names-subtree", control_d),
    ("E-broad-recursive-glob",
     "a broad ci/windows glob that can traverse the subtree is refused",
     "boundary.broad-glob-can-traverse-subtree", control_e),
    ("F-classified-retention-fixture",
     "a legitimate, classified retention fixture is accepted",
     None, control_f),
    ("G-gate-unpinned",
     "removing the gate from the retention workflow is detected",
     "boundary.gate-not-pinned", control_g),
]


def _restore(root: Path) -> None:
    subprocess.run(["git", "-C", str(root), "checkout", "--", "."],
                   check=True, capture_output=True)
    out = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z", "--others", "--exclude-standard"],
        check=True, capture_output=True, text=True,
    )
    for path in (p for p in out.stdout.split("\0") if p):
        target = root / path
        if target.is_file():
            target.unlink()
    for directory in sorted((root / SUBTREE).rglob("*"), reverse=True):
        if directory.is_dir() and not any(directory.iterdir()):
            directory.rmdir()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    root = boundary.repo_root()
    dirty = subprocess.run(["git", "-C", str(root), "status", "--porcelain"],
                           check=True, capture_output=True, text=True).stdout
    if dirty.strip():
        print("W1-A4 BOUNDARY CONTROLS HARD STOP: the working tree is not clean, and this "
              "suite reverts with `git checkout -- .`, which would destroy it:", file=sys.stderr)
        print(dirty, file=sys.stderr)
        return 2

    baseline = _tree_state(root)
    pristine = boundary.check_all(root)
    if pristine:
        print("W1-A4 BOUNDARY CONTROLS HARD STOP: the pristine tree already has findings, "
              "so no control can prove anything:", file=sys.stderr)
        for finding in pristine:
            print(f"  {finding.prop}: {finding.message}", file=sys.stderr)
        return 2

    failures = 0
    results = []
    print("excluded-subtree ownership controls")
    for name, description, expected, mutate in CONTROLS:
        inventory_before = dict(boundary.INVENTORY)
        grade, detail, properties = "ERROR", "", []
        try:
            mutate(root)
            findings = boundary.check_all(root)
            properties = sorted({f.prop for f in findings})
            if expected is None:
                grade, detail = (("PASS", "accepted, as it must be") if not findings
                                 else ("GREEN", f"refused a legitimate fixture: {properties}"))
            elif not findings:
                grade, detail = "GREEN", "ACCEPTED what must be refused"
            elif expected in properties:
                message = next(f.message for f in findings if f.prop == expected)
                grade, detail = "RED", f"named {expected}: {message[:100]}"
            else:
                grade, detail = "INERT", f"refused, but not for {expected}; got {properties}"
        except Inert as error:
            grade, detail = "ERROR", f"the mutation never reached the gate: {error}"
        except Exception as error:  # noqa: BLE001
            grade, detail = "ERROR", f"{type(error).__name__}: {error}"
        finally:
            boundary.INVENTORY.clear()
            boundary.INVENTORY.update(inventory_before)
            _restore(root)

        state = _tree_state(root)
        if state != baseline:
            grade = "ERROR"
            detail = f"the tree was not restored ({state[:12]} != {baseline[:12]})"
        if grade not in ("RED", "PASS"):
            failures += 1
        print(f"  {grade:<6} {name:<40} {detail}")
        results.append({"control": name, "property": description, "expected": expected,
                        "grade": grade, "detail": detail, "found": properties})

    print(f"\ntree before : {baseline}")
    print(f"tree after  : {_tree_state(root)}")

    if args.json:
        args.json.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    print()
    if failures:
        print(f"W1-A4 BOUNDARY CONTROLS HARD STOP: {failures} control(s) did not reach "
              f"their property", file=sys.stderr)
        return 1
    print(f"all {len(CONTROLS)} excluded-subtree controls reached their named property")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
