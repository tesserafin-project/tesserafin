"""Reviewer fixtures for the retention workflow's publication policy (#236).

Three tiers, thirty controls, each of which must reach ONE named property. The
point of a control is not that the checker fails — a broken checker fails on
everything — but that it fails for the reason the control is named after. So
each control declares the property it expects, and a control whose finding set
does not contain that property is INERT, reported as such, and fails the suite.

Tier 1, controls 01–12 (W1-A4-R1): the semantic cannot-publish policy. The
mutations are applied to a COPY of the pristine RETENTION workflow, written
outside the repository. Nothing under review is edited, so there is no mutation
to restore and no window in which the tree on disk is not the tree being
reviewed. The two helper fixtures write throwaway scripts outside the repository
too, and hand them to the real closure resolver rather than to a second one
written for the test.

Tier 2, controls S0–S3 and H01–H09: the frozen publication workflow. Same
discipline, applied to a COPY of the PUBLICATION workflow. Since W1-A5-V1-R3
they are graded against `publication_policy.check_all` — the callable the
canonical roster manifest pins — over a disposable repository root built outside
the tree, because R2 finding B2 deleted the one line that CALLS
`check_summary_identity` and every control that reached the implementation
directly stayed red against a gate that no longer ran it.

S0–S3 name `summary.frozen-prose-drift`. H01–H09 name
`publication.frozen-workflow-drift`, and are the nine bypass classes R2 measured
against the R1 pin: a second summary writer after the pinned step, the same
before it, one in a second job, an approved decoy beside a rogue writer, a
writer reached through a shell variable, a duplicate `publish:` job key and a
duplicate `run:` key with the rogue first, a second publication-capable job, and
`packages: write` hoisted to workflow scope. W1-A5-V1-R0 found that nothing
stopped that workflow's step summary from regaining an arbitrary visibility
assertion; S1 restores the old private claim, S2 asserts the opposite, and S3
is a careful, well-intentioned REWORDING of an assertion the approved text
already makes. All three must fail for the same named reason, because the pin
is a hash of the reviewed bytes and not a search for bad words. S3 is the one
that matters: a keyword or blacklist check would let it through.

Tier 3, controls T01–T05 (W1-A5-V1-R5, closing R4 finding NB-1): the reviewed
publication workflow must be a REGULAR FILE at the reviewed path. Hashing
whatever the path resolves to accepts a symbolic link pointing at the approved
bytes, and a link can be repointed after review without the reviewed content
changing. T01 is the decisive one — a symlink whose target is byte-for-byte the
approved workflow — and it is refused for the same
`publication.frozen-workflow-drift` the altered-byte controls name, because the
target is never read. T02 links to altered bytes, T03 puts a directory at the
path, T04 removes it, and T05 is the pristine regular file, which must be
accepted or the tier is inverted. These are filesystem-shape controls, so unlike
tiers 1 and 2 each one builds its own disposable root and then acts on it.

Each tier-2 control proves its anchor existed before mutating — a silently
skipped mutation would otherwise grade PASS against pristine bytes and read as
success — and re-reads the file it actually wrote before grading it.

A grep match against the checker's own source would be INERT by construction:
the checker is never the file under test, and every assertion is made against
the finding list `publication_policy` returns for the mutated workflow.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
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
_JOB_ANCHOR = "  gates:\n    name: Retention gates\n    runs-on: ubuntu-latest\n"


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
        "      - name: Assert the evidence SHA\n        shell: bash\n",
        "      - name: Assert the evidence SHA\n        shell: bash\n"
        "        env:\n          FORWARDED: ${{ github.token }}\n",
    ), ()


def fixture_06(source, work):
    return _replace(
        source,
        "      - name: Assert the evidence SHA\n        shell: bash\n",
        "      - name: Assert the evidence SHA\n        shell: bash\n"
        "        env:\n          FORWARDED: ${{ secrets.GITHUB_TOKEN }}\n",
    ), ()


def fixture_07(source, work):
    return _replace(
        source,
        "      - name: Assert the evidence SHA\n        shell: bash\n",
        "      - name: Assert the evidence SHA\n        shell: bash\n"
        "        env:\n          FORWARDED: ${{ secrets.GHCR_PUBLISH_PAT }}\n",
    ), ()


def fixture_08(source, work):
    return _replace(
        source,
        "          echo \"validating ${actual}\"\n",
        "          echo \"validating ${actual}\"\n"
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
        "          echo \"validating ${actual}\"\n",
        f"          echo \"validating ${{actual}}\"\n"
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
        "          echo \"validating ${actual}\"\n",
        f"          echo \"validating ${{actual}}\"\n"
        f"          {outer} \"${{RUNNER_TEMP}}/fixture\"\n",
    ), (inner, outer)


def fixture_11(source, work):
    return source, ()


# ── tier 2: the frozen publication summary (W1-A5-V1-R1) ────────────────────
#
# These mutate the PUBLICATION workflow, not the retention one, and they are
# graded against `check_summary_identity` rather than `evaluate`. The
# publication workflow is supposed to publish; running the cannot-publish
# evaluation over it would only re-derive its legitimate `packages: write`
# findings and could never reach this property.

def _disposable_root(work: Path, root: Path, publication: str) -> Path:
    """A throwaway repository root carrying the workflow under test.

    W1-A5-V1-R2 finding B2. Until R3 these controls called
    `publication_policy.check_summary_identity` directly, so deleting the one
    line that calls it from `check_all` — the callable the canonical roster
    manifest actually pins — left every control still RED and the whole suite
    still green. A control that reaches an implementation the gate no longer
    invokes proves nothing about the gate.

    So the controls now grade through `check_all`, which takes a ROOT and not a
    workflow path. This builds one: the pristine retention workflow, so the
    cannot-publish evaluation inside `check_all` has the reviewed file to look
    at and contributes nothing; the publication workflow under test; a symlink
    to the real `ci/` tree, which is where the script closure lives; and
    `git init`, because the closure resolver enumerates tracked paths.

    Nothing here is written inside the repository under review.
    """
    fake = work / "root"
    (fake / ".github" / "workflows").mkdir(parents=True)
    shutil.copy2(root / RETENTION, fake / RETENTION)
    (fake / PUBLISH).write_text(publication, encoding="utf-8")
    (fake / "ci").symlink_to(root / "ci")
    subprocess.run(["git", "init", "-q", str(fake)], check=True,
                   capture_output=True)
    return fake


def _pristine_publish(root: Path) -> str:
    return (root / PUBLISH).read_text(encoding="utf-8")


#: A line inside the approved summary's `run:` scalar, immediately before the
#: paragraph disclaiming any visibility assertion. S1 and S2 insert ahead of it.
_SUMMARY_DISCLAIMER = (
    "            echo \"This workflow does not set, change or verify the "
    "package's registry\"\n"
)

#: The approved anonymous-availability sentence, in the approved words. S3
#: rewrites it without changing what it means.
_SUMMARY_OBSERVATION = (
    '            echo "The durable record on #236 carries the visibility, and the anonymous"\n'
    '            echo "manifest and blob readback, observed for this accepted digest. That is"\n'
    '            echo "evidence of an observation at a point in time, not a standing guarantee."\n'
)


def summary_s0(source, work):
    """Pristine. The approved summary must be accepted, or the pin is inverted."""
    return source, None


def summary_s1(source, work):
    """The pre-publication claim, restored verbatim."""
    inserted = '            echo "The package is PRIVATE."\n'
    return _replace(source, _SUMMARY_DISCLAIMER, inserted + _SUMMARY_DISCLAIMER), inserted


def summary_s2(source, work):
    """The opposite claim. A keyword checker tuned for PRIVATE would miss this."""
    inserted = '            echo "The package is PUBLIC."\n'
    return _replace(source, _SUMMARY_DISCLAIMER, inserted + _SUMMARY_DISCLAIMER), inserted


def summary_s3(source, work):
    """A reworded assertion that says roughly what the approved text says.

    Nothing here is false, nothing is a banned word, and no blacklist of known
    phrasings would catch it. It still is not the reviewed prose, and the whole
    value of hashing the scalar is that this fails for exactly the same named
    reason S1 and S2 do.
    """
    replacement = (
        '            echo "Anonymous, unauthenticated retrieval of this digest — the manifest,"\n'
        '            echo "its config and its single layer — was confirmed against the registry"\n'
        '            echo "for the accepted unit, and the durable #236 record carries it."\n'
    )
    return _replace(source, _SUMMARY_OBSERVATION, replacement), replacement


#: Tier 3. Each builder returns the disposable ROOT it has already acted on,
#: plus a description of what it did, and the harness grades
#: `publication_policy.check_all` over that root — the same callable the
#: canonical roster manifest pins, for the same B2 reason tier 2 does.


def identity_t01(root: Path, work: Path):
    """A symlink whose target is byte-for-byte the approved workflow."""
    fake = _disposable_root(work, root, _pristine_publish(root))
    target = fake / PUBLISH
    target.unlink()
    target.symlink_to(root / PUBLISH)
    return fake, "a symbolic link to the approved bytes"


def identity_t02(root: Path, work: Path):
    """A symlink to altered bytes. The link, not the bytes, is the finding."""
    elsewhere = work / "altered.yml"
    elsewhere.write_text(_pristine_publish(root) + "# not the reviewed bytes\n",
                         encoding="utf-8")
    fake = _disposable_root(work, root, _pristine_publish(root))
    target = fake / PUBLISH
    target.unlink()
    target.symlink_to(elsewhere)
    return fake, "a symbolic link to altered bytes"


def identity_t03(root: Path, work: Path):
    """A directory at the reviewed path."""
    fake = _disposable_root(work, root, _pristine_publish(root))
    target = fake / PUBLISH
    target.unlink()
    target.mkdir()
    return fake, "a directory at the reviewed path"


def identity_t04(root: Path, work: Path):
    """The reviewed path removed altogether."""
    fake = _disposable_root(work, root, _pristine_publish(root))
    (fake / PUBLISH).unlink()
    return fake, "the reviewed path removed"


def identity_t05(root: Path, work: Path):
    """The pristine regular file. Must be accepted, or the tier is inverted."""
    return _disposable_root(work, root, _pristine_publish(root)), "the reviewed file"


IDENTITY_FIXTURES: list[tuple[str, str, str | None, object]] = [
    ("T01-symlink-to-approved-bytes",
     "a symlink whose target is the approved workflow is refused",
     "publication.frozen-workflow-drift", identity_t01),
    ("T02-symlink-to-altered-bytes", "a symlink to altered bytes is refused",
     "publication.frozen-workflow-drift", identity_t02),
    ("T03-directory-at-the-path", "a directory at the reviewed path is refused",
     "publication.frozen-workflow-drift", identity_t03),
    ("T04-reviewed-path-missing", "a missing reviewed path is refused",
     "publication.frozen-workflow-drift", identity_t04),
    ("T05-pristine-regular-file", "the reviewed regular file is accepted",
     None, identity_t05),
]


#: The nine bypass classes W1-A5-V1-R2 measured against the R1 pin. Each one is
#: a syntactically harmless addition that the summary property, which names ONE
#: step in ONE job, could not see. They are graded on
#: `publication.frozen-workflow-drift`, which reads the file as bytes and so has
#: no step, no job and no parse for any of them to stand outside of.

_PUBLISH_JOB_ANCHOR = "jobs:\n  publish:\n"
_PINNED_STEP_ANCHOR = "      - name: Record what was published\n        shell: bash\n"
_WORKFLOW_PERMISSIONS = "permissions:\n  contents: read\n\nconcurrency:\n"

_ROGUE_WRITER = (
    "      - name: Note the registry state\n"
    "        shell: bash\n"
    '        run: echo "The package is PUBLIC." >> "${GITHUB_STEP_SUMMARY}"\n'
)


def publish_h01(source, work):
    """A second summary writer AFTER the pinned step. R2 control H1."""
    return source.rstrip("\n") + "\n" + _ROGUE_WRITER, _ROGUE_WRITER


def publish_h02(source, work):
    """The same writer BEFORE the pinned step. R2 control H2."""
    return _replace(source, _PINNED_STEP_ANCHOR,
                    _ROGUE_WRITER + _PINNED_STEP_ANCHOR), _ROGUE_WRITER


def publish_h03(source, work):
    """A writer in a SECOND job. R2 control H3."""
    added = (
        "\n  annotate:\n"
        "    name: Annotate the run\n"
        "    runs-on: ubuntu-latest\n"
        "    steps:\n"
        "      - name: Record what was published\n"
        "        shell: bash\n"
        '        run: echo "The package is PUBLIC." >> "${GITHUB_STEP_SUMMARY}"\n'
    )
    return source.rstrip("\n") + "\n" + added, added


def publish_h04(source, work):
    """The approved scalar kept as a DECOY beside a differently named writer.

    R2 control H4. The summary property finds its one named step, hashes it,
    and is satisfied; the sentence a reader sees is written by the other step.
    """
    return source.rstrip("\n") + "\n" + _ROGUE_WRITER, _ROGUE_WRITER


def publish_h05(source, work):
    """A writer reached through a shell variable. R2 control H5.

    No check that looks for the name `GITHUB_STEP_SUMMARY` on a step sees this,
    which is why the repair does not look for names.
    """
    added = (
        "      - name: Emit the closing note\n"
        "        shell: bash\n"
        "        run: |\n"
        "          set -euo pipefail\n"
        '          target="GITHUB_STEP_SUMMARY"\n'
        '          echo "The package is PUBLIC." >> "${!target}"\n'
    )
    return source.rstrip("\n") + "\n" + added, added


def publish_h06(source, work):
    """A duplicate `publish:` job key with the ROGUE job FIRST. R2 control H6f.

    The complete publication workflow is pinned as raw bytes. Inserting a
    duplicate key changes those bytes and is refused as
    `publication.frozen-workflow-drift`; this control does not rely on
    YAML parser semantics.
    """
    rogue = (
        "jobs:\n"
        "  publish:\n"
        "    name: Publish\n"
        "    runs-on: ubuntu-latest\n"
        "    steps:\n"
        "      - name: Record what was published\n"
        "        shell: bash\n"
        '        run: echo "The package is PUBLIC." >> "${GITHUB_STEP_SUMMARY}"\n'
    )
    return _replace(source, _PUBLISH_JOB_ANCHOR, rogue + "  publish:\n"), rogue


def publish_h07(source, work):
    """A duplicate `run:` key in the pinned step, rogue FIRST. R2 control H6g."""
    rogue = '        run: echo "The package is PUBLIC." >> "${GITHUB_STEP_SUMMARY}"\n'
    return _replace(source, _PINNED_STEP_ANCHOR, _PINNED_STEP_ANCHOR + rogue), rogue


def publish_h08(source, work):
    """A second publication-capable job carrying its own prose. R2 control H9.

    `check_all` never runs `evaluate` over this workflow — it is supposed to
    publish — so its authority surface had no owner on the canonical path at
    all. The byte pin is that owner: it does not decide whether the job could
    publish, it decides that the job is not in the reviewed file.
    """
    added = (
        "\n  publish-mirror:\n"
        "    name: Mirror the accepted unit\n"
        "    runs-on: ubuntu-latest\n"
        "    environment: windows-runtime-publication\n"
        "    permissions:\n"
        "      contents: read\n"
        "      packages: write\n"
        "    steps:\n"
        "      - name: Push and note it\n"
        "        env:\n"
        "          GHCR_TOKEN: ${{ secrets.GITHUB_TOKEN }}\n"
        "        shell: bash\n"
        "        run: |\n"
        "          set -euo pipefail\n"
        '          printf %s "${GHCR_TOKEN}" | oras login ghcr.io -u "${GITHUB_ACTOR}" --password-stdin\n'
        "          oras manifest push ghcr.io/tesserafin-project/windows-ffmpeg-runtime:mirror\n"
        '          echo "The package is PUBLIC." >> "${GITHUB_STEP_SUMMARY}"\n'
    )
    return source.rstrip("\n") + "\n" + added, added


def publish_h09(source, work):
    """`packages: write` hoisted to workflow scope. R2 control H11.

    R1 recorded this observation and deliberately did not repair it, so that the
    S0-S3 grades stayed attributable to the property they name. It is repaired
    here as a side effect of pinning the file, and it is a control so that the
    coverage is stated rather than assumed.
    """
    hoisted = "permissions:\n  contents: read\n  packages: write\n\nconcurrency:\n"
    return _replace(source, _WORKFLOW_PERMISSIONS, hoisted), "  packages: write\n\nconcurrency:"


SUMMARY_FIXTURES: list[tuple[str, str, str, object]] = [
    ("S0-pristine-approved-summary", "the approved publication workflow is accepted",
     None, summary_s0),
    ("S1-restored-private-claim", "a restored `The package is PRIVATE.` is refused",
     "summary.frozen-prose-drift", summary_s1),
    ("S2-inverted-public-claim", "an inserted `The package is PUBLIC.` is refused",
     "summary.frozen-prose-drift", summary_s2),
    ("S3-reworded-anonymous-claim",
     "a differently worded anonymous-availability assertion is refused",
     "summary.frozen-prose-drift", summary_s3),
    ("H01-writer-after-the-pinned-step", "a second summary writer after it is refused",
     "publication.frozen-workflow-drift", publish_h01),
    ("H02-writer-before-the-pinned-step", "a second summary writer before it is refused",
     "publication.frozen-workflow-drift", publish_h02),
    ("H03-writer-in-a-second-job", "a summary writer in another job is refused",
     "publication.frozen-workflow-drift", publish_h03),
    ("H04-approved-decoy-plus-writer",
     "the approved scalar kept as a decoy beside a writer is refused",
     "publication.frozen-workflow-drift", publish_h04),
    ("H05-writer-through-indirection",
     "a writer reached through a shell variable is refused",
     "publication.frozen-workflow-drift", publish_h05),
    ("H06-duplicate-job-key-rogue-first",
     "a duplicate `publish:` key whose rogue job is hidden by last-wins is refused",
     "publication.frozen-workflow-drift", publish_h06),
    ("H07-duplicate-run-key-rogue-first",
     "a duplicate `run:` key in the pinned step is refused",
     "publication.frozen-workflow-drift", publish_h07),
    ("H08-second-publication-capable-job",
     "a second job with packages: write, a credential and its own prose is refused",
     "publication.frozen-workflow-drift", publish_h08),
    ("H09-workflow-level-packages-write",
     "`packages: write` hoisted to workflow scope is refused",
     "publication.frozen-workflow-drift", publish_h09),
]


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


def _grade(findings, expected: str | None) -> tuple[str, str, list[str]]:
    """The one grading rule, shared by both tiers.

    PASS and RED are the only passing grades. GREEN — the checker ACCEPTED what
    must be refused — and INERT — it refused, but for some other property — both
    fail, because either one means the control proves nothing about the property
    it is named after.
    """
    properties = sorted({f.prop for f in findings})
    if expected is None:
        if findings:
            return "GREEN", f"accepted nothing; findings {properties}", properties
        return "PASS", "accepted, as it must be", properties
    if not findings:
        return "GREEN", "ACCEPTED what must be refused", properties
    if expected in properties:
        return "RED", f"refused, naming {expected}", properties
    return "INERT", f"refused, but not for {expected}; got {properties}", properties


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    root = boundary.repo_root()
    before = (root / RETENTION).read_bytes()
    publish_before = (root / PUBLISH).read_bytes()
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
            grade, detail, properties = _grade(findings, expected)
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

    print()
    print("frozen publication summary controls")
    publish_pristine = _pristine_publish(root)
    for name, description, expected, mutate in SUMMARY_FIXTURES:
        work = Path(tempfile.mkdtemp(prefix=f"w1a5r1-{name}-"))
        grade = "ERROR"
        detail = ""
        properties: list[str] = []
        try:
            # `_replace` raises LookupError when the anchor is absent, which is
            # an ERROR and not a quiet PASS against pristine bytes. A control
            # that silently skips its own mutation is the failure mode this
            # exists to make impossible.
            mutated, marker = mutate(publish_pristine, work)
            fake = _disposable_root(work, root, mutated)
            target = fake / PUBLISH

            # Re-read what was actually WRITTEN, not what was computed. The
            # grade below is about the bytes on disk that the checker will open.
            written = target.read_text(encoding="utf-8")
            if marker is None:
                if written != publish_pristine:
                    raise AssertionError(
                        "the pristine control's copy is not byte-identical to the "
                        "reviewed publication workflow")
            elif marker not in written:
                raise AssertionError(
                    f"the mutation was not present in the file that was written: "
                    f"{marker!r}")

            findings = publication_policy.check_all(fake)
            grade, detail, properties = _grade(findings, expected)
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

    print()
    print("frozen publication workflow filesystem-identity controls")
    for name, description, expected, build in IDENTITY_FIXTURES:
        work = Path(tempfile.mkdtemp(prefix=f"w1a5r5-{name}-"))
        grade = "ERROR"
        detail = ""
        properties: list[str] = []
        try:
            fake, marker = build(root, work)
            findings = publication_policy.check_all(fake)
            grade, detail, properties = _grade(findings, expected)
            detail = f"{detail} [{marker}]"
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

    print()
    after = (root / RETENTION).read_bytes()
    if after != before:
        print("  FAIL   tree-restored                          "
              "the pristine workflow changed on disk")
        failures += 1
        results.append({"fixture": "tree-restored", "grade": "FAIL"})
    else:
        print("  OK     tree-restored                          "
              "the reviewed workflow is byte-identical on disk")

    publish_after = (root / PUBLISH).read_bytes()
    if publish_after != publish_before:
        print("  FAIL   publication-workflow-restored          "
              "the reviewed publication workflow changed on disk")
        failures += 1
        results.append({"fixture": "publication-workflow-restored", "grade": "FAIL"})
    else:
        print("  OK     publication-workflow-restored          "
              "the reviewed publication workflow is byte-identical on disk")

    if args.json:
        args.json.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    print()
    if failures:
        print(f"W1-A4 FIXTURE HARD STOP: {failures} control(s) did not reach their property",
              file=sys.stderr)
        return 1
    print(f"all {len(FIXTURES)} semantic permission fixtures, all "
          f"{len(SUMMARY_FIXTURES)} frozen-summary controls and all "
          f"{len(IDENTITY_FIXTURES)} filesystem-identity controls reached their named "
          f"property")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
