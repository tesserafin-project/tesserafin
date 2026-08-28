"""The W1 proof trigger is positive-only, and every retention change crosses it.

WHAT THIS REPLACES, AND WHY IT IS NOT THE SAME CHECK RENAMED.

W1-A4-R1 shipped `pathfilter.py`, whose authority was that an ordered negation
in the W1 dual-runner workflow's `paths:` filter subtracted EXACTLY the
retention subtree: it asserted the negation was present, that it followed the
positive pattern it subtracted from, and that a retention-only diff therefore
did NOT start the proof.

Independent review withdrew that optimisation (W1-A4-R1, finding D2). The
negation was safe only under a premise that cannot be discharged — that a
static pattern set recognises every way shell tooling can stage a directory
into a build. It cannot, and where it failed the exclusion guaranteed that no
proof ran over the ingested bytes.

This module therefore states the OPPOSITE contract, and `pathfilter.py` is
deleted rather than kept as evidence for a policy that no longer exists:

  * the W1 `paths:` filter carries NO negative pattern, at any position, for
    any subtree — the optimisation cannot be reintroduced by a later edit;
  * every representative change under the retention subtree resolves the
    trigger TRUE, including the exact staging-adjacent diffs that made the
    old exclusion unsafe;
  * every build-affecting change still resolves it true, as before.

The glob engine is retained because the positive claim needs it: "this diff
triggers the proof" is a statement about GitHub's matching rules, not about
whether a substring appears in a string.

  * `*`  matches zero or more characters, never `/`;
  * `**` matches zero or more characters, including `/`;
  * `?`  matches exactly one character, never `/`;
  * everything else is literal;
  * `paths:` is an allowlist, so a file starts excluded;
  * the workflow runs if ANY changed file ends up included.

Last-match-wins is not implemented, because a filter with no negation has no
order-dependent behaviour left to model. A negative pattern is a finding, not
an input to the matcher.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

import yaml

import boundary

SUBTREE = boundary.SUBTREE


def compile_pattern(pattern: str) -> re.Pattern[str]:
    """GitHub's `paths:` glob, as a regex."""
    out: list[str] = []
    index = 0
    while index < len(pattern):
        char = pattern[index]
        if char == "*":
            if pattern.startswith("**", index):
                out.append(".*")
                index += 2
                continue
            out.append("[^/]*")
        elif char == "?":
            out.append("[^/]")
        else:
            out.append(re.escape(char))
        index += 1
    return re.compile("^" + "".join(out) + "$")


def included(path: str, patterns: list[str]) -> bool:
    """Whether `path` is included by a POSITIVE-ONLY `paths:` list.

    A negative pattern is refused by `check_positive_only` before this runs, so
    it is never interpreted here. Interpreting one would be the beginning of
    tolerating it.
    """
    return any(compile_pattern(p).match(path) for p in patterns if not p.startswith("!"))


def triggers(changed: list[str], patterns: list[str]) -> bool:
    return any(included(path, patterns) for path in changed)


def workflow_paths(root: Path, workflow: str) -> list[str]:
    doc = yaml.safe_load((root / workflow).read_text(encoding="utf-8"))
    # `on:` is a YAML 1.1 boolean, so PyYAML gives the key back as True.
    triggers_block = doc.get("on", doc.get(True))
    pull_request = (triggers_block or {}).get("pull_request") or {}
    return list(pull_request.get("paths") or [])


def check_positive_only(patterns: list[str], workflow: str) -> list[boundary.Finding]:
    """No negative pattern, for any subtree, at any position.

    This is the structural half of the R2 repair. Removing the one negation by
    hand fixes today's tree; refusing the SYNTAX is what stops the optimisation
    coming back the next time a five-hour bill lands.
    """
    findings: list[boundary.Finding] = []
    for index, pattern in enumerate(patterns):
        if pattern.startswith("!"):
            findings.append(boundary.Finding(
                "trigger.negative-pattern",
                f"{workflow} `paths:` carries the negative pattern {pattern!r} at position "
                f"{index}; the proof trigger is positive-only, because a subtracted subtree "
                f"is a subtree no proof runs over",
            ))
    return findings


# ── the cases ───────────────────────────────────────────────────────────────
RETENTION_ONLY = [f"{SUBTREE}/contract.py", f"{SUBTREE}/accepted-runtime.json"]
WINDOWS_FFMPEG = ["ci/windows/ffmpeg/build.sh"]
LINUX_FFMPEG = ["ci/ffmpeg/versions.json"]
W1_FILE = [boundary.W1_WORKFLOW]

#: Every case expects the trigger to resolve TRUE. That is the whole contract
#: now, so the expectation is not carried per case: a case that should not
#: trigger has no place in a positive-only filter's proof.
CASES: list[tuple[str, list[str], str]] = [
    ("retention-only", RETENTION_ONLY,
     "a retention-only diff crosses the dual-runner proof trigger"),
    ("retention-nested", [f"{SUBTREE}/tests/fixtures/case.json"],
     "including a nested path added under the subtree, so `**` crosses `/`"),
    ("retention-single-file", [f"{SUBTREE}/boundary.py"],
     "including a one-file retention diff"),
    ("windows-ffmpeg", WINDOWS_FFMPEG,
     "a ci/windows/ffmpeg/** change triggers it"),
    ("linux-ffmpeg", LINUX_FFMPEG,
     "a ci/ffmpeg/** change triggers it"),
    ("w1-workflow-file", W1_FILE,
     "a change to the W1 workflow file itself triggers it"),
    ("mixed-retention-and-build", RETENTION_ONLY + WINDOWS_FFMPEG,
     "retention plus a build-affecting change triggers it"),
    ("mixed-retention-and-linux", RETENTION_ONLY + LINUX_FFMPEG,
     "retention plus a ci/ffmpeg change triggers it"),
    ("rename-into-retention", ["ci/windows/ffmpeg/notes.py", f"{SUBTREE}/notes.py"],
     "a rename INTO the subtree triggers it on both halves"),
    ("rename-out-of-retention", [f"{SUBTREE}/contract.py", "ci/windows/ffmpeg/contract.py"],
     "a rename OUT of the subtree triggers it on both halves"),
    ("rename-within-retention", [f"{SUBTREE}/boundary.py", f"{SUBTREE}/ownership.py"],
     "a rename wholly inside the subtree still triggers it"),
    ("delete-inside-retention", [f"{SUBTREE}/registry-controls.sh"],
     "a deletion wholly inside the subtree triggers it"),
    ("build-inputs", ["ci/windows/build-inputs/install-oras.sh"],
     "the rest of ci/windows/** triggers it, as it always did"),
    ("adjacent-lookalike-path", ["ci/windows/runtime-retention-notes.md"],
     "a sibling whose name merely starts with the subtree name triggers it too"),
]

#: The exact D2 staging mutations. Under W1-A4-R1 these were the diffs that
#: could ingest the excluded subtree into a build while every declared
#: dependency check stayed green. None of them has to be RECOGNISED as staging
#: syntax any more. Each one only has to cross the trigger, because a proof that
#: runs over the ingested bytes is what makes the ingestion visible.
D2_STAGING_CASES: list[tuple[str, list[str], str]] = [
    ("d2-cp-recursive", ["ci/windows/ffmpeg/stage.sh"],
     "`cp -r ci/windows dst` lives in a build script that triggers the proof"),
    ("d2-tar-parent", ["ci/windows/ffmpeg/package.sh"],
     "a tar of the parent directory likewise"),
    ("d2-rsync-alternate-exclude", ["ci/windows/build-inputs/sync.sh"],
     "an rsync whose --exclude spelling differs likewise"),
    ("d2-python-rglob", ["ci/windows/build-inputs/bundle.py"],
     "a Path().rglob() walk likewise"),
    ("d2-retention-file-itself", [f"{SUBTREE}/accepted-runtime.json"],
     "and the retention file such a stage would ingest triggers it on its own"),
]

#: The retention workflow must still see its own subtree.
RETENTION_CASES: list[tuple[str, list[str], str]] = [
    ("retention-workflow-sees-retention-only", RETENTION_ONLY,
     "the retention workflow runs on a retention-only diff"),
    ("retention-workflow-sees-nested", [f"{SUBTREE}/tests/fixtures/case.json"],
     "including a nested path added under the subtree"),
]


def check_all(root: Path) -> list[boundary.Finding]:
    findings: list[boundary.Finding] = []
    w1 = workflow_paths(root, boundary.W1_WORKFLOW)
    findings += check_positive_only(w1, boundary.W1_WORKFLOW)
    if not w1:
        findings.append(boundary.Finding(
            "trigger.no-path-filter",
            f"{boundary.W1_WORKFLOW} declares no pull_request `paths:` filter at all",
        ))
    for name, changed, description in CASES + D2_STAGING_CASES:
        if not triggers(changed, w1):
            findings.append(boundary.Finding(
                "trigger.case-does-not-trigger",
                f"{name}: {changed} does not cross the W1 proof trigger — {description}",
            ))

    retention = workflow_paths(root, boundary.RETENTION_WORKFLOW)
    findings += check_positive_only(retention, boundary.RETENTION_WORKFLOW)
    for name, changed, description in RETENTION_CASES:
        if not triggers(changed, retention):
            findings.append(boundary.Finding(
                "trigger.retention-case-does-not-trigger",
                f"{name}: {changed} does not cross the retention trigger — {description}",
            ))
    return findings


def main() -> int:
    root = boundary.repo_root()
    w1 = workflow_paths(root, boundary.W1_WORKFLOW)
    retention = workflow_paths(root, boundary.RETENTION_WORKFLOW)

    print(f"W1 `paths:` filter: {w1}")
    for name, changed, description in CASES:
        actual = triggers(changed, w1)
        print(f"  {'OK' if actual else 'FAIL':<6} {name:<42} "
              f"{'triggers' if actual else 'DOES NOT TRIGGER'} — {description}")
    print("\nthe W1-A4-R1 D2 staging mutations, which now only have to trigger:")
    for name, changed, description in D2_STAGING_CASES:
        actual = triggers(changed, w1)
        print(f"  {'OK' if actual else 'FAIL':<6} {name:<42} "
              f"{'triggers' if actual else 'DOES NOT TRIGGER'} — {description}")
    print(f"\nretention `paths:` filter: {retention}")
    for name, changed, description in RETENTION_CASES:
        actual = triggers(changed, retention)
        print(f"  {'OK' if actual else 'FAIL':<6} {name:<42} "
              f"{'triggers' if actual else 'DOES NOT TRIGGER'} — {description}")

    findings = check_all(root)
    print()
    if findings:
        print(f"W1-A4 TRIGGER HARD STOP: {len(findings)} finding(s)", file=sys.stderr)
        for finding in findings:
            print(f"  FAIL [{finding.prop}] {finding.message}", file=sys.stderr)
        return 1
    print("the W1 proof trigger is positive-only and every retention change crosses it")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
