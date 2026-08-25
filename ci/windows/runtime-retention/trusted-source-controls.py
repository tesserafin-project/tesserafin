"""The trusted-source assertions, each proved load-bearing on its own (#236, W1-A4-R2).

`contract.assert_trusted_source` refuses publication from anything but a
meaningful revision of trusted master. W1-A4-R1 wrote its sha-shape test as

    not _BARE_SHA256.match(github_sha.lower()) and len(github_sha) != 40

which is three defects in one expression:

  * a 64-hex CONTENT digest satisfied the first conjunct, so a sha256 passed as
    a commit sha;
  * ANY 40-character string satisfied the second conjunct and made the whole
    conjunction false, so `zzzz…` (40 z's) passed;
  * `.lower()` normalised an uppercase sha into acceptance instead of refusing
    it.

The repair requires exactly 40 lowercase hexadecimal characters. That is the
easy half. The half worth controlling is that the sha SHAPE and the HEAD =
GITHUB_SHA equality are two independent assertions and neither is carrying the
other:

  * a well-shaped sha with a disagreeing HEAD must fail on TRUSTED-SOURCE-HEAD;
  * an agreeing HEAD with a malformed sha must fail on TRUSTED-SOURCE-SHA.

A single control that reds on both at once proves neither. So every control
names the property token it expects and a control that refuses for a different
reason is INERT, reported as such, and fails the suite — the same grading the
other suites use.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import contract

GOOD = "2622dd442c5ce68f04c8c43ae1d66fd4163ffcde"
OTHER = "83e23b9579404883c2d3e93f6f3ac8748061c618"

#: (name, description, github_sha, head_sha, expected property token or None)
CONTROLS: list[tuple[str, str, str, str, str | None]] = [
    ("01-well-formed-sha-and-matching-head",
     "a 40-character lowercase sha with a matching HEAD is accepted",
     GOOD, GOOD, None),

    ("02-uppercase-sha",
     "an uppercase sha is refused rather than normalised into acceptance",
     GOOD.upper(), GOOD.upper(), "TRUSTED-SOURCE-SHA"),

    ("03-sha256-content-digest",
     "a 64-hex content digest is not a commit sha",
     "b" * 64, "b" * 64, "TRUSTED-SOURCE-SHA"),

    ("04-too-short",
     "a 39-character sha is refused",
     "a" * 39, "a" * 39, "TRUSTED-SOURCE-SHA"),

    ("05-too-long",
     "a 41-character sha is refused",
     "a" * 41, "a" * 41, "TRUSTED-SOURCE-SHA"),

    ("06-non-hexadecimal",
     "40 characters that are not hexadecimal are refused; R1 accepted these",
     "z" * 40, "z" * 40, "TRUSTED-SOURCE-SHA"),

    ("07-empty",
     "an empty sha is refused",
     "", "", "TRUSTED-SOURCE-SHA"),

    ("08-abbreviated-sha",
     "a short sha is refused; an abbreviation is not a revision",
     GOOD[:12], GOOD[:12], "TRUSTED-SOURCE-SHA"),

    # ── the two assertions, proved independent ──────────────────────────────
    ("09-shape-valid-head-disagrees",
     "a WELL-FORMED sha whose HEAD stands elsewhere fails on HEAD, not on shape",
     GOOD, OTHER, "TRUSTED-SOURCE-HEAD"),

    ("10-head-agrees-shape-invalid",
     "an AGREEING HEAD with a malformed sha fails on shape, not on HEAD",
     "z" * 40, "z" * 40, "TRUSTED-SOURCE-SHA"),
]

#: The other three assertions, so a repair to the sha test cannot silently
#: disable one of its neighbours.
CONTEXT_CONTROLS: list[tuple[str, str, dict, str | None]] = [
    ("11-untrusted-repository", "publication from another repository is refused",
     {"repository": "someone-else/tesserafin"}, "TRUSTED-SOURCE-REPOSITORY"),
    ("12-untrusted-event", "publication from another event is refused",
     {"event_name": "pull_request"}, "TRUSTED-SOURCE-EVENT"),
    # `assert_trusted_ref` raises without a leading token, so this control
    # asserts its NAMED REASON instead. A refusal that says "a feature branch is
    # not trusted" is a different assertion from one that says "a pull request
    # ref carries unreviewed code", and reading either as "it refused" is how a
    # control stops proving its own property.
    ("13-untrusted-branch-ref", "publication from a feature branch is refused",
     {"github_ref": "refs/heads/feature/x"}, "a feature branch is not trusted"),
    ("14-untrusted-pull-request-ref", "publication from a pull request ref is refused",
     {"github_ref": "refs/pull/254/merge"}, "a pull request ref carries unreviewed code"),
    ("15-untrusted-tag-ref", "publication from a tag is refused",
     {"github_ref": "refs/tags/v1.0.0"}, "a tag can be created and moved without review"),
]


def _invoke(**overrides) -> list[str]:
    """Run the assertion, returning the property tokens it refused with."""
    call = {
        "repository": contract.TRUSTED_REPOSITORY,
        "github_ref": "refs/heads/master",
        "event_name": contract.TRUSTED_EVENT,
        "github_sha": GOOD,
        "head_sha": GOOD,
    }
    call.update(overrides)
    try:
        contract.assert_trusted_source(**call)
    except contract.ContractError as error:
        text = str(error)
        token = text.split(":", 1)[0].strip()
        # `assert_trusted_ref` has no leading token, so its named reason — the
        # parenthesised clause — is the property that control asserts.
        reason = text[text.rfind("(") + 1:text.rfind(")")] if text.endswith(")") else ""
        return [t for t in (token, reason) if t]
    return []


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    results = []
    failures = 0
    print("trusted-source controls")

    cases: list[tuple[str, str, dict, str | None]] = [
        (name, description, {"github_sha": sha, "head_sha": head}, expected)
        for name, description, sha, head, expected in CONTROLS
    ] + CONTEXT_CONTROLS

    for name, description, overrides, expected in cases:
        grade = "ERROR"
        detail = ""
        tokens: list[str] = []
        try:
            tokens = _invoke(**overrides)
            if expected is None and name.startswith("01"):
                grade, detail = ("PASS", "accepted, as it must be") if not tokens else \
                    ("GREEN", f"refused a legitimate revision: {tokens}")
            elif expected is None:
                grade, detail = ("RED", f"refused, naming {tokens[0]}") if tokens else \
                    ("GREEN", "ACCEPTED what must be refused")
            elif not tokens:
                grade, detail = "GREEN", "ACCEPTED what must be refused"
            elif expected in tokens:
                grade, detail = "RED", f"refused, naming {expected}"
            else:
                grade, detail = "INERT", f"refused, but naming {tokens} rather than {expected}"
        except Exception as error:  # noqa: BLE001 - an ERROR grade is the point
            grade, detail = "ERROR", f"{type(error).__name__}: {error}"

        if grade not in ("RED", "PASS"):
            failures += 1
        print(f"  {grade:<6} {name:<40} {detail}")
        results.append({"control": name, "property": description,
                        "expected": expected, "grade": grade,
                        "detail": detail, "found": tokens})

    # The independence claim, stated as a comparison rather than left implicit.
    shape_only = _invoke(github_sha="z" * 40, head_sha="z" * 40)
    head_only = _invoke(github_sha=GOOD, head_sha=OTHER)
    independent = shape_only == ["TRUSTED-SOURCE-SHA"] and head_only == ["TRUSTED-SOURCE-HEAD"]
    print(f"  {'OK' if independent else 'FAIL':<6} {'shape-and-head-are-independent':<40} "
          f"malformed-sha -> {shape_only}, disagreeing-head -> {head_only}")
    if not independent:
        failures += 1
    results.append({"control": "shape-and-head-are-independent",
                    "grade": "OK" if independent else "FAIL",
                    "shapeOnly": shape_only, "headOnly": head_only})

    if args.json:
        args.json.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    print()
    if failures:
        print(f"W1-A4 TRUSTED-SOURCE HARD STOP: {failures} control(s) did not reach their "
              f"property", file=sys.stderr)
        return 1
    print(f"all {len(cases)} trusted-source controls reached their named property, and the "
          f"sha shape and the HEAD equality are each load-bearing on their own")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
