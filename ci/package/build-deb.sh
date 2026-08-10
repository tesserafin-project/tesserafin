#!/usr/bin/env bash
# Builds the Debian package from an already-staged payload tree (#225 / [L0]).
#
# Usage: ci/package/build-deb.sh --stage DIR --rid RID --out DIR [--revision N]
#
# --revision exists so the acceptance suite can build a synthetic higher package
# revision from the IDENTICAL payload and prove that an upgrade preserves
# configuration and state. It never changes the product version, which comes
# from SharedVersion.cs through docker/version-contract.sh.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

STAGE=""; RID=""; OUT=""; REVISION="1"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --stage)    STAGE="$2"; shift 2 ;;
        --rid)      RID="$2"; shift 2 ;;
        --out)      OUT="$2"; shift 2 ;;
        --revision) REVISION="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -d "${STAGE}" ]] || pkg_die "--stage must be an existing staged tree"
[[ -n "${RID}" ]]   || pkg_die "--rid is required"
[[ -n "${OUT}" ]]   || pkg_die "--out is required"
[[ "${REVISION}" =~ ^[0-9]+$ ]] || pkg_die "--revision must be a positive integer"

pkg_load_pins
pkg_load_version_contract

DEB_ARCH="$(pkg_deb_arch "${RID}")"
DEB_VERSION="${VERSION}-${REVISION}"

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT
ROOT="${WORK}/root"

cp -a "${STAGE}" "${ROOT}"
mkdir -p "${ROOT}/DEBIAN"

INSTALLED_SIZE_KB="$(du -sk --apparent-size "${ROOT}" | cut -f1)"

sed -e "s|@DEB_VERSION@|${DEB_VERSION}|g" \
    -e "s|@DEB_ARCH@|${DEB_ARCH}|g" \
    -e "s|@INSTALLED_SIZE_KB@|${INSTALLED_SIZE_KB}|g" \
    -e "s|@VCS_REF@|${VCS_REF}|g" \
    -e "s|@WEB_VCS_REF@|${WEB_VCS_REF}|g" \
    "${PKG_REPO_ROOT}/packaging/linux/deb/control.in" > "${ROOT}/DEBIAN/control"

# The one configuration file dpkg must never overwrite on upgrade.
printf '/etc/tesserafin/tesserafin.conf\n' > "${ROOT}/DEBIAN/conffiles"

for script in postinst prerm postrm; do
    install -m 0755 "${PKG_REPO_ROOT}/packaging/linux/deb/${script}" "${ROOT}/DEBIAN/${script}"
done

# md5sums over every packaged file, in a stable order.
( cd "${ROOT}" && find . -path ./DEBIAN -prune -o -type f -print \
    | sed 's|^\./||' | LC_ALL=C sort | xargs -r md5sum ) > "${ROOT}/DEBIAN/md5sums"

pkg_clamp_mtimes "${ROOT}"

mkdir -p "${OUT}"
DEB_FILE="${OUT}/tesserafin-server_${DEB_VERSION}_${DEB_ARCH}.deb"

# --root-owner-group makes every member root:root without needing fakeroot, so
# the build cannot inherit the build user's identity. SOURCE_DATE_EPOCH clamps
# the ar member timestamps; xz carries no timestamp of its own.
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH}" \
dpkg-deb --build --root-owner-group --uniform-compression -Zxz -z9 \
         "${ROOT}" "${DEB_FILE}" >/dev/null

pkg_log "built ${DEB_FILE} ($(pkg_sha256 "${DEB_FILE}"))"
printf '%s\n' "${DEB_FILE}"
