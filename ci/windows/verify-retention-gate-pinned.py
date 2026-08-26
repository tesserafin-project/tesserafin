#!/usr/bin/env python3
"""The retention gate roster is INVOKED, and it is the roster we accepted.

R1 finding D3, and its R2 successor. This file is the external authority for
two things the retention subtree is not allowed to decide about itself:

  1. COMMAND IDENTITY. `.github/workflows/w1-windows-runtime-retention.yml`
     must run the canonical orchestrator command, as one exact YAML scalar, in
     a named job, in a named step, with nothing around it that could make it
     inert.

  2. ROSTER AUTHORITY. `retention_gates.py` may report which gates it HAS. It
     may not decide which gates it MUST have. That set is frozen below, by
     exact id, module, callable, kind, argv and position, and is checked
     against the orchestrator's real bindings.

WHY THE COMMAND CHECK IS EQUALITY AND NOT A REGEX.

R2 decided "is this command a no-op?" with a prefix regex (`_NO_OP_PREFIX`), a
success-mask regex and a substring containment test. Every such list is a list
of the bypasses someone thought of. Measured against the R2 file, eleven
syntactically harmless wrappers were accepted as a live invocation: `#cmd`,
`##cmd`, `cmd || :`, `cmd || echo x`, `cmd &`, `if false; then cmd; fi`,
`(cmd)`, a block scalar containing the command, a padded scalar,
`working-directory:` and a `shell:` override.

So the question stops being "is this command inert?", which needs a shell
semantics model, and becomes "is this string the canonical string?", which
needs `==`. Any deviation is refused, including deviations that would in fact
have run the command. A gate step is not a place for expressive shell.

Nothing here executes a shell, and no rule depends on spacing after `#`.

WHY THE ROSTER LIVES HERE.

A roster that names its own required members can be edited in one place: delete
the entry and delete the requirement, and the run gets shorter and stays green.
`retention_gates.py` keeps a diagnostic copy of its expectation, and that copy
is explicitly NOT the authority — this file is, it sits outside
`ci/windows/runtime-retention/`, and `ci/run.sh` runs it on every branch.

TRUST ROOT. `ci/run.sh` is where this stops. It is a merge gate for every
branch, it invokes this file unconditionally, and a non-zero exit here fails
it. Nothing pins `ci/run.sh` in turn: a chain of scripts each pinning the next
has no last link, and pretending otherwise would be the same defect one level
further out. This file names ci/run.sh as its root and the chain ends.

Exit 0 if both contracts hold, 1 otherwise. Nothing is written.
"""

from __future__ import annotations

import importlib.util
import subprocess
import sys
from pathlib import Path

import yaml

WORKFLOW = ".github/workflows/w1-windows-runtime-retention.yml"
ORCHESTRATOR = "ci/windows/runtime-retention/retention_gates.py"
SUBTREE = "ci/windows/runtime-retention"

#: The job, the step and the command, exactly as they must appear.
REQUIRED_JOB = "gates"
REQUIRED_STEP_ID = "retention-gate-roster"
CANONICAL_COMMAND = "python3 ci/windows/runtime-retention/retention_gates.py --validate"

#: The one shell an invocation may declare. GitHub expands `bash` to
#: `bash --noprofile --norc -eo pipefail {0}`, which fails fast; `sh` does not,
#: and neither does `pwsh` or a custom command template.
PERMITTED_SHELL = "bash"

#: Environment names that can change which interpreter runs, or which file it
#: reads as the script, without changing the command's text. Permitted nowhere
#: in this workflow.
INTERPRETER_ENV = frozenset({
    "PATH", "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "PYTHONEXECUTABLE",
    "PYTHONSAFEPATH", "PYTHONNOUSERSITE", "LD_PRELOAD", "LD_LIBRARY_PATH",
    "VIRTUAL_ENV", "CONDA_PREFIX",
})

#: The path filters that must reach the job, so a change to the subtree or to
#: the workflow itself cannot land without running it.
REQUIRED_TRIGGER_PATHS = (f"{SUBTREE}/**", WORKFLOW)


class Entry:
    """One authoritative roster member. Position in EXPECTED_ROSTER is part of it."""

    __slots__ = ("gate_id", "module", "callable_name", "kind", "argv", "tier")

    def __init__(self, gate_id: str, module: str, callable_name: str, kind: str,
                 argv: tuple[str, ...], tier: str) -> None:
        self.gate_id = gate_id
        self.module = module
        self.callable_name = callable_name
        self.kind = kind
        self.argv = argv
        self.tier = tier

    @property
    def identity(self) -> tuple[str, str, str, tuple[str, ...], str]:
        return (self.module, self.callable_name, self.kind, self.argv, self.tier)

    def __repr__(self) -> str:  # pragma: no cover - diagnostics only
        return (f"{self.gate_id} = {self.tier}:{self.module}::{self.callable_name}"
                f"[{self.kind}]{list(self.argv)}")


#: THE AUTHORITATIVE ROSTER. Order is part of the contract: the orchestrator
#: must present its gates, then its proofs, in exactly this sequence. An
#: order-independent rule would let a proof and a gate trade places while both
#: sets stayed equal, and "no gate substituted by a self-proof" is one of the
#: properties this has to hold.
EXPECTED_ROSTER: tuple[Entry, ...] = (
    Entry("accepted-contract", "contract.py", "check_all", "findings", (), "gate"),
    Entry("deterministic-layout", "retention.py", "check_all", "findings", (), "gate"),
    Entry("publication-policy", "publication_policy.py", "check_all", "findings", (), "gate"),
    Entry("excluded-subtree-ownership", "boundary.py", "check_all", "findings", (), "gate"),
    Entry("proof-trigger", "trigger_policy.py", "check_all", "findings", (), "gate"),
    Entry("registry-authority", "loopback-corpus.py", "check_all", "findings", (), "gate"),
    Entry("ownership-self-proof", "boundary-controls.py", "main", "exit-code", (), "proof"),
    Entry("publication-self-proof", "permission-fixtures.py", "main", "exit-code", (),
          "proof"),
    Entry("reusable-workflow-self-proof", "reusable-workflow-controls.py", "main",
          "exit-code", (), "proof"),
    Entry("trusted-source-self-proof", "trusted-source-controls.py", "main", "exit-code",
          (), "proof"),
    Entry("reference-grammar-self-proof", "reference-corpus.py", "main", "exit-code",
          ("--allow-missing-pwsh",), "proof"),
    Entry("hostile-controls-self-proof", "negative-controls.py", "main", "exit-code",
          ("--fixture", "{fixture}"), "proof"),
    Entry("hostile-controls-ablation", "negative-controls.py", "main", "exit-code",
          ("--fixture", "{fixture}", "--ablate"), "proof"),
    Entry("gate-roster-self-proof", "gate-roster-controls.py", "main", "exit-code", (),
          "proof"),
)

EXPECTED_BY_ID: dict[str, Entry] = {entry.gate_id: entry for entry in EXPECTED_ROSTER}


class Finding:
    __slots__ = ("prop", "message")

    def __init__(self, prop: str, message: str) -> None:
        self.prop, self.message = prop, message


def repo_root() -> Path:
    out = subprocess.run(
        ["git", "-C", str(Path(__file__).resolve().parent), "rev-parse", "--show-toplevel"],
        check=True, capture_output=True, text=True,
    )
    return Path(out.stdout.strip())


# ── command identity ────────────────────────────────────────────────────────
def _env_findings(where: str, env, prop: str) -> list[Finding]:
    if env is None:
        return []
    if not isinstance(env, dict):
        return [Finding(prop, f"{where} declares a non-mapping `env:`, which cannot be read")]
    offending = sorted(set(env) & INTERPRETER_ENV)
    if offending:
        return [Finding(
            prop,
            f"{where} sets {offending} — an environment that can change which python3 runs, "
            f"or which file it reads, without changing one character of the command")]
    return []


def check_command(root: Path) -> list[Finding]:
    """The canonical command is this step's `run`, byte for byte. No shell runs."""
    findings: list[Finding] = []
    path = root / WORKFLOW
    if not path.is_file():
        return [Finding("cmd.workflow-missing", f"{WORKFLOW} does not exist")]
    if path.is_symlink():
        return [Finding("cmd.workflow-symlinked", f"{WORKFLOW} is a symbolic link")]

    try:
        doc = yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as error:
        return [Finding("cmd.workflow-unparseable", f"{WORKFLOW} is not valid YAML: {error}")]
    if not isinstance(doc, dict) or not isinstance(doc.get("jobs"), dict):
        return [Finding("cmd.workflow-unparseable", f"{WORKFLOW} has no `jobs:` mapping")]

    # Reachability. PyYAML reads the bare key `on` as the boolean True.
    triggers = doc.get("on", doc.get(True))
    if not isinstance(triggers, dict) or "pull_request" not in triggers:
        findings.append(Finding(
            "cmd.trigger-missing",
            f"{WORKFLOW} does not declare a `pull_request:` trigger, so the job the gate "
            f"lives in is not reachable from a pull request"))
    else:
        pull_request = triggers["pull_request"] or {}
        paths = pull_request.get("paths") if isinstance(pull_request, dict) else None
        if not isinstance(paths, list):
            findings.append(Finding(
                "cmd.trigger-paths-missing",
                f"{WORKFLOW} declares `pull_request:` with no `paths:` allowlist"))
        else:
            absent = [p for p in REQUIRED_TRIGGER_PATHS if p not in paths]
            if absent:
                findings.append(Finding(
                    "cmd.trigger-paths-narrowed",
                    f"{WORKFLOW} no longer triggers on {absent}; a change there would not "
                    f"start the job the gate runs in"))

    findings.extend(_env_findings(
        f"{WORKFLOW} workflow-level `env:`", doc.get("env"), "cmd.workflow-env"))

    jobs = doc["jobs"]
    job = jobs.get(REQUIRED_JOB)
    if not isinstance(job, dict):
        return findings + [Finding(
            "cmd.job-missing",
            f"{WORKFLOW} has no job `{REQUIRED_JOB}`; the roster is invoked from that job "
            f"and nowhere else. Jobs present: {sorted(jobs)}")]

    if "if" in job:
        findings.append(Finding(
            "cmd.job-conditional",
            f"job `{REQUIRED_JOB}` carries `if: {job['if']!r}`. A gate job runs or it is "
            f"not a gate; no condition is permitted, true ones included"))
    if "continue-on-error" in job:
        findings.append(Finding(
            "cmd.job-continue-on-error",
            f"job `{REQUIRED_JOB}` declares continue-on-error; a failure that does not fail "
            f"the run is not a refusal"))
    if "strategy" in job:
        findings.append(Finding(
            "cmd.job-matrix",
            f"job `{REQUIRED_JOB}` declares a `strategy:`; a matrix can vary the shell, the "
            f"environment and the working directory under one unchanged command"))
    findings.extend(_env_findings(f"job `{REQUIRED_JOB}` `env:`", job.get("env"),
                                  "cmd.job-env"))
    for needed in (job.get("needs") or []) if isinstance(job.get("needs"), list) else \
            ([job["needs"]] if isinstance(job.get("needs"), str) else []):
        upstream = jobs.get(needed)
        if not isinstance(upstream, dict):
            findings.append(Finding(
                "cmd.job-needs-missing",
                f"job `{REQUIRED_JOB}` needs `{needed}`, which is not a job"))
        elif "if" in upstream or upstream.get("continue-on-error"):
            findings.append(Finding(
                "cmd.job-needs-skippable",
                f"job `{REQUIRED_JOB}` needs `{needed}`, which is conditional or tolerated; "
                f"a skipped dependency skips the gate"))

    steps = job.get("steps")
    if not isinstance(steps, list):
        return findings + [Finding(
            "cmd.job-has-no-steps", f"job `{REQUIRED_JOB}` declares no steps")]

    identified = [(i, s) for i, s in enumerate(steps)
                  if isinstance(s, dict) and s.get("id") == REQUIRED_STEP_ID]
    if not identified:
        return findings + [Finding(
            "cmd.step-missing",
            f"job `{REQUIRED_JOB}` has no step with `id: {REQUIRED_STEP_ID}`. The step is "
            f"named by id and not by position or prose, so commenting it out, renaming it "
            f"or moving it elsewhere is this finding and not a silent pass")]
    if len(identified) > 1:
        findings.append(Finding(
            "cmd.step-duplicated",
            f"job `{REQUIRED_JOB}` declares `id: {REQUIRED_STEP_ID}` {len(identified)} "
            f"times; which one is the gate is then a matter of opinion"))

    index, step = identified[0]
    label = f"job `{REQUIRED_JOB}` step {index} (`{REQUIRED_STEP_ID}`)"

    if "uses" in step:
        findings.append(Finding(
            "cmd.step-uses-action",
            f"{label} is an action invocation, not the canonical command"))

    run = step.get("run")
    if not isinstance(run, str):
        findings.append(Finding(
            "cmd.run-missing",
            f"{label} has no `run:` string; its keys are {sorted(step)}"))
    elif run != CANONICAL_COMMAND:
        findings.append(Finding(
            "cmd.run-not-canonical",
            f"{label} runs {run!r}. The contract is EQUALITY with "
            f"{CANONICAL_COMMAND!r} — one plain scalar, no wrapper, no composition, no "
            f"padding, no trailing newline. A deviation that would still run the command "
            f"is refused too: deciding that would take a shell, and this decides it with "
            f"`==`"))

    if "if" in step:
        findings.append(Finding(
            "cmd.step-conditional",
            f"{label} carries `if: {step['if']!r}`; the gate step is unconditional"))
    if "continue-on-error" in step:
        findings.append(Finding(
            "cmd.step-continue-on-error",
            f"{label} declares continue-on-error; the gate's refusal would not fail the run"))
    if "working-directory" in step:
        findings.append(Finding(
            "cmd.step-working-directory",
            f"{label} sets `working-directory: {step['working-directory']!r}`; the command "
            f"names a repository-relative path and resolves it against the checkout root"))
    if "shell" in step and step["shell"] != PERMITTED_SHELL:
        findings.append(Finding(
            "cmd.step-shell-override",
            f"{label} sets `shell: {step['shell']!r}`. The one permitted shell is "
            f"{PERMITTED_SHELL!r}, which GitHub expands with `-eo pipefail`"))
    findings.extend(_env_findings(label, step.get("env"), "cmd.step-env"))

    return findings


# ── roster authority ────────────────────────────────────────────────────────
def load_orchestrator(path: Path):
    """Load `retention_gates.py` as the object whose real bindings are checked.

    Its own directory goes on `sys.path` for the duration, because the
    orchestrator imports `boundary` by name; it is removed again so this file
    leaves no way for the subtree to be imported implicitly afterwards.
    """
    spec = importlib.util.spec_from_file_location("w1a4_orchestrator_under_check", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"{path} cannot be loaded as a module")
    module = importlib.util.module_from_spec(spec)
    here = str(path.resolve().parent)
    sys.path.insert(0, here)
    try:
        spec.loader.exec_module(module)
    finally:
        try:
            sys.path.remove(here)
        except ValueError:
            pass
    return module


def _actual(orchestrator) -> tuple[list[tuple[str, object]], list[Finding]]:
    """The roster the orchestrator really carries, as (tier, gate) pairs."""
    out: list[tuple[str, object]] = []
    problems: list[Finding] = []
    for attribute, tier in (("GATES", "gate"), ("PROOFS", "proof")):
        roster = getattr(orchestrator, attribute, None)
        if not isinstance(roster, (tuple, list)):
            problems.append(Finding(
                "roster.orchestrator-shape",
                f"{ORCHESTRATOR} does not expose `{attribute}` as a sequence"))
            continue
        for gate in roster:
            out.append((tier, gate))
    return out, problems


def _describe(tier: str, gate) -> tuple[str, str, str, tuple[str, ...], str]:
    return (getattr(gate, "filename", "?"), getattr(gate, "function", "?"),
            getattr(gate, "kind", "?"), tuple(getattr(gate, "argv", ())), tier)


def check_roster(orchestrator) -> list[Finding]:
    """The orchestrator carries exactly the roster this file freezes."""
    findings: list[Finding] = []
    actual, problems = _actual(orchestrator)
    findings.extend(problems)
    if problems:
        return findings

    seen: dict[str, int] = {}
    for _tier, gate in actual:
        gate_id = getattr(gate, "gate_id", None)
        if not isinstance(gate_id, str):
            findings.append(Finding(
                "roster.entry-unidentifiable",
                f"a roster entry has no string `gate_id`: {gate!r}"))
            continue
        seen[gate_id] = seen.get(gate_id, 0) + 1

    duplicates = sorted(i for i, n in seen.items() if n > 1)
    if duplicates:
        findings.append(Finding(
            "roster.duplicate",
            f"the orchestrator names {duplicates} more than once; a gate invoked twice is "
            f"not two gates, and the second invocation can be quietly repointed"))

    missing = [e.gate_id for e in EXPECTED_ROSTER if e.gate_id not in seen]
    if missing:
        findings.append(Finding(
            "roster.missing",
            f"the orchestrator no longer carries {missing}. This set is frozen HERE, "
            f"outside `{SUBTREE}/`, precisely so that deleting the entry cannot delete the "
            f"requirement with it"))
    unknown = sorted(i for i in seen if i not in EXPECTED_BY_ID)
    if unknown:
        findings.append(Finding(
            "roster.unknown",
            f"the orchestrator carries {unknown}, which this contract does not name; a "
            f"member nobody required is a member that can be removed unnoticed"))

    if len(actual) != len(EXPECTED_ROSTER):
        findings.append(Finding(
            "roster.cardinality",
            f"the orchestrator carries {len(actual)} members; the contract is exactly "
            f"{len(EXPECTED_ROSTER)}"))

    for tier, gate in actual:
        gate_id = getattr(gate, "gate_id", None)
        expected = EXPECTED_BY_ID.get(gate_id)
        if expected is None:
            continue
        found = _describe(tier, gate)
        if found[4] != expected.tier:
            findings.append(Finding(
                "roster.tier",
                f"{gate_id!r} is declared as a {found[4]} and must be a {expected.tier}; a "
                f"gate moved into the proof collection stops deciding whether the tree is "
                f"acceptable while the roster still looks complete"))
        if found[0] != expected.module:
            findings.append(Finding(
                "roster.module",
                f"{gate_id!r} resolves to module {found[0]!r}; the contract is "
                f"{expected.module!r}. The same function name in another file is another "
                f"function"))
        if found[1] != expected.callable_name:
            findings.append(Finding(
                "roster.callable",
                f"{gate_id!r} resolves to {found[0]}::{found[1]}; the contract is "
                f"{expected.module}::{expected.callable_name}. An entry keeping its id "
                f"while its callable changes is a gate replaced, not a gate configured"))
        if found[2] != expected.kind:
            findings.append(Finding(
                "roster.kind",
                f"{gate_id!r} declares kind {found[2]!r}; the contract is "
                f"{expected.kind!r}"))
        if found[3] != expected.argv:
            findings.append(Finding(
                "roster.argv",
                f"{gate_id!r} is invoked with argv {list(found[3])}; the contract is "
                f"{list(expected.argv)}. The argv is part of the identity: the same entry "
                f"point with and without `--ablate` is two members, not one named twice"))

    if not missing and not unknown and not duplicates and len(actual) == len(EXPECTED_ROSTER):
        order = [getattr(g, "gate_id", "?") for _t, g in actual]
        contract = [e.gate_id for e in EXPECTED_ROSTER]
        if order != contract:
            first = next(i for i, (a, b) in enumerate(zip(order, contract)) if a != b)
            findings.append(Finding(
                "roster.order",
                f"the orchestrator presents its roster in a different order, first "
                f"differing at position {first}: {order[first]!r} where the contract has "
                f"{contract[first]!r}. Order is part of this contract so that a gate and a "
                f"proof cannot trade places under equal sets"))

    findings.extend(_check_bindings(orchestrator))
    return findings


def _check_bindings(orchestrator) -> list[Finding]:
    """Resolve each expected callable through the orchestrator's own loader.

    A lambda, an alias or a generic no-op bound over the expected name keeps the
    roster's declared identity and changes what runs. `__name__` catches the
    lambda and the aliased no-op; `__code__.co_filename` catches a callable
    imported from somewhere else and re-exported under the right name.
    """
    findings: list[Finding] = []
    loader = getattr(orchestrator, "_load", None)
    here = getattr(orchestrator, "HERE", None)
    if not callable(loader) or not isinstance(here, Path):
        return [Finding(
            "roster.orchestrator-shape",
            f"{ORCHESTRATOR} exposes no `_load`/`HERE`, so its real bindings cannot be "
            f"resolved")]

    on_path = str(Path(here).resolve())
    sys.path.insert(0, on_path)
    try:
        findings.extend(_resolve_bindings(loader, Path(here)))
    finally:
        try:
            sys.path.remove(on_path)
        except ValueError:
            pass
    return findings


def _resolve_bindings(loader, here: Path) -> list[Finding]:
    findings: list[Finding] = []
    for expected in EXPECTED_ROSTER:
        try:
            module = loader(expected.module)
        except Exception as error:  # noqa: BLE001 - a module that will not load is a finding
            findings.append(Finding(
                "roster.module-unloadable",
                f"{expected.gate_id!r} names {expected.module}, which does not load: "
                f"{type(error).__name__}: {error}"))
            continue
        target = getattr(module, expected.callable_name, None)
        if not callable(target):
            findings.append(Finding(
                "roster.callable-absent",
                f"{expected.module}::{expected.callable_name} is not a callable"))
            continue
        name = getattr(target, "__name__", "<unnamed>")
        if name != expected.callable_name:
            findings.append(Finding(
                "roster.callable-substituted",
                f"{expected.module}::{expected.callable_name} is bound to a callable named "
                f"{name!r}. A lambda or an alias satisfies the roster's declared identity "
                f"and runs something else"))
            continue
        code = getattr(target, "__code__", None)
        origin = Path(code.co_filename).resolve() if code is not None else None
        wanted = (here / expected.module).resolve()
        if origin is not None and origin != wanted:
            findings.append(Finding(
                "roster.callable-foreign",
                f"{expected.module}::{expected.callable_name} is defined in {origin}, not "
                f"in {wanted}; it has been re-exported under the expected name"))
    return findings


# ── entry point ─────────────────────────────────────────────────────────────
def check(root: Path) -> list[Finding]:
    findings = check_command(root)
    path = root / ORCHESTRATOR
    if not path.is_file():
        return findings + [Finding(
            "roster.orchestrator-missing", f"{ORCHESTRATOR} does not exist")]
    if path.is_symlink():
        return findings + [Finding(
            "roster.orchestrator-symlinked", f"{ORCHESTRATOR} is a symbolic link")]
    try:
        orchestrator = load_orchestrator(path)
    except Exception as error:  # noqa: BLE001
        return findings + [Finding(
            "roster.orchestrator-unloadable",
            f"{ORCHESTRATOR} does not import: {type(error).__name__}: {error}")]
    return findings + check_roster(orchestrator)


def main() -> int:
    root = repo_root()
    findings = check(root)
    if findings:
        print(f"W1-A4 RETENTION AUTHORITY HARD STOP: {len(findings)} finding(s)",
              file=sys.stderr)
        for finding in findings:
            print(f"  FAIL [{finding.prop}] {finding.message}", file=sys.stderr)
        return 1
    print(f"{WORKFLOW} job `{REQUIRED_JOB}` step `{REQUIRED_STEP_ID}` runs exactly "
          f"{CANONICAL_COMMAND!r}")
    print(f"{ORCHESTRATOR} carries exactly the {len(EXPECTED_ROSTER)} members this file "
          f"requires, in order, each resolving to its expected callable")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
