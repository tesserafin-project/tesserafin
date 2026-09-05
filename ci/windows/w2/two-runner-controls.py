#!/usr/bin/env python3
"""Hostile controls for W2's two-runner win-x64 ZIP identity (W2-A4, #256).

W2-A4's claim is W0 §8.1 read literally, and nothing else:

    two clean assemblies of `tesserafin-server_<version>_win-x64.zip` from the
    same commit, on two SEPARATE `windows-latest` allocations that share
    nothing, hash to the same 64 hex digits.

W2-A2 already proved two assemblies on ONE runner, which §8 clause 1 explicitly
does not accept ("Not two builds on one runner"). So everything here is about
the allocation boundary, the epoch both sides are given, and the one place a
two-job proof can quietly stop being one: the compare.

The controls come in two shapes.

  * OBSERVED REFUSALS OF THE REAL COMPARE, PLANT FIRST. The comparison the
    hosted `compare` job runs is `--compare` in THIS file, not a shell snippet
    in the workflow, so it can be driven here as a subprocess against planted
    hash files -- missing, empty, truncated, over-long, uppercase, CRLF,
    BOM-prefixed, non-hex, unterminated and simply different. Every one must
    exit non-zero and say why, and the one well-formed agreeing pair must exit
    zero. A compare that "PASSes on a missing hash" is the single failure that
    would make this whole slice a rubber stamp, so it is measured rather than
    read.

  * A WORKFLOW AUDIT WITH A LIVE MUTATION FOR EVERY NAMED RULE, PLUS A RAW-BYTE
    PIN. `needs:` between the two assemble jobs, an `actions/cache`, an upload
    of the archive itself, a compare that can be skipped, an epoch taken from
    the clock -- none of these can be observed by running anything, because a
    test can only fail to do a thing, which is indistinguishable from the thing
    being possible and unused. Each is asserted against the executable text of
    the workflow, and each assertion is proved load-bearing by MUTATING a copy
    of that text and requiring the audit to name the mutation. A rule whose
    mutation no longer applies reports INERT rather than a smaller green suite.

Nothing here reaches a registry, builds a server, packs an archive, starts one
or modifies the repository. The RESTORE row asserts the last of those rather
than assuming it.

    python3 ci/windows/w2/two-runner-controls.py
    python3 ci/windows/w2/two-runner-controls.py --only T07
    python3 ci/windows/w2/two-runner-controls.py --compare a/sha256.txt b/sha256.txt
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

# The one workflow W2-A4 adds. It is the only file this slice authors that runs
# on a hosted runner.
WORKFLOW = os.path.join(REPO_ROOT, ".github", "workflows",
                        "w2-windows-zip-two-runner.yml")
DOC = os.path.join(REPO_ROOT, "docs", "distribution", "W2-A4-two-runner.md")

# The frozen production path. W2-A4 authors none of it and edits none of it.
ASSEMBLER = os.path.join(HERE, "assemble-server-zip.ps1")
ACCEPTED_JSON = os.path.join(REPO_ROOT, "ci", "windows", "runtime-retention",
                             "accepted-runtime.json")

# ---------------------------------------------------------------------------
# The frozen identities, from the ruling. T14 requires the assembler, the
# committed acceptance manifest and the new workflow to agree with them --
# three independent statements of one identity, rather than one statement read
# three times.
# ---------------------------------------------------------------------------
WEB_PAYLOAD_SHA256 = "4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f"
ACCEPTED_RUNTIME_SHA256 = "f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e"

# Raw-byte pins. `.gitattributes` normalises this repository to LF, so a Windows
# checkout delivers the same bytes a Linux one does and these are safe to assert
# literally. A pin over a text-mode read would have been more forgiving and less
# true: a CRLF copy hashes identically under universal newlines, so a file
# differing only in line endings would be called byte-identical. T15 reports it
# as exactly that instead.
#
# Every previously accepted W2 file is here, not only the ones this workflow
# runs. The ruling forbids editing any of them, and a pin is the only thing that
# can say so about a file this slice never touches.
FROZEN_PINS = {
    "ci/windows/w2/assemble-server-zip.ps1":
        "b2dec792d71284299602403504d6b059f45acc06aa3e53d8989716bdfc49fcc6",
    "ci/windows/w2/consume-web-payload.ps1":
        "db49f21001067a8f55ae71432ff9d47830daa454704a09800bb0e1eadf3b117c",
    "ci/windows/w2/relocate-and-start.ps1":
        "637095a09ae2e845f5359bbe57e960727cb30bf7d198efc731ba07463cae6b94",
    "ci/windows/w2/pkg-tree-digest.py":
        "0c70114c69e85d06bc3d95249cc1a86f917eb2b8deb44718cc05ad6f3afa70b4",
    "ci/windows/w2/zip-controls.py":
        "1cdd22612db0ae34b2234c73e57aa6b345fec931266fd94868a0bb37a94353c2",
    "ci/windows/w2/start-controls.py":
        "585b4e740560eec6fe20ba58081eb4c1aecba8979d211d8087c1c6e66019d1c8",
    "ci/windows/w2/web-payload-controls.py":
        "60466ae4da90d9ed876e709c29c90fef025dc287ad8ffbaf5d64d1f053b6e9ea",
    "ci/windows/w2/ffmpeg-consume-controls.py":
        "b00f03836acf765155658e24e381bd9fe65afe22f700f2bd887f157ad78bafca",
    "ci/windows/runtime-retention/consume.ps1":
        "f19fefcc48de9ae2175aa49ecff6e732762219a3d76c38067ba4114a1924646d",
    "ci/windows/runtime-retention/accepted-runtime.json":
        "593c21f59c67dd564fa488f660efc14b74b5c5bcd775bbc3ef0bdf9e94dd9ece",
}

# Filled in by the pinning pass below; see T01.
WORKFLOW_SHA256 = "b954ec898ff8b2c4a90caa220e09e9ee172baa3865b2df16ed5d69bdac768b32"

# The .ps1 files `ci/windows/w2/` is allowed to carry. W2-A4 adds none: the
# ruling says "no new .ps1 under ci/windows/w2/" and T16 is what says so about
# the directory rather than about the diff.
ALLOWED_PS1 = ("assemble-server-zip.ps1", "consume-web-payload.ps1",
               "relocate-and-start.ps1")

WORKFLOW_ALLOWED_TRIGGERS = ("pull_request",)
WORKFLOW_ALLOWED_JOBS = ("controls", "assemble-a", "assemble-b", "compare")

# The frozen assembler's production parameter surface. `-StageRoot` is the
# pack-only oracle W2-A2's controls drive and is deliberately not here; T11
# asserts this workflow never passes it.
ASSEMBLER_ARGUMENTS = ("-RepoRoot", "-WorkDir", "-OutDir", "-SourceDateEpoch",
                       "-OrasPath", "-PythonPath")

# Every control this suite is required to report. A control that is deleted,
# renamed or silently skipped stops appearing here, and `main` turns that into a
# RED rather than into a smaller green suite. Without this, removing an
# inconvenient control would IMPROVE the summary line.
ROSTER = {
    "T01": "the new workflow is the authorised one, byte for byte",
    "T02": "the workflow audit refuses the V1 mutation, by name",
    "T03": "two assemble jobs on two allocations, neither needing the other",
    "T04": "no Actions cache, in any spelling",
    "T05": "no archive crosses a job boundary; only 65 bytes of hex do",
    "T06": "the compare cannot be skipped and cannot conclude without both allocations",
    "T07": "the compare refuses a missing hash file",
    "T08": "the compare refuses a malformed hash file",
    "T09": "the compare refuses two different hashes and accepts one agreeing pair",
    "T10": "both allocations derive SOURCE_DATE_EPOCH from the head commit, never the clock",
    "T11": "each allocation assembles exactly once, from directories asserted absent",
    "T12": "nothing is started, relocated or registered as a service",
    "T13": "no write grant at any scope, and no dispatch, push, schedule or target trigger",
    "T14": "the ruling, the assembler, the acceptance manifest and the workflow agree",
    "T15": "every previously accepted W2 file is unmodified",
    "T16": "W2-A4 adds no .ps1 to ci/windows/w2",
    "T17": "the workflow watches the whole ci/windows/w2 tree (W2-A1-V1 NB-1)",
    "T18": "actions are SHA-pinned, credentials are not persisted, no container engine",
    "T19": "the controls run on a runner that assembles nothing",
    "T20": "the doc states the non-goals and claims no acceptance",
}


# ===========================================================================
# The comparison the hosted compare job actually runs
# ===========================================================================
#
# This lives here rather than in the workflow for one reason: a comparison
# written as a shell snippet inside a YAML step can only ever be AUDITED, and an
# audit of a snippet cannot tell "refuses a missing file" from "happens not to
# have been given one". As a function it can be driven against planted inputs,
# which is what T07, T08 and T09 do.

HASH_FILE_BYTES = 65
LOWER_HEX = frozenset(b"0123456789abcdef")


def read_hash_file(path, label):
    """The 64 lowercase hex digits in a well-formed evidence file.

    Returns `(hash, None)` or `(None, finding)`. Read as BYTES: a text-mode read
    would translate CRLF to LF and silently accept a file this refuses, and a
    universal-newline read of a 66-byte CRLF file would report 65 characters.
    """
    if not os.path.isfile(path):
        return None, "%s: there is no hash file at %s" % (label, path)
    with open(path, "rb") as handle:
        raw = handle.read()
    if len(raw) != HASH_FILE_BYTES:
        return None, ("%s: the hash file is %d bytes, not %d (64 lowercase hex digits "
                      "and one LF)" % (label, len(raw), HASH_FILE_BYTES))
    if raw[HASH_FILE_BYTES - 1] != 0x0A:
        return None, "%s: the hash file does not end in exactly one LF" % label
    body = raw[:HASH_FILE_BYTES - 1]
    bad = [index for index, value in enumerate(body) if value not in LOWER_HEX]
    if bad:
        return None, ("%s: the hash file is not 64 lowercase hex digits (byte %d is %r)"
                      % (label, bad[0], bytes(body[bad[0]:bad[0] + 1])))
    return body.decode("ascii"), None


def compare_hash_files(path_a, path_b, label_a, label_b):
    """`(agreed_hash, findings)`. A hash is returned ONLY when both files are
    well formed and equal; every other outcome returns findings and no hash."""
    findings = []
    left, finding = read_hash_file(path_a, label_a)
    if finding:
        findings.append(finding)
    right, finding = read_hash_file(path_b, label_b)
    if finding:
        findings.append(finding)
    if findings:
        return None, findings
    if left != right:
        return None, ["the two allocations disagree: %s produced %s, %s produced %s"
                      % (label_a, left, label_b, right)]
    return left, []


def run_compare(args):
    agreed, findings = compare_hash_files(args.compare[0], args.compare[1],
                                          args.label_a, args.label_b)
    if findings:
        print("W2-A4 COMPARE: NO IDENTITY")
        for finding in findings:
            print("  %s" % finding)
        return 1
    print("W2-A4 COMPARE: IDENTICAL %s" % agreed)
    print("  %s and %s produced the same win-x64 archive bytes on two separate "
          "runner allocations." % (args.label_a, args.label_b))
    return 0


# ===========================================================================
# Reading a file for what it DOES
# ===========================================================================

def strip_commentary(path, text):
    """The executable part of a file, with comments and docstrings blanked out.

    An audit that cannot tell a declaration from a comment explaining why there
    is no declaration has two useless outcomes: it fires on the explanation, or
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


def workflow_code(text):
    return strip_commentary("workflow.yml", text)


def sha256_file(path):
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


# ===========================================================================
# Reading the workflow's structure from raw text
# ===========================================================================
#
# Deliberately not `yaml.safe_load`. A loader resolves duplicate keys last-wins,
# so a file carrying two `on:` blocks, or one job declared twice, would be
# audited on whichever copy the loader kept rather than on everything the file
# says. Reading the first block and stopping is the same defect mirrored, so
# every block is read and the count is reported.

def workflow_triggers(code):
    lines = code.splitlines()
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


def workflow_jobs(code):
    """`(ordered names, {name: body})`, from raw text, for the reason above.

    A name that appears twice appears twice in the list, so a duplicated job is
    a finding rather than a silent overwrite.
    """
    order = []
    bodies = {}
    inside = False
    current = None
    blocks = 0
    for line in code.splitlines():
        if re.match(r"^jobs:\s*$", line):
            blocks += 1
            inside = True
            current = None
            continue
        if not inside:
            continue
        if line.strip() and not line.startswith(" "):
            inside = False
            current = None
            continue
        nested = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if nested:
            current = nested.group(1)
            order.append(current)
            bodies.setdefault(current, [])
            continue
        if current is not None:
            bodies[current].append(line)
    return order, {name: "\n".join(body) for name, body in bodies.items()}, blocks


def job_steps(body):
    """Each step of a job as its own chunk, so a `uses:` can be related to the
    `with:` that configures it. A scan over a whole job cannot do that: it would
    read one step's `path:` as if it belonged to another step's action."""
    steps = []
    current = None
    for line in body.splitlines():
        if re.match(r"^      - ", line):
            if current is not None:
                steps.append("\n".join(current))
            current = [line]
        elif current is not None:
            current.append(line)
    if current is not None:
        steps.append("\n".join(current))
    return steps


def job_needs(body):
    match = re.search(r"^    needs:\s*(.+)$", body, re.MULTILINE)
    if not match:
        return []
    return re.findall(r"[A-Za-z0-9_-]+", match.group(1))


def job_runs_on(body):
    match = re.search(r"^    runs-on:\s*(\S+)", body, re.MULTILINE)
    return match.group(1) if match else None


def assembling_jobs(bodies):
    """The jobs that invoke the frozen assembler, found by what they DO rather
    than by a hard-coded name -- renaming a job must not be able to remove it
    from this audit."""
    return {name: body for name, body in bodies.items()
            if "assemble-server-zip.ps1" in body}


# ===========================================================================
# The audit, one function per named rule
# ===========================================================================

def allocation_findings(code):
    """§8.1: two assemble jobs, two allocations, chained to nothing."""
    _, bodies, _ = workflow_jobs(code)
    assemble = assembling_jobs(bodies)
    findings = []
    if len(assemble) != 2:
        findings.append("invokes the frozen assembler in %d jobs; two SEPARATE runner "
                        "allocations are the whole evidence of this slice" % len(assemble))
    for name, body in sorted(assemble.items()):
        runner = job_runs_on(body)
        if runner != "windows-latest":
            findings.append("the assemble job '%s' runs on %s, not windows-latest"
                            % (name, runner))
        calls = len(re.findall(r"assemble-server-zip\.ps1", body))
        if calls != 1:
            findings.append("the assemble job '%s' invokes the assembler %d times, not once"
                            % (name, calls))
        for needed in job_needs(body):
            if needed in assemble:
                findings.append("the assemble job '%s' needs: '%s'; a chained pair is one "
                                "allocation followed by another, not two" % (name, needed))
    work = set(re.findall(r"-WorkDir \(Join-Path \$env:RUNNER_TEMP '([^']+)'\)", code))
    out = set(re.findall(r"-OutDir \(Join-Path \$env:RUNNER_TEMP '([^']+)'\)", code))
    if len(assemble) == 2:
        if len(work) < 2:
            findings.append("the two allocations are given the same work directory")
        if len(out) < 2:
            findings.append("the two allocations are given the same output directory")
    return findings


def cache_findings(code):
    """§8.2 has no cache in it. `actions/cache` is the obvious spelling; a
    `cache:` input on `setup-dotnet` or `setup-python` is the one that would get
    past a rule naming only the action."""
    findings = []
    if re.search(r"actions/cache", code):
        findings.append("uses actions/cache")
    for match in re.finditer(r"^\s*cache(-dependency-path)?:\s*\S", code, re.MULTILINE):
        findings.append("declares a %s: input" % match.group(0).strip().split(":")[0])
    if re.search(r"^\s*restore-keys:", code, re.MULTILINE):
        findings.append("declares restore-keys:")
    return findings


def artifact_findings(code):
    """§8.7: the archive never crosses a job boundary. Sixty-five bytes of hex
    do, and that is a statement ABOUT a production output rather than an input
    to producing one."""
    order, bodies, _ = workflow_jobs(code)
    assemble = assembling_jobs(bodies)
    findings = []
    uploads = 0
    for name, body in bodies.items():
        for step in job_steps(body):
            if "actions/download-artifact" in step and name in assemble:
                findings.append("the assemble job '%s' downloads an artifact" % name)
            if "actions/upload-artifact" not in step:
                continue
            uploads += 1
            paths = re.findall(r"^\s*path:\s*(.+?)\s*$", step, re.MULTILINE)
            if not paths:
                findings.append("an upload in '%s' names no path" % name)
            for path in paths:
                # `${{ runner.temp }}\\w2a4-a\\evidence\\sha256.txt` is the form the other
                # Windows workflows in this repository use, and `@actions/glob`
                # treats both separators alike, so the rule has to as well --
                # otherwise it would fire on the spelling rather than on the
                # thing being uploaded.
                normalised = path.replace("\\", "/")
                if not normalised.endswith("/sha256.txt"):
                    findings.append("the job '%s' uploads %s; only a sha256.txt may leave a "
                                    "runner" % (name, path))
                if ".zip" in path or "*" in path:
                    findings.append("the job '%s' uploads an archive or a glob: %s"
                                    % (name, path))
            if "if-no-files-found: error" not in step:
                findings.append("the upload in '%s' does not fail when the evidence file is "
                                "missing; the default is a warning and an empty artifact"
                                % name)
    for pattern, label in ((r"actions/upload-pages-artifact", "uploads a Pages artifact"),
                           (r"softprops/action-gh-release", "creates a release"),
                           (r"gh\s+release\s+upload", "uploads a release asset")):
        if re.search(pattern, code):
            findings.append(label)
    if uploads and uploads != len(assemble):
        findings.append("uploads %d artifacts from %d assemble jobs" % (uploads, len(assemble)))
    return findings


def compare_findings(code):
    """The one job whose absence, or whose skip, would turn this proof into two
    unrelated builds."""
    order, bodies, _ = workflow_jobs(code)
    assemble = assembling_jobs(bodies)
    findings = []
    candidates = {name: body for name, body in bodies.items()
                  if set(assemble) and set(assemble).issubset(set(job_needs(body)))}
    if not candidates:
        findings.append("no job needs both assemble jobs, so nothing compares the two "
                        "allocations")
        return findings
    if len(candidates) > 1:
        findings.append("declares %d compare jobs: %s" % (len(candidates), sorted(candidates)))
    for name, body in sorted(candidates.items()):
        if not re.search(r"^    if:\s*\$\{\{\s*always\(\)\s*\}\}\s*$", body, re.MULTILINE):
            findings.append("the compare job '%s' is not `if: ${{ always() }}`, so a failed "
                            "allocation SKIPS it and the run carries no verdict" % name)
        guard = [step for step in job_steps(body)
                 if re.search(r"needs\.[A-Za-z0-9_-]+\.result", step)]
        if not guard:
            findings.append("the compare job '%s' never reads needs.<job>.result, so it "
                            "cannot tell a successful pair from a missing one" % name)
        else:
            named = set()
            for step in guard:
                named.update(re.findall(r"needs\.([A-Za-z0-9_-]+)\.result", step))
                if "exit 1" not in step:
                    findings.append("the result guard in '%s' does not exit non-zero" % name)
            for job in sorted(assemble):
                if job not in named:
                    findings.append("the compare job '%s' never checks the result of '%s'"
                                    % (name, job))
        if "two-runner-controls.py --compare" not in re.sub(r"\s+", " ", body):
            findings.append("the compare job '%s' does not run the audited comparison" % name)
        downloads = sum(1 for step in job_steps(body) if "actions/download-artifact" in step)
        if downloads != len(assemble):
            findings.append("the compare job '%s' downloads %d evidence files for %d "
                            "allocations" % (name, downloads, len(assemble)))
    return findings


def epoch_findings(code):
    """Both allocations must be given the SAME epoch, and must derive it from a
    commit named by the event rather than from anything either of them observes
    locally. A clock, a tag or a run identifier would make two allocations
    differ for a reason that has nothing to do with the build."""
    _, bodies, _ = workflow_jobs(code)
    assemble = assembling_jobs(bodies)
    findings = []
    for name, body in sorted(assemble.items()):
        if "git log -1 --format=%ct" not in body:
            findings.append("the assemble job '%s' does not derive SOURCE_DATE_EPOCH from a "
                            "commit" % name)
        if "github.event.pull_request.head.sha" not in body:
            findings.append("the assemble job '%s' does not name the pull request head "
                            "commit" % name)
        if "git rev-parse HEAD" not in body:
            findings.append("the assemble job '%s' does not check that it actually checked "
                            "out that commit" % name)
        # Any of these anywhere in an allocation's job is a finding. A rule
        # that only fired when the spelling sat on the same line as the
        # assignment would be walked past by one extra variable, and there is
        # no legitimate reason for an assemble job to name a clock, a run
        # identifier or a tag at all.
        for spelling in ("UtcNow", "Get-Date", "date +%s", "github.run_id",
                         "github.run_number", "github.ref_name"):
            if spelling in body:
                findings.append("the assemble job '%s' can take its epoch from %s"
                                % (name, spelling))
    epochs = set(re.findall(r"-SourceDateEpoch\s+(\$\{\{[^}]*\}\}|[^\s`]+)", code))
    if not epochs:
        findings.append("passes no -SourceDateEpoch")
    for epoch in epochs:
        if "steps.epoch.outputs.epoch" not in epoch:
            findings.append("takes its epoch from %r rather than the derived commit time"
                            % epoch)
    return findings


def clean_tree_findings(code):
    """§8.2, asserted: each allocation must require its directories to be absent
    before the assembler is handed them."""
    _, bodies, _ = workflow_jobs(code)
    assemble = assembling_jobs(bodies)
    findings = []
    for name, body in sorted(assemble.items()):
        guards = re.findall(r"if \(Test-Path -LiteralPath \$(work|out)\)\s*\{\s*throw", body)
        if set(guards) != {"work", "out"}:
            findings.append("the assemble job '%s' does not require both an absent work tree "
                            "and an absent output tree" % name)
    if "-StageRoot" in code:
        findings.append("passes -StageRoot; the pack-only oracle is not the production door")
    for match in re.finditer(r"(-[A-Z][A-Za-z0-9]*)\s", code):
        argument = match.group(1)
        if argument in ("-Reference", "-Digest", "-Tag", "-RunId", "-Registry", "-Image",
                        "-Version", "-Package") and argument not in ASSEMBLER_ARGUMENTS:
            findings.append("the workflow passes %s" % argument)
    return findings


def scope_findings(code):
    """Nothing this workflow does is a start, a relocation or a registration."""
    findings = []
    forbidden = {
        r"relocate-and-start\.ps1": "runs the W2-A3 relocate-and-start proof",
        r"tesserafin\.exe": "starts the server",
        r"New-Service": "registers a service",
        r"\bsc\.exe\b": "registers a service with sc.exe",
        r"Start-Process": "starts a process",
        r"\.reefin": "touches a .reefin marker",
        r"\bdocker\b": "names docker",
        r"\bpodman\b": "names podman",
        r"\bnerdctl\b": "names nerdctl",
    }
    for pattern, label in forbidden.items():
        if re.search(pattern, code):
            findings.append(label)
    return findings


def permission_findings(code):
    """The write surface. A grant is a thing a workflow DOES; a comment naming
    one is a comment, which is why this reads executable text only."""
    findings = []
    forbidden = {
        r"write-all": "grants write-all",
        r"packages:\s*write": "declares packages: write",
        r"contents:\s*write": "declares contents: write",
        r"id-token:\s*write": "declares id-token: write",
        r"pull-requests:\s*write": "declares pull-requests: write",
        r"^[ \t]*[a-z-]+:[ \t]*write[ \t]*$": "declares a write permission",
        r"^\s*pull_request_target:": "declares pull_request_target",
        r"^\s*workflow_dispatch:": "declares workflow_dispatch",
        r"^\s*workflow_call:": "declares workflow_call",
        r"^\s*schedule:": "declares schedule",
        r"^\s*push:": "declares push",
    }
    for pattern, label in forbidden.items():
        if re.search(pattern, code, re.MULTILINE):
            findings.append(label)

    triggers, trigger_blocks = workflow_triggers(code)
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

    order, _, job_blocks = workflow_jobs(code)
    if not order:
        findings.append("declares no job this audit can read")
    if job_blocks > 1:
        findings.append("declares %d top-level jobs: blocks" % job_blocks)
    for job in order:
        if job not in WORKFLOW_ALLOWED_JOBS:
            findings.append("declares the unauthorised job '%s'" % job)
    for job in sorted(set(order)):
        if order.count(job) > 1:
            findings.append("declares the job '%s' %d times" % (job, order.count(job)))
    if "contents: read" not in code:
        findings.append("does not request contents: read at any scope")
    return findings


def supply_chain_findings(code):
    findings = []
    for match in re.finditer(r"uses:\s*([^\s#]+)", code):
        reference = match.group(1)
        if "@" not in reference or not re.search(r"@[0-9a-f]{40}$", reference):
            findings.append("unpinned action %s" % reference)
    for match in re.finditer(r"ghcr\.io/[A-Za-z0-9._/-]+", code):
        image = match.group(0)
        if "@sha256:" not in code[match.start():match.start() + len(image) + 80]:
            findings.append("mutable image reference %s" % image)
    checkouts = len(re.findall(r"uses:\s*actions/checkout@", code))
    persisted = len(re.findall(r"persist-credentials:\s*false", code))
    if checkouts and persisted < checkouts:
        findings.append("%d checkouts but only %d declare persist-credentials: false"
                        % (checkouts, persisted))
    if "windows-latest" not in code:
        findings.append("has no windows-latest job")
    return findings


def paths_findings(code):
    findings = []
    if "'ci/windows/w2/**'" not in code:
        findings.append("does not watch the whole ci/windows/w2 tree")
    for required in ("'ci/windows/runtime-retention/consume.ps1'",
                     "'ci/windows/runtime-retention/accepted-runtime.json'",
                     "'ci/windows/build-inputs/install-oras.sh'"):
        if required not in code:
            findings.append("does not watch %s, which it runs" % required)
    return findings


def controls_job_findings(code):
    """The suite must run somewhere, and not on a runner that also assembles:
    a control that only ever runs beside a two-hour Windows job is a control
    nobody sees fail."""
    _, bodies, _ = workflow_jobs(code)
    assemble = assembling_jobs(bodies)
    findings = []
    running = {name: body for name, body in bodies.items()
               if re.search(r"two-runner-controls\.py(?!\s*--compare)", body)}
    if not running:
        findings.append("nothing runs ci/windows/w2/two-runner-controls.py")
    for name, body in sorted(running.items()):
        if name in assemble:
            findings.append("the controls run inside the assemble job '%s'" % name)
        if job_needs(body):
            findings.append("the controls job '%s' needs: %s, so a failure upstream hides it"
                            % (name, job_needs(body)))
    return findings


def audit_workflow(text):
    """Every named rule at once, for the raw-byte-pin control and the mutation
    proofs that have to see a whole file's worth of findings."""
    code = workflow_code(text)
    findings = []
    for producer in (permission_findings, supply_chain_findings, allocation_findings,
                     cache_findings, artifact_findings, compare_findings, epoch_findings,
                     clean_tree_findings, scope_findings, paths_findings,
                     controls_job_findings):
        findings.extend(producer(code))
    return findings


# ===========================================================================
# The mutations. Each returns (mutated text, applied) and a mutation that no
# longer applies makes its control report INERT rather than pass.
# ===========================================================================

def mutate_plant_cache(text):
    """An `actions/cache` step on the first assemble job, plus a `cache:` input
    on its setup-dotnet -- the spelling a rule naming only the action misses."""
    lines = text.splitlines()
    out = []
    applied = []
    for line in lines:
        out.append(line)
        if not applied and re.match(r"^        with:\s*$", line):
            continue
        if "dotnet-version: ${{ env.SDK_VERSION }}" in line and "setup-dotnet-cache" not in applied:
            out.append("          cache: true")
            applied.append("setup-dotnet-cache")
    if applied:
        out.extend([
            "      - uses: actions/cache@0400d5f644dc74513175e3cd8d07132dd4860809 "
            "# v4.2.4",
            "        with:",
            "          path: ${{ runner.temp }}/w2a4-a/work",
            "          key: w2a4-${{ github.sha }}",
            "          restore-keys: w2a4-",
        ])
        applied.append("actions/cache")
    return "\n".join(out) + "\n", applied


def mutate_chain_assemble_jobs(text):
    """`needs: [assemble-a]` on the second assemble job: two builds in sequence,
    which is what §8 clause 1 says is not the proof."""
    lines = text.splitlines()
    out = []
    applied = []
    for line in lines:
        out.append(line)
        if line == "  assemble-b:" and not applied:
            out.append("    needs: [assemble-a]")
            applied.append("needs: between the assemble jobs")
    return "\n".join(out) + "\n", applied


def mutate_upload_the_archive(text):
    """The §8.7 violation: the archive itself offered to another job."""
    marker = "          name: w2a4-sha256-a"
    if marker not in text:
        return text, []
    mutated = text.replace(
        "          name: w2a4-sha256-a\n"
        "          path: ${{ runner.temp }}/w2a4-a/evidence/sha256.txt\n",
        "          name: w2a4-zip-a\n"
        "          path: ${{ runner.temp }}/w2a4-a/out/tesserafin-server_1.0.0_win-x64.zip\n",
        1)
    if mutated == text:
        return text, []
    return mutated, ["an upload of the archive itself"]


def mutate_skippable_compare(text):
    """A compare that is SKIPPED when an allocation fails. GitHub renders a
    skipped job as neither red nor green, and a required check that never ran
    is exactly the shape a reader mistakes for agreement."""
    lines = text.splitlines()
    out = []
    applied = []
    skipping = False
    for line in lines:
        if line == "    if: ${{ always() }}":
            applied.append("the always() guard removed")
            continue
        if re.match(r"^      - name: Both allocations must have succeeded\s*$", line):
            skipping = True
            applied.append("the needs-result guard removed")
            continue
        if skipping:
            if re.match(r"^      - ", line):
                skipping = False
            else:
                continue
        out.append(line)
    return "\n".join(out) + "\n", applied


def mutate_clock_epoch(text):
    """The epoch taken from the runner's clock, which two allocations cannot
    agree on and which would make an identical pair a coincidence."""
    marker = "          $epoch = (& git log -1 --format=%ct $head).Trim()"
    if marker not in text:
        return text, []
    return text.replace(
        marker,
        "          $epoch = [string][System.DateTimeOffset]::UtcNow.ToUnixTimeSeconds()"),\
        ["the epoch taken from UtcNow"]


def v1_permission_mutation(text):
    """The exact shape W2-A0-V1 walked past that slice's workflow audit, plus
    the two shapes this slice adds.

    Everything the rest of the audit reads is carried over verbatim, so the only
    reason this file must be refused is the trigger, the write-all grants and
    the extra job. Every block it replaces is DEMOTED TO A COMMENT rather than
    deleted, so `pull_request:` and `contents: read` still appear in the file:
    that is exactly the substitution the earlier audit accepted, and a mutation
    that merely deleted them would be refused by rules that predate this repair.
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

    # EVERY job-scope block, not only the first. This workflow declares four
    # jobs; demoting one of them would leave a live `contents: read` in another
    # and the mutation would then be refused for a reason it did not plant.
    demoted = 0
    while copy_until(lambda line: line == "    permissions:"):
        out.append("    permissions: write-all")
        demote_block("      ")
        demoted += 1
    if demoted:
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


# A workflow with the real file's structural shape and nothing else. Once a
# mutation has been applied to the real file, that file no longer HAS the block
# the next mutation rewrites, so a proof that could only mutate the live text
# would degrade to INERT at exactly the moment it is supposed to report RED.
# This baseline audits clean, which T02 asserts, so every finding a proof
# requires comes from the mutation rather than from the baseline.
FROZEN_SHAPE_BASELINE = """\
name: baseline
on:
  pull_request:
    paths:
      - 'ci/windows/w2/**'
      - 'ci/windows/runtime-retention/consume.ps1'
      - 'ci/windows/runtime-retention/accepted-runtime.json'
      - 'ci/windows/build-inputs/install-oras.sh'

permissions:
  contents: read

jobs:
  controls:
    runs-on: ubuntu-latest
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7.0.0
        with:
          ref: ${{ github.event.pull_request.head.sha }}
          persist-credentials: false
      - name: controls
        run: python3 ci/windows/w2/two-runner-controls.py
  assemble-a:
    runs-on: windows-latest
    permissions:
      contents: read
      packages: read
    steps:
      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7.0.0
        with:
          ref: ${{ github.event.pull_request.head.sha }}
          persist-credentials: false
      - uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1 # v5.4.0
        with:
          dotnet-version: ${{ env.SDK_VERSION }}
      - name: epoch
        id: epoch
        shell: pwsh
        run: |
          $head = '${{ github.event.pull_request.head.sha }}'
          $checkout = (& git rev-parse HEAD).Trim()
          $epoch = (& git log -1 --format=%ct $head).Trim()
      - name: clean
        shell: pwsh
        run: |
          $work = Join-Path $env:RUNNER_TEMP 'w2a4-a/work'
          $out  = Join-Path $env:RUNNER_TEMP 'w2a4-a/out'
          if (Test-Path -LiteralPath $work) { throw 'exists' }
          if (Test-Path -LiteralPath $out) { throw 'exists' }
      - name: assemble
        shell: pwsh
        run: |
          ./ci/windows/w2/assemble-server-zip.ps1 `
            -RepoRoot $PWD `
            -WorkDir (Join-Path $env:RUNNER_TEMP 'w2a4-a/work') `
            -OutDir (Join-Path $env:RUNNER_TEMP 'w2a4-a/out') `
            -SourceDateEpoch ${{ steps.epoch.outputs.epoch }} `
            -OrasPath (Join-Path $env:RUNNER_TEMP 'bin\\oras.exe')
      - uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1
        with:
          name: w2a4-sha256-a
          path: ${{ runner.temp }}/w2a4-a/evidence/sha256.txt
          if-no-files-found: error
  assemble-b:
    runs-on: windows-latest
    permissions:
      contents: read
      packages: read
    steps:
      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7.0.0
        with:
          ref: ${{ github.event.pull_request.head.sha }}
          persist-credentials: false
      - uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1 # v5.4.0
        with:
          dotnet-version: ${{ env.SDK_VERSION }}
      - name: epoch
        id: epoch
        shell: pwsh
        run: |
          $head = '${{ github.event.pull_request.head.sha }}'
          $checkout = (& git rev-parse HEAD).Trim()
          $epoch = (& git log -1 --format=%ct $head).Trim()
      - name: clean
        shell: pwsh
        run: |
          $work = Join-Path $env:RUNNER_TEMP 'w2a4-b/work'
          $out  = Join-Path $env:RUNNER_TEMP 'w2a4-b/out'
          if (Test-Path -LiteralPath $work) { throw 'exists' }
          if (Test-Path -LiteralPath $out) { throw 'exists' }
      - name: assemble
        shell: pwsh
        run: |
          ./ci/windows/w2/assemble-server-zip.ps1 `
            -RepoRoot $PWD `
            -WorkDir (Join-Path $env:RUNNER_TEMP 'w2a4-b/work') `
            -OutDir (Join-Path $env:RUNNER_TEMP 'w2a4-b/out') `
            -SourceDateEpoch ${{ steps.epoch.outputs.epoch }} `
            -OrasPath (Join-Path $env:RUNNER_TEMP 'bin\\oras.exe')
      - uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1
        with:
          name: w2a4-sha256-b
          path: ${{ runner.temp }}/w2a4-b/evidence/sha256.txt
          if-no-files-found: error
  compare:
    runs-on: ubuntu-latest
    needs: [assemble-a, assemble-b]
    if: ${{ always() }}
    permissions:
      contents: read
    steps:
      - name: Both allocations must have succeeded
        if: ${{ needs.assemble-a.result != 'success' || needs.assemble-b.result != 'success' }}
        run: |
          exit 1
      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7.0.0
        with:
          ref: ${{ github.event.pull_request.head.sha }}
          persist-credentials: false
      - uses: actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c # v8.0.1
        with:
          name: w2a4-sha256-a
          path: ${{ runner.temp }}/hash-a
      - uses: actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c # v8.0.1
        with:
          name: w2a4-sha256-b
          path: ${{ runner.temp }}/hash-b
      - name: compare
        run: |
          python3 ci/windows/w2/two-runner-controls.py --compare \\
            "${RUNNER_TEMP}/hash-a/sha256.txt" \\
            "${RUNNER_TEMP}/hash-b/sha256.txt"
"""


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


def audited_rule(report, name, live_findings, mutations, description):
    """One named rule: clean on the real file, and proved load-bearing by a
    mutation the audit has to name.

    `mutations` is a list of `(mutate, required substring, label)`. A mutation
    that no longer applies, or that the audit does not refuse, reports INERT --
    a rule that cannot be shown to bite is a comment.
    """
    for mutate, needle, label in mutations:
        mutated, applied = mutate(FROZEN_SHAPE_BASELINE)
        if not applied:
            report.record(name, "INERT", "the %s mutation no longer applies to the baseline"
                          % label)
            return
        findings = audit_workflow(mutated)
        if not any(needle in finding for finding in findings):
            report.record(name, "INERT", "the audit does not refuse %s (planted; findings: %s)"
                          % (label, findings or "none"))
            return
    if live_findings:
        report.record(name, "RED", "; ".join(live_findings))
    else:
        report.record(name, "PASS", description)


# ===========================================================================
# Driving the real compare as a subprocess
# ===========================================================================

def write_bytes(path, payload):
    with open(path, "wb") as handle:
        handle.write(payload)
    return path


def drive_compare(work, left, right):
    """Run THIS file's `--compare` exactly the way the hosted compare job does."""
    command = [sys.executable, os.path.abspath(__file__), "--compare", left, right,
               "--label-a", "assemble-a", "--label-b", "assemble-b"]
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            cwd=work, check=False)
    return result.returncode, result.stdout.decode("utf-8", "replace")


GOOD = b"a" * 64 + b"\n"
OTHER = b"b" * 64 + b"\n"


def compare_case(work, name, left_bytes, right_bytes):
    directory = os.path.join(work, "compare", name)
    os.makedirs(directory, exist_ok=True)
    left = os.path.join(directory, "a.txt")
    right = os.path.join(directory, "b.txt")
    if left_bytes is not None:
        write_bytes(left, left_bytes)
    if right_bytes is not None:
        write_bytes(right, right_bytes)
    return drive_compare(work, left, right)


def refusal_cases(report, name, cases, description):
    """Every case must exit non-zero AND say NO IDENTITY. An exit code alone
    would be satisfied by a crash, and a crash proves nothing about the rule."""
    failures = []
    for label, left, right in cases:
        code, output = compare_case(report_work, label, left, right)
        if code == 0:
            failures.append("%s: the compare accepted it (exit 0)" % label)
        elif "NO IDENTITY" not in output:
            failures.append("%s: refused without a verdict line -- %s"
                            % (label, output.strip()[-160:]))
    if failures:
        report.record(name, "RED", "; ".join(failures))
    else:
        report.record(name, "PASS", description)


report_work = None


# ===========================================================================
# The controls
# ===========================================================================

def run_controls(work, report, only=None):
    global report_work
    report_work = work

    def selected(name):
        return only is None or name in only

    workflow_text = read_text(WORKFLOW) if os.path.isfile(WORKFLOW) else None
    code = workflow_code(workflow_text) if workflow_text is not None else ""

    # --- T01: the raw-byte pin over the new workflow --------------------------
    if selected("T01"):
        if workflow_text is None:
            report.record("T01", "RED", "the W2-A4 workflow is missing")
        else:
            with open(WORKFLOW, "rb") as handle:
                raw = handle.read()
            digest = hashlib.sha256(raw).hexdigest()
            if digest == WORKFLOW_SHA256:
                report.record("T01", "PASS",
                              "byte-identical to the pinned workflow (%d bytes, %s)"
                              % (len(raw), WORKFLOW_SHA256[:16]))
            elif hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest() == WORKFLOW_SHA256:
                report.record("T01", "RED",
                              "differs from the pinned workflow in its line endings only "
                              "(%d bytes, pinned content)" % len(raw))
            else:
                report.record("T01", "RED", "is not the pinned workflow: %s, pinned %s"
                              % (digest[:16], WORKFLOW_SHA256[:16]))

    # --- T02: the V1 mutation, named ------------------------------------------
    if selected("T02"):
        baseline_findings = audit_workflow(FROZEN_SHAPE_BASELINE)
        if baseline_findings:
            report.record("T02", "INERT", "the live-proof baseline does not audit clean: %s"
                          % baseline_findings)
        else:
            expected = ["push trigger", "workflow-scope write-all",
                        "job-scope write-all with the reads demoted to comments", "second job"]
            mutant, applied = v1_permission_mutation(FROZEN_SHAPE_BASELINE)
            if applied != expected:
                report.record("T02", "INERT",
                              "the V1 permission mutation no longer applies: %s" % applied)
            else:
                mutant_findings = audit_workflow(mutant)
                required = {
                    "grants write-all": "write-all",
                    # The mutation writes `on: [push, pull_request]`, a
                    # flow-style sequence, so the block-style `push:` rule is not
                    # what refuses it -- the trigger reader is.
                    "triggers on 'push'": "the push trigger",
                    "declares the unauthorised job 'exfiltrate'": "the second job",
                    "does not request contents: read at any scope":
                        "reads that exist only in a comment",
                }
                unproven = [label for finding, label in required.items()
                            if finding not in mutant_findings]
                if unproven:
                    report.record("T02", "INERT", "the audit does not refuse %s"
                                  % ", ".join(sorted(unproven)))
                elif workflow_text is not None and \
                        hashlib.sha256(mutant.encode("utf-8")).hexdigest() == WORKFLOW_SHA256:
                    report.record("T02", "INERT",
                                  "the content pin does not distinguish the mutation")
                else:
                    planted = ("on:\n  push:\n  workflow_dispatch:\npermissions:\n"
                               "  packages: write\njobs:\n  x:\n    steps:\n"
                               "      - uses: actions/checkout@v4\n")
                    planted_findings = audit_workflow(planted)
                    missed = [label for label in ("declares packages: write",
                                                  "declares workflow_dispatch",
                                                  "declares push",
                                                  "unpinned action actions/checkout@v4")
                              if label not in planted_findings]
                    if missed:
                        report.record("T02", "INERT",
                                      "the audit does not detect a planted violation: %s"
                                      % ", ".join(missed))
                    else:
                        live = permission_findings(code) + supply_chain_findings(code)
                        if live:
                            report.record("T02", "RED", "; ".join(live))
                        else:
                            report.record("T02", "PASS",
                                          "the audit names the V1 trigger, both write-all "
                                          "grants, the extra job and the demoted reads, and "
                                          "the real file carries none of them")

    # --- T03: two allocations, chained to nothing -----------------------------
    if selected("T03"):
        audited_rule(report, "T03", allocation_findings(code),
                     [(mutate_chain_assemble_jobs, "needs:", "a needs: edge")],
                     "two jobs invoke the frozen assembler once each, both on windows-latest, "
                     "into different work and output trees, and neither needs: the other")

    # --- T04: no cache --------------------------------------------------------
    if selected("T04"):
        audited_rule(report, "T04", cache_findings(code),
                     [(mutate_plant_cache, "cache", "a planted cache")],
                     "no actions/cache, no cache: input on a setup action and no restore-keys, "
                     "so the two allocations share no restored state")

    # --- T05: no archive crosses a job boundary -------------------------------
    if selected("T05"):
        audited_rule(report, "T05", artifact_findings(code),
                     [(mutate_upload_the_archive, "uploads an archive",
                       "an upload of the .zip")],
                     "each allocation uploads exactly one sha256.txt and nothing else, with "
                     "if-no-files-found: error, and no assemble job downloads anything (W0 "
                     "§8.7)")

    # --- T06: the compare cannot be skipped -----------------------------------
    if selected("T06"):
        audited_rule(report, "T06", compare_findings(code),
                     [(mutate_skippable_compare, "always()", "a skippable compare")],
                     "one job needs both allocations, runs under always(), fails unless both "
                     "results are success, downloads one evidence file per allocation and "
                     "runs the audited comparison")

    # --- T07/T08/T09: the REAL compare, driven ---------------------------------
    if selected("T07"):
        refusal_cases(report, "T07", [
            ("missing-left", None, GOOD),
            ("missing-right", GOOD, None),
            ("missing-both", None, None),
        ], "a missing evidence file is refused on either side, and on both")

    if selected("T08"):
        refusal_cases(report, "T08", [
            ("empty", b"", GOOD),
            ("truncated", b"a" * 63 + b"\n", GOOD),
            ("over-long", b"a" * 65 + b"\n", GOOD),
            ("unterminated", b"a" * 64, GOOD),
            ("crlf", b"a" * 64 + b"\r\n", GOOD),
            ("uppercase", b"A" * 64 + b"\n", GOOD),
            ("non-hex", b"z" * 64 + b"\n", GOOD),
            ("bom", b"\xef\xbb\xbf" + b"a" * 61 + b"\n", GOOD),
            ("two-lines", b"a" * 31 + b"\n" + b"a" * 32 + b"\n", GOOD),
            ("malformed-right", GOOD, b"A" * 64 + b"\n"),
        ], "empty, truncated, over-long, unterminated, CRLF, uppercase, non-hex, "
           "BOM-prefixed and two-line evidence files are all refused, on either side")

    if selected("T09"):
        code_differ, output_differ = compare_case(work, "differ", GOOD, OTHER)
        code_same, output_same = compare_case(work, "same", GOOD, GOOD)
        findings = []
        if code_differ == 0:
            findings.append("two different hashes were accepted")
        elif "disagree" not in output_differ:
            findings.append("two different hashes were refused without saying they disagree")
        if code_same != 0:
            findings.append("one well-formed agreeing pair was refused: %s"
                            % output_same.strip()[-160:])
        elif "IDENTICAL" not in output_same:
            findings.append("an agreeing pair produced no identity line")
        elif "a" * 64 not in output_same:
            findings.append("the agreed hash is not printed")
        if findings:
            report.record("T09", "RED", "; ".join(findings))
        else:
            report.record("T09", "PASS",
                          "two different hashes are refused as a disagreement, and the one "
                          "agreeing well-formed pair is accepted and its hash printed")

    # --- T10: the epoch -------------------------------------------------------
    if selected("T10"):
        audited_rule(report, "T10", epoch_findings(code),
                     [(mutate_clock_epoch, "UtcNow", "an epoch from the clock")],
                     "both allocations derive SOURCE_DATE_EPOCH from `git log -1 --format=%ct` "
                     "of the pull request head commit, check they checked that commit out, and "
                     "pass the derived value and nothing else")

    # --- T11: one assembly per allocation, from nothing -----------------------
    if selected("T11"):
        findings = clean_tree_findings(code)
        planted = FROZEN_SHAPE_BASELINE.replace(
            "          if (Test-Path -LiteralPath $work) { throw 'exists' }\n", "", 1)
        if not clean_tree_findings(workflow_code(planted)):
            report.record("T11", "INERT",
                          "removing an absent-tree guard from a copy changes nothing this "
                          "check sees")
        elif not clean_tree_findings(workflow_code(
                FROZEN_SHAPE_BASELINE.replace("-RepoRoot $PWD", "-RepoRoot $PWD `\n"
                                              "            -StageRoot $stage", 1))):
            report.record("T11", "INERT", "a planted -StageRoot is not detected")
        elif findings:
            report.record("T11", "RED", "; ".join(findings))
        else:
            report.record("T11", "PASS",
                          "each allocation requires both its work tree and its output tree to "
                          "be absent before the assembler is given them, and no caller-chosen "
                          "identity or pack-only oracle is passed anywhere")

    # --- T12: nothing starts --------------------------------------------------
    if selected("T12"):
        findings = scope_findings(code)
        planted = "run: ./ci/windows/w2/relocate-and-start.ps1\nrun: docker pull x\n"
        missed = [label for label in ("runs the W2-A3 relocate-and-start proof", "names docker")
                  if label not in scope_findings(planted)]
        if missed:
            report.record("T12", "INERT", "the scope audit misses %s" % ", ".join(missed))
        elif findings:
            report.record("T12", "RED", "; ".join(findings))
        else:
            report.record("T12", "PASS",
                          "no relocate-and-start, no tesserafin.exe, no service registration, "
                          "no .reefin and no container engine anywhere on this workflow")

    # --- T13: the write surface -----------------------------------------------
    if selected("T13"):
        findings = permission_findings(code)
        planted = ("on:\n  pull_request_target:\n  schedule:\npermissions:\n  contents: write\n"
                   "jobs:\n  y:\n    steps: []\n")
        missed = [label for label in ("declares pull_request_target", "declares schedule",
                                      "declares contents: write",
                                      "declares the unauthorised job 'y'")
                  if label not in permission_findings(planted)]
        if missed:
            report.record("T13", "INERT", "the permission audit misses %s" % ", ".join(missed))
        elif findings:
            report.record("T13", "RED", "; ".join(findings))
        else:
            report.record("T13", "PASS",
                          "one pull_request trigger, four authorised jobs, contents: read plus "
                          "a job-scoped packages: read, and no write grant at any scope")

    # --- T14: the identities agree --------------------------------------------
    if selected("T14"):
        if not os.path.isfile(ASSEMBLER):
            report.record("T14", "RED", "the frozen assembler is missing")
        elif not os.path.isfile(ACCEPTED_JSON):
            report.record("T14", "RED", "the committed acceptance manifest is missing")
        elif workflow_text is None:
            report.record("T14", "RED", "the W2-A4 workflow is missing")
        else:
            assembler_text = read_text(ASSEMBLER)
            assembler_code = strip_commentary(ASSEMBLER, assembler_text)
            accepted = json.loads(read_text(ACCEPTED_JSON))
            findings = []
            for who, text in (("the assembler", assembler_code), ("the workflow", code)):
                if WEB_PAYLOAD_SHA256 not in text:
                    findings.append("%s does not name WEB_PAYLOAD_SHA256" % who)
                if ACCEPTED_RUNTIME_SHA256 not in text:
                    findings.append("%s does not name the accepted runtimeSha256" % who)
            if accepted.get("runtimeSha256") != ACCEPTED_RUNTIME_SHA256:
                findings.append("the committed acceptance manifest pins runtime %s"
                                % accepted.get("runtimeSha256"))
            if accepted.get("platform") != "win-x64":
                findings.append("the committed acceptance manifest is for %s"
                                % accepted.get("platform"))
            if "provenance.json" not in code:
                findings.append("the workflow never reads the manifest packed inside its own "
                                "archive, so its restated identities are decorative")
            mutated = strip_commentary(ASSEMBLER,
                                       assembler_text.replace(WEB_PAYLOAD_SHA256, "0" * 64))
            if WEB_PAYLOAD_SHA256 in mutated:
                report.record("T14", "INERT", "the agreement check does not detect a mutation")
            elif findings:
                report.record("T14", "RED", "; ".join(findings))
            else:
                report.record("T14", "PASS",
                              "the ruling, the frozen assembler, the committed acceptance "
                              "manifest and this workflow state one web payload digest and one "
                              "runtime digest and agree, and each allocation reads them back "
                              "out of its own packed provenance manifest")

    # --- T15: every accepted W2 file is unmodified ----------------------------
    if selected("T15"):
        findings = []
        for relative, pinned in sorted(FROZEN_PINS.items()):
            path = os.path.join(REPO_ROOT, relative)
            if not os.path.isfile(path):
                findings.append("%s is missing" % relative)
                continue
            with open(path, "rb") as handle:
                raw = handle.read()
            digest = hashlib.sha256(raw).hexdigest()
            if digest == pinned:
                continue
            if hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest() == pinned:
                findings.append("%s differs in its line endings only" % relative)
            else:
                findings.append("%s is modified: %s, pinned %s"
                                % (relative, digest[:16], pinned[:16]))
        if findings:
            report.record("T15", "RED", "; ".join(findings))
        else:
            report.record("T15", "PASS",
                          "all %d previously accepted W2 files, including the frozen assembler "
                          "and ffmpeg-consume-controls.py (F18), are byte-identical"
                          % len(FROZEN_PINS))

    # --- T16: no new .ps1 -----------------------------------------------------
    if selected("T16"):
        entries = sorted(os.listdir(HERE))

        def ps1_findings(names):
            return ["ci/windows/w2/%s is a .ps1 W2-A4 is not authorised to add" % name
                    for name in sorted(names)
                    if name.lower().endswith(".ps1") and name not in ALLOWED_PS1]

        if not ps1_findings(entries + ["two-runner-compare.ps1"]):
            report.record("T16", "INERT", "the new-script check does not detect one")
        else:
            findings = ps1_findings(entries)
            if findings:
                report.record("T16", "RED", "; ".join(findings))
            else:
                report.record("T16", "PASS",
                              "ci/windows/w2 carries exactly the three accepted .ps1 files; "
                              "W2-A4 adds a .py and a workflow and nothing else")

    # --- T17: the paths filter ------------------------------------------------
    if selected("T17"):
        findings = paths_findings(code)
        planted = "paths:\n  - 'ci/windows/w2/two-runner-controls.py'\n"
        if not paths_findings(planted):
            report.record("T17", "INERT",
                          "a filter naming one file instead of the tree is not detected")
        elif findings:
            report.record("T17", "RED", "; ".join(findings))
        else:
            report.record("T17", "PASS",
                          "the filter watches the whole ci/windows/w2 tree plus every frozen "
                          "file this workflow runs, so a file added under it cannot be "
                          "reviewed without running this proof")

    # --- T18: the supply chain ------------------------------------------------
    if selected("T18"):
        audited_rule(report, "T18", supply_chain_findings(code), [],
                     "every action is pinned to a 40-hex commit, every checkout declares "
                     "persist-credentials: false, and there is a windows-latest job")

    # --- T19: the controls run where nothing is assembled ---------------------
    if selected("T19"):
        findings = controls_job_findings(code)
        planted = FROZEN_SHAPE_BASELINE.replace(
            "        run: python3 ci/windows/w2/two-runner-controls.py\n", "", 1)
        if not controls_job_findings(workflow_code(planted)):
            report.record("T19", "INERT",
                          "removing the controls invocation from a copy changes nothing this "
                          "check sees")
        elif findings:
            report.record("T19", "RED", "; ".join(findings))
        else:
            report.record("T19", "PASS",
                          "this suite runs in a job that assembles nothing and needs nothing, "
                          "so a defective suite is visible in seconds rather than behind two "
                          "hours of Windows time")

    # --- T20: the document ----------------------------------------------------
    if selected("T20"):
        if not os.path.isfile(DOC):
            report.record("T20", "RED", "docs/distribution/W2-A4-two-runner.md is missing")
        else:
            doc = read_text(DOC)
            required = {
                "the two-runner reading of §8.1": "8.1",
                "the §8.7 artifact rule": "8.7",
                "how the epoch is derived": "git log -1 --format=%ct",
                "the no-start non-goal": "does not start",
                "the no-relocation non-goal": "does not relocate",
                "the no-service-script non-goal": "service script",
                "the no-.reefin non-goal": ".reefin",
                "the no-publication non-goal": "publish",
                "that W2 is not accepted": "W2 is not accepted",
            }
            missing = [label for label, needle in required.items() if needle not in doc]
            claims = [phrase for phrase in ("W2 is accepted", "W2 accepted", "ready for merge")
                      if phrase in doc and "not " + phrase not in doc]
            if missing:
                report.record("T20", "RED", "the doc does not state %s" % ", ".join(missing))
            elif claims:
                report.record("T20", "RED", "the doc claims %s" % ", ".join(claims))
            else:
                report.record("T20", "PASS",
                              "the doc states the two-allocation reading of §8.1, the §8.7 "
                              "artifact rule, the epoch derivation and every non-goal, and "
                              "claims no acceptance")


# ===========================================================================

def _repository_fingerprint():
    fingerprint = {}
    paths = [WORKFLOW, DOC, ASSEMBLER, ACCEPTED_JSON, os.path.abspath(__file__)]
    paths += [os.path.join(REPO_ROOT, relative) for relative in FROZEN_PINS]
    for path in paths:
        if os.path.isfile(path):
            fingerprint[os.path.relpath(path, REPO_ROOT)] = sha256_file(path)
    return fingerprint


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--only", action="append", help="run only the named control(s)")
    parser.add_argument("--compare", nargs=2, metavar=("A", "B"),
                        help="compare two evidence files and exit non-zero unless they are "
                             "the same 64 lowercase hex digits")
    parser.add_argument("--label-a", default="A", help="what to call the first allocation")
    parser.add_argument("--label-b", default="B", help="what to call the second allocation")
    args = parser.parse_args(argv)

    if args.compare:
        return run_compare(args)

    work = tempfile.mkdtemp(prefix="w2a4-controls-")
    try:
        before = _repository_fingerprint()
        print("W2-A4 hostile controls")
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
        print("W2-A4 controls: %d PASS, %d RED, %d INERT in %.1fs"
              % (totals["PASS"], totals["RED"], totals["INERT"], time.time() - started))
        return 0 if (totals["RED"] == 0 and totals["INERT"] == 0) else 1
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
