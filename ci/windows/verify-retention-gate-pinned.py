#!/usr/bin/env python3
"""The retention gate roster is actually INVOKED (#236, W1-A4-R2).

R1's blocking finding D3. `ci/windows/runtime-retention/boundary.py` proved its
own gate was pinned with

    if "boundary-controls.py" not in body or "boundary.py" not in body

against the retention workflow's raw text. A comment naming either file
satisfies that. So the invocation could be commented out — or replaced by
`echo`, or suffixed with `|| true` — while the step name, the surrounding prose
and the check itself all stayed exactly as they were, and the gate reported
itself pinned while running nothing.

Three things about this file are the repair, and none of them works alone.

IT LIVES OUTSIDE THE SUBTREE. A pin that the pinned subtree's own gate performs
is circular: delete the subtree's gate and the pin goes with it. This is run by
`ci/run.sh`, which is a merge gate for every branch and has no stake in whether
the retention contract is convenient.

IT PARSES THE WORKFLOW. `yaml.safe_load` sees configuration and not text, so a
comment is gone before any assertion is made — the exact substitution the
substring check could not survive. The command has to exist as a step's `run`
value, in the named job, in the parsed document.

IT REFUSES THE NO-OPS INDIVIDUALLY. Present-but-inert is the failure mode this
is for, so `: python3 ...`, `true python3 ...`, `echo python3 ...`,
`# python3 ...`, `python3 ... || true`, `python3 ... ; true`,
`continue-on-error: true` and a condition that can never hold are each refused
under their own name. "The string is in the file" is what R1 checked and is
exactly what must stop being enough.

Exit 0 if the canonical command is live, 1 otherwise. Nothing is written.
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

import yaml

WORKFLOW = ".github/workflows/w1-windows-runtime-retention.yml"

#: The job, and the command, exactly as they must appear.
REQUIRED_JOB = "gates"
REQUIRED_COMMAND = "python3 ci/windows/runtime-retention/retention_gates.py --validate"

#: Prefixes that make a command a no-op while leaving its text in place.
_NO_OP_PREFIX = re.compile(
    r"^\s*(?::|true|false|echo|printf|#|//|rem\b|\bcat\b|\bprint\b)\s", re.I)

#: Suffixes that mask a non-zero exit.
_SUCCESS_MASK = re.compile(
    r"(\|\|\s*(?:true|:|exit\s+0)\b|;\s*(?:true|:|exit\s+0)\b|&&\s*true\b)")

#: Conditions that can never hold. `if: false`, and the expression forms of it.
_UNREACHABLE = re.compile(
    r"^\s*(?:\$\{\{\s*)?(?:false|'false'|\"false\"|0|1\s*==\s*2|"
    r"github\.event_name\s*==\s*''\s*)(?:\s*\}\})?\s*$", re.I)


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


def _condition_is_unreachable(condition) -> bool:
    if condition is None:
        return False
    if isinstance(condition, bool):
        return condition is False
    return bool(_UNREACHABLE.match(str(condition)))


def check(root: Path) -> list[Finding]:
    findings: list[Finding] = []
    path = root / WORKFLOW
    if not path.is_file():
        return [Finding("pin.workflow-missing", f"{WORKFLOW} does not exist")]
    if path.is_symlink():
        return [Finding("pin.workflow-symlinked", f"{WORKFLOW} is a symbolic link")]

    try:
        doc = yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as error:
        return [Finding("pin.workflow-unparseable", f"{WORKFLOW} is not valid YAML: {error}")]
    if not isinstance(doc, dict) or not isinstance(doc.get("jobs"), dict):
        return [Finding("pin.workflow-unparseable", f"{WORKFLOW} has no `jobs:` mapping")]

    jobs = doc["jobs"]
    job = jobs.get(REQUIRED_JOB)
    if not isinstance(job, dict):
        return [Finding(
            "pin.job-missing",
            f"{WORKFLOW} has no job `{REQUIRED_JOB}`; the roster is invoked from that job "
            f"and nowhere else, so removing it removes every gate at once. Jobs present: "
            f"{sorted(jobs)}",
        )]

    if job.get("continue-on-error"):
        findings.append(Finding(
            "pin.job-continue-on-error",
            f"job `{REQUIRED_JOB}` sets continue-on-error; a gate whose failure is "
            f"tolerated is not a gate",
        ))
    if _condition_is_unreachable(job.get("if")):
        findings.append(Finding(
            "pin.job-unreachable",
            f"job `{REQUIRED_JOB}` carries `if: {job.get('if')!r}`, under which it never runs",
        ))

    steps = job.get("steps")
    if not isinstance(steps, list):
        return findings + [Finding(
            "pin.job-has-no-steps", f"job `{REQUIRED_JOB}` declares no steps")]

    matches = []
    for index, step in enumerate(steps):
        if not isinstance(step, dict) or not isinstance(step.get("run"), str):
            continue
        for line in step["run"].splitlines():
            stripped = line.strip()
            if REQUIRED_COMMAND not in stripped:
                continue
            matches.append((index, step, line, stripped))

    if not matches:
        return findings + [Finding(
            "pin.command-absent",
            f"no step of job `{REQUIRED_JOB}` runs {REQUIRED_COMMAND!r}. It must appear as "
            f"a step's `run` value in the PARSED document — a comment mentioning it is not "
            f"an invocation, which is the whole of finding D3",
        )]

    live = 0
    for index, step, line, stripped in matches:
        label = f"job `{REQUIRED_JOB}` step {index} ({step.get('name') or 'unnamed'})"
        problems = []
        if _NO_OP_PREFIX.match(line):
            problems.append(Finding(
                "pin.command-is-a-no-op",
                f"{label} carries {stripped!r}, whose leading token makes it a no-op; the "
                f"command's TEXT being present is what R1 checked and is not enough",
            ))
        if _SUCCESS_MASK.search(stripped):
            problems.append(Finding(
                "pin.command-success-masked",
                f"{label} carries {stripped!r}, which masks a non-zero exit",
            ))
        if step.get("continue-on-error"):
            problems.append(Finding(
                "pin.step-continue-on-error",
                f"{label} sets continue-on-error; the gate's refusal would not fail the run",
            ))
        if _condition_is_unreachable(step.get("if")):
            problems.append(Finding(
                "pin.step-unreachable",
                f"{label} carries `if: {step.get('if')!r}`, under which it never runs",
            ))
        if problems:
            findings.extend(problems)
        else:
            live += 1

    if live == 0:
        findings.append(Finding(
            "pin.no-live-invocation",
            f"job `{REQUIRED_JOB}` names {REQUIRED_COMMAND!r} in {len(matches)} place(s), "
            f"and every one of them is inert",
        ))
    return findings


def main() -> int:
    root = repo_root()
    findings = check(root)
    if findings:
        print(f"W1-A4 GATE PIN HARD STOP: {len(findings)} finding(s) — the retention gate "
              f"roster is not provably invoked", file=sys.stderr)
        for finding in findings:
            print(f"  FAIL [{finding.prop}] {finding.message}", file=sys.stderr)
        return 1
    print(f"{WORKFLOW} job `{REQUIRED_JOB}` runs {REQUIRED_COMMAND!r} as a live, unmasked, "
          f"reachable step")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
