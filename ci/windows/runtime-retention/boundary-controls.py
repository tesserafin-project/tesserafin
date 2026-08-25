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
import os
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
        if target.is_symlink():
            # The LINK is the state, not what it points at. Hashing the target's
            # bytes would report a file replaced by a symlink to an identical
            # file as byte-identical, which is exactly the substitution the
            # symlink controls exist to catch.
            digest.update(b"<symlink>" + os.readlink(target).encode())
        elif target.is_file():
            digest.update(target.read_bytes())
        else:
            digest.update(b"<absent>")
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


def control_h(root: Path) -> None:
    """A file classified with a permitted but WRONG role.

    `consume.ps1` relabelled `accepted-manifest`. Both the role and the file are
    legitimate; the pairing is not. Without a shape and a cardinality rule this
    passes, and "exactly one permitted role" degrades to "one of the nine,
    whichever".
    """
    boundary.INVENTORY["consume.ps1"] = "accepted-manifest"


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


# ── D4: a path's TYPE is not its name ───────────────────────────────────────
#
# R1's inventory asked what a path was called and what it ended in. It never
# asked what it IS, so a symlink wearing a permitted name, a permitted extension
# and a permitted role passed every check while its content came from outside
# the boundary entirely.
def _symlink(root: Path, relative: str, target: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_symlink() or path.exists():
        path.unlink()
    path.symlink_to(target)


def control_i(root: Path) -> None:
    """The exact D4 mutation: a tracked corpus replaced by a link out of the subtree.

    `reference-corpus.json` still has that name, still ends in `.json`, still
    carries the role `tests-and-fixtures` — and its bytes are a build script from
    `ci/windows/ffmpeg/`. Nothing about the role schema can see this.
    """
    _symlink(root, f"{SUBTREE}/reference-corpus.json", "../ffmpeg/pe.py")


def control_j(root: Path) -> None:
    """A NEW symlink wearing a permitted extension and a permitted role.

    Not a replacement of an existing entry — an addition, classified on purpose,
    so the refusal cannot be attributed to the inventory noticing an unknown
    name. It is refused for what it is, not for what it is called.
    """
    _symlink(root, f"{SUBTREE}/fixtures/corpus.json", "../../ffmpeg/pe.py")
    boundary.INVENTORY["fixtures/corpus.json"] = "tests-and-fixtures"


def control_k(root: Path) -> None:
    """A relative symlink whose target is INSIDE the subtree.

    Nothing leaves the boundary here, so the "resolves outside the subtree"
    rule cannot fire and the refusal has to come from the file type alone. A
    check built on resolved paths rather than modes would accept this.
    """
    _symlink(root, f"{SUBTREE}/fixtures/alias.json", "../reference-corpus.json")
    boundary.INVENTORY["fixtures/alias.json"] = "tests-and-fixtures"


def control_l(root: Path) -> None:
    """A DANGLING symlink: `is_file()` answers False, `lstat` answers symlink."""
    _symlink(root, f"{SUBTREE}/fixtures/missing.json", "./nothing-is-here.json")
    boundary.INVENTORY["fixtures/missing.json"] = "tests-and-fixtures"


def control_m(root: Path) -> None:
    """The same substitution, STAGED, so the Git INDEX mode decides it.

    Controls I to L are unstaged, so `lstat` is what refuses them. A staged
    symlink enters the index at mode 120000, and this is the control that proves
    the index-mode branch is not dead code — without it, a repair that dropped
    the index check entirely would still show five green symlink controls.
    """
    _symlink(root, f"{SUBTREE}/reference-corpus.json", "../ffmpeg/pe.py")
    subprocess.run(
        ["git", "-C", str(root), "add", "--", f"{SUBTREE}/reference-corpus.json"],
        check=True, capture_output=True,
    )
    mode = boundary.index_modes(root, SUBTREE).get(f"{SUBTREE}/reference-corpus.json")
    if mode != "120000":
        raise Inert(f"the staged path entered the index at mode {mode!r}, not 120000")


def control_n(root: Path) -> None:
    """A legitimate REGULAR fixture file, classified. It must still be accepted.

    Without this, "every symlink is refused" would be indistinguishable from
    "every added file is refused", and the type rule would be proving nothing
    about types.
    """
    _write(root, f"{SUBTREE}/fixtures/regular.json",
           json.dumps({"kind": "a real file"}, indent=2) + "\n")
    boundary.INVENTORY["fixtures/regular.json"] = "tests-and-fixtures"


#: (name, description, expected property, mutation, properties that must be
#: ABSENT). The fifth element is how the D4 controls state their ORDERING
#: claim: a symlink has to be refused for what it is, and a run that also
#: produced a role finding would mean role classification had gone ahead on a
#: path that is not a file. "It went red" is not the property.
CONTROLS: list[tuple] = [
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
    ("H-misclassified-file",
     "a file wearing a permitted but wrong role is refused",
     "boundary.role-shape-mismatch", control_h),
    ("G-gate-unpinned",
     "removing the gate from the retention workflow is detected",
     "boundary.gate-not-pinned", control_g),
    ("I-tracked-file-replaced-by-symlink",
     "the exact D4 mutation: reference-corpus.json linked to ci/windows/ffmpeg/pe.py",
     "boundary.not-a-regular-file", control_i,
     ("boundary.role-shape-mismatch", "boundary.role-cardinality",
      "boundary.unclassified-content", "boundary.unknown-role",
      "boundary.forbidden-build-role")),
    ("J-symlink-with-permitted-extension-and-role",
     "a classified symlink with a permitted extension is refused on type, not name",
     "boundary.not-a-regular-file", control_j,
     ("boundary.role-shape-mismatch", "boundary.role-cardinality",
      "boundary.unclassified-content", "boundary.unknown-role",
      "boundary.forbidden-build-role")),
    ("K-relative-symlink-inside-subtree",
     "a symlink whose target never leaves the subtree is still refused",
     "boundary.not-a-regular-file", control_k,
     ("boundary.role-shape-mismatch", "boundary.role-cardinality",
      "boundary.unclassified-content", "boundary.unknown-role",
      "boundary.forbidden-build-role")),
    ("L-dangling-symlink",
     "a dangling symlink, which is_file() reports as absent, is refused",
     "boundary.not-a-regular-file", control_l,
     ("boundary.role-shape-mismatch", "boundary.role-cardinality",
      "boundary.unclassified-content", "boundary.unknown-role",
      "boundary.forbidden-build-role")),
    ("M-staged-symlink-index-mode",
     "a STAGED symlink is refused by its Git index mode 120000",
     "boundary.not-a-regular-file", control_m,
     ("boundary.role-shape-mismatch", "boundary.role-cardinality",
      "boundary.unclassified-content", "boundary.unknown-role",
      "boundary.forbidden-build-role")),
    ("N-regular-classified-fixture",
     "a legitimate regular fixture file is still accepted",
     None, control_n),
]


def _restore(root: Path) -> None:
    # `git reset -- .` first: a control that STAGES its mutation leaves the
    # index differing from HEAD, and `git checkout -- .` restores the worktree
    # FROM THE INDEX, so on its own it would reinstate the mutation it was
    # meant to revert. The suite has already refused to run on a dirty tree, so
    # there is nothing else in the index to lose.
    subprocess.run(["git", "-C", str(root), "reset", "-q", "--", "."],
                   check=True, capture_output=True)
    subprocess.run(["git", "-C", str(root), "checkout", "--", "."],
                   check=True, capture_output=True)
    out = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z", "--others", "--exclude-standard"],
        check=True, capture_output=True, text=True,
    )
    for path in (p for p in out.stdout.split("\0") if p):
        target = root / path
        # `is_file()` FOLLOWS the link, so a dangling symlink answers False and
        # would survive the restore into the next control's measurement. The
        # symlink controls make that a live failure mode rather than a
        # hypothetical one.
        if target.is_symlink() or target.is_file():
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
    for control in CONTROLS:
        name, description, expected, mutate = control[:4]
        forbidden = control[4] if len(control) > 4 else ()
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
                leaked = sorted(set(properties) & set(forbidden))
                if leaked:
                    grade, detail = "INERT", (
                        f"named {expected}, but also {leaked}; the mutation reached a "
                        f"check it must be refused before")
                else:
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
