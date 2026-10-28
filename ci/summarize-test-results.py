#!/usr/bin/env python3
"""Summarize TRX results and reject a suite that silently ran nothing.

`dotnet test` exits 0 when a filter matches no test ("No test matches the given
testcase filter"), so a renamed project, namespace or filter would otherwise
turn a required check green without running a single test.

Usage: summarize-test-results.py RESULTS_DIR [--require FULLY.QUALIFIED.Name ...]

Every --require test must have executed (Passed or Failed): a test that is
absent, or reported NotExecuted/skipped, fails this script.
"""

import argparse
import html
import os
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

EXECUTED = ("Passed", "Failed")


def seconds(duration):
    """Convert a TRX TimeSpan (hours:minutes:seconds) to seconds."""
    hours, minutes, secs = duration.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(secs)


def load(directory):
    """Return every UnitTestResult under the directory, failing if no TRX exists."""
    files = sorted(Path(directory).rglob("*.trx"))
    if not files:
        raise ValueError(f"No TRX results under {directory}; the suite did not report a test run.")

    results = []
    for path in files:
        results.extend(ET.parse(path).getroot().findall(".//{*}UnitTestResult"))
    return results


def matches(test_name, required):
    """A theory row is reported as `Name(arg: value)`; it counts for `Name`."""
    return test_name == required or test_name.startswith(required + "(")


def check(results, required):
    """Return the list of problems that must fail the suite."""
    problems = []
    if not any(result.get("outcome") in EXECUTED for result in results):
        problems.append("No tests executed; check the suite target and filter.")

    for name in required:
        outcomes = {r.get("outcome") for r in results if matches(r.get("testName", ""), name)}
        if not outcomes & set(EXECUTED):
            seen = ", ".join(sorted(o or "Unknown" for o in outcomes)) or "absent"
            problems.append(f"Required test {name} did not execute ({seen}).")
    return problems


def summarize(results):
    """Return a Markdown report of outcome counts and the slowest test cases."""
    counts = {}
    for result in results:
        outcome = result.get("outcome", "Unknown")
        counts[outcome] = counts.get(outcome, 0) + 1

    report = [
        "### Test durations",
        "",
        ", ".join(f"{outcome}: {count}" for outcome, count in sorted(counts.items())) or "No results",
        "",
        "The 20 slowest test cases (test time, excluding build and fixture setup/teardown):",
        "",
        "| Seconds | Outcome | Test |",
        "| ---: | --- | --- |",
    ]
    timed = [(seconds(result.get("duration", "0:0:0")), result) for result in results]
    for duration, result in sorted(timed, key=lambda entry: entry[0], reverse=True)[:20]:
        name = html.escape(result.get("testName", "Unknown")).replace("|", "\\|")
        name = " ".join(name.splitlines())
        report.append(f"| {duration:.3f} | {result.get('outcome', 'Unknown')} | {name} |")

    return "\n".join(report) + "\n"


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("directory")
    parser.add_argument("--require", action="append", default=[], metavar="TEST")
    args = parser.parse_args(argv)

    try:
        results = load(args.directory)
    except ValueError as error:
        print(f"::error::{error}")
        return 1

    report = summarize(results)
    print(report)
    if summary_path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary_path, "a", encoding="utf-8") as summary:
            summary.write(report)

    problems = check(results, args.require)
    for problem in problems:
        print(f"::error::{problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
