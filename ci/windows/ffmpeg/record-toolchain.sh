#!/usr/bin/env bash
# Record the toolchain the win-x64 runtime was built with (W1-A3 / #236).
#
# The compiler is an INPUT to this build, so provenance has to name it as
# precisely as it names the sources.
#
# TWO files, and the split is the point:
#
#   toolchain.json  goes INTO the delivered provenance. Everything in it comes
#                   from the digest-pinned package set, so two runners must
#                   agree on it byte for byte. Nothing host-specific may enter
#                   here or the reproducibility comparison would fail for a
#                   reason that has nothing to do with the artifact.
#
#   runner.json     stays OUTSIDE the delivered set. The runner image version,
#                   the machine name and the core count belong to the host, not
#                   to the runtime. The comparator prints both so that a
#                   byte-identical result across two DIFFERENT image versions is
#                   visible as the stronger result it is — and so a divergence
#                   names its likely cause instead of leaving it to be guessed.
#
# Usage: record-toolchain.sh <toolchain.json> [runner.json]

set -euo pipefail

OUT="${1:?usage: record-toolchain.sh <toolchain.json> [runner.json]}"
RUNNER_OUT="${2:-$(dirname "${OUT}")/runner.json}"
mkdir -p "$(dirname "${OUT}")" "$(dirname "${RUNNER_OUT}")"

# `tr -d '\r'`: clang, cmake and ninja are native Windows programs and end their
# lines with CRLF, so without this every recorded version would carry a trailing
# carriage return into the delivered provenance as a literal \r escape.
version_of() { # <command> [args...]
    if command -v "$1" >/dev/null 2>&1; then
        "$@" 2>&1 | head -1 | tr -d '\r'
    else
        printf 'ABSENT\n'
    fi
}

# The installed set from pacman's own database rather than from the lock file:
# the lock says what should be there, this says what is.
installed="$(pacman -Q 2>/dev/null | wc -l | tr -d ' ')"
installed_sha="$(pacman -Q 2>/dev/null | sort | sha256sum | cut -d' ' -f1)"

python3 - "${OUT}" "${RUNNER_OUT}" \
    "$(version_of clang --version)" \
    "$(version_of ld.lld --version)" \
    "$(version_of llvm-ar --version)" \
    "$(version_of cmake --version)" \
    "$(version_of meson --version)" \
    "$(version_of ninja --version)" \
    "$(version_of nasm -v)" \
    "$(version_of yasm --version)" \
    "$(version_of python3 --version)" \
    "$(version_of pkgconf --version)" \
    "${installed}" "${installed_sha}" <<'PY'
import json, os, platform, sys

(out, runner_out, clang, lld, ar, cmake, meson, ninja, nasm, yasm, python,
 pkgconf, installed, installed_sha) = sys.argv[1:15]

uname = platform.uname()

toolchain = {
    "probe": "winx64-toolchain",
    "msystem": os.environ.get("MSYSTEM", ""),
    "architecture": "win-x64",
    "native": True,
    "wine": False,
    "crossBuild": False,
    "system": uname.system,
    "machine": uname.machine,
    "compiler": clang,
    "linker": lld,
    "archiver": ar,
    "cmake": cmake,
    "meson": meson,
    "ninja": ninja,
    "nasm": nasm,
    "yasm": yasm,
    "python": python,
    "pkgconf": pkgconf,
    "installedPackages": int(installed or 0),
    "installedListSha256": installed_sha,
}
runner = {
    "probe": "winx64-runner",
    "note": "host identity, deliberately NOT part of the delivered set",
    "imageOs": os.environ.get("ImageOS", ""),
    "imageVersion": os.environ.get("ImageVersion", ""),
    "runnerOs": os.environ.get("RUNNER_OS", ""),
    "runnerArch": os.environ.get("RUNNER_ARCH", ""),
    "runnerName": os.environ.get("RUNNER_NAME", ""),
    "node": uname.node,
    "release": uname.release,
    "version": uname.version,
    "processors": os.cpu_count(),
}
for path, doc in ((out, toolchain), (runner_out, runner)):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, indent=2, sort_keys=True)
        fh.write("\n")
print(f"toolchain: {clang}; {installed} packages installed")
print(f"runner image: {runner['imageOs']} {runner['imageVersion']}")
PY
