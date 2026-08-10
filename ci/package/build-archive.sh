#!/usr/bin/env bash
# Builds the portable archive from an already-staged payload tree (#225 / [L0]).
#
# Usage: ci/package/build-archive.sh --stage DIR --rid RID --out DIR
#
# The archive carries the SAME application and web payload as the .deb and the
# .rpm — the equivalence gate compares their tree digests — but it deliberately
# carries NO systemd unit and NO /etc content. An archive cannot create a service
# account or register a service, so it must not ship files that suggest it did.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

STAGE=""; RID=""; OUT=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --stage) STAGE="$2"; shift 2 ;;
        --rid)   RID="$2"; shift 2 ;;
        --out)   OUT="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -d "${STAGE}" ]] || pkg_die "--stage must be an existing staged tree"
[[ -n "${RID}" ]]   || pkg_die "--rid is required"
[[ -n "${OUT}" ]]   || pkg_die "--out is required"

pkg_load_pins
pkg_load_version_contract

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

PREFIX="tesserafin-server-${VERSION}-${RID}"
ROOT="${WORK}/${PREFIX}"
mkdir -p "${ROOT}/lib" "${ROOT}/share" "${ROOT}/bin"

cp -a "${STAGE}/usr/lib/tesserafin"   "${ROOT}/lib/tesserafin"
cp -a "${STAGE}/usr/share/tesserafin" "${ROOT}/share/tesserafin"
cp -a "${STAGE}/usr/share/licenses/tesserafin-server/LICENSE" "${ROOT}/LICENSE"
ln -sf ../lib/tesserafin/tesserafin "${ROOT}/bin/tesserafin"

sed -e "s|@VERSION@|${VERSION}|g" \
    -e "s|@RID@|${RID}|g" \
    -e "s|@VCS_REF@|${VCS_REF}|g" \
    -e "s|@WEB_VCS_REF@|${WEB_VCS_REF}|g" \
    -e "s|@FFMPEG_VERSION@|${FFMPEG_VERSION}|g" \
    "${PKG_REPO_ROOT}/packaging/linux/archive/README.md.in" > "${ROOT}/README.md"

pkg_clamp_mtimes "${ROOT}"

mkdir -p "${OUT}"
ARCHIVE="${OUT}/${PREFIX}.tar.gz"

# Sorted members, no owner identity, commit-clamped mtimes; `gzip -n` keeps the
# original name and timestamp out of the gzip header.
tar --create \
    --directory "${WORK}" \
    --sort=name \
    --owner=0 --group=0 --numeric-owner \
    --mtime="@${SOURCE_DATE_EPOCH}" \
    --format=gnu \
    "${PREFIX}" \
  | gzip -9 -n > "${ARCHIVE}"

touch --date="@${SOURCE_DATE_EPOCH}" "${ARCHIVE}"

pkg_log "built ${ARCHIVE} ($(pkg_sha256 "${ARCHIVE}"))"
printf '%s\n' "${ARCHIVE}"
