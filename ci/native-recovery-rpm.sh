#!/usr/bin/env bash
# Native Linux (.rpm) cold-snapshot recovery rehearsal (#144).
#
# The RPM family needs an environment the hosted runner does not provide, so this
# boots the digest-pinned Rocky lifecycle image from ci/package/accept-rpm.sh, with
# systemd as PID 1 on the runner's native architecture, and runs the same
# ci/native-recovery-deb.sh procedure and oracle inside it. The container is the
# disposable host: it never held Tesserafin and is removed afterwards.
#
# Usage: ci/native-recovery-rpm.sh <rpm> <sha256>
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RPM="$(realpath "${1:?usage: native-recovery-rpm.sh <rpm> <sha256>}")"
RPM_SHA256="${2:?usage: native-recovery-rpm.sh <rpm> <sha256>}"
[[ "${RPM}" == *."$(uname -m)".rpm ]] \
  || { echo "refusing: $(basename "${RPM}") is not a native $(uname -m) package" >&2; exit 2; }

# shellcheck source=ci/package/lib.sh
source "${REPO_ROOT}/ci/package/lib.sh"
pkg_load_pins
IMAGE="$(pkg_rpm_accept_image)"
CONTAINER="tesserafin-rpm-recovery"
docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true
docker run --detach --name "${CONTAINER}" \
  --privileged --cgroupns=host \
  --tmpfs /run --tmpfs /run/lock \
  --volume /sys/fs/cgroup:/sys/fs/cgroup:rw \
  --volume "$(dirname "${RPM}"):/artifacts:ro" \
  --volume "${REPO_ROOT}:/repo:ro" \
  "${IMAGE}" /sbin/init >/dev/null
trap 'docker rm -f "${CONTAINER}" >/dev/null 2>&1 || true' EXIT

for _ in $(seq 1 60); do
  docker exec "${CONTAINER}" systemctl is-system-running 2>/dev/null | grep -qE 'running|degraded' && break
  sleep 1
done

# The lifecycle image is minimal; the oracle compares listings with cmp.
docker exec "${CONTAINER}" dnf -y -q install diffutils >/dev/null
docker exec --env TF_NATIVE_RECOVERY_DISPOSABLE=1 "${CONTAINER}" \
  bash /repo/ci/native-recovery-deb.sh "/artifacts/$(basename "${RPM}")" "${RPM_SHA256}"
