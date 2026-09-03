#!/usr/bin/env python3
"""Hostile controls for the daemonless Web payload consumer (W2-A0, #256).

A consumer that only ever runs against the one image it was written for proves
nothing: every one of its refusals is unreached code, and an unreached refusal
is indistinguishable from a missing one. So this suite builds disposable OCI
images in a temporary directory, serves them from a loopback registry that
speaks just enough of the distribution API, and drives the real
`consume-web-payload.ps1` against them.

Every negative control asserts the SPECIFIC refusal, by the property name the
consumer denies on, not merely a non-zero exit. A control that passes because
the script failed for an unrelated reason is a control that would keep passing
after the property it names was removed.

Nothing here touches the network, the repository working tree, or any real
registry. The single real pull is the hosted job's, in
.github/workflows/w2-windows-web-payload.yml.

    python3 ci/windows/w2/web-payload-controls.py            # everything
    python3 ci/windows/w2/web-payload-controls.py --only C11 # one control
"""

import argparse
import base64
import gzip
import hashlib
import http.server
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
CONSUMER = os.path.join(HERE, "consume-web-payload.ps1")
DIGEST_TOOL = os.path.join(HERE, "pkg-tree-digest.py")
WORKFLOW = os.path.join(REPO_ROOT, ".github", "workflows", "w2-windows-web-payload.yml")

sys.path.insert(0, HERE)
import importlib.util as _importlib_util

_spec = _importlib_util.spec_from_file_location("pkg_tree_digest", DIGEST_TOOL)
pkg_tree_digest = _importlib_util.module_from_spec(_spec)
_spec.loader.exec_module(pkg_tree_digest)

# ---------------------------------------------------------------------------
# The frozen contract. These are the ruling's values; C21 asserts that the
# PowerShell consumer's own constants still say exactly this.
# ---------------------------------------------------------------------------
ACCEPTED_REGISTRY = "ghcr.io"
ACCEPTED_REPOSITORY = "tesserafin-project/tesserafin-web-assets"
ACCEPTED_REFERENCE = "sha256:6150380052c8a3a154a8a25a9f40a741175a7563afdf89284f9c1f46d3042a6c"
ACCEPTED_TREE_DIGEST = "4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f"
ACCEPTED_REVISION = "a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a"

# C19's oracle. This is the digest GNU tar 1.35 produces for the synthetic tree
# `oracle_tree()` builds, recorded on Linux. On a Windows runner there is no GNU
# tar to ask, so the recorded value is what the Python implementation is held
# against; on a host that HAS GNU tar the control additionally re-derives it, so
# the constant cannot quietly rot into whatever the implementation happens to
# produce.
ORACLE_EPOCH = 1785852822
ORACLE_DIGEST = "8fb950a4a062c72d2fd913750f4bd3eb96193c9c4768dec35dd148c9f6b08a50"

OCI_MANIFEST = "application/vnd.oci.image.manifest.v1+json"
OCI_INDEX = "application/vnd.oci.image.index.v1+json"
OCI_CONFIG = "application/vnd.oci.image.config.v1+json"
OCI_LAYER_GZIP = "application/vnd.oci.image.layer.v1.tar+gzip"

FIXTURE_REVISION = "1111111111111111111111111111111111111111"
FIXTURE_EPOCH = 1700000000
FIXTURE_TOKEN = "fixture-registry-credential-do-not-reuse"


# ===========================================================================
# Building disposable images
# ===========================================================================

def _sha256(data):
    return "sha256:" + hashlib.sha256(data).hexdigest()


def build_layer(entries, epoch=FIXTURE_EPOCH):
    """A gzipped tar layer. `entries` are (name, kind, payload) triples.

    kind is 'file', 'dir', 'symlink', 'hardlink', 'fifo' or 'whiteout'; the
    unsafe kinds exist precisely so the consumer can be made to meet them.
    """
    raw = io.BytesIO()
    archive = tarfile.open(fileobj=raw, mode="w", format=tarfile.GNU_FORMAT)
    for name, kind, payload in entries:
        info = tarfile.TarInfo(name)
        info.mtime = epoch
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        if kind == "dir":
            info.type = tarfile.DIRTYPE
            info.mode = 0o755
            archive.addfile(info)
        elif kind in ("file", "whiteout"):
            data = payload if isinstance(payload, bytes) else str(payload).encode("utf-8")
            info.type = tarfile.REGTYPE
            info.mode = 0o644
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
        elif kind == "symlink":
            info.type = tarfile.SYMTYPE
            info.linkname = payload
            archive.addfile(info)
        elif kind == "hardlink":
            info.type = tarfile.LNKTYPE
            info.linkname = payload
            archive.addfile(info)
        elif kind == "fifo":
            info.type = tarfile.FIFOTYPE
            archive.addfile(info)
        else:
            raise ValueError("unknown fixture entry kind %r" % kind)
    archive.close()
    plain = raw.getvalue()
    # mtime=0 so the compressed bytes, and therefore the layer digest, are a
    # function of the content alone.
    compressed = io.BytesIO()
    with gzip.GzipFile(fileobj=compressed, mode="wb", mtime=0) as handle:
        handle.write(plain)
    return plain, compressed.getvalue()


def build_image(layer_entry_sets, layer_media_type=OCI_LAYER_GZIP,
                config_media_type=OCI_CONFIG, manifest_media_type=OCI_MANIFEST,
                break_layer_size=None, break_layer_bytes=None,
                break_manifest_bytes=None, break_diff_id=None,
                reverse_layers=False):
    """Return (reference, manifest_bytes, manifest_content_type, blobs)."""
    layers = [build_layer(entries) for entries in layer_entry_sets]
    if reverse_layers:
        layers = list(reversed(layers))

    blobs = {}
    descriptors = []
    diff_ids = []
    for index, (plain, compressed) in enumerate(layers):
        digest = _sha256(compressed)
        blobs[digest] = compressed
        diff_ids.append(_sha256(plain))
        size = len(compressed)
        if break_layer_size is not None and break_layer_size == index:
            size = size + 1
        descriptors.append({"mediaType": layer_media_type, "digest": digest, "size": size})
    if break_diff_id is not None:
        diff_ids[break_diff_id] = "sha256:" + ("0" * 64)
    if break_layer_bytes is not None:
        digest = descriptors[break_layer_bytes]["digest"]
        original = blobs[digest]
        # Same length, different content: the size gate must not be what
        # catches this one.
        blobs[digest] = bytes([original[0] ^ 0xFF]) + original[1:]

    config = json.dumps({
        "architecture": "amd64",
        "os": "linux",
        "rootfs": {"type": "layers", "diff_ids": diff_ids},
    }, separators=(",", ":")).encode("utf-8")
    config_digest = _sha256(config)
    blobs[config_digest] = config

    manifest = json.dumps({
        "schemaVersion": 2,
        "mediaType": manifest_media_type,
        "config": {"mediaType": config_media_type, "digest": config_digest, "size": len(config)},
        "layers": descriptors,
    }, separators=(",", ":")).encode("utf-8")
    reference = _sha256(manifest)
    if break_manifest_bytes is not None:
        manifest = break_manifest_bytes
    return reference, manifest, manifest_media_type, blobs


def web_layers(files=None, revision=FIXTURE_REVISION, epoch=FIXTURE_EPOCH):
    """The shape the real payload has: licenses/, metadata/, web/."""
    document = json.dumps({
        "repository": "https://example.invalid/fixture",
        "revision": revision,
        "version": "0.0.0-fixture",
        "license": "GPL-2.0-or-later",
        "sourceDateEpoch": str(epoch),
    }, indent=2).encode("utf-8")
    entries = [
        ("licenses", "dir", None),
        ("licenses/LICENSE", "file", "fixture licence\n"),
        ("metadata", "dir", None),
        ("metadata/web-revision.json", "file", document),
        ("web", "dir", None),
    ]
    for name, content in (files or [("index.html", "<!doctype html>fixture\n")]):
        entries.append(("web/" + name, "file", content))
    return entries


# ===========================================================================
# The loopback registry
# ===========================================================================

class FixtureRegistry:
    """Enough of the OCI distribution API to be refused by, or satisfy, the consumer."""

    def __init__(self, repository, reference, manifest, manifest_type, blobs,
                 require_auth=True):
        self.repository = repository
        self.reference = reference
        self.manifest = manifest
        self.manifest_type = manifest_type
        self.blobs = blobs
        self.require_auth = require_auth
        self.requests = []
        registry = self

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *args):
                pass

            def _send(self, status, body, content_type="application/json"):
                self.send_response(status)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_GET(self):
                registry.requests.append(self.path)
                auth = self.headers.get("Authorization", "")
                if self.path.startswith("/token"):
                    if registry.require_auth and not auth.startswith("Basic "):
                        return self._send(401, b'{"errors":[{"code":"UNAUTHORIZED"}]}')
                    if auth.startswith("Basic "):
                        try:
                            presented = base64.b64decode(auth.split(" ", 1)[1]).decode("utf-8")
                        except Exception:
                            return self._send(400, b'{"errors":[{"code":"BAD_CREDENTIAL"}]}')
                        # A real registry decides whether the credential grants
                        # pull. A fixture that hands out a bearer to anyone
                        # cannot be refused by, so C07 would never reach the
                        # authentication boundary it names.
                        if registry.require_auth and presented.split(":", 1)[-1] != FIXTURE_TOKEN:
                            return self._send(401, b'{"errors":[{"code":"DENIED"}]}')
                    return self._send(200, json.dumps({"token": FIXTURE_TOKEN}).encode())
                if registry.require_auth and auth != "Bearer " + FIXTURE_TOKEN:
                    return self._send(401, b'{"errors":[{"code":"UNAUTHORIZED"}]}')
                prefix = "/v2/%s/" % registry.repository
                if not self.path.startswith(prefix):
                    return self._send(404, b'{"errors":[{"code":"NAME_UNKNOWN"}]}')
                rest = self.path[len(prefix):]
                if rest.startswith("manifests/"):
                    return self._send(200, registry.manifest, registry.manifest_type)
                if rest.startswith("blobs/"):
                    digest = rest[len("blobs/"):]
                    if digest not in registry.blobs:
                        return self._send(404, b'{"errors":[{"code":"BLOB_UNKNOWN"}]}')
                    return self._send(200, registry.blobs[digest], "application/octet-stream")
                return self._send(404, b'{"errors":[{"code":"NOT_FOUND"}]}')

        self._server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)

    def __enter__(self):
        self._thread.start()
        return self

    def __exit__(self, *exc):
        self._server.shutdown()
        self._server.server_close()
        self._thread.join(timeout=5)
        return False

    @property
    def authority(self):
        host, port = self._server.server_address[0], self._server.server_address[1]
        return "%s:%d" % (host, port)


# ===========================================================================
# Driving the consumer
# ===========================================================================

def powershell():
    for candidate in ("pwsh", "powershell"):
        found = shutil.which(candidate)
        if found:
            return found
    raise RuntimeError("no PowerShell on PATH; W2-A0's controls cannot run")


def run_consumer(registry_authority, repository, reference, output, *,
                 tree_digest, revision, token=FIXTURE_TOKEN, extra=None,
                 payload_root="web", revision_path="metadata/web-revision.json",
                 fixture=True, scheme="http", evidence=None):
    command = [
        powershell(), "-NoProfile", "-NonInteractive", "-File", CONSUMER,
        "-Registry", registry_authority,
        "-Repository", repository,
        "-Reference", reference,
        "-ExpectedTreeDigest", tree_digest,
        "-ExpectedRevision", revision,
        "-PayloadRoot", payload_root,
        "-RevisionPath", revision_path,
        "-OutputPath", output,
        "-Scheme", scheme,
        "-PythonPath", sys.executable,
    ]
    if fixture:
        command.append("-Fixture")
    if evidence:
        command.extend(["-EvidencePath", evidence])
    if extra:
        command.extend(extra)
    environment = dict(os.environ)
    if token is None:
        environment.pop("GHCR_TOKEN", None)
    else:
        environment["GHCR_TOKEN"] = token
    completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               env=environment)
    return completed


DENY = re.compile(r"W2-A0 DENY \[([a-z-]+)\]")


def denial_property(completed):
    text = (completed.stdout + completed.stderr).decode("utf-8", "replace")
    match = DENY.search(text)
    return match.group(1) if match else None


# ===========================================================================
# The controls
# ===========================================================================

class Report:
    def __init__(self):
        self.rows = []

    def record(self, name, status, detail):
        self.rows.append((name, status, detail))
        marker = {"PASS": "PASS", "RED": "RED ", "INERT": "INERT"}[status]
        print("  %s  %-4s  %s" % (name, marker, detail), flush=True)

    def counts(self):
        totals = {"PASS": 0, "RED": 0, "INERT": 0}
        for _, status, _ in self.rows:
            totals[status] += 1
        return totals


def expect_denial(report, name, completed, expected, description, workdir=None, output=None):
    """A negative control: non-zero exit, the NAMED refusal, and no output left."""
    actual = denial_property(completed)
    if completed.returncode == 0:
        report.record(name, "RED", "%s: the consumer accepted it (exit 0)" % description)
        return False
    if actual is None:
        text = (completed.stdout + completed.stderr).decode("utf-8", "replace").strip()
        report.record(name, "RED", "%s: failed without a W2-A0 denial: %s" % (description, text[-200:]))
        return False
    if actual != expected:
        report.record(name, "RED", "%s: denied on '%s', expected '%s'" % (description, actual, expected))
        return False
    if output is not None and os.path.exists(output):
        report.record(name, "RED", "%s: denied on '%s' but left output behind" % (description, actual))
        return False
    if workdir is not None:
        leftovers = [n for n in os.listdir(workdir) if n.startswith(".w2a0-")]
        if leftovers:
            report.record(name, "RED", "%s: left staging directories %s" % (description, leftovers))
            return False
    report.record(name, "PASS", "%s -> DENY [%s]" % (description, actual))
    return True


def with_fixture(work, layer_sets, **image_kwargs):
    reference, manifest, manifest_type, blobs = build_image(layer_sets, **image_kwargs)
    return FixtureRegistry("fixtures/web", reference, manifest, manifest_type, blobs), reference


def expected_digest_for(work, layer_sets, payload_root="web", epoch=FIXTURE_EPOCH):
    """Materialise the fixture's payload locally and hash it canonically."""
    root = tempfile.mkdtemp(dir=work, prefix="expected-")
    for entries in layer_sets:
        for name, kind, payload in entries:
            target = os.path.join(root, name.replace("/", os.sep))
            base = os.path.basename(name)
            if base.startswith(".wh."):
                victim = os.path.join(os.path.dirname(target), base[4:])
                if os.path.isdir(victim):
                    shutil.rmtree(victim)
                elif os.path.exists(victim):
                    os.unlink(victim)
                continue
            if kind == "dir":
                os.makedirs(target, exist_ok=True)
            else:
                os.makedirs(os.path.dirname(target), exist_ok=True)
                data = payload if isinstance(payload, bytes) else str(payload).encode("utf-8")
                with open(target, "wb") as handle:
                    handle.write(data)
    return pkg_tree_digest.tree_digest(os.path.join(root, payload_root), epoch)


def scan_for(paths, pattern, code_only=False):
    """Every (path, line number, line) a pattern matches.

    With `code_only`, comments and docstrings are blanked first: what matters is
    whether a file DOES something, not whether it explains why it does not.
    """
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
                # on Windows that can sit on a different drive from the checkout:
                # `relpath` cannot relate the two and raises. The match has
                # already been made, so this is a display limit, not a result.
                # Fall back to the path as given rather than lose the finding.
                try:
                    shown = os.path.relpath(path, REPO_ROOT)
                except ValueError:
                    shown = os.fspath(path)
                hits.append((shown, number, line.rstrip()))
    return hits


def run_controls(work, report, only=None):
    def selected(name):
        return only is None or name in only

    # --- C01 -----------------------------------------------------------------
    if selected("C01"):
        out = os.path.join(work, "c01", "web")
        completed = run_consumer("127.0.0.1:1", "fixtures/web", "latest", out,
                                 tree_digest=ACCEPTED_TREE_DIGEST, revision=ACCEPTED_REVISION)
        expect_denial(report, "C01", completed, "immutable-reference",
                      "a tag-only reference", output=out)

    # --- C02 -----------------------------------------------------------------
    if selected("C02"):
        out = os.path.join(work, "c02", "web")
        completed = run_consumer("127.0.0.1:1", "fixtures/web", "sha256:dead", out,
                                 tree_digest=ACCEPTED_TREE_DIGEST, revision=ACCEPTED_REVISION)
        expect_denial(report, "C02", completed, "immutable-reference",
                      "a short digest", output=out)
        # The accepted contract itself must be unreachable without the digest form.
        out = os.path.join(work, "c02b", "web")
        completed = run_consumer("127.0.0.1:1", "fixtures/web",
                                 ACCEPTED_REFERENCE.upper(), out,
                                 tree_digest=ACCEPTED_TREE_DIGEST, revision=ACCEPTED_REVISION)
        expect_denial(report, "C02b", completed, "immutable-reference",
                      "an uppercase digest", output=out)

    # --- C03 -----------------------------------------------------------------
    if selected("C03"):
        # Not merely a different manifest: bytes that are not JSON at all. Only a
        # consumer that hashes BEFORE parsing can deny on the digest here.
        registry, reference = with_fixture(work, [web_layers()],
                                           break_manifest_bytes=b"this is not json {{{")
        with registry:
            out = os.path.join(work, "c03", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION)
            expect_denial(report, "C03", completed, "manifest-digest",
                          "a manifest that is neither the requested digest nor JSON",
                          workdir=os.path.dirname(out), output=out)

    # --- C04 -----------------------------------------------------------------
    if selected("C04"):
        registry, reference = with_fixture(work, [web_layers()], break_layer_bytes=0)
        with registry:
            out = os.path.join(work, "c04", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION)
            expect_denial(report, "C04", completed, "descriptor-digest",
                          "a layer whose bytes were substituted at the same length",
                          workdir=os.path.dirname(out), output=out)

    # --- C05 -----------------------------------------------------------------
    if selected("C05"):
        registry, reference = with_fixture(work, [web_layers()], break_layer_size=0)
        with registry:
            out = os.path.join(work, "c05", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION)
            expect_denial(report, "C05", completed, "descriptor-size",
                          "a layer descriptor that lies about its size",
                          workdir=os.path.dirname(out), output=out)

    # --- C06 -----------------------------------------------------------------
    if selected("C06"):
        registry, reference = with_fixture(
            work, [web_layers()], layer_media_type="application/vnd.in-toto+json")
        with registry:
            out = os.path.join(work, "c06", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION)
            expect_denial(report, "C06", completed, "media-type",
                          "an unsupported layer media type",
                          workdir=os.path.dirname(out), output=out)
        registry, reference = with_fixture(work, [web_layers()], manifest_media_type=OCI_INDEX)
        with registry:
            out = os.path.join(work, "c06b", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION)
            expect_denial(report, "C06b", completed, "media-type",
                          "an image index where an image manifest is required",
                          workdir=os.path.dirname(out), output=out)

    # --- C07 -----------------------------------------------------------------
    if selected("C07"):
        registry, reference = with_fixture(work, [web_layers()])
        with registry:
            out = os.path.join(work, "c07", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION,
                                     token=None)
            ok = expect_denial(report, "C07", completed, "authentication",
                               "no credential in the environment",
                               workdir=os.path.dirname(out), output=out)
            # And with a credential the registry rejects: the failure must name
            # the boundary without ever quoting what was presented.
            out = os.path.join(work, "c07b", "web")
            secret = "super-secret-fixture-token-9f3a"
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION,
                                     token=secret)
            text = (completed.stdout + completed.stderr).decode("utf-8", "replace")
            basic = base64.b64encode(("x-access-token:" + secret).encode()).decode()
            leaked = [n for n, v in (("token", secret), ("basic", basic), ("bearer", FIXTURE_TOKEN))
                      if v in text]
            if completed.returncode == 0:
                report.record("C07b", "RED", "a rejected credential still produced a payload")
            elif leaked:
                report.record("C07b", "RED", "the failure leaked %s into its output" % ", ".join(leaked))
            elif "<redacted>" not in text and denial_property(completed) is None:
                report.record("C07b", "RED", "the failure produced no W2-A0 denial")
            else:
                report.record("C07b", "PASS",
                              "a rejected credential -> DENY [%s], nothing leaked"
                              % denial_property(completed))
            del ok

    # --- C08 -----------------------------------------------------------------
    if selected("C08"):
        layers = [web_layers() + [("web/../../escape.txt", "file", "owned\n")]]
        registry, reference = with_fixture(work, layers)
        with registry:
            out = os.path.join(work, "c08", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION)
            expect_denial(report, "C08", completed, "path-safety",
                          "a '..' traversal entry",
                          workdir=os.path.dirname(out), output=out)

    # --- C09 -----------------------------------------------------------------
    if selected("C09"):
        hostile = [
            ("an absolute path", "/etc/hosts"),
            ("a drive-letter path", "C:/Windows/System32/drivers/etc/hosts"),
            ("a UNC path", "//attacker/share/payload.dll"),
            ("alternate-data-stream syntax", "web/index.html:hidden"),
            ("a reserved device name", "web/CON"),
            ("a trailing-dot component", "web/config."),
        ]
        for index, (description, name) in enumerate(hostile):
            registry, reference = with_fixture(work, [web_layers() + [(name, "file", "x\n")]])
            with registry:
                out = os.path.join(work, "c09-%d" % index, "web")
                completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                         tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION)
                expect_denial(report, "C09.%d" % (index + 1), completed, "path-safety",
                              description, workdir=os.path.dirname(out), output=out)

    # --- C10 -----------------------------------------------------------------
    if selected("C10"):
        cases = [
            ("a symlink escaping the root", ("web/escape", "symlink", "../../../../etc/passwd")),
            ("a hard link to an outside path", ("web/hard", "hardlink", "../../etc/passwd")),
            ("a device/FIFO entry", ("web/pipe", "fifo", None)),
        ]
        for index, (description, entry) in enumerate(cases):
            registry, reference = with_fixture(work, [web_layers() + [entry]])
            with registry:
                out = os.path.join(work, "c10-%d" % index, "web")
                completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                         tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION)
                expect_denial(report, "C10.%d" % (index + 1), completed, "entry-type",
                              description, workdir=os.path.dirname(out), output=out)

    # --- C11 -----------------------------------------------------------------
    if selected("C11"):
        # (a) the plain whiteout has one meaning, and it must be applied.
        first = web_layers(files=[("keep.txt", "keep\n"), ("drop.txt", "drop\n")])
        second = [("web/.wh.drop.txt", "whiteout", "")]
        expected = expected_digest_for(work, [first, second])
        naive = expected_digest_for(work, [first])
        registry, reference = with_fixture(work, [first, second])
        with registry:
            out = os.path.join(work, "c11a", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=expected, revision=FIXTURE_REVISION)
            if completed.returncode != 0:
                report.record("C11a", "RED", "a plain whiteout was not applied: %s"
                              % (completed.stdout + completed.stderr).decode("utf-8", "replace")[-200:])
            elif os.path.exists(os.path.join(out, "drop.txt")):
                report.record("C11a", "RED", "the whited-out file survived")
            elif expected == naive:
                report.record("C11a", "INERT", "the whiteout changed nothing; the control proves nothing")
            else:
                report.record("C11a", "PASS", "a plain whiteout removed the file and the tree digest matched")
        # (b) the opaque whiteout does not, so it is refused rather than guessed.
        registry, reference = with_fixture(
            work, [first, [("web/.wh..wh..opq", "whiteout", "")]])
        with registry:
            out = os.path.join(work, "c11b", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=expected, revision=FIXTURE_REVISION)
            expect_denial(report, "C11b", completed, "whiteout",
                          "an opaque whiteout", workdir=os.path.dirname(out), output=out)

    # --- C12 -----------------------------------------------------------------
    if selected("C12"):
        layers = [web_layers()]
        registry, reference = with_fixture(work, layers)
        with registry:
            out = os.path.join(work, "c12", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest="0" * 64, revision=FIXTURE_REVISION)
            expect_denial(report, "C12", completed, "tree-digest",
                          "a payload that is not the pinned tree",
                          workdir=os.path.dirname(out), output=out)

    # --- C13 -----------------------------------------------------------------
    if selected("C13"):
        layers = [web_layers()]
        expected = expected_digest_for(work, layers)
        registry, reference = with_fixture(work, layers)
        with registry:
            out = os.path.join(work, "c13", "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=expected, revision="2" * 40)
            expect_denial(report, "C13", completed, "web-revision",
                          "a payload recording a different web commit",
                          workdir=os.path.dirname(out), output=out)

    # --- C14 -----------------------------------------------------------------
    if selected("C14"):
        # Layer 0 is good and writes real files; layer 1 fails verification. The
        # failure therefore happens with a populated staging tree, which is the
        # only arrangement in which "no partial output" says anything.
        good = web_layers(files=[("index.html", "fixture\n"), ("app.js", "console.log(1)\n")])
        later = [("web/late.txt", "file", "late\n")]
        registry, reference = with_fixture(work, [good, later], break_layer_bytes=1)
        with registry:
            parent = os.path.join(work, "c14")
            os.makedirs(parent, exist_ok=True)
            out = os.path.join(parent, "web")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=ACCEPTED_TREE_DIGEST, revision=FIXTURE_REVISION)
            ok = expect_denial(report, "C14", completed, "descriptor-digest",
                               "a second layer that fails after the first was written",
                               workdir=parent, output=out)
            if ok:
                residue = sorted(os.listdir(parent))
                if residue:
                    report.record("C14b", "RED", "the failure left %s behind" % residue)
                else:
                    report.record("C14b", "PASS", "the destination directory is empty after the failure")

    # --- C15 -----------------------------------------------------------------
    if selected("C15"):
        base = web_layers(files=[("app.js", "VERSION=1\n")])
        over = [("web/app.js", "file", "VERSION=2\n")]
        forward = expected_digest_for(work, [base, over])
        reversed_ = expected_digest_for(work, [over, base])
        if forward == reversed_:
            report.record("C15", "INERT", "the fixture is order-insensitive; the control proves nothing")
        else:
            registry, reference = with_fixture(work, [base, over], reverse_layers=True)
            with registry:
                out = os.path.join(work, "c15", "web")
                completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                         tree_digest=forward, revision=FIXTURE_REVISION)
                expect_denial(report, "C15", completed, "tree-digest",
                              "layers served in reverse order",
                              workdir=os.path.dirname(out), output=out)

    # --- C16 -----------------------------------------------------------------
    if selected("C16"):
        # `vnd.docker.*` is an OCI media type this consumer must recognise to
        # refuse or accept a descriptor; it is not a dependency on a runtime.
        pattern = r"(?<!vnd\.)\bdocker\b|containerd|podman|nerdctl|buildx|dockerd|docker-compose"
        # The three files that make up the real acquisition path. This file is
        # not one of them: it is test scaffolding that has to be able to NAME a
        # runtime in order to plant one, and scanning it would only ever find
        # the scanner.
        targets = [CONSUMER, DIGEST_TOOL, WORKFLOW]
        hits = scan_for(targets, pattern, code_only=True)
        # A scanner that cannot find a planted dependency is a scanner that
        # would not have found a real one either.
        planted = os.path.join(work, "planted-runtime.ps1")
        with open(planted, "w", encoding="utf-8") as handle:
            handle.write("docker pull ghcr.io/example/image:latest\n")
        if not scan_for([planted], pattern, code_only=True):
            report.record("C16", "INERT",
                          "the container-runtime scanner does not detect a planted dependency")
        elif hits:
            report.record("C16", "RED", "a container-runtime dependency remains: %s" % hits[:3])
        else:
            report.record("C16", "PASS",
                          "the acquisition path invokes no container executable, daemon or engine")

    # --- C17 -----------------------------------------------------------------
    if selected("C17"):
        pattern = r"upload-artifact|download-artifact|actions/cache"
        hits = scan_for([WORKFLOW], pattern, code_only=True)
        planted = os.path.join(work, "planted-artifact.yml")
        with open(planted, "w", encoding="utf-8") as handle:
            handle.write("      - uses: actions/upload-artifact@v4\n")
        if not scan_for([planted], pattern, code_only=True):
            report.record("C17", "INERT", "the artifact scanner does not detect a planted upload")
        elif hits:
            report.record("C17", "RED", "an Actions artifact or cache step is present: %s" % hits[:3])
        elif not os.path.isfile(WORKFLOW):
            report.record("C17", "RED", "the W2 workflow is missing entirely")
        else:
            report.record("C17", "PASS",
                          "no artifact upload/download and no cache can carry the payload between jobs")

    # --- C18 -----------------------------------------------------------------
    if selected("C18"):
        one = web_layers(files=[("index.html", "v1\n"), ("stale.txt", "gone\n")])
        two = [("web", "dir", None), ("web/assets", "dir", None),
               ("web/assets/app.css", "file", "body{}\n"), ("web/index.html", "file", "v2\n")]
        three = [("web/.wh.stale.txt", "whiteout", "")]
        layers = [one, two, three]
        expected = expected_digest_for(work, layers)
        registry, reference = with_fixture(work, layers)
        with registry:
            out = os.path.join(work, "c18", "web")
            evidence = os.path.join(work, "c18", "evidence.json")
            completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                     tree_digest=expected, revision=FIXTURE_REVISION,
                                     evidence=evidence)
            text = (completed.stdout + completed.stderr).decode("utf-8", "replace")
            if completed.returncode != 0:
                report.record("C18", "RED", "a correct three-layer image was refused: %s" % text[-300:])
            elif not os.path.isfile(os.path.join(out, "assets", "app.css")):
                report.record("C18", "RED", "the accepted tree is missing a later layer's file")
            elif os.path.exists(os.path.join(out, "stale.txt")):
                report.record("C18", "RED", "the accepted tree kept a whited-out file")
            elif open(os.path.join(out, "index.html"), "rb").read() != b"v2\n":
                report.record("C18", "RED", "a later layer did not override an earlier file")
            else:
                document = json.load(open(evidence, encoding="utf-8"))
                if len(document["layers"]) != 3 or document["treeDigest"] != expected:
                    report.record("C18", "RED", "the evidence does not describe the three verified layers")
                else:
                    report.record("C18", "PASS",
                                  "a correct three-layer image with an override and a whiteout was accepted")

    # --- C19 -----------------------------------------------------------------
    if selected("C19"):
        tree = oracle_tree(work)
        computed = pkg_tree_digest.tree_digest(tree, ORACLE_EPOCH)
        gnu = pkg_tree_digest.gnu_tar_digest(tree, ORACLE_EPOCH)
        if computed != ORACLE_DIGEST:
            report.record("C19", "RED",
                          "the Windows digest implementation says %s, the recorded GNU tar oracle says %s"
                          % (computed, ORACLE_DIGEST))
        elif gnu is None:
            report.record("C19", "PASS",
                          "agrees with the recorded GNU tar 1.35 oracle %s (no live tar on this host)"
                          % ORACLE_DIGEST)
        elif gnu != computed:
            report.record("C19", "RED", "live GNU tar says %s, this implementation says %s" % (gnu, computed))
        else:
            report.record("C19", "PASS", "live GNU tar and the recorded oracle both agree: %s" % computed)

    # --- C20 -----------------------------------------------------------------
    if selected("C20"):
        layers = [web_layers(files=[("index.html", "v1\n"), ("assets.js", "x\n")]),
                  [("web/assets.js", "file", "y\n")]]
        expected = expected_digest_for(work, layers)
        registry, reference = with_fixture(work, layers)
        with registry:
            outs = []
            failed = None
            for run in range(2):
                out = os.path.join(work, "c20-%d" % run, "web")
                completed = run_consumer(registry.authority, "fixtures/web", reference, out,
                                         tree_digest=expected, revision=FIXTURE_REVISION)
                if completed.returncode != 0:
                    failed = (completed.stdout + completed.stderr).decode("utf-8", "replace")[-300:]
                    break
                outs.append(out)
            if failed is not None:
                report.record("C20", "RED", "a repeat consumption failed: %s" % failed)
            else:
                first = pkg_tree_digest.tree_digest(outs[0], FIXTURE_EPOCH)
                second = pkg_tree_digest.tree_digest(outs[1], FIXTURE_EPOCH)
                differences = _compare_trees(outs[0], outs[1])
                if first != second or first != expected:
                    report.record("C20", "RED", "two clean consumptions produced %s and %s" % (first, second))
                elif differences:
                    report.record("C20", "RED", "two clean consumptions differ: %s" % differences[:3])
                else:
                    report.record("C20", "PASS",
                                  "two clean consumptions are byte-identical and hash to %s" % first)

    # --- C21 -----------------------------------------------------------------
    if selected("C21"):
        source = open(CONSUMER, encoding="utf-8").read()
        missing = []
        for label, value in (
            ("registry", ACCEPTED_REGISTRY), ("repository", ACCEPTED_REPOSITORY),
            ("reference", ACCEPTED_REFERENCE), ("tree digest", ACCEPTED_TREE_DIGEST),
            ("revision", ACCEPTED_REVISION),
        ):
            if ("$Accepted" not in source) or (value not in source):
                missing.append(label)
        planted = "the accepted %s is absent" % ", ".join(missing) if missing else None
        if planted:
            report.record("C21", "RED", planted)
        elif ACCEPTED_TREE_DIGEST in open(__file__, encoding="utf-8").read() and \
                ACCEPTED_TREE_DIGEST not in source:
            report.record("C21", "INERT", "the constants are only asserted against themselves")
        else:
            report.record("C21", "PASS", "the consumer's accepted constants are the ruling's five values")

    # --- C22 -----------------------------------------------------------------
    if selected("C22"):
        _workflow_control(report, work)

    # --- C23 -----------------------------------------------------------------
    if selected("C23"):
        _cross_volume_control(report, work)


def strip_commentary(path, text):
    """The executable part of a file, with comments and docstrings blanked out.

    A scan for a container-runtime dependency that cannot tell an invocation
    from a comment explaining why there is no invocation has two useless
    outcomes: it fires on the explanation, or it is loosened until it would miss
    the real thing. Lines are blanked rather than removed, so the line numbers
    in a finding still point at the real file.
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


def _cross_volume_control(report, work):
    """C23: a finding the checkout cannot relate to is reported, not raised.

    A control plants its fixtures in temporary storage. On a Windows runner that
    is a different drive from the checkout -- `D:\\a\\...` against `C:\\Users\\...`
    -- and `os.path.relpath` refuses to relate two mounts. The match has already
    been made by then, so raising there turns a detected dependency into a crash
    and skips every control after it.

    No Linux host has two drive letters, so the refusal is forced directly: the
    path formatter is replaced for the duration of one scan. That also lets the
    control assert the part a real two-drive machine could not -- that an
    unrelated failure from the same call is still allowed to escape.
    """
    pattern = r"(?<!vnd\.)\bdocker\b|containerd|podman|nerdctl|buildx|dockerd|docker-compose"
    planted = os.path.join(work, "c23", "planted-cross-volume.ps1")
    os.makedirs(os.path.dirname(planted), exist_ok=True)
    with open(planted, "w", encoding="utf-8") as handle:
        handle.write("# an explanatory comment naming docker is not an invocation\n"
                     "docker pull ghcr.io/example/image:latest\n")
    expected = [(planted, 2, "docker pull ghcr.io/example/image:latest")]

    real_relpath = os.path.relpath
    calls = []

    def cross_volume(path, start=os.curdir):
        calls.append((os.fspath(path), os.fspath(start)))
        if os.fspath(path) == planted:
            raise ValueError("path is on mount 'C:', start on mount 'D:'")
        return real_relpath(path, start)

    def unrelated(path, start=os.curdir):
        if os.fspath(path) == planted:
            raise RuntimeError("an unrelated failure in the path formatter")
        return real_relpath(path, start)

    os.path.relpath = cross_volume
    try:
        hits = scan_for([planted], pattern, code_only=True)
    finally:
        os.path.relpath = real_relpath

    # Inert-proof: if `scan_for` ever stops relating a finding to the repository
    # root, the fallback below is unreached and this control proves nothing.
    if (planted, REPO_ROOT) not in calls:
        report.record("C23", "INERT",
                      "scan_for never asked to relate the finding to the checkout root")
        return
    if hits != expected:
        report.record("C23", "RED",
                      "a cross-volume finding was lost or altered: expected %s, got %s"
                      % (expected, hits))
        return

    os.path.relpath = unrelated
    try:
        scan_for([planted], pattern, code_only=True)
    except RuntimeError:
        propagated = True
    else:
        propagated = False
    finally:
        os.path.relpath = real_relpath

    if not propagated:
        report.record("C23", "RED",
                      "an unrelated failure in the path formatter was swallowed with the ValueError")
    else:
        report.record("C23", "PASS",
                      "a finding on an unrelatable path keeps its path, line %d and content, "
                      "and an unrelated failure still raises" % expected[0][1])


def _compare_trees(left, right):
    differences = []
    for root, _, files in os.walk(left):
        for name in files:
            a = os.path.join(root, name)
            b = os.path.join(right, os.path.relpath(a, left))
            if not os.path.isfile(b) or open(a, "rb").read() != open(b, "rb").read():
                differences.append(os.path.relpath(a, left))
    for root, _, files in os.walk(right):
        for name in files:
            b = os.path.join(root, name)
            a = os.path.join(left, os.path.relpath(b, right))
            if not os.path.isfile(a):
                differences.append(os.path.relpath(b, right))
    return sorted(set(differences))


def oracle_tree(work):
    """A small, fully deterministic tree, identical on every platform.

    Deliberately includes nested directories, an empty file, a file with no
    trailing newline, and names that sort differently under a byte comparison
    than under a case-insensitive one -- the places where two tar
    implementations disagree if they are going to.
    """
    root = os.path.join(work, "oracle-tree")
    if os.path.isdir(root):
        shutil.rmtree(root)
    files = {
        "index.html": b"<!doctype html>\n",
        "Alpha.txt": b"A",
        "alpha.md": b"a\n",
        "empty": b"",
        "no-newline.txt": b"tail",
        "assets/app.css": b"body{margin:0}\n",
        "assets/nested/deep.js": b"export default 1;\n",
        "zz-last.txt": b"z\n",
    }
    for name, content in sorted(files.items()):
        target = os.path.join(root, name.replace("/", os.sep))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "wb") as handle:
            handle.write(content)
    os.makedirs(os.path.join(root, "assets", "empty-dir"), exist_ok=True)
    # GNU tar reads the mode off disk while pkg_tree_digest normalises it, so
    # without this the control compares two different archives and reports the
    # umask of whoever ran it as a cross-implementation disagreement.
    for directory, _, names in os.walk(root):
        os.chmod(directory, 0o755)
        for name in names:
            os.chmod(os.path.join(directory, name), 0o644)
    return root


def _workflow_control(report, work):
    if not os.path.isfile(WORKFLOW):
        report.record("C22", "RED", "the W2 workflow is missing")
        return
    text = open(WORKFLOW, encoding="utf-8").read()
    findings = []

    forbidden = {
        r"packages:\s*write": "packages: write",
        r"contents:\s*write": "contents: write",
        r"id-token:\s*write": "id-token: write",
        r"^\s*pull_request_target:": "pull_request_target",
        r"^\s*workflow_dispatch:": "workflow_dispatch",
        r"^\s*schedule:": "schedule",
    }
    for pattern, label in forbidden.items():
        if re.search(pattern, text, re.MULTILINE):
            findings.append("declares %s" % label)

    for match in re.finditer(r"uses:\s*([^\s#]+)", text):
        reference = match.group(1)
        if "@" not in reference or not re.search(r"@[0-9a-f]{40}$", reference):
            findings.append("unpinned action %s" % reference)

    for match in re.finditer(r"ghcr\.io/[A-Za-z0-9._/-]+", text):
        image = match.group(0)
        if "@sha256:" not in text[match.start():match.start() + len(image) + 80]:
            findings.append("mutable image reference %s" % image)

    if "pull_request:" not in text:
        findings.append("has no pull_request trigger")
    if "persist-credentials: false" not in text:
        findings.append("checks out with persisted credentials")
    if "contents: read" not in text or "packages: read" not in text:
        findings.append("does not request exactly contents: read + packages: read")
    if "windows-latest" not in text:
        findings.append("has no windows-latest proof job")

    # Inert-proof: the same audit run over a deliberately bad workflow must fail.
    planted = os.path.join(work, "planted-workflow.yml")
    with open(planted, "w", encoding="utf-8") as handle:
        handle.write("on:\n  workflow_dispatch:\npermissions:\n  packages: write\n"
                     "jobs:\n  x:\n    steps:\n      - uses: actions/checkout@v4\n")
    planted_text = open(planted, encoding="utf-8").read()
    detects = bool(re.search(r"packages:\s*write", planted_text)) and \
        bool(re.search(r"^\s*workflow_dispatch:", planted_text, re.MULTILINE))
    if not detects:
        report.record("C22", "INERT", "the workflow audit does not detect a planted violation")
    elif findings:
        report.record("C22", "RED", "; ".join(findings))
    else:
        report.record("C22", "PASS",
                      "pull_request only, contents+packages read only, pinned actions, "
                      "no persisted credentials, no dispatch, no mutable image reference")


# ===========================================================================

def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--only", action="append", help="run only the named control(s)")
    parser.add_argument("--record-oracle", action="store_true",
                        help="print the GNU tar digest of the C19 tree and exit")
    args = parser.parse_args(argv)

    work = tempfile.mkdtemp(prefix="w2a0-controls-")
    try:
        if args.record_oracle:
            tree = oracle_tree(work)
            gnu = pkg_tree_digest.gnu_tar_digest(tree, ORACLE_EPOCH)
            if gnu is None:
                sys.stderr.write("no GNU tar on this host; the oracle can only be recorded on one\n")
                return 2
            sys.stdout.write(gnu + "\n")
            return 0

        # The controls must not be able to change the repository they audit.
        before = _repository_fingerprint()
        print("W2-A0 hostile controls")
        report = Report()
        started = time.time()
        run_controls(work, report, set(args.only) if args.only else None)
        after = _repository_fingerprint()

        totals = report.counts()
        if before != after:
            report.record("RESTORE", "RED", "the controls modified the audited files")
        else:
            report.record("RESTORE", "PASS", "every audited file is byte-identical to before the run")
        totals = report.counts()
        print("")
        print("W2-A0 controls: %d PASS, %d RED, %d INERT in %.1fs"
              % (totals["PASS"], totals["RED"], totals["INERT"], time.time() - started))
        return 0 if (totals["RED"] == 0 and totals["INERT"] == 0) else 1
    finally:
        shutil.rmtree(work, ignore_errors=True)


def _repository_fingerprint():
    fingerprint = {}
    for path in (CONSUMER, DIGEST_TOOL, os.path.abspath(__file__), WORKFLOW):
        if os.path.isfile(path):
            with open(path, "rb") as handle:
                fingerprint[os.path.relpath(path, REPO_ROOT)] = hashlib.sha256(handle.read()).hexdigest()
    return fingerprint


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
