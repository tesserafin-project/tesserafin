#!/usr/bin/env bash
# The registry protocol, as ONE implementation (#236, W1-R-B §3).
#
# The publisher and the local integration test must execute the same commands,
# or the rehearsal proves nothing about the performance. Everything either of
# them does to a registry is here:
#
#   push      the precomputed layer blob, the precomputed config blob and the
#             exact reviewed manifest bytes, addressed by digest
#   fetch     read the manifest back BY DIGEST, and the blobs it references
#   compare   the returned manifest bytes against the reviewed bytes, and the
#             config and layer digests and sizes against the descriptor
#   consumer  the ordinary consumer verification over the pulled content
#   verify    fetch, then compare, then consumer
#
# `compare` and `consumer` are separate subcommands rather than inlined into
# `verify` for one reason: a negative control can then damage what came back
# and require the comparison to refuse it, without this script carrying a
# tampering hook of its own.
#
# Usage:
#   oci-protocol.sh push     --oci DIR --repo REF [--plain-http]
#   oci-protocol.sh fetch    --oci DIR --repo REF --out DIR [--plain-http]
#   oci-protocol.sh compare  --oci DIR --fetched DIR
#   oci-protocol.sh consumer --fetched DIR [--trust DIR]
#   oci-protocol.sh verify   --oci DIR --repo REF --out DIR [--plain-http]

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MEDIA_TYPE='application/vnd.oci.image.manifest.v1+json'

stop() {
  echo "W1-R REGISTRY HARD STOP: $*" >&2
  exit 1
}

field() {
  python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]])' "$1" "$2"
}

require_digest_reference() {
  # A tag is never an accepted identity. Refused here as well as in the caller,
  # because this script is what actually talks to the registry.
  [[ "$1" =~ ^[^@:]+(:[0-9]+)?/[^@]+@sha256:[0-9a-f]{64}$ ]] \
    || stop "'$1' is not digest-pinned; a tag is not an identity this protocol accepts"
}

command="${1:-}"
shift || true

OCI=''
REPO=''
OUT=''
FETCHED=''
TRUST=''
PLAIN=()

while [ $# -gt 0 ]; do
  case "$1" in
    --oci) OCI="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --fetched) FETCHED="$2"; shift 2 ;;
    --trust) TRUST="$2"; shift 2 ;;
    --plain-http) PLAIN=(--plain-http); shift ;;
    *) stop "unknown argument '$1'" ;;
  esac
done

do_push() {
  [ -n "${OCI}" ] && [ -n "${REPO}" ] || stop 'push needs --oci and --repo'
  local descriptor="${OCI}/descriptor.json"
  local layer config manifest
  layer="$(field "${descriptor}" layerDigest)"
  config="$(field "${descriptor}" configDigest)"
  manifest="$(field "${descriptor}" manifestDigest)"

  # Blobs before the manifest: a manifest referencing a blob the registry does
  # not hold is rejected, and that ordering is the one worth having.
  oras blob push "${PLAIN[@]}" "${REPO}" "${OCI}/blobs/sha256/${layer#sha256:}"
  oras blob push "${PLAIN[@]}" "${REPO}" "${OCI}/blobs/sha256/${config#sha256:}"

  # `oras manifest push` and NEVER `oras push`: the latter constructs a manifest
  # of its own and would inject a created timestamp.
  require_digest_reference "${REPO}@${manifest}"
  oras manifest push "${PLAIN[@]}" "${REPO}@${manifest}" \
    --media-type "${MEDIA_TYPE}" "${OCI}/manifest.json"
  echo "pushed ${REPO}@${manifest}"
}

do_fetch() {
  [ -n "${OCI}" ] && [ -n "${REPO}" ] && [ -n "${OUT}" ] || stop 'fetch needs --oci, --repo and --out'
  local descriptor="${OCI}/descriptor.json"
  local manifest layer config
  manifest="$(field "${descriptor}" manifestDigest)"
  require_digest_reference "${REPO}@${manifest}"

  rm -rf "${OUT}"
  mkdir -p "${OUT}"
  oras manifest fetch "${PLAIN[@]}" "${REPO}@${manifest}" > "${OUT}/manifest.json"

  # The blobs are fetched from what CAME BACK, not from the local descriptor:
  # a registry that returned a manifest naming other blobs must be caught by
  # the comparison, not hidden by asking for the ones we already trust.
  layer="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["layers"][0]["digest"])' "${OUT}/manifest.json")"
  config="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["config"]["digest"])' "${OUT}/manifest.json")"
  oras blob fetch "${PLAIN[@]}" "${REPO}@${layer}" --output "${OUT}/layer.tar"
  oras blob fetch "${PLAIN[@]}" "${REPO}@${config}" --output "${OUT}/config.json"
  echo "fetched ${REPO}@${manifest} into ${OUT}"
}

do_compare() {
  [ -n "${OCI}" ] && [ -n "${FETCHED}" ] || stop 'compare needs --oci and --fetched'
  for required in manifest.json layer.tar config.json; do
    [ -f "${FETCHED}/${required}" ] \
      || stop "the read-back is missing ${required}; a blob the manifest references did not come back"
  done
  cmp "${FETCHED}/manifest.json" "${OCI}/manifest.json" \
    || stop 'the registry returned different manifest bytes than the reviewed ones'

  OCI="${OCI}" FETCHED="${FETCHED}" python3 - <<'PY'
import hashlib, json, os, sys

oci = os.environ["OCI"]
fetched = os.environ["FETCHED"]
descriptor = json.load(open(f"{oci}/descriptor.json"))
raw = open(f"{fetched}/manifest.json", "rb").read()

def stop(message):
    print(f"W1-R REGISTRY HARD STOP: {message}", file=sys.stderr)
    sys.exit(1)

digest = "sha256:" + hashlib.sha256(raw).hexdigest()
if digest != descriptor["manifestDigest"]:
    stop(f"the returned manifest hashes to {digest}, reviewed {descriptor['manifestDigest']}")
if len(raw) != descriptor["manifestSize"]:
    stop(f"the returned manifest is {len(raw)} bytes, reviewed {descriptor['manifestSize']}")

manifest = json.loads(raw)
checks = (
    ("config", manifest["config"], descriptor["configDigest"], descriptor["configSize"]),
    ("layer", manifest["layers"][0], descriptor["layerDigest"], descriptor["layerSize"]),
)
for label, entry, expected_digest, expected_size in checks:
    if entry["digest"] != expected_digest:
        stop(f"the returned manifest names {label} {entry['digest']}, reviewed {expected_digest}")
    if entry["size"] != expected_size:
        stop(f"the returned manifest gives {label} size {entry['size']}, reviewed {expected_size}")

# The blobs that actually came back, not merely the ones the manifest names.
for label, path, expected_digest, expected_size in (
    ("layer", f"{fetched}/layer.tar", descriptor["layerDigest"], descriptor["layerSize"]),
    ("config", f"{fetched}/config.json", descriptor["configDigest"], descriptor["configSize"]),
):
    hasher = hashlib.sha256()
    size = 0
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            hasher.update(chunk)
            size += len(chunk)
    actual = "sha256:" + hasher.hexdigest()
    if actual != expected_digest:
        stop(f"the returned {label} blob hashes to {actual}, reviewed {expected_digest}")
    if size != expected_size:
        stop(f"the returned {label} blob is {size} bytes, reviewed {expected_size}")

print(json.dumps({
    "probe": "w1r-registry-compare",
    "manifestDigest": digest,
    "manifestSize": len(raw),
    "configDigest": descriptor["configDigest"],
    "configSize": descriptor["configSize"],
    "layerDigest": descriptor["layerDigest"],
    "layerSize": descriptor["layerSize"],
    "manifestBytesIdentical": True,
}, indent=2, sort_keys=True))
PY
}

do_consumer() {
  [ -n "${FETCHED}" ] || stop 'consumer needs --fetched'
  local bundle_dir="${FETCHED}/bundle"
  rm -rf "${bundle_dir}"
  mkdir -p "${bundle_dir}"
  tar -xf "${FETCHED}/layer.tar" -C "${bundle_dir}"

  # Every delivered path against the bundle's own manifest.sha256 — the same
  # check the Windows installer makes before it hands anything to pacman.
  ( cd "${bundle_dir}" && sha256sum --quiet --check manifest.sha256 ) \
    || stop 'a pulled path does not match the bundle manifest'

  # Attribution, from the trust root that travelled inside the layer.
  python3 "${HERE}/signing.py" --bundle "${bundle_dir}" \
    --trust "${TRUST:-${bundle_dir}/trust}" > "${FETCHED}/signatures.json"

  python3 "${HERE}/verify-lock.py" --lock "${bundle_dir}/msys2-lock.json" > /dev/null
  echo "consumer verification passed over the pulled content"
}

case "${command}" in
  push) do_push ;;
  fetch) do_fetch ;;
  compare) do_compare ;;
  consumer) do_consumer ;;
  verify)
    do_fetch
    FETCHED="${OUT}"
    do_compare
    do_consumer
    ;;
  *) stop "unknown subcommand '${command}'; expected push, fetch, compare, consumer or verify" ;;
esac
