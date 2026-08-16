#!/usr/bin/env python3
"""Permanent negative controls for the win-x64 FFmpeg runtime (W1-A2, #236).

A gate that has never been seen to fail is a gate nobody has tested. Each
control below constructs an artifact that MUST be refused, runs the real gate
against it, and requires both a non-zero exit and the specific message that
proves the refusal came from the intended check rather than from an unrelated
failure earlier in the same script.

Every control runs on any platform: the PE fixtures are synthesised here rather
than compiled, so the wrong-architecture control does not need a Windows runner
or a second toolchain to produce a wrong-architecture binary.

Usage: negative-controls.py [--verbose]
"""

from __future__ import annotations

import argparse
import json
import hashlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parent.parent.parent
VERIFY_RUNTIME = HERE / "verify-runtime.py"
VERIFY_INPUTS = HERE / "verify-build-inputs.py"
ACCEPTED = json.loads((HERE / "accepted-build-inputs.json").read_text())

IMAGE_FILE_MACHINE_AMD64 = 0x8664
IMAGE_FILE_MACHINE_ARM64 = 0xAA64
IMAGE_FILE_MACHINE_I386 = 0x014C


# --------------------------------------------------------------------------- #
# fixtures
# --------------------------------------------------------------------------- #
DEFAULT_IMPORTS = {
    "kernel32.dll": ["CreateFileW", "GetLastError"],
    "secur32.dll": ["InitSecurityInterfaceW"],
    "ws2_32.dll": ["socket"],
}


def _import_blob(imports: dict[str, list[str]], base_rva: int) -> bytes:
    """A real import directory, laid out at base_rva.

    Synthesised rather than compiled so that the import-closure controls run on
    any host. It is also the parser's own test: a reader that mishandles the
    thunk array or the hint/name table cannot produce these names back.
    """
    n = len(imports)
    desc_size = (n + 1) * 20
    cursor = desc_size
    int_rvas: dict[str, int] = {}
    for dll, funcs in imports.items():
        int_rvas[dll] = base_rva + cursor
        cursor += (len(funcs) + 1) * 8

    strings = bytearray()
    string_base = cursor

    def put(text: bytes) -> int:
        rva = base_rva + string_base + len(strings)
        strings.extend(text)
        if len(strings) % 2:
            strings.append(0)
        return rva

    name_rvas: dict[str, int] = {}
    func_rvas: dict[str, list[int]] = {}
    for dll, funcs in imports.items():
        name_rvas[dll] = put(dll.encode() + b"\0")
        func_rvas[dll] = [put(struct.pack("<H", 0) + f.encode() + b"\0") for f in funcs]

    descriptors = bytearray()
    for dll in imports:
        descriptors += struct.pack("<IIIII", int_rvas[dll], 0, 0,
                                   name_rvas[dll], int_rvas[dll])
    descriptors += b"\0" * 20

    thunks = bytearray()
    for dll, funcs in imports.items():
        for rva in func_rvas[dll]:
            thunks += struct.pack("<Q", rva)
        thunks += struct.pack("<Q", 0)

    return bytes(descriptors) + bytes(thunks) + bytes(strings)


def synth_pe(machine: int = IMAGE_FILE_MACHINE_AMD64, magic: int = 0x20B,
             timestamp: int = 0, payload: bytes = b"",
             imports: dict[str, list[str]] | None = None) -> bytes:
    """A minimal, structurally valid PE image with one section and an import table.

    Enough for the architecture, format, timestamp and import-closure checks to
    have something real to read, and enough to carry a payload for the
    embedded-path check.
    """
    imports = DEFAULT_IMPORTS if imports is None else imports
    import_rva = 0x1000
    blob = _import_blob(imports, import_rva) if imports else b""
    body = blob + payload
    pad = (-len(body)) % 0x200
    section_data = body + b"\0" * (pad or 0)
    n_dirs = 16
    opt_size = (112 if magic == 0x20B else 96) + n_dirs * 8

    dos = bytearray(0x40)
    dos[0:2] = b"MZ"
    struct.pack_into("<I", dos, 0x3C, 0x40)

    coff = struct.pack("<IHHIIIHH", 0x00004550, machine, 1, timestamp, 0, 0,
                       opt_size, 0x0022)

    opt = bytearray(opt_size)
    struct.pack_into("<H", opt, 0, magic)
    opt[2] = 14  # linker major
    struct.pack_into("<I", opt, 4, len(section_data))       # SizeOfCode
    struct.pack_into("<I", opt, 16, 0x1000)                 # AddressOfEntryPoint
    struct.pack_into("<I", opt, 20, 0x1000)                 # BaseOfCode
    if magic == 0x20B:
        struct.pack_into("<Q", opt, 24, 0x140000000)        # ImageBase
        rest = 32
    else:
        struct.pack_into("<I", opt, 28, 0x400000)
        rest = 32
    struct.pack_into("<I", opt, rest, 0x1000)               # SectionAlignment
    struct.pack_into("<I", opt, rest + 4, 0x200)            # FileAlignment
    struct.pack_into("<I", opt, 56, 0x2000 + len(section_data))  # SizeOfImage
    struct.pack_into("<I", opt, 60, 0x200)                  # SizeOfHeaders
    struct.pack_into("<I", opt, 64, 0)                      # CheckSum
    struct.pack_into("<H", opt, 68, 3)                      # Subsystem: console
    struct.pack_into("<I", opt, (108 if magic == 0x20B else 92), n_dirs)
    if blob:
        dir_base = (112 if magic == 0x20B else 96)
        struct.pack_into("<II", opt, dir_base + 1 * 8, import_rva, len(blob))

    section = struct.pack("<8sIIII12xI", b".text\0\0\0", len(section_data),
                          0x1000, len(section_data), 0x200, 0x60000020)

    headers = bytes(dos) + coff + bytes(opt) + section
    headers += b"\0" * (0x200 - len(headers))
    return headers + section_data


def build_delivered_fixture(root: Path) -> Path:
    """A structurally complete delivered set that the gate accepts.

    Deliberately minimal but SELF-CONSISTENT: every control below breaks exactly
    one property of it, so a refusal names the property that was broken instead
    of a fixture that was never valid to begin with.
    """
    manifest = json.loads((REPO_ROOT / "ci/ffmpeg/components.json").read_text())
    win = [c for c in manifest["components"]
           if c.get("architectures") is None or "win-x64" in c["architectures"]]

    delivered = root / "delivered"
    (delivered / "bin").mkdir(parents=True)
    (delivered / "licenses").mkdir()
    (delivered / "runtime").mkdir()
    (delivered / "source").mkdir()

    (delivered / "bin/ffmpeg.exe").write_bytes(synth_pe())
    (delivered / "bin/ffprobe.exe").write_bytes(synth_pe())

    conf = ("--prefix=/c/tesserafin-ffmpeg --enable-gpl --enable-version3 "
            "--disable-nonfree --disable-libfdk-aac --enable-schannel "
            "--disable-openssl --disable-gnutls --enable-dxva2 --enable-d3d11va "
            "--enable-d3d12va --enable-amf --enable-libvpl --enable-ffnvcodec "
            "--enable-nvenc --enable-nvdec --enable-cuvid --enable-libx264 "
            "--enable-libx265 --enable-libsvtav1 --enable-libdav1d "
            "--enable-libvpx --enable-libzimg --enable-libass --enable-fontconfig")
    (delivered / "build-configuration.txt").write_text(conf + "\n")

    capability = {
        "probe": "w1a2-capability",
        "filters": ["tonemapx", "zscale", "ass", "subtitles", "scale", "overlay",
                    "alphasrc"],
        "encoders": ["libx264", "libx265", "libsvtav1", "aac", "libmp3lame",
                     "libopus"],
        "decoders": ["h264", "hevc", "av1"],
    }
    (delivered / "capability.json").write_text(json.dumps(capability, indent=2,
                                                          sort_keys=True) + "\n")

    notice_lines = ["# Third-party notices", ""]
    for c in sorted(win, key=lambda x: x["name"]):
        lic = delivered / "licenses" / f"{c['name']}-COPYING"
        lic.write_text(f"licence text for {c['name']}\n")
        digest = hashlib.sha256(lic.read_bytes()).hexdigest()
        notice_lines += [f"## {c['name']}", "",
                         f"- Licence text: `licenses/{lic.name}` (sha256 {digest})",
                         ""]
    (delivered / "THIRD-PARTY-NOTICES.md").write_text("\n".join(notice_lines))

    (delivered / "sbom.cdx.json").write_text(json.dumps({
        "bomFormat": "CycloneDX", "specVersion": "1.5", "version": 1,
        "components": [{"type": "library", "name": c["name"]} for c in win],
    }, indent=2, sort_keys=True) + "\n")

    archive = delivered / "runtime/tesserafin-ffmpeg-win-x64.zip"
    archive.write_bytes(b"PK\x05\x06" + b"\0" * 18)
    source = delivered / "source/tesserafin-ffmpeg-win-x64-source.tar.zst"
    source.write_bytes(b"\x28\xb5\x2f\xfd" + b"\0" * 16)

    provenance = {
        "probe": "w1a2-provenance",
        "buildRevision": manifest["buildRevision"],
        "repositorySha": "0" * 40,
        "buildInputs": {
            "reference": ACCEPTED["reference"],
            "manifestDigest": ACCEPTED["manifestDigest"],
            "layerDigest": ACCEPTED["layerDigest"],
            "lockSha256": ACCEPTED["lockSha256"],
            "trustRootSha256": ACCEPTED["trustRootSha256"],
            "packageCount": ACCEPTED["packageCount"],
            "installedSetEqualsLock": True,
            "mirrorsEmptied": True,
            "upstreamConsulted": False,
            "tagUsed": False,
        },
        "patches": {"seriesLength": 95, "applied": 95, "excluded": [], "fuzz": 0},
        "runtime": {"archive": "runtime/tesserafin-ffmpeg-win-x64.zip",
                    "sha256": hashlib.sha256(archive.read_bytes()).hexdigest()},
        "correspondingSource": {
            "archive": "source/tesserafin-ffmpeg-win-x64-source.tar.zst",
            "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "uncompressedSha256": "0" * 64},
    }
    (delivered / "provenance.json").write_text(json.dumps(provenance, indent=2,
                                                          sort_keys=True) + "\n")
    write_sums(delivered)
    return delivered


def write_sums(delivered: Path) -> None:
    files = sorted(p for p in delivered.rglob("*")
                   if p.is_file() and p.name != "SHA256SUMS")
    lines = []
    for p in files:
        digest = hashlib.sha256(p.read_bytes()).hexdigest()
        lines.append(f"{digest}  {p.relative_to(delivered).as_posix()}")
    (delivered / "SHA256SUMS").write_text("\n".join(lines) + "\n")


def _executable_lines(path: Path) -> list[tuple[int, str]]:
    """The lines of a file with comments and (for Python) string literals removed."""
    text = path.read_text(errors="replace")
    if path.suffix == ".py":
        import io
        import tokenize
        blanked: dict[int, list[str]] = {}
        try:
            for tok in tokenize.generate_tokens(io.StringIO(text).readline):
                if tok.type in (tokenize.COMMENT, tokenize.STRING):
                    continue
                blanked.setdefault(tok.start[0], []).append(tok.string)
        except (tokenize.TokenError, IndentationError):
            return [(i + 1, ln) for i, ln in enumerate(text.splitlines())]
        return [(n, " ".join(parts)) for n, parts in sorted(blanked.items())]

    out = []
    for i, line in enumerate(text.splitlines()):
        stripped = line.split("#", 1)[0] if path.suffix in (".sh", ".yml", ".yaml") else line
        if path.suffix == ".ps1":
            stripped = line.split("#", 1)[0]
        out.append((i + 1, stripped))
    return out


def _scan(path: Path, patterns: list[tuple[str, str]]) -> list[tuple[int, str]]:
    hits = []
    for number, line in _executable_lines(path):
        for pattern, label in patterns:
            if re.search(pattern, line):
                hits.append((number, label))
    return hits


# --------------------------------------------------------------------------- #
# harness
# --------------------------------------------------------------------------- #
class Controls:
    def __init__(self, verbose: bool) -> None:
        self.verbose = verbose
        self.passed = 0
        self.failed = 0

    def expect_refusal(self, name: str, argv: list[str], expected: str) -> None:
        proc = subprocess.run([sys.executable, *argv], capture_output=True, text=True)
        output = (proc.stdout or "") + (proc.stderr or "")
        if self.verbose:
            print(f"--- {name} ---\n{output}")
        if proc.returncode == 0:
            print(f"  CONTROL FAILED: {name} was ACCEPTED (exit 0)", file=sys.stderr)
            self.failed += 1
        elif expected.lower() not in output.lower():
            print(f"  CONTROL FAILED: {name} was refused, but not for the "
                  f"expected reason. Wanted {expected!r} in:\n{output}", file=sys.stderr)
            self.failed += 1
        else:
            print(f"  refused: {name}")
            self.passed += 1

    def expect_acceptance(self, name: str, argv: list[str]) -> None:
        proc = subprocess.run([sys.executable, *argv], capture_output=True, text=True)
        if proc.returncode != 0:
            print(f"  CONTROL FAILED: the positive control '{name}' was REFUSED:\n"
                  f"{proc.stdout}{proc.stderr}", file=sys.stderr)
            self.failed += 1
        else:
            print(f"  accepted: {name}")
            self.passed += 1

    def assert_true(self, name: str, condition: bool, detail: str = "") -> None:
        if condition:
            print(f"  ok  : {name}")
            self.passed += 1
        else:
            print(f"  CONTROL FAILED: {name}. {detail}", file=sys.stderr)
            self.failed += 1


def run_controls(c: Controls, tmp: Path) -> None:
    verify = str(VERIFY_RUNTIME)
    inputs = str(VERIFY_INPUTS)

    # ── positive control: the fixture as built must be accepted ─────────────
    base = build_delivered_fixture(tmp / "base")
    c.expect_acceptance("an intact delivered set",
                        [verify, "--delivered", str(base)])

    def fresh(tag: str) -> Path:
        root = tmp / tag
        if root.exists():
            shutil.rmtree(root)
        return build_delivered_fixture(root)

    # ── 1. the delivered path set ──────────────────────────────────────────
    d = fresh("missing")
    (d / "capability.json").unlink()
    c.expect_refusal("a MISSING delivered path", [verify, "--delivered", str(d)],
                     "which was not delivered")

    d = fresh("added")
    (d / "licenses/unexpected-extra.txt").write_text("not in the manifest\n")
    c.expect_refusal("an ADDED delivered path", [verify, "--delivered", str(d)],
                     "is not in SHA256SUMS")

    d = fresh("renamed")
    (d / "provenance.json").rename(d / "provenance-renamed.json")
    c.expect_refusal("a RENAMED delivered path", [verify, "--delivered", str(d)],
                     "which was not delivered")

    d = fresh("corrupted")
    blob = d / "runtime/tesserafin-ffmpeg-win-x64.zip"
    blob.write_bytes(blob.read_bytes() + b"\x00")
    c.expect_refusal("a CORRUPTED delivered path", [verify, "--delivered", str(d)],
                     "does not match")

    # ── 2. wrong-architecture and non-deterministic PE ─────────────────────
    for tag, machine, label in (("arm64", IMAGE_FILE_MACHINE_ARM64, "arm64"),
                                ("i386", IMAGE_FILE_MACHINE_I386, "x86")):
        d = fresh(f"pe-{tag}")
        (d / "bin/ffmpeg.exe").write_bytes(synth_pe(machine=machine))
        write_sums(d)
        c.expect_refusal(f"a WRONG-ARCHITECTURE PE ({label})",
                         [verify, "--delivered", str(d)], "not PE32+ x86-64")

    d = fresh("pe32")
    (d / "bin/ffmpeg.exe").write_bytes(synth_pe(magic=0x10B))
    write_sums(d)
    c.expect_refusal("a 32-bit PE32 image", [verify, "--delivered", str(d)],
                     "not PE32+ x86-64")

    d = fresh("timestamped")
    (d / "bin/ffmpeg.exe").write_bytes(synth_pe(timestamp=1786557931))
    write_sums(d)
    c.expect_refusal("a PE carrying a link timestamp",
                     [verify, "--delivered", str(d)], "link timestamp")

    # ── 3. an embedded build-host path, in both encodings ──────────────────
    for tag, encoding, label in (("leak-utf8", "utf-8", "UTF-8"),
                                 ("leak-utf16", "utf-16-le", "UTF-16LE")):
        d = fresh(tag)
        (d / "bin/ffmpeg.exe").write_bytes(
            synth_pe(payload=b"harmless" + "D:\\a\\tesserafin".encode(encoding)))
        write_sums(d)
        c.expect_refusal(f"an embedded build-host path ({label})",
                         [verify, "--delivered", str(d)], "build-host path")

    # ── 4. the build-input reference ───────────────────────────────────────
    tagged = f"{ACCEPTED['registry']}/{ACCEPTED['package']}:latest"
    c.expect_refusal("a TAGGED build-input reference",
                     [inputs, "--reference", tagged], "not digest-pinned")
    c.expect_refusal("a bare package name with no digest",
                     [inputs, "--reference",
                      f"{ACCEPTED['registry']}/{ACCEPTED['package']}"],
                     "not digest-pinned")
    c.expect_refusal("a digest-pinned reference to ANOTHER package",
                     [inputs, "--reference",
                      f"ghcr.io/someone-else/windows-ffmpeg-build-inputs@"
                      f"{ACCEPTED['manifestDigest']}"],
                     "not the authorised package")
    c.expect_refusal("a different manifest digest",
                     [inputs, "--reference",
                      f"{ACCEPTED['registry']}/{ACCEPTED['package']}@sha256:{'1' * 64}"],
                     "not the accepted")

    # ── 5. wrong manifest / layer / lock / trust root / signer ─────────────
    good = {
        "reference": ACCEPTED["reference"],
        "manifestDigest": ACCEPTED["manifestDigest"],
        "layerDigest": ACCEPTED["layerDigest"],
        "lockSha256": ACCEPTED["lockSha256"],
        "trustRootSha256": ACCEPTED["trustRootSha256"],
        "signaturesVerified": ACCEPTED["signaturesRequired"],
        "acceptedFingerprints": [ACCEPTED["acceptedSigner"]],
        "packageCount": ACCEPTED["packageCount"],
        "installedPackages": ACCEPTED["packageCount"],
        "installedSetEqualsLock": True,
        # The real consumer reports the LIST of mirrorlist files it emptied, so
        # the fixture reports one too. A fixture that used a boolean here would
        # have let a gate that only accepts booleans pass every control and then
        # refuse the first real build — which is exactly what happened.
        "mirrorsEmptied": ["mirrorlist.mingw", "mirrorlist.msys"],
        "upstreamConsulted": False,
        "tagUsed": False,
        "pacmanMode": "pacman -U over local files only",
    }
    evidence_dir = tmp / "evidence"
    evidence_dir.mkdir(exist_ok=True)

    def evidence(name: str, **overrides) -> str:
        doc = dict(good)
        doc.update(overrides)
        p = evidence_dir / f"{name}.json"
        p.write_text(json.dumps(doc, indent=2, sort_keys=True))
        return str(p)

    c.expect_acceptance("the accepted build inputs",
                        [inputs, "--consume-evidence", evidence("good")])
    # The decisive positive control: not a fixture this file invented, but the
    # consume.json a real hosted Windows runner wrote while pulling the accepted
    # digest. Every synthetic control above agrees with whatever shape this file
    # imagines; only this one disagrees with the consumer when the two drift.
    c.expect_acceptance("build-input evidence observed on a hosted runner",
                        [inputs, "--consume-evidence",
                         str(HERE / "testdata/consume-observed.json")])
    c.expect_refusal("a WRONG LAYER digest",
                     [inputs, "--consume-evidence",
                      evidence("layer", layerDigest="sha256:" + "2" * 64)],
                     "layerDigest is")
    c.expect_refusal("a WRONG LOCK digest",
                     [inputs, "--consume-evidence",
                      evidence("lock", lockSha256="3" * 64)],
                     "lockSha256 is")
    c.expect_refusal("a WRONG TRUST ROOT",
                     [inputs, "--consume-evidence",
                      evidence("trust", trustRootSha256="4" * 64)],
                     "trustRootSha256 is")
    c.expect_refusal("a WRONG MANIFEST digest in the evidence",
                     [inputs, "--consume-evidence",
                      evidence("manifest", manifestDigest="sha256:" + "5" * 64)],
                     "manifestDigest is")
    c.expect_refusal("a package count below the lock",
                     [inputs, "--consume-evidence",
                      evidence("count", installedPackages=245)],
                     "245 packages were installed")
    c.expect_refusal("an unverified signature",
                     [inputs, "--consume-evidence",
                      evidence("signatures", signaturesVerified=245)],
                     "245 signatures verified")
    c.expect_refusal("an unrecognised signer",
                     [inputs, "--consume-evidence",
                      evidence("signer", acceptedFingerprints=["DEADBEEF"])],
                     "accepted signer")

    # ── 6. live pacman resolution, in evidence and in the tree ─────────────
    c.expect_refusal("mirrors that were NOT emptied",
                     [inputs, "--consume-evidence",
                      evidence("mirrors", mirrorsEmptied=False)],
                     "mirrors were not emptied")
    c.expect_refusal("an EMPTY list of emptied mirrors",
                     [inputs, "--consume-evidence",
                      evidence("mirrors-empty", mirrorsEmptied=[])],
                     "mirrors were not emptied")
    c.expect_refusal("upstream having been consulted",
                     [inputs, "--consume-evidence",
                      evidence("upstream", upstreamConsulted=True)],
                     "upstream was consulted")
    c.expect_refusal("an installed set that is not the locked set",
                     [inputs, "--consume-evidence",
                      evidence("set", installedSetEqualsLock=False)],
                     "not exactly the locked set")
    c.expect_refusal("a tag used during acquisition",
                     [inputs, "--consume-evidence", evidence("tag", tagUsed=True)],
                     "a tag was used")
    c.expect_refusal("pacman -S instead of pacman -U",
                     [inputs, "--consume-evidence",
                      evidence("pacman", pacmanMode="pacman -S from a live mirror")],
                     "only `pacman -U`")

    # ── 7. the two-host comparator ─────────────────────────────────────────
    #
    # The same four path defects again, but between two HOSTS rather than inside
    # one delivered set. A comparator that only compares archive bytes would
    # report every one of these as "the archives differ", which is true and
    # useless; these controls require it to name the path.
    compare = str(HERE / "compare-hosts.py")
    host_a = fresh("host-a")
    host_b = fresh("host-b")
    c.expect_acceptance("two hosts that agree",
                        [compare, "--host-a", str(host_a), "--host-b", str(host_b),
                         "--report", str(tmp / "cmp-good.json")])

    host_b = fresh("host-b-missing")
    (host_b / "capability.json").unlink()
    c.expect_refusal("a path host b did not deliver",
                     [compare, "--host-a", str(host_a), "--host-b", str(host_b),
                      "--report", str(tmp / "cmp-missing.json")],
                     "different sets of paths")

    host_b = fresh("host-b-added")
    (host_b / "licenses/extra-on-b.txt").write_text("only on b\n")
    c.expect_refusal("a path only host b delivered",
                     [compare, "--host-a", str(host_a), "--host-b", str(host_b),
                      "--report", str(tmp / "cmp-added.json")],
                     "delivered by host b only")

    host_b = fresh("host-b-corrupt")
    target = host_b / "runtime/tesserafin-ffmpeg-win-x64.zip"
    target.write_bytes(target.read_bytes() + b"\x01")
    write_sums(host_b)
    c.expect_refusal("hosts that disagree on the runtime archive",
                     [compare, "--host-a", str(host_a), "--host-b", str(host_b),
                      "--report", str(tmp / "cmp-corrupt.json")],
                     "delivered paths differ between the two hosts")

    # A tree control, not an evidence control: the scripts and the workflow this
    # work owns must not CONTAIN a live-resolution or precompiled-runtime
    # acquisition in the first place.
    forbidden = [
        (r"pacman\s+-S(?![a-zA-Z])", "pacman -S (live resolution)"),
        (r"pacman\s+-Sy", "pacman -Sy (database refresh)"),
        (r"pacman\s+-Syu", "pacman -Syu (live upgrade)"),
        (r"\bchoco\s+install", "Chocolatey"),
        (r"\bwinget\s+install", "winget"),
        (r"\bvcpkg\b", "vcpkg"),
        (r"jellyfin-ffmpeg[_-][0-9].*\.zip", "a Jellyfin release asset"),
        (r"releases/download/.*ffmpeg", "a precompiled FFmpeg release asset"),
    ]
    owned = sorted([*(HERE.glob("*.sh")), *(HERE.glob("*.ps1")), *(HERE.glob("*.py")),
                    REPO_ROOT / ".github/workflows/w1-windows-ffmpeg-runtime.yml"])
    hits = []
    for path in owned:
        if not path.is_file() or path.resolve() == Path(__file__).resolve():
            continue
        # Comments and docstrings are stripped before the scan. Not to be
        # lenient: this file's own prose says "pacman -S is forbidden", and a
        # control that reads its own explanation as a violation would have to be
        # weakened until it stopped noticing real ones.
        for line, label in _scan(path, forbidden):
            hits.append(f"{path.name}:{line}: {label}")
    c.assert_true("no live-resolution or precompiled-runtime acquisition in the "
                  f"{len(owned)} owned files", not hits, "; ".join(hits))


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args(argv)

    c = Controls(args.verbose)
    with tempfile.TemporaryDirectory(prefix="w1a2-controls-") as tmpdir:
        run_controls(c, Path(tmpdir))

    print()
    if c.failed:
        print(f"W1-A2 NEGATIVE CONTROLS: FAIL — {c.failed} of "
              f"{c.passed + c.failed} did not behave as required", file=sys.stderr)
        return 1
    print(f"W1-A2 NEGATIVE CONTROLS: PASS — {c.passed} controls")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
