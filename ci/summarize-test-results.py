#!/usr/bin/env python3
"""Summarize TRX durations and reject a suite that did not execute any tests."""

import html
import os
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def seconds(duration):
    """Convert a TRX TimeSpan (hours:minutes:seconds) to seconds."""
    hours, minutes, secs = duration.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(secs)


def summarize(directory):
    """Return a Markdown report, failing if discovery selected no runnable test."""
    files = sorted(Path(directory).rglob("*.trx"))
    if not files:
        raise ValueError("No TRX results found; the suite did not report a test run.")

    results = []
    for path in files:
        root = ET.parse(path).getroot()
        results.extend(root.findall(".//{*}UnitTestResult"))

    if not any(result.get("outcome") in ("Passed", "Failed") for result in results):
        raise ValueError("No tests executed; check the suite target and filter.")

    counts = {}
    for result in results:
        outcome = result.get("outcome", "Unknown")
        counts[outcome] = counts.get(outcome, 0) + 1

    report = [
        "### Test durations",
        "",
        ", ".join(f"{outcome}: {count}" for outcome, count in sorted(counts.items())),
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


if __name__ == "__main__":
    report = summarize(sys.argv[1])
    print(report)
    if summary_path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary_path, "a", encoding="utf-8") as summary:
            summary.write(report)
