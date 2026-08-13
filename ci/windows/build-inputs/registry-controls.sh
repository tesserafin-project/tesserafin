#!/usr/bin/env bash
# Negative controls for the registry protocol (#236, W1-R-B §3).
#
# The publication path was, until this ran, the only part of W1-R that had never
# been executed. These controls exercise it against an ephemeral local OCI
# Distribution registry, through the SAME `oci-protocol.sh` the GHCR publisher
# calls, and require it to refuse every way the round trip could go wrong.
#
# Nothing here has `packages: write` and nothing here talks to an external
# registry.
#
# Usage:
#   registry-controls.sh --oci DIR --registry HOST:PORT --work DIR

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROTOCOL="${HERE}/oci-protocol.sh"

OCI=''
REGISTRY=''
WORK=''
while [ $# -gt 0 ]; do
  case "$1" in
    --oci) OCI="$2"; shift 2 ;;
    --registry) REGISTRY="$2"; shift 2 ;;
    --work) WORK="$2"; shift 2 ;;
    *) echo "unknown argument '$1'" >&2; exit 2 ;;
  esac
done
[ -n "${OCI}" ] && [ -n "${REGISTRY}" ] && [ -n "${WORK}" ] || {
  echo "usage: registry-controls.sh --oci DIR --registry HOST:PORT --work DIR" >&2
  exit 2
}

mkdir -p "${WORK}"
PASSED=0
FAILED=0

record() {
  local name="$1" ok="$2" detail="$3"
  if [ "${ok}" = yes ]; then
    PASSED=$((PASSED + 1))
    echo "[PASS] ${name}: ${detail}"
  else
    FAILED=$((FAILED + 1))
    echo "[FAIL] ${name}: ${detail}"
  fi
}

# A control passes when the command REFUSES. A command that succeeds here is the
# finding.
refuses() {
  local name="$1"; shift
  local output
  if output="$("$@" 2>&1)"; then
    record "${name}" no "accepted what it had to refuse: ${output##*$'\n'}"
  else
    record "${name}" yes "${output##*$'\n'}"
  fi
}

accepts() {
  local name="$1"; shift
  local output
  if output="$("$@" 2>&1)"; then
    record "${name}" yes "${output##*$'\n'}"
  else
    record "${name}" no "refused what it had to accept: ${output##*$'\n'}"
  fi
}

repo="${REGISTRY}/tesserafin-project/windows-ffmpeg-build-inputs"
digest="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["manifestDigest"])' "${OCI}/descriptor.json")"

# ── The protocol itself, end to end ─────────────────────────────────────────
accepts control.registry-push "${PROTOCOL}" push --oci "${OCI}" --repo "${repo}" --plain-http
accepts control.registry-verify \
  "${PROTOCOL}" verify --oci "${OCI}" --repo "${repo}" --out "${WORK}/pulled" --plain-http

# Submitting the same digest twice is not an error and does not change it.
accepts control.registry-push-is-idempotent \
  "${PROTOCOL}" push --oci "${OCI}" --repo "${repo}" --plain-http
accepts control.registry-verify-after-second-push \
  "${PROTOCOL}" verify --oci "${OCI}" --repo "${repo}" --out "${WORK}/pulled-again" --plain-http
if cmp -s "${WORK}/pulled/manifest.json" "${WORK}/pulled-again/manifest.json"; then
  record control.registry-idempotent-bytes yes "the second push returned the same manifest bytes"
else
  record control.registry-idempotent-bytes no "the second push changed the stored manifest"
fi

# ── The registry rejects what it must ───────────────────────────────────────
# A manifest whose blobs the registry does not hold.
refuses control.registry-refuses-a-manifest-with-a-missing-blob \
  oras manifest push --plain-http "${REGISTRY}/w1r/no-blobs@${digest}" \
  --media-type application/vnd.oci.image.manifest.v1+json "${OCI}/manifest.json"

# Bytes submitted under a digest that is not their own.
wrong_digest="sha256:$(printf '0%.0s' $(seq 64))"
refuses control.registry-refuses-a-wrong-manifest-digest \
  oras manifest push --plain-http "${repo}@${wrong_digest}" \
  --media-type application/vnd.oci.image.manifest.v1+json "${OCI}/manifest.json"

# Nothing was ever tagged, so a tag resolves to nothing. The accepted identity
# is the digest and only the digest.
refuses control.registry-holds-no-tag \
  oras manifest fetch --plain-http "${repo}:latest"

# A different repository does not hold it either: the name is part of the
# reference, not decoration.
refuses control.registry-refuses-a-different-repository \
  "${PROTOCOL}" fetch --oci "${OCI}" --repo "${REGISTRY}/w1r/somewhere-else" \
  --out "${WORK}/elsewhere" --plain-http

# ── The comparison rejects what a hostile registry could return ─────────────
# Each control damages exactly one thing in a COPY of what came back, and
# requires `compare` to refuse it.
tamper() {
  local name="$1" damage="$2"
  local dir="${WORK}/tamper-${name}"
  rm -rf "${dir}"
  cp -r "${WORK}/pulled" "${dir}"
  ( cd "${dir}" && eval "${damage}" )
  refuses "control.${name}" "${PROTOCOL}" compare --oci "${OCI}" --fetched "${dir}"
}

tamper registry-rewrote-the-manifest "printf ' ' >> manifest.json"
tamper registry-returned-an-altered-layer "printf 'x' >> layer.tar"
tamper registry-returned-a-truncated-layer "truncate -s -1 layer.tar"
tamper registry-returned-an-altered-config "printf 'x' >> config.json"
tamper registry-returned-no-config "rm config.json"

# A reviewed descriptor claiming a size the bytes do not have. The manifest is
# the untouched one, so only the size comparison can catch this.
size_dir="${WORK}/wrong-size"
rm -rf "${size_dir}"
cp -r "${OCI}" "${size_dir}"
python3 - "${size_dir}/descriptor.json" <<'PY'
import json, sys
path = sys.argv[1]
descriptor = json.load(open(path))
descriptor["layerSize"] += 1
json.dump(descriptor, open(path, "w"), indent=2, sort_keys=True)
PY
refuses control.reviewed-descriptor-with-a-wrong-blob-size \
  "${PROTOCOL}" compare --oci "${size_dir}" --fetched "${WORK}/pulled"

echo
echo "${PASSED} passed, ${FAILED} failed"
[ "${FAILED}" -eq 0 ]
