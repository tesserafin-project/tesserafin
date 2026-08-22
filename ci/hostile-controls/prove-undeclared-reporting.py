#!/usr/bin/env python3
"""The counter-control for the counter-control (#153-LTV-R7).

`cc-10` is the tenth grader counter-control: a mutation that fails one DECLARED deciding test and
nine UNDECLARED ones, whose grade must stay RED while the nine are reported by name. But a
counter-control only proves something if it can fail. R6 finding R6-1 was precisely a grade that was
RIGHT while the report that carried it was silent, so "cc-10 is green" is worth nothing until the
report is removed and cc-10 turns red.

This script runs cc-10 twice against the SAME tree:

    1. through `run.py` as committed        -> must pass, and must name the nine failures;
    2. through a copy of `run.py` whose ONE mutated line drops the undeclared failures from the
       classification (`unexpected = []`)   -> must fail, must say the reporting FAILED, and must
       not print a single one of the nine names.

The second run must still grade the control RED - if it did not, the negative control would be
proving that the mutation broke the CLASSIFICATION, which is not the property under test.

Usage: ci/hostile-controls/prove-undeclared-reporting.py [--keep]
Exit status: 0 only when both halves held.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
CONTROL = "cc-10-a-declared-red-with-undeclared-collateral-is-reported-as-such"

ANCHOR = "    unexpected = sorted(n for n in failures if not any(covers(d, n) for d in declared))\n"
REMOVAL = ANCHOR + "    unexpected = []  # NEGATIVE CONTROL: the #153-LTV-R6 R6-1 reporting, removed\n"


def stage(workdir, mutate):
    """A private copy of the runner and of cc-10 alone, optionally with the reporting removed."""
    root = os.path.join(workdir, "mutated" if mutate else "committed")
    os.makedirs(root)
    source = os.path.join(HERE, "run.py")
    with open(source, encoding="utf-8") as handle:
        body = handle.read()
    if mutate:
        if body.count(ANCHOR) != 1:
            raise SystemExit(f"the negative control's anchor occurs {body.count(ANCHOR)} times in "
                             "run.py, expected exactly 1 - the mutation is no longer valid")
        body = body.replace(ANCHOR, REMOVAL)
    with open(os.path.join(root, "run.py"), "w", encoding="utf-8") as handle:
        handle.write(body)

    with open(os.path.join(HERE, "counter-controls.json"), encoding="utf-8") as handle:
        counter = json.load(handle)
    only = [c for c in counter["controls"] if c["id"] == CONTROL]
    if len(only) != 1:
        raise SystemExit(f"{CONTROL} is not in counter-controls.json")
    with open(os.path.join(root, "counter-controls.json"), "w", encoding="utf-8") as handle:
        json.dump({"controls": only}, handle, indent=2)
    # run.py loads both files from its own directory; the manifest is not replayed here but must be
    # readable, so the committed one is linked rather than copied.
    os.symlink(os.path.join(HERE, "manifest.json"), os.path.join(root, "manifest.json"))
    return root, only[0]["expectUndeclaredFailures"]


def replay(root, out):
    done = subprocess.run([sys.executable, os.path.join(root, "run.py"), "--self-test", "--out", out],
                          cwd=HERE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          text=True, check=False)
    print(done.stdout, flush=True)
    with open(out, encoding="utf-8") as handle:
        payload = json.load(handle)
    return done.returncode, done.stdout, payload["counterControls"][0]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--keep", action="store_true")
    args = parser.parse_args()

    workdir = tempfile.mkdtemp(prefix="ltv-undeclared-reporting-")
    failures = []
    try:
        root, names = stage(workdir, mutate=False)
        print("=== half 1: run.py as committed - cc-10 must hold and must name the collateral ===",
              flush=True)
        code, log, record = replay(root, os.path.join(workdir, "committed.json"))
        if code != 0:
            failures.append(f"half 1: the committed runner exited {code}, expected 0")
        if record["grade"] != "RED":
            failures.append(f"half 1: cc-10 graded {record['grade']}, expected RED")
        if record.get("undeclaredReportingHeld") is not True:
            failures.append("half 1: the undeclared-failure reporting did not hold")
        missing = [n for n in names if n not in log]
        if missing:
            failures.append(f"half 1: {len(missing)} undeclared failure name(s) never reached the "
                            f"console, e.g. {missing[0]}")

        root, names = stage(workdir, mutate=True)
        print("\n=== half 2: the reporting removed - cc-10 must FAIL, and stay RED ===", flush=True)
        code, log, record = replay(root, os.path.join(workdir, "mutated.json"))
        if code == 0:
            failures.append("half 2: the runner exited 0 with the undeclared reporting removed - "
                            "cc-10 cannot detect the defect it exists for")
        if record["grade"] != "RED":
            failures.append(f"half 2: cc-10 graded {record['grade']}, expected RED - the mutation "
                            "changed the CLASSIFICATION, not only the report")
        if record.get("undeclaredReportingHeld") is not False:
            failures.append("half 2: the runner did not record the reporting as FAILED")
        present = [n for n in names if n in log]
        if present:
            failures.append(f"half 2: {len(present)} undeclared failure name(s) were still printed, "
                            f"e.g. {present[0]}")
    finally:
        if not args.keep:
            shutil.rmtree(workdir, ignore_errors=True)
        else:
            print(f"\nkept: {workdir}", flush=True)

    print("")
    if failures:
        print(f"the negative control did NOT hold ({len(failures)} problem(s)):")
        for problem in failures:
            print(f"  - {problem}")
        return 1
    print("the negative control HELD: cc-10 passes on the committed runner and fails, still RED, "
          "the moment the undeclared failures stop being reported.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
