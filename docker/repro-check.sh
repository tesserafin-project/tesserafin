#!/usr/bin/env bash
# Reproducibility gate for the Tesserafin production image (#87 / [A1]).
#
# Builds linux/amd64 TWICE from the same commit with the same pinned inputs and
# no cache, then compares the resulting OCI image manifest digests. Equal digests
# => identical config + layers => reproducible. On mismatch it diffs the two OCI
# layouts (manifest, config, per-layer blob digests) so the divergence is named,
# not hand-waved.
#
# Usage: docker/repro-check.sh [--platform linux/amd64]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${REPO_ROOT}"

PLATFORM="linux/amd64"
[[ "${1:-}" == "--platform" ]] && PLATFORM="$2"
TARGET="${PLATFORM##*/}"   # amd64 / arm64

OUT="$(mktemp -d)"
trap 'rm -rf "${OUT}"' EXIT

manifest_digest() { # <oci-tar> -> top manifest digest referenced by the index
  tar -xf "$1" -C "$2"
  # index.json -> manifests[0].digest (the image manifest for single-platform build)
  python3 - "$2/index.json" <<'PY'
import json,sys
idx=json.load(open(sys.argv[1]))
print(idx["manifests"][0]["digest"])
PY
}

echo "== reproducibility: two clean ${PLATFORM} builds of the same commit =="
for n in 1 2; do
  echo "-- build ${n} --"
  docker/build-clean.sh --target "${TARGET}" --output "oci:${OUT}/b${n}.tar"
done

mkdir -p "${OUT}/x1" "${OUT}/x2"
D1="$(manifest_digest "${OUT}/b1.tar" "${OUT}/x1")"
D2="$(manifest_digest "${OUT}/b2.tar" "${OUT}/x2")"

echo
echo "  build 1 manifest: ${D1}"
echo "  build 2 manifest: ${D2}"

if [[ "${D1}" == "${D2}" ]]; then
  echo "REPRO: PASS — identical image manifest digest across two clean builds"
  exit 0
fi

echo "REPRO: MISMATCH — investigating divergence"
# Compare config + layer digests from each OCI layout to localise the difference.
for x in x1 x2; do
  echo "--- ${x} ---"
  python3 - "${OUT}/${x}" <<'PY'
import json,sys,os
root=sys.argv[1]
idx=json.load(open(os.path.join(root,"index.json")))
mdig=idx["manifests"][0]["digest"].split(":")[1]
man=json.load(open(os.path.join(root,"blobs","sha256",mdig)))
print("config:", man["config"]["digest"])
for l in man["layers"]:
    print("layer :", l["digest"], l.get("size"))
PY
done
exit 1
