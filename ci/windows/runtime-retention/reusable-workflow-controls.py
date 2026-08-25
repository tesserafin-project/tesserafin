"""Controls for the recursive reusable-workflow closure (#236, W1-A4-R2).

R1's blocking finding D1. `publication_policy` read ONE workflow file. A job's
`uses:` hands the entire job to another workflow, and that other workflow has
its own `permissions:`, its own steps, its own `run:` bodies, its own `secrets:`
and its own `uses:`. A validation workflow that grants itself nothing and calls
a local reusable workflow holding `packages: write` publishes exactly as
effectively as one that held the grant itself — and R1 reported it as closed and
read-only, because it never opened the second file.

Each control builds a COMPLETE throwaway `.github/workflows/` tree outside the
repository and evaluates the caller against it, so what runs is the real graph
traversal rather than a second resolver written for the test. Nothing under
review is written to, and there is no mutation to restore.

Each control names the property it must reach. A control that refuses for some
other reason is INERT, reported as such, and fails the suite: a broken checker
refuses everything, so "it went red" is never the assertion.

Control 10 constructs its own pristine callee rather than pointing at a
repository workflow. The retention workflow has no job-level `uses:` at all, so
using it as the acceptance case would prove the traversal accepts a graph with
no edges in it — inert by construction, and the most comfortable kind of green.

The adversarial workflow bodies live in `reusable-workflow-fixtures.json`. See
the note at the top of that file for why they are data.
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
import tempfile
from pathlib import Path

import boundary
import publication_policy

HERE = Path(__file__).resolve().parent
FIXTURES = HERE / "reusable-workflow-fixtures.json"


def _materialise(work: Path, control: dict, corpus: dict) -> Path:
    """Write the control's workflow tree and return the caller's path."""
    workflows = work / publication_policy.WORKFLOWS_DIR
    workflows.mkdir(parents=True, exist_ok=True)

    for relative, body in (control.get("files") or {}).items():
        if body == "@readOnlyPreamble":
            body = corpus["readOnlyPreamble"]
        target = work / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(body, encoding="utf-8")

    for relative, points_to in (control.get("symlinks") or {}).items():
        target = work / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.is_symlink() or target.exists():
            target.unlink()
        target.symlink_to(points_to)

    caller = workflows / "caller.yml"
    caller.write_text(control["caller"], encoding="utf-8")
    return caller


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    corpus = json.loads(FIXTURES.read_text(encoding="utf-8"))
    root = boundary.repo_root()
    before = (root / boundary.RETENTION_WORKFLOW).read_bytes()
    results = []
    failures = 0

    print("recursive reusable-workflow closure controls")
    for control in corpus["controls"]:
        name, expected = control["name"], control["expect"]
        work = Path(tempfile.mkdtemp(prefix=f"w1a4r2-{name}-"))
        grade, detail, properties = "ERROR", "", []
        try:
            caller = _materialise(work, control, corpus)
            findings = publication_policy.evaluate(
                root, str(caller), uses_root=work)
            properties = sorted({f.prop for f in findings})
            if expected is None:
                grade, detail = (("PASS", "accepted, as it must be") if not findings
                                 else ("GREEN", f"refused a legitimate graph: {properties}"))
            elif not findings:
                grade, detail = "GREEN", "ACCEPTED what must be refused"
            elif expected in properties:
                grade, detail = "RED", f"refused, naming {expected}"
            else:
                grade, detail = "INERT", f"refused, but not for {expected}; got {properties}"
        except RecursionError:
            grade, detail = "ERROR", ("the traversal recursed without bound; a cycle must "
                                      "be a finding, not a stack overflow")
        except Exception as error:  # noqa: BLE001 - an ERROR grade is the point
            grade, detail = "ERROR", f"{type(error).__name__}: {error}"
        finally:
            shutil.rmtree(work, ignore_errors=True)

        if grade not in ("RED", "PASS"):
            failures += 1
        print(f"  {grade:<6} {name:<48} {detail}")
        results.append({"control": name, "property": control["property"],
                        "expected": expected, "grade": grade, "detail": detail,
                        "found": properties})

    after = (root / boundary.RETENTION_WORKFLOW).read_bytes()
    if after != before:
        print("  FAIL   tree-restored                                     "
              "the reviewed workflow changed on disk")
        failures += 1
    else:
        print("  OK     tree-restored                                     "
              "the reviewed workflow is byte-identical on disk")

    if args.json:
        args.json.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    print()
    if failures:
        print(f"W1-A4 REUSABLE-WORKFLOW HARD STOP: {failures} control(s) did not reach "
              f"their property", file=sys.stderr)
        return 1
    print(f"all {len(corpus['controls'])} reusable-workflow controls reached their named "
          f"property; the cannot-publish closure is the graph, not the file")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
