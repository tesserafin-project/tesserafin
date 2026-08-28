#!/usr/bin/env bash
# Build the retention unit TWICE in separate clean directories and require the
# two to be byte-identical (#236, W1-A4).
#
# One build proves nothing about determinism. Two builds in directories with
# different names, at different times, in the same process tree, prove that
# neither the path, the clock, nor the order of a filesystem walk reached the
# bytes. That is the whole claim, and it is checked at four levels rather than
# one: the file inventory, every per-file digest, the layer/config/manifest
# bytes, and finally the manifest digest that consumers pin.
#
# Usage:
#   build-twice.sh --delivered <dir> --evidence-a <dir> --evidence-b <dir> \
#                  --comparison <file> --accepted <file> --work <dir>
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

DELIVERED=""; EVIDENCE_A=""; EVIDENCE_B=""; COMPARISON=""; ACCEPTED=""; WORK=""
while [ $# -gt 0 ]; do
  case "$1" in
    --delivered)  DELIVERED="$2"; shift 2 ;;
    --evidence-a) EVIDENCE_A="$2"; shift 2 ;;
    --evidence-b) EVIDENCE_B="$2"; shift 2 ;;
    --comparison) COMPARISON="$2"; shift 2 ;;
    --accepted)   ACCEPTED="$2";   shift 2 ;;
    --work)       WORK="$2";       shift 2 ;;
    *) echo "W1-A4 RETENTION HARD STOP: unknown argument '$1'" >&2; exit 1 ;;
  esac
done

for required in DELIVERED EVIDENCE_A EVIDENCE_B COMPARISON ACCEPTED WORK; do
  if [ -z "${!required}" ]; then
    echo "W1-A4 RETENTION HARD STOP: --${required,,} is required" >&2
    exit 1
  fi
done

# Deliberately different directory names. If a path ever reached the bytes,
# two builds under 'first' and 'second' would disagree and this would catch it.
rm -rf "${WORK}/first" "${WORK}/second"
mkdir -p "${WORK}/first" "${WORK}/second"

build_once() {
  local slot="$1"
  python3 "${HERE}/assemble.py" \
    --delivered   "${DELIVERED}" \
    --evidence-a  "${EVIDENCE_A}" \
    --evidence-b  "${EVIDENCE_B}" \
    --comparison  "${COMPARISON}" \
    --accepted    "${ACCEPTED}" \
    --out         "${WORK}/${slot}/unit" >/dev/null
  python3 "${HERE}/build-oci.py" \
    --unit     "${WORK}/${slot}/unit" \
    --accepted "${ACCEPTED}" \
    --out      "${WORK}/${slot}/oci" >/dev/null
}

echo "build 1 of 2 ..."
build_once first
echo "build 2 of 2 ..."
build_once second

fail=0
report() {
  if [ "$1" = "yes" ]; then
    printf '  IDENTICAL   %s\n' "$2"
  else
    printf '  DIFFERENT   %s\n' "$2"
    fail=1
  fi
}

echo "comparing:"

# 1. file inventory
if diff <(cd "${WORK}/first/unit"  && find . -type f | sort) \
        <(cd "${WORK}/second/unit" && find . -type f | sort) >/dev/null; then
  report yes "unit file inventory"
else
  report no  "unit file inventory"
fi

# 2. per-file digests
if diff <(cd "${WORK}/first/unit"  && find . -type f | sort | xargs sha256sum) \
        <(cd "${WORK}/second/unit" && find . -type f | sort | xargs sha256sum) >/dev/null; then
  report yes "per-file digests"
else
  report no  "per-file digests"
fi

# 3. layer, config and manifest BYTES, not just their digests
if diff <(cd "${WORK}/first/oci"  && find . -type f | sort) \
        <(cd "${WORK}/second/oci" && find . -type f | sort) >/dev/null; then
  report yes "oci layout inventory"
else
  report no  "oci layout inventory"
fi

for blob in $(cd "${WORK}/first/oci/blobs/sha256" && ls); do
  if cmp -s "${WORK}/first/oci/blobs/sha256/${blob}" \
            "${WORK}/second/oci/blobs/sha256/${blob}"; then
    report yes "blob ${blob:0:16}…"
  else
    report no  "blob ${blob:0:16}…"
  fi
done

if cmp -s "${WORK}/first/oci/manifest.json" "${WORK}/second/oci/manifest.json"; then
  report yes "manifest bytes"
else
  report no  "manifest bytes"
fi

# 4. the digest consumers pin
first_digest="$(python3 -c "
import hashlib,sys
print('sha256:'+hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest())
" "${WORK}/first/oci/manifest.json")"
second_digest="$(python3 -c "
import hashlib,sys
print('sha256:'+hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest())
" "${WORK}/second/oci/manifest.json")"

if [ "${first_digest}" = "${second_digest}" ]; then
  report yes "manifest digest ${first_digest}"
else
  report no  "manifest digest ${first_digest} vs ${second_digest}"
fi

committed="$(python3 -c "
import json,sys
print(json.load(open(sys.argv[1]))['manifestDigest'])
" "${ACCEPTED}")"
if [ "${first_digest}" = "${committed}" ]; then
  report yes "matches the committed accepted-runtime.json digest"
else
  report no  "committed digest is ${committed}, built ${first_digest}"
fi

# 5. nothing that looks like a credential travelled into the unit
echo "scanning the retained unit for credentials:"
if "${HERE}/scan-secrets.sh" "${WORK}/first/unit"; then
  report yes "no secret, token or runner credential in any retained file"
else
  report no  "credential-shaped content in the retained unit"
fi

if [ "${fail}" -ne 0 ]; then
  echo "W1-A4 RETENTION HARD STOP: the retention unit is not deterministic" >&2
  exit 1
fi
echo "PASS: two independent builds are byte-identical at ${first_digest}"
