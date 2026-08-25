"""D3 replayed: the roster and its pin, both proved able to refuse (#236, W1-A4-R2).

R1's blocking finding D3 was that a gate's invocation could be commented out
while the check that proved it was invoked went on passing, because that check
searched the workflow's raw TEXT for a filename and a comment contains one.

Two mechanisms replaced it, and each is only worth something if it can be shown
failing:

  * `retention_gates.py` holds a closed roster resolved by exact module and
    function identity. Deleting an entry, naming one twice, or naming one the
    required set does not know must each refuse.
  * `ci/windows/verify-retention-gate-pinned.py`, which lives OUTSIDE this
    subtree and which `ci/run.sh` runs, parses the workflow and requires the
    canonical command to be a live step.

The pin controls mutate a COPY of the workflow in a temporary tree and point the
checker at it, so nothing under review is written to and there is no window in
which the tree on disk is not the tree being reviewed. The roster controls
mutate the in-memory roster tuple and restore it.

The exact D3 replay the review asked for is controls P1 to P4 and R1 to R2:
delete the boundary roster entry, comment out the workflow invocation, remove
the canonical job, and require the pristine workflow to pass.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import boundary
import retention_gates

HERE = Path(__file__).resolve().parent
PIN = HERE.parent / "verify-retention-gate-pinned.py"
COMMAND = "python3 ci/windows/runtime-retention/retention_gates.py --validate"


def _pin_module():
    spec = importlib.util.spec_from_file_location("verify_retention_gate_pinned", PIN)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# ── controls on the closed roster ───────────────────────────────────────────
def roster_delete_boundary(gates, proofs):
    """The exact D3 replay: the ownership gate deleted from the roster."""
    return (tuple(g for g in gates if g.gate_id != "excluded-subtree-ownership"), proofs)


def roster_delete_self_proof(gates, proofs):
    """Its self-proof deleted too, so neither tier can cover for the other."""
    return (gates, tuple(p for p in proofs if p.gate_id != "ownership-self-proof"))


def roster_duplicate_entry(gates, proofs):
    """The same identity twice. A gate invoked twice is not two gates."""
    return (gates + (gates[3],), proofs)


def roster_unknown_entry(gates, proofs):
    """An id the required set does not name; it could then be removed unnoticed."""
    extra = retention_gates.Gate(
        "convenient-extra", "boundary.py", "check_inventory", "findings", (),
        "an entry nobody required")
    return (gates + (extra,), proofs)


def roster_unknown_identity(gates, proofs):
    """A roster entry naming a function that does not exist."""
    broken = retention_gates.Gate(
        "excluded-subtree-ownership", "boundary.py", "check_everything_honest", "findings",
        (), "a function this module does not have")
    return (tuple(g for g in gates if g.gate_id != "excluded-subtree-ownership") + (broken,),
            proofs)


def roster_repointed_identity(gates, proofs):
    """The ownership entry keeping its id while its FUNCTION changes.

    A no-op has a perfectly good identity of its own, so resolving the roster
    cannot refuse this on its own; the required identity map is what does.
    """
    module = retention_gates._load("boundary.py")
    module.check_all_no_op = lambda root: []
    replaced = retention_gates.Gate(
        "excluded-subtree-ownership", "boundary.py", "check_all_no_op", "findings", (),
        "a no-op standing in for the ownership gate")
    return (tuple(g for g in gates if g.gate_id != "excluded-subtree-ownership") + (replaced,),
            proofs)


ROSTER_CONTROLS: list[tuple[str, str, str, object]] = [
    ("R1-delete-boundary-roster-entry",
     "deleting the ownership gate from the roster is refused",
     "required gate", roster_delete_boundary),
    ("R2-delete-ownership-self-proof",
     "deleting its self-proof is refused too",
     "required gate", roster_delete_self_proof),
    ("R3-duplicate-roster-entry",
     "the same module::function::argv named twice is refused",
     "twice", roster_duplicate_entry),
    ("R4-unknown-roster-entry",
     "a roster entry the required set does not name is refused",
     "the required set does not name", roster_unknown_entry),
    ("R5-roster-names-a-missing-function",
     "a roster entry naming a function that does not exist is refused",
     "not a callable", roster_unknown_identity),
    ("R7-roster-entry-repointed-to-a-no-op",
     "an entry keeping its id while its function changes is refused by the required identity",
     "a gate replaced", roster_repointed_identity),
]


# ── controls on the structural pin ──────────────────────────────────────────
def pin_comment_out(source: str) -> str:
    return source.replace(f"run: {COMMAND}\n", f"run: '# {COMMAND}'\n", 1)


def pin_colon_no_op(source: str) -> str:
    return source.replace(f"run: {COMMAND}\n", f"run: ': {COMMAND}'\n", 1)


def pin_echo_no_op(source: str) -> str:
    return source.replace(f"run: {COMMAND}\n", f"run: echo {COMMAND}\n", 1)


def pin_success_mask(source: str) -> str:
    return source.replace(f"run: {COMMAND}\n", f"run: {COMMAND} || true\n", 1)


def pin_semicolon_true(source: str) -> str:
    return source.replace(f"run: {COMMAND}\n", f"run: {COMMAND} ; true\n", 1)


def pin_continue_on_error(source: str) -> str:
    return source.replace(
        f"      - name: The complete retention gate roster\n        shell: bash\n",
        f"      - name: The complete retention gate roster\n"
        f"        continue-on-error: true\n        shell: bash\n", 1)


def pin_unreachable_step(source: str) -> str:
    return source.replace(
        f"      - name: The complete retention gate roster\n        shell: bash\n",
        f"      - name: The complete retention gate roster\n"
        f"        if: false\n        shell: bash\n", 1)


def pin_unreachable_job(source: str) -> str:
    return source.replace(
        "  gates:\n    name: Retention gates\n",
        "  gates:\n    name: Retention gates\n    if: false\n", 1)


def pin_remove_job(source: str) -> str:
    lines = source.splitlines(keepends=True)
    start = next(i for i, line in enumerate(lines) if line == "  gates:\n")
    end = next(i for i in range(start + 1, len(lines))
               if lines[i].startswith("  ") and lines[i][2:3] not in (" ", "\n", "#")
               and lines[i].rstrip().endswith(":"))
    return "".join(lines[:start] + lines[end:])


def pin_moved_to_another_job(source: str) -> str:
    """The command still present, but in a job the pin does not name.

    A check that searched the whole file for the command would accept this;
    naming the job is what makes it a pin rather than a sighting.
    """
    without = pin_remove_job(source)
    return without.replace(
        "      - name: Build the fixture\n",
        f"      - name: Retention gates, somewhere else\n        shell: bash\n"
        f"        run: {COMMAND}\n\n      - name: Build the fixture\n", 1)


def pin_pristine(source: str) -> str:
    return source


PIN_CONTROLS: list[tuple[str, str, str | None, object]] = [
    ("P1-invocation-commented-out",
     "the exact D3 mutation: the canonical command commented out",
     "pin.command-is-a-no-op", pin_comment_out),
    ("P2-invocation-colon-no-op",
     "the command prefixed by `:`",
     "pin.command-is-a-no-op", pin_colon_no_op),
    ("P3-invocation-echoed",
     "the command prefixed by `echo`",
     "pin.command-is-a-no-op", pin_echo_no_op),
    ("P4-success-masked-or-true",
     "the command suffixed by `|| true`",
     "pin.command-success-masked", pin_success_mask),
    ("P5-success-masked-semicolon-true",
     "the command suffixed by `; true`",
     "pin.command-success-masked", pin_semicolon_true),
    ("P6-step-continue-on-error",
     "the step tolerating its own failure",
     "pin.step-continue-on-error", pin_continue_on_error),
    ("P7-step-unreachable",
     "the step carrying a condition that never holds",
     "pin.step-unreachable", pin_unreachable_step),
    ("P8-job-unreachable",
     "the job carrying a condition that never holds",
     "pin.job-unreachable", pin_unreachable_job),
    ("P9-canonical-job-removed",
     "the canonical job removed altogether",
     "pin.job-missing", pin_remove_job),
    ("P10-command-moved-to-another-job",
     "the command present, but not in the job the pin names",
     "pin.job-missing", pin_moved_to_another_job),
    ("P11-pristine-workflow",
     "the pristine workflow passes",
     None, pin_pristine),
]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    root = boundary.repo_root()
    workflow = root / boundary.RETENTION_WORKFLOW
    before = workflow.read_bytes()
    pristine = workflow.read_text(encoding="utf-8")
    pin = _pin_module()
    results = []
    failures = 0

    print("closed-roster controls")
    saved_gates, saved_proofs = retention_gates.GATES, retention_gates.PROOFS
    for name, description, token, mutate in ROSTER_CONTROLS:
        grade, detail = "ERROR", ""
        try:
            retention_gates.GATES, retention_gates.PROOFS = mutate(saved_gates, saved_proofs)
            try:
                retention_gates.validate_roster()
                grade, detail = "GREEN", "ACCEPTED a roster that cannot describe itself"
            except retention_gates.RosterError as error:
                if token in str(error):
                    grade, detail = "RED", f"refused, naming {token!r}"
                else:
                    grade, detail = "INERT", f"refused, but not for {token!r}: {error}"
        except Exception as error:  # noqa: BLE001 - an ERROR grade is the point
            grade, detail = "ERROR", f"{type(error).__name__}: {error}"
        finally:
            retention_gates.GATES, retention_gates.PROOFS = saved_gates, saved_proofs
        if grade != "RED":
            failures += 1
        print(f"  {grade:<6} {name:<40} {detail}")
        results.append({"control": name, "property": description, "grade": grade,
                        "detail": detail})

    # The realistic no-op: `boundary.check_all` itself rewritten to return
    # nothing. The roster still resolves exactly the identity the required map
    # demands, so nothing structural can see it — the gate simply stops
    # refusing. Only the self-proof tier can, because every hostile control then
    # grades GREEN, "ACCEPTED what must be refused".
    #
    # Both copies of the module are patched. `boundary-controls.py` reaches
    # boundary through a plain `import`, while the roster resolves it through
    # importlib and holds a separate object; patching one and not the other
    # would make the gate fail on its own and prove nothing about the proof.
    print("\nthe no-op substitution, which only the self-proof tier can see")
    grade, detail = "ERROR", ""
    real = boundary.check_all
    loaded = retention_gates._load("boundary.py")
    loaded_real = loaded.check_all
    work = Path(tempfile.mkdtemp(prefix="w1a4r2-noop-"))
    try:
        fixture = work / "fixture"
        make_fixture = retention_gates._load("make-fixture.py")
        saved_argv = sys.argv
        sys.argv = ["make-fixture.py", "--out", str(fixture)]
        try:
            make_fixture.main()
        finally:
            sys.argv = saved_argv

        boundary.check_all = lambda root: []
        loaded.check_all = lambda root: []
        gate = next(g for g in retention_gates.GATES
                    if g.gate_id == "excluded-subtree-ownership")
        proof = next(p for p in retention_gates.PROOFS
                     if p.gate_id == "ownership-self-proof")
        gate_passed, _ = retention_gates._run(gate, root, fixture)
        proof_passed, proof_detail = retention_gates._run(proof, root, fixture)
        # Exit 1 is "controls did not reach their property", which is what a
        # no-op gate produces. Exit 2 is the suite's dirty-tree precondition,
        # and grading that as a pass would mean this control proved nothing on
        # any machine with an uncommitted edit.
        if gate_passed and not proof_passed and proof_detail.strip() == "exit 1":
            grade, detail = "RED", ("the no-op gate passed, as a no-op must, and its "
                                    "self-proof refused it: every hostile control graded "
                                    "GREEN")
        elif gate_passed and not proof_passed:
            grade, detail = "INERT", (
                f"the self-proof failed with {proof_detail.strip()!r}, not with the "
                f"control-grading exit 1; commit the working tree and re-run")
        elif not gate_passed:
            grade, detail = "INERT", "the no-op gate itself failed; that is not the claim"
        else:
            grade, detail = "GREEN", "ACCEPTED a roster whose ownership gate does nothing"
    except Exception as error:  # noqa: BLE001
        grade, detail = "ERROR", f"{type(error).__name__}: {error}"
    finally:
        boundary.check_all = real
        loaded.check_all = loaded_real
        shutil.rmtree(work, ignore_errors=True)
    if grade != "RED":
        failures += 1
    print(f"  {grade:<6} {'R6-no-op-gate-fails-its-self-proof':<40} {detail}")
    results.append({"control": "R6-no-op-gate-fails-its-self-proof", "grade": grade,
                    "detail": detail})

    print("\nstructural-pin controls")
    for name, description, expected, mutate in PIN_CONTROLS:
        work = Path(tempfile.mkdtemp(prefix=f"w1a4r2-pin-{name}-"))
        grade, detail, properties = "ERROR", "", []
        try:
            target = work / boundary.RETENTION_WORKFLOW
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(mutate(pristine), encoding="utf-8")
            findings = pin.check(work)
            properties = sorted({f.prop for f in findings})
            if expected is None:
                grade, detail = (("PASS", "accepted, as it must be") if not findings
                                 else ("GREEN", f"refused the pristine workflow: {properties}"))
            elif not findings:
                grade, detail = "GREEN", "ACCEPTED what must be refused"
            elif expected in properties:
                grade, detail = "RED", f"refused, naming {expected}"
            else:
                grade, detail = "INERT", f"refused, but not for {expected}; got {properties}"
        except Exception as error:  # noqa: BLE001
            grade, detail = "ERROR", f"{type(error).__name__}: {error}"
        finally:
            shutil.rmtree(work, ignore_errors=True)
        if grade not in ("RED", "PASS"):
            failures += 1
        print(f"  {grade:<6} {name:<40} {detail}")
        results.append({"control": name, "property": description, "expected": expected,
                        "grade": grade, "detail": detail, "found": properties})

    after = workflow.read_bytes()
    if after != before:
        print("  FAIL   tree-restored                             the workflow changed on disk")
        failures += 1
    else:
        print("  OK     tree-restored                             "
              "the reviewed workflow is byte-identical on disk")

    if args.json:
        args.json.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    print()
    if failures:
        print(f"W1-A4 GATE ROSTER HARD STOP: {failures} control(s) did not reach their "
              f"property", file=sys.stderr)
        return 1
    print(f"the closed roster and its structural pin each refuse every way of making them "
          f"inert: {len(ROSTER_CONTROLS) + 1} roster controls and {len(PIN_CONTROLS)} pin "
          f"controls")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
