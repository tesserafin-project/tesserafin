#!/usr/bin/env python3
"""Hostile controls for W2's deterministic win-x64 server ZIP (W2-A2, #256).

W2-A2's claim is narrow and entirely mechanical:

    two clean assemblies of `tesserafin-server_<version>_win-x64.zip` from the
    same commit produce the same bytes, and no archive is produced at all when
    the inputs are not the accepted ones.

So the controls come in three shapes, and each answers a different way that
claim could be false.

  * OBSERVED REFUSALS, PLANT FIRST. Every named refusal is driven against the
    REAL assembler over a disposable stage built here, and the assembler's own
    sentence is required verbatim rather than invented. Each is paired with a
    live INERT-proof: the same stage is handed to a MUTATED COPY of the
    assembler with that one check defeated, and the mutant is required to do the
    thing the real one refused. A control whose mutation no longer applies
    reports INERT rather than a smaller green suite -- a refusal that cannot be
    shown to be load-bearing is a comment.

  * DETERMINISM, MEASURED. The clamp and the entry order are properties of the
    bytes, so they are measured on real archives: the same content staged with
    different filesystem timestamps and packed twice must hash identically,
    while a copy of the packer that reads mtimes from disk, or that orders
    entries any other way, must not.

  * A SOURCE AND WORKFLOW AUDIT, PLUS A RAW-BYTE PIN. The absence of a
    `-Reference` cannot be observed behaviourally: a test can only fail to pass
    an option, which is indistinguishable from the option existing and being
    ignored. Those properties are asserted against the text of the production
    path, over its EXECUTABLE part only, and the pins over this slice's workflow
    and over the four frozen inputs refuse every other edit -- including edits
    this slice is not authorised to make at all.

Nothing here reaches a registry, builds a server, starts one, writes a package
or modifies the repository. The RESTORE row asserts the last of those rather
than assuming it.

    python3 ci/windows/w2/zip-controls.py
    python3 ci/windows/w2/zip-controls.py --only Z13
"""

import argparse
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))

# The one file W2-A2 adds to the production path, and the workflow that runs it.
ASSEMBLER = os.path.join(HERE, "assemble-server-zip.ps1")
WORKFLOW = os.path.join(REPO_ROOT, ".github", "workflows", "w2-windows-server-zip.yml")

# The frozen inputs. W2-A2 authors none of these and edits none of them.
WEB_CONSUMER = os.path.join(HERE, "consume-web-payload.ps1")
TREE_DIGEST = os.path.join(HERE, "pkg-tree-digest.py")
RUNTIME_CONSUMER = os.path.join(REPO_ROOT, "ci", "windows", "runtime-retention", "consume.ps1")
ACCEPTED_JSON = os.path.join(REPO_ROOT, "ci", "windows", "runtime-retention",
                             "accepted-runtime.json")

# ---------------------------------------------------------------------------
# The frozen identities. The ruling's values, written here so that Z07 can
# require the assembler, the workflow and the committed acceptance manifest to
# agree with them -- four independent statements of one identity, rather than
# one statement read four times.
# ---------------------------------------------------------------------------
WEB_PAYLOAD_SHA256 = "4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f"
ACCEPTED_RUNTIME_SHA256 = "f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e"
# Descriptive only, and never a trust boundary. Z12 requires it to be absent
# from the production path entirely.
DESCRIPTIVE_TAG = "accepted-83e23b957940"

# Raw-byte pins. `.gitattributes` normalises this repository to LF, so a Windows
# checkout delivers the same bytes a Linux one does and these are safe to assert
# literally. A pin over a text-mode read would have been more forgiving and less
# true: a CRLF copy hashes identically under universal newlines, so a file
# differing only in line endings would be called byte-identical. It is reported
# as exactly that instead.
FROZEN_PINS = {
    "ci/windows/w2/consume-web-payload.ps1":
        "db49f21001067a8f55ae71432ff9d47830daa454704a09800bb0e1eadf3b117c",
    "ci/windows/w2/pkg-tree-digest.py":
        "0c70114c69e85d06bc3d95249cc1a86f917eb2b8deb44718cc05ad6f3afa70b4",
    "ci/windows/runtime-retention/consume.ps1":
        "f19fefcc48de9ae2175aa49ecff6e732762219a3d76c38067ba4114a1924646d",
    "ci/windows/runtime-retention/accepted-runtime.json":
        "593c21f59c67dd564fa488f660efc14b74b5c5bcd775bbc3ef0bdf9e94dd9ece",
}
WORKFLOW_SHA256 = "337921cc0e473701a29d0ea193e70884ec93c68de1ba1d57bf1d84ec63dc4ac1"

WORKFLOW_ALLOWED_TRIGGERS = ("pull_request",)
WORKFLOW_ALLOWED_JOBS = ("zip",)

# The assembler's production parameter surface. `-StageRoot` is deliberately NOT
# here: it belongs to the pack-only oracle these controls drive, and Z11 asserts
# the workflow never passes it.
ASSEMBLER_ARGUMENTS = ("-RepoRoot", "-WorkDir", "-OutDir", "-SourceDateEpoch",
                       "-OrasPath", "-PythonPath")

# Parameter names whose presence in the assembler's top-level param block would
# make the packaged identity caller-controlled. `-RepoRoot` names the checkout
# being built and `-OrasPath` names a client pinned by tools.lock.json, which is
# why neither is on this list.
FORBIDDEN_PARAMETERS = ("$Reference", "$Digest", "$Tag", "$RunId", "$Package",
                        "$Registry", "$Image", "$Url", "$Uri", "$Version",
                        "$WebPayloadSha256", "$RuntimeSha256")

# Every control this suite is required to report. A control that is deleted,
# renamed or silently skipped stops appearing here, and `main` turns that into a
# RED rather than into a smaller green suite. Without this, removing an
# inconvenient control would IMPROVE the summary line.
ROSTER = {
    "Z01": "no SOURCE_DATE_EPOCH is refused, and the epoch is never defaulted to the clock",
    "Z02": "a zero SOURCE_DATE_EPOCH is refused",
    "Z03": "a SOURCE_DATE_EPOCH a ZIP cannot represent is refused",
    "Z04": "the mtime clamp is load-bearing",
    "Z05": "the entry order is load-bearing and ordinal",
    "Z06": "state under the stage is refused",
    "Z07": "the ruling, the assembler, the acceptance manifest and the workflow agree",
    "Z08": "a second top-level directory is refused",
    "Z09": "no caller-supplied identity anywhere on the production path",
    "Z10": "no container runtime on the production path",
    "Z11": "no Actions artifact, cache or dispatch, and the oracle is not the production door",
    "Z12": "the descriptive tag is never the trust boundary",
    "Z13": "the new workflow is the authorised one, byte for byte",
    "Z14": "the four frozen inputs are unmodified",
    "Z15": "the workflow requires two independent assemblies and compares bytes",
    "Z16": "the workflow watches the whole ci/windows/w2 tree (W2-A1-V1 NB-1)",
    "Z17": "the archive extracts under exactly one top-level directory",
    "Z18": "the assembler starts nothing and installs no service",
    "Z19": "an odd SOURCE_DATE_EPOCH packs rather than being refused",
    "Z20": "the publish tree is proven self-contained win-x64 before it is packed",
}


# ===========================================================================
# Reading a file for what it DOES
# ===========================================================================

def strip_commentary(path, text):
    """The executable part of a file, with comments and docstrings blanked out.

    An audit that cannot tell an invocation from a comment explaining why there
    is no invocation has two useless outcomes: it fires on the explanation, or
    it is loosened until it would miss the real thing. W2-A0-V1 graded a
    workflow green whose only `contents: read` was a comment, which is the
    second outcome. Lines are blanked rather than removed, so a finding's line
    number still points at the real file.
    """
    suffix = os.path.splitext(path)[1].lower()
    if suffix == ".py":
        import tokenize
        lines = text.splitlines()
        try:
            tokens = list(tokenize.generate_tokens(io.StringIO(text).readline))
        except (tokenize.TokenError, IndentationError):
            return text
        for token in tokens:
            triple = token.type == tokenize.STRING and \
                token.string.lstrip("rbuRBUf")[:3] in ('"' * 3, "'" * 3)
            if token.type == tokenize.COMMENT or triple:
                for number in range(token.start[0], token.end[0] + 1):
                    if 1 <= number <= len(lines):
                        lines[number - 1] = ""
        return "\n".join(lines)
    if suffix == ".ps1":
        lines = text.splitlines()
        inside = False
        for index, line in enumerate(lines):
            if not inside and "<#" in line:
                inside = True
            if inside:
                if "#>" in line:
                    inside = False
                lines[index] = ""
                continue
            lines[index] = re.sub(r"#.*$", "", line)
        return "\n".join(lines)
    if suffix in (".yml", ".yaml"):
        return "\n".join(re.sub(r"(^|\s)#.*$", r"\1", line) for line in text.splitlines())
    return text


def read_text(path):
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def executable_text(path, text=None):
    return strip_commentary(path, read_text(path) if text is None else text)


def scan_for(paths, pattern, code_only=False):
    """Every (path, line number, line) a pattern matches."""
    hits = []
    compiled = re.compile(pattern, re.IGNORECASE)
    for path in paths:
        if not os.path.isfile(path):
            continue
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            text = handle.read()
        if code_only:
            text = strip_commentary(path, text)
        for number, line in enumerate(text.splitlines(), 1):
            if compiled.search(line):
                # A control may scan a planted fixture in temporary storage, and
                # on Windows that can sit on a different drive from the
                # checkout: `relpath` cannot relate two mounts and raises. The
                # match has already been made, so this is a display limit, not a
                # result -- fall back to the path as given rather than lose it.
                try:
                    shown = os.path.relpath(path, REPO_ROOT)
                except ValueError:
                    shown = os.fspath(path)
                hits.append((shown, number, line.rstrip()))
    return hits


def sha256_file(path):
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


# ===========================================================================
# Driving the REAL assembler, and mutated copies of it
# ===========================================================================

ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")


def normalise(raw):
    """The assembler's own words, on one line.

    PowerShell's default error view wraps a long message across a gutter of `|`
    continuations and colours it. Neither is part of what the assembler said,
    and a substring search that saw them would fail on message length rather
    than on behaviour.
    """
    text = ANSI.sub("", raw.decode("utf-8", "replace"))
    text = re.sub(r"\n[ \t]*\|[ \t]?", " ", text)
    return re.sub(r"[ \t]+", " ", text)


def powershell():
    for candidate in ("pwsh", "powershell"):
        found = shutil.which(candidate)
        if found:
            return found
    return None


class Completed:
    def __init__(self, returncode, text, outdir):
        self.returncode = returncode
        self.text = text
        self.outdir = outdir

    def archives(self):
        if not os.path.isdir(self.outdir):
            return []
        return sorted(name for name in os.listdir(self.outdir) if name.endswith(".zip"))


def pack(shell, script, stage, outdir, epoch=None):
    """Drive a packer -- the real one or a mutant -- over a staged tree."""
    os.makedirs(outdir, exist_ok=True)
    command = [shell, "-NoProfile", "-NonInteractive", "-File", script,
               "-StageRoot", stage, "-OutDir", outdir]
    if epoch is not None:
        command += ["-SourceDateEpoch", str(epoch)]
    environment = dict(os.environ)
    environment["NO_COLOR"] = "1"
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            env=environment, check=False)
    return Completed(result.returncode, normalise(result.stdout), outdir)


# The mutations. Each is (name, [(needle, replacement), ...]). A mutation whose
# needle is no longer present cannot be applied, and the control that depends on
# it reports INERT rather than passing on a proof it did not make.
MUTATIONS = {
    # A silent clock substitution: exactly the defect the epoch check exists to
    # prevent, and the only mutation under which a missing epoch still produces
    # an archive.
    "clock-epoch": [
        ("    if ($Epoch -eq 0) {", "    if ($false) {"),
        ("    if ($Epoch -lt $DOS_EPOCH_FLOOR) {", "    if ($false) {"),
        ("    $stamp = [System.DateTimeOffset]::FromUnixTimeSeconds($Epoch).ToUniversalTime()",
         "    if ($Epoch -le 0) { $Epoch = [System.DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }\n"
         "    $stamp = [System.DateTimeOffset]::FromUnixTimeSeconds($Epoch).ToUniversalTime()"),
    ],
    # Read every mtime off the filesystem instead of clamping it.
    "disk-mtime": [
        ("                $entry.LastWriteTime = $stamp",
         "                $entry.LastWriteTime = [System.DateTimeOffset]::new("
         "(Get-Item -LiteralPath $source -Force).LastWriteTimeUtc, [System.TimeSpan]::Zero)"),
        # The read-back assertion would catch the mutant for the right reason
        # and hide the measurement this control is making, so it is defeated
        # too: the point here is what the BYTES do, not what the packer says.
        ("            if ($entry.LastWriteTime.DateTime -ne $clampWall) {",
         "            if ($false) {"),
    ],
    # Order the entries any other way. Reverse is used rather than "unsorted"
    # because an unsorted enumeration can happen to agree with the sort, and a
    # live-proof that sometimes agrees is not a proof.
    "shuffled-order": [(
        "    [Array]::Sort($names, [System.StringComparer]::Ordinal)",
        "    [Array]::Sort($names, [System.StringComparer]::Ordinal)\n    [Array]::Reverse($names)",
    )],
    # Pack whatever is staged, state included.
    "no-state-scan": [(
        "    if ($stateFindings.Count -gt 0) {",
        "    if ($false) {",
    )],
    # Accept any number of top-level entries, and let them into the archive.
    "many-top-level": [
        ("    if ($topLevel.Count -ne 1) {", "    if ($false) {"),
        ("            if (-not $entry.FullName.StartsWith(\"$packageName/\", [System.StringComparison]::Ordinal)) {",
         "            if ($false) {"),
    ],
}


def mutate(work, name):
    """A disposable copy of the assembler with one check defeated, or None."""
    text = read_text(ASSEMBLER)
    for needle, replacement in MUTATIONS[name]:
        if needle not in text:
            return None
        text = text.replace(needle, replacement, 1)
    path = os.path.join(work, "mutant-%s.ps1" % name)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    return path


# ===========================================================================
# Disposable stages
# ===========================================================================

PACKAGE_NAME = "tesserafin-server_1.0.0_win-x64"

# Names chosen so that an ordinal sort and a culture-aware one disagree, and so
# that the tree has real depth. The content is inert text: this suite never
# stages a server, a payload or a runtime, and never reaches a registry.
STAGE_FILES = {
    "tesserafin.exe": "MZ not-a-real-image\n",
    "Tesserafin.Api.dll": "managed\n",
    "tesserafin.runtimeconfig.json": "{}\n",
    "Resources/Configuration/logging.json": "{\"Serilog\":{}}\n",
    "web/index.html": "<!doctype html>\n",
    "web/assets/main.js": "console.log(1)\n",
    "web-manifest.json": "{}\n",
    "ffmpeg/ffmpeg.exe": "MZ ffmpeg\n",
    "ffmpeg/LICENSES/x264-COPYING": "GPL\n",
    "licenses/LICENSE": "GPL-2.0-or-later\n",
    "licenses/provenance.json": "{\"schemaVersion\":1}\n",
}


def build_stage(root, mtime=None, extra=None):
    """One top-level directory holding a small but structurally real package."""
    package = os.path.join(root, PACKAGE_NAME)
    entries = dict(STAGE_FILES)
    entries.update(extra or {})
    for relative, content in entries.items():
        path = os.path.join(package, *relative.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(content)
    if mtime is not None:
        for current, directories, files in os.walk(root):
            for name in list(directories) + list(files):
                os.utime(os.path.join(current, name), (mtime, mtime))
        os.utime(root, (mtime, mtime))
    return package


# ===========================================================================
# Reporting
# ===========================================================================

class Report:
    def __init__(self):
        self.rows = []

    def record(self, name, status, detail):
        self.rows.append((name, status, detail))
        print("  %-5s %-5s %s" % (name, status, detail), flush=True)

    def names(self):
        return {name for name, _, _ in self.rows}

    def counts(self):
        totals = {"PASS": 0, "RED": 0, "INERT": 0}
        for _, status, _ in self.rows:
            totals[status] = totals.get(status, 0) + 1
        return totals


def expect_refusal(report, name, completed, needle, description):
    """A negative control: non-zero exit, the assembler's OWN named refusal, and
    no archive anywhere."""
    if completed.returncode == 0:
        report.record(name, "RED", "%s: the assembler accepted it (exit 0)" % description)
        return False
    if needle not in completed.text:
        report.record(name, "RED", "%s: refused without saying %r -- %s"
                      % (description, needle, completed.text.strip()[-260:]))
        return False
    leftovers = completed.archives()
    if leftovers:
        report.record(name, "RED", "%s: refused but left %s behind"
                      % (description, ", ".join(leftovers)))
        return False
    report.record(name, "PASS", "%s -> %s" % (description, needle))
    return True


# ===========================================================================
# Auditing the production path
# ===========================================================================

def assembler_param_block(text):
    """The assembler's TOP-LEVEL param block only.

    Helper functions take parameters of their own, and a scan over the whole
    file would report those as if they were caller-facing options.
    """
    match = re.search(r"^param\(", text, re.MULTILINE)
    if not match:
        return None
    depth = 0
    for index in range(match.start() + len("param"), len(text)):
        if text[index] == "(":
            depth += 1
        elif text[index] == ")":
            depth -= 1
            if depth == 0:
                return text[match.start():index + 1]
    return None


def identity_findings(assembler_text, workflow_text):
    """Nothing on the production path may let a caller choose what is packaged."""
    findings = []
    block = assembler_param_block(strip_commentary(ASSEMBLER, assembler_text))
    if block is None:
        return ["the assembler has no readable top-level param block"]
    for parameter in FORBIDDEN_PARAMETERS:
        if re.search(r"%s\b" % re.escape(parameter), block):
            findings.append("the assembler takes a %s parameter" % parameter)
    if workflow_text is not None:
        code = strip_commentary(WORKFLOW, workflow_text)
        for match in re.finditer(r"(-[A-Z][A-Za-z0-9]*)\s", code):
            argument = match.group(1)
            if argument.startswith("-") and argument not in ASSEMBLER_ARGUMENTS and \
                    argument in ("-Reference", "-Digest", "-Tag", "-RunId", "-StageRoot",
                                 "-Registry", "-Image", "-Version"):
                findings.append("the workflow passes %s" % argument)
    return findings


SELF_CONTAINED_RULES = (
    ("hostfxr.dll", "does not require hostfxr.dll"),
    ("hostpolicy.dll", "does not require hostpolicy.dll"),
    ("coreclr.dll", "does not require coreclr.dll"),
    ("System.Private.CoreLib.dll", "does not require System.Private.CoreLib.dll"),
    ("'frameworks'", "does not refuse a shared framework declaration"),
    ("includedFrameworks", "does not require includedFrameworks"),
    ("0x8664", "does not require a PE x64 machine"),
)


def self_contained_findings(text):
    """A publish tree that is not self-contained produces a ZIP that needs a .NET
    runtime the operator has to install, which is exactly what the portable ZIP
    exists not to require.

    This is a SOURCE audit rather than a behavioural control: reaching the check
    means publishing the server first, and a suite that had to build the server
    to prove the check exists would be a build, not a control.
    """
    code = strip_commentary(ASSEMBLER, text)
    findings = []
    if "function Assert-SelfContained" not in code:
        return ["the assembler has no self-containment check at all"]
    if "Assert-SelfContained $publish" not in code:
        findings.append("never calls its self-containment check on the publish tree")
    for needle, label in SELF_CONTAINED_RULES:
        if needle not in code:
            findings.append(label)
    return findings


def audit_workflow(text):
    """Every way the new workflow could stop being what the ruling authorised.

    Runs over the executable part only. A permission grant, a trigger and a job
    are things a workflow DOES; a comment naming one is a comment.
    """
    code = strip_commentary("workflow.yml", text)
    findings = []

    forbidden = {
        r"write-all": "grants write-all",
        r"packages:\s*write": "declares packages: write",
        r"contents:\s*write": "declares contents: write",
        r"id-token:\s*write": "declares id-token: write",
        r"^[ \t]*[a-z-]+:[ \t]*write[ \t]*$": "declares a write permission",
        r"^\s*pull_request_target:": "declares pull_request_target",
        r"^\s*workflow_dispatch:": "declares workflow_dispatch",
        r"^\s*workflow_call:": "declares workflow_call",
        r"^\s*schedule:": "declares schedule",
        r"^\s*push:": "declares push",
        r"actions/upload-artifact": "uploads an Actions artifact",
        r"actions/download-artifact": "downloads an Actions artifact",
        r"actions/cache": "uses an Actions cache",
        r"\bdocker\b": "names docker",
        r"\bpodman\b": "names podman",
        r"\bnerdctl\b": "names nerdctl",
    }
    for pattern, label in forbidden.items():
        if re.search(pattern, code, re.MULTILINE):
            findings.append(label)

    triggers, trigger_blocks = _workflow_triggers(code)
    if not triggers:
        findings.append("declares no trigger this audit can read")
    if trigger_blocks > 1:
        findings.append("declares %d top-level on: blocks" % trigger_blocks)
    for trigger in triggers:
        if trigger not in WORKFLOW_ALLOWED_TRIGGERS:
            findings.append("triggers on '%s'" % trigger)
    for required in WORKFLOW_ALLOWED_TRIGGERS:
        if required not in triggers:
            findings.append("has no %s trigger" % required)

    jobs, job_blocks = _workflow_jobs(code)
    if not jobs:
        findings.append("declares no job this audit can read")
    if job_blocks > 1:
        findings.append("declares %d top-level jobs: blocks" % job_blocks)
    for job in jobs:
        if job not in WORKFLOW_ALLOWED_JOBS:
            findings.append("declares the unauthorised job '%s'" % job)
    for job in sorted(set(jobs)):
        if jobs.count(job) > 1:
            findings.append("declares the job '%s' %d times" % (job, jobs.count(job)))

    for match in re.finditer(r"uses:\s*([^\s#]+)", code):
        reference = match.group(1)
        if "@" not in reference or not re.search(r"@[0-9a-f]{40}$", reference):
            findings.append("unpinned action %s" % reference)

    for match in re.finditer(r"ghcr\.io/[A-Za-z0-9._/-]+", code):
        image = match.group(0)
        if "@sha256:" not in code[match.start():match.start() + len(image) + 80]:
            findings.append("mutable image reference %s" % image)

    if "persist-credentials: false" not in code:
        findings.append("checks out with persisted credentials")
    if "contents: read" not in code:
        findings.append("does not request contents: read at any scope")
    if "windows-latest" not in code:
        findings.append("has no windows-latest proof job")
    if "'ci/windows/w2/**'" not in code:
        findings.append("does not watch the whole ci/windows/w2 tree")

    findings.extend(workflow_assembly_findings(code))
    return findings


def workflow_assembly_findings(code):
    """The workflow must drive TWO assemblies into two trees and compare bytes."""
    findings = []
    calls = re.findall(r"assemble-server-zip\.ps1(.*?)(?:\n\s*\n|if \(\$LASTEXITCODE)",
                       code, re.DOTALL)
    if len(calls) < 2:
        findings.append("invokes the assembler %d times; two independent assemblies are the "
                        "whole evidence of this slice" % len(calls))
    work_dirs = set(re.findall(r"-WorkDir \(Join-Path \$env:RUNNER_TEMP '([^']+)'\)", code))
    out_dirs = set(re.findall(r"-OutDir \(Join-Path \$env:RUNNER_TEMP '([^']+)'\)", code))
    if len(work_dirs) < 2:
        findings.append("does not give the two assemblies different work directories")
    if len(out_dirs) < 2:
        findings.append("does not give the two assemblies different output directories")
    if "-StageRoot" in code:
        findings.append("passes -StageRoot; the pack-only oracle is not the production door")
    if "SequenceEqual" not in code:
        findings.append("does not compare the two archives byte for byte")
    epochs = set(re.findall(r"-SourceDateEpoch\s+(\$\{\{[^}]*\}\}|[^\s`]+)", code))
    if not epochs:
        findings.append("passes no -SourceDateEpoch")
    elif len(epochs) > 1:
        findings.append("gives the two assemblies different epochs: %s" % sorted(epochs))
    for epoch in epochs:
        if "steps.epoch.outputs.epoch" not in epoch:
            findings.append("takes its epoch from %r rather than the derived commit time" % epoch)
    if "git log -1 --format=%ct" not in code:
        findings.append("does not derive SOURCE_DATE_EPOCH from the commit")
    return findings


def _workflow_triggers(text):
    """Every trigger under every top-level `on:`, block style or flow style.

    Deliberately not `yaml.safe_load`: a loader resolves duplicate keys
    last-wins, so a file carrying two `on:` blocks would be audited on whichever
    one the loader kept rather than on everything the file says. Reading the
    first block and stopping is the same defect mirrored, so every block is read
    and the count is reported.
    """
    lines = text.splitlines()
    names = []
    blocks = 0
    for index, line in enumerate(lines):
        match = re.match(r"^on:(.*)$", line)
        if not match:
            continue
        blocks += 1
        inline = match.group(1).strip()
        if inline:
            names.extend(re.findall(r"[A-Za-z_][A-Za-z0-9_]*", inline))
            continue
        for following in lines[index + 1:]:
            if not following.strip():
                continue
            if not following.startswith(" "):
                break
            nested = re.match(r"^  ([A-Za-z_][A-Za-z0-9_]*):", following)
            if nested:
                names.append(nested.group(1))
    return names, blocks


def _workflow_jobs(text):
    """Every job under every top-level `jobs:`, from raw text, for that reason."""
    names = []
    blocks = 0
    inside = False
    for line in text.splitlines():
        if re.match(r"^jobs:\s*$", line):
            blocks += 1
            inside = True
            continue
        if not inside:
            continue
        if line.strip() and not line.startswith(" "):
            inside = False
            continue
        nested = re.match(r"^  ([A-Za-z0-9_-]+):", line)
        if nested:
            names.append(nested.group(1))
    return names, blocks


def v1_permission_mutation(text):
    """The exact shape W2-A0-V1 walked past that slice's workflow audit.

    Everything the rest of the audit reads is carried over verbatim -- the
    pinned checkout SHA, `persist-credentials: false`, `windows-latest`, the two
    assembler invocations -- so the only reason this file must be refused is the
    trigger, the two `write-all` grants and the second job. Every block it
    replaces is DEMOTED TO A COMMENT rather than deleted, so `pull_request:` and
    `contents: read` still appear in the file: that is exactly the substitution
    the earlier audit accepted, and a mutation that merely deleted them would be
    refused by rules that predate this repair.
    """
    lines = text.splitlines()
    total = len(lines)
    out = []
    applied = []
    index = 0

    def copy_until(predicate):
        nonlocal index
        while index < total and not predicate(lines[index]):
            out.append(lines[index])
            index += 1
        return index < total

    def demote_block(indent):
        nonlocal index
        out.append("#" + lines[index])
        index += 1
        while index < total and (not lines[index].strip() or lines[index].startswith(indent)):
            if lines[index].strip():
                out.append("#" + lines[index])
            index += 1

    if copy_until(lambda line: line == "on:"):
        out.append("on: [push, pull_request]")
        demote_block(" ")
        out.append("")
        applied.append("push trigger")

    if copy_until(lambda line: line == "permissions:"):
        out.append("permissions: write-all")
        demote_block(" ")
        out.append("")
        applied.append("workflow-scope write-all")

    if copy_until(lambda line: line == "    permissions:"):
        out.append("    permissions: write-all")
        demote_block("      ")
        applied.append("job-scope write-all with the reads demoted to comments")

    out.extend(lines[index:])
    out.extend([
        "",
        "  exfiltrate:",
        "    runs-on: ubuntu-latest",
        "    permissions: write-all",
        "    steps:",
        "      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7.0.0",
        "        with:",
        "          persist-credentials: false",
    ])
    applied.append("second job")
    return "\n".join(out) + "\n", applied


# A workflow with the real file's structural shape and nothing else. Once the V1
# mutation has been applied to the real file, that file no longer HAS an `on:`
# block or a workflow-scope `permissions:` block to rewrite, so a proof that
# could only mutate the live text would degrade to INERT at exactly the moment
# it is supposed to report RED. This baseline audits clean, which the control
# asserts, so every finding the proof requires comes from the mutation.
FROZEN_SHAPE_BASELINE = """\
name: baseline
on:
  pull_request:
    paths:
      - 'ci/windows/w2/**'

permissions:
  contents: read

jobs:
  zip:
    runs-on: windows-latest
    permissions:
      contents: read
      packages: read
    steps:
      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7.0.0
        with:
          persist-credentials: false
      - name: epoch
        shell: pwsh
        run: |
          $epoch = (& git log -1 --format=%ct $commit).Trim()
      - name: first
        shell: pwsh
        run: |
          ./ci/windows/w2/assemble-server-zip.ps1 `
            -RepoRoot $PWD `
            -WorkDir (Join-Path $env:RUNNER_TEMP 'w2a2-first/work') `
            -OutDir (Join-Path $env:RUNNER_TEMP 'w2a2-first/out') `
            -SourceDateEpoch ${{ steps.epoch.outputs.epoch }} `
            -OrasPath (Join-Path $env:RUNNER_TEMP 'bin\\oras.exe')
          if ($LASTEXITCODE -ne 0) { throw 'no archive' }
      - name: second
        shell: pwsh
        run: |
          ./ci/windows/w2/assemble-server-zip.ps1 `
            -RepoRoot $PWD `
            -WorkDir (Join-Path $env:RUNNER_TEMP 'w2a2-second/work') `
            -OutDir (Join-Path $env:RUNNER_TEMP 'w2a2-second/out') `
            -SourceDateEpoch ${{ steps.epoch.outputs.epoch }} `
            -OrasPath (Join-Path $env:RUNNER_TEMP 'bin\\oras.exe')
          if ($LASTEXITCODE -ne 0) { throw 'no archive' }
      - name: compare
        shell: pwsh
        run: |
          if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$a, [byte[]]$b)) { throw 'differ' }
"""


# ===========================================================================
# The controls
# ===========================================================================

def run_controls(work, report, only=None):
    def selected(name):
        return only is None or name in only

    shell = powershell()
    assembler_text = read_text(ASSEMBLER) if os.path.isfile(ASSEMBLER) else None
    workflow_text = read_text(WORKFLOW) if os.path.isfile(WORKFLOW) else None

    # --- Z01-Z03, Z04-Z06, Z08, Z17: the packer, driven for real -------------
    behavioural = ("Z01", "Z02", "Z03", "Z04", "Z05", "Z06", "Z08", "Z17", "Z19")
    if any(selected(name) for name in behavioural):
        if shell is None:
            for name in behavioural:
                if selected(name):
                    report.record(name, "INERT",
                                  "no PowerShell on PATH; the packer cannot be driven")
        elif assembler_text is None:
            for name in behavioural:
                if selected(name):
                    report.record(name, "RED", "the assembler is missing")
        else:
            _behavioural_controls(work, report, shell, selected)

    # --- Z07: four statements of one identity --------------------------------
    if selected("Z07"):
        _identity_agreement(report, assembler_text, workflow_text)

    # --- Z09: no caller-supplied identity ------------------------------------
    if selected("Z09"):
        if assembler_text is None:
            report.record("Z09", "RED", "the assembler is missing")
        else:
            planted = assembler_text.replace(
                "    [Parameter(Mandatory = $true, ParameterSetName = 'Assemble')]\n"
                "    [string] $RepoRoot,",
                "    [Parameter(Mandatory = $true, ParameterSetName = 'Assemble')]\n"
                "    [string] $RepoRoot,\n\n    [string] $Reference,", 1)
            if not identity_findings(planted, workflow_text):
                report.record("Z09", "INERT", "the parameter audit does not detect a planted "
                                              "$Reference")
            else:
                findings = identity_findings(assembler_text, workflow_text)
                if findings:
                    report.record("Z09", "RED", "; ".join(findings))
                else:
                    report.record("Z09", "PASS",
                                  "the assembler's param block offers no reference, digest, tag, "
                                  "run id, registry, image or version, and the workflow passes "
                                  "none: what is packaged travels with the commit")

    # --- Z10: no container runtime -------------------------------------------
    if selected("Z10"):
        production = [path for path in (ASSEMBLER, WORKFLOW) if os.path.isfile(path)]
        planted = os.path.join(work, "planted-docker.yml")
        with open(planted, "w", encoding="utf-8") as handle:
            handle.write("jobs:\n  x:\n    steps:\n      - run: docker pull ghcr.io/x/y\n")
        if not scan_for([planted], r"\b(docker|podman|nerdctl|containerd)\b", code_only=True):
            report.record("Z10", "INERT", "the container scanner does not detect a planted pull")
        else:
            hits = scan_for(production, r"\b(docker|podman|nerdctl|containerd)\b", code_only=True)
            if hits:
                report.record("Z10", "RED", "a container-runtime dependency remains: %s" % hits[:3])
            elif len(production) != 2:
                report.record("Z10", "RED", "the production path is not both files")
            else:
                report.record("Z10", "PASS",
                              "neither the assembler nor the workflow names a container engine, "
                              "a daemon or a CLI for one; both consumers speak the registry "
                              "protocol directly")

    # --- Z11: no artifact, no cache, no dispatch, no oracle in production -----
    if selected("Z11"):
        planted = os.path.join(work, "planted-artifact.yml")
        with open(planted, "w", encoding="utf-8") as handle:
            handle.write("jobs:\n  x:\n    steps:\n"
                         "      - uses: actions/upload-artifact@0000000000000000000000000000000000000000\n"
                         "      - uses: actions/cache@0000000000000000000000000000000000000000\n")
        proof = audit_workflow(read_text(planted))
        if "uploads an Actions artifact" not in proof or "uses an Actions cache" not in proof:
            report.record("Z11", "INERT", "the artifact scanner does not detect a planted upload")
        elif workflow_text is None:
            report.record("Z11", "RED", "the W2-A2 workflow is missing entirely")
        else:
            code = strip_commentary(WORKFLOW, workflow_text)
            findings = []
            for pattern, label in (
                    (r"actions/upload-artifact", "uploads an Actions artifact"),
                    (r"actions/download-artifact", "downloads an Actions artifact"),
                    (r"actions/cache", "uses an Actions cache"),
                    (r"workflow_dispatch", "declares workflow_dispatch"),
                    (r"-StageRoot", "passes the pack-only oracle's -StageRoot")):
                if re.search(pattern, code):
                    findings.append(label)
            if findings:
                report.record("Z11", "RED", "; ".join(findings))
            else:
                report.record("Z11", "PASS",
                              "no artifact upload or download, no cache and no dispatch, so the "
                              "ZIP can never become a production input to another job; and the "
                              "workflow never passes -StageRoot, so the pack-only oracle these "
                              "controls drive is not the production door")

    # --- Z12: the descriptive tag is never the trust boundary ----------------
    if selected("Z12"):
        production = [path for path in (ASSEMBLER, WORKFLOW) if os.path.isfile(path)]
        planted = os.path.join(work, "planted-tag.ps1")
        with open(planted, "w", encoding="utf-8") as handle:
            handle.write("$reference = 'ghcr.io/x/y:%s'\n" % DESCRIPTIVE_TAG)
        if not scan_for([planted], re.escape(DESCRIPTIVE_TAG), code_only=True):
            report.record("Z12", "INERT", "the tag scanner does not detect a planted tag")
        else:
            hits = scan_for(production, re.escape(DESCRIPTIVE_TAG), code_only=True)
            if hits:
                report.record("Z12", "RED", "the production path names the tag: %s" % hits[:3])
            else:
                report.record("Z12", "PASS",
                              "neither the assembler nor the workflow names the descriptive tag "
                              "%s; the runtime is consumed by digest only" % DESCRIPTIVE_TAG)

    # --- Z13: the workflow is the authorised one, byte for byte --------------
    if selected("Z13"):
        _workflow_control(report, work, workflow_text)

    # --- Z14: the four frozen inputs are unmodified --------------------------
    if selected("Z14"):
        findings = []
        for relative, pinned in sorted(FROZEN_PINS.items()):
            path = os.path.join(REPO_ROOT, *relative.split("/"))
            if not os.path.isfile(path):
                findings.append("%s is missing" % relative)
                continue
            with open(path, "rb") as handle:
                raw = handle.read()
            digest = hashlib.sha256(raw).hexdigest()
            if digest == pinned:
                continue
            if hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest() == pinned:
                findings.append("%s differs from the pin in its line endings only" % relative)
            else:
                findings.append("%s is not the pinned file: %s, pinned %s"
                                % (relative, digest[:16], pinned[:16]))
        if findings:
            report.record("Z14", "RED", "; ".join(findings))
        else:
            report.record("Z14", "PASS",
                          "all %d frozen inputs are byte-identical to their pins; W2-A2 drives "
                          "them and audits them, and authors none of them" % len(FROZEN_PINS))

    # --- Z15: two independent assemblies, compared as bytes ------------------
    if selected("Z15"):
        if workflow_text is None:
            report.record("Z15", "RED", "the W2-A2 workflow is missing entirely")
        else:
            code = strip_commentary(WORKFLOW, workflow_text)
            single = re.sub(r"      - name: Assemble it again into a second clean tree.*?"
                            r"(?=      - name: Require)", "", code, flags=re.DOTALL)
            if not workflow_assembly_findings(single):
                report.record("Z15", "INERT",
                              "the two-assembly check does not notice a removed assembly")
            else:
                findings = workflow_assembly_findings(code)
                if findings:
                    report.record("Z15", "RED", "; ".join(findings))
                else:
                    report.record("Z15", "PASS",
                                  "two assemblies into two work directories and two output "
                                  "directories, both given the epoch derived from the commit, "
                                  "compared by length, digest and SequenceEqual")

    # --- Z16: the path filter that closes W2-A1-V1 NB-1 ----------------------
    if selected("Z16"):
        if workflow_text is None:
            report.record("Z16", "RED", "the W2-A2 workflow is missing entirely")
        else:
            code = strip_commentary(WORKFLOW, workflow_text)
            required = ("'ci/windows/w2/**'",
                        "'ci/windows/runtime-retention/consume.ps1'",
                        "'ci/windows/runtime-retention/accepted-runtime.json'",
                        "'.github/workflows/w2-windows-server-zip.yml'")
            missing = [item for item in required if item not in code]
            widened = code.replace("'ci/windows/w2/**'", "'ci/windows/w2/zip-controls.py'")
            if "'ci/windows/w2/**'" in widened:
                report.record("Z16", "INERT", "the path-filter check cannot distinguish a "
                                              "narrowed filter")
            elif missing:
                report.record("Z16", "RED", "the paths filter omits %s" % ", ".join(missing))
            else:
                report.record("Z16", "PASS",
                              "the paths filter watches the whole ci/windows/w2 tree, so a second "
                              "file under it cannot silently skip this job (W2-A1-V1 NB-1), plus "
                              "the two frozen files it runs")

    # --- Z20: the publish tree is proven self-contained ----------------------
    if selected("Z20"):
        if assembler_text is None:
            report.record("Z20", "RED", "the assembler is missing")
        else:
            stripped = assembler_text.replace("Assert-SelfContained $publish",
                                              "# Assert-SelfContained $publish", 1)
            gutted = assembler_text
            for needle, _ in SELF_CONTAINED_RULES:
                gutted = gutted.replace(needle, "REMOVED")
            if not self_contained_findings(stripped):
                report.record("Z20", "INERT",
                              "the audit does not notice a self-containment check that is never "
                              "called")
            elif len(self_contained_findings(gutted)) < len(SELF_CONTAINED_RULES):
                report.record("Z20", "INERT",
                              "the audit does not notice every removed self-containment rule")
            else:
                findings = self_contained_findings(assembler_text)
                if findings:
                    report.record("Z20", "RED", "; ".join(findings))
                else:
                    report.record("Z20", "PASS",
                                  "the publish tree must carry the four host components, declare "
                                  "no shared framework and an includedFrameworks, and ship a PE "
                                  "x64 tesserafin.exe, and the check is called on it before "
                                  "anything is staged")

    # --- Z18: the assembler starts nothing -----------------------------------
    if selected("Z18"):
        production = [path for path in (ASSEMBLER, WORKFLOW) if os.path.isfile(path)]
        # Deliberately not "names tesserafin.exe": the assembler has to name it
        # to prove it is a PE x64 image, and a rule that cannot tell naming a
        # file from executing it would have to be loosened until it missed the
        # real thing. What is forbidden is starting or installing something.
        pattern = (r"(New-Service|Start-Service|New-ScheduledTask|\bsc\.exe\b|"
                   r"Start-Process|Invoke-WebRequest|Invoke-RestMethod|Invoke-Expression)")
        planted = os.path.join(work, "planted-start.ps1")
        with open(planted, "w", encoding="utf-8") as handle:
            handle.write("Start-Process -FilePath (Join-Path $stage 'tesserafin.exe')\n")
        if not scan_for([planted], pattern, code_only=True):
            report.record("Z18", "INERT", "the start scanner does not detect a planted start")
        else:
            hits = scan_for(production, pattern, code_only=True)
            if hits:
                report.record("Z18", "RED", "the production path starts or installs something: %s"
                              % hits[:3])
            else:
                report.record("Z18", "PASS",
                              "neither file registers a service, starts a process, or reaches an "
                              "HTTP endpoint; relocation and start-up are §2.3's proof and are "
                              "not claimed here")


def _behavioural_controls(work, report, shell, selected):
    """Every named refusal, observed live, each paired with an INERT-proof."""
    root = os.path.join(work, "stages")
    os.makedirs(root, exist_ok=True)
    epoch = 1756900000

    def stage(name, **kwargs):
        path = os.path.join(root, name)
        os.makedirs(path, exist_ok=True)
        build_stage(path, **kwargs)
        return path

    def out(name):
        return os.path.join(work, "out-%s" % name)

    # --- Z01: no epoch at all -------------------------------------------------
    if selected("Z01"):
        plain = stage("z01")
        result = pack(shell, ASSEMBLER, plain, out("z01"))
        if expect_refusal(report, "Z01", result, "no SOURCE_DATE_EPOCH was given",
                          "the packer is given no epoch"):
            mutant = mutate(work, "clock-epoch")
            if mutant is None:
                report.rows[-1] = ("Z01", "INERT",
                                   "the clock-substitution mutation no longer applies")
                print("  %-5s %-5s %s" % ("Z01", "INERT", report.rows[-1][2]), flush=True)
            else:
                loose = pack(shell, mutant, plain, out("z01-mutant"))
                if loose.returncode != 0 or not loose.archives():
                    report.rows[-1] = ("Z01", "INERT",
                                       "with the epoch check defeated the packer still produced "
                                       "no archive, so the check is not shown to be load-bearing")
                    print("  %-5s %-5s %s" % ("Z01", "INERT", report.rows[-1][2]), flush=True)

    # --- Z02: a zero epoch ----------------------------------------------------
    if selected("Z02"):
        result = pack(shell, ASSEMBLER, stage("z02"), out("z02"), epoch=0)
        expect_refusal(report, "Z02", result, "no SOURCE_DATE_EPOCH was given",
                       "the packer is given epoch 0")

    # --- Z03: an epoch a ZIP cannot represent --------------------------------
    if selected("Z03"):
        result = pack(shell, ASSEMBLER, stage("z03"), out("z03"), epoch=100)
        expect_refusal(report, "Z03", result, "is before 1980-01-01",
                       "the packer is given a pre-1980 epoch")

    # --- Z04: the clamp is load-bearing --------------------------------------
    if selected("Z04"):
        left = stage("z04a", mtime=1000000000)
        right = stage("z04b", mtime=1600000000)
        first = pack(shell, ASSEMBLER, left, out("z04a"), epoch=epoch)
        second = pack(shell, ASSEMBLER, right, out("z04b"), epoch=epoch)
        if first.returncode != 0 or second.returncode != 0:
            report.record("Z04", "RED", "the packer refused an ordinary stage: %s"
                          % (first.text or second.text).strip()[-260:])
        else:
            a = sha256_file(os.path.join(out("z04a"), first.archives()[0]))
            b = sha256_file(os.path.join(out("z04b"), second.archives()[0]))
            if a != b:
                report.record("Z04", "RED",
                              "the same content staged with different filesystem timestamps "
                              "packs to %s and %s" % (a[:16], b[:16]))
            else:
                mutant = mutate(work, "disk-mtime")
                if mutant is None:
                    report.record("Z04", "INERT", "the disk-mtime mutation no longer applies")
                else:
                    ma = pack(shell, mutant, left, out("z04a-mutant"), epoch=epoch)
                    mb = pack(shell, mutant, right, out("z04b-mutant"), epoch=epoch)
                    if ma.returncode != 0 or mb.returncode != 0:
                        report.record("Z04", "INERT",
                                      "the disk-mtime mutant refused rather than packing")
                    else:
                        x = sha256_file(os.path.join(out("z04a-mutant"), ma.archives()[0]))
                        y = sha256_file(os.path.join(out("z04b-mutant"), mb.archives()[0]))
                        if x == y:
                            report.record("Z04", "INERT",
                                          "a packer reading mtimes from disk produced the same "
                                          "bytes, so this stage cannot show the clamp matters")
                        else:
                            report.record("Z04", "PASS",
                                          "identical content with filesystem timestamps 10^9 "
                                          "apart packs to the same %s, while a copy reading "
                                          "mtimes from disk splits into %s and %s"
                                          % (a[:16], x[:16], y[:16]))

    # --- Z05: the entry order is load-bearing and ordinal --------------------
    if selected("Z05"):
        plain = stage("z05")
        real = pack(shell, ASSEMBLER, plain, out("z05"), epoch=epoch)
        if real.returncode != 0:
            report.record("Z05", "RED", "the packer refused an ordinary stage: %s"
                          % real.text.strip()[-260:])
        else:
            archive = os.path.join(out("z05"), real.archives()[0])
            with zipfile.ZipFile(archive) as handle:
                order = handle.namelist()
            expected = sorted(order)
            mutant = mutate(work, "shuffled-order")
            if order != expected:
                report.record("Z05", "RED",
                              "the archive's entries are not in ordinal order: %s" % order[:4])
            elif mutant is None:
                report.record("Z05", "INERT", "the entry-order mutation no longer applies")
            else:
                shuffled = pack(shell, mutant, plain, out("z05-mutant"), epoch=epoch)
                if shuffled.returncode != 0 or not shuffled.archives():
                    report.record("Z05", "INERT", "the shuffled-order mutant produced no archive")
                else:
                    a = sha256_file(archive)
                    b = sha256_file(os.path.join(out("z05-mutant"), shuffled.archives()[0]))
                    if a == b:
                        report.record("Z05", "INERT",
                                      "a packer emitting a different entry order produced the "
                                      "same bytes")
                    else:
                        report.record("Z05", "PASS",
                                      "%d entries in ordinal order, and a copy emitting any other "
                                      "order packs to %s rather than %s"
                                      % (len(order), b[:16], a[:16]))

    # --- Z06: state under the stage ------------------------------------------
    if selected("Z06"):
        planted = {
            "network.xml": "<ServerConfiguration />\n",
            "data/library.db": "SQLite format 3\n",
            "log/tesserafin.log": "started\n",
        }
        for name, (relative, needle) in enumerate((
                ("network.xml", "is server configuration"),
                ("data/library.db", "is a .db file"),
                ("log/tesserafin.log", "is a .log file")), start=1):
            label = "z06-%d" % name
            dirty = stage(label, extra={relative: planted[relative]})
            result = pack(shell, ASSEMBLER, dirty, out(label), epoch=epoch)
            if result.returncode == 0 or needle not in result.text:
                report.record("Z06", "RED",
                              "the packer did not refuse '%s': %s"
                              % (relative, result.text.strip()[-260:]))
                break
        else:
            mutant = mutate(work, "no-state-scan")
            if mutant is None:
                report.record("Z06", "INERT", "the state-scan mutation no longer applies")
            else:
                dirty = stage("z06-mutant", extra={"network.xml": planted["network.xml"]})
                loose = pack(shell, mutant, dirty, out("z06-mutant"), epoch=epoch)
                if loose.returncode != 0 or not loose.archives():
                    report.record("Z06", "INERT",
                                  "with the state scan defeated the packer still refused, so the "
                                  "scan is not shown to be load-bearing")
                else:
                    with zipfile.ZipFile(os.path.join(out("z06-mutant"),
                                                      loose.archives()[0])) as handle:
                        packed = [n for n in handle.namelist() if n.endswith("network.xml")]
                    if not packed:
                        report.record("Z06", "INERT",
                                      "the state-scan mutant produced an archive without the "
                                      "planted file")
                    else:
                        report.record("Z06", "PASS",
                                      "network.xml, a *.db under data/ and a *.log under log/ are "
                                      "each refused by name, and a copy with the scan defeated "
                                      "packs %s" % packed[0])

    # --- Z08: a second top-level directory -----------------------------------
    if selected("Z08"):
        two = stage("z08")
        os.makedirs(os.path.join(two, "scattered"), exist_ok=True)
        with open(os.path.join(two, "scattered", "readme.txt"), "w", encoding="utf-8") as handle:
            handle.write("loose\n")
        result = pack(shell, ASSEMBLER, two, out("z08"), epoch=epoch)
        if expect_refusal(report, "Z08", result, "top-level entries",
                          "the stage holds two top-level directories"):
            mutant = mutate(work, "many-top-level")
            if mutant is None:
                report.rows[-1] = ("Z08", "INERT",
                                   "the top-level mutation no longer applies")
                print("  %-5s %-5s %s" % ("Z08", "INERT", report.rows[-1][2]), flush=True)
            else:
                loose = pack(shell, mutant, two, out("z08-mutant"), epoch=epoch)
                if loose.returncode != 0 or not loose.archives():
                    report.rows[-1] = ("Z08", "INERT",
                                       "with the top-level check defeated the packer still "
                                       "refused")
                    print("  %-5s %-5s %s" % ("Z08", "INERT", report.rows[-1][2]), flush=True)
                else:
                    with zipfile.ZipFile(os.path.join(out("z08-mutant"),
                                                      loose.archives()[0])) as handle:
                        tops = sorted({n.split("/")[0] for n in handle.namelist()})
                    if len(tops) < 2:
                        report.rows[-1] = ("Z08", "INERT",
                                           "the mutant packed one top-level directory anyway")
                        print("  %-5s %-5s %s" % ("Z08", "INERT", report.rows[-1][2]), flush=True)

    # --- Z17: the archive extracts under exactly one top-level directory -----
    if selected("Z17"):
        plain = stage("z17")
        result = pack(shell, ASSEMBLER, plain, out("z17"), epoch=epoch)
        if result.returncode != 0 or not result.archives():
            report.record("Z17", "RED", "the packer produced no archive: %s"
                          % result.text.strip()[-260:])
        else:
            archive = os.path.join(out("z17"), result.archives()[0])
            with zipfile.ZipFile(archive) as handle:
                bad = handle.testzip()
                names = handle.namelist()
                infos = handle.infolist()
            tops = sorted({name.split("/")[0] for name in names})
            findings = []
            if bad is not None:
                findings.append("entry '%s' fails its CRC" % bad)
            if tops != [PACKAGE_NAME]:
                findings.append("extracts into %s" % tops)
            if result.archives()[0] != PACKAGE_NAME + ".zip":
                findings.append("is named %s" % result.archives()[0])
            stamps = {info.date_time for info in infos}
            if len(stamps) != 1:
                findings.append("carries %d distinct timestamps" % len(stamps))
            directories = [info.filename for info in infos if info.is_dir()]
            if directories:
                findings.append("carries %d directory entries" % len(directories))
            modes = {(info.external_attr >> 16) & 0o7777 for info in infos}
            if not modes <= {0o755, 0o644}:
                findings.append("carries modes %s" % sorted(oct(m) for m in modes))
            if findings:
                report.record("Z17", "RED", "; ".join(findings))
            else:
                report.record("Z17", "PASS",
                              "%d entries, every CRC good, one top-level directory '%s', one "
                              "timestamp %s, no directory entries, modes 0755/0644 only"
                              % (len(names), tops[0], sorted(stamps)[0]))


    # --- Z19: an odd epoch is a ZIP timestamp, not an error ------------------
    #
    # A regression guard, not a hypothetical. An MS-DOS timestamp stores
    # seconds/2, so it truncates an odd second to the even one below, and the
    # packer's own read-back check refused every odd clamp until it was taught
    # the format's granularity. Committer times are odd about half the time, so
    # that defect would have reddened one hosted run in two and looked like
    # nondeterminism rather than like arithmetic.
    if selected("Z19"):
        plain = stage("z19")
        odd = pack(shell, ASSEMBLER, plain, out("z19"), epoch=epoch + 1)
        even = pack(shell, ASSEMBLER, plain, out("z19-even"), epoch=epoch)
        if odd.returncode != 0 or not odd.archives():
            report.record("Z19", "RED",
                          "an odd SOURCE_DATE_EPOCH was refused: %s" % odd.text.strip()[-260:])
        elif even.returncode != 0 or not even.archives():
            report.record("Z19", "RED", "an even SOURCE_DATE_EPOCH was refused: %s"
                          % even.text.strip()[-260:])
        else:
            with zipfile.ZipFile(os.path.join(out("z19"), odd.archives()[0])) as handle:
                stamps = {info.date_time for info in handle.infolist()}
            a = sha256_file(os.path.join(out("z19"), odd.archives()[0]))
            b = sha256_file(os.path.join(out("z19-even"), even.archives()[0]))
            if len(stamps) != 1:
                report.record("Z19", "RED", "an odd clamp produced %d timestamps" % len(stamps))
            elif sorted(stamps)[0][5] % 2 != 0:
                report.record("Z19", "RED",
                              "an odd clamp stored an odd second %s, which a ZIP cannot hold"
                              % (sorted(stamps)[0],))
            elif a != b:
                report.record("Z19", "RED",
                              "epochs %d and %d land in the same MS-DOS second but pack to %s and "
                              "%s" % (epoch, epoch + 1, a[:16], b[:16]))
            else:
                report.record("Z19", "PASS",
                              "epoch %d packs, storing the even second %s the format can hold, "
                              "and agrees byte for byte with epoch %d"
                              % (epoch + 1, sorted(stamps)[0], epoch))


def _identity_agreement(report, assembler_text, workflow_text):
    if assembler_text is None or workflow_text is None:
        report.record("Z07", "RED", "the production path is not both files")
        return
    if not os.path.isfile(ACCEPTED_JSON):
        report.record("Z07", "RED", "the committed acceptance manifest is missing")
        return
    accepted = json.loads(read_text(ACCEPTED_JSON))

    def statements():
        return {
            "the assembler": (
                WEB_PAYLOAD_SHA256 in strip_commentary(ASSEMBLER, assembler_text),
                ACCEPTED_RUNTIME_SHA256 in strip_commentary(ASSEMBLER, assembler_text)),
            "the workflow": (
                WEB_PAYLOAD_SHA256 in strip_commentary(WORKFLOW, workflow_text),
                ACCEPTED_RUNTIME_SHA256 in strip_commentary(WORKFLOW, workflow_text)),
        }

    findings = []
    for who, (web, runtime) in statements().items():
        if not web:
            findings.append("%s does not name WEB_PAYLOAD_SHA256" % who)
        if not runtime:
            findings.append("%s does not name the accepted runtimeSha256" % who)
    if accepted.get("runtimeSha256") != ACCEPTED_RUNTIME_SHA256:
        findings.append("the committed acceptance manifest pins runtime %s"
                        % accepted.get("runtimeSha256"))
    if accepted.get("platform") != "win-x64":
        findings.append("the committed acceptance manifest is for %s" % accepted.get("platform"))

    mutated = assembler_text.replace(WEB_PAYLOAD_SHA256, "0" * 64)
    if WEB_PAYLOAD_SHA256 in strip_commentary(ASSEMBLER, mutated):
        report.record("Z07", "INERT", "the agreement check does not detect a mutation")
        return
    if findings:
        report.record("Z07", "RED", "; ".join(findings))
    else:
        report.record("Z07", "PASS",
                      "the ruling, the assembler, the workflow and the committed acceptance "
                      "manifest state one web payload digest and one runtime digest, and agree")


def _workflow_control(report, work, workflow_text):
    if workflow_text is None:
        report.record("Z13", "RED", "the W2-A2 workflow is missing")
        return

    findings = audit_workflow(workflow_text)
    with open(WORKFLOW, "rb") as handle:
        raw = handle.read()
    digest = hashlib.sha256(raw).hexdigest()
    if digest != WORKFLOW_SHA256:
        if hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest() == WORKFLOW_SHA256:
            findings.append("differs from the pinned workflow in its line endings only "
                            "(%d bytes, pinned content)" % len(raw))
        else:
            findings.append("is not the pinned workflow: %s, pinned %s"
                            % (digest[:16], WORKFLOW_SHA256[:16]))

    # --- live-proof 1: the mutation that got past W2-A0's first audit ---------
    expected_edits = ["push trigger", "workflow-scope write-all",
                      "job-scope write-all with the reads demoted to comments", "second job"]
    mutant, applied = v1_permission_mutation(workflow_text)
    if applied != expected_edits:
        findings.append("no longer has the authorised trigger/permission/job shape; the V1 "
                        "mutation applies only %s" % (applied or "nothing"))
        if audit_workflow(FROZEN_SHAPE_BASELINE):
            report.record("Z13", "INERT", "the live-proof baseline does not audit clean: %s"
                          % audit_workflow(FROZEN_SHAPE_BASELINE))
            return
        mutant, applied = v1_permission_mutation(FROZEN_SHAPE_BASELINE)
        if applied != expected_edits:
            report.record("Z13", "INERT",
                          "the V1 permission mutation no longer applies to the baseline: %s"
                          % applied)
            return
    mutant_findings = audit_workflow(mutant)
    required = {
        "grants write-all": "write-all",
        # The V1 mutation writes `on: [push, pull_request]`, a flow-style
        # sequence, so the block-style `push:` rule is not what refuses it --
        # the trigger reader is. The block-style rule is proven separately, on
        # the planted fixture below.
        "triggers on 'push'": "the push trigger",
        "declares the unauthorised job 'exfiltrate'": "the second job",
        "does not request contents: read at any scope": "reads that exist only in a comment",
    }
    unproven = [description for finding, description in required.items()
                if finding not in mutant_findings]
    if unproven:
        report.record("Z13", "INERT",
                      "the workflow audit does not refuse %s" % ", ".join(sorted(unproven)))
        return
    if hashlib.sha256(mutant.encode("utf-8")).hexdigest() == WORKFLOW_SHA256:
        report.record("Z13", "INERT", "the content pin does not distinguish the mutation")
        return

    # --- live-proof 2: a planted violation of the other named rules -----------
    planted = os.path.join(work, "planted-workflow.yml")
    with open(planted, "w", encoding="utf-8") as handle:
        handle.write("on:\n  push:\n  workflow_dispatch:\npermissions:\n  packages: write\n"
                     "jobs:\n  x:\n    steps:\n      - uses: actions/checkout@v4\n"
                     "      - uses: actions/upload-artifact@v4\n"
                     "      - run: docker pull ghcr.io/x/y\n")
    planted_findings = audit_workflow(read_text(planted))
    for label in ("declares packages: write", "declares workflow_dispatch", "declares push",
                  "unpinned action actions/checkout@v4", "uploads an Actions artifact",
                  "names docker"):
        if label not in planted_findings:
            report.record("Z13", "INERT",
                          "the workflow audit does not detect a planted violation: %s" % label)
            return

    if findings:
        report.record("Z13", "RED", "; ".join(findings))
    else:
        report.record("Z13", "PASS",
                      "byte-identical to the pinned workflow, and its executable text declares "
                      "one on: block triggering pull_request only, one jobs: block with the one "
                      "authorised job, contents: read plus packages: read and no write grant at "
                      "either scope, no write-all, pinned actions, no persisted credentials, no "
                      "dispatch, no artifact, no cache, no container engine, and two assemblies "
                      "compared byte for byte")


# ===========================================================================

def _repository_fingerprint():
    fingerprint = {}
    for path in (ASSEMBLER, WORKFLOW, WEB_CONSUMER, TREE_DIGEST, RUNTIME_CONSUMER,
                 ACCEPTED_JSON, os.path.abspath(__file__)):
        if os.path.isfile(path):
            fingerprint[os.path.relpath(path, REPO_ROOT)] = sha256_file(path)
    return fingerprint


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--only", action="append", help="run only the named control(s)")
    args = parser.parse_args(argv)

    work = tempfile.mkdtemp(prefix="w2a2-controls-")
    try:
        before = _repository_fingerprint()
        print("W2-A2 hostile controls")
        report = Report()
        started = time.time()
        run_controls(work, report, set(args.only) if args.only else None)
        after = _repository_fingerprint()

        # A control that is deleted or renamed simply stops running, which would
        # make the summary line SHORTER and still green. The roster is what
        # turns that into a failure.
        if args.only is None:
            missing = sorted(set(ROSTER) - report.names())
            if missing:
                report.record("ROSTER", "RED", "these controls did not report: %s"
                              % ", ".join("%s (%s)" % (name, ROSTER[name]) for name in missing))
            else:
                report.record("ROSTER", "PASS",
                              "all %d rostered controls reported" % len(ROSTER))

        if before != after:
            report.record("RESTORE", "RED", "the controls modified the audited files")
        else:
            report.record("RESTORE", "PASS",
                          "every audited file is byte-identical to before the run")

        totals = report.counts()
        print("")
        print("W2-A2 controls: %d PASS, %d RED, %d INERT in %.1fs"
              % (totals["PASS"], totals["RED"], totals["INERT"], time.time() - started))
        return 0 if (totals["RED"] == 0 and totals["INERT"] == 0) else 1
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
