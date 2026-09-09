#!/usr/bin/env python3
"""W4-A0 (#234) -- the controls that do not need a Windows host.

The four hostile controls the ruling names first are properties of an installed
package and can only be measured on a native runner. The fifth is not:

    workflow write-all / production artifact reused as a later input

That one is a property of the authored files, it is the one a reviewer is least
likely to notice, and it is the one that can be checked here, in a second, on
any machine -- so it is checked here rather than being left to the reading of a
YAML file.

Everything below is read out of the files. Nothing is asserted from memory, and
the permission check parses YAML rather than grepping it: `write-all` and a
quoted `"packages": "write"` both grant and both slip past a grep gate.

Two modes:

    (default)     grade the real files; exit 1 on any finding
    --self-test   ALSO mutate a copy of the workflow four ways and require each
                  mutation to be caught. A gate that cannot be made to fail has
                  not been shown to be a gate.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

import yaml

REPO_ROOT = pathlib.Path(__file__).resolve().parents[3]
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "w4-windows-msi.yml"
AUTHORING = REPO_ROOT / "packaging" / "windows" / "msi" / "Tesserafin.wxs"
BUILDER = REPO_ROOT / "ci" / "windows" / "w4" / "build-msi.ps1"
PROBE = REPO_ROOT / "ci" / "windows" / "w4" / "probe-msi-skeleton.ps1"
SELF_TEST = REPO_ROOT / "ci" / "windows" / "w4" / "assertion-self-test.ps1"
A0_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A0-wix-skeleton.md"
A1_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A1-upgradecode.md"

# W4-A1 (#234). The owner ruling froze the GUID W4-A0 had already authored:
# "Ordinal, lowercase, no braces. I do not authorize a new GUID." This is the
# only place that string is stated in the controls, and the comparison below is
# an ordinal one, so the same digits in a different case, or wrapped in braces,
# are a different UpgradeCode and are RED -- which is what the ruling names as
# the hostile control it expects to have been observed RED.
FROZEN_UPGRADE_CODE = "0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05"

# The ruling is equally explicit that "A comment that claims it is unfrozen is
# RED". A pin the prose contradicts is worse than no pin: the next reader
# believes the sentence, not the attribute. These patterns are the W4-A0-era
# wording, so a straight revert of either document trips them.
UNFROZEN_CLAIM_PATTERNS = (
    r"unfrozen",
    r"not\s+frozen",
    r"nothing\s+in\s+this\s+slice\s+freezes",
    r"nothing\s+here\s+freezes",
)

# The entire write surface this slice is allowed to ask for. `packages: read` is
# needed and only needed because the frozen W2 assembler pulls the accepted Web
# payload image with the job's own token, exactly as W3's job does.
ALLOWED_PERMISSIONS = {"contents": "read", "packages": "read"}

# An accepted artifact must never become a later production input, and this
# slice uploads nothing at all, so any of these in the workflow is a finding.
FORBIDDEN_ARTIFACT_MARKERS = (
    "actions/upload-artifact",
    "actions/download-artifact",
    "gh run download",
    "dawidd6/action-download-artifact",
    "/actions/artifacts",
)

# The W0 §4 argument list, in full. Each must appear in the authoring as a
# literal argument, so a slice that quietly dropped one could not be green.
CONTRACT_ARGUMENTS = (
    "--service",
    "--configdir",
    "--datadir",
    "--cachedir",
    "--logdir",
    "--webdir",
    "--ffmpeg",
)

# The frozen W2-A2 assembler's own control (`zip-controls.py` Z11) refuses any
# parameter that would let the identity of what is packaged be supplied at call
# time instead of travelling with the commit. The MSI builder gets the same
# refusal, for the same reason.
FORBIDDEN_BUILDER_PARAMETERS = ("Tag", "RunId", "Reference", "Url", "Uri", "Digest", "Ref")


def without_comments(text: str, kind: str) -> str:
    """Drop commented-out text before asking what a file DOES.

    Both files under these gates explain themselves at length, and several of
    the things the gates forbid are named in those explanations -- the
    authoring's comment says why it does not carry `Start="install"`, and the
    workflow's says where the WiX pin lives instead of Directory.Packages.props.
    Grading the prose would make a file fail for describing its own decision.
    """
    if kind == "xml":
        return re.sub(r"<!--.*?-->", "", text, flags=re.S)
    return "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("#"))


def findings_for_permissions(document: dict) -> list[str]:
    """Every `permissions:` block in the workflow, top level and per job."""
    findings: list[str] = []

    def grade(where: str, block: object) -> None:
        if block is None:
            return
        if isinstance(block, str):
            # `permissions: write-all` and `permissions: read-all` are both the
            # scalar form. Only the read one is acceptable, and this slice
            # should not be using the scalar form at all.
            findings.append(f"{where}: scalar permissions '{block}' -- name each scope instead")
            return
        if not isinstance(block, dict):
            findings.append(f"{where}: permissions is a {type(block).__name__}, not a mapping")
            return
        for scope, level in block.items():
            scope_name, level_name = str(scope), str(level)
            if scope_name not in ALLOWED_PERMISSIONS:
                findings.append(f"{where}: grants '{scope_name}', which this slice does not need")
            elif level_name != ALLOWED_PERMISSIONS[scope_name]:
                findings.append(
                    f"{where}: grants {scope_name}: {level_name}, "
                    f"but only '{ALLOWED_PERMISSIONS[scope_name]}' is authorised"
                )

    grade("workflow", document.get("permissions"))
    if document.get("permissions") is None:
        findings.append("workflow: no top-level permissions block, so the job inherits the default")
    for job_name, job in (document.get("jobs") or {}).items():
        if not isinstance(job, dict):
            continue
        grade(f"job '{job_name}'", job.get("permissions"))
    return findings


def findings_for_artifacts(text: str) -> list[str]:
    return [
        f"workflow: '{marker}' -- this slice uploads nothing and consumes no artifact as an input"
        for marker in FORBIDDEN_ARTIFACT_MARKERS
        if marker in text
    ]


def findings_for_triggers(document: dict) -> list[str]:
    findings: list[str] = []
    # PyYAML reads a bare `on:` key as the boolean True.
    triggers = document.get("on", document.get(True))
    if not isinstance(triggers, dict) or set(triggers) != {"pull_request"}:
        findings.append(
            f"workflow: triggers are {sorted(triggers) if isinstance(triggers, dict) else triggers!r}; "
            "the ruling authorises pull_request only"
        )
    for job_name, job in (document.get("jobs") or {}).items():
        if isinstance(job, dict) and job.get("runs-on") != "windows-latest":
            findings.append(f"job '{job_name}': runs-on is {job.get('runs-on')!r}, not windows-latest")
    return findings


def findings_for_mutation(text: str) -> list[str]:
    """The hosted acceptance must build the REAL package.

    `build-msi.ps1` carries a control-only `-Mutation` parameter so the hostile
    controls drive the real authoring rather than a copy of it. That affordance
    is only safe while the production path cannot reach it: the probe chooses
    the mutations, and the workflow passes none.
    """
    findings: list[str] = []
    if re.search(r"-Mutation\b", text):
        findings.append(
            "workflow: passes -Mutation, so the hosted acceptance may not be building the real package"
        )
    if re.search(r"build-msi\.ps1", text):
        findings.append(
            "workflow: invokes build-msi.ps1 directly; the MSI under acceptance is built by the probe"
        )
    return findings


def findings_for_builder(text: str) -> list[str]:
    findings = [
        f"build-msi.ps1: declares a '-{name}' parameter, which would let the identity of what is "
        "packaged be supplied at call time instead of travelling with the commit"
        for name in FORBIDDEN_BUILDER_PARAMETERS
        if re.search(rf"^\s*\[[^\]]*\]\s*\[\w+\]\s*\${name}\b", text, re.M)
    ]
    if "Invoke-WebRequest" in text or "curl" in text or "oras " in text:
        findings.append("build-msi.ps1: reaches the network; it packages a stage it is given")
    return findings


def findings_for_authoring(text: str) -> list[str]:
    findings: list[str] = []
    for argument in CONTRACT_ARGUMENTS:
        if argument not in text:
            findings.append(f"authoring: the W0 §4 argument '{argument}' does not appear")
    if 'Name="Tesserafin"' not in text:
        findings.append("authoring: the service is not named Tesserafin")
    if 'Start="install"' in text:
        findings.append(
            "authoring: ServiceControl starts the service inside the transaction. W0 §5.2 measured "
            "that failing with 1920 and rolling the whole install back to 1603"
        )
    if 'Permanent="yes"' not in text:
        findings.append(
            "authoring: no Permanent component, so the retained-data policy is not expressed in the package"
        )
    if "SuppressSignature" in text or "signtool" in text.lower():
        findings.append("authoring: signs the package, which this slice does not do")
    findings += findings_for_upgrade_code(text)
    return findings


def findings_for_upgrade_code(text: str) -> list[str]:
    """W4-A1: the authored UpgradeCode is the frozen one, byte for byte.

    `text` is already comment-stripped, so this grades what the package WOULD
    build with and never the prose about it. The comparison is ordinal against
    a single literal rather than a case-folded GUID parse, because a GUID parse
    would accept `{0F0C9F4E-...}` as equal and the ruling does not.
    """
    authored = re.findall(r'UpgradeCode\s*=\s*"([^"]*)"', text)
    if not authored:
        return ["authoring: no UpgradeCode attribute; W4-A1 froze one and the package must carry it"]
    if len(authored) > 1:
        return [f"authoring: {len(authored)} UpgradeCode attributes, so the frozen identity is ambiguous"]
    if authored[0] != FROZEN_UPGRADE_CODE:
        return [
            f"authoring: UpgradeCode is '{authored[0]}', but W4-A1 froze "
            f"'{FROZEN_UPGRADE_CODE}' -- ordinal, lowercase, no braces"
        ]
    return []


def findings_for_upgrade_code_prose(authoring_text: str, a0_text: str, a1_text: str) -> list[str]:
    """W4-A1: no comment or document may still describe the UpgradeCode as open.

    The authoring is read RAW here -- the comments are exactly what is being
    graded -- and the attestation is required to live in a comment, so the
    attribute satisfying it would not count.
    """
    findings: list[str] = []
    commentary = "\n".join(re.findall(r"<!--(.*?)-->", authoring_text, flags=re.S))
    for where, body in (("authoring comment", commentary), ("W4-A0 document", a0_text)):
        for pattern in UNFROZEN_CLAIM_PATTERNS:
            if re.search(pattern, body, re.I):
                findings.append(
                    f"{where}: still says the UpgradeCode is open ('{pattern}'); W4-A1 froze it"
                )
    for where, body in (("authoring comment", commentary), ("W4-A1 document", a1_text)):
        if "W4-A1" not in body:
            findings.append(f"{where}: does not cite the W4-A1 ruling that froze the UpgradeCode")
        if FROZEN_UPGRADE_CODE not in body:
            findings.append(f"{where}: does not state the frozen UpgradeCode {FROZEN_UPGRADE_CODE}")
    return findings


def grade_authoring(authoring_text: str, a0_text: str, a1_text: str) -> list[str]:
    """Everything graded off the authoring, mutable as one text for the self-test."""
    return findings_for_authoring(without_comments(authoring_text, "xml")) + findings_for_upgrade_code_prose(
        authoring_text, a0_text, a1_text
    )


def findings_for_wiring(text: str) -> list[str]:
    """The two harnesses must actually run, or they are decoration."""
    findings: list[str] = []
    if "assertion-self-test.ps1" not in text:
        findings.append("workflow: never runs the assertion self-test, so the grader is ungraded")
    if "msi-controls.py" not in text:
        findings.append("workflow: never runs these controls")
    if "probe-msi-skeleton.ps1" not in text:
        findings.append("workflow: never runs the MSI proof")
    return findings


def grade(workflow_text: str) -> list[str]:
    document = yaml.safe_load(workflow_text) or {}
    findings: list[str] = []
    findings += findings_for_permissions(document)
    findings += findings_for_triggers(document)
    findings += findings_for_artifacts(workflow_text)
    executable = without_comments(workflow_text, "yaml")
    findings += findings_for_mutation(executable)
    findings += findings_for_wiring(executable)
    return findings


def grade_everything(workflow_text: str) -> list[str]:
    return (
        grade(workflow_text)
        + findings_for_builder(BUILDER.read_text(encoding="utf-8"))
        + grade_authoring(
            AUTHORING.read_text(encoding="utf-8"),
            A0_DOC.read_text(encoding="utf-8"),
            A1_DOC.read_text(encoding="utf-8"),
        )
    )


def self_test_upgrade_code(authoring_text: str, a0_text: str, a1_text: str) -> list[str]:
    """W4-A1: the freeze gate must be reachable, in every shape the ruling names.

    The ruling's hostile control is "UpgradeCode in the wxs replaced by any
    other GUID, including the same digits with different case or braces", and
    the case and brace variants are precisely the ones a GUID-parsing gate
    would wave through -- so each is mutated here rather than argued about.
    The prose mutations cover the other half of the ruling: a pin that a
    comment or a document contradicts is not a pin.
    """
    attribute = f'UpgradeCode="{FROZEN_UPGRADE_CODE}"'
    mutations = {
        "a different GUID": lambda a, d0, d1: (
            a.replace(attribute, 'UpgradeCode="6b1e8d37-5f92-4a04-8e7c-3d05b9f2a618"', 1),
            d0,
            d1,
        ),
        "the same digits, upper case": lambda a, d0, d1: (
            a.replace(attribute, f'UpgradeCode="{FROZEN_UPGRADE_CODE.upper()}"', 1),
            d0,
            d1,
        ),
        "the same digits, braced": lambda a, d0, d1: (
            a.replace(attribute, f'UpgradeCode="{{{FROZEN_UPGRADE_CODE}}}"', 1),
            d0,
            d1,
        ),
        "no UpgradeCode at all": lambda a, d0, d1: (a.replace(attribute, "", 1), d0, d1),
        "the authoring calls it unfrozen": lambda a, d0, d1: (
            a.replace("The freeze reaches that one string", "It is unfrozen", 1),
            d0,
            d1,
        ),
        "the W4-A0 document calls it unfrozen": lambda a, d0, d1: (
            a,
            d0.replace("no longer does", "no longer does. Nothing here freezes them", 1),
            d1,
        ),
        "the W4-A1 document drops the GUID": lambda a, d0, d1: (
            a,
            d0,
            d1.replace(FROZEN_UPGRADE_CODE, "a GUID chosen at release time"),
        ),
    }
    failures: list[str] = []
    for name, mutate in mutations.items():
        mutated = mutate(authoring_text, a0_text, a1_text)
        if mutated == (authoring_text, a0_text, a1_text):
            failures.append(f"self-test '{name}': the mutation did not change the text")
            continue
        if not grade_authoring(*mutated):
            failures.append(f"self-test '{name}': the UpgradeCode freeze gate did not fire")
        else:
            print(f"  control OK   {name}")
    if not failures:
        print(f"  {len(mutations)} UpgradeCode freeze controls, all RED as declared")
    return failures


def self_test(workflow_text: str) -> list[str]:
    """Every gate above must be reachable. A gate nothing can trip is not a gate."""
    mutations = {
        "write-all": lambda t: t.replace("permissions:\n  contents: read", "permissions: write-all", 1),
        "quoted packages write": lambda t: t.replace(
            "      packages: read", '      "packages": "write"', 1
        ),
        "artifact upload": lambda t: t.replace(
            "    steps:", "    steps:\n      - uses: actions/upload-artifact@v4", 1
        ),
        "acceptance builds a mutation": lambda t: t.replace(
            "        run: |",
            "        run: |\n          ./ci/windows/w4/build-msi.ps1 -Mutation no-exe",
            1,
        ),
        "self-test dropped": lambda t: t.replace("assertion-self-test.ps1", "nothing.ps1"),
    }
    failures: list[str] = []
    for name, mutate in mutations.items():
        mutated = mutate(workflow_text)
        if mutated == workflow_text:
            failures.append(f"self-test '{name}': the mutation did not change the workflow text")
            continue
        if not grade(mutated):
            failures.append(f"self-test '{name}': the gate did not fire")
        else:
            print(f"  control OK   {name}")
    if not failures:
        print(f"  {len(mutations)} controls, all RED as declared")
    return failures


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true", help="also prove each gate can fire")
    options = parser.parse_args()

    for required in (WORKFLOW, AUTHORING, BUILDER, PROBE, SELF_TEST, A0_DOC, A1_DOC):
        if not required.is_file():
            print(f"W4-A0 CONTROLS REFUSED: missing {required.relative_to(REPO_ROOT)}")
            return 1

    workflow_text = WORKFLOW.read_text(encoding="utf-8")
    findings = grade_everything(workflow_text)

    print("W4-A0 static controls")
    if findings:
        for finding in findings:
            print(f"  FINDING  {finding}")
    else:
        print("  no findings")

    failures: list[str] = []
    if options.self_test:
        failures += self_test(workflow_text)
        failures += self_test_upgrade_code(
            AUTHORING.read_text(encoding="utf-8"),
            A0_DOC.read_text(encoding="utf-8"),
            A1_DOC.read_text(encoding="utf-8"),
        )
    for failure in failures:
        print(f"  FINDING  {failure}")

    if findings or failures:
        print(f"W4-A0 static controls FAILED: {len(findings) + len(failures)} finding(s)")
        return 1
    print("W4-A0 static controls: clean")
    return 0


if __name__ == "__main__":
    sys.exit(main())
