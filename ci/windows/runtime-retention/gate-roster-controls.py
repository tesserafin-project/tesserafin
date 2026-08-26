"""D3 replayed twice: exact command identity, and roster authority from outside.

R1's finding D3 was that a gate's invocation could be commented out while the
check that proved it was invoked went on passing, because that check searched
the workflow's raw TEXT for a filename and a comment contains one.

R2 replaced the substring test with a parser plus a list of no-op shapes. R3's
review found that list is a list of the bypasses someone thought of, and that
the roster still named its own required members. Both are closed here, and both
are closed by something that lives OUTSIDE this subtree:

  * `ci/windows/verify-retention-gate-pinned.py::check_command` requires the
    canonical command to be the `run` scalar of a step named by id, BYTE FOR
    BYTE. Twenty-two controls, including every wrapper that R2 accepted.
  * `ci/windows/verify-retention-gate-pinned.py::check_roster` freezes the
    roster by id, module, callable, kind, argv, tier and position, and resolves
    this subtree's REAL bindings. Thirteen controls plus the reviewer's exact
    three-deletion mutation.

MEASURED, AGAINST THE R2 FILE. Eleven syntactically harmless wrappers were
accepted by R2 as a live invocation and are refused here: `#cmd`, `##cmd`,
`cmd || :`, `cmd || echo x`, `cmd &`, `if false; then cmd; fi`, `(cmd)`, a
block scalar containing the command, a padded scalar, `working-directory:` and
a `shell:` override. They are controls C01, C03, C04, C05, C06, C08, C09, C12,
C13, C18 and C19 below, marked `[R2-BYPASS]`. Nothing decides them by running a
shell, and no rule depends on spacing after `#`.

The command controls mutate a COPY of the workflow in a temporary tree. The
roster controls mutate a COPY of this whole subtree, load the copied
orchestrator and hand the real module object to the external contract, so a
mutation that silently did not apply grades ERROR rather than RED. Nothing
under review is written to.

Two ablations close the argument about where authority stops. A1 disables the
orchestrator's own self-check and requires the external contract to refuse
anyway. A2 removes the external verifier and requires `ci/run.sh`'s permanent
block to fail. `ci/run.sh` is the trust root and nothing pins it in turn: a
chain of scripts each pinning the next has no last link.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import re
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
RUNLINE = f"        run: {COMMAND}\n"
STEPHDR = ("      - name: The complete retention gate roster\n"
           "        id: retention-gate-roster\n"
           "        shell: bash\n")
JOBHDR = "  gates:\n    name: Retention gates\n"


def _pin_module():
    spec = importlib.util.spec_from_file_location("verify_retention_gate_pinned", PIN)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# ── command identity: the twenty required controls, and two more ────────────
def _run(new: str):
    return lambda source: source.replace(RUNLINE, new, 1)


def _hdr(new: str):
    return lambda source: source.replace(STEPHDR, new, 1)


def _comment_out_step(source: str) -> str:
    return source.replace(
        STEPHDR + RUNLINE,
        "".join(f"      # {line.strip()}\n" for line in (STEPHDR + RUNLINE).splitlines()),
        1)


def _remove_job(source: str) -> str:
    lines = source.splitlines(keepends=True)
    start = next(i for i, line in enumerate(lines) if line == "  gates:\n")
    end = next(i for i in range(start + 1, len(lines))
               if lines[i].startswith("  ") and lines[i][2:3] not in (" ", "\n", "#")
               and lines[i].rstrip().endswith(":"))
    return "".join(lines[:start] + lines[end:])


def _move_to_another_job(source: str) -> str:
    without = _remove_job(source)
    return without.replace(
        "      - name: Build the fixture\n",
        f"      - name: Retention gates, somewhere else\n"
        f"        id: retention-gate-roster\n        shell: bash\n"
        f"        run: {COMMAND}\n\n      - name: Build the fixture\n", 1)


COMMAND_CONTROLS: list[tuple[str, str, str | None, object]] = [
    ("C01-hash-cmd", "[R2-BYPASS] `#cmd`, no space after the hash",
     "cmd.run-not-canonical", _run(f"        run: '#{COMMAND}'\n")),
    ("C02-hash-space-cmd", "`# cmd`, the exact D3 mutation",
     "cmd.run-not-canonical", _run(f"        run: '# {COMMAND}'\n")),
    ("C03-double-hash-cmd", "[R2-BYPASS] `##cmd`",
     "cmd.run-not-canonical", _run(f"        run: '##{COMMAND}'\n")),
    ("C04-or-colon", "[R2-BYPASS] `cmd || :`",
     "cmd.run-not-canonical", _run(f"        run: '{COMMAND} || :'\n")),
    ("C05-or-echo", "[R2-BYPASS] `cmd || echo x`",
     "cmd.run-not-canonical", _run(f"        run: {COMMAND} || echo x\n")),
    ("C06-background", "[R2-BYPASS] `cmd &`, whose exit status is the shell's",
     "cmd.run-not-canonical", _run(f"        run: {COMMAND} &\n")),
    ("C07-set-plus-e", "`set +e; cmd; exit 0`",
     "cmd.run-not-canonical", _run(f"        run: 'set +e; {COMMAND}; exit 0'\n")),
    ("C08-if-false", "[R2-BYPASS] `if false; then cmd; fi`",
     "cmd.run-not-canonical", _run(f"        run: 'if false; then {COMMAND}; fi'\n")),
    ("C09-subshell", "[R2-BYPASS] `(cmd)`",
     "cmd.run-not-canonical", _run(f"        run: ({COMMAND})\n")),
    ("C10-true-and", "`true && cmd`",
     "cmd.run-not-canonical", _run(f"        run: true && {COMMAND}\n")),
    ("C11-echo", "`echo cmd`",
     "cmd.run-not-canonical", _run(f"        run: echo {COMMAND}\n")),
    ("C12-block-scalar", "[R2-BYPASS] a block scalar containing the command",
     "cmd.run-not-canonical",
     _run(f"        run: |\n          set -euo pipefail\n          {COMMAND}\n")),
    ("C13-padded-scalar", "[R2-BYPASS] leading and trailing whitespace",
     "cmd.run-not-canonical", _run(f'        run: "  {COMMAND}  "\n')),
    ("C14-step-commented-out", "the whole step commented out",
     "cmd.step-missing", _comment_out_step),
    ("C15-step-false-condition", "a step condition that never holds",
     "cmd.step-conditional",
     _hdr("      - name: The complete retention gate roster\n"
          "        id: retention-gate-roster\n        if: false\n        shell: bash\n")),
    ("C16-job-false-condition", "a job condition that never holds",
     "cmd.job-conditional",
     lambda source: source.replace(JOBHDR, JOBHDR + "    if: false\n", 1)),
    ("C17-continue-on-error", "the step tolerating its own failure",
     "cmd.step-continue-on-error",
     _hdr("      - name: The complete retention gate roster\n"
          "        id: retention-gate-roster\n        continue-on-error: true\n"
          "        shell: bash\n")),
    ("C18-working-directory", "[R2-BYPASS] the command resolved against another directory",
     "cmd.step-working-directory",
     _hdr("      - name: The complete retention gate roster\n"
          "        id: retention-gate-roster\n        working-directory: ci\n"
          "        shell: bash\n")),
    ("C19-shell-override", "[R2-BYPASS] `shell: sh`, which does not fail fast",
     "cmd.step-shell-override",
     _hdr("      - name: The complete retention gate roster\n"
          "        id: retention-gate-roster\n        shell: sh\n")),
    ("C20-pristine", "the pristine workflow is accepted", None, lambda source: source),
    ("C21-job-removed", "the canonical job removed altogether",
     "cmd.job-missing", _remove_job),
    ("C22-step-moved-to-another-job", "the command present, but not in the named job",
     "cmd.job-missing", _move_to_another_job),
    ("C23-step-id-duplicated",
     "the pristine step first, a second step claiming the same id after it",
     "cmd.step-duplicated",
     lambda source: source.replace(
         STEPHDR + RUNLINE,
         STEPHDR + RUNLINE + "\n      - name: Also the gate, apparently\n"
         "        id: retention-gate-roster\n        shell: bash\n"
         "        run: 'true'\n", 1)),
    ("C24-step-env-replaces-the-interpreter",
     "a step `env:` that can change which python3 runs, under an unchanged command",
     "cmd.step-env",
     _hdr("      - name: The complete retention gate roster\n"
          "        id: retention-gate-roster\n        env:\n"
          "          PATH: /tmp/nowhere\n        shell: bash\n")),
]


# ── roster authority: source mutations of a copied subtree ──────────────────
def _entry_span(lines: list[str], gate_id: str) -> tuple[int, int]:
    """The whole `Gate(...)` call, found by balancing its parentheses.

    `Gate("x", "f.py", "main", "exit-code", (),` closes a pair on its first
    line and continues on the next, so "the first line ending in `),`" is not
    the end of the entry.
    """
    start = next(i for i, line in enumerate(lines)
                 if line.startswith(f'    Gate("{gate_id}"'))
    depth = 0
    for index in range(start, len(lines)):
        depth += lines[index].count("(") - lines[index].count(")")
        if depth == 0:
            return start, index + 1
    raise AssertionError(f"the roster entry {gate_id!r} is not closed")


def _drop_entry(source: str, gate_id: str) -> str:
    lines = source.splitlines(keepends=True)
    start, end = _entry_span(lines, gate_id)
    return "".join(lines[:start] + lines[end:])


def _replace_entry(source: str, gate_id: str, replacement: str) -> str:
    lines = source.splitlines(keepends=True)
    start, end = _entry_span(lines, gate_id)
    return "".join(lines[:start] + [replacement] + lines[end:])


def _entry_text(source: str, gate_id: str) -> str:
    lines = source.splitlines(keepends=True)
    start, end = _entry_span(lines, gate_id)
    return "".join(lines[start:end])


def _tuple_close(lines: list[str], name: str) -> int:
    start = next(i for i, line in enumerate(lines) if line.startswith(f"{name}: tuple["))
    return next(i for i in range(start + 1, len(lines)) if lines[i] == ")\n")


def _append_to(source: str, name: str, text: str) -> str:
    lines = source.splitlines(keepends=True)
    close = _tuple_close(lines, name)
    return "".join(lines[:close] + [text] + lines[close:])


def _drop_diagnostic(source: str, gate_id: str) -> str:
    return "".join(line for line in source.splitlines(keepends=True)
                   if not line.startswith(f'    "{gate_id}":'))


OWNERSHIP = "excluded-subtree-ownership"


def m_delete_ownership_gate(source: str, _boundary: str) -> tuple[str, str]:
    return _drop_entry(source, OWNERSHIP), _boundary


def m_delete_ownership_proof(source: str, _boundary: str) -> tuple[str, str]:
    return _drop_entry(source, "ownership-self-proof"), _boundary


def m_delete_roster_proof(source: str, _boundary: str) -> tuple[str, str]:
    return _drop_entry(source, "gate-roster-self-proof"), _boundary


def m_delete_from_both(source: str, _boundary: str) -> tuple[str, str]:
    return _drop_diagnostic(_drop_entry(source, OWNERSHIP), OWNERSHIP), _boundary


def m_lambda_callable(source: str, boundary_src: str) -> tuple[str, str]:
    """The roster is untouched; `boundary.check_all` is rebound to a lambda."""
    return source, boundary_src + "\n\ncheck_all = lambda root: []  # substituted\n"


def m_other_permitted_callable(source: str, _boundary: str) -> tuple[str, str]:
    return _replace_entry(
        source, OWNERSHIP,
        f'    Gate("{OWNERSHIP}", "boundary.py", "check_inventory", "findings", (),\n'
        f'         "another callable this module really has"),\n'), _boundary


def m_duplicate(source: str, _boundary: str) -> tuple[str, str]:
    return _append_to(source, "GATES", _entry_text(source, OWNERSHIP)), _boundary


def m_unknown(source: str, _boundary: str) -> tuple[str, str]:
    return _append_to(
        source, "GATES",
        '    Gate("convenient-extra", "boundary.py", "check_inventory", "findings", (),\n'
        '         "an entry nobody required"),\n'), _boundary


def m_gate_becomes_proof(source: str, _boundary: str) -> tuple[str, str]:
    text = _entry_text(source, OWNERSHIP)
    return _append_to(_drop_entry(source, OWNERSHIP), "PROOFS", text), _boundary


def m_same_name_other_module(source: str, _boundary: str) -> tuple[str, str]:
    return _replace_entry(
        source, OWNERSHIP,
        f'    Gate("{OWNERSHIP}", "contract.py", "check_all", "findings", (),\n'
        f'         "the same callable name in another file"),\n'), _boundary


def m_reorder(source: str, _boundary: str) -> tuple[str, str]:
    text = _entry_text(source, OWNERSHIP)
    return _append_to(_drop_entry(source, OWNERSHIP), "GATES", text), _boundary


def m_reviewer_three_deletions(source: str, _boundary: str) -> tuple[str, str]:
    for gate_id in (OWNERSHIP, "ownership-self-proof", "gate-roster-self-proof"):
        source = _drop_diagnostic(_drop_entry(source, gate_id), gate_id)
    return source, _boundary


def m_pristine(source: str, boundary_src: str) -> tuple[str, str]:
    return source, boundary_src


def m_ablate_self_check(source: str, _boundary: str) -> tuple[str, str]:
    """A1: the orchestrator's own self-check neutered, and a member deleted."""
    source = _drop_entry(source, OWNERSHIP)
    return re.sub(r"(?m)^def validate_roster\(\) -> None:\n",
                  "def validate_roster() -> None:\n    return None\n", source, count=1), _boundary


ROSTER_CONTROLS: list[tuple[str, str, tuple[str, ...], object]] = [
    ("N01-delete-ownership-gate", "the ownership gate deleted from the actual roster",
     ("roster.missing",), m_delete_ownership_gate),
    ("N02-delete-ownership-self-proof", "its self-proof deleted",
     ("roster.missing",), m_delete_ownership_proof),
    ("N03-delete-gate-roster-self-proof", "the gate-roster self-proof deleted",
     ("roster.missing",), m_delete_roster_proof),
    ("N04-delete-from-roster-and-diagnostic",
     "deleted from GATES and from the orchestrator's diagnostic copy together",
     ("roster.missing",), m_delete_from_both),
    ("N05-expected-callable-is-a-lambda",
     "the declared identity kept, the callable rebound to a lambda",
     ("roster.callable-substituted",), m_lambda_callable),
    ("N06-id-bound-to-another-callable",
     "the expected id bound to another callable the module really has",
     ("roster.callable",), m_other_permitted_callable),
    ("N07-duplicate-gate", "the same member named twice",
     ("roster.duplicate",), m_duplicate),
    ("N08-unknown-gate", "a member the external contract does not name",
     ("roster.unknown",), m_unknown),
    ("N09-gate-moved-into-proofs", "a gate substituted by a self-proof collection member",
     ("roster.tier",), m_gate_becomes_proof),
    ("N10-same-name-other-module", "the names preserved, the module changed",
     ("roster.module",), m_same_name_other_module),
    ("N11-reordered", "the same members in a different order",
     ("roster.order",), m_reorder),
    ("N12-pristine", "the pristine roster is accepted", (), m_pristine),
    ("NX-reviewer-three-deletions",
     "the reviewer's exact mutation: ownership gate, its self-proof and the gate-roster "
     "self-proof deleted from the roster and from the diagnostic copy",
     ("roster.missing",), m_reviewer_three_deletions),
    ("A1-external-authority-survives-ablated-self-check",
     "the orchestrator's own validate_roster() neutered and a member deleted",
     ("roster.missing",), m_ablate_self_check),
]


def _copy_subtree(source: str, boundary_src: str) -> Path:
    work = Path(tempfile.mkdtemp(prefix="w1a4r3-roster-"))
    target = work / "ci" / "windows" / "runtime-retention"
    shutil.copytree(HERE, target, symlinks=True)
    shutil.copy2(PIN, work / "ci" / "windows" / PIN.name)
    (target / "retention_gates.py").write_text(source, encoding="utf-8")
    (target / "boundary.py").write_text(boundary_src, encoding="utf-8")
    return work


def _grade_roster(pin, name, expected, mutate, pristine_src, pristine_boundary):
    work = None
    try:
        source, boundary_src = mutate(pristine_src, pristine_boundary)
        if not expected and (source, boundary_src) != (pristine_src, pristine_boundary):
            return "ERROR", "the pristine control mutated something", []
        if expected and (source, boundary_src) == (pristine_src, pristine_boundary):
            return "ERROR", "the mutation did not apply", []
        work = _copy_subtree(source, boundary_src)
        orchestrator = pin.load_orchestrator(
            work / "ci" / "windows" / "runtime-retention" / "retention_gates.py")
        findings = pin.check_roster(orchestrator)
        properties = sorted({f.prop for f in findings})
        if not expected:
            if findings:
                return "GREEN", f"refused the pristine roster: {properties}", properties
            return "PASS", "accepted, as it must be", properties
        if not findings:
            return "GREEN", "ACCEPTED what must be refused", properties
        absent = [p for p in expected if p not in properties]
        if absent:
            return "INERT", f"refused, but not for {list(expected)}; got {properties}", \
                properties
        return "RED", f"refused, naming {list(expected)}", properties
    except Exception as error:  # noqa: BLE001 - an ERROR grade is the point
        return "ERROR", f"{type(error).__name__}: {error}", []
    finally:
        if work is not None:
            shutil.rmtree(work, ignore_errors=True)


# ── the contract is two-sided ───────────────────────────────────────────────
def _external_roster_narrowed(pin) -> tuple[str, str]:
    """N14: the EXTERNAL expectation edited, the orchestrator left pristine.

    Every other roster control mutates the subtree and requires the external
    contract to refuse. This is the other direction: the external file is the
    authority, so an expectation that no longer matches a pristine orchestrator
    must also refuse — otherwise "authority" would only mean "veto", and a
    quiet edit HERE would be the way to drop a gate.
    """
    saved_roster = pin.EXPECTED_ROSTER
    saved_by_id = pin.EXPECTED_BY_ID
    try:
        pin.EXPECTED_ROSTER = tuple(e for e in saved_roster if e.gate_id != OWNERSHIP)
        pin.EXPECTED_BY_ID = {e.gate_id: e for e in pin.EXPECTED_ROSTER}
        orchestrator = pin.load_orchestrator(HERE / "retention_gates.py")
        findings = pin.check_roster(orchestrator)
        properties = sorted({f.prop for f in findings})
        if not findings:
            return "GREEN", "ACCEPTED an external expectation that no longer matches"
        if "roster.unknown" in properties:
            return "RED", f"refused, naming {properties}"
        return "INERT", f"refused, but not for roster.unknown; got {properties}"
    except Exception as error:  # noqa: BLE001
        return "ERROR", f"{type(error).__name__}: {error}"
    finally:
        pin.EXPECTED_ROSTER = saved_roster
        pin.EXPECTED_BY_ID = saved_by_id


# ── A2: ci/run.sh is the trust root ─────────────────────────────────────────
PIN_BLOCK_START = "# >>> W1A4-PIN-BLOCK"
PIN_BLOCK_END = "# <<< W1A4-PIN-BLOCK"


def _ci_run_block(root: Path) -> str:
    text = (root / "ci" / "run.sh").read_text(encoding="utf-8")
    start = text.index(PIN_BLOCK_START) + len(PIN_BLOCK_START)
    return text[start:text.index(PIN_BLOCK_END)]


def _exercise_ci_run_block(root: Path, block: str, verifier_present: bool) -> int:
    """Run ci/run.sh's permanent pin block alone and report its STATUS."""
    work = Path(tempfile.mkdtemp(prefix="w1a4r3-trustroot-"))
    try:
        if verifier_present:
            target = root
        else:
            target = work / "root"
            (target / "ci" / "windows").mkdir(parents=True)
            target = target
        script = work / "block.sh"
        script.write_text(
            "set -uo pipefail\nSTATUS=0\nbanner() { :; }\n"
            f"REPO_ROOT={target}\n{block}\nexit $STATUS\n", encoding="utf-8")
        return subprocess.run(["bash", str(script)], capture_output=True, text=True).returncode
    finally:
        shutil.rmtree(work, ignore_errors=True)


# ── the no-op substitution only the self-proof tier can see ─────────────────
def _no_op_tier_control(root: Path) -> tuple[str, str]:
    real = boundary.check_all
    loaded = retention_gates._load("boundary.py")
    loaded_real = loaded.check_all
    work = Path(tempfile.mkdtemp(prefix="w1a4r3-noop-"))
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
        gate = next(g for g in retention_gates.GATES if g.gate_id == OWNERSHIP)
        proof = next(p for p in retention_gates.PROOFS if p.gate_id == "ownership-self-proof")
        gate_passed, _ = retention_gates._run(gate, root, fixture)
        proof_passed, proof_detail = retention_gates._run(proof, root, fixture)
        if gate_passed and not proof_passed and proof_detail.strip() == "exit 1":
            return "RED", ("the no-op gate passed, as a no-op must, and its self-proof "
                           "refused it: every hostile control graded GREEN")
        if gate_passed and not proof_passed:
            return "INERT", (f"the self-proof failed with {proof_detail.strip()!r}, not with "
                             f"the control-grading exit 1; commit the working tree and re-run")
        if not gate_passed:
            return "INERT", "the no-op gate itself failed; that is not the claim"
        return "GREEN", "ACCEPTED a roster whose ownership gate does nothing"
    except Exception as error:  # noqa: BLE001
        return "ERROR", f"{type(error).__name__}: {error}"
    finally:
        boundary.check_all = real
        loaded.check_all = loaded_real
        shutil.rmtree(work, ignore_errors=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    root = boundary.repo_root()
    workflow = root / boundary.RETENTION_WORKFLOW
    before = workflow.read_bytes()
    pristine = workflow.read_text(encoding="utf-8")
    orchestrator_before = (HERE / "retention_gates.py").read_bytes()
    pristine_src = orchestrator_before.decode("utf-8")
    pristine_boundary = (HERE / "boundary.py").read_text(encoding="utf-8")
    pin = _pin_module()
    results = []
    failures = 0

    print("command identity — the canonical scalar, decided by `==` and no shell")
    for name, description, expected, mutate in COMMAND_CONTROLS:
        work = Path(tempfile.mkdtemp(prefix="w1a4r3-cmd-"))
        grade, detail, properties = "ERROR", "", []
        try:
            target = work / boundary.RETENTION_WORKFLOW
            target.parent.mkdir(parents=True, exist_ok=True)
            body = mutate(pristine)
            if expected is not None and body == pristine:
                raise AssertionError("the mutation did not apply")
            target.write_text(body, encoding="utf-8")
            findings = pin.check_command(work)
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

    print("\nroster authority — the expected set lives outside this subtree")
    for name, description, expected, mutate in ROSTER_CONTROLS:
        grade, detail, properties = _grade_roster(
            pin, name, expected, mutate, pristine_src, pristine_boundary)
        if grade not in ("RED", "PASS"):
            failures += 1
        print(f"  {grade:<6} {name:<40} {detail}")
        results.append({"control": name, "property": description,
                        "expected": list(expected), "grade": grade, "detail": detail,
                        "found": properties})

    print("\nthe contract is two-sided")
    grade, detail = _external_roster_narrowed(pin)
    if grade != "RED":
        failures += 1
    print(f"  {grade:<6} {'N14-external-expectation-narrowed':<40} {detail}")
    results.append({"control": "N14-external-expectation-narrowed", "grade": grade,
                    "detail": detail})

    print("\nthe no-op substitution, which only the self-proof tier can see")
    grade, detail = _no_op_tier_control(root)
    if grade != "RED":
        failures += 1
    print(f"  {grade:<6} {'N13-no-op-gate-fails-its-self-proof':<40} {detail}")
    results.append({"control": "N13-no-op-gate-fails-its-self-proof", "grade": grade,
                    "detail": detail})

    print("\nA2 — ci/run.sh is the trust root")
    grade, detail = "ERROR", ""
    try:
        block = _ci_run_block(root)
        with_verifier = _exercise_ci_run_block(root, block, True)
        without = _exercise_ci_run_block(root, block, False)
        if without != 0 and with_verifier == 0:
            grade, detail = "RED", (f"the block exits {without} with the external verifier "
                                    f"removed and 0 with it present; ci/run.sh fails when "
                                    f"the authority is disabled")
        elif with_verifier != 0:
            grade, detail = "INERT", (f"the block exits {with_verifier} on the real tree; "
                                      f"that is not the claim")
        else:
            grade, detail = "GREEN", "ci/run.sh tolerated the verifier being removed"
    except Exception as error:  # noqa: BLE001
        grade, detail = "ERROR", f"{type(error).__name__}: {error}"
    if grade != "RED":
        failures += 1
    print(f"  {grade:<6} {'A2-ci-run-sh-is-the-trust-root':<40} {detail}")
    results.append({"control": "A2-ci-run-sh-is-the-trust-root", "grade": grade,
                    "detail": detail})

    print()
    if workflow.read_bytes() != before:
        print("  FAIL   tree-restored     the workflow changed on disk")
        failures += 1
    elif (HERE / "retention_gates.py").read_bytes() != orchestrator_before:
        print("  FAIL   tree-restored     retention_gates.py changed on disk")
        failures += 1
    else:
        print("  OK     tree-restored     the reviewed tree is byte-identical on disk")

    if args.json:
        args.json.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    print()
    if failures:
        print(f"W1-A4 GATE ROSTER HARD STOP: {failures} control(s) did not reach their "
              f"property", file=sys.stderr)
        return 1
    print(f"{len(COMMAND_CONTROLS)} command-identity controls and {len(ROSTER_CONTROLS)} "
          f"roster-authority controls each reached their own named property, plus the "
          f"self-proof tier control and the ci/run.sh trust-root ablation")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
