#!/usr/bin/env bash
# Builds every native Linux artifact for one architecture (#225 / [L0]).
#
# Usage: ci/package/build-all.sh --rid linux-x64|linux-arm64 --out DIR
#
# ONE accepted FFmpeg runtime is built from the pinned sources, then ONE staged
# payload tree is assembled around it, then all three formats derive from that
# tree. Sharing the runtime between the .deb, the .rpm and the .tar.gz of the
# SAME architecture is what makes "the three formats carry the same encoder" a
# fact rather than a claim; sharing it across a reproducibility comparison would
# make that comparison meaningless, so ci/package/ffmpeg-runtime.sh refuses to be
# reused under PKG_REPRO=1.
#
# Nothing here publishes anything: the artifacts land in --out and stay there.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

RID=""; OUT=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid) RID="$2"; shift 2 ;;
        --out) OUT="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -n "${RID}" ]] || pkg_die "--rid is required"
[[ -n "${OUT}" ]] || pkg_die "--out is required"

pkg_load_pins
pkg_load_version_contract

mkdir -p "${OUT}"
STAGE="${OUT}/stage-${RID}"
ARTIFACTS="${OUT}/artifacts"
FFMPEG_OUT="${OUT}/ffmpeg-${RID}"
mkdir -p "${ARTIFACTS}"

# =============================================================================
# 1. The accepted FFmpeg runtime, rebuilt from the pinned sources
# =============================================================================
FFMPEG_RUNTIME="$("${PKG_REPO_ROOT}/ci/package/ffmpeg-runtime.sh" \
                    --rid "${RID}" --out "${FFMPEG_OUT}" --reuse | tail -1)"

"${PKG_REPO_ROOT}/ci/package/assemble-payload.sh" \
    --rid "${RID}" --out "${STAGE}" --ffmpeg-runtime "${FFMPEG_RUNTIME}"

DEB="$("${PKG_REPO_ROOT}/ci/package/build-deb.sh"     --stage "${STAGE}" --rid "${RID}" --out "${ARTIFACTS}" | tail -1)"
RPM="$("${PKG_REPO_ROOT}/ci/package/build-rpm.sh"     --stage "${STAGE}" --rid "${RID}" --out "${ARTIFACTS}" | tail -1)"
TGZ="$("${PKG_REPO_ROOT}/ci/package/build-archive.sh" --stage "${STAGE}" --rid "${RID}" --out "${ARTIFACTS}" | tail -1)"

# The synthetic upgrade targets: a higher package revision built from the SAME
# staged payload. Identical bytes, valid higher version — so an upgrade test
# measures the packaging lifecycle and nothing else.
"${PKG_REPO_ROOT}/ci/package/build-deb.sh" --stage "${STAGE}" --rid "${RID}" \
    --out "${ARTIFACTS}" --revision 2 >/dev/null
"${PKG_REPO_ROOT}/ci/package/build-rpm.sh" --stage "${STAGE}" --rid "${RID}" \
    --out "${ARTIFACTS}" --release 2 >/dev/null

# =============================================================================
# 2. The corresponding-source sidecar
# =============================================================================
#
# Emitted BESIDE the packages, never inside them. It is ~232 MB and identical for
# every architecture and every format; six copies of one archive is not a
# redistribution improvement, it is six chances for them to disagree.
#
# It is copied rather than moved so the runtime directory stays self-describing
# for the closure gate, and its digest is recorded here so a later release
# assembler can deduplicate the two architectures' copies by comparing digests
# rather than by trusting the filename.
SIDECAR="${ARTIFACTS}/${F0_SOURCE_ARCHIVE}"
cp -a "${FFMPEG_RUNTIME}/${F0_SOURCE_ARCHIVE}" "${SIDECAR}"
touch --date="@${SOURCE_DATE_EPOCH}" "${SIDECAR}"
SIDECAR_SHA="$(pkg_sha256 "${SIDECAR}")"

# The runtime archive travels too: it is what an independent party rebuilds and
# compares, and it is the only artifact that carries the runtime's own manifests
# in the form F0 accepted them.
RUNTIME_ARCHIVE_NAME="$(pkg_f0_runtime_archive_name "${RID}")"
cp -a "${FFMPEG_RUNTIME}/${RUNTIME_ARCHIVE_NAME}" "${ARTIFACTS}/${RUNTIME_ARCHIVE_NAME}"
touch --date="@${SOURCE_DATE_EPOCH}" "${ARTIFACTS}/${RUNTIME_ARCHIVE_NAME}"

# =============================================================================
# 3. Provenance
# =============================================================================
# The payload manifest assemble-payload.sh wrote carries the shared provenance;
# each artifact manifest is that plus the artifact's own identity.
PAYLOAD_JSON="${STAGE}.payload.json"

emit_manifest() { # <artifact path> <format>
    local artifact="$1" format="$2" name sha size
    name="$(basename "${artifact}")"
    sha="$(pkg_sha256 "${artifact}")"
    size="$(stat -c '%s' "${artifact}")"

    python3 - "${PAYLOAD_JSON}" "${format}" "${name}" "${sha}" "${size}" \
             "${ARTIFACTS}/${name}.provenance.json" <<'PY'
import json, sys

payload_path, fmt, name, sha, size, dest = sys.argv[1:7]
payload = json.load(open(payload_path))

manifest = {
    "schemaVersion": 2,
    "packageFormat": fmt,
    "packageName": payload["packageName"],
    "packageVersion": payload["packageVersion"],
    "architecture": {
        "deb": payload["debianArchitecture"],
        "rpm": payload["rpmArchitecture"],
    }.get(fmt, payload["runtimeIdentifier"]),
    "runtimeIdentifier": payload["runtimeIdentifier"],

    # Source provenance. Three distinct works, recorded distinctly: the FFmpeg
    # corresponding-source archive does NOT contain the server or the web source,
    # and a manifest that blurred them would imply a redistribution closure that
    # does not exist.
    "serverCommit": payload["serverCommit"],
    "serverRepository": payload["serverRepository"],
    "webCommit": payload["webCommit"],
    "webVersion": payload["webVersion"],
    "webRepository": payload["webRepository"],
    "webAssetsImage": payload["webAssetsImage"],
    "webPayloadSha256": payload["webPayloadSha256"],

    # The FFmpeg runtime, identified by BUILD REVISION rather than by an
    # upstream version string: "7.1.4-3" is the upstream baseline this fork
    # tracks and "7.1.4-tesserafin.1" is what is actually installed. A single
    # ambiguous "ffmpegVersion" field could not tell those apart, which is
    # exactly how a package once claimed to carry a runtime it did not build.
    "ffmpegRuntime": payload["ffmpegRuntime"],

    "licensing": {
        "server": payload["serverLicense"],
        "ffmpegRuntime": payload["ffmpegRuntime"]["license"],
        "spdxExpression": "GPL-2.0-or-later AND GPL-3.0-or-later",
        "note": "Separately licensed works in one artifact. The server is not "
                "relicensed by being distributed alongside the runtime.",
    },

    "applicationPayloadSha256": payload["applicationPayloadSha256"],
    "stagedTreeSha256": payload["stagedTreeSha256"],
    "sourceDateEpoch": payload["sourceDateEpoch"],
    "buildTimestamp": payload["buildTimestamp"],
    "toolchain": payload["toolchain"],
    "artifactFilename": name,
    "artifactSizeBytes": int(size),
    "artifactSha256": sha,
}
with open(dest, "w") as handle:
    json.dump(manifest, handle, indent=2, sort_keys=True)
    handle.write("\n")
PY
}

emit_manifest "${DEB}" deb
emit_manifest "${RPM}" rpm
emit_manifest "${TGZ}" tar.gz

# =============================================================================
# 4. The delivered inventory and its checksums
# =============================================================================
#
# One checksum file per architecture, in a stable order, covering EVERYTHING a
# later release assembler receives: the three artifacts, the two synthetic
# upgrade packages, the runtime archive and the corresponding-source sidecar.
# The sidecar's digest belongs here specifically so a recipient can verify the
# source archive against the same manifest that describes the binaries.
( cd "${ARTIFACTS}" && \
  find . -maxdepth 1 -type f \
       \( -name "*_$(pkg_deb_arch "${RID}").deb" \
       -o -name "*.$(pkg_rpm_arch "${RID}").rpm" \
       -o -name "*-${RID}.tar.gz" \
       -o -name "${RUNTIME_ARCHIVE_NAME}" \
       -o -name "${F0_SOURCE_ARCHIVE}" \) -printf '%f\n' \
  | LC_ALL=C sort | xargs -r sha256sum ) > "${ARTIFACTS}/SHA256SUMS-${RID}.txt"

pkg_log "artifacts for ${RID}"
cat "${ARTIFACTS}/SHA256SUMS-${RID}.txt"
pkg_log "corresponding source ${F0_SOURCE_ARCHIVE} ${SIDECAR_SHA} (architecture-independent)"
