#!/usr/bin/env python3
"""Package and describe the win-x64 FFmpeg runtime (W1-A2, issue #236).

Produces the complete delivered set from a staged build:

    runtime/<name>.zip              the unsigned, deterministic runtime archive
    capability.json                 read from the produced binaries, not asserted
    build-configuration.txt         the configure line the binaries report
    pe-closure.json                 every PE, its architecture and its imports
    provenance.json                 what this runtime is made of, end to end
    sbom.cdx.json                   CycloneDX 1.5
    THIRD-PARTY-NOTICES.md          one entry per component, with its licence file
    licenses/...                    the referenced licence texts themselves
    source/<name>-source.tar.zst    the complete corresponding source
    SHA256SUMS                      every delivered path, including the archives

Determinism is the point, so nothing here reads the clock, the hostname, the
process id or a directory iteration order. Every timestamp is
SOURCE_DATE_EPOCH, every listing is sorted, and the CycloneDX serial number is
derived from the content rather than generated.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import stat
import subprocess
import sys
import tarfile
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parent.parent.parent
sys.path.insert(0, str(HERE))
import pe  # noqa: E402  (local module, deliberately after sys.path)

# The pinned FFmpeg baseline's commit date, the single definition of "now" for
# every artifact this project produces. Kept identical to ff_source_date_epoch()
# in ci/ffmpeg/lib.sh; asserted below rather than trusted.
SOURCE_DATE_EPOCH = 1780743384
ZIP_DATE_TIME = (2026, 6, 6, 10, 56, 24)

LICENSE_NAMES = ("COPYING", "COPYING.LIB", "COPYING.LESSER", "LICENSE", "LICENCE",
                 "LICENSE.md", "LICENSE.txt", "LICENSE.TXT", "License.txt",
                 "COPYING.txt", "COPYING.MIT", "LICENCE.txt", "LICENSE.rst",
                 "NOTICE", "COPYRIGHT", "LICENSE-BSD", "LICENSE.BSD")

# Components that carry no standalone licence file because their terms live in a
# per-file header. The representative file is named rather than searched for, and
# it is the same file ci/ffmpeg/package-runtime.sh names for the Linux runtime,
# so the two runtimes ship the same notice for the same component.
HEADER_LICENSE = {
    "nv-codec-headers": "include/ffnvcodec/nvEncodeAPI.h",
}


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def run(binary: Path, *args: str) -> str:
    """Run a produced binary and return its combined output.

    Capabilities are READ from the artifact. Nothing in this file asserts a
    capability from the configure flags: a flag says what was asked for, the
    binary says what happened.
    """
    proc = subprocess.run(
        [str(binary), "-hide_banner", *args],
        capture_output=True, text=True, check=False,
        env={**os.environ, "LC_ALL": "C", "TZ": "UTC"},
    )
    return (proc.stdout or "") + (proc.stderr or "")


# --------------------------------------------------------------------------- #
# capability
# --------------------------------------------------------------------------- #
def _listing(binary: Path, flag: str) -> list[str]:
    """Names from an `ffmpeg -encoders`-shaped listing.

    Every such listing prints a legend, then a rule of dashes, then one row per
    item as `<flags> <name> <description>`. Parsing starts after the rule; a
    parser that guesses from indentation reads the legend as data and every row
    as a header, and reports an empty capability set for a healthy binary.
    """
    names: set[str] = set()
    started = False
    for line in run(binary, flag).splitlines():
        stripped = line.strip()
        if not started:
            if stripped and set(stripped) <= {"-"}:
                started = True
            continue
        parts = stripped.split()
        if len(parts) >= 2 and not stripped.startswith("-"):
            names.add(parts[1])
    return sorted(names)


def capability(ffmpeg: Path, ffprobe: Path) -> dict:
    version = run(ffmpeg, "-version").splitlines()
    buildconf = run(ffmpeg, "-buildconf")
    hwaccels = [ln.strip() for ln in run(ffmpeg, "-hwaccels").splitlines()
                if ln.strip() and not ln.strip().startswith("Hardware")]
    protocols = [ln.strip() for ln in run(ffmpeg, "-protocols").splitlines()
                 if ln.strip() and ":" not in ln and not ln.strip().startswith("Supported")]
    encoders = _listing(ffmpeg, "-encoders")
    decoders = _listing(ffmpeg, "-decoders")
    filters = _listing(ffmpeg, "-filters")

    return {
        "probe": "w1a2-capability",
        "architecture": "win-x64",
        "note": "Every list here was read from the produced binaries. A compiled "
                "capability is what the binary can attempt, never a claim that "
                "this or any other machine has the device or driver for it.",
        "ffmpegVersion": version[0] if version else "",
        "ffprobeVersion": (run(ffprobe, "-version").splitlines() or [""])[0],
        "buildConfiguration": buildconf.strip(),
        "hwaccels": sorted(hwaccels),
        "protocols": sorted(protocols),
        "encoderCount": len(encoders),
        "decoderCount": len(decoders),
        "filterCount": len(filters),
        "encoders": encoders,
        "decoders": decoders,
        "filters": filters,
    }


# --------------------------------------------------------------------------- #
# PE closure
# --------------------------------------------------------------------------- #
def pe_closure(stage: Path) -> dict:
    binaries = sorted(p for p in stage.rglob("*")
                      if p.suffix.lower() in (".exe", ".dll") and p.is_file())
    delivered = {p.name.lower() for p in binaries}
    images = []
    for b in binaries:
        image = pe.read(b)
        d = image.to_dict()
        d["path"] = str(b.relative_to(stage)).replace("\\", "/")
        d["sha256"] = sha256_file(b)
        images.append(d)
    required = sorted({dll for i in images for dll in i["allDlls"]})
    return {
        "probe": "w1a2-pe-closure",
        "delivered": sorted(delivered),
        "required": required,
        "images": images,
    }


# --------------------------------------------------------------------------- #
# licences and notices
# --------------------------------------------------------------------------- #
def collect_licenses(components: list[dict], cache: Path, out: Path) -> list[dict]:
    """Copy each component's own licence text out of the fetched source.

    Not a curated copy kept in this repository: the text that ships is the text
    that was in the bytes the build compiled, so the notice cannot drift from
    the source it describes.
    """
    licenses_dir = out / "licenses"
    licenses_dir.mkdir(parents=True, exist_ok=True)
    records = []
    for c in components:
        name = c["name"]
        copied: list[dict] = []
        if c["sourceType"] == "git":
            root = cache / "git" / name
            found: list[Path] = []
            if root.is_dir():
                found = [root / n for n in LICENSE_NAMES if (root / n).is_file()]
                if not found:
                    for p in sorted(root.rglob("COPYING*")) + sorted(root.rglob("LICENSE*")):
                        if p.is_file() and len(p.relative_to(root).parts) <= 2:
                            found = [p]
                            break
            for p in sorted(set(found)):
                dest = licenses_dir / f"{name}-{p.name}"
                shutil.copyfile(p, dest)
                os.chmod(dest, 0o644)
                copied.append({"file": f"licenses/{dest.name}", "sha256": sha256_file(dest)})
        else:
            # Read the licence straight out of the fetched tarball. Extracting a
            # whole source tree just to copy one file would make the notice
            # depend on an unpack step, and an unpack step that silently found
            # nothing would ship a runtime with no notice at all.
            for archive in sorted((cache / "archives").glob(f"{name}-*")):
                with tarfile.open(archive) as tf:
                    members = sorted(
                        (m for m in tf.getmembers()
                         if m.isfile()
                         and len(Path(m.name).parts) <= 2
                         and Path(m.name).name in LICENSE_NAMES),
                        key=lambda m: m.name)
                    for m in members:
                        fh = tf.extractfile(m)
                        if fh is None:
                            continue
                        dest = licenses_dir / f"{name}-{Path(m.name).name}"
                        dest.write_bytes(fh.read())
                        os.chmod(dest, 0o644)
                        copied.append({"file": f"licenses/{dest.name}",
                                       "sha256": sha256_file(dest)})
                break

        if not copied and name in HEADER_LICENSE:
            relative = HEADER_LICENSE[name]
            candidate = cache / "git" / name / relative
            if candidate.is_file():
                text = candidate.read_text(errors="replace")[:8000]
                end = text.find("*/")
                if end > 0:
                    dest = licenses_dir / (
                        f"{name}-LICENSE-extracted-from-{Path(relative).name}")
                    dest.write_text(text[: end + 2], encoding="utf-8", newline="\n")
                    os.chmod(dest, 0o644)
                    copied.append({"file": f"licenses/{dest.name}",
                                   "sha256": sha256_file(dest)})

        records.append({
            "component": name,
            "declaredLicense": c.get("license", ""),
            "requiredBy": c.get("requiredBy", ""),
            "sourceType": c["sourceType"],
            "pin": c.get("sha256") or c.get("commit", ""),
            "origin": c.get("url") or c.get("repository", ""),
            "licenseFiles": copied,
        })
    return records


def write_notices(records: list[dict], ffmpeg_meta: dict, out: Path) -> Path:
    lines = [
        "# Third-party notices — Tesserafin FFmpeg runtime (win-x64)",
        "",
        "This runtime is a combined work. It is distributed under the GNU General",
        "Public License version 3 or later: FFmpeg is configured with --enable-gpl",
        "and --enable-version3, and x264 and x265 are GPL-2.0-or-later.",
        "",
        f"Baseline: {ffmpeg_meta['project']} {ffmpeg_meta['baseline']} "
        f"@ {ffmpeg_meta['commit']}",
        "",
        "The complete corresponding source for every component below is delivered",
        "alongside this runtime as `source/*-source.tar.zst`. Each licence text",
        "named here is delivered under `licenses/`.",
        "",
    ]
    for r in sorted(records, key=lambda x: x["component"]):
        lines.append(f"## {r['component']}")
        lines.append("")
        lines.append(f"- Licence: {r['declaredLicense']}")
        lines.append(f"- Source: {r['origin']}")
        lines.append(f"- Pinned at: `{r['pin']}`")
        lines.append(f"- Required by: {r['requiredBy']}")
        if r["licenseFiles"]:
            for f in r["licenseFiles"]:
                lines.append(f"- Licence text: `{f['file']}` (sha256 {f['sha256']})")
        else:
            lines.append("- Licence text: NONE FOUND IN SOURCE — see provenance.json")
        lines.append("")
    path = out / "THIRD-PARTY-NOTICES.md"
    path.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    return path


# --------------------------------------------------------------------------- #
# SBOM
# --------------------------------------------------------------------------- #
def sbom(components: list[dict], ffmpeg_meta: dict, build_revision: str,
         runtime_digest: str, archive_name: str) -> dict:
    def entry(name: str, version: str, license_: str, ref: str, hashes: list[dict]) -> dict:
        c = {
            "type": "library",
            "bom-ref": ref,
            "name": name,
            "version": version,
        }
        if license_:
            c["licenses"] = [{"license": {"name": license_}}]
        if hashes:
            c["hashes"] = hashes
        return c

    comps = [entry(
        ffmpeg_meta["project"], ffmpeg_meta["baseline"], "GPL-3.0-or-later",
        f"pkg:github/{ffmpeg_meta['project']}@{ffmpeg_meta['commit']}", [])]
    comps[0]["properties"] = [
        {"name": "tesserafin:vcsCommit", "value": ffmpeg_meta["commit"]},
        {"name": "tesserafin:repository", "value": ffmpeg_meta["repository"]},
    ]

    for c in sorted(components, key=lambda x: x["name"]):
        pin = c.get("sha256") or c.get("commit", "")
        hashes = [{"alg": "SHA-256", "content": pin}] if c.get("sha256") else []
        item = entry(c["name"], c.get("ref") or c.get("sha256", "")[:12] or "pinned",
                     c.get("license", ""), f"tesserafin:component/{c['name']}", hashes)
        item["properties"] = [
            {"name": "tesserafin:sourceType", "value": c["sourceType"]},
            {"name": "tesserafin:origin", "value": c.get("url") or c.get("repository", "")},
            {"name": "tesserafin:pin", "value": pin},
        ]
        comps.append(item)

    doc = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "version": 1,
        "metadata": {
            # Not the clock: the pinned baseline's commit time, so two runners
            # produce the same document.
            "timestamp": "2026-06-06T10:56:24Z",
            "component": {
                "type": "application",
                "bom-ref": f"tesserafin-ffmpeg@{build_revision}",
                "name": "tesserafin-ffmpeg",
                "version": build_revision,
                "hashes": [{"alg": "SHA-256", "content": runtime_digest}],
                "properties": [
                    {"name": "tesserafin:architecture", "value": "win-x64"},
                    {"name": "tesserafin:archive", "value": archive_name},
                ],
            },
            "tools": [{"name": "ci/windows/ffmpeg/package.py", "vendor": "Tesserafin"}],
        },
        "components": comps,
    }
    serial = hashlib.sha256(
        json.dumps(doc, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    doc["serialNumber"] = (f"urn:uuid:{serial[0:8]}-{serial[8:12]}-{serial[12:16]}"
                           f"-{serial[16:20]}-{serial[20:32]}")
    return doc


# --------------------------------------------------------------------------- #
# archives
# --------------------------------------------------------------------------- #
def deterministic_zip(source_dir: Path, dest: Path) -> None:
    """A zip whose bytes depend only on the files inside it.

    Fixed date, fixed external attributes, sorted entries, no directory entries
    and no extra fields. Deflate at a stated level, because the level is an
    input to the compressed bytes just as the content is.
    """
    files = sorted(p for p in source_dir.rglob("*") if p.is_file())
    dest.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(dest, "w", compression=zipfile.ZIP_DEFLATED,
                         compresslevel=9) as zf:
        for f in files:
            arcname = str(f.relative_to(source_dir)).replace(os.sep, "/")
            info = zipfile.ZipInfo(arcname, date_time=ZIP_DATE_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            executable = f.suffix.lower() == ".exe"
            mode = 0o755 if executable else 0o644
            info.external_attr = (stat.S_IFREG | mode) << 16
            info.create_system = 0  # MS-DOS, so no host OS is recorded
            zf.writestr(info, f.read_bytes())


def source_archive(cache: Path, components: list[dict], ffmpeg_meta: dict,
                   dest_tar: Path) -> None:
    """The complete corresponding source, assembled from the fetched cache.

    GPLv3 §1 asks for the preferred form for modification, so this is the
    retained source tree itself, not a list of URLs. Only the win-x64-applicable
    components go in — shipping the Linux-only ones would describe a runtime
    that was never built here.
    """
    staging = dest_tar.parent / "_source-stage"
    if staging.exists():
        shutil.rmtree(staging)
    (staging / "archives").mkdir(parents=True)
    (staging / "git").mkdir(parents=True)

    for c in components:
        name = c["name"]
        if c["sourceType"] == "tar":
            for p in sorted((cache / "archives").glob(f"{name}-*")):
                shutil.copyfile(p, staging / "archives" / p.name)
        else:
            src = cache / "git" / name
            if src.is_dir():
                shutil.copytree(src, staging / "git" / name,
                                ignore=shutil.ignore_patterns(".git"))
    ff_src = cache / "git" / "jellyfin-ffmpeg"
    shutil.copytree(ff_src, staging / "git" / "jellyfin-ffmpeg",
                    ignore=shutil.ignore_patterns(".git"))
    (staging / "SOURCE-MANIFEST.json").write_text(
        json.dumps({
            "ffmpeg": ffmpeg_meta,
            "components": [{"name": c["name"], "sourceType": c["sourceType"],
                            "pin": c.get("sha256") or c.get("commit", ""),
                            "origin": c.get("url") or c.get("repository", "")}
                           for c in sorted(components, key=lambda x: x["name"])],
        }, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")

    subprocess.run(
        ["bash", "-c",
         f'source "{REPO_ROOT}/ci/ffmpeg/lib.sh"; ff_normalize_modes "{staging}"; '
         f'ff_deterministic_tar "{staging}" "{dest_tar}" cat'],
        check=True)
    shutil.rmtree(staging)


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #
def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--stage", required=True, help="the staged runtime tree")
    ap.add_argument("--cache", required=True, help="the fetched source cache")
    ap.add_argument("--out", required=True, help="where the delivered set is written")
    ap.add_argument("--consume-evidence", required=True,
                    help="consume.json produced by the W1-R consumer")
    ap.add_argument("--repo-sha", required=True)
    ap.add_argument("--toolchain", required=True,
                    help="JSON file describing the compiler and the locked set")
    ap.add_argument("--skip-binary-probe", action="store_true",
                    help="do not execute the produced binaries (non-Windows dry run)")
    args = ap.parse_args(argv)

    stage = Path(args.stage)
    cache = Path(args.cache)
    out = Path(args.out)
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    manifest = json.loads((REPO_ROOT / "ci/ffmpeg/components.json").read_text())
    build_revision = manifest["buildRevision"]
    ffmpeg_meta = manifest["ffmpeg"]
    components = [c for c in manifest["components"]
                  if c.get("architectures") is None or "win-x64" in c["architectures"]]

    epoch = subprocess.run(
        ["bash", "-c", f'source "{REPO_ROOT}/ci/ffmpeg/lib.sh"; ff_source_date_epoch'],
        capture_output=True, text=True, check=True).stdout.strip()
    if int(epoch) != SOURCE_DATE_EPOCH:
        print(f"HARD STOP: ci/ffmpeg/lib.sh says SOURCE_DATE_EPOCH={epoch}, "
              f"package.py says {SOURCE_DATE_EPOCH}", file=sys.stderr)
        return 1

    ffmpeg_exe = stage / "bin/ffmpeg.exe"
    ffprobe_exe = stage / "bin/ffprobe.exe"
    for exe in (ffmpeg_exe, ffprobe_exe):
        if not exe.is_file():
            print(f"HARD STOP: {exe} is missing from the staged runtime", file=sys.stderr)
            return 1

    # --- what the binaries themselves say --------------------------------
    if args.skip_binary_probe:
        cap = {"probe": "w1a2-capability", "architecture": "win-x64",
               "skipped": "binaries were not executed on this host"}
        buildconf = ""
    else:
        cap = capability(ffmpeg_exe, ffprobe_exe)
        buildconf = cap["buildConfiguration"]

    licence_records = collect_licenses(components, cache, out)
    uncovered = [r["component"] for r in licence_records if not r["licenseFiles"]]
    if uncovered:
        # Fail-closed. A runtime that ships a component whose licence text nobody
        # could find is a distribution problem, not a packaging warning.
        print("HARD STOP: no licence text was found in the fetched source for: "
              + ", ".join(sorted(uncovered)), file=sys.stderr)
        return 1
    notices = write_notices(licence_records, ffmpeg_meta, out)

    # --- the runtime archive ---------------------------------------------
    archive_stage = out / "_runtime-stage"
    shutil.copytree(stage, archive_stage)
    shutil.copytree(out / "licenses", archive_stage / "LICENSES")
    shutil.copyfile(notices, archive_stage / "THIRD-PARTY-NOTICES.md")
    (archive_stage / "build-configuration.txt").write_text(
        buildconf + "\n", encoding="utf-8", newline="\n")
    (archive_stage / "capability.json").write_text(
        json.dumps(cap, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")

    archive_name = f"tesserafin-ffmpeg-{build_revision}-win-x64.zip"
    archive = out / "runtime" / archive_name
    deterministic_zip(archive_stage, archive)
    shutil.rmtree(archive_stage)
    runtime_digest = sha256_file(archive)

    # --- the corresponding source ----------------------------------------
    source_name = f"tesserafin-ffmpeg-{build_revision}-win-x64-source.tar"
    source_tar = out / "source" / source_name
    source_tar.parent.mkdir(parents=True, exist_ok=True)
    source_archive(cache, components, ffmpeg_meta, source_tar)
    source_uncompressed_digest = sha256_file(source_tar)
    subprocess.run(["zstd", "-19", "-q", "-f", "--no-progress", str(source_tar),
                    "-o", str(source_tar) + ".zst"], check=True)
    source_tar.unlink()
    source_digest = sha256_file(Path(str(source_tar) + ".zst"))

    # --- the descriptions -------------------------------------------------
    (out / "capability.json").write_text(
        json.dumps(cap, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    (out / "build-configuration.txt").write_text(
        buildconf + "\n", encoding="utf-8", newline="\n")

    closure = pe_closure(stage)
    (out / "pe-closure.json").write_text(
        json.dumps(closure, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")

    consume = json.loads(Path(args.consume_evidence).read_text())
    toolchain = json.loads(Path(args.toolchain).read_text())
    series = (cache / "git/jellyfin-ffmpeg/debian/patches/series").read_text().split()

    provenance = {
        "probe": "w1a2-provenance",
        "schemaVersion": 1,
        "buildRevision": build_revision,
        "architecture": "win-x64",
        "repositorySha": args.repo_sha,
        "buildInputs": {
            "reference": consume["reference"],
            "manifestDigest": consume["manifestDigest"],
            "layerDigest": consume["layerDigest"],
            "lockSha256": consume["lockSha256"],
            "trustRootSha256": consume["trustRootSha256"],
            "signaturesVerified": consume["signaturesVerified"],
            "acceptedFingerprints": consume["acceptedFingerprints"],
            "packageCount": consume["packageCount"],
            "installedSetEqualsLock": consume["installedSetEqualsLock"],
            "mirrorsEmptied": consume["mirrorsEmptied"],
            "upstreamConsulted": consume["upstreamConsulted"],
            "pacmanMode": consume["pacmanMode"],
            "tagUsed": consume["tagUsed"],
        },
        "ffmpeg": ffmpeg_meta,
        "patches": {
            "seriesLength": len(series),
            "applied": len(series),
            "excluded": [],
            "fuzz": 0,
            "order": "series order, top to bottom",
            "series": series,
            "seriesSha256": hashlib.sha256(
                (cache / "git/jellyfin-ffmpeg/debian/patches/series").read_bytes()).hexdigest(),
        },
        "components": [
            {"name": c["name"], "license": c.get("license", ""),
             "sourceType": c["sourceType"],
             "pin": c.get("sha256") or c.get("commit", ""),
             "origin": c.get("url") or c.get("repository", "")}
            for c in sorted(components, key=lambda x: x["name"])
        ],
        "toolchain": toolchain,
        "configuration": {
            "flagsFile": "ci/windows/ffmpeg/ffmpeg-configure.win-x64.txt",
            "flagsSha256": hashlib.sha256(
                (HERE / "ffmpeg-configure.win-x64.txt").read_bytes()).hexdigest(),
            "reportedByBinary": buildconf,
        },
        "capability": {
            "file": "capability.json",
            "sha256": hashlib.sha256(
                (out / "capability.json").read_bytes()).hexdigest(),
        },
        "runtime": {
            "archive": f"runtime/{archive_name}",
            "sha256": runtime_digest,
            "sizeBytes": archive.stat().st_size,
            "signed": False,
        },
        "correspondingSource": {
            "archive": f"source/{source_name}.zst",
            "sha256": source_digest,
            # The zstd CONTAINER can differ between two identical trees while the
            # content is identical, so the decompressed stream is recorded too and
            # the comparator checks both. This is a measured F0 behaviour, not a
            # precaution.
            "uncompressedSha256": source_uncompressed_digest,
        },
        "determinism": {
            "sourceDateEpoch": SOURCE_DATE_EPOCH,
            "jobs": int(os.environ.get("FF_JOBS", "4")),
            "lto": False,
            "peTimestampInserted": False,
        },
    }
    (out / "provenance.json").write_text(
        json.dumps(provenance, indent=2, sort_keys=True) + "\n",
        encoding="utf-8", newline="\n")

    doc = sbom(components, ffmpeg_meta, build_revision, runtime_digest, archive_name)
    (out / "sbom.cdx.json").write_text(
        json.dumps(doc, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")

    # --- the checksum manifest, covering EVERY delivered path -------------
    delivered = sorted(p for p in out.rglob("*") if p.is_file() and p.name != "SHA256SUMS")
    lines = [f"{sha256_file(p)}  {p.relative_to(out).as_posix()}" for p in delivered]
    (out / "SHA256SUMS").write_text("\n".join(lines) + "\n",
                                    encoding="utf-8", newline="\n")

    print(f"runtime  {runtime_digest}  {archive_name}")
    print(f"source   {source_digest}  {source_name}.zst")
    print(f"delivered {len(delivered) + 1} paths")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
