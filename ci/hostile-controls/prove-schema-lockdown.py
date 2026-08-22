#!/usr/bin/env python3
"""The eight hostile controls that hold the closed schema shut (#153-LTV-R9).

#153-LTV-R8 found the roster's collateral gate switchable: an ORDINARY roster line that broke nine
tests it did not declare failed the run (exit 1) without a flag and passed it (exit 0) with
`allowUndeclaredFailures: true`. The boolean is gone. What replaces it is a schema that is CLOSED in
both directions and different in the two documents, and a schema is only closed if trying to open it
is proven to fail. That is this file. It is the permanent AFTER half of the R8 before/after pair:
cases 1 and 2 replay the exact line that used to exit 0.

The eight situations, all mandated by #153-LTV-R9 step 3:

    1  an ordinary ROSTER line carrying `allowUndeclaredFailures: true`   -> ERROR
    2  the same line carrying `allowUndeclaredFailures: false`            -> ERROR
    3  an ordinary ROSTER line carrying `expectUndeclaredFailures`        -> ERROR
    4  an ordinary ROSTER line carrying any unknown key at all            -> ERROR
    5  `expectUndeclaredFailures` moved off CC10 onto another AUTOTEST    -> ERROR
    6  CC10's list with one name removed                                  -> the autotest FAILS
    7  CC10's list with one name added                                    -> the autotest FAILS
    8  CC10's list with one name perturbed                                -> the autotest FAILS

Cases 1 to 5 must cost NOTHING: an already-invalid schema is refused before a mutation is applied
and before anything is compiled, so each of those runs is asserted to have built nothing. Cases 6 to
8 are oracle failures rather than schema failures - the list is well-formed, it simply is not what
the run produced - so they must really run CC10 and really compare.

Usage: ci/hostile-controls/prove-schema-lockdown.py [--keep]
Exit status: 0 only when all eight held.
"""
import argparse
import copy
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
CC10 = "cc-10-a-declared-red-with-undeclared-collateral-is-reported-as-such"


def load(name):
    with open(os.path.join(HERE, name), encoding="utf-8") as handle:
        return json.load(handle)


def counter_control(cid):
    only = [c for c in load("counter-controls.json")["controls"] if c["id"] == cid]
    if len(only) != 1:
        raise SystemExit(f"{cid} is not in counter-controls.json")
    return copy.deepcopy(only[0])


def ordinary_roster_line():
    """The R8 line itself: an ordinary production control whose mutation breaks nine undeclared
    tests. Under R8 this line exited 0 the moment it declared the opt-out. Cases 1 to 4 perturb it.
    """
    cc10 = counter_control(CC10)
    return {
        "id": "r9-lockdown-an-ordinary-roster-line",
        "stage": "R9",
        "status": "NEW",
        "expect": "RED",
        "property": "the #153-LTV-R8 line: an ordinary roster control with undeclared collateral",
        "timeoutSeconds": cc10["timeoutSeconds"],
        "gate": cc10["gate"],
        "mutations": cc10["mutations"],
    }


def stage(workdir, name, manifest_controls=None, counter_controls=None):
    root = os.path.join(workdir, name)
    os.makedirs(root)
    shutil.copy2(os.path.join(HERE, "run.py"), os.path.join(root, "run.py"))

    manifest = load("manifest.json")
    if manifest_controls is not None:
        manifest["controls"] = manifest_controls
    with open(os.path.join(root, "manifest.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=2)

    counter = {"controls": load("counter-controls.json")["controls"]
               if counter_controls is None else counter_controls}
    with open(os.path.join(root, "counter-controls.json"), "w", encoding="utf-8") as handle:
        json.dump(counter, handle, indent=2)
    return root


def replay(root, out, self_test):
    mode = "--self-test" if self_test else "--no-self-test"
    done = subprocess.run([sys.executable, os.path.join(root, "run.py"), mode, "--out", out],
                          cwd=HERE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          text=True, check=False)
    payload = {}
    if os.path.exists(out):
        with open(out, encoding="utf-8") as handle:
            payload = json.load(handle)
    return done.returncode, done.stdout, payload


def roster_case(workdir, tag, perturbation, expected_in_reason):
    """Cases 1 to 4: one ordinary roster line, perturbed, must ERROR before anything is built."""
    row = ordinary_roster_line()
    row.update(perturbation)
    root = stage(workdir, tag, manifest_controls=[row])
    code, log, payload = replay(root, os.path.join(workdir, f"{tag}.json"), self_test=False)
    print(f"--- {tag} ---\n{log}", flush=True)

    problems = []
    if code == 0:
        problems.append(f"{tag}: the runner exited 0; the schema is not closed against it")
    rows = payload.get("results") or []
    grades = [r.get("grade") for r in rows]
    if grades != ["ERROR"]:
        problems.append(f"{tag}: graded {grades}, expected exactly ['ERROR']")
    reason = (rows[0].get("headline") if rows else "") or ""
    if expected_in_reason not in reason:
        problems.append(f"{tag}: the ERROR does not name '{expected_in_reason}': {reason!r}")
    # An already-invalid schema costs nothing: no worktree was created, so nothing was mutated and
    # nothing was compiled.
    if "worktree /" in log:
        problems.append(f"{tag}: a worktree was created for a control whose schema is already "
                        "invalid; the refusal is not free")
    if "baseline tree" in log:
        problems.append(f"{tag}: the run reached the baseline stage before refusing the schema")
    return problems


def autotest_case(workdir, tag, controls, expect_grade, expect_reporting_held,
                  expect_collateral, expected_in_reason=None):
    """Cases 5 to 8: a perturbed AUTOTEST document, replayed on its own."""
    root = stage(workdir, tag, counter_controls=controls)
    code, log, payload = replay(root, os.path.join(workdir, f"{tag}.json"), self_test=True)
    print(f"--- {tag} ---\n{log}", flush=True)

    problems = []
    if code == 0:
        problems.append(f"{tag}: the runner exited 0; the perturbation was not caught")
    if payload.get("selfTest") != "FAILED":
        problems.append(f"{tag}: self-test reported {payload.get('selfTest')!r}, expected 'FAILED'")
    rows = payload.get("counterControls") or []
    if len(rows) != 1:
        problems.append(f"{tag}: {len(rows)} autotest row(s) ran, expected 1")
        return problems
    row = rows[0]
    if row.get("grade") != expect_grade:
        problems.append(f"{tag}: graded {row.get('grade')!r}, expected {expect_grade!r}")
    if row.get("undeclaredReportingHeld") is not expect_reporting_held:
        problems.append(f"{tag}: undeclaredReportingHeld is "
                        f"{row.get('undeclaredReportingHeld')!r}, expected "
                        f"{expect_reporting_held!r}")
    kept = [r["id"] for r in payload.get("undeclaredFailureRows") or []]
    if expect_collateral and kept != [CC10]:
        problems.append(f"{tag}: the row kept its collateral exemption; undeclaredFailureRows "
                        f"is {kept}, expected ['{CC10}']")
    if not expect_collateral and kept:
        problems.append(f"{tag}: undeclaredFailureRows is {kept}, expected []")
    if expected_in_reason and expected_in_reason not in (row.get("headline") or ""):
        problems.append(f"{tag}: the ERROR does not name '{expected_in_reason}': "
                        f"{row.get('headline')!r}")
    return problems


def perturbed_cc10(change):
    cc10 = counter_control(CC10)
    cc10["expectUndeclaredFailures"] = change(list(cc10["expectUndeclaredFailures"]))
    return [cc10]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--keep", action="store_true")
    args = parser.parse_args()

    workdir = tempfile.mkdtemp(prefix="ltv-schema-lockdown-")
    failures = []
    try:
        # 1 and 2: the R8 escape hatch, at both of its values.
        failures += roster_case(workdir, "case-1-opt-out-true",
                                {"allowUndeclaredFailures": True}, "allowUndeclaredFailures")
        failures += roster_case(workdir, "case-2-opt-out-false",
                                {"allowUndeclaredFailures": False}, "allowUndeclaredFailures")
        # 3: naming the collateral does not buy a roster line the exemption either.
        failures += roster_case(workdir, "case-3-expect-list-on-a-roster-line",
                                {"expectUndeclaredFailures": ["Some.Test.Name"]},
                                "expectUndeclaredFailures")
        # 4: the schema is closed, not merely closed against the two keys R8 was about.
        failures += roster_case(workdir, "case-4-an-unknown-key",
                                {"someKeyNothingReads": "anything at all"},
                                "someKeyNothingReads")

        # 5: the exemption is CC10's alone, inside the autotests.
        other = counter_control("cc-1-a-real-deciding-test-broken-is-RED")
        other["expectUndeclaredFailures"] = ["Some.Test.Name"]
        failures += autotest_case(workdir, "case-5-expect-list-moved-off-cc-10", [other],
                                  expect_grade="ERROR", expect_reporting_held=None,
                                  expect_collateral=False,
                                  expected_in_reason="expectUndeclaredFailures is allowed on")

        # 6, 7, 8: the list is the ORACLE. It must match the observed set exactly, and a row whose
        # list does not match also loses the exemption that list is what grants.
        failures += autotest_case(workdir, "case-6-list-amputated",
                                  perturbed_cc10(lambda names: names[:-1]),
                                  expect_grade="RED", expect_reporting_held=False,
                                  expect_collateral=True)
        failures += autotest_case(workdir, "case-7-list-with-an-extra-name",
                                  perturbed_cc10(lambda names: names + [
                                      "Tesserafin.Api.Tests.LiveTvSegmentOwnership."
                                      "ActiveEncodingsOwnershipTests.ThisOneNeverFailed"]),
                                  expect_grade="RED", expect_reporting_held=False,
                                  expect_collateral=True)
        failures += autotest_case(workdir, "case-8-a-perturbed-name",
                                  perturbed_cc10(lambda names: [names[0][:-1] + "X"] + names[1:]),
                                  expect_grade="RED", expect_reporting_held=False,
                                  expect_collateral=True)
    finally:
        if not args.keep:
            shutil.rmtree(workdir, ignore_errors=True)
        else:
            print(f"\nkept: {workdir}", flush=True)

    print("")
    if failures:
        print(f"the schema lockdown did NOT hold ({len(failures)} problem(s)):")
        for problem in failures:
            print(f"  - {problem}")
        return 1
    print("the schema lockdown HELD on all eight situations: the roster has no opt-out at any "
          "value, its schema is closed to unknown keys, and CC10's list is both the only "
          "permission and an exact oracle.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
