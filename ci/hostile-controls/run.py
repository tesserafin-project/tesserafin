#!/usr/bin/env python3
"""Replay the Live TV hostile controls from ci/hostile-controls/manifest.json (#153-LTV-R1).

Every control mutates a real production line, builds, is graded by the gate that names the
property it breaks, and is reverted. A mutation that does not turn its gate red is a mutation
whose property nothing actually protects.

LTV-R0 finding 4 is what this closes: the #153-LTV-S0 and #153-LTV-S1 rosters ran from python
harnesses that lived outside the tree, hardcoded one worktree path, and mutated it in place.
Nothing a reviewer clones could replay them.

Usage:
    ci/hostile-controls/run.py [--commit <rev>] [--out <file>] [--rig <script>] [id ...]

The runner creates its OWN git worktree from <rev> (default HEAD) and never writes to the
worktree it was invoked from. Controls run serially, in the foreground.

Exit status: 0 only when every selected control matched its expectation and every tree was
restored byte-identically.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

MANIFEST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "manifest.json")

# The test summary on some hosts is localised. Both spellings are matched, and a run with NO
# summary at all - a build or discovery failure - is an ERROR for the caller to attribute, never
# a green. Matching "Failed" as a literal against a localised runner is how a green run silently
# grades as failed.
_SUMMARY = re.compile(
    r"(?:échec|Failed!?)\s*[:!]?\s*(\d+),\s*(?:réussite|Passed)\s*:\s*(\d+)"
    r"|(?:Failed|Échoué)!?\s*-\s*(?:échec|failed)\s*:\s*(\d+),\s*(?:réussite|passed)\s*:\s*(\d+)",
    re.IGNORECASE)


def run(cmd, cwd, timeout, env=None):
    merged = dict(os.environ)
    merged.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    merged.setdefault("DOTNET_NOLOGO", "1")
    if env:
        merged.update(env)
    try:
        done = subprocess.run(
            cmd, cwd=cwd, timeout=timeout, env=merged,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=False)
        return done.returncode, done.stdout
    except subprocess.TimeoutExpired as expired:
        return "TIMEOUT", (expired.stdout or "")


def tree_hash(worktree):
    """The worktree's real tree.

    `git add -A -n` is a DRY RUN: it stages nothing, so write-tree would return the same hash for
    a mutated tree as for a clean one and "restored byte-identically" would be true of nothing.
    The index has to be updated for real. The S1 harness shipped with exactly that defect.
    """
    code, out = run(["git", "add", "-A"], worktree, 300)
    if code != 0:
        return "ERROR:add:" + out.strip()[:160]
    code, out = run(["git", "write-tree"], worktree, 120)
    return out.strip() if code == 0 else "ERROR:" + out.strip()[:160]


def revert(worktree):
    # Hard, because tree_hash() stages what it measures; `git checkout --` would leave the
    # mutation in the index.
    run(["git", "reset", "--hard", "HEAD"], worktree, 300)
    run(["git", "clean", "-fdq"], worktree, 300)


def apply_mutation(worktree, mutation):
    path = os.path.join(worktree, mutation["file"])
    if not os.path.exists(path):
        return f"{mutation['file']} does not exist"
    with open(path, encoding="utf-8") as handle:
        body = handle.read()
    found = body.count(mutation["find"])
    if found != 1:
        return f"anchor occurs {found} times in {mutation['file']}, expected exactly 1"
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(body.replace(mutation["find"], mutation["replace"]))
    return None


def build(worktree, project, timeout):
    # The build ALWAYS runs before the gate. A gate that consults a previously compiled dll grades
    # a mutation the code under test never saw, and comes back INERT: the S1 harness graded
    # `remove-the-propagator-call` INERT for exactly that reason before it was fixed.
    return run(
        ["dotnet", "build", project, "-p:UseSharedCompilation=false", "-nodereuse:false",
         "--nologo", "-v:q"],
        worktree, timeout)


def gate_dotnet_test(worktree, gate, timeout):
    code, log = build(worktree, gate["project"], timeout)
    if code == "TIMEOUT":
        return None, "TIMEOUT"
    if code != 0:
        return None, "build failed\n" + log[-3000:]

    code, log = run(
        ["dotnet", "test", gate["project"], "--no-build", "--nologo", "--filter", gate["filter"]],
        worktree, timeout)
    if code == "TIMEOUT":
        return None, "TIMEOUT"

    match = _SUMMARY.search(log)
    if match is None:
        return None, "no test summary: the run did not happen\n" + log[-3000:]
    groups = [g for g in match.groups() if g is not None]
    failures, passes = int(groups[0]), int(groups[1])
    if failures == 0 and passes == 0:
        return None, "the filter matched no tests\n" + log[-2000:]
    return failures == 0 and code == 0, f"failed={failures} passed={passes}\n" + log[-1500:]


def gate_source(worktree, gate, timeout):
    """A source assertion, for a property no behavioural test can separate.

    Used where two variants emit identical bytes for every reachable input. It is recorded as a
    source-level grade rather than allowed to pass as INERT.
    """
    del timeout
    path = os.path.join(worktree, gate["file"])
    with open(path, encoding="utf-8") as handle:
        body = handle.read()
    present = re.search(gate["absentPattern"], body) is not None
    return not present, f"pattern {'present' if present else 'absent'} in {gate['file']}"


def gate_inventory(worktree, gate, timeout):
    code, log = run(["bash", gate["script"]], worktree, timeout)
    if code == "TIMEOUT":
        return None, "TIMEOUT"
    return code == 0, log[-1500:]


def gate_rig(worktree, gate, timeout, rig):
    if not rig:
        return "NO-RIG", (
            f"scenario '{gate['scenario']}' needs a live rig; pass --rig <script>. "
            f"Requires: {'; '.join(gate.get('requires', []))}")
    code, log = run([rig, gate["scenario"], worktree], os.path.dirname(rig) or ".", timeout)
    if code == "TIMEOUT":
        return None, "TIMEOUT"
    if code == 2:
        # The adapter could not decide - the mutated tree did not build, or it produced no
        # evidence. That is an ERROR to attribute, never a RED: grading a build failure as a
        # proven property is exactly the false green this whole runner exists to prevent.
        return None, "the rig could not decide (exit 2)\n" + log[-2000:]
    return code == 0, log[-2000:]


def evaluate(worktree, gate, timeout, rig):
    kind = gate["kind"]
    if kind == "dotnet-test":
        return gate_dotnet_test(worktree, gate, timeout)
    if kind == "source":
        return gate_source(worktree, gate, timeout)
    if kind == "inventory":
        return gate_inventory(worktree, gate, timeout)
    if kind == "rig":
        return gate_rig(worktree, gate, timeout, rig)
    raise SystemExit(f"unknown gate kind '{kind}'")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--commit", default="HEAD")
    parser.add_argument("--out", default=None)
    parser.add_argument("--rig", default=None, help="script run as <script> <scenario> <worktree>")
    parser.add_argument("--keep", action="store_true", help="do not delete the temporary worktree")
    parser.add_argument("ids", nargs="*")
    args = parser.parse_args()

    with open(MANIFEST, encoding="utf-8") as handle:
        manifest = json.load(handle)

    selected = [c for c in manifest["controls"] if not args.ids or c["id"] in args.ids]
    if not selected:
        raise SystemExit("no control matched")

    source = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()
    commit = subprocess.run(
        ["git", "rev-parse", args.commit], cwd=source, capture_output=True, text=True, check=True).stdout.strip()

    worktree = tempfile.mkdtemp(prefix="ltv-hostile-controls-")
    os.rmdir(worktree)
    subprocess.run(["git", "worktree", "add", "--detach", worktree, commit], cwd=source, check=True)
    print(f"commit {commit}\nworktree {worktree}", flush=True)

    results = []
    try:
        revert(worktree)
        baseline = tree_hash(worktree)
        print(f"baseline tree {baseline}", flush=True)

        for control in selected:
            started = time.time()
            record = {"id": control["id"], "stage": control["stage"], "status": control["status"],
                      "expect": control["expect"], "property": control["property"]}
            if "note" in control:
                record["note"] = control["note"]

            failure = None
            for mutation in control["mutations"]:
                failure = apply_mutation(worktree, mutation)
                if failure:
                    break

            if failure:
                record["grade"] = "ERROR"
                record["detail"] = failure
            else:
                outcome, detail = evaluate(worktree, control["gate"], control["timeoutSeconds"], args.rig)
                record["detail"] = detail
                if outcome == "NO-RIG":
                    record["grade"] = "SKIPPED-NO-RIG"
                elif outcome is None:
                    record["grade"] = "HUNG" if detail == "TIMEOUT" else "ERROR"
                elif control["expect"] == "PASS":
                    record["grade"] = "PASS" if outcome else "ERROR"
                else:
                    record["grade"] = "RED" if not outcome else "INERT"

            revert(worktree)
            restored = tree_hash(worktree)
            record["restoredByteIdentically"] = restored == baseline
            if not record["restoredByteIdentically"]:
                record["grade"] = "ERROR"
                record["detail"] = f"tree {restored} != baseline {baseline}; {record.get('detail')}"
            record["seconds"] = round(time.time() - started, 1)

            print(f"{record['grade']:15} {record['id']:48} {record['seconds']:7.1f}s  "
                  f"{str(record.get('detail')).splitlines()[0][:90] if record.get('detail') else ''}",
                  flush=True)
            results.append(record)

            if args.out:
                with open(args.out, "w", encoding="utf-8") as handle:
                    json.dump({"commit": commit, "baselineTree": baseline, "results": results},
                              handle, indent=2)
    finally:
        if not args.keep:
            subprocess.run(["git", "worktree", "remove", "--force", worktree], cwd=source, check=False)
            shutil.rmtree(worktree, ignore_errors=True)

    grades = [r["grade"] for r in results]
    print("")
    for grade in ("PASS", "RED", "INERT", "ERROR", "HUNG", "SKIPPED-NO-RIG"):
        print(f"{grades.count(grade):3} {grade}")

    matched = all(
        r["grade"] == r["expect"] and r["restoredByteIdentically"]
        for r in results if r["grade"] != "SKIPPED-NO-RIG")
    unresolved = grades.count("SKIPPED-NO-RIG")
    if unresolved:
        print(f"\n{unresolved} control(s) were NOT graded: they need a rig. This run is incomplete.")
    return 0 if matched and not unresolved else 1


if __name__ == "__main__":
    sys.exit(main())
