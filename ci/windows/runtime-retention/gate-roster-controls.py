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
COMMAND = "/usr/bin/python3 ci/windows/runtime-retention/retention_gates.py --validate"
RUNLINE = f"        run: {COMMAND}\n"

#: The gate step's neutralising environment, written exactly as the workflow
#: writes it. R3 finding F1: this block is the RUNTIME half of the repair, and
#: the structural half refuses the step without it — so a control that rebuilds
#: the step header has to carry it, or it is measuring two things at once.
STEP_ENV_YAML = (
    "        env:\n"
    "          BASH_ENV: /dev/null\n"
    "          ENV: /dev/null\n"
    "          PYTHONSTARTUP: /dev/null\n"
    "          PYTHONPATH: ''\n"
    "          PYTHONHOME: ''\n"
    "          PYTHONEXECUTABLE: ''\n"
    "          PYTHONSAFEPATH: ''\n"
    "          PYTHONNOUSERSITE: ''\n"
    "          PYTHONWARNINGS: ''\n"
    "          LD_PRELOAD: ''\n"
    "          LD_LIBRARY_PATH: ''\n"
    "          VIRTUAL_ENV: ''\n"
    "          CONDA_PREFIX: ''\n"
)
STEPNAME = ("      - name: The complete retention gate roster\n"
            "        id: retention-gate-roster\n")
STEPHDR = STEPNAME + "        shell: bash\n" + STEP_ENV_YAML
JOBHDR = "  gates:\n    name: Retention gates\n"
WORKFLOW_ENV_ANCHOR = "env:\n  # On `pull_request`"


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
        "      - name: Retention gates, somewhere else\n"
        "        id: retention-gate-roster\n        shell: bash\n"
        + STEP_ENV_YAML + RUNLINE + "\n      - name: Build the fixture\n", 1)


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
     _hdr(STEPNAME + "        if: false\n        shell: bash\n" + STEP_ENV_YAML)),
    ("C16-job-false-condition", "a job condition that never holds",
     "cmd.job-conditional",
     lambda source: source.replace(JOBHDR, JOBHDR + "    if: false\n", 1)),
    ("C17-continue-on-error", "the step tolerating its own failure",
     "cmd.step-continue-on-error",
     _hdr(STEPNAME + "        continue-on-error: true\n        shell: bash\n"
          + STEP_ENV_YAML)),
    ("C18-working-directory", "[R2-BYPASS] the command resolved against another directory",
     "cmd.step-working-directory",
     _hdr(STEPNAME + "        working-directory: ci\n        shell: bash\n"
          + STEP_ENV_YAML)),
    ("C19-shell-override", "[R2-BYPASS] `shell: sh`, which does not fail fast",
     "cmd.step-shell-override",
     _hdr(STEPNAME + "        shell: sh\n" + STEP_ENV_YAML)),
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
         + STEP_ENV_YAML + "        run: 'true'\n", 1)),
    ("C24-step-env-replaces-the-interpreter",
     "a step `env:` that can change which python3 runs, under an unchanged command",
     "cmd.step-env-extra",
     _hdr(STEPNAME + "        shell: bash\n"
          + STEP_ENV_YAML + "          PATH: /tmp/nowhere\n")),
]


# ── the execution root: every scope that can change what actually runs ──────
#
# R3 findings F1 and F2. Until R4 the only scope this contract read was the
# gate step, so a wider one decided the same things unobserved:
#
#   * `env: {BASH_ENV: mal.sh}` at workflow, job or step scope made bash source
#     mal.sh BEFORE the step's first line. `python3() { return 0; }` defined
#     there answered to the command's name, the validator never executed, and
#     the step succeeded.
#   * `defaults: {run: {shell: sh}}` at workflow or job scope changed the shell
#     the command ran under while the step stayed byte-identical, and `sh` is
#     not expanded with `-eo pipefail`.
#   * `defaults: {run: {working-directory: ci}}` likewise moved the directory
#     the repository-relative path resolves against.
#
# Each control below reaches exactly one of those, and each names the property
# it must be refused for. B1 further replays the reviewer's experiment as an
# execution rather than as a parse.
def _insert_workflow_env(pairs: str):
    return lambda source: source.replace("env:\n  # On `pull_request`",
                                         f"env:\n{pairs}  # On `pull_request`", 1)


def _insert_workflow_defaults(block: str):
    return lambda source: source.replace("jobs:\n", f"{block}jobs:\n", 1)


def _insert_job_block(block: str):
    return lambda source: source.replace(JOBHDR, JOBHDR + block, 1)


def _sibling_step_env(source: str) -> str:
    return source.replace(
        "      - name: Assert the evidence SHA\n        shell: bash\n",
        "      - name: Assert the evidence SHA\n        shell: bash\n"
        "        env:\n          BASH_ENV: /tmp/w1a4-mal.sh\n", 1)


SCOPE_CONTROLS: list[tuple[str, str, str | None, object]] = [
    ("E01-workflow-env-bash-env",
     "the reviewer's exact bypass, declared at workflow scope",
     "cmd.workflow-env", _insert_workflow_env("  BASH_ENV: /tmp/w1a4-mal.sh\n")),
    ("E02-job-env-bash-env", "the same bypass at job scope",
     "cmd.job-env", _insert_job_block("    env:\n      BASH_ENV: /tmp/w1a4-mal.sh\n")),
    ("E03-step-env-bash-env-repointed",
     "the neutralising entry kept, its value repointed at a startup file",
     "cmd.step-env-not-neutral",
     lambda source: source.replace("          BASH_ENV: /dev/null\n",
                                   "          BASH_ENV: /tmp/w1a4-mal.sh\n", 1)),
    ("E04-step-env-bash-env-removed", "the neutralising entry simply deleted",
     "cmd.step-env-incomplete",
     lambda source: source.replace("          BASH_ENV: /dev/null\n", "", 1)),
    ("E05-step-env-absent", "the whole neutralising block deleted",
     "cmd.step-env-absent", _hdr(STEPNAME + "        shell: bash\n")),
    ("E06-workflow-env-ENV", "`ENV`, which a POSIX shell sources the same way",
     "cmd.workflow-env", _insert_workflow_env("  ENV: /tmp/w1a4-mal.sh\n")),
    ("E07-workflow-env-PATH", "`PATH`, which decides which python3 a bare name is",
     "cmd.workflow-env", _insert_workflow_env("  PATH: /tmp/w1a4-fake\n")),
    ("E08-job-env-PYTHONPATH", "`PYTHONPATH`, which decides which `boundary` is imported",
     "cmd.job-env", _insert_job_block("    env:\n      PYTHONPATH: /tmp/w1a4-fake\n")),
    ("E09-workflow-env-PYTHONSTARTUP",
     "`PYTHONSTARTUP`, a python startup file rather than a shell one",
     "cmd.workflow-env", _insert_workflow_env("  PYTHONSTARTUP: /tmp/w1a4-mal.py\n")),
    ("E10-step-env-extra-name", "a name beside the closed neutralising set",
     "cmd.step-env-extra",
     lambda source: source.replace(
         "          CONDA_PREFIX: ''\n",
         "          CONDA_PREFIX: ''\n          PYTHONOPTIMIZE: '2'\n", 1)),
    ("E11-workflow-defaults-shell", "`defaults.run.shell` at workflow scope",
     "cmd.workflow-defaults-shell",
     _insert_workflow_defaults("defaults:\n  run:\n    shell: sh\n\n")),
    ("E12-job-defaults-shell", "`defaults.run.shell` at job scope",
     "cmd.job-defaults-shell",
     _insert_job_block("    defaults:\n      run:\n        shell: sh\n")),
    ("E13-workflow-defaults-working-directory",
     "`defaults.run.working-directory` at workflow scope",
     "cmd.workflow-defaults-working-directory",
     _insert_workflow_defaults("defaults:\n  run:\n    working-directory: ci\n\n")),
    ("E14-job-defaults-working-directory",
     "`defaults.run.working-directory` at job scope",
     "cmd.job-defaults-working-directory",
     _insert_job_block("    defaults:\n      run:\n        working-directory: ci\n")),
    ("E15-step-shell-absent",
     "the step declaring no shell, so `defaults.run.shell` would decide it",
     "cmd.step-shell-absent",
     _hdr(STEPNAME + STEP_ENV_YAML)),
    ("E16-relative-interpreter",
     "the command with a bare `python3`, which PATH and a shell function both answer",
     "cmd.run-not-canonical",
     _run("        run: python3 ci/windows/runtime-retention/retention_gates.py "
          "--validate\n")),
    ("E17-duplicate-run-key",
     "a second `run:` in the gate step — last-wins locally, rejected by GitHub",
     "cmd.workflow-unparseable",
     lambda source: source.replace(RUNLINE, RUNLINE + "        run: 'true'\n", 1)),
    ("E18-sibling-step-env",
     "the bypass declared on an earlier step of the same job",
     "cmd.sibling-step-env", _sibling_step_env),
    ("E19-trigger-drops-the-anchor",
     "the canonical manifest removed from the paths that start this job",
     "cmd.trigger-paths-narrowed",
     lambda source: source.replace(
         "      - 'ci/windows/w1a4-roster-manifest.v1.json'\n", "", 1)),
    ("E20-trigger-drops-the-trust-root",
     "ci/run.sh removed from the paths that start this job",
     "cmd.trigger-paths-narrowed",
     lambda source: source.replace("      - 'ci/run.sh'\n", "", 1)),
]

COMMAND_CONTROLS.extend(SCOPE_CONTROLS)

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


# ── the pin-block markers, still named here for the reachability controls ───
#
# R3's A2 control lived here: it EXTRACTED the block between these markers and
# executed the extracted text against a tree with the verifier removed. That
# proves the block works and says nothing about whether ci/run.sh reaches it —
# the reviewer's mutation, the intact block moved below `exit "$STATUS"`, passed
# it unchanged. It is replaced by the RB structural controls and the D dynamic
# runs below, which execute the real script.
PIN_BLOCK_START = "# >>> W1A4-PIN-BLOCK"
PIN_BLOCK_END = "# <<< W1A4-PIN-BLOCK"


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


# ════════════════════════════════════════════════════════════════════════════
# THE INDEPENDENT ANCHOR (R3 finding F3)
# ════════════════════════════════════════════════════════════════════════════
#
# The R3 attack is bilateral: the orchestrator declares the roster, the
# verifier froze the roster, and one edit to each removed a member and the
# requirement for it together. Every check then agreed with every other check.
#
# There is now a third party, and it is not a fourth self-verifier:
#
#   ci/windows/w1a4-roster-manifest.v1.json   the canonical roster, a data file
#   ci/run.sh W1A4_ROSTER_MANIFEST_SHA256     its content digest
#   ci/run.sh W1A4_ROSTER_IDS                 its fourteen identities, in order
#
# The verifier CONSUMES the manifest and READS both constants out of ci/run.sh.
# It writes down no roster of its own, so there is nothing in it to edit.
# Removing three members now takes an edit to the orchestrator, an edit to the
# manifest, AND an edit to the trust root — and the trust root is the merge
# gate for every branch. Where that stops is stated, not hidden: control
# BX-three-file-edit below is the boundary itself, measured rather than
# claimed.
MANIFEST_REL = "ci/windows/w1a4-roster-manifest.v1.json"
VERIFIER_REL = "ci/windows/verify-retention-gate-pinned.py"
TRUST_ROOT_REL = "ci/run.sh"
SUBTREE_REL = "ci/windows/runtime-retention"
THREE = (OWNERSHIP, "ownership-self-proof", "gate-roster-self-proof")


def _anchor_tree(root: Path) -> Path:
    """A temp root carrying the four files the anchor contract spans."""
    work = Path(tempfile.mkdtemp(prefix="w1a4r4-anchor-"))
    (work / "ci" / "windows").mkdir(parents=True)
    shutil.copy2(root / TRUST_ROOT_REL, work / TRUST_ROOT_REL)
    shutil.copy2(root / MANIFEST_REL, work / MANIFEST_REL)
    shutil.copy2(root / VERIFIER_REL, work / VERIFIER_REL)
    shutil.copytree(root / SUBTREE_REL, work / SUBTREE_REL,
                    ignore=shutil.ignore_patterns("__pycache__"))
    return work


def _manifest_rows(work: Path) -> dict:
    return json.loads((work / MANIFEST_REL).read_text(encoding="utf-8"))


def _write_manifest(work: Path, doc: dict) -> None:
    (work / MANIFEST_REL).write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")


def _repin_digest(work: Path) -> None:
    """Update ci/run.sh's pinned digest to whatever the manifest now hashes to."""
    import hashlib
    digest = hashlib.sha256((work / MANIFEST_REL).read_bytes()).hexdigest()
    text = (work / TRUST_ROOT_REL).read_text(encoding="utf-8")
    text = re.sub(r'(?m)^W1A4_ROSTER_MANIFEST_SHA256="[0-9a-f]{64}"$',
                  f'W1A4_ROSTER_MANIFEST_SHA256="{digest}"', text)
    (work / TRUST_ROOT_REL).write_text(text, encoding="utf-8")


def _drop_ids_from_trust_root(work: Path, ids: tuple[str, ...]) -> None:
    text = (work / TRUST_ROOT_REL).read_text(encoding="utf-8")
    for gate_id in ids:
        text = text.replace(f"    {gate_id}\n", "", 1)
    (work / TRUST_ROOT_REL).write_text(text, encoding="utf-8")


def _drop_from_orchestrator(work: Path, ids: tuple[str, ...]) -> None:
    path = work / SUBTREE_REL / "retention_gates.py"
    source = path.read_text(encoding="utf-8")
    for gate_id in ids:
        source = _drop_diagnostic(_drop_entry(source, gate_id), gate_id)
    path.write_text(source, encoding="utf-8")


def _drop_from_manifest(work: Path, ids: tuple[str, ...]) -> None:
    doc = _manifest_rows(work)
    doc["roster"] = [r for r in doc["roster"] if r["id"] not in ids]
    for position, row in enumerate(doc["roster"]):
        row["position"] = position
    _write_manifest(work, doc)


# ── the anchor mutations ────────────────────────────────────────────────────
def x_bilateral_deletion(work: Path) -> str:
    """R3's exact attack: the orchestrator and the external expectation, together."""
    _drop_from_orchestrator(work, THREE)
    _drop_from_manifest(work, THREE)
    return "three members removed from retention_gates.py and from the manifest"


def x_bilateral_deletion_reordered(work: Path) -> str:
    _drop_from_orchestrator(work, THREE)
    _drop_from_manifest(work, THREE)
    doc = _manifest_rows(work)
    doc["roster"] = list(reversed(doc["roster"]))
    for position, row in enumerate(doc["roster"]):
        row["position"] = position
    _write_manifest(work, doc)
    return "the same deletion, with the eleven survivors reordered"


def x_bilateral_alias(work: Path) -> str:
    """Both sides renamed onto a no-op callable that really exists."""
    path = work / SUBTREE_REL / "boundary.py"
    path.write_text(path.read_text(encoding="utf-8")
                    + "\n\ndef check_nothing(root):\n    return []\n", encoding="utf-8")
    orchestrator = work / SUBTREE_REL / "retention_gates.py"
    source = _replace_entry(
        orchestrator.read_text(encoding="utf-8"), OWNERSHIP,
        f'    Gate("{OWNERSHIP}", "boundary.py", "check_nothing", "findings", (),\n'
        f'         "a no-op that really exists"),\n')
    orchestrator.write_text(source, encoding="utf-8")
    doc = _manifest_rows(work)
    for row in doc["roster"]:
        if row["id"] == OWNERSHIP:
            row["callable"] = "check_nothing"
    _write_manifest(work, doc)
    return "both sides repointed at a no-op callable"


def x_bilateral_attributes(work: Path) -> str:
    """Matching kind/argv/tier changes on both sides."""
    orchestrator = work / SUBTREE_REL / "retention_gates.py"
    source = orchestrator.read_text(encoding="utf-8")
    text = _entry_text(source, OWNERSHIP)
    source = _append_to(_drop_entry(source, OWNERSHIP), "PROOFS", text)
    orchestrator.write_text(source, encoding="utf-8")
    doc = _manifest_rows(work)
    rows = [r for r in doc["roster"] if r["id"] != OWNERSHIP]
    moved = next(r for r in doc["roster"] if r["id"] == OWNERSHIP)
    moved["tier"] = "proof"
    rows.append(moved)
    for position, row in enumerate(rows):
        row["position"] = position
    doc["roster"] = rows
    _write_manifest(work, doc)
    return "the ownership gate moved into the proof tier on both sides"


def x_manifest_only(work: Path) -> str:
    _drop_from_manifest(work, THREE)
    return "the manifest edited, the pinned digest left alone"


def x_digest_unpinned(work: Path) -> str:
    text = (work / TRUST_ROOT_REL).read_text(encoding="utf-8")
    text = re.sub(r'(?m)^W1A4_ROSTER_MANIFEST_SHA256="[0-9a-f]{64}"\n', "", text)
    (work / TRUST_ROOT_REL).write_text(text, encoding="utf-8")
    return "the pinned digest removed from the trust root"


def x_ids_unpinned(work: Path) -> str:
    text = (work / TRUST_ROOT_REL).read_text(encoding="utf-8")
    text = re.sub(r"(?ms)^W1A4_ROSTER_IDS=\(\n.*?^\)\n", "", text)
    (work / TRUST_ROOT_REL).write_text(text, encoding="utf-8")
    return "the pinned identity list removed from the trust root"


def x_ids_weakened(work: Path) -> str:
    _drop_ids_from_trust_root(work, THREE)
    return "three identities quietly dropped from the trust root's list"


def x_manifest_deleted(work: Path) -> str:
    (work / MANIFEST_REL).unlink()
    return "the canonical manifest deleted outright"


def x_manifest_version_bumped(work: Path) -> str:
    doc = _manifest_rows(work)
    doc["version"] = 2
    _write_manifest(work, doc)
    _repin_digest(work)
    return "the schema version raised past what the verifier understands, digest repinned"


def x_pristine(work: Path) -> str:
    return "unmutated"


ANCHOR_CONTROLS: list[tuple[str, str, tuple[str, ...], object]] = [
    ("X01-bilateral-deletion",
     "R3's exact two-file attack: excluded-subtree-ownership, its self-proof and "
     "gate-roster-self-proof removed from retention_gates.py AND from the external "
     "expected roster",
     ("anchor.manifest-digest", "anchor.manifest-roster-drift"), x_bilateral_deletion),
    ("X02-bilateral-deletion-reordered", "the same, with the survivors reordered",
     ("anchor.manifest-digest", "anchor.manifest-roster-drift"),
     x_bilateral_deletion_reordered),
    ("X03-bilateral-alias", "both sides repointed at a matching no-op callable",
     ("anchor.manifest-digest",), x_bilateral_alias),
    ("X04-bilateral-attribute-substitution",
     "matching tier/position changes on both sides",
     ("anchor.manifest-digest",), x_bilateral_attributes),
    ("X05-manifest-changed-without-the-authority",
     "the canonical manifest edited, the independently pinned digest untouched",
     ("anchor.manifest-digest", "anchor.manifest-roster-drift"), x_manifest_only),
    ("X06-independent-authority-removed", "the pinned digest deleted from ci/run.sh",
     ("anchor.digest-unpinned",), x_digest_unpinned),
    ("X07-identity-list-removed", "the pinned identity list deleted from ci/run.sh",
     ("anchor.ids-unpinned",), x_ids_unpinned),
    ("X08-independent-authority-weakened",
     "three identities dropped from the trust root's list while the manifest keeps them",
     ("anchor.manifest-roster-drift",), x_ids_weakened),
    ("X09-manifest-deleted", "the canonical manifest deleted",
     ("anchor.manifest-missing",), x_manifest_deleted),
    ("X10-manifest-version-bumped",
     "a schema version this contract does not understand, correctly repinned",
     ("anchor.manifest-schema",), x_manifest_version_bumped),
    ("X11-pristine", "the pristine anchor is accepted", (), x_pristine),
]


# ── tier P: the obligations are OWNED, not declared (W1-A5-V1-R2 B2/B3) ─────
#
# Everything in tier X establishes that the roster runs the callables the
# manifest names. R2 showed that is not the same claim as those callables still
# doing anything:
#
#   B2. Delete `+ check_summary_identity(root)` from `publication_policy.
#       check_all` — one line. Every identity check passes, the orchestrator
#       runs 14/14, and the subtree's own S1-S3 controls stay RED, because they
#       called the implementation directly rather than through the roster's
#       callable.
#
#   B3. Co-remove the call, the implementation, the controls and their
#       invocation. Nothing refuses at all: the property was declared only by
#       the file that implemented it.
#
# The repair is a third constant beside the roster digest and the member ids —
# `W1A4_ROSTER_PROPERTIES` in the trust root — plus witness violations in the
# external verifier, which CALL the pinned callable and require it to name the
# property. These controls prove that mechanism refuses R2's two attacks, that
# it refuses a manifest that quietly drops an obligation, and that it refuses a
# gate which satisfies the witness by reporting the property unconditionally.
#
# The last control is the boundary itself, measured rather than claimed: an
# edit to the manifest AND to ci/run.sh together does remove the obligation.
# That is the same two-key boundary tier X already states for the roster, and
# ci/run.sh is the merge gate for every branch.
PUBLISH_REL = ".github/workflows/w1-windows-runtime-publish.yml"
POLICY_REL = "publication_policy.py"

_CALL_BOTH = """    return (evaluate(root, boundary.RETENTION_WORKFLOW)
            + check_summary_identity(root)
            + check_workflow_identity(root))"""


def _property_tree(root: Path) -> Path:
    """An anchor tree that also carries the two workflows the witnesses need."""
    work = _anchor_tree(root)
    (work / ".github" / "workflows").mkdir(parents=True)
    for relative in (
        ".github/workflows/w1-windows-runtime-retention.yml",
        PUBLISH_REL,
    ):
        shutil.copy2(root / relative, work / relative)
    return work


def _policy(work: Path) -> Path:
    return work / SUBTREE_REL / POLICY_REL


def _rewrite_check_all(work: Path, replacement: str) -> None:
    path = _policy(work)
    source = path.read_text(encoding="utf-8")
    if _CALL_BOTH not in source:
        raise AssertionError(f"{POLICY_REL} no longer carries the check_all body this "
                             f"control mutates")
    path.write_text(source.replace(_CALL_BOTH, replacement, 1), encoding="utf-8")


def p_unwire_summary(work: Path) -> str:
    """R2 finding B2, exactly: one line deleted from the roster's callable."""
    _rewrite_check_all(work, """    return (evaluate(root, boundary.RETENTION_WORKFLOW)
            + check_workflow_identity(root))""")
    return "the summary call site removed from check_all"


def p_unwire_workflow(work: Path) -> str:
    _rewrite_check_all(work, """    return (evaluate(root, boundary.RETENTION_WORKFLOW)
            + check_summary_identity(root))""")
    return "the whole-file call site removed from check_all"


def p_co_remove(work: Path) -> str:
    """R2 finding B3: the calls, the implementations and the controls together."""
    _rewrite_check_all(work, "    return evaluate(root, boundary.RETENTION_WORKFLOW)")
    path = _policy(work)
    source = path.read_text(encoding="utf-8")
    for name in ("check_summary_identity", "check_workflow_identity"):
        start = source.index(f"def {name}(")
        end = source.index("\ndef ", start) + 1
        source = source[:start] + source[end:]
    path.write_text(source, encoding="utf-8")
    fixtures = work / SUBTREE_REL / "permission-fixtures.py"
    text = fixtures.read_text(encoding="utf-8")
    text = re.sub(r"SUMMARY_FIXTURES: list\[tuple\[str, str, str, object\]\] = \[.*?\n\]\n",
                  "SUMMARY_FIXTURES: list[tuple[str, str, str, object]] = []\n",
                  text, flags=re.S)
    fixtures.write_text(text, encoding="utf-8")
    return "both call sites, both implementations and every tier-2 control removed"


def p_unconditional(work: Path) -> str:
    """A gate that satisfies the witness by always naming the property.

    The witness half alone would accept this. The baseline half — the same
    callable, against an unmodified tree — is what refuses it.
    """
    _rewrite_check_all(work, """    return (evaluate(root, boundary.RETENTION_WORKFLOW)
            + [Finding("summary.frozen-prose-drift", "unconditional")]
            + check_workflow_identity(root))""")
    return "the summary property reported for every tree, modified or not"


def p_manifest_drops_obligation(work: Path) -> str:
    """The obligation deleted from the manifest, its digest correctly re-pinned."""
    doc = _manifest_rows(work)
    for row in doc["roster"]:
        if row["id"] == "publication-policy":
            row["properties"] = []
    _write_manifest(work, doc)
    _repin_digest(work)
    return "the obligations dropped from the manifest and the digest re-pinned"


#: The obligation the boundary control drops from BOTH data files at once.
_DROPPED = "summary.frozen-prose-drift"


def p_two_file_boundary(work: Path) -> str:
    """The stated boundary: the manifest AND the trust root, edited together.

    One obligation is dropped from both, consistently, and the manifest digest
    is re-pinned. This is ACCEPTED, and saying so is the point: the contract
    stops at ci/run.sh, which is the merge gate for every branch and is reviewed
    as one. A chain of files each pinning the next has no last link.

    The other obligation is left in place. Removing the array altogether is a
    different outcome — `anchor.properties-unpinned` — because the trust root
    may not stop naming obligations at all.
    """
    doc = _manifest_rows(work)
    for row in doc["roster"]:
        row["properties"] = [p for p in row["properties"] if p != _DROPPED]
    _write_manifest(work, doc)
    text = (work / TRUST_ROOT_REL).read_text(encoding="utf-8")
    text = text.replace(f"    {_DROPPED}\n", "", 1)
    (work / TRUST_ROOT_REL).write_text(text, encoding="utf-8")
    _repin_digest(work)
    return f"{_DROPPED} dropped from the manifest AND from the trust root"


def p_pristine(work: Path) -> str:
    return "nothing changed"


PROPERTY_CONTROLS: list[tuple[str, str, tuple[str, ...], object]] = [
    ("P01-summary-call-site-removed",
     "R2 finding B2: the one-line call site deleted from the pinned callable",
     ("ownership.property-not-reported",), p_unwire_summary),
    ("P02-file-call-site-removed", "the other call site deleted",
     ("ownership.property-not-reported",), p_unwire_workflow),
    ("P03-co-removed-with-its-controls",
     "R2 finding B3: calls, implementations and controls removed together",
     ("ownership.property-not-reported",), p_co_remove),
    ("P04-obligation-dropped-from-the-manifest",
     "the obligation deleted from the manifest, its digest correctly re-pinned",
     ("anchor.properties-drift",), p_manifest_drops_obligation),
    ("P05-property-reported-unconditionally",
     "a gate that names the property for every tree satisfies no witness",
     ("ownership.property-always-reported",), p_unconditional),
    ("P06-pristine", "the pristine obligations are accepted", (), p_pristine),
    ("P07-two-file-boundary",
     "the stated boundary, measured: manifest and trust root edited together",
     (), p_two_file_boundary),
]


def _grade_property(pin, name, expected, mutate, real_root: Path):
    """Mutate a copy of the anchored files and read the OWNERSHIP verdict."""
    work = None
    try:
        work = _property_tree(real_root)
        before = sorted((str(p.relative_to(work)), p.read_bytes())
                        for p in work.rglob("*") if p.is_file())
        reached = mutate(work)
        after = sorted((str(p.relative_to(work)), p.read_bytes())
                       for p in work.rglob("*") if p.is_file())
        if name != "P06-pristine" and before == after:
            return "ERROR", "the mutation did not apply", []

        findings = pin.install_roster(work)
        if not findings:
            orchestrator = pin.load_orchestrator(
                work / SUBTREE_REL / "retention_gates.py")
            findings = pin.check_properties(work, orchestrator)
        properties = sorted({f.prop for f in findings})

        if not expected:
            if findings:
                return "GREEN", f"refused: {properties}", properties
            return "PASS", f"accepted ({reached})", properties
        if not findings:
            return "GREEN", "ACCEPTED what must be refused", properties
        absent = [prop for prop in expected if prop not in properties]
        if absent:
            return "INERT", f"refused, but not for {absent}; got {properties}", properties
        return "RED", f"refused, naming {list(properties)}", properties
    except Exception as error:  # noqa: BLE001
        return "ERROR", f"{type(error).__name__}: {error}", []
    finally:
        pin.install_roster(real_root)
        if work is not None:
            shutil.rmtree(work, ignore_errors=True)


def _grade_anchor(pin, name, expected, mutate, real_root: Path):
    """Mutate a copy of the four anchored files and read the verifier's verdict.

    The verifier's module-level roster is installed from whatever anchor the
    COPY carries, and is reinstalled from the real tree afterwards, so a
    control cannot leave the next one measuring its mutation.
    """
    work = None
    try:
        work = _anchor_tree(real_root)
        before = sorted((str(p.relative_to(work)), p.read_bytes())
                        for p in work.rglob("*") if p.is_file())
        reached = mutate(work)
        after = sorted((str(p.relative_to(work)), p.read_bytes())
                       for p in work.rglob("*") if p.is_file())
        if expected and before == after:
            return "ERROR", "the mutation did not apply", []

        findings = pin.install_roster(work)
        if not findings:
            orchestrator = pin.load_orchestrator(
                work / SUBTREE_REL / "retention_gates.py")
            findings = pin.check_roster(orchestrator)
        properties = sorted({f.prop for f in findings})
        messages = " ".join(f.message for f in findings)

        if not expected:
            if findings:
                return "GREEN", f"refused the pristine anchor: {properties}", properties
            return "PASS", f"accepted, as it must be ({reached})", properties
        if not findings:
            return "GREEN", "ACCEPTED what must be refused", properties
        absent = [prop for prop in expected if prop not in properties]
        if absent:
            return "INERT", f"refused, but not for {absent}; got {properties}", properties
        if name.startswith("X01") or name.startswith("X02") or name.startswith("X05"):
            unnamed = [gate_id for gate_id in THREE if gate_id not in messages]
            if unnamed:
                return "INERT", (f"refused for {properties} but did not NAME {unnamed}; "
                                 f"the anchor owes the reviewer the identities"), properties
            return "RED", (f"refused, naming {list(properties)} and every one of "
                           f"{list(THREE)} by identity"), properties
        return "RED", f"refused, naming {list(properties)}", properties
    except Exception as error:  # noqa: BLE001
        return "ERROR", f"{type(error).__name__}: {error}", []
    finally:
        pin.install_roster(real_root)
        if work is not None:
            shutil.rmtree(work, ignore_errors=True)


def _three_file_boundary(pin, real_root: Path) -> tuple[str, str]:
    """The stated trust boundary, MEASURED rather than claimed.

    Editing the orchestrator, the manifest AND the trust root together is not
    detected, and nothing here pretends otherwise: ci/run.sh is the merge gate
    for every branch, it is the last link, and a chain of files each pinning the
    next has no end. What this control asserts is that the boundary is exactly
    where the documentation says it is — three files, not two.
    """
    work = None
    try:
        work = _anchor_tree(real_root)
        _drop_from_orchestrator(work, THREE)
        _drop_from_manifest(work, THREE)
        _drop_ids_from_trust_root(work, THREE)
        _repin_digest(work)
        findings = pin.install_roster(work)
        if not findings:
            orchestrator = pin.load_orchestrator(work / SUBTREE_REL / "retention_gates.py")
            findings = pin.check_roster(orchestrator)
        if findings:
            return "INERT", (f"the three-file edit was refused for "
                             f"{sorted({f.prop for f in findings})}; the documented "
                             f"boundary is narrower than the implementation, so the "
                             f"documentation is wrong")
        return "PASS", ("not detected, and stated as the boundary: removing a member now "
                        "takes an edit to retention_gates.py, to the canonical manifest "
                        "AND to ci/run.sh, which is the merge gate for every branch")
    except Exception as error:  # noqa: BLE001
        return "ERROR", f"{type(error).__name__}: {error}"
    finally:
        pin.install_roster(real_root)
        if work is not None:
            shutil.rmtree(work, ignore_errors=True)


def _stubbed_verifier(pin, real_root: Path) -> tuple[str, str]:
    """A verifier replaced by unconditional success is not a verifier.

    A2 proves ci/run.sh fails when the file is REMOVED. This is the other
    shape: the file still exists and exits 0. Nothing about a process that
    exits 0 distinguishes it from one that checked, so the distinguishing fact
    has to be structural — the authority has an API, and a stub does not
    implement it.
    """
    work = None
    try:
        work = _anchor_tree(real_root)
        (work / VERIFIER_REL).write_text(
            "#!/usr/bin/env python3\nraise SystemExit(0)\n", encoding="utf-8")
        spec = importlib.util.spec_from_file_location(
            "w1a4_stub_verifier_under_check", work / VERIFIER_REL)
        module = importlib.util.module_from_spec(spec)
        try:
            spec.loader.exec_module(module)
        except SystemExit:
            pass
        required = ("check", "check_command", "check_roster", "check_properties",
                    "check_trust_root", "install_roster", "read_anchor", "load_manifest")
        absent = [name for name in required if not callable(getattr(module, name, None))]
        if absent:
            return "RED", (f"the stub exits 0 and implements none of {absent}; the roster "
                           f"self-proof refuses to run against a file that answers no "
                           f"question")
        return "GREEN", "a verifier that only exits 0 satisfied the authority's API"
    except Exception as error:  # noqa: BLE001
        return "ERROR", f"{type(error).__name__}: {error}"
    finally:
        if work is not None:
            shutil.rmtree(work, ignore_errors=True)


# ════════════════════════════════════════════════════════════════════════════
# TRUST-ROOT REACHABILITY (R3 finding F4)
# ════════════════════════════════════════════════════════════════════════════
#
# The R3 proof extracted the pin block from ci/run.sh and ran the extracted
# text on its own. That proves the block works. It proves nothing about whether
# ci/run.sh reaches it, and the reviewer's mutation — the intact block moved
# below `exit "$STATUS"` — passed it unchanged.
#
# Two halves, and neither is sufficient alone.
#
#   STRUCTURAL   `verify-retention-gate-pinned.py::check_trust_root` requires
#                the block to be top-level, unconditional, fail-closed, and
#                preceded by nothing but prologue. It cannot see whether the
#                script would actually run.
#   DYNAMIC      ci/run.sh is executed, with a stubbed verifier, a stubbed
#                cleanup, stubbed host probes and a stubbed `docker` on PATH.
#                The sentinel must appear, the run must fail, and the build
#                stage must never be reached. It cannot see a block that is
#                reachable today and fragile tomorrow.
def _relocate_block(text: str, before: str, after: str) -> str:
    """Cut the pin block out and paste it somewhere else, byte-identically."""
    start = text.index(PIN_BLOCK_START)
    end = text.index(PIN_BLOCK_END) + len(PIN_BLOCK_END) + 1
    block = text[start:end]
    without = text[:start] + text[end:]
    if before:
        return without.replace(before, block + after + before, 1), block
    return without.rstrip("\n") + "\n" + after + block, block


def _relocation(before: str, after: str = ""):
    return lambda text: _relocate_block(text, before, after)[0]


def r_below_final_exit(text: str) -> str:
    """The reviewer's exact mutation."""
    start = text.index(PIN_BLOCK_START)
    end = text.index(PIN_BLOCK_END) + len(PIN_BLOCK_END) + 1
    block = text[start:end]
    without = text[:start] + text[end:]
    return without.rstrip("\n") + "\n\n" + block


def r_after_another_exit(text: str) -> str:
    start = text.index(PIN_BLOCK_START)
    end = text.index(PIN_BLOCK_END) + len(PIN_BLOCK_END) + 1
    block = text[start:end]
    without = text[:start] + 'echo "early"\nexit 0\n\n' + block + text[end:]
    return without


def r_into_uncalled_function(text: str) -> str:
    start = text.index(PIN_BLOCK_START)
    end = text.index(PIN_BLOCK_END) + len(PIN_BLOCK_END) + 1
    block = text[start:end]
    indented = "".join(("    " + line if line.strip() else line)
                       for line in block.splitlines(keepends=True))
    return (text[:start] + "w1a4_pin() {\n" + indented + "}\n" + text[end:])


def r_under_if_false(text: str) -> str:
    start = text.index(PIN_BLOCK_START)
    end = text.index(PIN_BLOCK_END) + len(PIN_BLOCK_END) + 1
    block = text[start:end]
    indented = "".join(("    " + line if line.strip() else line)
                       for line in block.splitlines(keepends=True))
    return text[:start] + "if false; then\n" + indented + "fi\n" + text[end:]


def r_into_ignored_subshell(text: str) -> str:
    start = text.index(PIN_BLOCK_START)
    end = text.index(PIN_BLOCK_END) + len(PIN_BLOCK_END) + 1
    block = text[start:end]
    indented = "".join(("    " + line if line.strip() else line)
                       for line in block.splitlines(keepends=True))
    return text[:start] + "(\n" + indented + ") || true\n" + text[end:]


def r_after_a_return(text: str) -> str:
    start = text.index(PIN_BLOCK_START)
    return text[:start] + "return 0\n\n" + text[start:]


def r_after_status_finalization(text: str) -> str:
    start = text.index(PIN_BLOCK_START)
    end = text.index(PIN_BLOCK_END) + len(PIN_BLOCK_END) + 1
    block = text[start:end]
    without = text[:start] + text[end:]
    return without.replace('banner "SUMMARY"\n', block + '\nbanner "SUMMARY"\n', 1)


def r_sourced_fragment(text: str) -> str:
    """The block moved into a file that is never sourced: the markers vanish."""
    start = text.index(PIN_BLOCK_START)
    end = text.index(PIN_BLOCK_END) + len(PIN_BLOCK_END) + 1
    return text[:start] + text[end:]


def r_masked_with_or_true(text: str) -> str:
    return text.replace(
        '        /usr/bin/python3 "$REPO_ROOT/ci/windows/verify-retention-gate-pinned.py"; '
        'then\n',
        '        /usr/bin/python3 "$REPO_ROOT/ci/windows/verify-retention-gate-pinned.py" '
        '|| true; then\n', 1)


def r_status_instead_of_exit(text: str) -> str:
    return text.replace("    exit 1\n", "    STATUS=1\n", 1)


def r_errexit_disabled_before(text: str) -> str:
    start = text.index(PIN_BLOCK_START)
    return text[:start] + "set +e\n\n" + text[start:]


def r_pristine(text: str) -> str:
    return text


REACHABILITY_CONTROLS: list[tuple[str, str, str | None, object]] = [
    ("RB01-below-the-final-exit",
     "the reviewer's exact mutation: the intact block moved below `exit \"$STATUS\"`",
     "trustroot.pin-block-not-top-level", r_below_final_exit),
    ("RB02-after-another-unconditional-exit",
     "an unconditional `exit 0` inserted above the block",
     "trustroot.pin-block-not-top-level", r_after_another_exit),
    ("RB03-into-an-uncalled-function", "the block wrapped in a function nothing calls",
     "trustroot.pin-block-not-top-level", r_into_uncalled_function),
    ("RB04-under-if-false", "the block under a condition that never holds",
     "trustroot.pin-block-not-top-level", r_under_if_false),
    ("RB05-into-an-ignored-subshell", "the block in a subshell whose result is discarded",
     "trustroot.pin-block-not-top-level", r_into_ignored_subshell),
    ("RB06-after-a-return", "a `return` above the block",
     "trustroot.pin-block-not-top-level", r_after_a_return),
    ("RB07-after-status-finalization", "the block moved down beside the summary",
     "trustroot.pin-block-not-top-level", r_after_status_finalization),
    ("RB08-into-a-sourced-fragment",
     "the block removed to a fragment that is never invoked",
     "trustroot.pin-block-missing", r_sourced_fragment),
    ("RB09-masked-with-or-true", "the verifier's failure tolerated inside the block",
     "trustroot.pin-block-masked", r_masked_with_or_true),
    ("RB10-status-instead-of-exit",
     "the hard exit replaced by a status a later stage reassigns",
     "trustroot.pin-block-not-fail-closed", r_status_instead_of_exit),
    ("RB11-errexit-disabled-before", "`set +e` above the block",
     "trustroot.errexit-disabled-before-pin", r_errexit_disabled_before),
    ("RB12-pristine", "the pristine trust root is accepted", None, r_pristine),
]


def _grade_reachability(pin, name, expected, mutate, pristine_run_sh: str, real_root: Path):
    work = Path(tempfile.mkdtemp(prefix="w1a4r4-reach-"))
    try:
        (work / "ci" / "windows").mkdir(parents=True)
        body = mutate(pristine_run_sh)
        if expected is not None and body == pristine_run_sh:
            return "ERROR", "the mutation did not apply", []
        (work / TRUST_ROOT_REL).write_text(body, encoding="utf-8")
        findings = pin.check_trust_root(work)
        properties = sorted({f.prop for f in findings})
        if expected is None:
            if findings:
                return "GREEN", f"refused the pristine trust root: {properties}", properties
            return "PASS", "accepted, as it must be", properties
        if not findings:
            return "GREEN", "ACCEPTED what must be refused", properties
        if expected in properties:
            return "RED", f"refused, naming {expected}", properties
        return "INERT", f"refused, but not for {expected}; got {properties}", properties
    except Exception as error:  # noqa: BLE001
        return "ERROR", f"{type(error).__name__}: {error}", []
    finally:
        shutil.rmtree(work, ignore_errors=True)


# ── the dynamic half ────────────────────────────────────────────────────────
STUB_SENTINEL = "W1A4-STUB-VERIFIER-RAN"
DOCKER_SENTINEL = "W1A4-PROBE-DOCKER-REACHED"


def _dynamic_tree(root: Path, run_sh: str) -> Path:
    """A runnable ci/run.sh whose every heavy dependency is a stub.

    The script under test is the REAL ci/run.sh — that is the point. What is
    stubbed is everything it calls, so the run costs a second and the only
    thing it can measure is order and reachability.
    """
    work = Path(tempfile.mkdtemp(prefix="w1a4r4-dyn-"))
    (work / "ci" / "lib").mkdir(parents=True)
    (work / "ci" / "windows").mkdir(parents=True)
    (work / "ci" / "hostile-controls").mkdir(parents=True)
    (work / "bin").mkdir()
    (work / TRUST_ROOT_REL).write_text(run_sh, encoding="utf-8")
    (work / "ci" / "lib" / "clean-artifacts.sh").write_text(
        "ci_clean_artifacts() { :; }\n", encoding="utf-8")
    for probe in ("prove-undeclared-reporting.py", "prove-schema-lockdown.py"):
        (work / "ci" / "hostile-controls" / probe).write_text(
            "raise SystemExit(0)\n", encoding="utf-8")
    shutil.copy2(root / MANIFEST_REL, work / MANIFEST_REL)
    (work / VERIFIER_REL).write_text(
        f'import sys\nprint("{STUB_SENTINEL}")\nsys.exit(1)\n', encoding="utf-8")
    stub = work / "bin" / "docker"
    stub.write_text(f'#!/bin/sh\necho "{DOCKER_SENTINEL}"\nexit 0\n', encoding="utf-8")
    stub.chmod(0o755)
    return work


def _run_dynamic(work: Path) -> tuple[int, str]:
    import os
    environment = dict(os.environ)
    environment["PATH"] = f"{work / 'bin'}:{environment['PATH']}"
    done = subprocess.run(["bash", str(work / TRUST_ROOT_REL)],
                          capture_output=True, text=True, env=environment, timeout=300)
    return done.returncode, done.stdout + done.stderr


def _dynamic_reachability(root: Path, pristine_run_sh: str) -> list[tuple[str, str, str]]:
    """Three runs: pristine, the reviewer's relocation, and the sourced fragment."""
    out: list[tuple[str, str, str]] = []

    work = _dynamic_tree(root, pristine_run_sh)
    try:
        code, log = _run_dynamic(work)
        reached = "W1A4-PIN-BLOCK-REACHED" in log
        ran = STUB_SENTINEL in log
        built = DOCKER_SENTINEL in log
        if reached and ran and code != 0 and not built:
            out.append(("D1-pristine-run-reaches-the-pin-first", "RED",
                        f"ci/run.sh printed the sentinel, ran the verifier, exited {code} "
                        f"and never reached the build stage"))
        elif not reached or not ran:
            out.append(("D1-pristine-run-reaches-the-pin-first", "INERT",
                        f"the pin block was not reached (sentinel={reached}, "
                        f"verifier={ran}); exit {code}"))
        elif built:
            out.append(("D1-pristine-run-reaches-the-pin-first", "GREEN",
                        "ci/run.sh dispatched the build stage despite a failing pin"))
        else:
            out.append(("D1-pristine-run-reaches-the-pin-first", "GREEN",
                        f"the pin failed and ci/run.sh exited {code}"))
    finally:
        shutil.rmtree(work, ignore_errors=True)

    # The authority disabled two ways, with the block where it belongs. This is
    # what R3's A2 control was for, run against the real script rather than
    # against an extract of it.
    for name, damage, why in (
            ("D4-verifier-removed",
             lambda w: (w / VERIFIER_REL).unlink(),
             "the external verifier deleted"),
            ("D5-manifest-not-authentic",
             lambda w: (w / MANIFEST_REL).write_text("{}\n", encoding="utf-8"),
             "the canonical manifest replaced, the pinned digest untouched")):
        work = _dynamic_tree(root, pristine_run_sh)
        try:
            damage(work)
            code, log = _run_dynamic(work)
            reached = "W1A4-PIN-BLOCK-REACHED" in log
            built = DOCKER_SENTINEL in log
            if reached and code != 0 and not built:
                out.append((name, "RED",
                            f"with {why}, ci/run.sh reached the pin block, exited {code} "
                            f"and never dispatched the build"))
            elif not reached:
                out.append((name, "INERT", f"the pin block was not reached; exit {code}"))
            else:
                out.append((name, "GREEN",
                            f"ci/run.sh tolerated {why} (exit {code}, build "
                            f"reached={built})"))
        finally:
            shutil.rmtree(work, ignore_errors=True)

    for name, mutate in (("D2-relocated-below-the-final-exit", r_below_final_exit),
                         ("D3-removed-to-a-sourced-fragment", r_sourced_fragment)):
        work = _dynamic_tree(root, mutate(pristine_run_sh))
        try:
            code, log = _run_dynamic(work)
            reached = "W1A4-PIN-BLOCK-REACHED" in log
            ran = STUB_SENTINEL in log
            if not reached and not ran and code == 0:
                out.append((name, "RED",
                            f"the relocated block never ran and ci/run.sh still exited "
                            f"{code}; the structural half is what refuses this, and the "
                            f"dynamic half is what proves the mutation reached its "
                            f"property"))
            elif reached or ran:
                out.append((name, "INERT",
                            f"the block still ran after relocation (sentinel={reached}, "
                            f"verifier={ran}); the mutation did not reach its property"))
            else:
                out.append((name, "INERT",
                            f"the block did not run but ci/run.sh exited {code}"))
        finally:
            shutil.rmtree(work, ignore_errors=True)
    return out


# ════════════════════════════════════════════════════════════════════════════
# THE BOOTSTRAP EXPERIMENT (R3 finding F1), REPLAYED AS AN EXECUTION
# ════════════════════════════════════════════════════════════════════════════
#
# Not a parse. The reviewer's experiment, run under GitHub's own effective Bash
# template — `bash --noprofile --norc -eo pipefail {0}` — twice:
#
#   VULNERABLE  the R3 command (`python3 ...`, no neutralising env) with
#               BASH_ENV pointing at a startup file that defines a `python3`
#               shell function. The malicious file must run and the validator
#               must not: that is what makes the repaired case mean something.
#   REPAIRED    the R4 command (`/usr/bin/python3 ...`) with the gate step's
#               env overlay. The malicious file must NOT run and the validator
#               MUST, evidenced by its W1A4-ROSTER-SENTINEL line.
#
# `--list` rather than `--validate`: the question is which interpreter executes
# which file, and `--validate` would re-enter this very control suite. The
# sentinel is printed on entry either way, and it is printed by the orchestrator
# itself, so it cannot be produced by a wrapper that returned zero.
GITHUB_BASH = ["bash", "--noprofile", "--norc", "-eo", "pipefail"]
MALICIOUS_MARKER = "W1A4-MALICIOUS-STARTUP-RAN"


def _bootstrap_case(root: Path, command: str, overlay: dict) -> tuple[int, str, bool]:
    import os
    work = Path(tempfile.mkdtemp(prefix="w1a4r4-boot-"))
    try:
        marker = work / "marker"
        startup = work / "mal.sh"
        startup.write_text(
            f'echo "{MALICIOUS_MARKER}" > "{marker}"\n'
            f'python3() {{ return 0; }}\n', encoding="utf-8")
        script = work / "step.sh"
        script.write_text(command + "\n", encoding="utf-8")
        environment = dict(os.environ)
        environment["BASH_ENV"] = str(startup)
        environment.update(overlay)
        done = subprocess.run(GITHUB_BASH + [str(script)], cwd=str(root),
                              capture_output=True, text=True, env=environment, timeout=300)
        return done.returncode, done.stdout + done.stderr, marker.exists()
    finally:
        shutil.rmtree(work, ignore_errors=True)


def _bootstrap_experiment(root: Path, pin) -> tuple[str, str]:
    try:
        vulnerable_code, vulnerable_log, vulnerable_marker = _bootstrap_case(
            root, "python3 ci/windows/runtime-retention/retention_gates.py --list", {})
        overlay = {name: value for name, value in pin.REQUIRED_STEP_ENV.items()}
        repaired_code, repaired_log, repaired_marker = _bootstrap_case(
            root, "/usr/bin/python3 ci/windows/runtime-retention/retention_gates.py --list",
            overlay)

        vulnerable_ran = "W1A4-ROSTER-SENTINEL" in vulnerable_log
        repaired_ran = "W1A4-ROSTER-SENTINEL" in repaired_log

        if not vulnerable_marker or vulnerable_ran or vulnerable_code != 0:
            return "INERT", (f"the vulnerable case did not reproduce: startup file ran="
                             f"{vulnerable_marker}, validator ran={vulnerable_ran}, exit="
                             f"{vulnerable_code}. Without it the repaired case proves "
                             f"nothing")
        if repaired_marker:
            return "GREEN", "the malicious startup file ran under the repaired contract"
        if not repaired_ran:
            return "GREEN", (f"the malicious file did not run, but neither did the "
                             f"validator (exit {repaired_code})")
        if repaired_code != 0:
            return "INERT", f"the repaired case exited {repaired_code}"
        return "RED", ("R3's experiment reproduced: with `python3` and no neutralising "
                       "env the startup file ran, the shell function answered and the "
                       "validator never executed (exit 0). With `/usr/bin/python3` and "
                       "the step env overlay the startup file did not run and the real "
                       "validator reached W1A4-ROSTER-SENTINEL")
    except Exception as error:  # noqa: BLE001
        return "ERROR", f"{type(error).__name__}: {error}"


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
    trust_root_before = (root / TRUST_ROOT_REL).read_bytes()
    pristine_run_sh = trust_root_before.decode("utf-8")
    manifest_before = (root / MANIFEST_REL).read_bytes()
    pin = _pin_module()
    # The verifier writes down no roster of its own: it installs one from the
    # anchor. Every control that reads `pin.EXPECTED_ROSTER` needs that done
    # first, and every control that installs from a mutated copy puts it back.
    anchor_findings = pin.install_roster(root)
    if anchor_findings:
        print("W1-A4 GATE ROSTER HARD STOP: the anchor does not authenticate the roster "
              "on the real tree, so no control below measures anything", file=sys.stderr)
        for finding in anchor_findings:
            print(f"  FAIL [{finding.prop}] {finding.message}", file=sys.stderr)
        return 2
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

    print("\nthe independent anchor — a third party to a two-party agreement")
    for name, description, expected, mutate in ANCHOR_CONTROLS:
        grade, detail, properties = _grade_anchor(pin, name, expected, mutate, root)
        if grade not in ("RED", "PASS"):
            failures += 1
        print(f"  {grade:<6} {name:<40} {detail}")
        results.append({"control": name, "property": description,
                        "expected": list(expected), "grade": grade, "detail": detail,
                        "found": properties})

    print("\nthe obligations are owned, not declared (W1-A5-V1-R2 B2/B3)")
    for name, description, expected, mutate in PROPERTY_CONTROLS:
        grade, detail, properties = _grade_property(pin, name, expected, mutate, root)
        if grade not in ("RED", "PASS"):
            failures += 1
        print(f"  {grade:<6} {name:<40} {detail}")
        results.append({"control": name, "property": description,
                        "expected": list(expected), "grade": grade, "detail": detail,
                        "found": properties})

    print("\nthe trust boundary, measured")
    for name, (grade, detail) in (
            ("BX-three-file-edit-is-the-boundary", _three_file_boundary(pin, root)),
            ("BY-stubbed-verifier-implements-nothing", _stubbed_verifier(pin, root))):
        if grade not in ("RED", "PASS"):
            failures += 1
        print(f"  {grade:<6} {name:<40} {detail}")
        results.append({"control": name, "grade": grade, "detail": detail})

    print("\ntrust-root reachability — structural")
    for name, description, expected, mutate in REACHABILITY_CONTROLS:
        grade, detail, properties = _grade_reachability(
            pin, name, expected, mutate, pristine_run_sh, root)
        if grade not in ("RED", "PASS"):
            failures += 1
        print(f"  {grade:<6} {name:<40} {detail}")
        results.append({"control": name, "property": description, "expected": expected,
                        "grade": grade, "detail": detail, "found": properties})

    print("\ntrust-root reachability — dynamic, by running ci/run.sh")
    for name, grade, detail in _dynamic_reachability(root, pristine_run_sh):
        if grade != "RED":
            failures += 1
        print(f"  {grade:<6} {name:<40} {detail}")
        results.append({"control": name, "grade": grade, "detail": detail})

    print("\nthe bootstrap experiment — R3's own BASH_ENV case, executed")
    grade, detail = _bootstrap_experiment(root, pin)
    if grade != "RED":
        failures += 1
    print(f"  {grade:<6} {'B1-bash-env-cannot-intercept':<40} {detail}")
    results.append({"control": "B1-bash-env-cannot-intercept", "grade": grade,
                    "detail": detail})

    print()
    if workflow.read_bytes() != before:
        print("  FAIL   tree-restored     the workflow changed on disk")
        failures += 1
    elif (HERE / "retention_gates.py").read_bytes() != orchestrator_before:
        print("  FAIL   tree-restored     retention_gates.py changed on disk")
        failures += 1
    elif (root / TRUST_ROOT_REL).read_bytes() != trust_root_before:
        print("  FAIL   tree-restored     ci/run.sh changed on disk")
        failures += 1
    elif (root / MANIFEST_REL).read_bytes() != manifest_before:
        print("  FAIL   tree-restored     the canonical manifest changed on disk")
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
