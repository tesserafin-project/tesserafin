#!/usr/bin/env python3
"""Hostile controls for W2's relocate-and-start proof (W2-A3, #256).

W2-A3's claim is narrow:

    the tree produced by the FROZEN W2-A2 assembler starts after a hostile-path
    relocation, and starts again after a second move to a different depth, with
    fresh isolated state both times and nothing borrowed from the build tree.

The whole risk of a claim like that is that it goes GREEN for the wrong reason.
W0 §2.3 records four ways it already did: a server that had logged "Startup
complete" was recorded as "did not start" because the prober knocked on the
port a file had ASKED for; a healthy 302 looked like no answer at all; the
startup `SetupServer` answered every path with 503 and a probe that accepted
"any status" measured that instead of the server; and a dying host served one
non-503 response on its way down, which made W0's no-FFmpeg negative control
report a STARTED server.

So the controls come in three shapes.

  * OBSERVED REFUSALS, PLANT FIRST. Every readiness, port and Web-bootstrap
    rule is driven against a REAL HTTP fixture through the production script's
    own `-Oracle` parameter set -- the same functions the hosted run uses, not a
    second copy written for a test. Each is paired with a live INERT-proof: the
    same fixture is handed to a MUTATED COPY of the script with that one check
    defeated, and the mutant is required to do the thing the real one refused.
    A control whose mutation no longer applies reports INERT rather than a
    smaller green suite, because a refusal that cannot be shown to be
    load-bearing is a comment.

  * A SOURCE AUDIT OF THE PRODUCTION PATH. Some properties cannot be observed
    behaviourally at all. "The port is never read from the network
    configuration file" cannot be tested by not reading it: a test can only
    fail to observe a read, which is indistinguishable from the read existing
    and being unreachable on that input. Those are asserted against the text of
    the production path, over its EXECUTABLE part only.

  * A RAW-BYTE PIN over this slice's workflow and over the five frozen inputs,
    which refuses every other edit -- including edits this slice is not
    authorised to make at all.

Nothing here reaches a registry, publishes a server, assembles an archive or
modifies the repository. The RESTORE row asserts the last of those rather than
assuming it. The fixtures listen on 127.0.0.1 on a port the OS chooses.

    python3 ci/windows/w2/start-controls.py
    python3 ci/windows/w2/start-controls.py --only S05
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
import threading
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))

# The one file W2-A3 adds to the production path, and the workflow that runs it.
PROOF = os.path.join(HERE, "relocate-and-start.ps1")
WORKFLOW = os.path.join(REPO_ROOT, ".github", "workflows", "w2-windows-relocate-start.yml")

# The frozen inputs. W2-A3 authors none of these and edits none of them. The
# assembler is on this list because W2-A3 CALLS it and must not change it: the
# amendment that let this file exist at all (W2-A3-F18) widened one allowlist in
# A1's controls and nothing else.
ASSEMBLER = os.path.join(HERE, "assemble-server-zip.ps1")
WEB_CONSUMER = os.path.join(HERE, "consume-web-payload.ps1")
TREE_DIGEST = os.path.join(HERE, "pkg-tree-digest.py")
RUNTIME_CONSUMER = os.path.join(REPO_ROOT, "ci", "windows", "runtime-retention", "consume.ps1")
ACCEPTED_JSON = os.path.join(REPO_ROOT, "ci", "windows", "runtime-retention",
                             "accepted-runtime.json")

# ---------------------------------------------------------------------------
# The frozen identities. The ruling's values, written here so S12 can require
# the workflow, the acceptance manifest and the assembler to agree with them --
# independent statements of one identity, rather than one statement read
# several times.
# ---------------------------------------------------------------------------
WEB_PAYLOAD_SHA256 = "4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f"
ACCEPTED_RUNTIME_SHA256 = "f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e"
# Descriptive only, and never a trust boundary.
DESCRIPTIVE_TAG = "accepted-83e23b957940"

# Raw-byte pins. `.gitattributes` normalises this repository to LF, so a Windows
# checkout delivers the same bytes a Linux one does and these are safe to assert
# literally. A pin over a text-mode read would have been more forgiving and less
# true: a CRLF copy hashes identically under universal newlines, so a file
# differing only in line endings would be called byte-identical.
FROZEN_PINS = {
    # Moved under W2-A4-R2-S15, which authorised this file for this pin and
    # nothing else: W2-A4-R2 changed the assembler, and a pin that still named
    # the pre-R2 bytes would fail A3's hosted job at its controls step and
    # never reach the start proof. Every other pin below is unchanged.
    "ci/windows/w2/assemble-server-zip.ps1":
        "b4fbb81538e5fdefb26928373bee61969c141733b3fae91159e0198092f94f33",
    "ci/windows/w2/consume-web-payload.ps1":
        "db49f21001067a8f55ae71432ff9d47830daa454704a09800bb0e1eadf3b117c",
    "ci/windows/w2/pkg-tree-digest.py":
        "0c70114c69e85d06bc3d95249cc1a86f917eb2b8deb44718cc05ad6f3afa70b4",
    "ci/windows/runtime-retention/consume.ps1":
        "f19fefcc48de9ae2175aa49ecff6e732762219a3d76c38067ba4114a1924646d",
    "ci/windows/runtime-retention/accepted-runtime.json":
        "593c21f59c67dd564fa488f660efc14b74b5c5bcd775bbc3ef0bdf9e94dd9ece",
}
WORKFLOW_SHA256 = "d493f1f25cdc94fecf33649fdbfe6bd401795b6db14c410fe9f1ac123ba07ce9"

WORKFLOW_ALLOWED_TRIGGERS = ("pull_request",)
WORKFLOW_ALLOWED_JOBS = ("relocate-start",)

# The proof's production parameter surface. `-Oracle` and its companions are
# deliberately NOT here: they belong to the fixture-driven oracle these controls
# use, and S13 asserts the production workflow never passes any of them.
PROOF_ARGUMENTS = ("-RepoRoot", "-WorkDir", "-OutDir", "-EvidencePath",
                   "-SourceDateEpoch", "-OrasPath", "-PythonPath",
                   "-ReadyTimeoutSeconds")

ORACLE_ARGUMENTS = ("-Oracle", "-BaseUrl", "-OracleProcessId",
                    "-OracleListeningPorts", "-OracleTimeoutSeconds")

# Parameter names whose presence in the proof's top-level param block would make
# the started identity caller-controlled. The whole security property of this
# slice is that what gets started is what the frozen assembler built from THIS
# commit, in THIS job -- W0 §8.7 forbids an Actions-artifact handover in
# production, and a `-RunId` or an `-ArchivePath` is that handover with a
# different spelling.
FORBIDDEN_PARAMETERS = ("$Reference", "$Digest", "$Tag", "$RunId", "$Run",
                        "$ArchivePath", "$Archive", "$ZipPath", "$Package",
                        "$Registry", "$Image", "$Url", "$Uri", "$Version",
                        "$Port", "$WebPayloadSha256", "$RuntimeSha256")

# Every control this suite is required to report. A control that is deleted,
# renamed or silently skipped stops appearing here, and `main` turns that into a
# RED rather than into a smaller green suite. Without this, removing an
# inconvenient control would IMPROVE the summary line.
ROSTER = {
    "S01": "a server that only ever answers 503 is not ready",
    "S02": "readiness is keyed to '/', never to /System/Info/Public",
    "S03": "a redirect is an answer, and requiring 200 is a different rule",
    "S04": "the process must still be running three seconds after it answers",
    "S05": "the port comes from the process, and an empty answer is refused",
    "S06": "the entry document must reference the hashed bundle",
    "S07": "the port is never read from configuration, the environment or a default",
    "S08": "the two starts are given their own state, and a shared one is refused",
    "S09": "a start from the assembler's own tree is refused",
    "S10": "the archive is extracted once and the second start is a move",
    "S11": "no caller-supplied identity anywhere on the production path",
    "S12": "the ruling, the proof, the acceptance manifest and the workflow agree",
    "S13": "no Actions artifact, cache or dispatch, and the oracle is not the production door",
    "S14": "the new workflow is the authorised one, byte for byte",
    "S15": "the five frozen inputs are unmodified",
    "S16": "the proof registers no service, publishes nothing and starts no third time",
    "S17": "the workflow watches the whole ci/windows/w2 tree (W2-A1-V1 NB-1)",
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


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


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


# ===========================================================================
# The HTTP fixtures
#
# Written to disk and spawned rather than served in-process, because one of the
# shapes under test is "answers, then the process is GONE three seconds later",
# and a thread cannot exit a process the controls still need.
# ===========================================================================

FIXTURE_SOURCE = r'''
import os
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MODE = sys.argv[1]
BUNDLE = (b'<!DOCTYPE html><html><head>'
          b'<script defer src="main.tesserafin.0123456789abcdef.bundle.js"></script>'
          b'</head><body></body></html>')
SETUP_PAGE = (b'<!DOCTYPE html><html><head><title>Setup</title></head>'
              b'<body>Setup Wizard</body></html>')
STARTING = b'{"status":"starting","version":"1.0.0"}'


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def _send(self, status, body=b"", location=None):
        self.send_response(status)
        if location is not None:
            self.send_header("Location", location)
        self.send_header("Content-Type", "text/html")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def do_GET(self):
        path = self.path
        if MODE == "always-503":
            # The startup SetupServer: it binds the real port early and answers
            # EVERY path with 503 until the application host takes over.
            self._send(503, STARTING)
            return
        if MODE == "setup-public-ok":
            # The same setup server, except that /System/Info/Public answers.
            # This is the exact shape that made keying readiness on that
            # endpoint report a server that had not started.
            if path.startswith("/System/Info/Public"):
                self._send(200, b'{"Id":"x"}')
            else:
                self._send(503, STARTING)
            return
        if MODE == "redirect":
            # A healthy server: '/' redirects to the web client and the entry
            # document references the hashed bundle.
            if path == "/":
                self._send(302, b"", location="web/")
            elif path == "/web/index.html":
                self._send(200, BUNDLE)
            else:
                self._send(404)
            return
        if MODE == "no-bundle":
            # A 200 that returns the setup page rather than the Web client.
            if path == "/web/index.html":
                self._send(200, SETUP_PAGE)
            else:
                self._send(302, b"", location="web/")
            return
        if MODE == "answer-then-die":
            # The dying host: it serves the one response it manages before the
            # application host finishes tearing itself down.
            self._send(302, b"", location="web/")
            try:
                self.wfile.flush()
            except Exception:
                pass
            threading.Timer(0.4, lambda: os._exit(0)).start()
            return
        self._send(500)


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
sys.stdout.write("PORT %d PID %d\n" % (server.server_address[1], os.getpid()))
sys.stdout.flush()
server.serve_forever()
'''


class Fixture:
    """One spawned HTTP fixture, addressed by its port and its process id."""

    def __init__(self, work, mode):
        self.mode = mode
        self.path = os.path.join(work, "fixture.py")
        if not os.path.exists(self.path):
            with open(self.path, "w", encoding="utf-8") as handle:
                handle.write(FIXTURE_SOURCE)
        self.process = subprocess.Popen(
            [sys.executable, self.path, mode],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        # Reap the fixture the instant it exits. The `answer-then-die` shape
        # calls os._exit AFTER answering, and on a POSIX host an unreaped child
        # stays a zombie whose /proc entry is still there -- so a liveness check
        # three seconds later would find the "process" alive and the control
        # would grade the real rule RED for a defect in this harness. Windows
        # has no zombies and does not need this; reaping is correct on both.
        self.reaper = threading.Thread(target=self._reap, daemon=True)
        self.reaper.start()
        line = self.process.stdout.readline().strip()
        match = re.match(r"^PORT (\d+) PID (\d+)$", line)
        if not match:
            self.stop()
            raise RuntimeError("the %r fixture did not announce itself: %r" % (mode, line))
        self.port = int(match.group(1))
        self.pid = int(match.group(2))

    def _reap(self):
        try:
            self.process.wait()
        except Exception:
            pass

    @property
    def base_url(self):
        return "http://127.0.0.1:%d/" % self.port

    def stop(self):
        try:
            self.process.kill()
        except Exception:
            pass
        try:
            self.process.wait(timeout=10)
        except Exception:
            pass

    def __enter__(self):
        return self

    def __exit__(self, *exception):
        self.stop()
        return False


# ===========================================================================
# Driving the REAL decision functions, and mutated copies of them
# ===========================================================================

class Oracle:
    """Run one `-Oracle` mode of a proof script and capture what it decided."""

    def __init__(self, script):
        self.script = script

    def run(self, *arguments, timeout=180):
        command = [POWERSHELL, "-NoProfile", "-NonInteractive",
                   "-File", self.script] + [str(a) for a in arguments]
        completed = subprocess.run(command, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, timeout=timeout)
        return completed.returncode, completed.stdout

    def readiness(self, base_url, pid, seconds=6):
        return self.run("-Oracle", "readiness", "-BaseUrl", base_url,
                        "-OracleProcessId", pid, "-OracleTimeoutSeconds", seconds)

    def port(self, ports, seconds=2):
        return self.run("-Oracle", "port", "-OracleListeningPorts", ports,
                        "-OracleTimeoutSeconds", seconds)

    def bundle(self, base_url):
        return self.run("-Oracle", "bundle", "-BaseUrl", base_url)


POWERSHELL = None


def find_powershell():
    for candidate in ("pwsh", "pwsh.exe", "powershell.exe"):
        found = shutil.which(candidate)
        if found:
            return found
    return None


def mutate(work, name, replacements):
    """A copy of the production proof with one check defeated.

    Returns (path, applied) where `applied` counts each replacement. A mutation
    that no longer applies makes its control report INERT: the control is then
    proving nothing about a rule that has moved or gone, and a suite that
    quietly kept it green would be worse than one that admits it.
    """
    text = read_text(PROOF)
    applied = []
    for old, new in replacements:
        count = text.count(old)
        applied.append(count)
        if count:
            text = text.replace(old, new)
    path = os.path.join(work, "mutant-%s.ps1" % name)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    return path, applied


# The mutations, each defeating exactly ONE rule and nothing else. They are kept
# together so a reader can see at a glance that no mutation touches two rules.
MUTATIONS = {
    # Readiness accepts any status, which is what measures the SetupServer.
    "any-status": [("if ($response.status -ne 503) { $answer = $response; break }",
                    "if ($true) { $answer = $response; break }")],
    # Readiness requires 200, which calls a healthy redirecting server dead.
    "require-200": [("if ($response.status -ne 503) { $answer = $response; break }",
                     "if ($response.status -eq 200) { $answer = $response; break }")],
    # Readiness is a single sample: no liveness gate after the answer.
    "no-liveness": [("    if (-not $alive) { return $null }",
                     "    if ($false) { return $null }")],
    # The port falls back to the compiled-in default instead of refusing.
    "port-default": [("    Deny 'port' ('the server bound no listening TCP port within the budget.",
                      "    return 8096\n    Deny 'port' ('the server bound no listening TCP port within the budget.")],
    # Any 200 is the Web client, so the setup page passes.
    "any-200-entry": [("    if ($response.body -notmatch $BUNDLE_PATTERN) {",
                       "    if ($false) {")],
}


# ===========================================================================
# The workflow audit
# ===========================================================================

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
        r"\bsc\.exe\b": "names the service control manager",
        r"New-Service": "registers a service",
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

    findings.extend(workflow_start_findings(code))
    return findings


def workflow_start_findings(code):
    """The workflow must drive ONE assembly and TWO starts, through the proof."""
    findings = []
    calls = re.findall(r"relocate-and-start\.ps1(.*?)(?:\n\s*\n|if \(\$LASTEXITCODE)",
                       code, re.DOTALL)
    if len(calls) != 1:
        findings.append("invokes the start proof %d times; the pair of starts belongs INSIDE "
                        "one invocation, so a third start cannot be added by copying a step"
                        % len(calls))
    body = "".join(calls)
    for argument in ("-RepoRoot", "-WorkDir", "-EvidencePath", "-SourceDateEpoch", "-OrasPath"):
        if argument not in body:
            findings.append("does not pass %s to the start proof" % argument)
    for argument in ORACLE_ARGUMENTS:
        if re.search(r"(^|\s)%s(\s|$)" % re.escape(argument), body):
            findings.append("passes %s; the fixture oracle is not the production door" % argument)
    if "assemble-server-zip.ps1" in code:
        findings.append("calls the frozen assembler directly; the start proof calls it, and a "
                        "second caller is a second acquisition path")
    epochs = set(re.findall(r"-SourceDateEpoch\s+(\$\{\{[^}]*\}\}|[^\s`]+)", code))
    if not epochs:
        findings.append("passes no -SourceDateEpoch")
    for epoch in epochs:
        if "steps.epoch.outputs.epoch" not in epoch:
            findings.append("takes its epoch from %r rather than the derived commit time" % epoch)
    if "git log -1 --format=%ct" not in code:
        findings.append("does not derive SOURCE_DATE_EPOCH from the commit")
    if "start-controls.py" not in code:
        findings.append("does not run the hostile controls")
    if "discoveredPort" not in code:
        findings.append("does not print the discovered ports")
    if "rootStatus" not in code:
        findings.append("does not print the readiness status codes")
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
    pinned checkout SHA, `persist-credentials: false`, `windows-latest`, the
    proof invocation -- so the only reason this file must be refused is the
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

    if copy_until(lambda line: re.match(r"^on:\s*$", line)):
        demote_block("  ")
        out.append("on: [push, pull_request]")
        applied.append("trigger")

    if copy_until(lambda line: re.match(r"^permissions:\s*$", line)):
        demote_block("  ")
        out.append("permissions: write-all")
        applied.append("top-level permissions")

    if copy_until(lambda line: re.match(r"^\s{4}permissions:\s*$", line)):
        demote_block("      ")
        out.append("    permissions: write-all")
        applied.append("job permissions")

    while index < total:
        out.append(lines[index])
        index += 1

    out.extend([
        "",
        "  exfiltrate:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - run: echo second job",
    ])
    applied.append("second job")
    return "\n".join(out) + "\n", applied


# A minimal file carrying only the shape the V1 mutation attacks, so the
# live-proof still works if this slice's real workflow is ever reformatted in a
# way the mutation cannot parse. It is the FALLBACK baseline, never the pin.
FROZEN_SHAPE_BASELINE = """\
name: baseline
on:
  pull_request:
    paths:
      - 'ci/windows/w2/**'
permissions:
  contents: read
jobs:
  relocate-start:
    runs-on: windows-latest
    permissions:
      contents: read
      packages: read
    steps:
      - uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0
        with:
          persist-credentials: false
      - name: controls
        run: python3 ci/windows/w2/start-controls.py
      - name: prove
        run: |
          ./ci/windows/w2/relocate-and-start.ps1 `
            -RepoRoot $PWD `
            -WorkDir (Join-Path $env:RUNNER_TEMP 'w2a3/work') `
            -EvidencePath (Join-Path $env:RUNNER_TEMP 'w2a3/relocate-start.json') `
            -SourceDateEpoch ${{ steps.epoch.outputs.epoch }} `
            -OrasPath (Join-Path $env:RUNNER_TEMP 'bin/oras.exe')
          if ($LASTEXITCODE -ne 0) { throw 'no' }
      - name: report
        run: |
          git log -1 --format=%ct
          echo discoveredPort rootStatus
"""


# ===========================================================================
# The controls
# ===========================================================================

def expect_oracle(report, name, real, mutant, description, mutation_name,
                  applied, expect_real, expect_mutant):
    """One observed rule: the real script decides one way, the mutant the other.

    `expect_real` and `expect_mutant` are the expected exit codes -- 0 accepted,
    1 refused. A mutation that no longer applies, or that does not flip the
    decision, is INERT rather than a smaller green suite.

    THE REAL DECISION IS GRADED FIRST, and the mutation's applicability only
    after it. The other order looks equivalent and is not: if the production
    path is ever defeated in exactly the way the mutant defeats it, the mutation
    string is then absent from it, so an applicability-first check reports
    "the mutation no longer applies" -- INERT, a harness complaint -- for what
    is really the defect this control exists to name. The suite fails either
    way, but only this order says WHY. It was observed: planting the
    any-status, no-liveness and any-200-entry defects in the production path
    graded S01, S04 and S06 INERT rather than RED.
    """
    real_code, real_output = real
    mutant_code, mutant_output = mutant
    if real_code != expect_real:
        report.record(name, "RED", "%s: the real proof exited %d, expected %d -- %s"
                      % (description, real_code, expect_real, real_output.strip()[-260:]))
        return False
    if 0 in applied:
        report.record(name, "INERT",
                      "the '%s' mutation no longer applies to the production path" % mutation_name)
        return False
    if mutant_code != expect_mutant:
        report.record(name, "INERT",
                      "%s: the '%s' mutant exited %d, expected %d, so the rule is not shown to "
                      "be load-bearing -- %s"
                      % (description, mutation_name, mutant_code, expect_mutant,
                         mutant_output.strip()[-260:]))
        return False
    report.record(name, "PASS", description)
    return True


def run_controls(work, report, only=None):
    def selected(name):
        return only is None or name in only

    proof_text = read_text(PROOF) if os.path.isfile(PROOF) else None
    workflow_text = read_text(WORKFLOW) if os.path.isfile(WORKFLOW) else None
    real = Oracle(PROOF)

    # --- S01: a server that only ever answers 503 is not ready ---------------
    if selected("S01"):
        path, applied = mutate(work, "any-status", MUTATIONS["any-status"])
        with Fixture(work, "always-503") as fixture:
            expect_oracle(
                report, "S01",
                real.readiness(fixture.base_url, fixture.pid),
                Oracle(path).readiness(fixture.base_url, fixture.pid),
                "the startup SetupServer answering 503 on every path is not a started server",
                "any-status", applied, expect_real=1, expect_mutant=0)

    # --- S02: readiness is keyed to '/', never to /System/Info/Public --------
    #
    # No mutant is needed and none would be honest: the trap is not a defeated
    # check, it is asking a DIFFERENT endpoint. So the same real rule is pointed
    # at both, against a fixture shaped exactly like the one W0 measured -- 200
    # on /System/Info/Public while '/' is still 503 -- and the two answers are
    # required to differ. The production path is then audited for which one it
    # actually asks.
    if selected("S02"):
        with Fixture(work, "setup-public-ok") as fixture:
            root_code, _ = real.readiness(fixture.base_url, fixture.pid)
            public_code, _ = real.readiness(
                fixture.base_url + "System/Info/Public", fixture.pid)
        findings = []
        if root_code != 1:
            findings.append("the real rule called a 503-on-'/' server ready")
        if public_code != 0:
            findings.append("the fixture does not reproduce W0's shape: /System/Info/Public did "
                            "not answer")
        if proof_text is not None:
            code = executable_text(PROOF, proof_text)
            if "System/Info/Public" in code:
                findings.append("the production path names /System/Info/Public")
            if '-BaseUri "$baseUri/"' not in code:
                findings.append("the production path does not wait on '/'")
        if findings:
            report.record("S02", "RED", "; ".join(findings))
        else:
            report.record("S02", "PASS",
                          "against a server answering 200 on /System/Info/Public while '/' is "
                          "still 503, readiness on '/' refuses and readiness on the public "
                          "endpoint accepts; the production path waits on '/' and never names "
                          "the other")

    # --- S03: a redirect is an answer ---------------------------------------
    if selected("S03"):
        path, applied = mutate(work, "require-200", MUTATIONS["require-200"])
        with Fixture(work, "redirect") as fixture:
            expect_oracle(
                report, "S03",
                real.readiness(fixture.base_url, fixture.pid),
                Oracle(path).readiness(fixture.base_url, fixture.pid),
                "'/' answering 302 is ready; a rule that required 200 would call the same "
                "healthy server dead",
                "require-200", applied, expect_real=0, expect_mutant=1)

    # --- S04: the process must survive three seconds after answering --------
    if selected("S04"):
        path, applied = mutate(work, "no-liveness", MUTATIONS["no-liveness"])
        real_answer = None
        mutant_answer = None
        with Fixture(work, "answer-then-die") as fixture:
            real_answer = real.readiness(fixture.base_url, fixture.pid)
        with Fixture(work, "answer-then-die") as fixture:
            mutant_answer = Oracle(path).readiness(fixture.base_url, fixture.pid)
        expect_oracle(
            report, "S04", real_answer, mutant_answer,
            "a process that answers and is gone three seconds later did not start; without the "
            "liveness gate the same run reads as started, which is how W0's no-FFmpeg control "
            "reported success",
            "no-liveness", applied, expect_real=1, expect_mutant=0)

    # --- S05: the port comes from the process --------------------------------
    if selected("S05"):
        path, applied = mutate(work, "port-default", MUTATIONS["port-default"])
        # The positive half first: a process that DID bind a port is answered
        # with that port. Decided BEFORE anything is recorded, so this control
        # reports one row rather than correcting an earlier one.
        bound_code, bound_output = real.port("45678")
        if bound_code != 0 or "45678" not in bound_output:
            report.record("S05", "RED", "a bound port was not returned: exit %d, %r"
                          % (bound_code, bound_output.strip()))
        else:
            expect_oracle(
                report, "S05", real.port(""), Oracle(path).port(""),
                "a process that bound no listening TCP port is refused, while a bound port is "
                "returned; a fallback to the compiled-in default answers with a port nothing "
                "is listening on",
                "port-default", applied, expect_real=1, expect_mutant=0)

    # --- S06: the entry document must reference the hashed bundle -----------
    if selected("S06"):
        path, applied = mutate(work, "any-200-entry", MUTATIONS["any-200-entry"])
        with Fixture(work, "no-bundle") as fixture:
            expect_oracle(
                report, "S06",
                real.bundle(fixture.base_url),
                Oracle(path).bundle(fixture.base_url),
                "a 200 that returns the setup page is not the Web client; a rule that accepted "
                "any 200 passes it",
                "any-200-entry", applied, expect_real=1, expect_mutant=0)

    # --- S07: the port is never read from configuration or the environment ---
    #
    # This cannot be observed behaviourally. A test can only fail to see a read,
    # which is indistinguishable from the read existing and being unreachable on
    # that input, so it is asserted against the executable text.
    if selected("S07"):
        if proof_text is None:
            report.record("S07", "RED", "there is no production path to audit")
        else:
            code = executable_text(PROOF, proof_text)
            findings = []
            if re.search(r"\b8096\b", code):
                findings.append("names the compiled-in default port 8096")
            for pattern, label in (
                    (r"(Get-Content|ReadAllText|\[xml\])[^\n]*network", "reads network.xml back"),
                    (r"\$env:[A-Za-z_]*PORT", "reads a port out of the environment"),
                    (r"ASPNETCORE_URLS", "names ASPNETCORE_URLS"),
                    (r"\$requestedPort\s*\)?\s*\"?\s*$", None)):
                if label and re.search(pattern, code, re.IGNORECASE | re.MULTILINE):
                    findings.append(label)
            # The requested port may be WRITTEN. It must never be what a URL is
            # built from: that is the difference between asking and knowing.
            for match in re.finditer(r"http://127\.0\.0\.1:\$([A-Za-z_][A-Za-z0-9_]*)", code):
                if match.group(1) != "port":
                    findings.append("builds a base URL from $%s rather than the resolved port"
                                    % match.group(1))
            if "Get-NetTCPConnection -OwningProcess" not in code:
                findings.append("does not ask the process which ports it bound")
            if "Resolve-ServerPort" not in code:
                findings.append("has no port resolution to audit")
            # The refusal must be a refusal, not a default.
            resolve = code.split("function Resolve-ServerPort", 1)[-1].split("\nfunction ", 1)[0]
            if "Deny 'port'" not in resolve:
                findings.append("Resolve-ServerPort does not refuse an empty answer")
            if re.search(r"return\s+\d+", resolve):
                findings.append("Resolve-ServerPort returns a literal port")
            if findings:
                report.record("S07", "RED", "; ".join(findings))
            else:
                report.record("S07", "PASS",
                              "the port is asked of the process, an empty answer is a named "
                              "refusal, no literal default is returned, and no URL is built "
                              "from the requested value")

    # --- S08: the two starts get their own state ----------------------------
    if selected("S08"):
        if proof_text is None:
            report.record("S08", "RED", "there is no production path to audit")
        else:
            code = executable_text(PROOF, proof_text)
            findings = []
            states = set(re.findall(r"Combine\(\$work,\s*'(state-[^']+)'\)", code))
            if len(states) < 2:
                findings.append("does not give the two starts different state roots (%s)"
                                % sorted(states))
            for flag in ("--datadir", "--configdir", "--cachedir", "--logdir"):
                if flag not in code:
                    findings.append("does not pass %s" % flag)
            for flag in ("--webdir", "--ffmpeg"):
                if flag not in code:
                    findings.append("does not pass %s" % flag)
            if "$sharedState" not in code or "Deny 'state'" not in code:
                findings.append("does not refuse a shared state directory")
            # --webdir and --ffmpeg must come from the relocated tree.
            if "Join-Path $PackageRoot $WEB_SUBDIR" not in code:
                findings.append("does not take --webdir from the relocated package")
            if "Join-Path $PackageRoot ($FFMPEG_RELATIVE_EXE" not in code:
                findings.append("does not take --ffmpeg from the relocated package")
            if findings:
                report.record("S08", "RED", "; ".join(findings))
            else:
                report.record("S08", "PASS",
                              "each start is given its own datadir/configdir/cachedir/logdir, a "
                              "shared one is a named refusal, and --webdir and --ffmpeg point "
                              "inside the relocated tree")

    # --- S09: a start from the assembler's own tree is refused --------------
    if selected("S09"):
        if proof_text is None:
            report.record("S09", "RED", "there is no production path to audit")
        else:
            code = executable_text(PROOF, proof_text)
            findings = []
            if "Assert-NotBuildTree" not in code:
                findings.append("has no build-tree assertion")
            if "Deny 'build-tree'" not in code:
                findings.append("does not refuse a start from the build tree")
            calls = len(re.findall(r"^\s*Assert-NotBuildTree\s", code, re.MULTILINE))
            if calls < 2:
                findings.append("asserts the build tree for %d of the two starts" % calls)
            if "$assemblyWork" not in code or "$assemblyOut" not in code:
                findings.append("does not name the assembler's own directories as forbidden")
            if findings:
                report.record("S09", "RED", "; ".join(findings))
            else:
                report.record("S09", "PASS",
                              "both starts are asserted to be outside the assembler's work and "
                              "output directories, and a start inside either is a named refusal")

    # --- S10: extracted once, and the second start is a MOVE ----------------
    if selected("S10"):
        if proof_text is None:
            report.record("S10", "RED", "there is no production path to audit")
        else:
            code = executable_text(PROOF, proof_text)
            findings = []
            extracts = len(re.findall(r"ExtractToDirectory|Expand-Archive", code))
            if extracts != 1:
                findings.append("extracts the archive %d times" % extracts)
            if "Move-Item" not in code:
                findings.append("does not move the tree")
            if "$extractions -ne 1" not in code:
                findings.append("does not require exactly one extraction")
            if "-cne $firstExeDigest" not in code:
                findings.append("does not hash the exe on both sides of the move")
            if "still exists after the move" not in code:
                findings.append("does not require the first location to be gone")
            if "$firstDepth -eq $secondDepth" not in code:
                findings.append("does not require a different depth")
            if findings:
                report.record("S10", "RED", "; ".join(findings))
            else:
                report.record("S10", "PASS",
                              "the archive is opened exactly once, the second start is a "
                              "Move-Item to a different depth, the exe is hashed on both sides "
                              "and the first location must be gone")

    # --- S11: no caller-supplied identity on the production path ------------
    if selected("S11"):
        if proof_text is None:
            report.record("S11", "RED", "there is no production path to audit")
        else:
            findings = []
            block = proof_text.split("param(", 1)[-1].split("\n)", 1)[0]
            for forbidden in FORBIDDEN_PARAMETERS:
                if re.search(r"\%s\b" % re.escape(forbidden), block):
                    findings.append("takes %s" % forbidden)
            code = executable_text(PROOF, proof_text)
            if DESCRIPTIVE_TAG in code:
                findings.append("names the descriptive tag, which is never a trust boundary")
            for pattern, label in (
                    (r"Invoke-WebRequest\s+[^\n]*https?://(?!127\.0\.0\.1)", "fetches over the network"),
                    (r"gh\s+run\s+download", "downloads a run artifact"),
                    (r"actions/download-artifact", "downloads an Actions artifact"),
                    (r"\bdocker\b", "names docker"),
                    (r"\bpodman\b", "names podman"),
                    (r"New-Service|sc\.exe", "registers a service")):
                if re.search(pattern, code):
                    findings.append(label)
            if "assemble-server-zip.ps1" not in code:
                findings.append("does not call the frozen assembler")
            # The oracle must reach no registry and start nothing. The slice is
            # terminated at the production block's own `try {`, the only one at
            # column 0. A `# ===` banner cannot be the terminator: comments are
            # BLANKED by executable_text, so that split never matches and the
            # slice would swallow the whole production path instead -- which
            # graded this control RED for a defect in the control.
            oracle = re.split(r"^try \{", code.split("function Invoke-Oracle", 1)[-1],
                              maxsplit=1, flags=re.MULTILINE)[0]
            for pattern, label in ((r"Start-Process", "starts a process"),
                                   (r"assemble-server-zip", "assembles"),
                                   (r"WriteAllText|Set-Content|Out-File", "writes a file")):
                if re.search(pattern, oracle):
                    findings.append("the oracle %s" % label)
            if findings:
                report.record("S11", "RED", "; ".join(findings))
            else:
                report.record("S11", "PASS",
                              "the param block takes no reference, digest, tag, run, archive or "
                              "port; the archive comes only from the frozen assembler; and the "
                              "oracle starts nothing, assembles nothing and writes nothing")

    # --- S12: the ruling, the proof, the manifest and the workflow agree ----
    if selected("S12"):
        findings = []
        if workflow_text is None:
            findings.append("there is no workflow to read")
        else:
            code = executable_text(WORKFLOW, workflow_text)
            if WEB_PAYLOAD_SHA256 not in code:
                findings.append("the workflow does not state the accepted web payload digest")
            if ACCEPTED_RUNTIME_SHA256 not in code:
                findings.append("the workflow does not state the accepted runtime digest")
            if "provenance.json" not in code:
                findings.append("the workflow never reads the manifest that shipped in the "
                                "archive it started, so the restated digests are decoration")
        if os.path.isfile(ACCEPTED_JSON):
            accepted = json.loads(read_text(ACCEPTED_JSON))
            if accepted.get("runtimeSha256") != ACCEPTED_RUNTIME_SHA256:
                findings.append("the acceptance manifest pins runtime %s"
                                % accepted.get("runtimeSha256"))
        else:
            findings.append("the acceptance manifest is missing")
        if os.path.isfile(ASSEMBLER):
            assembler = executable_text(ASSEMBLER)
            if WEB_PAYLOAD_SHA256 not in assembler:
                findings.append("the frozen assembler does not pin the same web payload digest")
            if ACCEPTED_RUNTIME_SHA256 not in assembler:
                findings.append("the frozen assembler does not pin the same runtime digest")
        else:
            findings.append("the frozen assembler is missing")
        if findings:
            report.record("S12", "RED", "; ".join(findings))
        else:
            report.record("S12", "PASS",
                          "the ruling's two digests, the committed acceptance manifest, the "
                          "frozen assembler and this workflow all state the same identity, and "
                          "the workflow checks them against the manifest inside the started tree")

    # --- S13: no artifact, cache or dispatch; the oracle is not the door ----
    if selected("S13"):
        if workflow_text is None:
            report.record("S13", "RED", "there is no workflow to audit")
        else:
            code = executable_text(WORKFLOW, workflow_text)
            findings = []
            for pattern, label in (
                    (r"actions/upload-artifact", "uploads an Actions artifact"),
                    (r"actions/download-artifact", "downloads an Actions artifact"),
                    (r"actions/cache", "uses an Actions cache"),
                    (r"workflow_dispatch", "declares workflow_dispatch"),
                    (r"gh\s+run\s+download", "downloads a run artifact"),
                    (r"\bdocker\b|\bpodman\b|\bnerdctl\b", "names a container engine")):
                if re.search(pattern, code):
                    findings.append(label)
            for argument in ORACLE_ARGUMENTS:
                if re.search(r"(^|\s)%s(\s|$)" % re.escape(argument), code):
                    findings.append("passes %s" % argument)
            # Inert-proof: the same audit MUST fire on a planted violation.
            planted = code + "\n      - uses: actions/upload-artifact@v4\n" \
                             "      - run: ./ci/windows/w2/relocate-and-start.ps1 -Oracle port\n"
            planted_findings = []
            if not re.search(r"actions/upload-artifact", planted):
                planted_findings.append("upload-artifact")
            if not re.search(r"(^|\s)-Oracle(\s|$)", planted):
                planted_findings.append("-Oracle")
            if planted_findings:
                report.record("S13", "INERT",
                              "the audit cannot detect a planted %s" % ", ".join(planted_findings))
            elif findings:
                report.record("S13", "RED", "; ".join(findings))
            else:
                report.record("S13", "PASS",
                              "no artifact upload or download, no cache, no dispatch, no "
                              "container engine, and the workflow passes no -Oracle argument: "
                              "the fixture door is not the production door")

    # --- S14: the workflow is the authorised one, byte for byte -------------
    if selected("S14"):
        control_S14(work, report, workflow_text)

    # --- S15: the five frozen inputs are unmodified -------------------------
    if selected("S15"):
        findings = []
        for relative, expected in sorted(FROZEN_PINS.items()):
            path = os.path.join(REPO_ROOT, relative)
            if not os.path.isfile(path):
                findings.append("%s is missing" % relative)
                continue
            actual = sha256_file(path)
            if actual != expected:
                findings.append("%s is %s, pinned %s" % (relative, actual[:16], expected[:16]))
        # Inert-proof: the pin must distinguish a one-byte change.
        if not findings:
            probe = os.path.join(work, "pin-probe")
            shutil.copyfile(os.path.join(REPO_ROOT, "ci/windows/w2/assemble-server-zip.ps1"), probe)
            with open(probe, "ab") as handle:
                handle.write(b"\n")
            if sha256_file(probe) == FROZEN_PINS["ci/windows/w2/assemble-server-zip.ps1"]:
                report.record("S15", "INERT", "the content pin does not distinguish a change")
                return
        if findings:
            report.record("S15", "RED", "; ".join(findings))
        else:
            report.record("S15", "PASS",
                          "all %d frozen inputs are byte-identical to their pins, including the "
                          "assembler this slice calls and does not edit" % len(FROZEN_PINS))

    # --- S16: no service, no publication, no third start --------------------
    if selected("S16"):
        if proof_text is None or workflow_text is None:
            report.record("S16", "RED", "there is no production path to audit")
        else:
            code = executable_text(PROOF, proof_text)
            wf = executable_text(WORKFLOW, workflow_text)
            findings = []
            for pattern, label in (
                    (r"New-Service|sc\.exe|Register-Service|Set-Service", "registers a service"),
                    (r"--service\b", "passes --service"),
                    (r"\.reefin", "touches a .reefin marker"),
                    (r"gh\s+release|softprops/action-gh-release", "creates a release")):
                for name, body in (("the proof", code), ("the workflow", wf)):
                    if re.search(pattern, body):
                        findings.append("%s %s" % (name, label))
            starts = len(re.findall(r"^\s*\$\w+ = Invoke-ServerStart\s", code, re.MULTILINE))
            if starts != 2:
                findings.append("the proof performs %d starts; the pair is the proof and a third "
                                "is not authorised" % starts)
            if "serviceRegistered = $false" not in code or "published = $false" not in code:
                findings.append("the evidence does not record that nothing was registered or "
                                "published")
            if findings:
                report.record("S16", "RED", "; ".join(findings))
            else:
                report.record("S16", "PASS",
                              "exactly two starts, no service registration, no --service, no "
                              ".reefin rename and no release; the evidence says so in its own "
                              "fields")

    # --- S17: the workflow watches the whole ci/windows/w2 tree -------------
    if selected("S17"):
        if workflow_text is None:
            report.record("S17", "RED", "there is no workflow to audit")
        else:
            code = executable_text(WORKFLOW, workflow_text)
            findings = []
            if "'ci/windows/w2/**'" not in code:
                findings.append("does not watch the whole ci/windows/w2 tree; W2-A1-V1 NB-1 is "
                                "that a file added there could otherwise be reviewed without "
                                "ever running the proof that covers it")
            for required in ("ci/windows/runtime-retention/consume.ps1",
                             "ci/windows/runtime-retention/accepted-runtime.json",
                             "SharedVersion.cs"):
                if required not in code:
                    findings.append("does not watch %s" % required)
            # Inert-proof: the rule must fire on a filter that names files.
            narrowed = code.replace("'ci/windows/w2/**'",
                                    "'ci/windows/w2/relocate-and-start.ps1'")
            if "'ci/windows/w2/**'" in narrowed:
                report.record("S17", "INERT", "the directory rule cannot be narrowed")
            elif findings:
                report.record("S17", "RED", "; ".join(findings))
            else:
                report.record("S17", "PASS",
                              "the filter watches the whole authored directory plus the two "
                              "frozen files the assembly runs and the one version source that "
                              "names the archive")


def control_S14(work, report, workflow_text):
    """The workflow is the authorised one, byte for byte, and the audit bites."""
    if workflow_text is None:
        report.record("S14", "RED", "there is no workflow to pin")
        return

    findings = []
    actual = sha256_file(WORKFLOW)
    if actual != WORKFLOW_SHA256:
        findings.append("the workflow is %s, pinned %s" % (actual[:16], WORKFLOW_SHA256[:16]))
    findings.extend(audit_workflow(workflow_text))

    # --- live-proof 1: the V1 permission mutation must be refused ------------
    expected_edits = ["trigger", "top-level permissions", "job permissions", "second job"]
    mutant, applied = v1_permission_mutation(workflow_text)
    if applied != expected_edits:
        findings.append("no longer has the authorised trigger/permission/job shape; the V1 "
                        "mutation applies only %s" % (applied or "nothing"))
        if audit_workflow(FROZEN_SHAPE_BASELINE):
            report.record("S14", "INERT", "the live-proof baseline does not audit clean: %s"
                          % audit_workflow(FROZEN_SHAPE_BASELINE))
            return
        mutant, applied = v1_permission_mutation(FROZEN_SHAPE_BASELINE)
        if applied != expected_edits:
            report.record("S14", "INERT",
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
        report.record("S14", "INERT",
                      "the workflow audit does not refuse %s" % ", ".join(sorted(unproven)))
        return
    if hashlib.sha256(mutant.encode("utf-8")).hexdigest() == WORKFLOW_SHA256:
        report.record("S14", "INERT", "the content pin does not distinguish the mutation")
        return

    # --- live-proof 2: a planted violation of the other named rules ----------
    planted = ("on:\n  push:\n  workflow_dispatch:\npermissions:\n  packages: write\n"
               "jobs:\n  x:\n    steps:\n      - uses: actions/checkout@v4\n"
               "      - uses: actions/upload-artifact@v4\n"
               "      - run: docker pull ghcr.io/x/y\n"
               "      - run: New-Service -Name tesserafin\n")
    planted_findings = audit_workflow(planted)
    for label in ("declares packages: write", "declares workflow_dispatch", "declares push",
                  "unpinned action actions/checkout@v4", "uploads an Actions artifact",
                  "names docker", "registers a service"):
        if label not in planted_findings:
            report.record("S14", "INERT",
                          "the workflow audit does not detect a planted violation: %s" % label)
            return

    if findings:
        report.record("S14", "RED", "; ".join(findings))
    else:
        report.record("S14", "PASS",
                      "byte-identical to the pinned workflow, and its executable text declares "
                      "one on: block triggering pull_request only, one jobs: block with the one "
                      "authorised job, contents: read plus packages: read and no write grant at "
                      "either scope, no write-all, pinned actions, no persisted credentials, no "
                      "dispatch, no artifact, no cache, no container engine, no service "
                      "registration, and one invocation of the start proof that prints both "
                      "ports and both readiness status codes")


# ===========================================================================

def _repository_fingerprint():
    fingerprint = {}
    for path in (PROOF, WORKFLOW, ASSEMBLER, WEB_CONSUMER, TREE_DIGEST, RUNTIME_CONSUMER,
                 ACCEPTED_JSON, os.path.abspath(__file__)):
        if os.path.isfile(path):
            fingerprint[os.path.relpath(path, REPO_ROOT)] = sha256_file(path)
    return fingerprint


def main(argv):
    global POWERSHELL
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--only", action="append", help="run only the named control(s)")
    args = parser.parse_args(argv)

    POWERSHELL = find_powershell()
    if not POWERSHELL:
        # Not a skip. Every observed refusal in this suite is driven through the
        # real script, so without an interpreter the suite proves nothing and
        # must say so rather than reporting a smaller green run.
        print("W2-A3 hostile controls")
        print("  SETUP RED   no PowerShell on PATH, so the real decision functions cannot be "
              "driven; install pwsh 7 or newer")
        return 1

    work = tempfile.mkdtemp(prefix="w2a3-controls-")
    try:
        before = _repository_fingerprint()
        print("W2-A3 hostile controls")
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
        print("W2-A3 controls: %d PASS, %d RED, %d INERT in %.1fs"
              % (totals["PASS"], totals["RED"], totals["INERT"], time.time() - started))
        return 0 if (totals["RED"] == 0 and totals["INERT"] == 0) else 1
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
