"""Reviewer fixtures for the semantic cannot-publish policy (#236, W1-A4-R1).

Twelve fixtures, each of which must reach ONE named property. The point of a
fixture is not that the checker fails — a broken checker fails on everything —
but that it fails for the reason the fixture is named after. So each fixture
declares the property it expects, and a fixture whose finding set does not
contain that property is INERT, reported as such, and fails the suite.

The mutations are applied to a COPY of the pristine retention workflow, written
outside the repository. Nothing under review is edited, so there is no mutation
to restore and no window in which the tree on disk is not the tree being
reviewed. The two helper fixtures write throwaway scripts outside the repository
too, and hand them to the real closure resolver rather than to a second one
written for the test.

A grep match against the checker's own source would be INERT by construction:
the checker is never the file under test, and every assertion is made against
the finding list `publication_policy.evaluate` returns for the mutated workflow.
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

RETENTION = boundary.RETENTION_WORKFLOW
PUBLISH = ".github/workflows/w1-windows-runtime-publish.yml"


def _pristine(root: Path) -> str:
    return (root / RETENTION).read_text(encoding="utf-8")


def _replace(source: str, old: str, new: str) -> str:
    if old not in source:
        raise LookupError(f"the fixture anchor {old!r} is not in the pristine workflow")
    return source.replace(old, new, 1)


# ── the mutations ───────────────────────────────────────────────────────────
_WORKFLOW_PERMS = "permissions:\n  contents: read\n"
_JOB_ANCHOR = "  contract:\n    name: Contract and controls\n    runs-on: ubuntu-latest\n"


def fixture_01(source, work):
    return _replace(source, _WORKFLOW_PERMS, "permissions: write-all\n"), ()


def fixture_02(source, work):
    return _replace(
        source, _JOB_ANCHOR,
        _JOB_ANCHOR.replace("    runs-on: ubuntu-latest\n",
                            "    runs-on: ubuntu-latest\n    permissions: write-all\n"),
    ), ()


def fixture_03(source, work):
    return _replace(
        source, _JOB_ANCHOR,
        _JOB_ANCHOR.replace(
            "    runs-on: ubuntu-latest\n",
            '    runs-on: ubuntu-latest\n    permissions:\n      "contents": "read"\n'
            '      "packages": "write"\n'),
    ), ()


def fixture_04(source, work):
    return _replace(
        source, _JOB_ANCHOR,
        _JOB_ANCHOR.replace(
            "    runs-on: ubuntu-latest\n",
            "    runs-on: ubuntu-latest\n    permissions: { contents: read, packages: write }\n"),
    ), ()


def fixture_05(source, work):
    return _replace(
        source,
        "      - name: Build the fixture\n        shell: bash\n",
        "      - name: Build the fixture\n        shell: bash\n"
        "        env:\n          FORWARDED: ${{ github.token }}\n",
    ), ()


def fixture_06(source, work):
    return _replace(
        source,
        "      - name: Build the fixture\n        shell: bash\n",
        "      - name: Build the fixture\n        shell: bash\n"
        "        env:\n          FORWARDED: ${{ secrets.GITHUB_TOKEN }}\n",
    ), ()


def fixture_07(source, work):
    return _replace(
        source,
        "      - name: Build the fixture\n        shell: bash\n",
        "      - name: Build the fixture\n        shell: bash\n"
        "        env:\n          FORWARDED: ${{ secrets.GHCR_PUBLISH_PAT }}\n",
    ), ()


def fixture_08(source, work):
    return _replace(
        source,
        "          python3 make-fixture.py --out \"${RUNNER_TEMP}/fixture\"\n",
        "          python3 make-fixture.py --out \"${RUNNER_TEMP}/fixture\"\n"
        "          oras manifest push ghcr.io/tesserafin-project/windows-ffmpeg-runtime@sha256:0\n",
    ), ()


def _helper(work: Path, name: str, body: str) -> str:
    path = work / name
    path.write_text(body, encoding="utf-8")
    path.chmod(0o755)
    return str(path)


def fixture_09(source, work):
    helper = _helper(work, "retention-helper.sh", "#!/usr/bin/env bash\nset -eu\noras manifest push \"$1\"\n")
    return _replace(
        source,
        "          python3 make-fixture.py --out \"${RUNNER_TEMP}/fixture\"\n",
        f"          python3 make-fixture.py --out \"${{RUNNER_TEMP}}/fixture\"\n"
        f"          {helper} \"${{RUNNER_TEMP}}/fixture\"\n",
    ), (helper,)


def fixture_10(source, work):
    inner = _helper(work, "deep-helper.sh", "#!/usr/bin/env bash\nset -eu\noras manifest push \"$1\"\n")
    outer = _helper(
        work, "outer-helper.sh",
        f"#!/usr/bin/env bash\nset -eu\nHERE=\"$(dirname \"$0\")\"\nexec \"${{HERE}}\"/deep-helper.sh \"$@\"\n",
    )
    return _replace(
        source,
        "          python3 make-fixture.py --out \"${RUNNER_TEMP}/fixture\"\n",
        f"          python3 make-fixture.py --out \"${{RUNNER_TEMP}}/fixture\"\n"
        f"          {outer} \"${{RUNNER_TEMP}}/fixture\"\n",
    ), (inner, outer)


def fixture_11(source, work):
    return source, ()


FIXTURES: list[tuple[str, str, str, object]] = [
    ("01-workflow-level-write-all", "a workflow-level `permissions: write-all` is refused",
     "permissions.scalar-write-all", fixture_01),
    ("02-job-level-write-all", "a job-level `permissions: write-all` is refused",
     "permissions.scalar-write-all", fixture_02),
    ("03-quoted-packages-write", 'a quoted `"packages": "write"` is refused',
     "permissions.packages-write", fixture_03),
    ("04-inline-mapping-packages-write", "an inline `{ packages: write }` mapping is refused",
     "permissions.packages-write", fixture_04),
    ("05-github-token", "a ${{ github.token }} reference is refused",
     "credential.github-token", fixture_05),
    ("06-secrets-github-token", "a ${{ secrets.GITHUB_TOKEN }} reference is refused",
     "credential.secrets-github-token", fixture_06),
    ("07-other-secret", "any other ${{ secrets.* }} credential is refused",
     "credential.secrets-reference", fixture_07),
    ("08-inline-oras-push", "an inline `oras manifest push` is refused",
     "registry.write-verb-inline", fixture_08),
    ("09-helper-publishes", "a local helper containing a registry push is refused",
     "registry.write-verb-in-helper", fixture_09),
    ("10-helper-of-helper-publishes", "a helper reached only through another helper is refused",
     "registry.write-verb-in-helper", fixture_10),
    ("11-pristine-validation-workflow", "the pristine validation workflow is accepted",
     None, fixture_11),
    ("12-real-publication-workflow", "the real publication workflow is refused",
     "permissions.packages-write", None),
]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    root = boundary.repo_root()
    before = (root / RETENTION).read_bytes()
    pristine = _pristine(root)
    results = []
    failures = 0

    print("semantic cannot-publish fixtures")
    for name, description, expected, mutate in FIXTURES:
        work = Path(tempfile.mkdtemp(prefix=f"w1a4r1-{name}-"))
        grade = "ERROR"
        detail = ""
        properties: list[str] = []
        try:
            if mutate is None:
                findings = publication_policy.evaluate(root, PUBLISH)
            else:
                mutated, extra = mutate(pristine, work)
                target = work / "fixture-workflow.yml"
                target.write_text(mutated, encoding="utf-8")
                findings = publication_policy.evaluate(root, str(target), tuple(extra))
            properties = sorted({f.prop for f in findings})
            if expected is None:
                if findings:
                    grade, detail = "GREEN", f"accepted nothing; findings {properties}"
                else:
                    grade, detail = "PASS", "accepted, as it must be"
            elif not findings:
                grade, detail = "GREEN", "ACCEPTED what must be refused"
            elif expected in properties:
                grade, detail = "RED", f"refused, naming {expected}"
            else:
                grade, detail = "INERT", f"refused, but not for {expected}; got {properties}"
        except Exception as error:  # noqa: BLE001 - an ERROR grade is the point
            grade, detail = "ERROR", f"{type(error).__name__}: {error}"
        finally:
            shutil.rmtree(work, ignore_errors=True)

        if grade not in ("RED", "PASS"):
            failures += 1
        print(f"  {grade:<6} {name:<38} {detail}")
        results.append({
            "fixture": name, "property": description, "expected": expected,
            "grade": grade, "detail": detail, "found": properties,
        })

    after = (root / RETENTION).read_bytes()
    if after != before:
        print("  FAIL   tree-restored                          "
              "the pristine workflow changed on disk")
        failures += 1
        results.append({"fixture": "tree-restored", "grade": "FAIL"})
    else:
        print("  OK     tree-restored                          "
              "the reviewed workflow is byte-identical on disk")

    if args.json:
        args.json.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    print()
    if failures:
        print(f"W1-A4 FIXTURE HARD STOP: {failures} fixture(s) did not reach their property",
              file=sys.stderr)
        return 1
    print(f"all {len(FIXTURES)} semantic permission fixtures reached their named property")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
