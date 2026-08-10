#!/usr/bin/env bash
# Builds the RPM package from an already-staged payload tree (#225 / [L0]).
#
# Usage: ci/package/build-rpm.sh --stage DIR --rid RID --out DIR [--release N]
#
# rpmbuild runs inside a container image pinned by digest, so the packaging
# toolchain is a build input like any other and a workstation's rpm version
# cannot change the artifact. --release is the RPM counterpart of the Debian
# revision and exists for the synthetic upgrade test.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

STAGE=""; RID=""; OUT=""; RELEASE="1"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --stage)   STAGE="$2"; shift 2 ;;
        --rid)     RID="$2"; shift 2 ;;
        --out)     OUT="$2"; shift 2 ;;
        --release) RELEASE="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -d "${STAGE}" ]] || pkg_die "--stage must be an existing staged tree"
[[ -n "${RID}" ]]   || pkg_die "--rid is required"
[[ -n "${OUT}" ]]   || pkg_die "--out is required"
[[ "${RELEASE}" =~ ^[0-9]+$ ]] || pkg_die "--release must be a positive integer"

pkg_load_pins
pkg_load_version_contract

RPM_ARCH="$(pkg_rpm_arch "${RID}")"

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT
mkdir -p "${WORK}/SPECS" "${WORK}/RPMS" "${WORK}/stage"

cp -a "${STAGE}/." "${WORK}/stage/"

# A changelog date must be a real date, and it has to be derived from the commit
# rather than from the clock, or the header differs between two clean builds.
CHANGELOG_DATE="$(LC_ALL=C date -u -d "@${SOURCE_DATE_EPOCH}" +'%a %b %d %Y')"

sed -e "s|@RPM_VERSION@|${VERSION}|g" \
    -e "s|@RPM_RELEASE@|${RELEASE}|g" \
    -e "s|@RPM_CHANGELOG_DATE@|${CHANGELOG_DATE}|g" \
    -e "s|@VCS_REF@|${VCS_REF}|g" \
    -e "s|@WEB_VCS_REF@|${WEB_VCS_REF}|g" \
    "${PKG_REPO_ROOT}/packaging/linux/rpm/tesserafin-server.spec.in" \
    > "${WORK}/SPECS/tesserafin-server.spec"

BUILDER="$(pkg_rpm_builder_image)"
pkg_log "running rpmbuild ${RPM_BUILDER_RPM_VERSION} in ${BUILDER}"
# rpmbuild is extremely chatty on stderr; keep the log and surface it only when
# something actually fails.
RPMBUILD_LOG="${WORK}/rpmbuild.log"
if ! docker run --rm \
    --user "$(id -u):$(id -g)" \
    --env SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH}" \
    --volume "${WORK}:/work" \
    "${BUILDER}" \
    rpmbuild \
        --define "_topdir /work" \
        --define "_stagedir /work/stage" \
        --define "_rpmdir /work/RPMS" \
        --define "_rpmfilename %%{NAME}-%%{VERSION}-%%{RELEASE}.%%{ARCH}.rpm" \
        --target "${RPM_ARCH}" \
        -bb /work/SPECS/tesserafin-server.spec \
    >"${RPMBUILD_LOG}" 2>&1; then
    cat "${RPMBUILD_LOG}" >&2
    pkg_die "rpmbuild failed"
fi

mkdir -p "${OUT}"
RPM_FILE="${OUT}/tesserafin-server-${VERSION}-${RELEASE}.${RPM_ARCH}.rpm"
mv "${WORK}/RPMS/tesserafin-server-${VERSION}-${RELEASE}.${RPM_ARCH}.rpm" "${RPM_FILE}"
touch --date="@${SOURCE_DATE_EPOCH}" "${RPM_FILE}"

pkg_log "built ${RPM_FILE} ($(pkg_sha256 "${RPM_FILE}"))"
printf '%s\n' "${RPM_FILE}"
