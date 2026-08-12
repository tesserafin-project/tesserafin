#!/usr/bin/env bash
# Debian-family lifecycle acceptance for the native package (#225 / [L0]).
#
# Usage: ci/package/accept-deb.sh --artifacts DIR --rid RID
#        ci/package/accept-deb.sh --inner --artifacts DIR --rid RID
#
# The outer invocation boots a digest-pinned Ubuntu 24.04 container with a real
# systemd as PID 1 — on the runner's NATIVE architecture, never under emulation —
# and re-executes itself inside it. A fresh container is a genuinely clean
# environment, which the runner itself is not.
#
# The inner invocation is the lifecycle: install, inspect, start, smoke, upgrade,
# re-read sentinels, uninstall, and check what survived. It exercises the
# INSTALLED artifact, never the source tree.

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

DEB_ARCH="$(pkg_deb_arch "${RID}")"

# =============================================================================
# Outer: boot the lifecycle environment and hand over.
# =============================================================================
if [[ "${INNER}" -eq 0 ]]; then
    expected_uname="$(case "${RID}" in linux-x64) echo x86_64 ;; linux-arm64) echo aarch64 ;; esac)"
    [[ "$(uname -m)" == "${expected_uname}" ]] || pkg_die \
        "architecture mismatch: this host is $(uname -m), the artifact is ${DEB_ARCH}. \
Lifecycle acceptance must run on an architecture-native machine."

    IMAGE="$(pkg_deb_accept_image)"
    CONTAINER="tesserafin-deb-lifecycle-${DEB_ARCH}"
    docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true

    pkg_log "booting ${IMAGE} with systemd as PID 1 ($(uname -m))"
    docker run --detach --name "${CONTAINER}" \
        --privileged --cgroupns=host \
        --tmpfs /run --tmpfs /run/lock \
        --volume /sys/fs/cgroup:/sys/fs/cgroup:rw \
        --volume "${ARTIFACTS}:/artifacts:ro" \
        --volume "${PKG_REPO_ROOT}:/repo:ro" \
        "${IMAGE}" /sbin/init >/dev/null

    trap 'docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true' EXIT

    for _ in $(seq 1 60); do
        if docker exec "${CONTAINER}" systemctl is-system-running 2>/dev/null \
             | grep -qE 'running|degraded'; then
            break
        fi
        sleep 1
    done

    docker exec \
        --env PKG_VERSION="${VERSION}" \
        --env PKG_VCS_REF="${VCS_REF}" \
        --env PKG_SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH}" \
        "${CONTAINER}" \
        /repo/ci/package/accept-deb.sh --inner --artifacts /artifacts --rid "${RID}"
    exit $?
fi

# =============================================================================
# Inner: the lifecycle itself, against the INSTALLED artifact.
# =============================================================================
[[ "$(id -u)" -eq 0 ]]       || pkg_die "the inner run must be root"
[[ -d /run/systemd/system ]] || pkg_die "no running systemd inside the lifecycle environment"
[[ "$(dpkg --print-architecture)" == "${DEB_ARCH}" ]] || pkg_die \
    "architecture mismatch inside the lifecycle environment: $(dpkg --print-architecture) vs ${DEB_ARCH}"

DEB_V1="${ARTIFACTS}/tesserafin-server_${VERSION}-1_${DEB_ARCH}.deb"
DEB_V2="${ARTIFACTS}/tesserafin-server_${VERSION}-2_${DEB_ARCH}.deb"
[[ -f "${DEB_V1}" ]] || pkg_die "missing ${DEB_V1}"
[[ -f "${DEB_V2}" ]] || pkg_die "missing the synthetic upgrade package ${DEB_V2}"

FAILURES=0
fail() { echo "  FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok  : $*"; }

wait_for_http() { # <seconds>
    local deadline=$(( SECONDS + $1 )) code
    while (( SECONDS < deadline )); do
        code="$(curl -s -o /dev/null -w '%{http_code}' -m 3 http://127.0.0.1:8096/ || true)"
        [[ "${code}" == "302" ]] && return 0
        sleep 2
    done
    return 1
}

# =============================================================================
echo "== 1. install"
apt-get -qq update >/dev/null
DEBIAN_FRONTEND=noninteractive apt-get -qq install -y "${DEB_V1}" >/dev/null
pass "installed $(basename "${DEB_V1}")"

# =============================================================================
echo "== 2. package metadata and file ownership"
dpkg -s tesserafin-server > /tmp/tf-dpkg-status.txt
grep -q '^Package: tesserafin-server$'      /tmp/tf-dpkg-status.txt || fail "wrong package name"
grep -q "^Version: ${VERSION}-1$"            /tmp/tf-dpkg-status.txt || fail "wrong package version"
grep -q "^Architecture: ${DEB_ARCH}$"        /tmp/tf-dpkg-status.txt || fail "wrong architecture"
# Identity fields only. No package metadata carries Jellyfin branding at all:
# which is the bundled encoder's real upstream name.
if grep -E '^(Package|Homepage|Maintainer|Section):' /tmp/tf-dpkg-status.txt | grep -qi 'jellyfin'; then
    fail "package identity metadata carries Jellyfin branding"
fi
pass "metadata: tesserafin-server ${VERSION}-1 ${DEB_ARCH}"

for path in /usr/bin/tesserafin /usr/lib/tesserafin/tesserafin \
            /usr/lib/tesserafin/ffmpeg/bin/ffmpeg /usr/share/tesserafin/web/index.html \
            /usr/lib/systemd/system/tesserafin.service /etc/tesserafin/tesserafin.conf; do
    [[ -e "${path}" ]] || fail "missing installed path ${path}"
done
[[ "$(stat -c '%U:%G:%a' /usr/lib/tesserafin/tesserafin)" == "root:root:755" ]] \
    || fail "application binary is not root-owned 0755"
[[ "$(stat -c '%U:%G' /var/lib/tesserafin)" == "tesserafin:tesserafin" ]] \
    || fail "/var/lib/tesserafin is not owned by the service account"
[[ "$(stat -c '%a' /var/lib/tesserafin)" == "750" ]] \
    || fail "/var/lib/tesserafin is not mode 0750"
pass "installed paths and ownership"

# =============================================================================
echo "== 3. service account and directories"
getent passwd tesserafin >/dev/null || fail "the tesserafin user does not exist"
getent group  tesserafin >/dev/null || fail "the tesserafin group does not exist"
[[ "$(getent passwd tesserafin | cut -d: -f7)" == */nologin ]] \
    || fail "the tesserafin user has a login shell"
for dir in /etc/tesserafin /var/lib/tesserafin /var/cache/tesserafin /var/log/tesserafin; do
    [[ -d "${dir}" ]] || fail "missing directory ${dir}"
done
pass "service account and runtime directories"

# =============================================================================
echo "== 4. unit verification"
systemd-analyze verify /usr/lib/systemd/system/tesserafin.service
pass "systemd-analyze verify"

# The unit must not carry hardening that would hide transcoding devices.
for directive in PrivateDevices DevicePolicy DeviceAllow ProtectClock; do
    if grep -qE "^${directive}=" /usr/lib/systemd/system/tesserafin.service; then
        fail "the unit sets ${directive}, which can block VAAPI/NVIDIA device access"
    fi
done
pass "no device-blocking hardening directives"

# =============================================================================
echo "== 5. start the packaged service"
systemctl start tesserafin.service
if wait_for_http 180; then
    pass "the service reached HTTP readiness"
else
    journalctl -u tesserafin.service --no-pager -n 60 >&2 || true
    fail "the service never answered on 127.0.0.1:8096"
fi

# =============================================================================
echo "== 6. HTTP and bundled web payload"
root_code="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8096/ || true)"
root_target="$(curl -s -o /dev/null -w '%{redirect_url}' http://127.0.0.1:8096/ || true)"
web_code="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8096/web/index.html || true)"
health="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8096/health || true)"

[[ "${root_code}" == "302" ]]        || fail "/ answered ${root_code}, expected 302"
[[ "${root_target}" == */web/* ]]    || fail "/ redirected to '${root_target}', expected the web client"
[[ "${web_code}" == "200" ]]         || fail "/web/index.html answered ${web_code}, expected 200"
[[ "${health}" =~ ^(200|503)$ ]]     || fail "/health answered ${health}"
pass "/ -> ${root_target}, /web/index.html -> ${web_code}, /health -> ${health}"

# The served web client must be the bundled payload, not something else on disk.
grep -q '"revision"' /usr/share/tesserafin/web-revision.json || fail "no web provenance file"
served_revision="$(python3 -c 'import json;print(json.load(open("/usr/share/tesserafin/web-revision.json"))["revision"])')"
[[ "${served_revision}" == "${WEB_VCS_REF}" ]] \
    || fail "the installed web payload is ${served_revision}, not the pinned ${WEB_VCS_REF}"
pass "the installed web payload is the pinned commit ${served_revision}"

# =============================================================================
echo "== 7. the service does not run as root"
main_pid="$(systemctl show -p MainPID --value tesserafin.service)"
[[ -n "${main_pid}" && "${main_pid}" != "0" ]] || fail "the service has no main PID"
run_uid="$(ps -o uid= -p "${main_pid}" | tr -d ' ')"
run_user="$(ps -o user= -p "${main_pid}" | tr -d ' ')"
[[ "${run_uid}" != "0" ]] || fail "the service is running as root"
[[ "${run_user}" == "tesserafin" ]] || fail "the service runs as '${run_user}', expected tesserafin"
pass "running as ${run_user} (uid ${run_uid})"

# The bundled encoder must be the one in use, not a host ffmpeg.
/usr/lib/tesserafin/ffmpeg/bin/ffmpeg -hide_banner -version | head -1
grep -q '^TESSERAFIN_FFMPEG=/usr/lib/tesserafin/ffmpeg/bin/ffmpeg$' /etc/tesserafin/tesserafin.conf \
    || fail "the environment file does not point at the bundled encoder"
pass "the service is configured with the bundled encoder"

# =============================================================================
echo "== 8. sentinels in configuration and state"
echo 'sentinel-config' > /etc/tesserafin/acceptance-sentinel.txt
echo 'sentinel-state'  > /var/lib/tesserafin/acceptance-sentinel.txt
printf '\n# acceptance edit\nTESSERAFIN_EXTRA_ARGS=\n' >> /etc/tesserafin/tesserafin.conf
conf_before="$(sha256sum /etc/tesserafin/tesserafin.conf | cut -d' ' -f1)"
pass "sentinels written"

# =============================================================================
echo "== 9. synthetic upgrade ${VERSION}-1 -> ${VERSION}-2"
DEBIAN_FRONTEND=noninteractive apt-get -qq install -y "${DEB_V2}" >/dev/null
dpkg -s tesserafin-server | grep -q "^Version: ${VERSION}-2$" || fail "the upgrade did not take"
pass "upgraded to ${VERSION}-2"

# =============================================================================
echo "== 10. sentinels survived"
[[ -f /etc/tesserafin/acceptance-sentinel.txt ]]    || fail "the configuration sentinel was lost"
[[ -f /var/lib/tesserafin/acceptance-sentinel.txt ]]|| fail "the state sentinel was lost"
[[ "$(sha256sum /etc/tesserafin/tesserafin.conf | cut -d' ' -f1)" == "${conf_before}" ]] \
    || fail "the upgrade overwrote the edited tesserafin.conf"
systemctl is-active --quiet tesserafin.service || fail "the service is not running after the upgrade"
wait_for_http 180 || fail "the service did not answer HTTP after the upgrade"
pass "configuration, state and the running service survived the upgrade"

# =============================================================================
echo "== 11. uninstall"
DEBIAN_FRONTEND=noninteractive apt-get -qq purge -y tesserafin-server >/dev/null
pass "purged"

# =============================================================================
echo "== 12. what was removed and what remains"
for path in /usr/bin/tesserafin /usr/lib/tesserafin \
            /usr/share/tesserafin /usr/lib/systemd/system/tesserafin.service; do
    [[ ! -e "${path}" ]] || fail "${path} survived uninstall"
done
systemctl list-unit-files 2>/dev/null | grep -q '^tesserafin\.service' \
    && fail "the unit is still registered after uninstall"

[[ -d /var/lib/tesserafin ]] || fail "PERSISTENT STATE DELETED: /var/lib/tesserafin is gone"
[[ -f /var/lib/tesserafin/acceptance-sentinel.txt ]] \
    || fail "PERSISTENT STATE DELETED: the state sentinel is gone"
[[ -f /etc/tesserafin/acceptance-sentinel.txt ]] \
    || fail "configuration was deleted: the /etc sentinel is gone"
getent passwd tesserafin >/dev/null || fail "the tesserafin user was deleted, orphaning its files"
pass "binaries and unit removed; state, configuration and the service account retained"

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "DEB LIFECYCLE: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "DEB LIFECYCLE: PASS — ${DEB_ARCH}"
