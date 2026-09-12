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
PROBE_UPGRADE = REPO_ROOT / "ci" / "windows" / "w4" / "probe-msi-upgrade.ps1"
PROBE_EVENTLOG = REPO_ROOT / "ci" / "windows" / "w4" / "probe-msi-eventlog.ps1"
INSTRUMENTS = REPO_ROOT / "ci" / "windows" / "w4" / "W4MsiInstruments.psm1"
SELF_TEST = REPO_ROOT / "ci" / "windows" / "w4" / "assertion-self-test.ps1"
PACKAGE_PROPS = REPO_ROOT / "Directory.Packages.props"
A0_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A0-wix-skeleton.md"
A1_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A1-upgradecode.md"
A2_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A2-service-recovery.md"
A3_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A3-programdata-acls.md"
A4_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A4-major-upgrade.md"
A5_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A5-remember-installfolder.md"
A6_DOC = REPO_ROOT / "docs" / "distribution" / "W4-A6-eventlog-source.md"

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

# The preprocessor variables every authored descriptor must come from. An SDDL
# written inline at a PermissionEx would be a descriptor no mutation reaches and
# no gate below parses.
#
# There are three since W4-A3-R2 (#234). W4-A3 had two and let OICI inheritance
# carry the data root's descriptor down to the state directories; run
# 34502732425 measured that it does not, because Windows Installer writes the
# DACL PROTECTED and without SE_DACL_AUTO_INHERITED, so the auto-inherit pass
# never runs and a directory that already exists keeps what it had. A grant is
# only where it is authored.
SDDL_DEFINE_NAMES = ("DataRootSddl", "StateDirSddl", "InstallFolderSddl")


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
            # W0 §9.3 requires Administrators and SYSTEM Full on ALL of the
            # paths, and W4-A3-R1 (#234) made that an invariant of every
            # variant of BOTH descriptors rather than of the data root alone.
            # Run 34500789866 measured why: a descriptor applied through
            # MsiLockPermissionsEx becomes the object's WHOLE DACL, so an
            # INSTALLFOLDER descriptor that omits SYSTEM leaves the installer
            # unable to write its own payload and the install dies 1310 into
            # 1603, two seconds into InstallFinalize, with 2871 files staged.
            for who, label in ((SID_ADMINISTRATORS, "Administrators"), (SID_LOCAL_SYSTEM, "SYSTEM")):
                if allow_mask_for(aces, {who}) & RIGHTS_FULL_CONTROL != RIGHTS_FULL_CONTROL:
                    findings.append(
                        f"authoring: {name} '{value}' does not grant {label} Full. W0 §9.3 "
                        "requires it of every variant, so no control reddens it as collateral, "
                        "and an installer that cannot write to the directory it is installing "
                        "into refuses the whole package"
                    )
            # Scoped to INSTALLFOLDER on purpose. Two of the data-root controls
            # plant a `Users` Modify ACE deliberately -- that IS their declared
            # defect -- so the same assertion there would redden the authoring
            # for carrying its own controls. No authorised control plants one
            # here, so here it is an invariant.
            if name == "InstallFolderSddl" and (
                allow_mask_for(aces, UNPRIVILEGED_SDDL_SIDS) & RIGHTS_WRITE_MASK
            ):
                findings.append(
                    f"authoring: {name} '{value}' grants an unprivileged identity a write bit "
                    "under INSTALLFOLDER, which no variant may do"
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
        # W4-A3-R1 (#234): the WHOLE DACL, not the one row §9.3 is about. A
        # descriptor applied through MsiLockPermissionsEx replaces everything,
        # so Administrators, SYSTEM and `Users` are authored here rather than
        # left to the inheritance that run 34500789866 proved does not survive.
        # W4-A3-R2: what every directory in the operator tree is given -- the
        # four W0 §9.3 names and `Server` above them.
        "StateDirSddl": (
            "D:P"
            f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_ADMINISTRATORS})"
            f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_LOCAL_SYSTEM})"
            f"(A;OICI;0x{RIGHTS_MODIFY:x};;;{sid})"
        ),
        "InstallFolderSddl": (
            "D:"
            f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_ADMINISTRATORS})"
            f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_LOCAL_SYSTEM})"
            f"(A;OICI;0x{RIGHTS_READ_EXECUTE:x};;;BU)"
            f"(A;OICI;0x{RIGHTS_READ_EXECUTE:x};;;{sid})"
        ),
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
    # The two protected descriptors under %ProgramData%. Both must ask for `P`,
    # both must grant the service account Modify, and neither may name an
    # unprivileged identity at all. Windows Installer protects the object
    # whatever the SDDL says -- run 34502732425 measured that, and it is why
    # W4-A3-R2 withdrew the control that removed the `P` -- but the authoring is
    # still required to STATE it, so a reader of this file is not left inferring
    # the contract from installer behaviour.
    for name in ("DataRootSddl", "StateDirSddl"):
        contract = contracts[name]
        if contract not in defines[name]:
            continue
        protected, aces = parse_sddl_dacl(contract)
        if not protected:
            findings.append(f"authoring: the real {name} is not protected, so it does not state the break")
        if allow_mask_for(aces, {sid}) & RIGHTS_MODIFY != RIGHTS_MODIFY:
            findings.append(f"authoring: the real {name} does not grant {sid} Modify")
        if allow_mask_for(aces, UNPRIVILEGED_SDDL_SIDS) != 0:
            findings.append(f"authoring: the real {name} grants an unprivileged identity rights")
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


# ---------------------------------------------------------------------------
# W4-A6 (#234). The Event Log source, and the one gate this slice had to narrow
# rather than add.
# ---------------------------------------------------------------------------

# The source IS this registry key. .NET's `EventLog.CreateEventSource`,
# `EventCreate.exe` and `util:EventSource` all write the same one, which is why
# the authoring needs no second WiX extension to register it.
EVENTLOG_SOURCE_KEY = r"SYSTEM\CurrentControlSet\Services\EventLog\Application\Tesserafin"

# The message file the package ships. `ServiceBase` writes with event id 0 and
# the message as the single insertion string, so a source that names no message
# file with an entry for it records events nothing can render. This one is a
# managed file of the `Microsoft.AspNetCore.App` win-x64 runtime pack, so it is
# already in the self-contained publish the accepted W2 layout is -- which is
# what keeps a distribution that needs no system .NET runtime from taking a
# dependency on the .NET Framework's copy for the sake of a string.
EVENTLOG_MESSAGE_FILE = "System.Diagnostics.EventLog.Messages.dll"

# The three W4-A6 controls, by the name `build-msi.ps1` accepts. Each must be
# reachable from the authoring, or it is a control that cannot be built.
EVENTLOG_MUTATIONS = (
    "eventlog-no-source",
    "eventlog-source-survives",
    "eventlog-start-install",
)

# The one mutation `Start="install"` is allowed to appear under. Before W4-A6
# the gate was "nowhere in the file"; the ruling's own hostile control is a
# package that starts the service inside the MSI transaction, so the gate is now
# "nowhere in the package the real build emits, and in exactly one branch".
EVENTLOG_START_INSTALL_MUTATION = "eventlog-start-install"

_PREPROCESSOR = re.compile(r"<\?(if|elseif|else|endif|ifdef|ifndef)\b([^?]*)\?>")
_MUTATION_TEST = re.compile(r"\$\(var\.Mutation\)\s*(!?=)\s*\"([^\"]*)\"")


def _mutation_branch_taken(condition: str) -> bool | None:
    """Whether a `$(var.Mutation)` condition holds for the REAL package.

    `None` for any other condition, which this reducer does not evaluate and
    whose branches it therefore keeps.
    """
    match = _MUTATION_TEST.search(condition)
    if not match:
        return None
    operator, value = match.group(1), match.group(2)
    return value == "none" if operator == "=" else value != "none"


def real_authoring(text: str) -> str:
    """The authoring with every hostile-control branch removed.

    `Tesserafin.wxs` carries its own hostile controls as `$(var.Mutation)`
    branches -- which is what makes them drive the real file rather than a copy
    of it, and what means a gate reading the raw text cannot say what the REAL
    package is authored to be. `acl-users-write` plants a `Users` Modify ACE and
    `eventlog-start-install` starts the service inside the transaction; both are
    in the file on purpose.

    This runs the WiX preprocessor's own branch selection for `Mutation` =
    `none`, the value `findings_for_mutation` already proves is the only one the
    hosted acceptance build passes. Conditions that are not a `$(var.Mutation)`
    comparison are not evaluated and both of their branches are kept, so this is
    a reducer for the control branches and nothing else.
    """
    kept: list[str] = []
    # Each frame: [emitting, some branch of this construct was already taken,
    # this construct's conditions are ones we evaluate].
    stack: list[list[bool]] = []
    position = 0

    def emitting() -> bool:
        return all(frame[0] for frame in stack)

    for directive in _PREPROCESSOR.finditer(text):
        if emitting():
            kept.append(text[position : directive.start()])
        position = directive.end()
        keyword, condition = directive.group(1), directive.group(2)
        if keyword in ("if", "ifdef", "ifndef"):
            taken = _mutation_branch_taken(condition) if keyword == "if" else None
            if taken is None:
                stack.append([True, True, False])
            else:
                stack.append([taken, taken, True])
        elif keyword == "elseif":
            if not stack:
                continue
            frame = stack[-1]
            taken = _mutation_branch_taken(condition)
            if taken is None or not frame[2]:
                frame[0], frame[1], frame[2] = True, True, False
            else:
                frame[0] = taken and not frame[1]
                frame[1] = frame[1] or taken
        elif keyword == "else":
            if not stack:
                continue
            frame = stack[-1]
            frame[0] = True if not frame[2] else not frame[1]
            frame[1] = True
        elif keyword == "endif":
            if stack:
                stack.pop()
    if emitting():
        kept.append(text[position:])
    return "".join(kept)


def findings_for_event_log_source(text: str, real: str) -> list[str]:
    """W4-A6: the W0 §4 Event Log source, as the REAL package registers it.

    `text` is the whole comment-stripped authoring, controls included; `real` is
    what the `none` build emits. The difference matters for every predicate
    here: two of the three controls are an ABSENT or a WEAKENED registration, so
    asking the raw text whether the source is registered correctly would be
    answered by a control.
    """
    findings: list[str] = []

    if EVENTLOG_SOURCE_KEY not in real:
        findings.append(
            "authoring: the real package registers no Event Log source at "
            f"HKLM\\{EVENTLOG_SOURCE_KEY}, so the service's own lifecycle events have nowhere "
            "to go and a non-administrator service identity cannot create one"
        )
        return findings

    if 'ForceDeleteOnUninstall="yes"' not in real:
        findings.append(
            "authoring: the Event Log source key is not removed on uninstall. "
            "EventLog.SourceExists asks whether the SUBKEY exists and never reads its values, "
            "so removing only the values leaves the source registered"
        )
    if EVENTLOG_MESSAGE_FILE not in real:
        findings.append(
            f"authoring: the source names no {EVENTLOG_MESSAGE_FILE}, so the events it carries "
            "render as a missing description"
        )
    if 'Name="EventMessageFile"' not in real:
        findings.append("authoring: the source has no EventMessageFile value")
    if 'Name="TypesSupported"' not in real:
        findings.append("authoring: the source has no TypesSupported value")

    # The component that owns the key must be re-stated by every install and
    # removed by the uninstall, exactly like the six OperatorTreePermissions
    # components. `Permanent` is the whole of the `eventlog-source-survives`
    # control, so finding it HERE means the real package carries the control.
    for opening in re.findall(r"<Component\s+Id=\"EventLogSourceRegistration\"[^>]*>", real):
        for attribute in ('Permanent="yes"', 'NeverOverwrite="yes"'):
            if attribute in opening:
                findings.append(
                    f"authoring: the Event Log source component is {attribute} in the real "
                    "package, so the registration outlives the product that made it"
                )
    if 'ComponentGroupRef Id="EventLogSource"' not in real:
        findings.append(
            "authoring: the EventLogSource component group is never referenced by the feature, "
            "so nothing installs it"
        )

    # §4 gives the Event Log service-lifecycle events ONLY. A package that
    # defined a log of its own would be claiming the application-events row this
    # slice is explicitly not.
    if re.search(r"EventLog\\\\(?!Application\\\\)", real):
        findings.append(
            "authoring: registers a source under a log other than Application. W0 §4 asks for "
            "service-lifecycle events in the Windows Event Log, not a Tesserafin log"
        )
    return findings


def findings_for_start_install(text: str, real: str) -> list[str]:
    """W0 §5.2's 1920, and the one branch W4-A6 authorises it in.

    Before W4-A6 this was `'Start="install"' in text`. The ruling's own hostile
    control is a package that starts the service inside the MSI transaction, so
    the property is now stated where it was always meant: the REAL package does
    not carry it, and the one copy in the file is the declared control.
    """
    findings: list[str] = []
    if 'Start="install"' in real:
        findings.append(
            "authoring: ServiceControl starts the service inside the transaction. W0 §5.2 "
            "measured that failing with 1920 and rolling the whole install back to 1603, and "
            "W0 §10 leaves a fresh installation installed and enabled but not started"
        )
    occurrences = text.count('Start="install"')
    if occurrences > 1:
        findings.append(
            f"authoring: {occurrences} ServiceControl elements start the service inside the "
            "transaction. One is the declared W4-A6 control; a second is not attributable to it"
        )
    if occurrences == 1:
        guarded = real_authoring(
            text.replace(
                f'$(var.Mutation) = "{EVENTLOG_START_INSTALL_MUTATION}"',
                '$(var.Mutation) = "none"',
            )
        )
        if 'Start="install"' not in guarded:
            findings.append(
                'authoring: the one Start="install" is not inside the '
                f"'{EVENTLOG_START_INSTALL_MUTATION}' branch, so it belongs to no declared control"
            )
    return findings


def findings_for_event_log_mutations(text: str) -> list[str]:
    """Every W4-A6 control must be reachable from the authoring and buildable."""
    findings: list[str] = []
    builder = BUILDER.read_text(encoding="utf-8")
    for mutation in EVENTLOG_MUTATIONS:
        if f'"{mutation}"' not in text:
            findings.append(
                f"authoring: no branch for the '{mutation}' control, so the control cannot drive "
                "this authoring"
            )
        if f"'{mutation}'" not in builder:
            findings.append(
                f"build-msi.ps1: does not accept -Mutation {mutation}, so the control cannot be built"
            )
    return findings


def eventlog_measurement_path() -> str:
    """The two files W4-A6 measures through, as one executable text."""
    return (
        without_comments(PROBE_EVENTLOG.read_text(encoding="utf-8"), "ps1")
        + "\n"
        + without_comments(INSTRUMENTS.read_text(encoding="utf-8"), "ps1")
    )


def findings_for_eventlog_probe(text: str) -> list[str]:
    """W4-A6: the measurement path does what the ruling authorised, and no more.

    `text` is `probe-msi-eventlog.ps1` and `W4MsiInstruments.psm1` together,
    because the measurement is split across them by design -- the probe drives
    the sequence and the module owns the Windows-only reads -- and a gate that
    looked at only one half would be satisfied by a probe that calls nothing or
    by a module nothing calls.

    The start is the point of this slice, so `sc.exe start` is not forbidden
    here the way it is in the upgrade probe. What is graded instead is that the
    source is read back, a real event is read back, and the source is looked for
    again after the uninstall.
    """
    findings: list[str] = []
    if "sc.exe start" not in text and "Start-Service" not in text:
        findings.append(
            "the W4-A6 measurement path never starts the service, so no lifecycle event is produced"
        )
    if "sc.exe stop" not in text and "Stop-Service" not in text:
        findings.append("the W4-A6 measurement path never stops the service")
    if "Get-WinEvent" not in text:
        findings.append(
            "the W4-A6 measurement path never reads an event back, so the source is asserted "
            "rather than measured"
        )
    if EVENTLOG_SOURCE_KEY not in text:
        findings.append(
            "the W4-A6 measurement path never states the Event Log source key, so 'the source "
            "exists' and 'the source is gone' are both unmeasured"
        )
    if EVENTLOG_MESSAGE_FILE not in text:
        findings.append(
            f"the W4-A6 measurement path never checks that {EVENTLOG_MESSAGE_FILE} is installed, "
            "so the authoring's EventMessageFile may dangle"
        )
    for forbidden, why in (
        ("signtool", "this slice signs nothing"),
        ("REINSTALL=", "this slice exercises no repair path"),
        ("/f ", "this slice exercises no repair path"),
    ):
        if forbidden in text:
            findings.append(f"the W4-A6 measurement path: '{forbidden}' -- {why}")
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
    if kind == "ps1":
        # PowerShell has both forms and the probe uses both: `<# ... #>` for the
        # comment-based help every function carries, and `#` for the rest. The
        # block form goes first, so a `#` inside one is not counted as a line
        # comment boundary.
        stripped = re.sub(r"<#.*?#>", "", text, flags=re.S)
        return "\n".join(
            line for line in stripped.splitlines() if not line.lstrip().startswith("#")
        )
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
    # W4-A4 (#234). The same affordance and the same rule. `-PatchBump` exists so
    # the upgrade proof can build two packages from one commit; a workflow that
    # chose the bump would be choosing the version of what is under acceptance.
    if re.search(r"-PatchBump\b", text):
        findings.append(
            "workflow: passes -PatchBump, so the hosted acceptance may not be building the version "
            "the commit declares"
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
    real = real_authoring(text)
    findings += findings_for_start_install(text, real)
    findings += findings_for_event_log_source(text, real)
    findings += findings_for_event_log_mutations(text)
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
        + findings_for_remember_property(without_comments(authoring_text, "xml"))
        + findings_for_upgrade_code_prose(authoring_text, a0_text, a1_text)
        + findings_for_failure_actions_prose(a0_text, a2_text)
        + findings_for_acl_prose(a3_text)
    )


# W4-A4 (#234). The ruling is explicit about what this slice is NOT, and three
# of those are checkable from the probe's own text before any runner time is
# spent. `Start-Service` and `sc.exe start` would start the service the ruling
# says not to start; a write to SharedVersion.cs would be the edit the ruling
# forbids; and a repair would be a lifecycle path W4-A4 does not claim.
FORBIDDEN_UPGRADE_PROBE_MARKERS = (
    "Start-Service",
    "sc.exe start",
    "/f ",
    "REINSTALL=",
)

# The W4-A4 document must not claim the stage above it. The ruling's "not this
# slice" list ends with "claiming W4 accepted", and a document that said so
# would be the finding -- the sentence, not the code, is what a later reader
# acts on.
W4_OVERCLAIM_PATTERNS = (
    r"W4\s+is\s+accepted",
    r"accepts?\s+W4\b",
    r"W4\s+is\s+complete",
)


def findings_for_upgrade_probe(text: str) -> list[str]:
    """W4-A4: the upgrade proof does what the ruling authorised, and no more.

    `text` is comment-stripped, so the probe's own explanation of why it does
    not start the service is not itself read as starting it.
    """
    findings: list[str] = []
    for marker in FORBIDDEN_UPGRADE_PROBE_MARKERS:
        if marker in text:
            findings.append(
                f"probe-msi-upgrade.ps1: '{marker.strip()}' -- W4-A4 does not start the service "
                "and exercises no repair path"
            )
    # The FILE, not the word. The probe states `editedSharedVersion = $false` in
    # its evidence, which is the assertion this gate exists to make checkable --
    # not a thing the gate should refuse.
    if "SharedVersion.cs" in text:
        findings.append(
            "probe-msi-upgrade.ps1: names SharedVersion.cs, but the ruling bumps the version only "
            "through the builder's existing read and forbids touching that file"
        )
    # A and B, and the bump that separates them. Both literals have to be here:
    # a probe that built both packages at the same version would be measuring a
    # reinstall while reading like an upgrade.
    if "-PatchBump 0" not in text:
        findings.append("probe-msi-upgrade.ps1: never builds a package at the declared version")
    if "-PatchBump 1" not in text:
        findings.append("probe-msi-upgrade.ps1: never builds a package at a higher version")
    findings += findings_for_omitted_install_folder(text)
    if FROZEN_UPGRADE_CODE not in text:
        findings.append(
            f"probe-msi-upgrade.ps1: never states the frozen UpgradeCode {FROZEN_UPGRADE_CODE}, so "
            "'the UpgradeCode bytes did not move' is not a comparison against anything"
        )
    return findings


def findings_for_omitted_install_folder(text: str) -> list[str]:
    r"""W4-A5 (#234): A is told the prefix, and every B is NOT.

    W4-A4 required INSTALLFOLDER on EVERY msiexec install, because the authoring
    then had no remember-property and an install that omitted it would have
    relocated the binaries -- the defect that slice recorded as NB-3. W4-A5
    authors the property, so the requirement inverts for exactly the installs
    the slice is about, and stays for the others.

    The rule is per CALL, not a count. The probe runs an `/i` for A, an `/i` for
    B and a record-only `/i` that replays A over B; "some of them mention it"
    would stay green with the wrong one broken, in either direction. Each
    argument array is classified by the MSI it installs, which is the only thing
    that distinguishes them: `$bMsiPath` is B, and everything else is A.

    Bounded by `-LogPath`, which every call in the probe passes, and NOT by the
    array's own closing parenthesis: the arguments interpolate PowerShell
    subexpressions that contain parentheses of their own, and a lazy match on
    `\)` stops inside the first one.
    """
    findings: list[str] = []
    installs = re.findall(r"-Arguments\s+@\(\s*'/i'.*?-LogPath", text, re.S)
    if not installs:
        findings.append(
            "probe-msi-upgrade.ps1: runs no msiexec install at all, so it exercises no upgrade"
        )
    upgrades = 0
    firsts = 0
    for index, call in enumerate(installs, 1):
        is_upgrade = "$bMsiPath" in call
        passes = "INSTALLFOLDER=" in call
        if is_upgrade:
            upgrades += 1
            if passes:
                findings.append(
                    f"probe-msi-upgrade.ps1: msiexec install {index} of {len(installs)} upgrades to "
                    "B and passes INSTALLFOLDER. W4-A5 is the slice in which B is NOT told where to "
                    "install: a B that is told again measures the W4-A4 sequence and says nothing "
                    "about the remember-property"
                )
        else:
            firsts += 1
            if not passes:
                findings.append(
                    f"probe-msi-upgrade.ps1: msiexec install {index} of {len(installs)} installs A "
                    "and does not pass INSTALLFOLDER. A is what puts the product under the "
                    "disposable prefix, so without it the run never leaves the runner's real "
                    "%ProgramFiles% and there is no remembered prefix to find"
                )
    if installs and not upgrades:
        findings.append(
            "probe-msi-upgrade.ps1: no msiexec install upgrades to B, so nothing exercises the "
            "remembered prefix"
        )
    if installs and not firsts:
        findings.append(
            "probe-msi-upgrade.ps1: no msiexec install puts A under a prefix of its own"
        )
    return findings


def findings_for_remember_property(text: str) -> list[str]:
    """W4-A5 (#234): the authoring carries the remember-property, in the right shape.

    `text` is comment-stripped, so this grades what the package WOULD build
    with. Three elements have to be there, and one shape has to NOT be:

      * a component that WRITES `[INSTALLFOLDER]` to the remembered value;
      * an AppSearch `RegistrySearch` that reads it back;
      * a `SetProperty` that copies it into INSTALLFOLDER, conditioned on
        INSTALLFOLDER being unset.

    The last condition is the gate that matters, and the shape it refuses is the
    shorter one: a `RegistrySearch` hung directly on `Property Id="INSTALLFOLDER"`.
    AppSearch OVERWRITES the property it searches for, command line included, so
    that form pins the first prefix forever -- and it would pass every pair this
    slice runs, because both halves of a hosted pair use one prefix. A defect no
    hosted control can reach is exactly what a static gate is for.

    The file carries a deliberately broken variant too -- `upgrade-no-remember`
    authors none of the three -- so these are PRESENCE gates over the file as a
    whole, the same rule the recovery and ACL gates are held to.
    """
    findings: list[str] = []
    if not re.search(r"<RegistrySearch\b[^>]*Name=\"InstallFolder\"", text, re.S):
        findings.append(
            "authoring: no RegistrySearch reads the remembered install location back, so a later "
            "install has nothing to find and an upgrade that omits INSTALLFOLDER relocates"
        )
    if not re.search(r"<RegistryValue\b[^>]*Value=\"\[INSTALLFOLDER\]\"", text, re.S):
        findings.append(
            "authoring: nothing WRITES [INSTALLFOLDER] to the registry, so the search above reads a "
            "value no install ever stores"
        )
    set_property = re.search(r"<SetProperty\b[^>]*Id=\"INSTALLFOLDER\"[^>]*/>", text, re.S)
    if not set_property:
        findings.append(
            "authoring: no SetProperty copies the remembered location into INSTALLFOLDER, so the "
            "search result never reaches the Directory table"
        )
    else:
        element = set_property.group(0)
        if 'After="AppSearch"' not in element:
            findings.append(
                "authoring: the INSTALLFOLDER SetProperty is not scheduled After=AppSearch, so it "
                "runs before the value it copies has been read, or after CostFinalize has already "
                "resolved the directory"
            )
        if "NOT INSTALLFOLDER" not in element:
            findings.append(
                "authoring: the INSTALLFOLDER SetProperty is not conditioned on INSTALLFOLDER being "
                "unset, so it overwrites a prefix the operator passed on the command line and the "
                "remembered location can never be changed"
            )
    if re.search(r"<Property\b[^>]*Id=\"INSTALLFOLDER\"", text, re.S):
        findings.append(
            "authoring: a Property element declares INSTALLFOLDER itself. AppSearch OVERWRITES the "
            "property it searches for, command-line values included, so a search authored there "
            "pins the first prefix forever -- and no hosted pair can see it, because both halves of "
            "a pair use one prefix"
        )
    return findings


def findings_for_upgrade_prose(a4_text: str) -> list[str]:
    """W4-A4: the document records this slice and does not claim the stage."""
    findings: list[str] = []
    if "W4-A4" not in a4_text:
        findings.append("W4-A4 document: does not cite the W4-A4 ruling it records")
    if FROZEN_UPGRADE_CODE not in a4_text:
        findings.append(
            f"W4-A4 document: does not state the frozen UpgradeCode {FROZEN_UPGRADE_CODE}, which is "
            "the one value the upgrade is measured against"
        )
    if "MajorUpgrade" not in a4_text:
        findings.append("W4-A4 document: does not name MajorUpgrade, which is what this slice exercises")
    for pattern in W4_OVERCLAIM_PATTERNS:
        if re.search(pattern, a4_text, re.I):
            findings.append(
                f"W4-A4 document: claims the stage ('{pattern}'); the ruling excludes claiming W4 accepted"
            )
    return findings


W4A5_REQUIRED_PHRASES = (
    ("W4-A5", "does not cite the W4-A5 ruling it records"),
    ("INSTALLFOLDER", "does not name INSTALLFOLDER, which is the property this slice remembers"),
    ("MajorUpgrade", "does not name MajorUpgrade, which is the path the prefix has to survive"),
    ("upgrade-no-remember", "does not name the hostile control that proves the property is load-bearing"),
)


def findings_for_remember_prose(a5_text: str) -> list[str]:
    """W4-A5: the document records this slice and does not claim the stage."""
    findings = [
        f"W4-A5 document: {why}"
        for phrase, why in W4A5_REQUIRED_PHRASES
        if phrase not in a5_text
    ]
    if FROZEN_UPGRADE_CODE not in a5_text:
        findings.append(
            f"W4-A5 document: does not state the frozen UpgradeCode {FROZEN_UPGRADE_CODE}, which "
            "this slice leaves exactly where W4-A1 froze it"
        )
    for pattern in W4_OVERCLAIM_PATTERNS:
        if re.search(pattern, a5_text, re.I):
            findings.append(
                f"W4-A5 document: claims the stage ('{pattern}'); the ruling excludes claiming W4 accepted"
            )
    return findings


W4A6_REQUIRED_PHRASES = (
    ("W4-A6", "does not cite the W4-A6 ruling it records"),
    (EVENTLOG_SOURCE_KEY, "does not state the registry key the Event Log source IS"),
    ("AutoLog", "does not say what actually writes the lifecycle events"),
    ("eventlog-no-source", "does not name the control that proves the registration is load-bearing"),
    ("eventlog-start-install", "does not name the control over starting inside the transaction"),
)


def findings_for_eventlog_prose(a6_text: str) -> list[str]:
    """W4-A6: the document records this slice and does not claim the stage."""
    findings = [
        f"W4-A6 document: {why}"
        for phrase, why in W4A6_REQUIRED_PHRASES
        if phrase not in a6_text
    ]
    if FROZEN_UPGRADE_CODE not in a6_text:
        findings.append(
            f"W4-A6 document: does not state the frozen UpgradeCode {FROZEN_UPGRADE_CODE}, which "
            "this slice leaves exactly where W4-A1 froze it"
        )
    for pattern in W4_OVERCLAIM_PATTERNS:
        if re.search(pattern, a6_text, re.I):
            findings.append(
                f"W4-A6 document: claims the stage ('{pattern}'); the ruling excludes claiming W4 accepted"
            )
    return findings


def findings_for_wiring(text: str) -> list[str]:
    """The two harnesses must actually run, or they are decoration."""
    findings: list[str] = []
    if "assertion-self-test.ps1" not in text:
        findings.append("workflow: never runs the assertion self-test, so the grader is ungraded")
    if "msi-controls.py" not in text:
        findings.append("workflow: never runs these controls")
    if "probe-msi-skeleton.ps1" not in text:
        findings.append("workflow: never runs the MSI proof")
    # W4-A4. A probe the workflow never runs is decoration, exactly like a
    # self-test nothing invokes.
    if "probe-msi-upgrade.ps1" not in text:
        findings.append("workflow: never runs the W4-A4 MajorUpgrade proof")
    # W4-A6. Same rule again: a probe the workflow never runs is decoration.
    if "probe-msi-eventlog.ps1" not in text:
        findings.append("workflow: never runs the W4-A6 Event Log source proof")
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
        + findings_for_upgrade_probe(
            without_comments(PROBE_UPGRADE.read_text(encoding="utf-8"), "ps1")
        )
        + findings_for_eventlog_probe(eventlog_measurement_path())
        + findings_for_upgrade_prose(A4_DOC.read_text(encoding="utf-8"))
        + findings_for_remember_prose(A5_DOC.read_text(encoding="utf-8"))
        + findings_for_eventlog_prose(A6_DOC.read_text(encoding="utf-8"))
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
    # W4-A3-R2: identical text, different variable. Both are mutated, because a
    # gate that only watched the data root would not notice the descriptor the
    # state directories actually get -- which is the defect R2 exists to repair.
    real_state_dir = real_data_root
    real_install = (
        "D:"
        f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_ADMINISTRATORS})"
        f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_LOCAL_SYSTEM})"
        f"(A;OICI;0x{RIGHTS_READ_EXECUTE:x};;;BU)"
        f"(A;OICI;0x{RIGHTS_READ_EXECUTE:x};;;{sid})"
    )

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
        "the state directory descriptor is not protected": swap_real(
            "StateDirSddl", real_state_dir, real_state_dir.replace("D:P", "D:", 1)
        ),
        "the data root hands Users Modify": swap_real(
            "DataRootSddl", real_data_root, real_data_root + f"(A;OICI;0x{RIGHTS_MODIFY:x};;;BU)"
        ),
        "the state directories hand Users Modify": swap_real(
            "StateDirSddl", real_state_dir, real_state_dir + f"(A;OICI;0x{RIGHTS_MODIFY:x};;;BU)"
        ),
        "the service account gets no grant on the data root": swap_real(
            "DataRootSddl",
            real_data_root,
            real_data_root.replace(f"(A;OICI;0x{RIGHTS_MODIFY:x};;;{sid})", "", 1),
        ),
        "the service account gets no grant on the state directories": swap_real(
            "StateDirSddl",
            real_state_dir,
            real_state_dir.replace(f"(A;OICI;0x{RIGHTS_MODIFY:x};;;{sid})", "", 1),
        ),
        "the state directories stop granting SYSTEM Full": swap_real(
            "StateDirSddl",
            real_state_dir,
            real_state_dir.replace(
                f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_LOCAL_SYSTEM})", "", 1
            ),
        ),
        "the service account SID is a different one": swap(
            sid, service_account_sid("TesserafinServer")
        ),
        "INSTALLFOLDER grants the service Modify": swap_real(
            "InstallFolderSddl",
            real_install,
            real_install.replace(
                f"(A;OICI;0x{RIGHTS_READ_EXECUTE:x};;;{sid})",
                f"(A;OICI;0x{RIGHTS_MODIFY:x};;;{sid})",
                1,
            ),
        ),
        # W4-A3-R1 (#234): the shape that took the install down. A descriptor
        # applied through MsiLockPermissionsEx becomes the object's whole DACL,
        # so an INSTALLFOLDER descriptor without SYSTEM leaves the installer
        # unable to write its own payload.
        "INSTALLFOLDER drops SYSTEM": swap_real(
            "InstallFolderSddl",
            real_install,
            real_install.replace(
                f"(A;OICI;0x{RIGHTS_FULL_CONTROL:x};;;{SID_LOCAL_SYSTEM})", "", 1
            ),
        ),
        "INSTALLFOLDER hands Users a write bit": swap_real(
            "InstallFolderSddl",
            real_install,
            real_install.replace(
                f"(A;OICI;0x{RIGHTS_READ_EXECUTE:x};;;BU)",
                f"(A;OICI;0x{RIGHTS_MODIFY:x};;;BU)",
                1,
            ),
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


def self_test_upgrade(probe_text: str, a4_text: str) -> list[str]:
    """W4-A4: every gate over the upgrade proof and its document must be reachable.

    Each mutation below is a shape the ruling names as out of scope, or a way
    the pair would stop being a pair. A gate none of them trips is a gate that
    would not have caught the real thing either.
    """
    probe_mutations = {
        "starts the service": lambda p: p.replace(
            "$allPassed = $true", "Start-Service -Name $SERVICE_NAME\n$allPassed = $true", 1
        ),
        "repairs the installation": lambda p: p.replace(
            "$allPassed = $true", '$null = @("/f ")\n$allPassed = $true', 1
        ),
        "edits the declared version": lambda p: p.replace(
            "$allPassed = $true", "$null = 'SharedVersion.cs'\n$allPassed = $true", 1
        ),
        "both packages at one version": lambda p: p.replace("-PatchBump 1", "-PatchBump 0"),
        "no frozen UpgradeCode to compare against": lambda p: p.replace(
            FROZEN_UPGRADE_CODE, "a GUID chosen at release time"
        ),
    }
    doc_mutations = {
        "document claims the stage": lambda d: d + "\n\nW4 is accepted.\n",
        "document drops the frozen UpgradeCode": lambda d: d.replace(
            FROZEN_UPGRADE_CODE, "the frozen GUID"
        ),
        "document never names MajorUpgrade": lambda d: d.replace("MajorUpgrade", "the upgrade"),
    }

    failures: list[str] = []
    for name, mutate in probe_mutations.items():
        mutated = mutate(probe_text)
        if mutated == probe_text:
            failures.append(f"self-test '{name}': the mutation did not change the probe")
            continue
        if not findings_for_upgrade_probe(without_comments(mutated, "ps1")):
            failures.append(f"self-test '{name}': the upgrade-probe gate did not fire")
        else:
            print(f"  control OK   {name}")
    for name, mutate in doc_mutations.items():
        mutated = mutate(a4_text)
        if mutated == a4_text:
            failures.append(f"self-test '{name}': the mutation did not change the document")
            continue
        if not findings_for_upgrade_prose(mutated):
            failures.append(f"self-test '{name}': the W4-A4 prose gate did not fire")
        else:
            print(f"  control OK   {name}")
    if not failures:
        print(
            f"  {len(probe_mutations) + len(doc_mutations)} W4-A4 controls, all RED as declared"
        )
    return failures


def self_test_remember(
    authoring_text: str, probe_text: str, a5_text: str
) -> list[str]:
    """W4-A5: every gate over the remember-property must be reachable.

    Each mutation is a shape that would leave the hosted pair green while the
    property was not doing its job -- including the two the hosted pair CANNOT
    see, because both halves of a pair use one prefix: a search authored on
    INSTALLFOLDER itself, and a SetProperty with no condition. A gate none of
    them trips is a gate that would not have caught the real thing either.
    """
    authoring_mutations = {
        "nothing reads the remembered location": lambda a: a.replace(
            '<RegistrySearch Id="RememberedInstallFolder"',
            '<RegistrySearchDisabled Id="RememberedInstallFolder"',
            1,
        ),
        "nothing writes the remembered location": lambda a: a.replace(
            'Value="[INSTALLFOLDER]"', 'Value="unremembered"', 1
        ),
        "the remembered location never reaches INSTALLFOLDER": lambda a: a.replace(
            "<SetProperty ", "<SetPropertyDisabled ", 1
        ),
        "the copy is scheduled after the directory is resolved": lambda a: a.replace(
            'After="AppSearch"', 'After="CostFinalize"', 1
        ),
        "the copy overwrites the command line": lambda a: a.replace(
            'Condition="REMEMBEREDINSTALLFOLDER AND NOT INSTALLFOLDER"', 'Condition="1"', 1
        ),
        "the search is authored on INSTALLFOLDER itself": lambda a: a.replace(
            '<Property Id="REMEMBEREDINSTALLFOLDER">', '<Property Id="INSTALLFOLDER">', 1
        ),
    }
    probe_mutations = {
        "the upgrade is told the prefix again": lambda p: p.replace(
            '@(\'/i\', "`"$bMsiPath`"")',
            '@(\'/i\', "`"$bMsiPath`"", "INSTALLFOLDER=`"$prefix`"")',
            1,
        ),
        "no install upgrades to B": lambda p: p.replace("$bMsiPath", "$someOtherMsi"),
        # A is still required to carry the prefix, and the mutation names the
        # WHOLE argument array rather than the first `INSTALLFOLDER=` in the
        # file: the probe's own prose says `INSTALLFOLDER=P` before any code
        # does, and a mutation that edited a comment would be graded on a gate
        # that never saw it.
        "the first install forgets the prefix": lambda p: p.replace(
            '@(\'/i\', "`"$($msi[\'a\'])`"", "INSTALLFOLDER=`"$prefix`"")',
            '@(\'/i\', "`"$($msi[\'a\'])`"")',
            1,
        ),
    }
    doc_mutations = {
        "document drops the hostile control": lambda d: d.replace("upgrade-no-remember", "the control"),
        "document claims the stage": lambda d: d + "\n\nW4 is accepted.\n",
        "document drops the frozen UpgradeCode": lambda d: d.replace(
            FROZEN_UPGRADE_CODE, "the frozen GUID"
        ),
    }

    failures: list[str] = []
    for name, mutate in authoring_mutations.items():
        mutated = mutate(authoring_text)
        if mutated == authoring_text:
            failures.append(f"self-test '{name}': the mutation did not change the authoring")
            continue
        if not findings_for_remember_property(without_comments(mutated, "xml")):
            failures.append(f"self-test '{name}': the remember-property gate did not fire")
        else:
            print(f"  control OK   {name}")
    for name, mutate in probe_mutations.items():
        mutated = mutate(probe_text)
        if mutated == probe_text:
            failures.append(f"self-test '{name}': the mutation did not change the probe")
            continue
        if not findings_for_omitted_install_folder(without_comments(mutated, "ps1")):
            failures.append(f"self-test '{name}': the omitted-INSTALLFOLDER gate did not fire")
        else:
            print(f"  control OK   {name}")
    for name, mutate in doc_mutations.items():
        mutated = mutate(a5_text)
        if mutated == a5_text:
            failures.append(f"self-test '{name}': the mutation did not change the document")
            continue
        if not findings_for_remember_prose(mutated):
            failures.append(f"self-test '{name}': the W4-A5 prose gate did not fire")
        else:
            print(f"  control OK   {name}")
    if not failures:
        total = len(authoring_mutations) + len(probe_mutations) + len(doc_mutations)
        print(f"  {total} W4-A5 controls, all RED as declared")
    return failures


def self_test_event_log(
    authoring_text: str, probe_text: str, a6_text: str
) -> list[str]:
    """W4-A6: every gate above must be reachable.

    The authoring mutations here are the shapes a slice could plausibly reach
    for -- the registration dropped, the key left behind, the component made
    permanent, the message file dropped, the control's `Start="install"` moved
    out of its branch -- and each has to be RED. The last two prove the narrowed
    `Start="install"` gate is still a gate: it must fire for a second copy and
    for a copy that is not inside the declared control.
    """
    documents = (a6_text,)
    failures: list[str] = []

    def grade_one(text: str) -> list[str]:
        real = real_authoring(text)
        return (
            findings_for_start_install(text, real)
            + findings_for_event_log_source(text, real)
            + findings_for_event_log_mutations(text)
        )

    if grade_one(without_comments(authoring_text, "xml")):
        failures.append("self-test 'W4-A6 baseline': the real authoring is already RED")

    authoring_mutations = {
        "the source is never registered": lambda t: t.replace(EVENTLOG_SOURCE_KEY, "SOFTWARE\\Tesserafin\\NotASource"),
        "the source key survives uninstall": lambda t: t.replace(
            'ForceDeleteOnUninstall="yes"', 'ForceDeleteOnUninstall="no"'
        ),
        "the source component is permanent": lambda t: t.replace(
            '<Component Id="EventLogSourceRegistration" Guid="55ce15a7-640d-4216-b490-0236e5948fce">',
            '<Component Id="EventLogSourceRegistration" Guid="55ce15a7-640d-4216-b490-0236e5948fce" Permanent="yes">',
        ),
        "the message file is dropped": lambda t: t.replace(EVENTLOG_MESSAGE_FILE, "nothing.dll"),
        "nothing installs the source": lambda t: t.replace(
            '<ComponentGroupRef Id="EventLogSource" />', ""
        ),
        "the real package starts the service": lambda t: t.replace(
            '<?elseif $(var.Mutation) = "eventlog-start-install" ?>',
            '<?elseif $(var.Mutation) = "never-taken" ?>',
        ),
        "a second package starts the service": lambda t: t.replace(
            '<ServiceControl Id="TesserafinServiceControl"\n                          Name="Tesserafin"\n                          Stop="both"',
            '<ServiceControl Id="TesserafinServiceControl"\n                          Name="Tesserafin"\n                          Start="install"\n                          Stop="both"',
        ),
        "a control is unreachable": lambda t: t.replace('"eventlog-no-source"', '"eventlog-gone"'),
    }
    for name, mutate in authoring_mutations.items():
        mutated = mutate(authoring_text)
        if mutated == authoring_text:
            failures.append(f"self-test '{name}': the mutation did not change the authoring")
            continue
        if not grade_one(without_comments(mutated, "xml")):
            failures.append(f"self-test '{name}': the gate did not fire")
        else:
            print(f"  control OK   {name}")

    probe_mutations = {
        "the probe never starts the service": lambda t: t.replace("sc.exe start", "sc.exe query").replace(
            "Start-Service", "Get-Service"
        ),
        "the probe reads no event": lambda t: t.replace("Get-WinEvent", "Get-Nothing"),
        "the probe never looks at the source key": lambda t: t.replace(EVENTLOG_SOURCE_KEY, "SOFTWARE\\Elsewhere"),
        "the probe never checks the message file": lambda t: t.replace(EVENTLOG_MESSAGE_FILE, "nothing.dll"),
    }
    executable_probe = probe_text
    for name, mutate in probe_mutations.items():
        mutated = mutate(executable_probe)
        if mutated == executable_probe:
            failures.append(f"self-test '{name}': the mutation did not change the probe")
            continue
        if not findings_for_eventlog_probe(mutated):
            failures.append(f"self-test '{name}': the gate did not fire")
        else:
            print(f"  control OK   {name}")

    prose_mutations = {
        "document drops the source key": lambda t: t.replace(EVENTLOG_SOURCE_KEY, "somewhere"),
        "document never says what writes the events": lambda t: t.replace("AutoLog", "somehow"),
        "document claims the stage": lambda t: t + "\n\nW4 is accepted.\n",
        "document drops the frozen UpgradeCode": lambda t: t.replace(FROZEN_UPGRADE_CODE, "0"),
    }
    for name, mutate in prose_mutations.items():
        mutated = mutate(documents[0])
        if mutated == documents[0]:
            failures.append(f"self-test '{name}': the mutation did not change the document")
            continue
        if not findings_for_eventlog_prose(mutated):
            failures.append(f"self-test '{name}': the gate did not fire")
        else:
            print(f"  control OK   {name}")

    if not failures:
        total = len(authoring_mutations) + len(probe_mutations) + len(prose_mutations)
        print(f"  {total} W4-A6 controls, all RED as declared")
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
        # W4-A4 (#234)
        "upgrade proof dropped": lambda t: t.replace("probe-msi-upgrade.ps1", "nothing.ps1"),
        # W4-A6 (#234)
        "event log proof dropped": lambda t: t.replace("probe-msi-eventlog.ps1", "nothing.ps1"),
        "acceptance chooses the version": lambda t: t.replace(
            "        run: |",
            "        run: |\n          ./ci/windows/w4/probe-msi-upgrade.ps1 -PatchBump 1",
            1,
        ),
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

    for required in (WORKFLOW, AUTHORING, BUILDER, PROBE, PROBE_UPGRADE, PROBE_EVENTLOG,
                     INSTRUMENTS, SELF_TEST,
                     A0_DOC, A1_DOC, A2_DOC, A3_DOC, A4_DOC, A5_DOC, A6_DOC):
        if not required.is_file():
            print(f"W4 CONTROLS REFUSED: missing {required.relative_to(REPO_ROOT)}")
            return 1

    workflow_text = WORKFLOW.read_text(encoding="utf-8")
    findings = grade_everything(workflow_text)

    print("W4-A0 / W4-A2 / W4-A3 / W4-A4 / W4-A5 / W4-A6 static controls")
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
        failures += self_test_upgrade(
            PROBE_UPGRADE.read_text(encoding="utf-8"), A4_DOC.read_text(encoding="utf-8")
        )
        failures += self_test_remember(
            AUTHORING.read_text(encoding="utf-8"),
            PROBE_UPGRADE.read_text(encoding="utf-8"),
            A5_DOC.read_text(encoding="utf-8"),
        )
        failures += self_test_event_log(
            AUTHORING.read_text(encoding="utf-8"),
            eventlog_measurement_path(),
            A6_DOC.read_text(encoding="utf-8"),
        )
    for failure in failures:
        print(f"  FINDING  {failure}")

    if findings or failures:
        print(f"W4 static controls FAILED: {len(findings) + len(failures)} finding(s)")
        return 1
    print("W4 static controls: clean")
    return 0


if __name__ == "__main__":
    sys.exit(main())
