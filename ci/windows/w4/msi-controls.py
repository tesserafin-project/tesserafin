#!/usr/bin/env python3
"""W4-A0 and W4-A2 (#234) -- the controls that do not need a Windows host.

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

W4-A2 adds one more property of the authored files: that every `ServiceInstall`
the authoring states carries the W0 §4 recovery policy. That is a statement
about the source text, so it is checked here rather than being left to the
hosted run -- which measures the other half, what the Service Control Manager
actually ended up with.

W4-A2-R1 adds three more, all of them about HOW that policy is authored after
the core `ServiceConfigFailureActions` element made `MsiConfigureServices`
answer MSI error 1939: that the core element is GONE, that the `util` namespace
the replacement needs is declared, and that `WixToolset.Util.wixext` is pinned
by exact version on the `wix` command line rather than in
`Directory.Packages.props` -- which the ruling forbids inventing an entry in,
and which nothing on this path restores through anyway.

Two modes:

    (default)     grade the real files; exit 1 on any finding
    --self-test   ALSO mutate copies of the workflow, the authoring and the
                  documents and require each mutation to be caught. A gate that
                  cannot be made to fail has not been shown to be a gate.
"""

from __future__ import annotations

import argparse
import hashlib
import pathlib
import re
import struct
import sys

import yaml

REPO_ROOT = pathlib.Path(__file__).resolve().parents[3]
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "w4-windows-msi.yml"
AUTHORING = REPO_ROOT / "packaging" / "windows" / "msi" / "Tesserafin.wxs"
BUILDER = REPO_ROOT / "ci" / "windows" / "w4" / "build-msi.ps1"
PROBE = REPO_ROOT / "ci" / "windows" / "w4" / "probe-msi-skeleton.ps1"
SELF_TEST = REPO_ROOT / "ci" / "windows" / "w4" / "assertion-self-test.ps1"
PACKAGE_PROPS = REPO_ROOT / "Directory.Packages.props"
A0_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A0-wix-skeleton.md"
A1_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A1-upgradecode.md"
A2_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A2-service-recovery.md"
A3_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A3-programdata-acls.md"

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

# W4-A2 (#234), as amended by the W4-A2-R1 ruling. W0 §4's recovery row, as the
# authoring must state it:
#
#     restart after 60 s on first and second failure; no action on the third,
#     so a crash loop is visible rather than hidden
#
# The element is `util:ServiceConfig` from `WixToolset.Util.wixext`, NOT the
# core `ServiceConfigFailureActions`. The ruling replaced the core element
# because the `MsiServiceConfigFailureActions` table it writes made
# `MsiConfigureServices` answer MSI error 1939 under InstallFinalize and roll
# the install back to 1603.
#
# The units are the EXTENSION'S. Its custom action multiplies
# `RestartServiceDelayInSeconds` by 1000 into SC_ACTION.Delay and
# `ResetPeriodInDays` by 86400 into SERVICE_FAILURE_ACTIONS.dwResetPeriod, so
# `60` and `1` here are the `restart/60000/restart/60000//0` and `reset= 86400`
# that `W4MsiAssertions.psm1` reads back off the live service in the SCM's own
# units. Neither file restates the other's numbers.
#
# The third entry is graded as hard as the first two. The SCM repeats the LAST
# configured action for every failure past the end of the array, so an authoring
# that stopped after two restarts would restart forever, which is the outcome
# §4's third row exists to refuse.
#
# This is a PRESENCE gate, deliberately. The authoring carries deliberately
# broken recovery policies too -- they are how the hostile controls drive the
# real authoring -- so "no element with the wrong delay appears" is not a
# property this file can have. What it can have, and what is checked, is that
# every authored ServiceInstall carries the correct policy -- and, since the
# ruling, that the core element is gone from the file entirely.
CONTRACT_FAILURE_ACTIONS = (
    '<util:ServiceConfig FirstFailureActionType="restart"'
    ' SecondFailureActionType="restart"'
    ' ThirdFailureActionType="none"'
    ' RestartServiceDelayInSeconds="60"'
    ' ResetPeriodInDays="1" />'
)

# The namespace declaration the element above cannot be linked without. WiX
# resolves an unbound prefix at compile time, so this is belt and braces -- but
# the failure it prevents is a `wix build` error two hours into a hosted run.
CONTRACT_UTIL_NAMESPACE = 'xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util"'

# The core element the ruling removed. Its presence anywhere in the executable
# authoring is RED: it is the element that reaches 1939, and one left behind on
# any ServiceInstall variant would put the `MsiServiceConfigFailureActions`
# table back into the package. The ruling names this as a hostile control it
# expects to have been observed RED.
CORE_FAILURE_ACTIONS_ELEMENT = "<ServiceConfigFailureActions"

# The extension, pinned by exact version on the `wix` command line -- which is
# where the ruling requires the pin to live, and which `build-msi.ps1` is the
# only file to state. `Directory.Packages.props` is not consulted by anything on
# this path: `wix` is a `dotnet tool` and there is no `.wixproj` in the tree, so
# there is no NuGet restore for central package management to govern.
CONTRACT_UTIL_EXTENSION = "WixToolset.Util.wixext"

# The single line the W4-A2 document must carry, in the SCM's own notation, so
# the document and the authoring cannot drift apart silently.
CONTRACT_SC_POLICY = "restart/60000/restart/60000//0"

# W4-A0 §3 originally claimed all of W0 §4's table. The ruling names that
# over-claim, and a straight revert of the corrected sentence trips this.
A0_OVERCLAIM_PATTERN = r"W0\s+§4's\s+table\s+is\s+implemented\s+as\s+written"


# ---------------------------------------------------------------------------
# W4-A3 (#234). W0 §9.3, as the authoring must state it.
#
# The account is a VIRTUAL SERVICE ACCOUNT, and SDDL takes SIDs. The SID of
# `NT SERVICE\<name>` is S-1-5-80 followed by the SHA-1 of the upper-case
# UTF-16LE service name read as five little-endian DWORDs -- deterministic, the
# same on every machine, and valid before the service exists. It is RECOMPUTED
# here from the service name rather than copied from the authoring, because a
# well-formed SDDL that names some other SID installs perfectly and grants
# nobody anything: an eyeballed 41-character number is exactly the kind of
# constant a reviewer cannot check and a gate can.
# ---------------------------------------------------------------------------
SERVICE_ACCOUNT_NAME = "Tesserafin"

# File-specific access masks, in the units Get-Acl reports FileSystemRights in.
# The SDDL generic aliases (GA, GR, GX) are deliberately not used anywhere: they
# come back off a live ACL as raw numbers no predicate could compare against a
# named right.
RIGHTS_FULL_CONTROL = 0x1F01FF
RIGHTS_MODIFY = 0x1301BF
RIGHTS_READ_EXECUTE = 0x1200A9

SID_ADMINISTRATORS = "BA"
SID_LOCAL_SYSTEM = "SY"

# The SDDL abbreviations for every identity §9.3 means by `Users`, plus the
# well-known SIDs the same grant is sometimes written with. Any of them holding
# a write bit under %ProgramData%\Tesserafin\ is the finding.
UNPRIVILEGED_SDDL_SIDS = frozenset(
    {"BU", "AU", "WD", "BG", "S-1-5-32-545", "S-1-5-11", "S-1-1-0", "S-1-5-32-546"}
)

# Every bit that lets the holder change something, and the two generic aliases
# that would smuggle all of them past a mask comparison. Stated here and in
# `W4MsiAssertions.psm1`; neither file computes the other's value.
RIGHTS_WRITE_MASK = (
    0x00000002 | 0x00000004 | 0x00000010 | 0x00000040 | 0x00000100
    | 0x00010000 | 0x00040000 | 0x00080000 | 0x10000000 | 0x40000000
)

# The mechanism, settled by measurement rather than by preference. The W4-A3
# ruling prefers `util:PermissionEx` "if Util 6.0.2 already covers it", and it
# does not: the compiler answers WIX0004 for `Sddl`, requires `User`, and knows
# no Protected / DenyInheritance / NoInheritance / ReplaceExisting attribute, so
# the element can add an ACE and cannot break inheritance. The core
# `PermissionEx` element writes the Windows Installer 5.0 MsiLockPermissionsEx
# table and needs no extension at all, so no second extension is taken.
#
# The core `Permission` element writes the OTHER table, LockPermissions.
# Windows Installer refuses a package carrying both, and LockPermissions always
# discards inherited permissions -- which would take the choice this slice is
# about away from the authoring. Its presence is RED.
CORE_PERMISSION_ELEMENT = "<Permission "
CORE_PERMISSION_EX_ELEMENT = "<PermissionEx "
UTIL_PERMISSION_EX_ELEMENT = "<util:PermissionEx"

# The two preprocessor variables every authored descriptor must come from. An
# SDDL written inline at a PermissionEx would be a descriptor no mutation
# reaches and no gate below parses.
SDDL_DEFINE_NAMES = ("DataRootSddl", "InstallFolderSddl")


def service_account_sid(name: str) -> str:
    """The SID of `NT SERVICE\\<name>`, computed rather than quoted."""
    digest = hashlib.sha1(name.upper().encode("utf-16-le")).digest()
    return "S-1-5-80-" + "-".join(
        str(value) for value in struct.unpack("<5I", digest)
    )


def parse_sddl_dacl(sddl: str) -> tuple[bool, list[dict]] | None:
    """Split an SDDL DACL into (protected, [ace, ...]), or None if malformed.

    Deliberately small and deliberately strict: this parses the shape the
    authoring is allowed to write, not the whole of SDDL. Anything it cannot
    read is a finding rather than something to be lenient about -- an SDDL the
    gate silently skipped would be an SDDL nothing checks.
    """
    if not sddl.startswith("D:"):
        return None
    body = sddl[2:]
    flags = ""
    while body and body[0] not in "(":
        flags += body[0]
        body = body[1:]
    if set(flags) - set("PARI"):
        return None
    aces: list[dict] = []
    for match in re.finditer(r"\(([^()]*)\)", body):
        fields = match.group(1).split(";")
        if len(fields) != 6:
            return None
        ace_type, ace_flags, rights, object_guid, inherit_guid, sid = fields
        if object_guid or inherit_guid:
            return None
        try:
            mask = int(rights, 16) if rights.lower().startswith("0x") else None
        except ValueError:
            return None
        if mask is None:
            return None
        aces.append({"type": ace_type, "flags": ace_flags, "mask": mask, "sid": sid})
    # Every parenthesised group has to have been an ACE, or something was
    # dropped silently.
    if "".join(f"({ace_text})" for ace_text in re.findall(r"\(([^()]*)\)", body)) != body:
        return None
    if not aces:
        return None
    return ("P" in flags, aces)


def allow_mask_for(aces: list[dict], sids: set[str]) -> int:
    mask = 0
    for ace in aces:
        if ace["type"] == "A" and ace["sid"] in sids:
            mask |= ace["mask"]
    return mask


def sddl_defines(text: str) -> dict[str, list[str]]:
    """Every `<?define <Name> = "<sddl>" ?>` in the authoring, by variable."""
    found: dict[str, list[str]] = {name: [] for name in SDDL_DEFINE_NAMES}
    for name, value in re.findall(
        r"<\?define\s+(\w+)\s*=\s*\"([^\"]*)\"\s*\?>", text
    ):
        if name in found:
            found[name].append(value)
    return found


def findings_for_acls(text: str) -> list[str]:
    """W4-A3: the §9.3 descriptors, parsed rather than pattern-matched.

    `text` is comment-stripped, so this grades what the package WOULD build
    with. Like the recovery gate above it is a PRESENCE gate over a file that
    deliberately carries broken variants too: what it can assert is that the
    real descriptors are stated exactly once each and say exactly what §9.3
    says, and that EVERY variant -- mutants included -- keeps the rows no
    control is about.
    """
    findings: list[str] = []
    sid = service_account_sid(SERVICE_ACCOUNT_NAME)

    if CORE_PERMISSION_ELEMENT in text:
        findings.append(
            "authoring: the core Permission element is present. It writes the LockPermissions "
            "table, Windows Installer refuses a package carrying both permission tables, and "
            "LockPermissions always discards inherited permissions"
        )
    if UTIL_PERMISSION_EX_ELEMENT in text:
        findings.append(
            "authoring: util:PermissionEx is used. It cannot express W0 §9.3 -- it takes no Sddl "
            "and knows no protection attribute, so it can add an ACE and cannot break inheritance"
        )
    if CORE_PERMISSION_EX_ELEMENT not in text:
        findings.append(
            "authoring: no PermissionEx element, so the package applies none of the W0 §9.3 ACLs"
        )

    # Every descriptor the authoring applies comes from one of the two
    # variables the gates below parse.
    for applied in re.findall(r"<PermissionEx\s+Sddl=\"([^\"]*)\"", text):
        if applied not in tuple(f"$(var.{name})" for name in SDDL_DEFINE_NAMES):
            findings.append(
                f"authoring: a PermissionEx applies '{applied}', which is not one of the "
                f"{' / '.join(SDDL_DEFINE_NAMES)} variables every gate here parses"
            )

    defines = sddl_defines(text)
    for name in SDDL_DEFINE_NAMES:
        if not defines[name]:
            findings.append(f"authoring: no {name} is defined, so nothing states the W0 §9.3 grant")

    # Invariants over EVERY variant, mutants included. None of the authorised
    # controls is about the administrative rights or about the service account's
    # identity, so a variant that moved either would redden predicates it never
    # declared and be attributable to nothing.
    for name in SDDL_DEFINE_NAMES:
        for value in defines[name]:
            parsed = parse_sddl_dacl(value)
            if parsed is None:
                findings.append(f"authoring: {name} '{value}' is not a DACL this gate can read")
                continue
            _, aces = parsed
            for ace in aces:
                if ace["type"] != "A":
                    findings.append(
                        f"authoring: {name} carries a '{ace['type']}' ACE. This package grants; "
                        "it denies nothing, and a deny ACE reaches every member of the group"
                    )
                if "OI" not in ace["flags"] or "CI" not in ace["flags"]:
                    findings.append(
                        f"authoring: {name} has an ACE with flags '{ace['flags']}'. Every ACE must "
                        "be OICI or the directories below it do not inherit the grant"
                    )
                if ace["sid"] not in {SID_ADMINISTRATORS, SID_LOCAL_SYSTEM, sid} | UNPRIVILEGED_SDDL_SIDS:
                    findings.append(
                        f"authoring: {name} names the SID '{ace['sid']}', which is not the service "
                        "account, an administrative identity, or one of the unprivileged "
                        "identities a control is allowed to plant"
                    )
            if name == "DataRootSddl":
                for who, label in ((SID_ADMINISTRATORS, "Administrators"), (SID_LOCAL_SYSTEM, "SYSTEM")):
                    if allow_mask_for(aces, {who}) & RIGHTS_FULL_CONTROL != RIGHTS_FULL_CONTROL:
                        findings.append(
                            f"authoring: {name} '{value}' does not grant {label} Full. W0 §9.3 "
                            "requires it of every variant, so no control reddens it as collateral"
                        )

    findings += findings_for_real_descriptors(defines, sid)
    return findings


def findings_for_real_descriptors(defines: dict[str, list[str]], sid: str) -> list[str]:
    """The two descriptors the REAL package installs, stated exactly.

    The authoring carries deliberately broken variants, so these are asked for
    by value: the contract string must be among the variants defined, exactly
    once, and must say exactly what W0 §9.3 says and nothing more.
    """
    findings: list[str] = []
    contracts = {
        "DataRootSddl": (
            "D:P"
            f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_ADMINISTRATORS})"
            f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_LOCAL_SYSTEM})"
            f"(A;OICI;0x{RIGHTS_MODIFY:x};;;{sid})"
        ),
        "InstallFolderSddl": f"D:(A;OICI;0x{RIGHTS_READ_EXECUTE:x};;;{sid})",
    }
    for name, contract in contracts.items():
        occurrences = defines[name].count(contract)
        if occurrences == 0:
            findings.append(
                f"authoring: no {name} states the W0 §9.3 descriptor '{contract}'. Every variant "
                f"defined is: {defines[name] or 'none'}"
            )
        elif occurrences > 1:
            findings.append(
                f"authoring: {name} states the W0 §9.3 descriptor {occurrences} times, so which "
                "one the real package builds with is ambiguous"
            )

    # The one property the string comparison above would not explain if it
    # failed, spelled out so a reviewer reading a finding knows what broke.
    data_root = contracts["DataRootSddl"]
    if data_root in defines["DataRootSddl"]:
        protected, aces = parse_sddl_dacl(data_root)
        if not protected:
            findings.append("authoring: the real DataRootSddl is not protected, so inheritance is not broken")
        if allow_mask_for(aces, {sid}) & RIGHTS_MODIFY != RIGHTS_MODIFY:
            findings.append(f"authoring: the real DataRootSddl does not grant {sid} Modify")
        if allow_mask_for(aces, UNPRIVILEGED_SDDL_SIDS) != 0:
            findings.append("authoring: the real DataRootSddl grants an unprivileged identity rights")
    install = contracts["InstallFolderSddl"]
    if install in defines["InstallFolderSddl"]:
        protected, aces = parse_sddl_dacl(install)
        if protected:
            findings.append(
                "authoring: the real InstallFolderSddl is protected. W0 §9.3 breaks inheritance at "
                "%ProgramData%\\Tesserafin\\ and nowhere else"
            )
        if allow_mask_for(aces, {sid}) & RIGHTS_READ_EXECUTE != RIGHTS_READ_EXECUTE:
            findings.append(f"authoring: the real InstallFolderSddl does not grant {sid} read and execute")
        if allow_mask_for(aces, {sid}) & RIGHTS_WRITE_MASK:
            findings.append(
                f"authoring: the real InstallFolderSddl grants {sid} a write bit. W0 §9.3: the "
                "service must not be able to rewrite its own binaries or its own FFmpeg"
            )
    return findings


def findings_for_acl_prose(a3_text: str) -> list[str]:
    """The A3 document says what the package does, in the package's own terms."""
    findings: list[str] = []
    sid = service_account_sid(SERVICE_ACCOUNT_NAME)
    if "W4-A3" not in a3_text:
        findings.append("W4-A3 document: does not cite the W4-A3 ruling it records")
    if sid not in a3_text:
        findings.append(f"W4-A3 document: does not state the service account SID {sid}")
    if "D:P" not in a3_text:
        findings.append(
            "W4-A3 document: does not state the protected DACL that breaks inheritance"
        )
    return findings


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
    findings += findings_for_extension_pin(text)
    return findings


def findings_for_extension_pin(text: str) -> list[str]:
    """W4-A2-R1 (#234): the extension is pinned by EXACT version, on the command line.

    The ruling is specific about both halves. The pin is exact, in the same
    style as the `wix` tool pin, and it lives on the `wix` command line -- not
    in `Directory.Packages.props`, which the ruling forbids inventing an entry
    in. That second half is checkable from here and is checked: `wix` is a
    `dotnet tool` and the tree carries no `.wixproj`, so nothing on this path is
    a NuGet restore central package management could govern, and an entry there
    would be a pin that governs nothing while reading like the real one.
    """
    findings: list[str] = []
    if CONTRACT_UTIL_EXTENSION not in text:
        findings.append(
            f"build-msi.ps1: never names {CONTRACT_UTIL_EXTENSION}, so util:ServiceConfig "
            "cannot link and the W0 §4 recovery policy is not in the package"
        )
        return findings
    if not re.search(r"-ext\s", text):
        findings.append(
            "build-msi.ps1: passes no -ext to wix build, so the extension is installed and "
            "never used"
        )
    if not re.search(
        r"\$UtilExtensionVersion\s*=\s*'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'", text
    ):
        findings.append(
            "build-msi.ps1: the extension version is not pinned to an exact MAJOR.MINOR.PATCH"
        )
    if "wix extension add" not in text:
        findings.append("build-msi.ps1: never acquires the extension at the pinned version")
    if "wix extension list" not in text:
        findings.append(
            "build-msi.ps1: never reads the installed extension version back, so the pin is "
            "asserted rather than measured"
        )
    wixprojects = sorted(q.relative_to(REPO_ROOT).as_posix() for q in REPO_ROOT.rglob("*.wixproj"))
    if wixprojects:
        findings.append(
            "the tree carries a .wixproj (" + ", ".join(wixprojects) + "), so the WiX path is a "
            "NuGet restore after all and the command-line pin is no longer the whole pin"
        )
    if PACKAGE_PROPS.is_file() and CONTRACT_UTIL_EXTENSION in PACKAGE_PROPS.read_text(
        encoding="utf-8"
    ):
        findings.append(
            f"Directory.Packages.props: carries {CONTRACT_UTIL_EXTENSION}. The ruling forbids "
            "inventing an entry there, and nothing on the wix path restores through it, so the "
            "entry would govern nothing while reading like the pin"
        )
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
    findings += findings_for_failure_actions(text)
    findings += findings_for_acls(text)
    return findings


def normalised(text: str) -> str:
    """Collapse runs of whitespace, so an indentation change is not a finding."""
    return re.sub(r"\s+", " ", text)


def findings_for_failure_actions(text: str) -> list[str]:
    """W4-A2: every authored ServiceInstall carries the W0 §4 recovery policy.

    `text` is already comment-stripped, so this grades what the package WOULD
    build with. The count is compared against the number of `ServiceInstall`
    elements rather than against a fixed number: the authoring states the
    service once per argument variant, and a variant that quietly lost its
    recovery row would otherwise be invisible here and only surface on a runner.
    """
    flat = normalised(text)
    findings: list[str] = []
    if CORE_FAILURE_ACTIONS_ELEMENT in flat:
        findings.append(
            "authoring: the core ServiceConfigFailureActions element is still present. It writes "
            "the MsiServiceConfigFailureActions table, which is what MsiConfigureServices answered "
            "MSI error 1939 for, rolling the install back to 1603; W4-A2-R1 replaced it with "
            "util:ServiceConfig and requires it gone"
        )
    if CONTRACT_UTIL_NAMESPACE not in flat:
        findings.append(
            "authoring: the util namespace is not declared, so util:ServiceConfig cannot link"
        )
    authored = flat.count(CONTRACT_FAILURE_ACTIONS)
    services = flat.count("<ServiceInstall ")
    if authored == 0:
        return findings + [
            "authoring: no W0 §4 recovery policy is authored. The SCM's default is to do "
            "nothing on any failure, so the first and second restart would never happen"
        ]
    if authored != services:
        return findings + [
            f"authoring: {services} ServiceInstall element(s) but {authored} carry the W0 §4 "
            "recovery policy, so at least one registers a service the SCM would recover "
            "differently"
        ]
    return findings


def findings_for_failure_actions_prose(a0_text: str, a2_text: str) -> list[str]:
    """W4-A2: the documents say what the package does, and no more.

    The W4-A0 document's §3 originally claimed W0 §4's table whole. It never
    implemented the recovery, stop-timeout or logging rows, and the ruling
    authorises correcting exactly that sentence -- so a revert of it is RED.
    """
    findings: list[str] = []
    if re.search(A0_OVERCLAIM_PATTERN, a0_text):
        findings.append(
            "W4-A0 document: still claims W0 §4's table is implemented as written, but W4-A0 "
            "implemented neither the recovery row (W4-A2) nor the stop-timeout and logging rows"
        )
    if "W4-A2" not in a2_text:
        findings.append("W4-A2 document: does not cite the W4-A2 ruling it records")
    if CONTRACT_SC_POLICY not in a2_text:
        findings.append(
            f"W4-A2 document: does not state the W0 §4 failure policy '{CONTRACT_SC_POLICY}'"
        )
    if "86400" not in a2_text:
        findings.append("W4-A2 document: does not state the W0 §4 reset period 86400")
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


def grade_authoring(
    authoring_text: str, a0_text: str, a1_text: str, a2_text: str, a3_text: str
) -> list[str]:
    """Everything graded off the authoring, mutable as one text for the self-test."""
    return (
        findings_for_authoring(without_comments(authoring_text, "xml"))
        + findings_for_upgrade_code_prose(authoring_text, a0_text, a1_text)
        + findings_for_failure_actions_prose(a0_text, a2_text)
        + findings_for_acl_prose(a3_text)
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
            A2_DOC.read_text(encoding="utf-8"),
            A3_DOC.read_text(encoding="utf-8"),
        )
    )


def self_test_upgrade_code(
    authoring_text: str, a0_text: str, a1_text: str, a2_text: str, a3_text: str
) -> list[str]:
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
        "a different GUID": lambda a, d0, d1, d2, d3: (
            a.replace(attribute, 'UpgradeCode="6b1e8d37-5f92-4a04-8e7c-3d05b9f2a618"', 1),
            d0,
            d1,
            d2,
            d3,
        ),
        "the same digits, upper case": lambda a, d0, d1, d2, d3: (
            a.replace(attribute, f'UpgradeCode="{FROZEN_UPGRADE_CODE.upper()}"', 1),
            d0,
            d1,
            d2,
            d3,
        ),
        "the same digits, braced": lambda a, d0, d1, d2, d3: (
            a.replace(attribute, f'UpgradeCode="{{{FROZEN_UPGRADE_CODE}}}"', 1),
            d0,
            d1,
            d2,
            d3,
        ),
        "no UpgradeCode at all": lambda a, d0, d1, d2, d3: (a.replace(attribute, "", 1), d0, d1, d2, d3),
        "the authoring calls it unfrozen": lambda a, d0, d1, d2, d3: (
            a.replace("The freeze reaches that one string", "It is unfrozen", 1),
            d0,
            d1,
            d2,
            d3,
        ),
        "the W4-A0 document calls it unfrozen": lambda a, d0, d1, d2, d3: (
            a,
            d0.replace("no longer does", "no longer does. Nothing here freezes them", 1),
            d1,
            d2,
            d3,
        ),
        "the W4-A1 document drops the GUID": lambda a, d0, d1, d2, d3: (
            a,
            d0,
            d1.replace(FROZEN_UPGRADE_CODE, "a GUID chosen at release time"),
            d2,
            d3,
        ),
    }
    return run_authoring_self_test(
        "UpgradeCode freeze", mutations, authoring_text, a0_text, a1_text, a2_text, a3_text
    )


def self_test_failure_actions(
    authoring_text: str, a0_text: str, a1_text: str, a2_text: str, a3_text: str
) -> list[str]:
    """W4-A2: the recovery gate must be reachable, in each shape the ruling names.

    Every mutation here replaces EVERY occurrence, never the first -- with the
    one deliberate exception of "one ServiceInstall loses its recovery row",
    which is the whole point of that control. The authoring carries
    deliberately broken recovery policies of its own -- they are how the
    hostile controls drive the real authoring -- so a first-occurrence replace
    would land on a control branch and leave the real policy, and the gate
    would correctly stay green while the self-test claimed it had been tripped.

    W4-A2-R1 (#234) added the shape the ruling names last: "core
    ServiceConfigFailureActions still present (must be gone)". It is mutated
    back IN rather than argued about, because a gate for the absence of
    something is the kind that is easiest to write inert.
    """
    mutations = {
        "no util:ServiceConfig authored": lambda a, d0, d1, d2, d3: (
            re.sub(r"\s*<util:ServiceConfig\b.*?/>", "", a, flags=re.S),
            d0,
            d1,
            d2,
            d3,
        ),
        "the util namespace is not declared": lambda a, d0, d1, d2, d3: (
            a.replace("\n" + "     " + CONTRACT_UTIL_NAMESPACE, "", 1),
            d0,
            d1,
            d2,
            d3,
        ),
        "the core ServiceConfigFailureActions element is back": lambda a, d0, d1, d2, d3: (
            a.replace(
                "</ServiceInstall>",
                '  <ServiceConfigFailureActions OnInstall="yes" ResetPeriod="86400">'
                ' <Failure Action="restartService" Delay="60000" />'
                " </ServiceConfigFailureActions>\n          </ServiceInstall>",
                1,
            ),
            d0,
            d1,
            d2,
            d3,
        ),
        "the restart delay is not 60 s": lambda a, d0, d1, d2, d3: (
            a.replace('RestartServiceDelayInSeconds="60"', 'RestartServiceDelayInSeconds="1"'),
            d0,
            d1,
            d2,
            d3,
        ),
        "the third failure is a restart": lambda a, d0, d1, d2, d3: (
            a.replace('ThirdFailureActionType="none"', 'ThirdFailureActionType="restart"'),
            d0,
            d1,
            d2,
            d3,
        ),
        "the reset period is not one day": lambda a, d0, d1, d2, d3: (
            a.replace('ResetPeriodInDays="1"', 'ResetPeriodInDays="3"'),
            d0,
            d1,
            d2,
            d3,
        ),
        "the second failure is not a restart": lambda a, d0, d1, d2, d3: (
            a.replace('SecondFailureActionType="restart"', 'SecondFailureActionType="none"'),
            d0,
            d1,
            d2,
            d3,
        ),
        # The one mutation here that is deliberately NOT global. The first
        # util:ServiceConfig in the file is one of the correct ones, so removing
        # it leaves the authoring with more ServiceInstall elements than
        # policies -- which is exactly the drift the count comparison exists to
        # catch, and which no whole-file replace could ever produce.
        "one ServiceInstall loses its recovery row": lambda a, d0, d1, d2, d3: (
            re.sub(r"\s*<util:ServiceConfig\b.*?/>", "", a, count=1, flags=re.S),
            d0,
            d1,
            d2,
            d3,
        ),
        "the W4-A0 document reclaims the whole §4 table": lambda a, d0, d1, d2, d3: (
            a,
            d0.replace(
                "The six rows W4-A0 implements are implemented as written",
                "W0 §4's table is implemented as written",
                1,
            ),
            d1,
            d2,
            d3,
        ),
        "the W4-A2 document drops the policy": lambda a, d0, d1, d2, d3: (
            a,
            d0,
            d1,
            d2.replace(CONTRACT_SC_POLICY, "a policy chosen at install time"),
            d3,
        ),
    }
    return run_authoring_self_test(
        "recovery", mutations, authoring_text, a0_text, a1_text, a2_text, a3_text
    )


def self_test_acls(
    authoring_text: str, a0_text: str, a1_text: str, a2_text: str, a3_text: str
) -> list[str]:
    """W4-A3: the §9.3 gate must be reachable, in each shape §9.3 rules out.

    Every mutation replaces the REAL descriptor, never the first occurrence:
    the authoring carries four deliberately broken variants of its own, they
    are how the hostile controls drive the real authoring, and a
    first-occurrence replace would land on one of them and leave the real
    descriptor standing while the self-test claimed it had been tripped.

    The two "mechanism" mutations are the ones easiest to write inert, because
    each is a gate for the ABSENCE of something.
    """
    sid = service_account_sid(SERVICE_ACCOUNT_NAME)
    real_data_root = (
        "D:P"
        f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_ADMINISTRATORS})"
        f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_LOCAL_SYSTEM})"
        f"(A;OICI;0x{RIGHTS_MODIFY:x};;;{sid})"
    )
    real_install = f"D:(A;OICI;0x{RIGHTS_READ_EXECUTE:x};;;{sid})"

    def swap(old: str, new: str):
        return lambda a, d0, d1, d2, d3: (a.replace(old, new, 1), d0, d1, d2, d3)

    def define(name: str, value: str) -> str:
        """The whole `<?define ?>`, not the SDDL alone.

        The real data-root descriptor is a PREFIX of two of the mutant ones --
        `acl-users-write` is it plus a `Users` ACE -- and the mutants are
        authored first, so a replace of the bare SDDL would land on a control
        branch and leave the real descriptor standing while this self-test
        claimed it had been tripped. That is the exact failure mode the
        recovery self-test documents for `util:ServiceConfig`.
        """
        return f'<?define {name} = "{value}" ?>'

    def swap_real(name: str, value: str, replacement: str):
        return swap(define(name, value), define(name, replacement))

    mutations = {
        "the data root descriptor is not protected": swap_real(
            "DataRootSddl", real_data_root, real_data_root.replace("D:P", "D:", 1)
        ),
        "the data root hands Users Modify": swap_real(
            "DataRootSddl", real_data_root, real_data_root + f"(A;OICI;0x{RIGHTS_MODIFY:x};;;BU)"
        ),
        "the service account gets no grant at all": swap_real(
            "DataRootSddl",
            real_data_root,
            real_data_root.replace(f"(A;OICI;0x{RIGHTS_MODIFY:x};;;{sid})", "", 1),
        ),
        "the service account SID is a different one": swap(
            sid, service_account_sid("TesserafinServer")
        ),
        "INSTALLFOLDER grants the service Modify": swap_real(
            "InstallFolderSddl", real_install, f"D:(A;OICI;0x{RIGHTS_MODIFY:x};;;{sid})"
        ),
        "INSTALLFOLDER is protected too": swap_real(
            "InstallFolderSddl", real_install, "D:P" + real_install[2:]
        ),
        "an ACE is not inheritable": swap(
            f"(A;OICI;0x{RIGHTS_MODIFY:x};;;{sid})", f"(A;;0x{RIGHTS_MODIFY:x};;;{sid})"
        ),
        "the data root stops granting SYSTEM Full": swap(
            f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_LOCAL_SYSTEM})", ""
        ),
        "the rights are an SDDL generic alias": swap(
            f"0x{RIGHTS_READ_EXECUTE:x};;;{sid}", f"GRGX;;;{sid}"
        ),
        "a descriptor is applied inline instead of through a variable": swap(
            '<PermissionEx Sddl="$(var.InstallFolderSddl)" />',
            '<PermissionEx Sddl="D:(A;OICI;0x1f01ff;;;WD)" />',
        ),
        "the core Permission element is used": swap(
            '<PermissionEx Sddl="$(var.DataRootSddl)" />',
            '<Permission User="Everyone" GenericAll="yes" />',
        ),
        "util:PermissionEx is used instead": swap(
            '<PermissionEx Sddl="$(var.DataRootSddl)" />',
            '<util:PermissionEx User="Tesserafin" Domain="NT SERVICE" GenericAll="yes" />',
        ),
        "no PermissionEx is authored at all": lambda a, d0, d1, d2, d3: (
            re.sub(r"\s*<PermissionEx\b[^>]*/>", "", a),
            d0,
            d1,
            d2,
            d3,
        ),
        "the W4-A3 document drops the SID": lambda a, d0, d1, d2, d3: (
            a,
            d0,
            d1,
            d2,
            d3.replace(sid, "the service account's SID"),
        ),
        "the W4-A3 document drops the protected DACL": lambda a, d0, d1, d2, d3: (
            a,
            d0,
            d1,
            d2,
            d3.replace("D:P", "a descriptor"),
        ),
    }
    return run_authoring_self_test(
        "ACL", mutations, authoring_text, a0_text, a1_text, a2_text, a3_text
    )


def run_authoring_self_test(
    label: str,
    mutations: dict,
    authoring_text: str,
    a0_text: str,
    a1_text: str,
    a2_text: str,
    a3_text: str,
) -> list[str]:
    """Apply each mutation and require the authoring gates to catch every one."""
    original = (authoring_text, a0_text, a1_text, a2_text, a3_text)
    failures: list[str] = []
    for name, mutate in mutations.items():
        mutated = mutate(*original)
        if mutated == original:
            failures.append(f"self-test '{name}': the mutation did not change the text")
            continue
        if not grade_authoring(*mutated):
            failures.append(f"self-test '{name}': the {label} gate did not fire")
        else:
            print(f"  control OK   {name}")
    if not failures:
        print(f"  {len(mutations)} {label} controls, all RED as declared")
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

    for required in (WORKFLOW, AUTHORING, BUILDER, PROBE, SELF_TEST, A0_DOC, A1_DOC, A2_DOC, A3_DOC):
        if not required.is_file():
            print(f"W4 CONTROLS REFUSED: missing {required.relative_to(REPO_ROOT)}")
            return 1

    workflow_text = WORKFLOW.read_text(encoding="utf-8")
    findings = grade_everything(workflow_text)

    print("W4-A0 / W4-A2 / W4-A3 static controls")
    if findings:
        for finding in findings:
            print(f"  FINDING  {finding}")
    else:
        print("  no findings")

    failures: list[str] = []
    if options.self_test:
        failures += self_test(workflow_text)
        documents = (
            AUTHORING.read_text(encoding="utf-8"),
            A0_DOC.read_text(encoding="utf-8"),
            A1_DOC.read_text(encoding="utf-8"),
            A2_DOC.read_text(encoding="utf-8"),
            A3_DOC.read_text(encoding="utf-8"),
        )
        failures += self_test_upgrade_code(*documents)
        failures += self_test_failure_actions(*documents)
        failures += self_test_acls(*documents)
    for failure in failures:
        print(f"  FINDING  {failure}")

    if findings or failures:
        print(f"W4 static controls FAILED: {len(findings) + len(failures)} finding(s)")
        return 1
    print("W4 static controls: clean")
    return 0


if __name__ == "__main__":
    sys.exit(main())
