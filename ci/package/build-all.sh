#!/usr/bin/env bash
# Builds every native Linux artifact for one architecture (#225 / [L0]).
#
# Usage: ci/package/build-all.sh --rid linux-x64|linux-arm64 --out DIR
#
# One staged payload tree feeds all three formats, then each artifact gets a
# machine-readable provenance manifest and a line in the checksum file. Nothing
# here publishes anything: the artifacts land in --out and stay there.

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
mkdir -p "${ARTIFACTS}"

"${PKG_REPO_ROOT}/ci/package/assemble-payload.sh" --rid "${RID}" --out "${STAGE}"

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
    "packageFormat": fmt,
    "packageName": payload["packageName"],
    "packageVersion": payload["packageVersion"],
    "architecture": {
        "deb": payload["debianArchitecture"],
        "rpm": payload["rpmArchitecture"],
    }[fmt] if fmt in ("deb", "rpm") else payload["runtimeIdentifier"],
    "runtimeIdentifier": payload["runtimeIdentifier"],
    "serverCommit": payload["serverCommit"],
    "webCommit": payload["webCommit"],
    "webVersion": payload["webVersion"],
    "webAssetsImage": payload["webAssetsImage"],
    "webPayloadSha256": payload["webPayloadSha256"],
    "applicationPayloadSha256": payload["applicationPayloadSha256"],
    "stagedTreeSha256": payload["stagedTreeSha256"],
    "ffmpegVersion": payload["ffmpegVersion"],
    "ffmpegAsset": payload["ffmpegAsset"],
    "ffmpegSha256": payload["ffmpegSha256"],
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

# One checksum file per architecture, in a stable order.
( cd "${ARTIFACTS}" && \
  find . -maxdepth 1 -type f \
       \( -name "*_$(pkg_deb_arch "${RID}").deb" \
       -o -name "*.$(pkg_rpm_arch "${RID}").rpm" \
       -o -name "*-${RID}.tar.gz" \) -printf '%f\n' \
  | LC_ALL=C sort | xargs -r sha256sum ) > "${ARTIFACTS}/SHA256SUMS-${RID}.txt"

pkg_log "artifacts for ${RID}"
cat "${ARTIFACTS}/SHA256SUMS-${RID}.txt"
