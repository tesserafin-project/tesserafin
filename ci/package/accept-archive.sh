#!/usr/bin/env bash
# Portable-archive runtime acceptance (#225 / [L0]).
#
# Usage: ci/package/accept-archive.sh --artifacts DIR --rid RID
#        ci/package/accept-archive.sh --inner --artifacts DIR --rid RID
#
# The archive claims to run anywhere with a few ordinary system libraries and no
# installation. That claim is tested in a digest-pinned Debian container carrying
# ONLY the documented dependencies — no .NET, no ffmpeg, no build tooling, none
# of the runner's preinstalled software — on the host's NATIVE architecture. If
# the payload leans on anything inherited from the build machine, it fails here.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

ARTIFACTS=""; RID=""; INNER=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --artifacts) ARTIFACTS="$2"; shift 2 ;;
        --rid)       RID="$2"; shift 2 ;;
        --inner)     INNER=1; shift ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -d "${ARTIFACTS}" ]] || pkg_die "--artifacts must be an existing directory"
[[ -n "${RID}" ]]       || pkg_die "--rid is required"

pkg_load_pins
pkg_load_version_contract

ARCHIVE_NAME="tesserafin-server-${VERSION}-${RID}.tar.gz"

if [[ "${INNER}" -eq 0 ]]; then
    expected_uname="$(case "${RID}" in linux-x64) echo x86_64 ;; linux-arm64) echo aarch64 ;; esac)"
    [[ "$(uname -m)" == "${expected_uname}" ]] || pkg_die \
        "architecture mismatch: this host is $(uname -m), the archive is ${RID}. \
Runtime acceptance must run on an architecture-native machine."

    pkg_log "running archive acceptance in ${ARCHIVE_ACCEPT_IMAGE} ($(uname -m))"
    docker run --rm \
        --volume "${ARTIFACTS}:/artifacts:ro" \
        --volume "${PKG_REPO_ROOT}:/repo:ro" \
        --env RID="${RID}" \
        --env PKG_VERSION="${VERSION}" \
        --env PKG_VCS_REF="${VCS_REF}" \
        --env PKG_SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH}" \
        "${ARCHIVE_ACCEPT_IMAGE}" \
        sh -c "apt-get -qq update >/dev/null && \
               DEBIAN_FRONTEND=noninteractive apt-get -qq install -y \
                   libicu72 libfontconfig1 fonts-dejavu-core curl python3 procps >/dev/null && \
               /repo/ci/package/accept-archive.sh --inner --artifacts /artifacts --rid ${RID}"
    exit $?
fi

# =============================================================================
# Inner: extract into a clean prefix and run it.
# =============================================================================
FAILURES=0
fail() { echo "  FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok  : $*"; }

ARCHIVE="${ARTIFACTS}/${ARCHIVE_NAME}"
[[ -f "${ARCHIVE}" ]] || pkg_die "missing ${ARCHIVE}"

echo "== 1. no path escapes and no unsafe ownership"
members="$(tar -tzf "${ARCHIVE}")"
if grep -qE '(^/|(^|/)\.\.(/|$))' <<<"${members}"; then
    fail "the archive contains an absolute path or a '..' component"
else
    pass "every member is a relative path under the archive prefix"
fi
if [[ "$(tar -tvzf "${ARCHIVE}" | awk '{print $2}' | sort -u)" != "0/0" ]]; then
    fail "the archive carries non-root numeric ownership: $(tar -tvzf "${ARCHIVE}" | awk '{print $2}' | sort -u | tr '\n' ' ')"
else
    pass "every member is owned by 0/0"
fi
if [[ "$(tar -tzf "${ARCHIVE}" | cut -d/ -f1 | sort -u | wc -l)" -ne 1 ]]; then
    fail "the archive does not unpack into a single prefix directory"
else
    pass "single prefix directory: $(tar -tzf "${ARCHIVE}" | cut -d/ -f1 | sort -u)"
fi

echo "== 2. extract into a clean temporary prefix"
PREFIX="$(mktemp -d)"
tar -xzf "${ARCHIVE}" -C "${PREFIX}"
ROOT="${PREFIX}/tesserafin-server-${VERSION}-${RID}"
[[ -d "${ROOT}" ]] || pkg_die "the archive did not unpack to ${ROOT}"
[[ -x "${ROOT}/lib/tesserafin/tesserafin" ]] || fail "the application binary is not executable"
[[ -x "${ROOT}/lib/tesserafin/ffmpeg/bin/ffmpeg" ]] || fail "the bundled encoder is not executable"
[[ -f "${ROOT}/README.md" ]] || fail "the archive has no README"
[[ ! -e "${ROOT}/etc" ]] || fail "the archive ships /etc content"
archive_units="$(find "${ROOT}" -name '*.service' -print 2>/dev/null || true)"
if [[ -n "${archive_units}" ]]; then
    fail "the archive ships a systemd unit"
else
    pass "no unit file and no /etc content: the archive does not pretend to install a service"
fi

echo "== 2b. the bundled FFmpeg runtime survives relocation"
# The whole claim of a portable archive is that it works wherever it is unpacked.
# For the encoder that claim rests on one mechanism: RUNPATH=$ORIGIN/../lib, which
# is resolved relative to the BINARY, not to any install prefix. So it is tested
# the only way that means anything — by moving the tree to a second, differently
# named directory and running it from there.
#
# There is no system ffmpeg in this container, so a binary that silently fell
# back to one would simply fail; and every resolved library is checked to be
# inside the relocated prefix, so one that found a HOST libva would be caught even
# on a machine that has one.
RELOC="${PREFIX}/relocated-$$/deeper/still"
mkdir -p "$(dirname "${RELOC}")"
cp -a "${ROOT}" "${RELOC}"

reloc_ffmpeg="${RELOC}/lib/tesserafin/ffmpeg/bin/ffmpeg"
if "${reloc_ffmpeg}" -hide_banner -version >/dev/null 2>&1; then
    pass "the relocated encoder runs: $("${reloc_ffmpeg}" -hide_banner -version | head -1)"
else
    "${reloc_ffmpeg}" -hide_banner -version 2>&1 | head -5 >&2
    fail "the relocated encoder does not run; \$ORIGIN/../lib did not survive relocation"
fi

"${reloc_ffmpeg}" -hide_banner -buildconf > "${PREFIX}/relocated-buildconf.txt" 2>&1 \
    && pass "-buildconf executes from the relocated path ($(wc -l < "${PREFIX}/relocated-buildconf.txt") lines)" \
    || fail "-buildconf failed from the relocated path"
"${reloc_ffmpeg}" -hide_banner -hwaccels > "${PREFIX}/relocated-hwaccels.txt" 2>&1 \
    && pass "-hwaccels executes from the relocated path: $(tail -n +2 "${PREFIX}/relocated-hwaccels.txt" | tr -d ' ' | grep -v '^$' | tr '\n' ' ')" \
    || fail "-hwaccels failed from the relocated path"

# Every bundled soname must resolve INSIDE the relocated tree. `ldd` prints the
# path the loader would actually use, so this catches a host substitution that a
# successful run alone would not.
outside=""
while read -r soname _ resolved _; do
    [[ "${resolved}" == /* ]] || continue
    case "${resolved}" in
        "${RELOC}"/*) : ;;
        *) [[ -e "${RELOC}/lib/tesserafin/ffmpeg/lib/${soname}" ]] && outside+="${soname} -> ${resolved}"$'\n' ;;
    esac
done < <(ldd "${reloc_ffmpeg}" 2>/dev/null | sed -nE 's/^\s*(\S+) (=>) (\S+).*/\1 \2 \3 x/p')
if [[ -n "${outside}" ]]; then
    fail "a bundled library resolved to a HOST copy after relocation:"$'\n'"${outside}"
else
    pass "every bundled library the loader resolves comes from the relocated tree"
fi

echo "== 3. run the server from the extracted payload, unprivileged"
useradd --system --no-create-home --shell /usr/sbin/nologin tfarchive 2>/dev/null || true
STATE="${PREFIX}/state"
mkdir -p "${STATE}/config" "${STATE}/data" "${STATE}/cache" "${STATE}/log"
chown -R tfarchive "${STATE}"
chmod -R a+rX "${PREFIX}"

setpriv --reuid="$(id -u tfarchive)" --regid="$(id -g tfarchive)" --clear-groups \
    "${ROOT}/bin/tesserafin" \
        --configdir "${STATE}/config" \
        --datadir   "${STATE}/data" \
        --cachedir  "${STATE}/cache" \
        --logdir    "${STATE}/log" \
        --webdir    "${ROOT}/share/tesserafin/web" \
        --ffmpeg    "${ROOT}/lib/tesserafin/ffmpeg/bin/ffmpeg" \
    > "${PREFIX}/server.log" 2>&1 &
SERVER_PID=$!
# shellcheck disable=SC2064
trap "kill ${SERVER_PID} 2>/dev/null || true" EXIT

ready=0
for _ in $(seq 1 90); do
    if [[ "$(curl -s -o /dev/null -w '%{http_code}' -m 3 http://127.0.0.1:8096/ || true)" == "302" ]]; then
        ready=1; break
    fi
    kill -0 "${SERVER_PID}" 2>/dev/null || break
    sleep 2
done
if [[ "${ready}" -ne 1 ]]; then
    tail -40 "${PREFIX}/server.log" >&2
    fail "the extracted payload never answered on 127.0.0.1:8096"
else
    pass "the extracted payload serves HTTP"
fi

echo "== 4. the same HTTP and web smoke as the packages"
root_target="$(curl -s -o /dev/null -w '%{redirect_url}' http://127.0.0.1:8096/ || true)"
web_code="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8096/web/index.html || true)"
[[ "${root_target}" == */web/* ]] || fail "/ redirected to '${root_target}', expected the web client"
[[ "${web_code}" == "200" ]]      || fail "/web/index.html answered ${web_code}, expected 200"
served_revision="$(python3 -c "import json;print(json.load(open('${ROOT}/share/tesserafin/web-revision.json'))['revision'])")"
[[ "${served_revision}" == "${WEB_VCS_REF}" ]] \
    || fail "the archived web payload is ${served_revision}, not the pinned ${WEB_VCS_REF}"
pass "/ -> ${root_target}, /web/index.html -> ${web_code}, web payload ${served_revision}"

run_uid="$(ps -o uid= -p "${SERVER_PID}" | tr -d ' ')"
[[ "${run_uid}" != "0" ]] || fail "the archive smoke ran as root"
pass "ran as uid ${run_uid}"

echo "== 5. no dependence on files inherited from the build host"
# This container has no .NET, no ffmpeg and none of the build machine's tooling,
# so simply having reached step 4 proves the payload is self-contained. What is
# still worth asserting is that nothing REACHES OUT to a build path. The same
# classifier the artifact gate uses decides that here: an embedded path is either
# exactly one of the enumerated upstream dependency paths, or it is a leak.
embedded_leak=""
while IFS=$'\t' read -r verdict file path; do
    [[ "${verdict}" == "LEAK" ]] || continue
    embedded_leak="${embedded_leak} ${file}:'${path}'"
done < <(pkg_scan_embedded_build_paths "${ROOT}")
if [[ -n "${embedded_leak}" ]]; then
    fail "the extracted payload carries unenumerated build paths:${embedded_leak}"
else
    pass "every embedded build path is an enumerated upstream dependency path"
fi
if [[ -n "$(command -v dotnet || true)" ]]; then
    fail "the acceptance environment has a system .NET, so self-containment was not tested"
else
    pass "the environment has no system .NET: the payload supplied its own runtime"
fi

kill "${SERVER_PID}" 2>/dev/null || true

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "ARCHIVE ACCEPTANCE: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "ARCHIVE ACCEPTANCE: PASS — ${RID}"
