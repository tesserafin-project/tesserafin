#!/usr/bin/env python3
"""Refuse a win-x64 FFmpeg runtime that does not hold (W1-A3, issue #236).

Runs against the DELIVERED SET, not against the build that produced it, and
runs anywhere: every check here reads bytes and JSON, so the whole gate can be
exercised on a workstation before a single hosted minute is spent. The checks
that need Windows to answer — a media round trip, relocation, a PATH with no
system FFmpeg on it — live in accept-runtime.ps1 instead of being faked here.

What it refuses:

  * a binary that is not PE32+ x86-64;
  * an import, ordinary or delay-loaded, that is neither a declared system DLL
    nor a delivered file;
  * a runtime whose TLS is not Schannel, or that carries OpenSSL, GnuTLS or
    anything FFmpeg classifies as nonfree;
  * an MSYS2 or MinGW runtime DLL in the closure;
  * a workspace, runner or build-root path embedded in a delivered binary, in
    UTF-8 or UTF-16;
  * a PE carrying a link timestamp, which two runners can never agree on;
  * a delivered set whose checksum manifest, provenance, SBOM, notices or
    licence texts do not describe exactly what was delivered.

Usage:
    verify-runtime.py --delivered DIR [--workspace PATH]...
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import pe  # noqa: E402

REPO_ROOT = HERE.parent.parent.parent

# MSYS2/MinGW runtime libraries. None of these may appear: the build links them
# statically, and one showing up means the delivered runtime needs a file from
# the build machine's MSYS2 installation to start.
MSYS_RUNTIME = (
    "libwinpthread-1.dll", "libc++.dll", "libunwind.dll", "libgcc_s_seh-1.dll",
    "libgcc_s_dw2-1.dll", "libstdc++-6.dll", "msys-2.0.dll", "libatomic-1.dll",
    "libssp-0.dll", "libomp.dll",
)
# TLS and nonfree material that must not be reachable from this runtime.
FORBIDDEN_SUBSTRINGS = (
    "libssl", "libcrypto", "libgnutls", "gnutls", "nettle", "hogweed",
    "libtasn1", "fdk-aac", "fdk_aac", "libp11-kit",
)

# Paths that identify the machine that built the artifact rather than the
# artifact. Checked in UTF-8 and UTF-16LE: a .NET-adjacent lesson learned the
# hard way on this project is that an ASCII-only string scan reads a
# contaminated binary as clean.
HOST_PATH_MARKERS = (
    "D:\\a\\", "D:/a/", "/d/a/",
    "C:\\a\\", "C:/a/",
    "msys64", "MSYS64",
    "/home/runner", "C:\\Users\\runneradmin", "C:/Users/runneradmin",
    "runneradmin", "RUNNER_TEMP",
    "/c/tf-ffbuild", "C:/tf-ffbuild", "C:\\tf-ffbuild",
    "/c/tf-ffinstall", "C:/tf-ffinstall",
    "_temp\\msys", "_temp/msys",
)

# Compiled surfaces the contract requires the configure line to show.
REQUIRED_CONFIGURE = (
    "--enable-gpl", "--enable-version3", "--disable-nonfree",
    "--disable-libfdk-aac", "--enable-schannel", "--disable-openssl",
    "--disable-gnutls", "--enable-dxva2", "--enable-d3d11va", "--enable-d3d12va",
    "--enable-amf", "--enable-libvpl", "--enable-ffnvcodec", "--enable-nvenc",
    "--enable-nvdec", "--enable-cuvid", "--enable-libx264", "--enable-libx265",
    "--enable-libsvtav1", "--enable-libdav1d", "--enable-libvpx",
    "--enable-libzimg", "--enable-libass", "--enable-fontconfig",
)
FORBIDDEN_CONFIGURE = (
    "--enable-nonfree", "--enable-libfdk-aac", "--enable-openssl",
    "--enable-gnutls", "--enable-lto", "--enable-vaapi", "--enable-libdrm",
)
# Named in Tesserafin.MediaEncoding EncoderValidator; the whole reason the
# runtime is built from the Jellyfin fork rather than from upstream FFmpeg.
REQUIRED_FILTERS = ("tonemapx", "zscale", "ass", "subtitles", "scale", "overlay",
                    "alphasrc")
REQUIRED_ENCODERS = ("libx264", "libx265", "libsvtav1", "aac", "libmp3lame",
                     "libopus")


class Gate:
    def __init__(self) -> None:
        self.failures = 0

    def fail(self, message: str) -> None:
        print(f"  FAIL: {message}", file=sys.stderr)
        self.failures += 1

    def ok(self, message: str) -> None:
        print(f"  ok  : {message}")


def load_allowlist() -> tuple[set[str], list[str]]:
    exact: set[str] = set()
    prefixes: list[str] = []
    for line in (HERE / "allowed-system-dlls.txt").read_text().splitlines():
        line = line.split("#", 1)[0].strip().lower()
        if not line:
            continue
        if line.endswith("-"):
            prefixes.append(line)
        else:
            exact.add(line)
    return exact, prefixes


def allowed(dll: str, exact: set[str], prefixes: list[str]) -> bool:
    dll = dll.lower()
    return dll in exact or any(dll.startswith(p) for p in prefixes)


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def check_binaries(gate: Gate, stage: Path, extra_markers: list[str]) -> None:
    exact, prefixes = load_allowlist()
    binaries = sorted(p for p in stage.rglob("*")
                      if p.suffix.lower() in (".exe", ".dll") and p.is_file())
    if not binaries:
        gate.fail(f"no PE binaries under {stage}")
        return
    delivered = {p.name.lower() for p in binaries}

    for b in binaries:
        rel = b.relative_to(stage).as_posix()
        try:
            image = pe.read(b)
        except pe.PEError as exc:
            gate.fail(f"{rel}: {exc}")
            continue

        if not image.is_pe32_plus or not image.is_amd64:
            fmt = "PE32+" if image.is_pe32_plus else f"magic 0x{image.magic:03x}"
            gate.fail(f"{rel} is {fmt} {image.machine_name}, not PE32+ x86-64")
        else:
            gate.ok(f"{rel}: PE32+ x86-64")

        if image.timestamp not in (0,):
            gate.fail(f"{rel} carries link timestamp {image.timestamp}; "
                      "-Wl,--no-insert-timestamp did not take and two runners "
                      "cannot agree on the bytes")
        else:
            gate.ok(f"{rel}: no link timestamp")

        # Counted, then reported. A check that only ever speaks when it fails
        # leaves no evidence that it ran: the hosted log and the delivered
        # artifact look the same whether the closure was computed or skipped.
        ordinary = sorted(image.imports)
        delayed = sorted(image.delay_imports)
        refused = 0
        system_admitted, delivered_beside = 0, 0
        for dll in image.all_dlls():
            if allowed(dll, exact, prefixes):
                system_admitted += 1
                continue
            if dll in delivered:
                delivered_beside += 1
                continue
            gate.fail(f"{rel} imports {dll}, which is neither a declared system "
                      f"DLL nor delivered beside it")
            refused += 1
        for dll in image.all_dlls():
            if dll in MSYS_RUNTIME:
                gate.fail(f"{rel} imports the MSYS2/MinGW runtime {dll}")
                refused += 1
            for bad in FORBIDDEN_SUBSTRINGS:
                if bad in dll:
                    gate.fail(f"{rel} imports {dll}, which matches forbidden '{bad}'")
                    refused += 1
        if refused == 0:
            gate.ok(f"{rel}: import closure — {len(ordinary)} ordinary and "
                    f"{len(delayed)} delay-imported DLL(s) inspected, "
                    f"{system_admitted} declared system import(s) admitted, "
                    f"{delivered_beside} delivered beside the image, 0 refused")

        blob = b.read_bytes()
        for marker in (*HOST_PATH_MARKERS, *extra_markers):
            for encoding, label in (("utf-8", "UTF-8"), ("utf-16-le", "UTF-16LE"),
                                    ("utf-16-be", "UTF-16BE")):
                needle = marker.encode(encoding)
                if not needle:
                    continue
                at = blob.find(needle)
                if at < 0:
                    continue
                # The bytes AROUND the hit, the way ci/ffmpeg/verify-runtime.sh
                # has always reported them on Linux. The marker alone says a
                # build path survived; the context says which kind, and they
                # need different answers. A path with a source file after it is
                # a compiled __FILE__, which -ffile-prefix-map can reach. A bare
                # path is a generated config or resource string, which no
                # compiler flag will ever rewrite.
                width = 90 * (2 if encoding.startswith("utf-16") else 1)
                terminator = b"\x00\x00" if encoding.startswith("utf-16") else b"\x00"
                ctx = blob[at:at + width].split(terminator)[0]
                gate.fail(f"{rel} embeds build-host path '{marker}' ({label}): "
                          + ctx.decode(encoding, "replace").strip())

    # Schannel, positively.
    ffmpeg = stage / "bin/ffmpeg.exe"
    if ffmpeg.is_file():
        image = pe.read(ffmpeg)
        if "secur32.dll" in image.all_dlls():
            gate.ok("ffmpeg.exe imports secur32.dll — Schannel is linked, not merely configured")
        else:
            gate.fail("ffmpeg.exe does not import secur32.dll; Schannel is the "
                      "runtime's only TLS provider and must be present in the "
                      "import table as well as in the configure line")
    else:
        gate.fail("bin/ffmpeg.exe is missing from the delivered runtime")


def check_configuration(gate: Gate, delivered: Path) -> None:
    conf_path = delivered / "build-configuration.txt"
    if not conf_path.is_file():
        gate.fail("build-configuration.txt is missing")
        return
    conf = conf_path.read_text()
    if not conf.strip():
        gate.fail("build-configuration.txt is empty; the binary reported no "
                  "configure line")
        return
    for flag in REQUIRED_CONFIGURE:
        if flag not in conf:
            gate.fail(f"the binary's own configure line does not contain {flag}")
    for flag in FORBIDDEN_CONFIGURE:
        if flag in conf:
            gate.fail(f"the binary's own configure line contains {flag}")
    gate.ok("the configure line the binary reports matches the contract")


def check_capability(gate: Gate, delivered: Path) -> None:
    cap_path = delivered / "capability.json"
    if not cap_path.is_file():
        gate.fail("capability.json is missing")
        return
    cap = json.loads(cap_path.read_text())
    if cap.get("skipped"):
        gate.fail("capability.json was produced without executing the binaries; "
                  "a capability manifest that nothing read is not evidence")
        return
    filters = set(cap.get("filters", []))
    encoders = set(cap.get("encoders", []))
    for f in REQUIRED_FILTERS:
        if f not in filters:
            gate.fail(f"filter '{f}' is absent from the produced binary")
    for e in REQUIRED_ENCODERS:
        if e not in encoders:
            gate.fail(f"encoder '{e}' is absent from the produced binary")
    if any("fdk" in name.lower() for name in encoders | set(cap.get("decoders", []))):
        gate.fail("an fdk codec is present in the produced binary")
    else:
        gate.ok("no fdk codec in the produced binary")
    if "tonemapx" in filters:
        gate.ok("tonemapx is present — the Jellyfin fork baseline landed")


def check_delivered_set(gate: Gate, delivered: Path) -> None:
    sums = delivered / "SHA256SUMS"
    if not sums.is_file():
        gate.fail("SHA256SUMS is missing")
        return
    recorded: dict[str, str] = {}
    for line in sums.read_text().splitlines():
        if not line.strip():
            continue
        digest, _, rel = line.partition("  ")
        recorded[rel] = digest
    actual = {p.relative_to(delivered).as_posix()
              for p in delivered.rglob("*") if p.is_file() and p.name != "SHA256SUMS"}
    missing = sorted(set(recorded) - actual)
    extra = sorted(actual - set(recorded))
    for m in missing:
        gate.fail(f"SHA256SUMS names {m}, which was not delivered")
    for e in extra:
        gate.fail(f"{e} was delivered but is not in SHA256SUMS")
    bad = 0
    for rel, digest in sorted(recorded.items()):
        p = delivered / rel
        if p.is_file() and sha256_file(p) != digest:
            gate.fail(f"{rel} does not match its recorded digest")
            bad += 1
    if not missing and not extra and not bad:
        gate.ok(f"SHA256SUMS covers all {len(recorded)} delivered paths and every "
                "digest matches")

    prov_path = delivered / "provenance.json"
    if not prov_path.is_file():
        gate.fail("provenance.json is missing")
        return
    prov = json.loads(prov_path.read_text())

    for key, field in (("runtime", "archive"), ("correspondingSource", "archive")):
        rel = prov[key][field]
        p = delivered / rel
        if not p.is_file():
            gate.fail(f"provenance names {rel}, which was not delivered")
        elif sha256_file(p) != prov[key]["sha256"]:
            gate.fail(f"{rel} does not match the digest provenance records")
        else:
            gate.ok(f"{rel} matches provenance ({prov[key]['sha256'][:16]}…)")

    for field in ("repositorySha", "buildRevision"):
        if not prov.get(field):
            gate.fail(f"provenance does not bind {field}")
    inputs = prov.get("buildInputs", {})
    for field in ("reference", "manifestDigest", "layerDigest", "lockSha256",
                  "trustRootSha256", "packageCount"):
        if not inputs.get(field):
            gate.fail(f"provenance does not bind buildInputs.{field}")
    if inputs.get("tagUsed") is not False:
        gate.fail("provenance does not record tagUsed=false")
    if inputs.get("upstreamConsulted") is not False:
        gate.fail("provenance does not record upstreamConsulted=false")
    if inputs.get("installedSetEqualsLock") is not True:
        gate.fail("provenance does not record installedSetEqualsLock=true")
    if "@sha256:" not in str(inputs.get("reference", "")):
        gate.fail(f"the build-input reference is not digest-pinned: {inputs.get('reference')}")

    # The applied set is checked against ci/ffmpeg/fork-patches.json — the base
    # decision plus win-x64's declared exceptions — not against a literal. A
    # runtime that quietly skipped a patch, or quietly took one the policy
    # refuses, must be a different verdict from one that did what was declared.
    catalogue = json.loads((REPO_ROOT / "ci/ffmpeg/fork-patches.json").read_text())
    expected_applied, expected_excluded, expected_exceptions = [], [], []
    for entry in catalogue["patches"]:
        applied = entry["applied"]
        for exc in entry.get("platformExceptions", []):
            if exc["platform"] == "win-x64":
                applied = exc["applied"]
                expected_exceptions.append(entry["patch"])
        (expected_applied if applied else expected_excluded).append(entry["patch"])

    patches = prov.get("patches", {})
    if patches.get("seriesLength") != len(catalogue["patches"]):
        gate.fail(f"provenance records a series of {patches.get('seriesLength')} "
                  f"patches; the catalogue classifies {len(catalogue['patches'])}")
    elif patches.get("applied") != len(expected_applied):
        gate.fail(f"provenance records {patches.get('applied')} patches applied; "
                  f"the win-x64 decision is {len(expected_applied)}")
    elif sorted(patches.get("excluded") or []) != sorted(expected_excluded):
        gate.fail(f"provenance records excluded={sorted(patches.get('excluded') or [])}; "
                  f"the win-x64 decision excludes {sorted(expected_excluded)}")
    elif patches.get("fuzz") != 0:
        gate.fail(f"provenance records fuzz={patches.get('fuzz')}")
    else:
        gate.ok(f"provenance binds {patches['applied']}/{patches['seriesLength']} "
                f"patches, zero fuzz, {len(expected_excluded)} excluded")

    # The exception record itself: present, closed to unknown fields, and
    # agreeing with the catalogue. `excluded: []` alone can mean "nothing
    # diverged" or "everything diverged and nobody said so"; this separates them.
    recorded_exceptions = patches.get("platformExceptions")
    exception_keys = {"patch", "platform", "baseClassification", "baseApplied",
                      "applied", "rationale", "compensatingControls"}
    if recorded_exceptions is None:
        gate.fail("provenance records no patches.platformExceptions list, so a "
                  "divergence from the base patch policy would be invisible")
    elif sorted(e.get("patch") for e in recorded_exceptions) != sorted(expected_exceptions):
        gate.fail(f"provenance records platform exceptions for "
                  f"{sorted(str(e.get('patch')) for e in recorded_exceptions)}; the "
                  f"catalogue declares {sorted(expected_exceptions)}")
    else:
        bad = False
        for e in recorded_exceptions:
            unknown = set(e) - exception_keys
            missing = exception_keys - set(e)
            if unknown or missing:
                gate.fail(f"the platform exception for {e.get('patch')} has "
                          f"unknown {sorted(unknown)} / missing {sorted(missing)} field(s)")
                bad = True
                continue
            if e["platform"] != "win-x64":
                gate.fail(f"the delivered provenance carries an exception for "
                          f"{e['platform']}, which is not this runtime's platform")
                bad = True
            if e["applied"] is e["baseApplied"]:
                gate.fail(f"the platform exception for {e['patch']} decides what "
                          "the base decision already decided")
                bad = True
            if not str(e.get("rationale", "")).strip() or not e.get("compensatingControls"):
                gate.fail(f"the platform exception for {e['patch']} carries no "
                          "rationale or no compensating controls")
                bad = True
        if not bad:
            if recorded_exceptions:
                gate.ok(f"{len(recorded_exceptions)} declared platform exception(s), "
                        "each with a rationale and compensating controls: "
                        + ", ".join(f"{e['patch']} "
                                    f"{'applied' if e['applied'] else 'excluded'}"
                                    for e in recorded_exceptions))
            else:
                gate.ok("provenance declares no platform exception to the base "
                        "patch decision")

    # Component patches: the provenance must describe exactly the series in the
    # tree, digests included. A build that applied a patch the repository does
    # not carry, or carried one it did not apply, is not the build this pull
    # request reviews.
    series_path = HERE / "patches/series.txt"
    declared = []
    if series_path.is_file():
        for line in series_path.read_text().splitlines():
            line = line.strip()
            if line and not line.startswith("#"):
                parts = line.split(None, 2)
                if len(parts) >= 2:
                    declared.append((parts[0], parts[1]))
    recorded = prov.get("componentPatches")
    if recorded is None:
        gate.fail("provenance records no componentPatches list")
    elif len(recorded) != len(declared):
        gate.fail(f"provenance records {len(recorded)} component patches, "
                  f"the series declares {len(declared)}")
    else:
        for (component, name), entry in zip(declared, recorded):
            if entry.get("component") != component or not entry.get("patch", "").endswith(name):
                gate.fail(f"provenance patch entry {entry.get('patch')} does not "
                          f"match the series entry {component}/{name}")
                continue
            actual = sha256_file(HERE / "patches" / name)
            if entry.get("sha256") != actual:
                gate.fail(f"{name} hashes to {actual[:16]}…, provenance records "
                          f"{str(entry.get('sha256'))[:16]}…")
        if not gate.failures:
            gate.ok(f"{len(declared)} component patch(es), each matching the "
                    "committed series by digest")

    # Every win-x64 component must appear in the SBOM, in the notices, and have a
    # licence text delivered.
    manifest = json.loads((REPO_ROOT / "ci/ffmpeg/components.json").read_text())
    win = [c for c in manifest["components"]
           if c.get("architectures") is None or "win-x64" in c["architectures"]]
    sbom_path = delivered / "sbom.cdx.json"
    notices_path = delivered / "THIRD-PARTY-NOTICES.md"
    if not sbom_path.is_file():
        gate.fail("sbom.cdx.json is missing")
    elif not notices_path.is_file():
        gate.fail("THIRD-PARTY-NOTICES.md is missing")
    else:
        sbom = json.loads(sbom_path.read_text())
        named = {c["name"] for c in sbom.get("components", [])}
        notices = notices_path.read_text()
        licence_dir = delivered / "licenses"
        for c in win:
            if c["name"] not in named:
                gate.fail(f"component {c['name']} is absent from the SBOM")
            if f"## {c['name']}" not in notices:
                gate.fail(f"component {c['name']} is absent from the notices")
            if not sorted(licence_dir.glob(f"{c['name']}-*")):
                gate.fail(f"no licence text was delivered for {c['name']}")
        if sbom.get("specVersion") != "1.5" or sbom.get("bomFormat") != "CycloneDX":
            gate.fail("sbom.cdx.json is not a CycloneDX 1.5 document")
        else:
            gate.ok(f"CycloneDX 1.5 SBOM names {len(named)} components")
        for line in notices.splitlines():
            if line.startswith("- Licence text: `") and "sha256" in line:
                rel = line.split("`")[1]
                if not (delivered / rel).is_file():
                    gate.fail(f"the notices reference {rel}, which was not delivered")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--delivered", required=True)
    ap.add_argument("--stage", help="the staged runtime tree (defaults to "
                                    "<delivered>/../stage if present)")
    ap.add_argument("--workspace", action="append", default=[],
                    help="an additional path that must not appear in any binary")
    args = ap.parse_args(argv)

    delivered = Path(args.delivered)
    if not delivered.is_dir():
        print(f"not a directory: {delivered}", file=sys.stderr)
        return 2

    gate = Gate()
    stage = Path(args.stage) if args.stage else delivered
    print("== binaries")
    check_binaries(gate, stage, args.workspace)
    print("== build configuration")
    check_configuration(gate, delivered)
    print("== capability, read from the binaries")
    check_capability(gate, delivered)
    print("== the delivered set")
    check_delivered_set(gate, delivered)

    print()
    if gate.failures:
        print(f"WIN-X64 RUNTIME: FAIL — {gate.failures} check(s) failed", file=sys.stderr)
        return 1
    print("WIN-X64 RUNTIME: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
