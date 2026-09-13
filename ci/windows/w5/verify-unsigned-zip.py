#!/usr/bin/env python3
"""W5-A1 (#276): verify the unsigned win-x64 portable ZIP from its own bytes.

This is the "evidence no build job produced" gate. It is handed two archives
that two independent assemble jobs uploaded, and nothing those jobs said about
them. Every statement below is recomputed here, from the archive bytes and from
the accepted FFmpeg runtime archive the verify job acquired for itself:

  * both archives hash to the same SHA-256, measured by THIS script;
  * the unpacked `web/` tree has the canonical `pkg_tree_digest` pinned for the
    accepted Web payload, computed by the frozen `ci/windows/w2/pkg-tree-digest.py`;
  * the given FFmpeg runtime archive hashes to the accepted `runtimeSha256`, and
    the archive's `ffmpeg/` tree holds exactly that archive's members, byte for
    byte -- no file missing, added or changed;
  * `licenses/provenance.json` names the commit built, its SOURCE_DATE_EPOCH,
    the Web pin and the FFmpeg pin;
  * `tesserafin.exe` is a PE x64 image and the package is self-contained:
    hostfxr, hostpolicy, coreclr and System.Private.CoreLib are present, and the
    runtimeconfig declares `includedFrameworks` and no shared framework.

It never publishes, never assembles, never reads a hash file and takes no pin
from its command line: the two accepted identities are constants here, and
`unsigned-zip-controls.py` asserts they equal `ci/package/pins.env` and
`accepted-runtime.json`. `verify()` accepts other pins only so the controls can
drive this exact implementation against synthetic fixtures.

    python3 ci/windows/w5/verify-unsigned-zip.py \\
        --zip-a a/tesserafin-server_1.0.0_win-x64.zip \\
        --zip-b b/tesserafin-server_1.0.0_win-x64.zip \\
        --runtime-archive runtime/tesserafin-ffmpeg-...-win-x64.zip \\
        --head <40-hex commit> --epoch <SOURCE_DATE_EPOCH>
"""

import argparse
import hashlib
import json
import os
import re
import struct
import subprocess
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
TREE_DIGEST = os.path.join(HERE, "..", "w2", "pkg-tree-digest.py")

WEB_PAYLOAD_SHA256 = "4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f"
FFMPEG_RUNTIME_SHA256 = "f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e"
PINS = {"web": WEB_PAYLOAD_SHA256, "ffmpeg": FFMPEG_RUNTIME_SHA256}

# SharedVersion stays 1.0.0 in W5-A1; the 1.1 number is W5-A3 / W5-A4.
PACKAGE = "tesserafin-server_1.0.0_win-x64"
ARCHIVE_NAME = PACKAGE + ".zip"
HOST_COMPONENTS = ("hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "System.Private.CoreLib.dll")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
HEX40 = re.compile(r"^[0-9a-f]{40}$")


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def member_digests(archive):
    """{name: sha256} of every file entry, refusing names that could escape."""
    result = {}
    for info in archive.infolist():
        name = info.filename
        if name.endswith("/"):
            continue
        if name.startswith("/") or "\\" in name or re.match(r"^[A-Za-z]:", name) or \
                any(part in ("", ".", "..") for part in name.split("/")):
            raise ValueError("unsafe entry name %r" % name)
        if name in result:
            raise ValueError("duplicate entry %r" % name)
        result[name] = hashlib.sha256(archive.read(info)).hexdigest()
    return result


def web_tree_digest(archive, epoch):
    prefix = PACKAGE + "/web/"
    with tempfile.TemporaryDirectory(prefix="w5a1-web-") as root:
        count = 0
        for info in archive.infolist():
            if not info.filename.startswith(prefix) or info.filename.endswith("/"):
                continue
            target = os.path.join(root, *info.filename[len(prefix):].split("/"))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "wb") as handle:
                handle.write(archive.read(info))
            count += 1
        if count == 0:
            return None
        run = subprocess.run([sys.executable, TREE_DIGEST, root, str(epoch)],
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        if run.returncode != 0:
            raise ValueError("pkg-tree-digest refused the web tree: %s"
                             % run.stderr.decode("utf-8", "replace").strip())
        return run.stdout.decode("ascii").strip()


def pe_machine(data):
    if len(data) < 0x40 or data[:2] != b"MZ":
        return None
    offset = struct.unpack_from("<i", data, 0x3C)[0]
    if offset <= 0 or offset + 6 > len(data) or data[offset:offset + 4] != b"PE\0\0":
        return None
    return struct.unpack_from("<H", data, offset + 4)[0]


def check_contents(label, path, head, epoch, runtime_members, pins, fail):
    """Every content statement about one archive. Findings go through `fail`."""
    with zipfile.ZipFile(path) as archive:
        try:
            members = member_digests(archive)
        except ValueError as error:
            fail("LAYOUT", "%s: %s" % (label, error))
            return
        outside = sorted(n for n in members if not n.startswith(PACKAGE + "/"))
        if outside:
            fail("LAYOUT", "%s: %d entries outside %s/, first %r" % (label, len(outside), PACKAGE, outside[0]))

        # -- provenance ------------------------------------------------------
        provenance = None
        try:
            provenance = json.loads(archive.read(PACKAGE + "/licenses/provenance.json").decode("utf-8"))
        except (KeyError, ValueError) as error:
            fail("PROVENANCE-MISSING", "%s: no readable licenses/provenance.json (%s)" % (label, error))
        if provenance is not None:
            if provenance.get("serverCommit") != head:
                fail("PROVENANCE-COMMIT", "%s: provenance names serverCommit %r, the commit built is %s"
                     % (label, provenance.get("serverCommit"), head))
            if provenance.get("sourceDateEpoch") != epoch:
                fail("PROVENANCE-EPOCH", "%s: provenance names sourceDateEpoch %r, the commit's is %d"
                     % (label, provenance.get("sourceDateEpoch"), epoch))
            if (provenance.get("web") or {}).get("payloadSha256") != pins["web"]:
                fail("PROVENANCE-WEB", "%s: provenance names web payload %r, the pin is %s"
                     % (label, (provenance.get("web") or {}).get("payloadSha256"), pins["web"]))
            if (provenance.get("ffmpegRuntime") or {}).get("archiveSha256") != pins["ffmpeg"]:
                fail("PROVENANCE-FFMPEG", "%s: provenance names FFmpeg runtime %r, the pin is %s"
                     % (label, (provenance.get("ffmpegRuntime") or {}).get("archiveSha256"), pins["ffmpeg"]))

        # -- the Web tree, recomputed ----------------------------------------
        web_epoch = ((provenance or {}).get("web") or {}).get("sourceDateEpoch")
        if not isinstance(web_epoch, int) or web_epoch <= 0:
            fail("WEB-DIGEST", "%s: no usable web sourceDateEpoch to hash the tree at" % label)
        else:
            try:
                digest = web_tree_digest(archive, web_epoch)
            except ValueError as error:
                digest = None
                fail("WEB-DIGEST", "%s: %s" % (label, error))
            else:
                if digest != pins["web"]:
                    fail("WEB-DIGEST", "%s: the unpacked web/ tree hashes to %s, the pin is %s"
                         % (label, digest, pins["web"]))

        # -- the FFmpeg tree, against the accepted archive's own members -----
        prefix = PACKAGE + "/ffmpeg/"
        packed = {n[len(prefix):]: d for n, d in members.items() if n.startswith(prefix)}
        if runtime_members is not None and packed != runtime_members:
            missing = sorted(set(runtime_members) - set(packed))
            added = sorted(set(packed) - set(runtime_members))
            changed = sorted(n for n in set(packed) & set(runtime_members) if packed[n] != runtime_members[n])
            fail("FFMPEG-TREE", "%s: ffmpeg/ is not the accepted runtime archive: %d missing, %d added, "
                 "%d changed (first: %r)" % (label, len(missing), len(added), len(changed),
                                             (missing + added + changed)[0]))

        # -- self-contained win-x64 ------------------------------------------
        for component in HOST_COMPONENTS:
            if PACKAGE + "/" + component not in members:
                fail("SELF-CONTAINED", "%s: no %s at the package root" % (label, component))
        try:
            options = json.loads(archive.read(PACKAGE + "/tesserafin.runtimeconfig.json")
                                 .decode("utf-8-sig"))["runtimeOptions"]
        except (KeyError, ValueError, TypeError) as error:
            fail("SELF-CONTAINED", "%s: no readable runtimeOptions in tesserafin.runtimeconfig.json (%s)"
                 % (label, error))
        else:
            for shared in ("framework", "frameworks"):
                if shared in options:
                    fail("SELF-CONTAINED", "%s: runtimeconfig declares a shared %r" % (label, shared))
            if "includedFrameworks" not in options:
                fail("SELF-CONTAINED", "%s: runtimeconfig declares no includedFrameworks" % label)
        try:
            machine = pe_machine(archive.read(PACKAGE + "/tesserafin.exe"))
        except KeyError:
            machine = None
        if machine != 0x8664:
            fail("PE-X64", "%s: tesserafin.exe is not a PE x64 image (machine %r)" % (label, machine))


def verify(zip_a, zip_b, runtime_archive, head, epoch, pins=PINS):
    """Return a list of (code, message). Empty means accepted."""
    findings = []

    def fail(code, message):
        findings.append((code, message))

    if not HEX40.match(head or ""):
        fail("HEAD", "%r is not a full lowercase commit" % head)
    for label, path in (("zip-a", zip_a), ("zip-b", zip_b)):
        if os.path.basename(path) != ARCHIVE_NAME:
            fail("ARCHIVE-NAME", "%s is %r, not %s" % (label, os.path.basename(path), ARCHIVE_NAME))
        if not os.path.isfile(path):
            fail("MISSING", "%s: no archive at %s" % (label, path))
    if not os.path.isfile(runtime_archive):
        fail("MISSING", "no FFmpeg runtime archive at %s" % runtime_archive)
    if findings:
        return findings

    # The pair verdict: two digests measured here, from the two artifacts.
    digest_a = sha256_file(zip_a)
    digest_b = sha256_file(zip_b)
    print("zip-a sha256 %s (%d bytes, recomputed)" % (digest_a, os.path.getsize(zip_a)))
    print("zip-b sha256 %s (%d bytes, recomputed)" % (digest_b, os.path.getsize(zip_b)))
    if digest_a != digest_b:
        fail("HASH-MISMATCH", "zip-a %s != zip-b %s" % (digest_a, digest_b))
        with zipfile.ZipFile(zip_a) as a, zipfile.ZipFile(zip_b) as b:
            ma, mb = member_digests(a), member_digests(b)
        for name in sorted(set(ma) | set(mb)):
            if ma.get(name) != mb.get(name):
                print("  differs: %s  a=%s b=%s" % (name, ma.get(name), mb.get(name)))

    runtime_digest = sha256_file(runtime_archive)
    print("runtime archive sha256 %s (recomputed)" % runtime_digest)
    runtime_members = None
    if runtime_digest != pins["ffmpeg"]:
        fail("FFMPEG-ARCHIVE", "the FFmpeg runtime archive hashes to %s, the pin is %s"
             % (runtime_digest, pins["ffmpeg"]))
    else:
        with zipfile.ZipFile(runtime_archive) as runtime:
            runtime_members = member_digests(runtime)

    check_contents("zip-a", zip_a, head, epoch, runtime_members, pins, fail)
    if digest_a != digest_b:
        check_contents("zip-b", zip_b, head, epoch, runtime_members, pins, fail)
    return findings


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--zip-a", required=True)
    parser.add_argument("--zip-b", required=True)
    parser.add_argument("--runtime-archive", required=True)
    parser.add_argument("--head", required=True)
    parser.add_argument("--epoch", required=True, type=int)
    args = parser.parse_args(argv)

    findings = verify(args.zip_a, args.zip_b, args.runtime_archive, args.head, args.epoch)
    for code, message in findings:
        print("RED %s: %s" % (code, message))
    if findings:
        print("VERDICT RED (%d finding(s))" % len(findings))
        return 1
    print("VERDICT PASS: both archives are %s; web %s; ffmpeg %s; serverCommit %s; epoch %d"
          % (sha256_file(args.zip_a), PINS["web"], PINS["ffmpeg"], args.head, args.epoch))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
