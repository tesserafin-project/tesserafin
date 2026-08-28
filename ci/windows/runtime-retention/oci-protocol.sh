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
ALLOW_REMOTE=0

stop() { echo "W1-A4 REGISTRY HARD STOP: $*" >&2; exit 1; }

field() {
  python3 -c "
import json,sys
print(json.load(open(sys.argv[1]))[sys.argv[2]])
" "$1" "$2"
}

# The reference grammar, restated. `contract.classify_reference` is the first
# statement and `consume.ps1` is the third; `reference-corpus.py` runs all three
# over one corpus and requires the same verdict AND the same reason token. An
# earlier form of this used `[^@]+` for the repository path, which let a
# reference carrying BOTH a tag and a digest through, and the PowerShell
# consumer kept that shape until R1. Two independent statements of one rule are
# only worth having if they actually agree.
REFERENCE_HOST='[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*(:[0-9]{1,5})?'
REFERENCE_COMPONENT='[a-z0-9]+([._-][a-z0-9]+)*'

classify_reference() {
  # Echoes the reason token, or nothing when the grammar accepts. Classification
  # order is identical to contract.py: at-count, digest shape, tag-and-digest,
  # then name.
  local reference="$1" ats name digest last
  ats="$(printf '%s' "${reference}" | tr -cd '@' | wc -c)"
  if [ "${ats}" -eq 0 ]; then
    last="${reference##*/}"
    case "${last}" in *:*) echo tag-only ;; *) echo missing-digest ;; esac
    return 0
  fi
  if [ "${ats}" -gt 1 ]; then echo multiple-at; return 0; fi
  name="${reference%@*}"
  digest="${reference#*@}"
  [[ "${digest}" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo malformed-digest; return 0; }
  last="${name##*/}"
  case "${last}" in *:*) echo tag-and-digest; return 0 ;; esac
  [[ "${name}" =~ ^${REFERENCE_HOST}(/${REFERENCE_COMPONENT})+$ ]] \
    || { echo malformed-name; return 0; }
  return 0
}

reference_reason_text() {
  case "$1" in
    tag-only)         echo "is not digest-pinned; a tag is never an accepted identity" ;;
    missing-digest)   echo "is not digest-pinned; it names no sha256 digest" ;;
    multiple-at)      echo "carries more than one '@'" ;;
    tag-and-digest)   echo "carries both a tag and a digest; use the digest alone" ;;
    malformed-digest) echo "does not end in a canonical lowercase sha256:<64 hex> digest" ;;
    malformed-name)   echo "has a malformed registry or repository" ;;
  esac
}

require_digest_reference() {
  local reason
  reason="$(classify_reference "$1")"
  [ -z "${reason}" ] \
    || stop "REFERENCE-REJECTED:${reason} '$1' $(reference_reason_text "${reason}")"
}

# A WRITE to a registry is only allowed against loopback unless the caller says
# otherwise in as many words. This is what makes the validation workflow's use of
# this script provably unable to publish: the retention workflow never passes
# --allow-remote, so every write path here refuses any host but localhost. The
# publication workflow passes it, and `publication_policy.py` refuses any
# workflow that does. Reads (fetch/compare/verify) are not gated: they cannot
# publish.
# The authority is PARSED, not string-matched. `${host%%:*}` — which this
# function used until W1-A4-R2 — yields `[` for `[::1]:5000`, the empty string
# for `::1:5000` and `user` for `user:pass@evil.example`, so every one of those
# compared unequal to every entry in a loopback list and only `[::1]` was
# accepted at all, because the literal string happened to be in the list.
#
# The verdict vocabulary here is the same vocabulary `publication_policy.py`
# returns, and `loopback-corpus.py` runs one corpus through both and requires
# identical verdicts entry by entry. Two hand-written parsers that agree only
# where somebody remembered to test are how a guard drifts open.
local_authority_verdict() {
  local rest="$1" host="" port="" bracketed=0 colons

  case "${rest}" in *@*) echo embedded-credentials; return 0 ;; esac

  case "${rest}" in
    '['*)
      case "${rest}" in
        *']'*) : ;;
        *) echo unclosed-bracket; return 0 ;;
      esac
      bracketed=1
      host="${rest#\[}"; host="${host%%]*}"
      port="${rest#*]}"
      if [ -n "${port}" ]; then
        case "${port}" in
          :*) port="${port#:}" ;;
          *)  echo junk-after-bracket; return 0 ;;
        esac
      fi
      ;;
    *)
      colons="${rest//[!:]/}"
      if [ "${#colons}" -gt 1 ]; then
        # Unbracketed with more than one colon cannot be split into host and
        # port without guessing which colon separates them. RFC 3986 requires
        # the brackets precisely so nobody has to guess.
        echo unbracketed-ipv6; return 0
      fi
      host="${rest%%:*}"
      port="${rest#"${host}"}"; port="${port#:}"
      ;;
  esac

  if [ -n "${port}" ]; then
    case "${port}" in *[!0-9]*) echo non-numeric-port; return 0 ;; esac
  fi
  [ -n "${host}" ] || { echo empty-host; return 0; }

  host="$(printf '%s' "${host}" | tr 'A-Z' 'a-z')"
  if [ "${host}" = "localhost" ]; then
    if [ "${bracketed}" -eq 1 ]; then echo non-loopback-name; else echo localhost; fi
    return 0
  fi

  case "${host}" in
    *:*)
      # IPv6. Only the two spellings publication_policy.SUPPORTED_IPV6_LOOPBACK
      # names are accepted; any other VALID loopback spelling fails closed under
      # its own reason rather than being permitted by whichever parser is the
      # more generous of the two.
      if [ "${bracketed}" -eq 0 ]; then echo unbracketed-ipv6; return 0; fi
      case "${host}" in
        ::1|0:0:0:0:0:0:0:1) echo loopback-address; return 0 ;;
      esac
      # Not a supported spelling. Say WHICH kind of refusal it is, in the same
      # vocabulary the Python side uses. Bash has no address parser, so
      # loopback-shape is decided textually: an IPv6 loopback is written
      # entirely from zeroes, colons and a single trailing 1, so deleting every
      # `0` and `:` leaves exactly `1` and nothing else does.
      local bare="${host//[0:]/}"
      if [ "${bare}" = "1" ]; then
        echo ipv6-form-not-supported
      else
        echo non-loopback-address
      fi
      return 0
      ;;
    *[!0-9.]*) echo non-loopback-name; return 0 ;;
  esac

  # A run of digits and dots. A dotted quad is an address; anything else is a
  # name that merely looks like one, and `127.0.0.1.evil` is exactly that.
  local first second third fourth extra octet
  IFS=. read -r first second third fourth extra <<<"${host}"
  if [ -n "${extra}" ] || [ -z "${fourth}" ]; then
    echo non-loopback-name; return 0
  fi
  for octet in "${first}" "${second}" "${third}" "${fourth}"; do
    case "${octet}" in ''|*[!0-9]*) echo non-loopback-name; return 0 ;; esac
    [ "${octet}" -le 255 ] || { echo non-loopback-name; return 0; }
  done
  if [ "${bracketed}" -eq 1 ]; then echo bracketed-ipv4; return 0; fi
  if [ "${first}" -eq 127 ]; then echo loopback-address; else echo non-loopback-address; fi
}

# A WRITE is permitted only against an explicitly supported LOCAL authority.
require_loopback_write() {
  local authority="${1%%/*}" verdict
  if [ "${ALLOW_REMOTE}" -eq 1 ]; then
    return 0
  fi
  verdict="$(local_authority_verdict "${authority}")"
  case "${verdict}" in
    localhost|loopback-address) return 0 ;;
  esac
  stop "refusing to write to the registry authority '${authority}' (${verdict}) without --allow-remote"
}

do_push() {
  [ -n "${OCI}" ] && [ -n "${REPO}" ] || stop 'push needs --oci and --repo'
  require_loopback_write "${REPO}"
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
  require_loopback_write "${REPO}"
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
    --allow-remote) ALLOW_REMOTE=1; shift ;;
    *) stop "unknown argument '$1'" ;;
  esac
done

case "${command}" in
  check-reference)
           # Grammar only, for the cross-language corpus. It talks to no
           # registry and takes no --oci.
           require_digest_reference "${REPO}"
           echo "the grammar accepts '${REPO}'" ;;
  check-authority)
           # The authority verdict, for the cross-language loopback corpus. It
           # calls the SAME function every write path calls, so a corpus entry
           # cannot pass here while the write path decides otherwise.
           [ -n "${REPO}" ] || stop 'check-authority needs --repo'
           local_authority_verdict "${REPO%%/*}" ;;
  push)    do_push ;;
  tag)     do_tag ;;
  fetch)   do_fetch ;;
  compare) do_compare ;;
  verify)  do_fetch; do_compare ;;
  *)       stop "unknown subcommand '${command}'" ;;
esac
