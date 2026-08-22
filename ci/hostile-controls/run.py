#!/usr/bin/env python3
"""Replay the Live TV hostile controls from ci/hostile-controls/manifest.json (#153-LTV-R1).

Every control mutates a real production line, builds, is graded by the gate that names the
property it breaks, and is reverted. A mutation that does not turn its gate red is a mutation
whose property nothing actually protects.

LTV-R0 finding 4 is what this closes: the #153-LTV-S0 and #153-LTV-S1 rosters ran from python
harnesses that lived outside the tree, hardcoded one worktree path, and mutated it in place.
Nothing a reviewer clones could replay them.

#153-LTV-R5 finding F8 is what the GRADER half closes. Until R5 the `expectedTests` array that
every `dotnet-test` control declares was never read: `grep -n expectedTests run.py` returned
nothing, and a control naming a test that does not exist still graded RED off a localised stdout
summary line. A RED is now decided from a structured TRX result and only when a DECLARED test
really ran and really failed. Text on stdout can no longer decide anything.

Usage:
    ci/hostile-controls/run.py [--commit <rev>] [--out <file>] [--rig <script>]
                               [--self-test | --no-self-test] [id ...]

The runner creates its OWN git worktree from <rev> (default HEAD) and never writes to the
worktree it was invoked from. Controls run serially, in the foreground.

Unless `--no-self-test` is passed, a full roster run first replays
ci/hostile-controls/counter-controls.json — eight situations whose classification is mandated —
and refuses to report a roster result if any of them is classified differently. A roster whose
grader has not been proven on that table is not evidence.

Exit status: 0 only when the self-test held, every selected control matched its expectation, and
every tree was restored byte-identically.
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
import traceback
import xml.etree.ElementTree as ElementTree

HERE = os.path.dirname(os.path.abspath(__file__))
MANIFEST = os.path.join(HERE, "manifest.json")
COUNTER_MANIFEST = os.path.join(HERE, "counter-controls.json")

TRX_NS = {"trx": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# Every verdict a gate may return. GREEN means the gate HELD - the property the control attacks
# was not broken by the mutation. It is the roster's INERT and the positive controls' PASS.
GREEN, RED, ERROR, HUNG, NO_RIG = "GREEN", "RED", "ERROR", "HUNG", "NO-RIG"


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
    # mutation in the index. It also restores mtimes to "now", so the next build cannot skip a
    # rebuild and grade the PREVIOUS control's compiled mutation (an R4 Phase 2 rig defect).
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


def validate_schema(control):
    """The control's own shape, checked before it is allowed to decide anything.

    An invalid control is an ERROR, never a grade: a control that cannot say which test decides it
    has no claim to make about the tree. Only `dotnet-test` carries `expectedTests` - the source,
    inventory and rig gates have no test list to validate and must not be failed for lacking one.
    """
    for field in ("id", "stage", "status", "expect", "property", "timeoutSeconds", "gate", "mutations"):
        if field not in control:
            return f"schema: control is missing '{field}'"
    gate = control["gate"]
    kind = gate.get("kind")
    if kind not in ("dotnet-test", "source", "inventory", "rig"):
        return f"schema: unknown gate kind '{kind}'"
    if control["expect"] not in ("PASS", "RED", "INERT", "ERROR", "HUNG"):
        return f"schema: unknown expectation '{control['expect']}'"

    if kind != "dotnet-test":
        if "expectedTests" in gate:
            return f"schema: a '{kind}' gate declares expectedTests, which nothing can validate"
        return None

    for field in ("project", "filter"):
        if not gate.get(field):
            return f"schema: a dotnet-test gate is missing '{field}'"
    declared = gate.get("expectedTests")
    if declared is None:
        return "schema: a dotnet-test gate must declare expectedTests"
    if not isinstance(declared, list) or not declared:
        return "schema: expectedTests must be a non-empty list"
    if any(not isinstance(name, str) or not name.strip() for name in declared):
        return "schema: every expectedTests entry must be a non-empty string"
    if len(set(declared)) != len(declared):
        return "schema: expectedTests contains a duplicate"
    return None


def build(worktree, project, timeout):
    # The build ALWAYS runs before the gate. A gate that consults a previously compiled dll grades
    # a mutation the code under test never saw, and comes back INERT: the S1 harness graded
    # `remove-the-propagator-call` INERT for exactly that reason before it was fixed.
    return run(
        ["dotnet", "build", project, "-p:UseSharedCompilation=false", "-nodereuse:false",
         "--nologo", "-v:q"],
        worktree, timeout)


def covers(declared, test_name):
    """Does a declared deciding test cover this TRX result?

    `expectedTests` entries are written at the granularity the control decides at: a whole class
    (`…HlsOwnershipMatrixTests`), a theory (`…VideoSegmentFamily`, whose TRX rows carry their data
    row in parentheses) or one fact. Coverage is therefore prefix-based, and one-directional: a
    declared name never matches a SHORTER test name, so declaring a class cannot be satisfied by an
    unrelated test whose name happens to start the same way at a non-boundary.
    """
    return (test_name == declared
            or test_name.startswith(declared + ".")
            or test_name.startswith(declared + "("))


def read_trx(path):
    """(results, summary) from a TRX file, or (None, reason).

    TRX is read rather than stdout because the stdout summary on this host is localised, is a
    single count with no test names in it, and cannot distinguish "the declared test failed" from
    "some other test failed". A grade of RED is a claim about a NAMED test; only the structured
    result carries the names.
    """
    try:
        root = ElementTree.parse(path).getroot()
    except (OSError, ElementTree.ParseError) as failure:
        return None, f"the TRX result is absent or unreadable: {failure}"

    results = []
    for node in root.findall(".//trx:UnitTestResult", TRX_NS):
        name = node.get("testName")
        if name:
            results.append((name, node.get("outcome") or "Unknown"))

    # RunInfos is NOT a run-level error channel here and must not be read as one. xUnit copies its
    # own console lines into it, so every ordinary `[FAIL]` line arrives as a RunInfo whose outcome
    # is an error - and a first draft of this grader classified a perfectly good RED as ERROR for
    # exactly that reason. Whether the RUN aborted is in Counters, which xUnit fills correctly.
    counters = root.find(".//trx:ResultSummary/trx:Counters", TRX_NS)
    summary = {key: int(counters.get(key, 0)) for key in ("total", "executed", "passed", "failed", "error", "aborted")} if counters is not None else {}
    return results, summary


def classify_trx(results, summary, declared):
    """The mission's four-valued classification, decided from the structured result alone."""
    if summary.get("aborted") or summary.get("error"):
        return ERROR, f"the run aborted or errored before it could decide: {summary}"
    if not results:
        return ERROR, "the filter matched no tests: the run produced no test result at all"

    executed = {name for name, outcome in results if outcome != "NotExecuted"}
    for name in declared:
        matched = [n for n, _ in results if covers(name, n)]
        if not matched:
            return ERROR, (f"the declared deciding test '{name}' does not exist in the run "
                           f"({len(results)} tests ran under this filter)")
        if not any(n in executed for n in matched):
            return ERROR, f"the declared deciding test '{name}' exists but was not executed"

    failures = [name for name, outcome in results if outcome == "Failed"]
    if not failures:
        return GREEN, f"every declared deciding test ran and stayed green ({len(results)} tests)"

    decisive = [name for name in failures if any(covers(d, name) for d in declared)]
    if not decisive:
        return ERROR, ("only tests OUTSIDE expectedTests failed, so nothing this control declares "
                       "decisive was broken: " + "; ".join(sorted(failures)[:5]))
    return RED, (f"{len(decisive)} declared deciding test(s) failed: "
                 + "; ".join(sorted(decisive)[:5]))


def gate_dotnet_test(worktree, gate, timeout):
    code, log = build(worktree, gate["project"], timeout)
    if code == "TIMEOUT":
        return HUNG, "TIMEOUT"
    if code != 0:
        return ERROR, "build failed\n" + log[-3000:]

    results_dir = tempfile.mkdtemp(prefix="ltv-trx-")
    try:
        code, log = run(
            ["dotnet", "test", gate["project"], "--no-build", "--nologo",
             "--filter", gate["filter"],
             "--logger", "trx;LogFileName=control.trx",
             "--results-directory", results_dir],
            worktree, timeout)
        if code == "TIMEOUT":
            return HUNG, "TIMEOUT"

        trx = os.path.join(results_dir, "control.trx")
        if not os.path.exists(trx):
            return ERROR, ("no TRX result was written: the run did not happen\n" + log[-3000:])
        results, summary = read_trx(trx)
        if results is None:
            return ERROR, summary + "\n" + log[-2000:]

        verdict, detail = classify_trx(results, summary, gate["expectedTests"])
        counts = {k: summary.get(k) for k in ("total", "executed", "passed", "failed")}
        return verdict, f"{detail}\ntrx {counts}\n" + log[-1200:]
    finally:
        shutil.rmtree(results_dir, ignore_errors=True)


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
    return (RED if present else GREEN), f"pattern {'present' if present else 'absent'} in {gate['file']}"


def gate_inventory(worktree, gate, timeout):
    code, log = run(["bash", gate["script"]], worktree, timeout)
    if code == "TIMEOUT":
        return HUNG, "TIMEOUT"
    return (GREEN if code == 0 else RED), log[-1500:]


def gate_rig(worktree, gate, timeout, rig):
    if not rig:
        return NO_RIG, (
            f"scenario '{gate['scenario']}' needs a live rig; pass --rig <script>. "
            f"Requires: {'; '.join(gate.get('requires', []))}")
    code, log = run([rig, gate["scenario"], worktree], os.path.dirname(rig) or ".", timeout)
    if code == "TIMEOUT":
        return HUNG, "TIMEOUT"
    if code == 2:
        # The adapter could not decide - the mutated tree did not build, or it produced no
        # evidence. That is an ERROR to attribute, never a RED: grading a build failure as a
        # proven property is exactly the false green this whole runner exists to prevent.
        return ERROR, "the rig could not decide (exit 2)\n" + log[-2000:]
    return (GREEN if code == 0 else RED), log[-2000:]


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
    return ERROR, f"schema: unknown gate kind '{kind}'"


def grade(control, verdict):
    """The gate's verdict, read against what this control expects to happen.

    GREEN is the only verdict whose NAME depends on the control: for a positive control it is the
    PASS the control exists to demonstrate; for a mutation it is INERT - the mutation ran and broke
    nothing, which is the finding, not a success.
    """
    if verdict == NO_RIG:
        return "SKIPPED-NO-RIG"
    if verdict in (ERROR, HUNG):
        return verdict
    if control["expect"] == "PASS":
        return "PASS" if verdict == GREEN else "ERROR"
    return "RED" if verdict == RED else "INERT"


def replay(controls, worktree, baseline, rig, on_record=None):
    results = []
    for control in controls:
        started = time.time()
        record = {"id": control["id"], "stage": control.get("stage"), "status": control.get("status"),
                  "expect": control.get("expect"), "property": control.get("property")}
        if "note" in control:
            record["note"] = control["note"]

        invalid = validate_schema(control)
        if invalid:
            record["grade"] = "ERROR"
            record["detail"] = invalid
        else:
            failure = None
            for mutation in control["mutations"]:
                failure = apply_mutation(worktree, mutation)
                if failure:
                    break

            if failure:
                record["grade"] = "ERROR"
                record["detail"] = failure
            else:
                try:
                    verdict, detail = evaluate(
                        worktree, control["gate"], control["timeoutSeconds"], rig)
                except Exception:  # noqa: BLE001 - a grader that crashes must ERROR, never kill the run
                    verdict = ERROR
                    detail = "the grader crashed\n" + traceback.format_exc()[-2000:]
                record["verdict"] = verdict
                record["detail"] = detail
                record["grade"] = grade(control, verdict)

        revert(worktree)
        restored = tree_hash(worktree)
        record["restoredByteIdentically"] = restored == baseline
        if not record["restoredByteIdentically"]:
            record["grade"] = "ERROR"
            record["detail"] = f"tree {restored} != baseline {baseline}; {record.get('detail')}"
        record["seconds"] = round(time.time() - started, 1)

        print(f"{record['grade']:15} {record['id']:52} {record['seconds']:7.1f}s  "
              f"{str(record.get('detail')).splitlines()[0][:88] if record.get('detail') else ''}",
              flush=True)
        results.append(record)
        if on_record:
            on_record(results)
    return results


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--commit", default="HEAD")
    parser.add_argument("--out", default=None)
    parser.add_argument("--rig", default=None, help="script run as <script> <scenario> <worktree>")
    parser.add_argument("--keep", action="store_true", help="do not delete the temporary worktree")
    parser.add_argument("--self-test", action="store_true",
                        help="replay ONLY the grader counter-controls and stop")
    parser.add_argument("--no-self-test", action="store_true",
                        help="skip the grader counter-controls (the roster is then unproven)")
    parser.add_argument("ids", nargs="*")
    args = parser.parse_args()

    with open(MANIFEST, encoding="utf-8") as handle:
        manifest = json.load(handle)
    with open(COUNTER_MANIFEST, encoding="utf-8") as handle:
        counter = json.load(handle)

    selected = [] if args.self_test else [
        c for c in manifest["controls"] if not args.ids or c["id"] in args.ids]
    if not selected and not args.self_test:
        raise SystemExit("no control matched")

    # The self-test proves the grader itself. It runs by default on a FULL roster replay, because
    # a roster graded by an unproven grader is what R4 finding F8 was: 45 grades, 33 of them
    # decided by a field nothing read.
    full_roster = not args.ids
    run_self_test = args.self_test or (full_roster and not args.no_self_test)

    source = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()
    commit = subprocess.run(
        ["git", "rev-parse", args.commit], cwd=source, capture_output=True, text=True, check=True).stdout.strip()

    worktree = tempfile.mkdtemp(prefix="ltv-hostile-controls-")
    os.rmdir(worktree)
    subprocess.run(["git", "worktree", "add", "--detach", worktree, commit], cwd=source, check=True)
    print(f"commit {commit}\nworktree {worktree}", flush=True)

    payload = {"commit": commit, "selfTest": "skipped", "counterControls": [], "results": []}

    def flush():
        if args.out:
            with open(args.out, "w", encoding="utf-8") as handle:
                json.dump(payload, handle, indent=2)

    counter_results = []
    results = []
    try:
        revert(worktree)
        baseline = tree_hash(worktree)
        payload["baselineTree"] = baseline
        print(f"baseline tree {baseline}", flush=True)

        if run_self_test:
            print("\n--- grader counter-controls (classification, not the product) ---", flush=True)
            counter_results = replay(
                counter["controls"], worktree, baseline, args.rig,
                on_record=lambda r: (payload.__setitem__("counterControls", r), flush()))
            payload["counterControls"] = counter_results

        misgraded = [r for r in counter_results
                     if r["grade"] != r["expect"] or not r["restoredByteIdentically"]]
        payload["selfTest"] = ("skipped" if not run_self_test
                               else ("HELD" if not misgraded else "FAILED"))
        flush()

        if run_self_test and misgraded:
            print("\nThe grader misclassified "
                  f"{len(misgraded)} counter-control(s); the roster is NOT graded.", flush=True)
            for record in misgraded:
                print(f"  {record['id']}: expected {record['expect']}, got {record['grade']}", flush=True)
        elif selected:
            print("\n--- roster ---", flush=True)
            results = replay(
                selected, worktree, baseline, args.rig,
                on_record=lambda r: (payload.__setitem__("results", r), flush()))
            payload["results"] = results
            flush()
    finally:
        if not args.keep:
            subprocess.run(["git", "worktree", "remove", "--force", worktree], cwd=source, check=False)
            shutil.rmtree(worktree, ignore_errors=True)

    print("")
    for label, records in (("counter-control", counter_results), ("roster", results)):
        if not records:
            continue
        grades = [r["grade"] for r in records]
        print(f"{label}:")
        for name in ("PASS", "RED", "INERT", "ERROR", "HUNG", "SKIPPED-NO-RIG"):
            if grades.count(name):
                print(f"  {grades.count(name):3} {name}")

    def matched(records):
        return all(r["grade"] == r["expect"] and r["restoredByteIdentically"]
                   for r in records if r["grade"] != "SKIPPED-NO-RIG")

    unresolved = [r for r in results if r["grade"] == "SKIPPED-NO-RIG"]
    if unresolved:
        print(f"\n{len(unresolved)} control(s) were NOT graded: they need a rig. This run is incomplete.")
    if run_self_test:
        print(f"\nself-test: {payload['selfTest']}")
    elif selected:
        print("\nself-test: SKIPPED - this roster result is not admissible on its own.")

    ok = matched(counter_results) and (not run_self_test or payload["selfTest"] == "HELD")
    if selected and not (run_self_test and payload["selfTest"] == "FAILED"):
        ok = ok and matched(results) and not unresolved
    elif selected:
        ok = False
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
