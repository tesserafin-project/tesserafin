#!/usr/bin/env bash
# #127: use published historical images, never build a substitute candidate.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}"
EVIDENCE="${REPO_ROOT}/artifacts/forward-migration"
mkdir -p "${EVIDENCE}"

BASELINE='ghcr.io/tesserafin-project/tesserafin-server@sha256:fd1fa9e0f5a28a07e5872cc5ff13257a92d988717a33519f62c4b26c6ab36249'
BASE_REVISION='44f5ab62b522684b4fa58ed10de80b8c6a7bb392'
# Annotated v1.1.0 tag -> bb2234aebbf0ff2c1cbe291df48f2ffa62beea46.
# This is the version-contract's immutable source tag, NOT a claim that an
# image was published for this source revision. Preflight must prove that.
CAND_REVISION='bb2234aebbf0ff2c1cbe291df48f2ffa62beea46'
CANDIDATE_TAG="ghcr.io/tesserafin-project/tesserafin-server:sha-${CAND_REVISION}"

HARNESS_SOURCE="$(git rev-parse HEAD)"
printf 'Harness source: %s\n' "${HARNESS_SOURCE}"
printf 'GitHub run: %s attempt %s\n' "${GITHUB_RUN_ID:-local}" "${GITHUB_RUN_ATTEMPT:-local}"
printf 'Baseline: %s\nCandidate lookup: %s\n' "${BASELINE}" "${CANDIDATE_TAG}"

# Inspect manifests first: an absent/unreadable candidate is a failed
# prerequisite, never a green migration run or permission to rebuild 1.1.0.
if ! docker manifest inspect "${BASELINE}" > "${EVIDENCE}/baseline-manifest.json"; then
  echo 'BLOCKED: canonical baseline manifest is unavailable; check package access/retention.' >&2
  exit 1
fi
if ! docker manifest inspect "${CANDIDATE_TAG}" > "${EVIDENCE}/candidate-manifest.json"; then
  echo 'BLOCKED: no readable published container at the v1.1.0 source tag.' >&2
  echo 'Record the registry error. Supply verified existing image provenance before changing this lookup.' >&2
  exit 1
fi

docker pull --platform linux/amd64 "${BASELINE}"
docker pull --platform linux/amd64 "${CANDIDATE_TAG}"

# Docker inspect includes environment values; do not retain its raw output.
# Store only identity fields, and require source/architecture agreement before
# executing either historical image or accepting its migration result.
docker image inspect "${BASELINE}" "${CANDIDATE_TAG}" | python3 -c '
import json, pathlib, re, sys
images = json.load(sys.stdin)
if len(images) != 2:
    raise SystemExit("expected exactly two inspected images")
result = {}
for role, image, revision in zip(("baseline", "candidate"), images, sys.argv[2:]):
    labels = image.get("Config", {}).get("Labels") or {}
    if labels.get("org.opencontainers.image.revision") != revision:
        raise SystemExit(role + ": OCI source revision does not match the selected historical commit")
    if image.get("Architecture") != "amd64" or image.get("Os") != "linux":
        raise SystemExit(role + ": expected linux/amd64")
    refs = [r for r in image.get("RepoDigests", []) if re.fullmatch(
        r"ghcr\.io/tesserafin-project/tesserafin-server@sha256:[0-9a-f]{64}", r)]
    if len(set(refs)) != 1:
        raise SystemExit(role + ": expected one unambiguous registry digest")
    result[role] = {
        "ref": refs[0], "revision": revision, "architecture": "linux/amd64",
        "version": labels.get("org.opencontainers.image.version"),
    }
pathlib.Path(sys.argv[1]).write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps(result, indent=2))
' "${EVIDENCE}/images.json" "${BASE_REVISION}" "${CAND_REVISION}"

CANDIDATE="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["candidate"]["ref"])' "${EVIDENCE}/images.json")"
# Keep the original harness assertions and --require-migration intact.
bash docker/upgrade-roundtrip.sh \
  --baseline "${BASELINE}" \
  --candidate "${CANDIDATE}" \
  --require-migration
