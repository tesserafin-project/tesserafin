"""GitHub `paths:` semantics, and the cases the exclusion has to survive (#236, W1-A4-R1).

W1-A4 added an ordered negation to the W1 dual-runner workflow's `paths:` filter
so that a retention-only pull request does not start two five-hour metered native
Windows builds. That exclusion is only correct if it excludes EXACTLY the
retention subtree and nothing else, and "correct" here is a claim about GitHub's
matching rules, not about whether a substring appears in a string.

So the rules are implemented rather than approximated:

  * `*`  matches zero or more characters, never `/`;
  * `**` matches zero or more characters, including `/`;
  * `?`  matches exactly one character, never `/`;
  * everything else is literal;
  * `paths:` is an ALLOWLIST, so a file starts excluded;
  * patterns are evaluated IN ORDER and the LAST one that matches decides, which
    is what makes `- '!ci/windows/runtime-retention/**'` after `- 'ci/windows/**'`
    subtract rather than the two cancelling;
  * the workflow runs if ANY changed file ends up included.

That last-match-wins rule is the one a substring approximation gets wrong, and it
is exactly the rule the mixed diff depends on: a pull request touching both the
retention subtree and `ci/windows/ffmpeg/**` must still trigger, because the
ffmpeg file is included on its own regardless of what happens to the retention
file. A whole-set subtraction would get that case wrong in the dangerous
direction.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

import yaml

import boundary

SUBTREE = boundary.SUBTREE


def compile_pattern(pattern: str) -> re.Pattern[str]:
    """GitHub's glob, as a regex. Negation is handled by the caller."""
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
    """Whether `path` is included by an ordered `paths:` list. Last match wins."""
    verdict = False
    for pattern in patterns:
        negated = pattern.startswith("!")
        body = pattern[1:] if negated else pattern
        if compile_pattern(body).match(path):
            verdict = not negated
    return verdict


def triggers(changed: list[str], patterns: list[str]) -> bool:
    return any(included(path, patterns) for path in changed)


def workflow_paths(root: Path, workflow: str) -> list[str]:
    doc = yaml.safe_load((root / workflow).read_text(encoding="utf-8"))
    # `on:` is a YAML 1.1 boolean, so PyYAML gives the key back as True.
    triggers_block = doc.get("on", doc.get(True))
    pull_request = (triggers_block or {}).get("pull_request") or {}
    return list(pull_request.get("paths") or [])


# ── the cases ───────────────────────────────────────────────────────────────
RETENTION_ONLY = [f"{SUBTREE}/contract.py", f"{SUBTREE}/accepted-runtime.json"]
WINDOWS_FFMPEG = ["ci/windows/ffmpeg/build.sh"]
LINUX_FFMPEG = ["ci/ffmpeg/versions.json"]
W1_FILE = [boundary.W1_WORKFLOW]

CASES: list[tuple[str, list[str], bool, str]] = [
    ("retention-only", RETENTION_ONLY, False,
     "a retention-only diff does not start the metered dual-runner proof"),
    ("windows-ffmpeg", WINDOWS_FFMPEG, True,
     "a ci/windows/ffmpeg/** change still triggers it"),
    ("linux-ffmpeg", LINUX_FFMPEG, True,
     "a ci/ffmpeg/** change still triggers it"),
    ("w1-workflow-file", W1_FILE, True,
     "a change to the W1 workflow file itself still triggers it"),
    ("mixed-retention-and-build", RETENTION_ONLY + WINDOWS_FFMPEG, True,
     "retention plus a build-affecting change triggers it"),
    ("mixed-retention-and-linux", RETENTION_ONLY + LINUX_FFMPEG, True,
     "retention plus a ci/ffmpeg change triggers it"),
    ("rename-across-boundary-build-affecting",
     [f"{SUBTREE}/contract.py", "ci/windows/ffmpeg/contract.py"], True,
     "a rename out of the subtree into the build tree triggers it"),
    ("delete-inside-boundary-only", [f"{SUBTREE}/registry-controls.sh"], False,
     "a deletion wholly inside the subtree does not trigger it"),
    ("delete-across-boundary-build-affecting",
     [f"{SUBTREE}/build-twice.sh", "ci/windows/build-inputs/bundle.py"], True,
     "a deletion that also removes a build input triggers it"),
    ("nested-retention-path", [f"{SUBTREE}/tests/fixtures/case.json"], False,
     "a nested path under the subtree is excluded, so `**` crosses `/`"),
    ("adjacent-lookalike-path", ["ci/windows/runtime-retention-notes.md"], True,
     "a sibling whose name merely starts with the subtree name is NOT excluded"),
    ("build-inputs-still-included", ["ci/windows/build-inputs/install-oras.sh"], True,
     "the rest of ci/windows/** is untouched by the exclusion"),
]

#: The retention workflow must POLICE what the build workflow excludes.
RETENTION_CASES: list[tuple[str, list[str], bool, str]] = [
    ("retention-workflow-sees-retention-only", RETENTION_ONLY, True,
     "the retention workflow runs on a retention-only diff, so the subtree is never unvalidated"),
    ("retention-workflow-sees-nested", [f"{SUBTREE}/tests/fixtures/case.json"], True,
     "including a nested path added under the subtree"),
]


def check_ordering(patterns: list[str]) -> list[boundary.Finding]:
    """The negation must FOLLOW the positive pattern it subtracts from.

    Ordering is the whole mechanism. `!ci/windows/runtime-retention/**` placed
    BEFORE `ci/windows/**` matches nothing, because the later pattern re-includes
    every path it excluded — and the filter would silently go back to starting
    two five-hour builds while still looking, line by line, exactly as intended.
    """
    findings: list[boundary.Finding] = []
    negation = f"!{SUBTREE}/**"
    if negation not in patterns:
        findings.append(boundary.Finding(
            "pathfilter.negation-absent",
            f"the W1 `paths:` filter no longer carries {negation!r}",
        ))
        return findings
    negation_at = patterns.index(negation)
    for index, pattern in enumerate(patterns):
        if pattern.startswith("!"):
            continue
        if compile_pattern(pattern).match(f"{SUBTREE}/contract.py") and index > negation_at:
            findings.append(boundary.Finding(
                "pathfilter.negation-out-of-order",
                f"{pattern!r} at position {index} re-includes the subtree after the "
                f"negation at position {negation_at}; last match wins, so the exclusion "
                f"is inert",
            ))
    return findings


def main() -> int:
    root = boundary.repo_root()
    w1 = workflow_paths(root, boundary.W1_WORKFLOW)
    retention = workflow_paths(root, boundary.RETENTION_WORKFLOW)
    failures = 0

    print(f"W1 `paths:` filter: {w1}")
    for finding in check_ordering(w1):
        print(f"  FAIL   [{finding.prop}] {finding.message}")
        failures += 1

    for name, changed, expected, description in CASES:
        actual = triggers(changed, w1)
        ok = actual == expected
        failures += 0 if ok else 1
        print(f"  {'OK' if ok else 'FAIL':<6} {name:<42} "
              f"{'triggers' if actual else 'does not trigger'} — {description}")

    print(f"\nretention `paths:` filter: {retention}")
    for name, changed, expected, description in RETENTION_CASES:
        actual = triggers(changed, retention)
        ok = actual == expected
        failures += 0 if ok else 1
        print(f"  {'OK' if ok else 'FAIL':<6} {name:<42} "
              f"{'triggers' if actual else 'does not trigger'} — {description}")

    print()
    if failures:
        print(f"W1-A4 PATH FILTER HARD STOP: {failures} case(s) disagree with GitHub semantics",
              file=sys.stderr)
        return 1
    print("the exclusion subtracts exactly the retention subtree, the build workflow still "
          "triggers on every build-affecting change, and the retention workflow covers what "
          "the build workflow excludes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
