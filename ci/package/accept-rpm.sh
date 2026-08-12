#!/usr/bin/env bash
# RPM-family lifecycle acceptance for the native package (#225 / [L0]).
#
# Usage: ci/package/accept-rpm.sh --artifacts DIR --rid RID
#        ci/package/accept-rpm.sh --inner --artifacts DIR --rid RID
#
# The RPM family needs an environment the hosted runner does not provide, so the
# outer invocation boots a digest-pinned Rocky container with a real systemd as
# PID 1 — on the runner's NATIVE architecture, never under emulation — and
# re-executes itself inside it. The inner invocation is the actual lifecycle.

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

RPM_ARCH="$(pkg_rpm_arch "${RID}")"

# =============================================================================
# Outer: boot the lifecycle environment and hand over.
# =============================================================================
if [[ "${INNER}" -eq 0 ]]; then
    host_arch="$(uname -m)"
    [[ "${host_arch}" == "${RPM_ARCH}" ]] || pkg_die \
        "architecture mismatch: this host is ${host_arch}, the artifact is ${RPM_ARCH}. \
Lifecycle acceptance must run on an architecture-native machine."

    IMAGE="$(pkg_rpm_accept_image)"
    CONTAINER="tesserafin-rpm-lifecycle-${RPM_ARCH}"
    docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true

    pkg_log "booting ${IMAGE} with systemd as PID 1 (${host_arch})"
    docker run --detach --name "${CONTAINER}" \
        --privileged --cgroupns=host \
        --tmpfs /run --tmpfs /run/lock \
        --volume /sys/fs/cgroup:/sys/fs/cgroup:rw \
        --volume "${ARTIFACTS}:/artifacts:ro" \
        --volume "${PKG_REPO_ROOT}:/repo:ro" \
        "${IMAGE}" /sbin/init >/dev/null

    trap 'docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true' EXIT

    # Wait for systemd to finish coming up before asking it to do anything.
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
        /repo/ci/package/accept-rpm.sh --inner --artifacts /artifacts --rid "${RID}"
    exit $?
fi

# =============================================================================
# Inner: the lifecycle itself, against the INSTALLED artifact.
# =============================================================================
[[ "$(id -u)" -eq 0 ]]       || pkg_die "the inner run must be root"
[[ -d /run/systemd/system ]] || pkg_die "no running systemd inside the lifecycle environment"

RPM_V1="${ARTIFACTS}/tesserafin-server-${VERSION}-1.${RPM_ARCH}.rpm"
RPM_V2="${ARTIFACTS}/tesserafin-server-${VERSION}-2.${RPM_ARCH}.rpm"
[[ -f "${RPM_V1}" ]] || pkg_die "missing ${RPM_V1}"
[[ -f "${RPM_V2}" ]] || pkg_die "missing the synthetic upgrade package ${RPM_V2}"

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

echo "== 1. install"
dnf -q -y install "${RPM_V1}" >/dev/null
pass "installed $(basename "${RPM_V1}")"

echo "== 2. package metadata and file ownership"
rpm -qi tesserafin-server > /tmp/tf-rpm-info.txt
grep -q "^Name *: tesserafin-server$" /tmp/tf-rpm-info.txt || fail "wrong package name"
grep -q "^Version *: ${VERSION}$"      /tmp/tf-rpm-info.txt || fail "wrong package version"
grep -q "^Architecture: ${RPM_ARCH}$"  /tmp/tf-rpm-info.txt || fail "wrong architecture"
# Identity fields only; no package metadata carries Jellyfin branding at all.
if grep -E '^(Name|Summary|URL|Packager) *:' /tmp/tf-rpm-info.txt | grep -qi 'jellyfin'; then
    fail "package identity metadata carries Jellyfin branding"
fi
# The build host must not be recorded: it is both a reproducibility hazard and a
# build-environment leak.
grep -q "^Build Host *: tesserafin-build$" /tmp/tf-rpm-info.txt \
    || fail "the RPM header does not carry the pinned build host"
pass "metadata: tesserafin-server ${VERSION}-1 ${RPM_ARCH}"

for path in /usr/bin/tesserafin /usr/lib/tesserafin/tesserafin \
            /usr/lib/tesserafin/ffmpeg/bin/ffmpeg /usr/share/tesserafin/web/index.html \
            /usr/lib/systemd/system/tesserafin.service /etc/tesserafin/tesserafin.conf; do
    [[ -e "${path}" ]] || fail "missing installed path ${path}"
done
[[ "$(stat -c '%U:%G' /var/lib/tesserafin)" == "tesserafin:tesserafin" ]] \
    || fail "/var/lib/tesserafin is not owned by the service account"
[[ "$(stat -c '%a' /var/lib/tesserafin)" == "750" ]] \
    || fail "/var/lib/tesserafin is not mode 0750"
pass "installed paths and ownership"

echo "== 3. service account and directories"
getent passwd tesserafin >/dev/null || fail "the tesserafin user does not exist"
getent group  tesserafin >/dev/null || fail "the tesserafin group does not exist"
for dir in /etc/tesserafin /var/lib/tesserafin /var/cache/tesserafin /var/log/tesserafin; do
    [[ -d "${dir}" ]] || fail "missing directory ${dir}"
done
pass "service account and runtime directories"

echo "== 4. unit verification"
systemd-analyze verify /usr/lib/systemd/system/tesserafin.service
for directive in PrivateDevices DevicePolicy DeviceAllow ProtectClock; do
    if grep -qE "^${directive}=" /usr/lib/systemd/system/tesserafin.service; then
        fail "the unit sets ${directive}, which can block VAAPI/NVIDIA device access"
    fi
done
pass "systemd-analyze verify, no device-blocking hardening"

echo "== 5. start the packaged service"
systemctl start tesserafin.service
if wait_for_http 180; then
    pass "the service reached HTTP readiness"
else
    journalctl -u tesserafin.service --no-pager -n 60 >&2 || true
    fail "the service never answered on 127.0.0.1:8096"
fi

echo "== 6. HTTP and bundled web payload"
root_code="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8096/ || true)"
root_target="$(curl -s -o /dev/null -w '%{redirect_url}' http://127.0.0.1:8096/ || true)"
web_code="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8096/web/index.html || true)"
health="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8096/health || true)"
[[ "${root_code}" == "302" ]]     || fail "/ answered ${root_code}, expected 302"
[[ "${root_target}" == */web/* ]] || fail "/ redirected to '${root_target}', expected the web client"
[[ "${web_code}" == "200" ]]      || fail "/web/index.html answered ${web_code}, expected 200"
[[ "${health}" =~ ^(200|503)$ ]]  || fail "/health answered ${health}"
served_revision="$(python3 -c 'import json;print(json.load(open("/usr/share/tesserafin/web-revision.json"))["revision"])')"
[[ "${served_revision}" == "${WEB_VCS_REF}" ]] \
    || fail "the installed web payload is ${served_revision}, not the pinned ${WEB_VCS_REF}"
pass "/ -> ${root_target}, /web/index.html -> ${web_code}, web payload ${served_revision}"

echo "== 7. the service does not run as root"
main_pid="$(systemctl show -p MainPID --value tesserafin.service)"
run_uid="$(ps -o uid= -p "${main_pid}" | tr -d ' ')"
run_user="$(ps -o user= -p "${main_pid}" | tr -d ' ')"
[[ "${run_uid}" != "0" ]]           || fail "the service is running as root"
[[ "${run_user}" == "tesserafin" ]] || fail "the service runs as '${run_user}', expected tesserafin"
/usr/lib/tesserafin/ffmpeg/bin/ffmpeg -hide_banner -version | head -1
pass "running as ${run_user} (uid ${run_uid})"

echo "== 8. sentinels in configuration and state"
echo 'sentinel-config' > /etc/tesserafin/acceptance-sentinel.txt
echo 'sentinel-state'  > /var/lib/tesserafin/acceptance-sentinel.txt
printf '\n# acceptance edit\nTESSERAFIN_EXTRA_ARGS=\n' >> /etc/tesserafin/tesserafin.conf
conf_before="$(sha256sum /etc/tesserafin/tesserafin.conf | cut -d' ' -f1)"
pass "sentinels written"

echo "== 9. synthetic upgrade ${VERSION}-1 -> ${VERSION}-2"
dnf -q -y upgrade "${RPM_V2}" >/dev/null
rpm -q tesserafin-server | grep -q -- "-${VERSION}-2\." || fail "the upgrade did not take"
pass "upgraded to ${VERSION}-2"

echo "== 10. sentinels survived"
[[ -f /etc/tesserafin/acceptance-sentinel.txt ]]     || fail "the configuration sentinel was lost"
[[ -f /var/lib/tesserafin/acceptance-sentinel.txt ]] || fail "the state sentinel was lost"
# %config(noreplace) keeps the edited file in place; rpm may drop a .rpmnew
# beside it, which is correct behaviour and not a modification.
[[ "$(sha256sum /etc/tesserafin/tesserafin.conf | cut -d' ' -f1)" == "${conf_before}" ]] \
    || fail "the upgrade overwrote the edited tesserafin.conf"
systemctl is-active --quiet tesserafin.service || fail "the service is not running after the upgrade"
wait_for_http 180 || fail "the service did not answer HTTP after the upgrade"
pass "configuration, state and the running service survived the upgrade"

echo "== 11. uninstall"
dnf -q -y remove tesserafin-server >/dev/null
pass "removed"

echo "== 12. what was removed and what remains"
for path in /usr/bin/tesserafin /usr/lib/tesserafin \
            /usr/share/tesserafin /usr/lib/systemd/system/tesserafin.service; do
    [[ ! -e "${path}" ]] || fail "${path} survived uninstall"
done
[[ -d /var/lib/tesserafin ]] || fail "PERSISTENT STATE DELETED: /var/lib/tesserafin is gone"
[[ -f /var/lib/tesserafin/acceptance-sentinel.txt ]] \
    || fail "PERSISTENT STATE DELETED: the state sentinel is gone"
[[ -f /etc/tesserafin/acceptance-sentinel.txt ]] \
    || fail "configuration was deleted: the /etc sentinel is gone"
getent passwd tesserafin >/dev/null || fail "the tesserafin user was deleted, orphaning its files"
pass "binaries and unit removed; state, configuration and the service account retained"

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "RPM LIFECYCLE: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "RPM LIFECYCLE: PASS — ${RPM_ARCH}"
