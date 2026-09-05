#!/usr/bin/env python3
"""Hostile controls for W2's FFmpeg acquisition path (W2-A1, #256).

W2-A1 adds no consumer. Its whole claim is that W2's production FFmpeg
acquisition is exactly `ci/windows/runtime-retention/consume.ps1`, driven by the
committed `ci/windows/runtime-retention/accepted-runtime.json`, and nothing
else. Both of those files are FROZEN by the ruling: this suite drives them and
audits them, and never edits them.

So the controls come in three shapes, and each shape answers a different way the
claim could be false.

  * DISPOSABLE ACCEPTANCE MANIFESTS. The consumer takes no reference, no digest
    and no tag; the only thing a caller can vary is which acceptance manifest it
    reads. Every control that attacks the identity therefore hands the REAL,
    UNMODIFIED consumer a disposable copy of the acceptance manifest with one
    field changed, and requires the refusal the consumer already throws, quoted
    verbatim from the frozen script rather than invented here.

  * A SOURCE AND WORKFLOW AUDIT. The absence of a `-Reference` parameter cannot
    be observed behaviourally: a test can only fail to pass an option, which is
    indistinguishable from the option existing and being ignored. Those
    properties are asserted against the text of the frozen consumer and of the
    new workflow, over their EXECUTABLE part only, and every such audit is
    proven live by applying the mutation it is supposed to refuse to a
    disposable copy.

  * A RAW-BYTE PIN. The named rules can only refuse the shapes someone thought
    of. The pins over the new workflow and over the two frozen inputs refuse
    every other edit, including edits this slice is not authorised to make at
    all.

Four controls (F06-F09) reach the network, and they are the only ones that do.
They fetch the accepted manifest -- 1851 bytes, no blob -- from ghcr.io
anonymously, with `DOCKER_CONFIG` pointed at an empty directory so no credential
the host happens to carry can be used, and require the frozen consumer to refuse
the registry's honest answer because the committed identity was mutated. They
need the pinned ORAS client:

    ci/windows/build-inputs/install-oras.sh linux-amd64 /tmp/w2a1/bin

    python3 ci/windows/w2/ffmpeg-consume-controls.py --oras /tmp/w2a1/bin/oras
    python3 ci/windows/w2/ffmpeg-consume-controls.py --only F10

Nothing here builds a ZIP, starts a server, writes a package or modifies the
repository. The RESTORE row asserts the last of those rather than assuming it.
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

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))

# The frozen production path. W2-A1 authors neither of these files.
CONSUMER = os.path.join(REPO_ROOT, "ci", "windows", "runtime-retention", "consume.ps1")
ACCEPTED_JSON = os.path.join(REPO_ROOT, "ci", "windows", "runtime-retention",
                             "accepted-runtime.json")
# The one file W2-A1 adds to the production path.
WORKFLOW = os.path.join(REPO_ROOT, ".github", "workflows", "w2-windows-ffmpeg-consume.yml")

# ---------------------------------------------------------------------------
# The frozen identity. These are the ruling's values, written here so that F12
# can require the acceptance manifest and the workflow to agree with them --
# three independent statements of one identity, rather than one statement read
# twice.
# ---------------------------------------------------------------------------
CANONICAL_PACKAGE = "ghcr.io/tesserafin-project/windows-ffmpeg-runtime"
ACCEPTED_MANIFEST_DIGEST = \
    "sha256:99e45f154a5d72aba4185eb19b6671aa1a11c30be837deac9dd26f473593c0b9"
ACCEPTED_REFERENCE = CANONICAL_PACKAGE + "@" + ACCEPTED_MANIFEST_DIGEST
ACCEPTED_RUNTIME_SHA256 = \
    "f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e"
# Descriptive only, and never a trust boundary. F11 requires it to be absent
# from the production path entirely.
DESCRIPTIVE_TAG = "accepted-83e23b957940"

# Raw-byte pins. `.gitattributes` normalises this repository to LF, so a Windows
# checkout delivers the same bytes a Linux one does and these are safe to assert
# literally. A pin over a text-mode read would have been more forgiving and less
# true: a CRLF copy hashes identically under universal newlines, so a file
# differing only in line endings would be called byte-identical. It is reported
# as exactly that instead.
CONSUMER_SHA256 = "f19fefcc48de9ae2175aa49ecff6e732762219a3d76c38067ba4114a1924646d"
ACCEPTED_JSON_SHA256 = "593c21f59c67dd564fa488f660efc14b74b5c5bcd775bbc3ef0bdf9e94dd9ece"
WORKFLOW_SHA256 = "82ce2aa5c2a4b2b832a49cfe2ca8390be4feb663b12bcaf0c7aaea43040df4e6"

# The new workflow's whole authorised shape, asserted by name rather than
# inferred from the pin.
WORKFLOW_ALLOWED_TRIGGERS = ("pull_request",)
WORKFLOW_ALLOWED_JOBS = ("ffmpeg",)

# The frozen consumer's entire parameter surface for a real consumption. The
# production path must pass exactly these and nothing else: every additional
# argument is a caller-supplied input to an identity that is supposed to travel
# with the commit.
CONSUMER_ARGUMENTS = ("-AcceptedManifest", "-WorkDir", "-OutDir", "-OrasPath")

# Parameter names whose presence in the consumer's top-level param block would
# make the identity caller-controlled. `-AcceptedManifest` names a file in the
# repository being built, which is why it is not on this list.
FORBIDDEN_PARAMETERS = ("$Reference", "$Digest", "$Tag", "$RunId", "$Package",
                        "$Registry", "$Image", "$Url", "$Uri")

# Every control this suite is required to report. A control that is deleted,
# renamed or silently skipped stops appearing here, and `main` turns that into a
# RED rather than into a smaller green suite. Without this, removing an
# inconvenient control would IMPROVE the summary line.
ROSTER = {
    "F01": "a tag reference in the acceptance manifest",
    "F02": "a short digest in the acceptance manifest",
    "F03": "an uppercase digest in the acceptance manifest",
    "F04": "a reference carrying both a tag and a digest",
    "F05": "a reference naming a package other than the authorised one",
    "F05b": "the ORAS sentinel is live and records an invocation",
    "F06": "a mutated manifest digest",
    "F07": "a mutated manifest size",
    "F08": "a mutated config descriptor digest",
    "F09": "a mutated layer descriptor digest",
    "F10": "no caller-supplied identity anywhere on the production path",
    "F11": "the descriptive tag is never the trust boundary",
    "F12": "the ruling, the acceptance manifest and the workflow agree",
    "F13": "no container runtime on the production path",
    "F14": "no Actions artifact or cache handover",
    "F15": "the new workflow is the authorised one, byte for byte",
    "F16": "the frozen consumer and acceptance manifest are unmodified",
    "F17": "the pull carries no credential",
    "F18": "W2 adds no FFmpeg consumer of its own",
    "F19": "the workflow requires two independent consumptions",
}

# The grammar oracle's own parameter set, used by F01-F05 as a second witness.
# It reaches no registry, opens no manifest and returns before ORAS is ever
# consulted, which is why F05b has to prove the sentinel separately.
GRAMMAR_REASONS = {
    "tag": "tag-only",
    "short": "malformed-digest",
    "upper": "malformed-digest",
    "both": "tag-and-digest",
    "foreign": "not-canonical-package",
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
                # result -- fall back to the path as given rather than lose the
                # finding.
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
# Driving the FROZEN consumer
# ===========================================================================

ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")


def normalise(raw):
    """The consumer's own words, on one line.

    PowerShell's default error view wraps a long message across a gutter of
    `|` continuations and colours it. Neither is part of what the consumer said,
    and a substring search that sees them would fail on message length rather
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
    raise RuntimeError("no PowerShell on PATH; W2-A1's controls cannot run")


DRIVER = """\
param([Parameter(Mandatory = $true)][string] $CallSpec)
$ErrorActionPreference = 'Stop'
$spec = Get-Content -LiteralPath $CallSpec -Raw | ConvertFrom-Json
$parameters = @{}
foreach ($property in $spec.parameters.PSObject.Properties) {
    $parameters[$property.Name] = $property.Value
}
foreach ($name in @($spec.switches)) { $parameters[$name] = $true }
try {
    & $spec.script @parameters
    if ($null -eq $LASTEXITCODE) { exit 0 }
    exit $LASTEXITCODE
} catch {
    [Console]::Error.WriteLine('W2A1-CAUGHT: ' + $_.Exception.Message)
    exit 1
}
"""

# A client that cannot serve anything. It exists to prove that the reference
# controls are refused BEFORE the registry is consulted: they assert this file
# was never executed. It is not a stand-in for ORAS and never returns bytes --
# substituting a fixture-serving client for the pinned one would be a registry
# override wearing a different name, which is the defect the frozen consumer
# refuses by construction.
SENTINEL = """\
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Ignored)
Add-Content -LiteralPath $env:W2A1_SENTINEL_MARKER -Value 'invoked'
[Console]::Error.WriteLine('W2A1 sentinel: this client serves nothing')
exit 9
"""


class Harness:
    """Generated scaffolding. It changes no behaviour of what it drives."""

    def __init__(self, work, oras=None):
        self.work = work
        self.oras = oras
        self.driver = os.path.join(work, "driver.ps1")
        self.sentinel = os.path.join(work, "sentinel-oras.ps1")
        with open(self.driver, "w", encoding="utf-8") as handle:
            handle.write(DRIVER)
        with open(self.sentinel, "w", encoding="utf-8") as handle:
            handle.write(SENTINEL)
        self.accepted = json.loads(read_text(ACCEPTED_JSON))

    def manifest(self, name, **overrides):
        """A disposable copy of the committed acceptance manifest."""
        document = json.loads(json.dumps(self.accepted))
        document.update(overrides)
        path = os.path.join(self.work, "accepted-%s.json" % name)
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(document, handle, indent=2, sort_keys=True)
        return path

    def call(self, name, parameters, switches=(), marker=None):
        spec = {
            "script": CONSUMER,
            "parameters": parameters,
            "switches": list(switches),
        }
        spec_path = os.path.join(self.work, "call-%s.json" % name)
        with open(spec_path, "w", encoding="utf-8") as handle:
            json.dump(spec, handle)
        environment = dict(os.environ)
        environment["NO_COLOR"] = "1"
        environment["W2A1_SENTINEL_MARKER"] = marker or os.path.join(
            self.work, "sentinel-%s.log" % name)
        # No credential is available to the consumer. A successful fetch under
        # this environment is therefore an anonymous fetch, measured rather
        # than asserted.
        empty = os.path.join(self.work, "no-credentials")
        os.makedirs(empty, exist_ok=True)
        environment["DOCKER_CONFIG"] = empty
        completed = subprocess.run(
            [powershell(), "-NoProfile", "-NonInteractive", "-File", self.driver, spec_path],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=environment)
        completed.text = normalise(completed.stdout + completed.stderr)
        return completed

    def consume(self, name, manifest, oras=None):
        """A real consumption attempt, with the four authorised arguments."""
        outdir = os.path.join(self.work, "out-%s" % name)
        marker = os.path.join(self.work, "sentinel-%s.log" % name)
        completed = self.call(name, {
            "AcceptedManifest": manifest,
            "WorkDir": os.path.join(self.work, "work-%s" % name),
            "OutDir": outdir,
            "OrasPath": oras if oras is not None else self.sentinel,
        }, marker=marker)
        completed.outdir = outdir
        completed.marker = marker
        return completed


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


def expect_refusal(report, name, completed, needle, description,
                   forbid_output=True, sentinel_untouched=False):
    """A negative control: non-zero exit, the consumer's OWN named refusal, and
    no accepted output anywhere."""
    if completed.returncode == 0:
        report.record(name, "RED", "%s: the consumer accepted it (exit 0)" % description)
        return False
    if needle not in completed.text:
        report.record(name, "RED", "%s: refused without saying %r -- %s"
                      % (description, needle, completed.text.strip()[-220:]))
        return False
    outdir = getattr(completed, "outdir", None)
    if forbid_output and outdir is not None and os.path.exists(outdir):
        report.record(name, "RED", "%s: refused but exposed %s" % (description, outdir))
        return False
    if sentinel_untouched:
        marker = getattr(completed, "marker", None)
        if marker is not None and os.path.exists(marker):
            report.record(name, "RED",
                          "%s: refused only after consulting the registry" % description)
            return False
    report.record(name, "PASS", "%s -> %s" % (description, needle))
    return True


# ===========================================================================
# Auditing the production path
# ===========================================================================

def consumer_param_block(text):
    """The consumer's TOP-LEVEL param block only.

    `Assert-DigestReference` takes a `$Reference` parameter of its own, and
    matching that would report the consumer as caller-controlled because one of
    its internal functions names a variable -- a false positive that would make
    this audit permanently red for the wrong reason.
    """
    if "\nparam(" not in text:
        return None
    return text.split("\nparam(", 1)[1].split("\n)", 1)[0]


def consumer_findings(text):
    findings = []
    block = consumer_param_block(text)
    if block is None:
        return ["has no top-level param block this audit can read"]
    for forbidden in FORBIDDEN_PARAMETERS:
        if forbidden in block:
            findings.append("accepts a caller-supplied %s" % forbidden)
    if "$AcceptedManifest" not in block:
        findings.append("no longer reads a committed acceptance manifest")
    if "$env:" in block:
        findings.append("takes a default from the environment")
    return findings


def consumer_invocations(text):
    """Every INVOCATION of the frozen consumer in a workflow's executable text.

    An invocation, not a mention: the `paths:` filter names the same file and is
    not a call, so the marker is the `./`-rooted command form. PowerShell
    continues a command with a trailing backtick, so a call spans several lines
    and reading one line would see one argument.

    Returns (arguments, values, joined) per call. A value runs to the next
    argument name rather than to the next space, because every real value here
    is a parenthesised `Join-Path` expression that contains spaces.
    """
    marker = "./ci/windows/runtime-retention/consume.ps1"
    lines = text.splitlines()
    calls = []
    index = 0
    while index < len(lines):
        if marker not in lines[index]:
            index += 1
            continue
        collected = [lines[index]]
        while collected[-1].rstrip().endswith("`") and index + 1 < len(lines):
            index += 1
            collected.append(lines[index])
        joined = " ".join(part.strip().rstrip("`").strip() for part in collected)
        pairs = re.findall(
            r"(?<![\w-])(-[A-Za-z][A-Za-z0-9]*)\s+(.*?)(?=\s+-[A-Za-z][A-Za-z0-9]*\s|$)",
            joined)
        arguments = [name for name, _ in pairs]
        values = {name: value.strip() for name, value in pairs}
        calls.append((arguments, values, joined))
        index += 1
    return calls


def workflow_reference_findings(text):
    """The workflow must pass exactly the four authorised arguments."""
    findings = []
    calls = consumer_invocations(text)
    if not calls:
        return ["invokes the frozen consumer nowhere this audit can read"]
    for arguments, values, _ in calls:
        extra = [argument for argument in arguments if argument not in CONSUMER_ARGUMENTS]
        if extra:
            findings.append("passes %s to the frozen consumer" % ", ".join(sorted(set(extra))))
        missing = [argument for argument in CONSUMER_ARGUMENTS if argument not in arguments]
        if missing:
            findings.append("omits %s" % ", ".join(missing))
        manifest = values.get("-AcceptedManifest", "")
        if not manifest.endswith("ci/windows/runtime-retention/accepted-runtime.json"):
            findings.append("reads its identity from %r rather than the committed manifest"
                            % manifest)
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


def audit_workflow(text):
    """Every way the new workflow could stop being what the ruling authorised.

    Runs over the executable part only. A permission grant, a trigger and a job
    are things a workflow DOES; a comment naming one is a comment.
    """
    code = strip_commentary("workflow.yml", text)
    findings = []

    forbidden = {
        r"write-all": "grants write-all",
        r"packages:\s*\w": "declares a packages: grant",
        r"contents:\s*write": "declares contents: write",
        r"id-token:\s*write": "declares id-token: write",
        r"^[ \t]*[a-z-]+:[ \t]*write[ \t]*$": "declares a write permission",
        r"^\s*pull_request_target:": "declares pull_request_target",
        r"^\s*workflow_dispatch:": "declares workflow_dispatch",
        r"^\s*workflow_call:": "declares workflow_call",
        r"^\s*schedule:": "declares schedule",
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

    findings.extend(workflow_reference_findings(code))
    return findings


def v1_permission_mutation(text):
    """The exact shape W2-A0-V1 walked past that slice's workflow audit.

    Everything the rest of the audit reads is carried over verbatim -- the
    pinned checkout SHA, `persist-credentials: false`, `windows-latest`, the
    consumer invocations -- so the only reason this file must be refused is the
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


# A workflow with the frozen file's structural shape and nothing else. Once the
# V1 mutation has been applied to the real file, that file no longer HAS an
# `on:` block or a workflow-scope `permissions:` block to rewrite, so a proof
# that could only mutate the live text would degrade to INERT at exactly the
# moment it is supposed to report RED. This baseline audits clean, which the
# control asserts, so every finding the proof requires comes from the mutation.
FROZEN_SHAPE_BASELINE = """\
name: baseline
on:
  pull_request:
    paths:
      - 'ci/windows/w2/**'

permissions:
  contents: read

jobs:
  ffmpeg:
    runs-on: windows-latest
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7.0.0
        with:
          persist-credentials: false
      - name: consume
        shell: pwsh
        run: |
          ./ci/windows/runtime-retention/consume.ps1 `
            -AcceptedManifest ci/windows/runtime-retention/accepted-runtime.json `
            -WorkDir (Join-Path $env:RUNNER_TEMP 'a/work') `
            -OutDir (Join-Path $env:RUNNER_TEMP 'a/runtime') `
            -OrasPath (Join-Path $env:RUNNER_TEMP 'bin\\oras.exe')
      - name: consume again
        shell: pwsh
        run: |
          ./ci/windows/runtime-retention/consume.ps1 `
            -AcceptedManifest ci/windows/runtime-retention/accepted-runtime.json `
            -WorkDir (Join-Path $env:RUNNER_TEMP 'b/work') `
            -OutDir (Join-Path $env:RUNNER_TEMP 'b/runtime') `
            -OrasPath (Join-Path $env:RUNNER_TEMP 'bin\\oras.exe')
"""


def w2_directory_findings(entries):
    """W2 may add controls and a workflow. It may not add a second consumer.

    Taken over a directory LISTING rather than the directory itself, so the
    inert-proof can hand it a planted name without writing into the checkout.
    """
    findings = []
    for name in sorted(entries):
        if not name.lower().endswith(".ps1"):
            continue
        if name == "consume-web-payload.ps1":
            continue  # W2-A0's accepted Web payload consumer.
        if name == "assemble-server-zip.ps1":
            continue  # W2-A2's ZIP assembler, allowed by name (W2-A2-F18).
        if name == "relocate-and-start.ps1":
            continue  # W2-A3's relocate-and-start proof, by name (W2-A3-F18).
        findings.append("ci/windows/w2/%s is a second consumer" % name)
    return findings


# ===========================================================================
# The controls
# ===========================================================================

def run_controls(work, report, oras, only=None):
    def selected(name):
        return only is None or name in only

    harness = Harness(work, oras)
    workflow_text = read_text(WORKFLOW) if os.path.isfile(WORKFLOW) else None
    consumer_text = read_text(CONSUMER) if os.path.isfile(CONSUMER) else None

    # --- F01..F05: the identity, attacked through the only thing a caller can
    #     vary. Each also asserts the registry was never consulted, because a
    #     consumer that resolves a tag and then rejects it has already told the
    #     registry which tag it wanted.
    references = [
        ("F01", "tag", CANONICAL_PACKAGE + ":" + DESCRIPTIVE_TAG,
         "REFERENCE-REJECTED:tag-only", "the descriptive tag as the identity"),
        ("F02", "short", CANONICAL_PACKAGE + "@sha256:dead",
         "REFERENCE-REJECTED:malformed-digest", "a short digest"),
        ("F03", "upper", CANONICAL_PACKAGE + "@" + ACCEPTED_MANIFEST_DIGEST.upper(),
         "REFERENCE-REJECTED:malformed-digest", "the accepted digest in uppercase"),
        ("F04", "both", CANONICAL_PACKAGE + ":" + DESCRIPTIVE_TAG + "@"
         + ACCEPTED_MANIFEST_DIGEST,
         "REFERENCE-REJECTED:tag-and-digest", "a tag and the accepted digest together"),
        ("F05", "foreign", "ghcr.io/attacker/windows-ffmpeg-runtime@"
         + ACCEPTED_MANIFEST_DIGEST,
         "REFERENCE-REJECTED:not-canonical-package", "another package at the accepted digest"),
    ]
    for name, label, reference, needle, description in references:
        if not selected(name):
            continue
        manifest = harness.manifest(label, reference=reference)
        completed = harness.consume(label, manifest)
        if not expect_refusal(report, name, completed, needle, description,
                              sentinel_untouched=True):
            continue
        # The grammar oracle must give the same answer as the production path.
        # Two witnesses for one property: the oracle can be read by a person,
        # the production path is what actually runs.
        oracle = harness.call("grammar-" + label, {"GrammarCheck": reference},
                              switches=["Canonical"])
        expected = "REFERENCE-REJECTED:" + GRAMMAR_REASONS[label]
        if oracle.returncode == 0 or expected not in oracle.text:
            report.rows[-1] = (name, "RED",
                               "%s: the production path refused it but the grammar oracle did not"
                               % description)
            print("  %-5s %-5s %s" % (name, "RED", report.rows[-1][2]), flush=True)

    # --- F05b: the sentinel that F01-F05 rely on is live ----------------------
    if selected("F05b"):
        # A well-formed reference to the authorised package at a digest that
        # cannot exist. The grammar accepts it, so the consumer DOES reach the
        # client -- which is the point: without this, "the sentinel was never
        # invoked" would also be true of a sentinel that can never be invoked.
        manifest = harness.manifest("unreachable",
                                    reference=CANONICAL_PACKAGE + "@sha256:" + "0" * 64)
        completed = harness.consume("unreachable", manifest)
        if completed.returncode == 0:
            report.record("F05b", "RED", "the consumer accepted an impossible digest")
        elif not os.path.exists(completed.marker):
            report.record("F05b", "INERT",
                          "the client sentinel records nothing, so F01-F05 prove nothing "
                          "about when the registry is consulted")
        elif "could not fetch the manifest" not in completed.text:
            report.record("F05b", "RED",
                          "the consumer did not stop at the fetch: %s"
                          % completed.text.strip()[-200:])
        else:
            report.record("F05b", "PASS",
                          "a syntactically valid reference does reach the client, and the "
                          "client sentinel records it")

    # --- F06..F09: the committed identity, mutated one field at a time --------
    #
    # These are the suite's only network-touching controls. Each fetches the
    # accepted 1851-byte manifest and no blob, anonymously, and requires the
    # frozen consumer to refuse the registry's honest answer.
    mutations = [
        ("F06", "manifest-digest",
         {"manifestDigest": ACCEPTED_MANIFEST_DIGEST[:-1] + "8"},
         "the registry returned manifest", "a mutated manifest digest"),
        ("F07", "manifest-size", {"manifestSize": 1850},
         "but the committed identity records 1850", "a mutated manifest size"),
        ("F08", "config-digest",
         {"configDigest": "sha256:" + "1" * 64},
         "names a config this consumer did not accept", "a mutated config digest"),
        ("F09", "layer-digest",
         {"layerDigest": "sha256:" + "2" * 64},
         "names a layer this consumer did not accept", "a mutated layer digest"),
    ]
    for name, label, overrides, needle, description in mutations:
        if not selected(name):
            continue
        if oras is None:
            report.record(name, "RED",
                          "no ORAS client: run ci/windows/build-inputs/install-oras.sh and "
                          "pass --oras; %s cannot be measured without one" % description)
            continue
        manifest = harness.manifest(label, **overrides)
        completed = harness.consume(label, manifest, oras=oras)
        expect_refusal(report, name, completed, needle, description)

    # --- F10: no caller-supplied identity anywhere on the production path -----
    if selected("F10"):
        findings = []
        if consumer_text is None:
            findings.append("the frozen consumer is missing")
        else:
            findings.extend("consume.ps1 " + finding
                            for finding in consumer_findings(consumer_text))
        if workflow_text is None:
            findings.append("the W2-A1 workflow is missing")
        else:
            findings.extend("the workflow " + finding for finding in
                            workflow_reference_findings(executable_text(WORKFLOW, workflow_text)))

        # Live-proof, both halves. A disposable copy of each file with the
        # mutation this control exists to refuse.
        proven = []
        if consumer_text is not None:
            block = consumer_param_block(consumer_text)
            if block is not None:
                mutant = consumer_text.replace(
                    "\nparam(" + block, "\nparam(\n    [string] $Reference," + block, 1)
                if consumer_findings(mutant):
                    proven.append("consumer")
        if workflow_text is not None:
            code = executable_text(WORKFLOW, workflow_text)
            mutant = code.replace("-OrasPath", "-Reference $env:PICK -OrasPath", 1)
            if workflow_reference_findings(mutant):
                proven.append("workflow")
            tag_mutant = code.replace(
                "-AcceptedManifest ci/windows/runtime-retention/accepted-runtime.json",
                "-AcceptedManifest $env:SOMEWHERE_ELSE", 1)
            if workflow_reference_findings(tag_mutant):
                proven.append("manifest path")
        if len(proven) < 3:
            report.record("F10", "INERT",
                          "the caller-supplied-identity audit does not refuse its own "
                          "mutation; it proved only %s" % (", ".join(proven) or "nothing"))
        elif findings:
            report.record("F10", "RED", "; ".join(findings))
        else:
            report.record("F10", "PASS",
                          "the frozen consumer offers no reference, digest, tag, run id, "
                          "package or registry parameter and takes no environment default, "
                          "and the workflow passes it exactly %s with the committed "
                          "acceptance manifest" % " ".join(CONSUMER_ARGUMENTS))

    # --- F11: the descriptive tag is never the trust boundary -----------------
    if selected("F11"):
        pattern = re.escape(DESCRIPTIVE_TAG) + r"|windows-ffmpeg-runtime:[A-Za-z0-9._-]"
        hits = scan_for([WORKFLOW, CONSUMER], pattern, code_only=True)
        planted = os.path.join(work, "planted-tag.yml")
        with open(planted, "w", encoding="utf-8") as handle:
            handle.write("          $reference = '%s:%s'\n"
                         % (CANONICAL_PACKAGE, DESCRIPTIVE_TAG))
        if not scan_for([planted], pattern, code_only=True):
            report.record("F11", "INERT", "the tag scanner does not detect a planted tag")
        elif hits:
            report.record("F11", "RED", "the production path names the tag: %s" % hits[:3])
        else:
            report.record("F11", "PASS",
                          "neither the workflow nor the frozen consumer names "
                          "'%s' or any other tag of the accepted package" % DESCRIPTIVE_TAG)

    # --- F12: three independent statements of one identity --------------------
    if selected("F12"):
        def disagreements(accepted, workflow_code):
            found = []
            if accepted.get("reference") != ACCEPTED_REFERENCE:
                found.append("the acceptance manifest names %r" % accepted.get("reference"))
            if accepted.get("manifestDigest") != ACCEPTED_MANIFEST_DIGEST:
                found.append("the acceptance manifest's manifestDigest is %r"
                             % accepted.get("manifestDigest"))
            if accepted.get("runtimeSha256") != ACCEPTED_RUNTIME_SHA256:
                found.append("the acceptance manifest pins runtime %r"
                             % accepted.get("runtimeSha256"))
            if accepted.get("platform") != "win-x64":
                found.append("the acceptance manifest is for %r" % accepted.get("platform"))
            for label, value in (("ACCEPTED_REFERENCE", ACCEPTED_REFERENCE),
                                 ("ACCEPTED_RUNTIME_SHA256", ACCEPTED_RUNTIME_SHA256)):
                declared = re.search(r"^\s*%s:\s*(\S+)\s*$" % label, workflow_code,
                                     re.MULTILINE)
                if declared is None:
                    found.append("the workflow declares no %s" % label)
                elif declared.group(1) != value:
                    found.append("the workflow declares %s = %s" % (label, declared.group(1)))
            return found

        code = executable_text(WORKFLOW, workflow_text) if workflow_text else ""
        findings = disagreements(harness.accepted, code)
        # Live-proof: each of the three statements, mutated in turn.
        mutated_manifest = dict(harness.accepted)
        mutated_manifest["runtimeSha256"] = "0" * 64
        mutated_code = code.replace(ACCEPTED_RUNTIME_SHA256, "9" * 64)
        if not disagreements(mutated_manifest, code) or not disagreements(harness.accepted,
                                                                         mutated_code):
            report.record("F12", "INERT", "the agreement check does not detect a mutation")
        elif findings:
            report.record("F12", "RED", "; ".join(findings))
        else:
            report.record("F12", "PASS",
                          "the ruling's reference and runtime digest, the committed "
                          "acceptance manifest and the workflow's env all state the same "
                          "identity")

    # --- F13: no container runtime on the production path ---------------------
    if selected("F13"):
        # `vnd.docker.*` is an OCI media type, not a dependency on a runtime,
        # and `DOCKER_CONFIG` is the registry client's credential-path variable,
        # which this workflow sets to an EMPTY directory. Neither is an
        # invocation, and `\bdocker\b` matches neither: there is no word
        # boundary before an underscore. A planted `docker pull` is matched, and
        # the control refuses to pass unless it is.
        pattern = r"(?<!vnd\.)\bdocker\b|containerd|podman|nerdctl|buildx|dockerd|docker-compose"
        targets = [path for path in (CONSUMER, WORKFLOW) if os.path.isfile(path)]
        hits = scan_for(targets, pattern, code_only=True)
        planted = os.path.join(work, "planted-runtime.ps1")
        with open(planted, "w", encoding="utf-8") as handle:
            handle.write("docker pull ghcr.io/example/image:latest\n")
        if not scan_for([planted], pattern, code_only=True):
            report.record("F13", "INERT",
                          "the container-runtime scanner does not detect a planted dependency")
        elif hits:
            report.record("F13", "RED", "a container-runtime dependency remains: %s" % hits[:3])
        elif len(targets) != 2:
            report.record("F13", "RED", "the production path is not both files")
        else:
            report.record("F13", "PASS",
                          "the acquisition path invokes no container executable, daemon "
                          "or engine")

    # --- F14: no Actions artifact or cache handover ---------------------------
    if selected("F14"):
        pattern = r"upload-artifact|download-artifact|actions/cache"
        hits = scan_for([WORKFLOW], pattern, code_only=True)
        planted = os.path.join(work, "planted-artifact.yml")
        with open(planted, "w", encoding="utf-8") as handle:
            handle.write("      - uses: actions/upload-artifact@v4\n")
        if not scan_for([planted], pattern, code_only=True):
            report.record("F14", "INERT", "the artifact scanner does not detect a planted upload")
        elif hits:
            report.record("F14", "RED", "an Actions artifact or cache step is present: %s"
                          % hits[:3])
        elif workflow_text is None:
            report.record("F14", "RED", "the W2-A1 workflow is missing entirely")
        else:
            report.record("F14", "PASS",
                          "no artifact upload/download and no cache can carry the runtime "
                          "between jobs")

    # --- F15: the new workflow is the authorised one, byte for byte -----------
    if selected("F15"):
        _workflow_control(report, work, workflow_text)

    # --- F16: the frozen inputs are unmodified --------------------------------
    if selected("F16"):
        findings = []
        for path, pinned in ((CONSUMER, CONSUMER_SHA256), (ACCEPTED_JSON, ACCEPTED_JSON_SHA256)):
            relative = os.path.relpath(path, REPO_ROOT)
            if not os.path.isfile(path):
                findings.append("%s is missing" % relative)
                continue
            with open(path, "rb") as handle:
                raw = handle.read()
            digest = hashlib.sha256(raw).hexdigest()
            if digest == pinned:
                continue
            if hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest() == pinned:
                findings.append("%s differs in its line endings only (%d bytes)"
                                % (relative, len(raw)))
            else:
                findings.append("%s is not the frozen file: %s, pinned %s"
                                % (relative, digest[:16], pinned[:16]))
        if findings:
            report.record("F16", "RED", "; ".join(findings))
        else:
            report.record("F16", "PASS",
                          "consume.ps1 and accepted-runtime.json are byte-identical to the "
                          "files W1 accepted; W2-A1 forked neither")

    # --- F17: the pull carries no credential ----------------------------------
    if selected("F17"):
        pattern = (r"secrets\.|GITHUB_TOKEN|GHCR_TOKEN|docker/login-action"
                   r"|oras\s+login|--password|--username|Authorization")
        hits = scan_for([WORKFLOW], pattern, code_only=True)
        planted = os.path.join(work, "planted-credential.yml")
        with open(planted, "w", encoding="utf-8") as handle:
            handle.write("          GHCR_TOKEN: ${{ secrets.GITHUB_TOKEN }}\n")
        if not scan_for([planted], pattern, code_only=True):
            report.record("F17", "INERT", "the credential scanner does not detect a planted token")
        elif hits:
            report.record("F17", "RED", "the workflow carries a credential: %s" % hits[:3])
        elif workflow_text is None:
            report.record("F17", "RED", "the W2-A1 workflow is missing entirely")
        elif "DOCKER_CONFIG" not in strip_commentary("w.yml", workflow_text):
            report.record("F17", "RED",
                          "the workflow does not point the client at an empty credential "
                          "store, so a credential the runner image carries could be used")
        else:
            report.record("F17", "PASS",
                          "no token, secret or registry login anywhere, and the client is "
                          "pointed at an empty DOCKER_CONFIG so the pull is anonymous by "
                          "construction")

    # --- F18: W2 adds no FFmpeg consumer of its own ---------------------------
    if selected("F18"):
        entries = sorted(os.listdir(HERE))
        findings = w2_directory_findings(entries)
        if not w2_directory_findings(entries + ["ffmpeg-consume.ps1"]):
            report.record("F18", "INERT", "the second-consumer check does not detect one")
        elif findings:
            report.record("F18", "RED", "; ".join(findings))
        else:
            report.record("F18", "PASS",
                          "ci/windows/w2 carries no FFmpeg consumer; the production path is "
                          "the frozen ci/windows/runtime-retention/consume.ps1")

    # --- F19: the workflow requires two independent consumptions --------------
    if selected("F19"):
        def consumption_findings(code):
            calls = consumer_invocations(code)
            found = []
            if len(calls) != 2:
                found.append("invokes the frozen consumer %d times, not twice" % len(calls))
            outdirs = [values.get("-OutDir") for _, values, _ in calls]
            workdirs = [values.get("-WorkDir") for _, values, _ in calls]
            if len(set(outdirs)) != len(outdirs):
                found.append("reuses an -OutDir between consumptions")
            if len(set(workdirs)) != len(workdirs):
                found.append("reuses a -WorkDir between consumptions")
            if not re.search(r"ACCEPTED_RUNTIME_SHA256", code):
                found.append("never compares a consumed archive to the accepted digest")
            return found

        code = executable_text(WORKFLOW, workflow_text) if workflow_text else ""
        findings = consumption_findings(code)
        # Live-proof: rename the second invocation on a disposable copy, so
        # the workflow still says everything else it says and only the second
        # consumption is gone.
        marker = "./ci/windows/runtime-retention/consume.ps1"
        head, sep, tail = code.partition(marker)
        single = head + sep + tail.replace(marker, "./ci/windows/w2/not-a-consumer.ps1", 1)
        if not consumption_findings(single):
            report.record("F19", "INERT",
                          "removing a consumption from a copy changes nothing this check sees")
        elif findings:
            report.record("F19", "RED", "; ".join(findings))
        else:
            report.record("F19", "PASS",
                          "two consumptions into two directories that have never held "
                          "anything, both compared to the accepted runtime digest")


def _workflow_control(report, work, workflow_text):
    if workflow_text is None:
        report.record("F15", "RED", "the W2-A1 workflow is missing")
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
            report.record("F15", "INERT", "the live-proof baseline does not audit clean: %s"
                          % audit_workflow(FROZEN_SHAPE_BASELINE))
            return
        mutant, applied = v1_permission_mutation(FROZEN_SHAPE_BASELINE)
        if applied != expected_edits:
            report.record("F15", "INERT",
                          "the V1 permission mutation no longer applies to the baseline: %s"
                          % applied)
            return
    mutant_findings = audit_workflow(mutant)
    required = {
        "grants write-all": "write-all",
        "triggers on 'push'": "the push trigger",
        "declares the unauthorised job 'exfiltrate'": "the second job",
        "does not request contents: read at any scope": "reads that exist only in a comment",
    }
    unproven = [description for finding, description in required.items()
                if finding not in mutant_findings]
    if unproven:
        report.record("F15", "INERT",
                      "the workflow audit does not refuse %s" % ", ".join(sorted(unproven)))
        return
    if hashlib.sha256(mutant.encode("utf-8")).hexdigest() == WORKFLOW_SHA256:
        report.record("F15", "INERT", "the content pin does not distinguish the mutation")
        return

    # --- live-proof 2: a planted violation of the other named rules -----------
    planted = os.path.join(work, "planted-workflow.yml")
    with open(planted, "w", encoding="utf-8") as handle:
        handle.write("on:\n  workflow_dispatch:\npermissions:\n  packages: write\n"
                     "jobs:\n  x:\n    steps:\n      - uses: actions/checkout@v4\n")
    planted_findings = audit_workflow(read_text(planted))
    for label in ("declares a packages: grant", "declares workflow_dispatch",
                  "unpinned action actions/checkout@v4"):
        if label not in planted_findings:
            report.record("F15", "INERT",
                          "the workflow audit does not detect a planted violation: %s" % label)
            return

    if findings:
        report.record("F15", "RED", "; ".join(findings))
    else:
        report.record("F15", "PASS",
                      "byte-identical to the pinned workflow, and its executable text "
                      "declares one on: block triggering pull_request only, one jobs: block "
                      "with the one authorised job, contents: read and no packages grant "
                      "at either scope, no write-all, pinned actions, no persisted "
                      "credentials, no dispatch, no mutable image reference, and it drives "
                      "the frozen consumer with the committed acceptance manifest")


# ===========================================================================

def _repository_fingerprint():
    fingerprint = {}
    for path in (CONSUMER, ACCEPTED_JSON, WORKFLOW, os.path.abspath(__file__)):
        if os.path.isfile(path):
            fingerprint[os.path.relpath(path, REPO_ROOT)] = sha256_file(path)
    return fingerprint


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--only", action="append", help="run only the named control(s)")
    parser.add_argument("--oras", help="the pinned ORAS client F06-F09 need")
    args = parser.parse_args(argv)

    oras = args.oras or shutil.which("oras") or shutil.which("oras.exe")
    if oras is not None and not os.path.isfile(oras):
        oras = None

    work = tempfile.mkdtemp(prefix="w2a1-controls-")
    try:
        before = _repository_fingerprint()
        print("W2-A1 hostile controls")
        report = Report()
        started = time.time()
        run_controls(work, report, oras, set(args.only) if args.only else None)
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
        print("W2-A1 controls: %d PASS, %d RED, %d INERT in %.1fs"
              % (totals["PASS"], totals["RED"], totals["INERT"], time.time() - started))
        return 0 if (totals["RED"] == 0 and totals["INERT"] == 0) else 1
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
