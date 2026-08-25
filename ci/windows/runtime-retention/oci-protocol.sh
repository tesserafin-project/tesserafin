#!/usr/bin/env bash
# The one registry implementation for the retained runtime (#236, W1-A4).
#
# Every registry interaction goes through here so that the digest rule is stated
# in the place that actually talks to the registry, not only in the caller. The
# Python contract makes the same statement; two independent statements of one
# rule is the point, not duplication to be tidied away.
#
# Subcommands:
#   push     blobs then manifest, addressed by digest, never by tag
#   tag      apply the immutable acceptance tag, refusing to repoint it
#   fetch    pull the manifest and the blobs it names
#   compare  byte-compare what the registry stored against what was reviewed
#   verify   fetch + compare
#
# `oras manifest push` is used and `oras push` is NEVER used: the latter builds
# a manifest of its own and would inject org.opencontainers.image.created,
# changing the digest that consumers pin.
set -euo pipefail

OCI=""; REPO=""; OUT=""; TAG=""
PLAIN=()

stop() { echo "W1-A4 REGISTRY HARD STOP: $*" >&2; exit 1; }

field() {
  python3 -c "
import json,sys
print(json.load(open(sys.argv[1]))[sys.argv[2]])
" "$1" "$2"
}

require_digest_reference() {
  # A tag is never an accepted identity. Refused here as well as in the caller,
  # because this script is what actually talks to the registry.
  #
  # Every path component excludes ':' deliberately. An earlier form used
  # `[^@]+` for the repository path, which let a reference carrying BOTH a tag
  # and a digest through — `host:5000/owner/name:sometag@sha256:...` matched,
  # because the tag hid inside the "path". The Python contract refuses that
  # case by name; this one silently accepted it. Two independent statements of
  # one rule are only worth having if they actually agree, and a local-registry
  # control is what showed that they did not.
  [[ "$1" =~ ^[^@:/]+(:[0-9]+)?(/[^@:/]+)+@sha256:[0-9a-f]{64}$ ]] \
    || stop "'$1' is not digest-pinned; a tag is not an identity this protocol accepts"
}

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

  require_digest_reference "${REPO}@${manifest}"
  oras manifest push "${PLAIN[@]}" "${REPO}@${manifest}" \
    --media-type "application/vnd.oci.image.manifest.v1+json" \
    "${OCI}/manifest.json"
  echo "pushed ${REPO}@${manifest}"
}

do_tag() {
  [ -n "${OCI}" ] && [ -n "${REPO}" ] && [ -n "${TAG}" ] || stop 'tag needs --oci, --repo and --tag'
  local manifest resolved
  manifest="$(field "${OCI}/descriptor.json" manifestDigest)"

  # An immutable tag may exist for discovery. It is never repointed, so the only
  # three outcomes are: absent (create it), already exactly this digest (an
  # idempotent no-op), or pointing elsewhere (refuse).
  set +e
  resolved="$(oras manifest fetch "${PLAIN[@]}" --descriptor "${REPO}:${TAG}" 2>/dev/null \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["digest"])' 2>/dev/null)"
  local status=$?
  set -e

  if [ ${status} -eq 0 ] && [ -n "${resolved}" ]; then
    if [ "${resolved}" = "${manifest}" ]; then
      echo "tag ${TAG} already resolves to ${manifest}; nothing to do"
      return 0
    fi
    stop "tag ${TAG} already resolves to ${resolved}, but the reviewed digest is ${manifest}. An immutable tag is never repointed."
  fi

  oras tag "${PLAIN[@]}" "${REPO}@${manifest}" "${TAG}"
  echo "tagged ${REPO}@${manifest} as ${TAG}"
}

do_fetch() {
  [ -n "${REPO}" ] && [ -n "${OUT}" ] || stop 'fetch needs --repo and --out'
  [ -n "${OCI}" ] || stop 'fetch needs --oci to know which digest to ask for'
  local manifest
  manifest="$(field "${OCI}/descriptor.json" manifestDigest)"
  require_digest_reference "${REPO}@${manifest}"

  mkdir -p "${OUT}/blobs/sha256"
  oras manifest fetch "${PLAIN[@]}" --output "${OUT}/manifest.json" "${REPO}@${manifest}"

  # The blobs are resolved from WHAT CAME BACK, not from the local descriptor. A
  # registry that returned a manifest naming other blobs must be caught by the
  # comparison, not hidden by asking for the ones we already trust.
  local returned_layer returned_config
  returned_layer="$(python3 -c "
import json,sys
print(json.load(open(sys.argv[1]))['layers'][0]['digest'])
" "${OUT}/manifest.json")"
  returned_config="$(python3 -c "
import json,sys
print(json.load(open(sys.argv[1]))['config']['digest'])
" "${OUT}/manifest.json")"

  oras blob fetch "${PLAIN[@]}" --output "${OUT}/blobs/sha256/${returned_layer#sha256:}" \
    "${REPO}@${returned_layer}"
  oras blob fetch "${PLAIN[@]}" --output "${OUT}/blobs/sha256/${returned_config#sha256:}" \
    "${REPO}@${returned_config}"
  echo "fetched ${REPO}@${manifest}"
}

do_compare() {
  [ -n "${OCI}" ] && [ -n "${OUT}" ] || stop 'compare needs --oci and --out'

  cmp "${OUT}/manifest.json" "${OCI}/manifest.json" \
    || stop 'the registry returned different manifest bytes than the reviewed ones'

  python3 - "${OCI}" "${OUT}" <<'PY'
import hashlib, json, pathlib, sys

reviewed = pathlib.Path(sys.argv[1])
fetched = pathlib.Path(sys.argv[2])
descriptor = json.loads((reviewed / "descriptor.json").read_bytes())

manifest_bytes = (fetched / "manifest.json").read_bytes()
digest = "sha256:" + hashlib.sha256(manifest_bytes).hexdigest()
if digest != descriptor["manifestDigest"]:
    raise SystemExit(f"the stored manifest hashes to {digest}, not {descriptor['manifestDigest']}")
if len(manifest_bytes) != descriptor["manifestSize"]:
    raise SystemExit(
        f"the stored manifest is {len(manifest_bytes)} bytes, not {descriptor['manifestSize']}"
    )

manifest = json.loads(manifest_bytes)
for what, want_digest, want_size in (
    ("config", descriptor["configDigest"], descriptor["configSize"]),
    ("layer", descriptor["layerDigest"], descriptor["layerSize"]),
):
    node = manifest["config"] if what == "config" else manifest["layers"][0]
    if node["digest"] != want_digest:
        raise SystemExit(f"the stored manifest names {what} {node['digest']}, not {want_digest}")
    # The size is checked as well as the digest. A descriptor that lies about a
    # size cannot be caught by the manifest digest alone.
    if node["size"] != want_size:
        raise SystemExit(f"the stored manifest gives {what} size {node['size']}, not {want_size}")

    blob = fetched / "blobs" / "sha256" / want_digest.removeprefix("sha256:")
    actual = hashlib.sha256()
    with blob.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            actual.update(chunk)
    if "sha256:" + actual.hexdigest() != want_digest:
        raise SystemExit(f"the stored {what} blob does not hash to {want_digest}")
    if blob.stat().st_size != want_size:
        raise SystemExit(f"the stored {what} blob is not {want_size} bytes")

print("the registry stored exactly the reviewed bytes")
PY
}

command="${1:-}"
[ -n "${command}" ] || stop 'a subcommand is required'
shift || true

while [ $# -gt 0 ]; do
  case "$1" in
    --oci)        OCI="$2"; shift 2 ;;
    --repo)       REPO="$2"; shift 2 ;;
    --out)        OUT="$2"; shift 2 ;;
    --tag)        TAG="$2"; shift 2 ;;
    --plain-http) PLAIN=(--plain-http); shift ;;
    *) stop "unknown argument '$1'" ;;
  esac
done

case "${command}" in
  push)    do_push ;;
  tag)     do_tag ;;
  fetch)   do_fetch ;;
  compare) do_compare ;;
  verify)  do_fetch; do_compare ;;
  *)       stop "unknown subcommand '${command}'" ;;
esac
