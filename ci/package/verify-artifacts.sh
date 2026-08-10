#!/usr/bin/env bash
# Equivalence and hygiene gate for the native Linux artifacts (#225 / [L0]).
#
# Usage: ci/package/verify-artifacts.sh --artifacts DIR --rid RID
#
# Proves, from the ARTIFACTS themselves rather than from the build that produced
# them:
#   1. the .deb, the .rpm and the .tar.gz carry byte-identical application and
#      web payloads for this architecture;
#   2. the bundled web payload matches the pinned commit and digest;
#   3. no artifact carries build-host absolute paths, key material, credentials
#      or unexpectedly writable application files;
#   4. no user-facing package or service metadata is branded as Jellyfin. The
#      bundled upstream encoder is NOT renamed: it is genuine jellyfin-ffmpeg and
#      says so, which is honest provenance rather than branding.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

ARTIFACTS=""; RID=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --artifacts) ARTIFACTS="$2"; shift 2 ;;
        --rid)       RID="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -d "${ARTIFACTS}" ]] || pkg_die "--artifacts must be an existing directory"
[[ -n "${RID}" ]]       || pkg_die "--rid is required"

pkg_load_pins
pkg_load_version_contract

DEB_ARCH="$(pkg_deb_arch "${RID}")"
RPM_ARCH="$(pkg_rpm_arch "${RID}")"

DEB="${ARTIFACTS}/tesserafin-server_${VERSION}-1_${DEB_ARCH}.deb"
RPM="${ARTIFACTS}/tesserafin-server-${VERSION}-1.${RPM_ARCH}.rpm"
TGZ="${ARTIFACTS}/tesserafin-server-${VERSION}-${RID}.tar.gz"

for artifact in "${DEB}" "${RPM}" "${TGZ}"; do
    [[ -f "${artifact}" ]] || pkg_die "missing artifact: ${artifact}"
done

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

FAILURES=0
fail() { echo "  FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok  : $*"; }

# --- unpack ------------------------------------------------------------------
mkdir -p "${WORK}/deb" "${WORK}/rpm" "${WORK}/tgz"
dpkg-deb -x "${DEB}" "${WORK}/deb"
# rpm2cpio comes from the same pinned image the package was built with, so no
# workstation rpm tooling participates in the verdict.
RPM_TOOLS="$(pkg_rpm_builder_image)"
docker run --rm --user "$(id -u):$(id -g)" \
    --volume "${ARTIFACTS}:/artifacts:ro" --volume "${WORK}/rpm:/out" \
    "${RPM_TOOLS}" \
    sh -c "cd /out && rpm2cpio '/artifacts/$(basename "${RPM}")' | cpio -idm --quiet"
tar -xzf "${TGZ}" -C "${WORK}/tgz"
TGZ_ROOT="${WORK}/tgz/tesserafin-server-${VERSION}-${RID}"

# --- 1. payload equivalence --------------------------------------------------
echo "== payload equivalence (${RID})"
EPOCH_FOR_COMPARE="${SOURCE_DATE_EPOCH}"

app_deb="$(pkg_tree_digest "${WORK}/deb/usr/lib/tesserafin"  "${EPOCH_FOR_COMPARE}")"
app_rpm="$(pkg_tree_digest "${WORK}/rpm/usr/lib/tesserafin"  "${EPOCH_FOR_COMPARE}")"
app_tgz="$(pkg_tree_digest "${TGZ_ROOT}/lib/tesserafin"      "${EPOCH_FOR_COMPARE}")"

if [[ "${app_deb}" == "${app_rpm}" && "${app_deb}" == "${app_tgz}" ]]; then
    pass "application payload identical across .deb/.rpm/.tar.gz (${app_deb})"
else
    fail "application payload differs: deb=${app_deb} rpm=${app_rpm} tgz=${app_tgz}"
fi

web_deb="$(pkg_tree_digest "${WORK}/deb/usr/share/tesserafin/web" "${EPOCH_FOR_COMPARE}")"
web_rpm="$(pkg_tree_digest "${WORK}/rpm/usr/share/tesserafin/web" "${EPOCH_FOR_COMPARE}")"
web_tgz="$(pkg_tree_digest "${TGZ_ROOT}/share/tesserafin/web"     "${EPOCH_FOR_COMPARE}")"

if [[ "${web_deb}" == "${web_rpm}" && "${web_deb}" == "${web_tgz}" ]]; then
    pass "web payload identical across .deb/.rpm/.tar.gz (${web_deb})"
else
    fail "web payload differs: deb=${web_deb} rpm=${web_rpm} tgz=${web_tgz}"
fi

# --- 2. web provenance -------------------------------------------------------
echo "== web provenance"
revision="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["revision"])' \
            "${WORK}/deb/usr/share/tesserafin/web-revision.json")"
web_epoch="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["sourceDateEpoch"])' \
             "${WORK}/deb/usr/share/tesserafin/web-revision.json")"
if [[ "${revision}" == "${WEB_VCS_REF}" ]]; then
    pass "bundled web commit is the pinned commit (${revision})"
else
    fail "bundled web commit ${revision} is not the pinned ${WEB_VCS_REF}"
fi

installed_web_digest="$(pkg_tree_digest "${WORK}/deb/usr/share/tesserafin/web" "${web_epoch}")"
if [[ "${installed_web_digest}" == "${WEB_PAYLOAD_SHA256}" ]]; then
    pass "bundled web payload digest is the pinned digest (${installed_web_digest})"
else
    fail "bundled web payload digest ${installed_web_digest} is not the pinned ${WEB_PAYLOAD_SHA256}"
fi

# --- 3. content hygiene ------------------------------------------------------
echo "== content hygiene"

# 3a. Absolute paths belonging to THIS build. The directory the packages were
#     assembled in and the building account's home must not appear anywhere.
leaks=0
for root in "${WORK}/deb" "${WORK}/rpm" "${TGZ_ROOT}"; do
    for pattern in "${PKG_REPO_ROOT}" "${HOME}"; do
        [[ -n "${pattern}" && "${pattern}" != "/" ]] || continue
        hits="$(grep -rlaF -- "${pattern}" "${root}" 2>/dev/null || true)"
        if [[ -n "${hits}" ]]; then
            fail "this build's path '${pattern}' is present under $(basename "${root}")"
            leaks=$((leaks + 1))
        fi
    done
done
if [[ "${leaks}" -eq 0 ]]; then
    pass "no path from this build environment appears in any artifact"
fi

# 3b. Generic CI-workspace paths in FIRST-PARTY output. Third-party NuGet
#     assemblies are build INPUTS: several are compiled on GitHub Actions by
#     their own maintainers and embed that project's own workspace path. Those
#     bytes are identical in the container image and are not this packaging's to
#     rewrite — but they are reported by name rather than passed over in silence.
firstparty_leak=0
while IFS= read -r file; do
    case "$(basename "${file}")" in
        [Tt]esserafin*) firstparty_leak=1; fail "first-party file carries a CI workspace path: ${file}" ;;
    esac
done < <(grep -rlaE -- '/home/runner/work|/root/\.nuget' "${WORK}/deb/usr/lib/tesserafin" 2>/dev/null || true)
if [[ "${firstparty_leak}" -eq 0 ]]; then
    pass "no first-party file carries a CI workspace path"
fi

thirdparty="$(grep -rlaE -- '/home/runner/work|/root/\.nuget' "${WORK}/deb/usr/lib/tesserafin" 2>/dev/null \
              | xargs -r -n1 basename | LC_ALL=C sort | tr '\n' ' ')"
if [[ -n "${thirdparty}" ]]; then
    echo "  note: third-party dependency assemblies carrying their own upstream CI paths: ${thirdparty}"
fi

# 3c. Key material and credential files.
secrets="$(find "${WORK}/deb" "${WORK}/rpm" "${TGZ_ROOT}" \
        \( -name '*.pem' -o -name '*.key' -o -name '*.pfx' -o -name '*.p12' \
        -o -name '.env' -o -name '.npmrc' -o -name '.netrc' -o -name 'id_rsa*' \) \
        -print 2>/dev/null || true)"
if [[ -n "${secrets}" ]]; then
    fail "key material or credential file present in an artifact: ${secrets}"
else
    pass "no key material or credential files"
fi

# 3d. Application files must not be writable by group or other: nothing under
#     /usr is state, and a writable application file is a privilege path.
# No `| head`: a closed pipe would raise SIGPIPE, and with pipefail that turns a
# real finding into a silently skipped check.
writable="$(find "${WORK}/deb/usr" "${WORK}/rpm/usr" "${TGZ_ROOT}/lib" "${TGZ_ROOT}/share" \
                 -type f -perm /022 -print 2>/dev/null || true)"
if [[ -n "${writable}" ]]; then
    fail "group/other-writable application files: ${writable}"
else
    pass "no group/other-writable application files"
fi

# 3e. The archive must not pretend to be an installer.
archive_units="$(find "${TGZ_ROOT}" -name '*.service' -print 2>/dev/null || true)"
if [[ -e "${TGZ_ROOT}/etc" || -n "${archive_units}" ]]; then
    fail "the portable archive ships service or /etc content"
else
    pass "the portable archive ships no service unit and no /etc content"
fi

# 3f. No path escapes in the archive.
tgz_members="$(tar -tzf "${TGZ}")"
if grep -qE '(^/|(^|/)\.\.(/|$))' <<<"${tgz_members}"; then
    fail "the portable archive contains an absolute path or a '..' component"
else
    pass "the portable archive has no absolute or parent-relative members"
fi

# --- 4. user-facing naming ---------------------------------------------------
echo "== user-facing naming"

# The identity fields — what a user sees in a package manager listing — must be
# Tesserafin. The DESCRIPTION is allowed to name jellyfin-ffmpeg, because the
# bundled encoder genuinely is jellyfin-ffmpeg and stating so is accurate
# provenance; renaming it to hide the ancestry would be the dishonest option.
identity="$(dpkg-deb -f "${DEB}" Package Source Maintainer Homepage Section)"
identity+=$'\n'"$(docker run --rm --volume "${ARTIFACTS}:/artifacts:ro" "${RPM_TOOLS}" \
                  rpm -qp --nosignature --qf '%{NAME}\n%{SUMMARY}\n%{URL}\n%{PACKAGER}\n' \
                  "/artifacts/$(basename "${RPM}")" 2>/dev/null)"

if grep -qi 'jellyfin' <<<"${identity}"; then
    fail "package identity metadata carries Jellyfin branding: $(grep -i jellyfin <<<"${identity}")"
else
    pass "package identity metadata is Tesserafin-branded"
fi

# Any remaining mention must be the encoder, spelled as the upstream project.
descriptions="$(dpkg-deb -f "${DEB}" Description)"
descriptions+=$'\n'"$(docker run --rm --volume "${ARTIFACTS}:/artifacts:ro" "${RPM_TOOLS}" \
                      rpm -qp --nosignature --qf '%{DESCRIPTION}\n' \
                      "/artifacts/$(basename "${RPM}")" 2>/dev/null)"
stray="$(grep -oiE 'jellyfin[a-z0-9-]*' <<<"${descriptions}" | grep -viE '^jellyfin-ffmpeg$' || true)"
if [[ -n "${stray}" ]]; then
    fail "package descriptions mention Jellyfin outside the bundled encoder: ${stray}"
else
    pass "the only Jellyfin mention in any description is the bundled jellyfin-ffmpeg encoder"
fi

unit="${WORK}/deb/usr/lib/systemd/system/tesserafin.service"
if grep -qi 'jellyfin' "${unit}"; then
    fail "the systemd unit carries Jellyfin branding"
else
    pass "the systemd unit is Tesserafin-branded"
fi

# The encoder is genuine upstream jellyfin-ffmpeg and is not renamed. Assert it
# is present and identifies itself, so provenance stays visible.
if [[ -x "${WORK}/deb/usr/lib/tesserafin/ffmpeg/ffmpeg" ]]; then
    pass "the bundled upstream encoder is present at /usr/lib/tesserafin/ffmpeg/ffmpeg"
else
    fail "the bundled encoder is missing"
fi

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "VERIFY: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "VERIFY: PASS — ${RID}"
