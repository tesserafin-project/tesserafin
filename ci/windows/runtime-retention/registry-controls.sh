#!/usr/bin/env bash
# Registry-side controls for the retained runtime (#236, W1-A4).
#
# Run against a LOCAL registry over plain HTTP. Nothing here can reach ghcr.io:
# there is no login, no token and no credential, and the repository is passed in
# as localhost. The point is to prove the PROTOCOL — that the reviewed bytes are
# what gets stored, that a tag is never an identity, and that an immutable tag
# is never repointed — without publishing anything.
#
# `accepts` and `refuses` are separate helpers on purpose. A control that only
# ever asserts "this failed" cannot tell a working refusal from a broken script,
# so the suite asserts both directions.
#
# Deliberately NOT `set -o pipefail` around the match helpers below: a `grep -q`
# that matches closes the pipe, the writer dies of SIGPIPE, and pipefail turns
# that into exit 141 — which reads as an infrastructure flake rather than as the
# refusal it actually is.
set -eu

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROTOCOL="${HERE}/oci-protocol.sh"

OCI=""; REPO=""; WORK=""
while [ $# -gt 0 ]; do
  case "$1" in
    --oci)  OCI="$2";  shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --work) WORK="$2"; shift 2 ;;
    *) echo "unknown argument '$1'" >&2; exit 1 ;;
  esac
done
[ -n "${OCI}" ] && [ -n "${REPO}" ] && [ -n "${WORK}" ] || {
  echo "usage: registry-controls.sh --oci <dir> --repo <ref> --work <dir>" >&2; exit 1; }

mkdir -p "${WORK}"
failures=0

record() {
  printf '  %-6s %-46s %s\n' "$1" "$2" "$3"
  if [ "$1" != "RED" ] && [ "$1" != "OK" ]; then failures=$((failures + 1)); fi
}

accepts() {
  local name="$1"; shift
  set +e
  "$@" >"${WORK}/${name}.log" 2>&1
  local status=$?
  set -e
  if [ ${status} -eq 0 ]; then
    record OK "${name}" "accepted, as it must be"
  else
    record FAIL "${name}" "refused something that is legitimate (exit ${status})"
  fi
}

refuses() {
  local name="$1"; local expected="$2"; shift 2
  set +e
  "$@" >"${WORK}/${name}.log" 2>&1
  local status=$?
  set -e
  if [ ${status} -eq 0 ]; then
    record GREEN "${name}" "ACCEPTED what must be refused"
    return
  fi
  set +e
  local hits
  hits="$(grep -a -c -F -- "${expected}" "${WORK}/${name}.log")"
  set -e
  if [ "${hits}" != "0" ]; then
    record RED "${name}" "refused, naming the property"
  else
    record INERT "${name}" "failed, but not for the property under test (wanted '${expected}')"
  fi
}

manifest_digest="$(python3 -c "
import json,sys
print(json.load(open(sys.argv[1]))['manifestDigest'])
" "${OCI}/descriptor.json")"
tag="accepted-000000000000"

echo "registry controls against ${REPO}"

# ── the protocol works at all ───────────────────────────────────────────────
accepts control.push \
  "${PROTOCOL}" push --oci "${OCI}" --repo "${REPO}" --plain-http

accepts control.verify-stored-bytes \
  "${PROTOCOL}" verify --oci "${OCI}" --repo "${REPO}" --out "${WORK}/pulled" --plain-http

# Submitting the same digest twice is not an error and does not change it.
accepts control.push-is-idempotent \
  "${PROTOCOL}" push --oci "${OCI}" --repo "${REPO}" --plain-http

accepts control.verify-after-second-push \
  "${PROTOCOL}" verify --oci "${OCI}" --repo "${REPO}" --out "${WORK}/pulled-again" --plain-http

if cmp -s "${WORK}/pulled/manifest.json" "${WORK}/pulled-again/manifest.json"; then
  record OK control.idempotent-bytes "the second push returned the same manifest bytes"
else
  record FAIL control.idempotent-bytes "the second push changed the stored manifest"
fi

# ── a tag is never an identity ──────────────────────────────────────────────
# Before any tag is applied, the registry resolves none. The accepted identity
# is the digest, and only the digest.
refuses control.registry-holds-no-tag "not found" \
  oras manifest fetch --plain-http "${REPO}:latest"

accepts control.tag-applies \
  "${PROTOCOL}" tag --oci "${OCI}" --repo "${REPO}" --tag "${tag}" --plain-http

# Applying the same tag to the same digest is an idempotent no-op.
accepts control.tag-is-idempotent \
  "${PROTOCOL}" tag --oci "${OCI}" --repo "${REPO}" --tag "${tag}" --plain-http

# ── an immutable tag is never repointed ─────────────────────────────────────
# A second, different artifact is published, and the SAME tag is offered for it.
# That must be refused rather than moved.
rm -rf "${WORK}/other"
cp -r "${OCI}" "${WORK}/other"
python3 - "${WORK}/other" <<'PY'
import hashlib, json, pathlib, sys

# A different config produces a different manifest, and therefore a different
# identity. Nothing about the retained bytes changes; this is only a second
# artifact for the tag to be tempted by.
root = pathlib.Path(sys.argv[1])
manifest = json.loads((root / "manifest.json").read_bytes())
manifest["annotations"]["dev.tesserafin.runtime.controlOnly"] = "second artifact"
data = (json.dumps(manifest, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode()
(root / "manifest.json").write_bytes(data)
descriptor = json.loads((root / "descriptor.json").read_bytes())
descriptor["manifestDigest"] = "sha256:" + hashlib.sha256(data).hexdigest()
descriptor["manifestSize"] = len(data)
(root / "descriptor.json").write_bytes(
    (json.dumps(descriptor, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode()
)
PY

accepts control.second-artifact-pushes \
  "${PROTOCOL}" push --oci "${WORK}/other" --repo "${REPO}" --plain-http

refuses control.immutable-tag-not-repointed "is never repointed" \
  "${PROTOCOL}" tag --oci "${WORK}/other" --repo "${REPO}" --tag "${tag}" --plain-http

# The tag must still resolve to the ORIGINAL digest after that refusal.
resolved="$(oras manifest fetch --plain-http --descriptor "${REPO}:${tag}" 2>/dev/null \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["digest"])' 2>/dev/null || true)"
if [ "${resolved}" = "${manifest_digest}" ]; then
  record OK control.tag-still-original "the refused repoint left the tag where it was"
else
  record FAIL control.tag-still-original "the tag now resolves to '${resolved}'"
fi

# ── the protocol refuses a tag as an identity ───────────────────────────────
# The reference the fetch builds is `${REPO}:${tag}@${manifest}` — a tag AND a
# digest, which is the exact shape the old permissive `[^@]+` repository pattern
# let through. The reason token is asserted, not just the refusal: all three
# parsers now name this case identically.
refuses control.push-refuses-tag-reference "REFERENCE-REJECTED:tag-and-digest" \
  "${PROTOCOL}" fetch --oci "${OCI}" --repo "${REPO}:${tag}" --out "${WORK}/by-tag" --plain-http

# ── a write to a non-loopback registry needs saying so out loud ─────────────
# This is what makes the publication policy's exemption for this file worth
# anything: `oci-protocol.sh` is allowed to contain a push verb only because it
# refuses to write anywhere but loopback unless handed --allow-remote, and the
# validation workflow never hands it that. Offered a real registry, it stops
# before ORAS is invoked, so nothing here reaches the network.
refuses control.write-refuses-non-loopback "without --allow-remote" \
  "${PROTOCOL}" push --oci "${OCI}" \
  --repo ghcr.io/tesserafin-project/windows-ffmpeg-runtime --plain-http

refuses control.tag-refuses-non-loopback "without --allow-remote" \
  "${PROTOCOL}" tag --oci "${OCI}" \
  --repo ghcr.io/tesserafin-project/windows-ffmpeg-runtime --tag "${tag}" --plain-http

echo
if [ "${failures}" -ne 0 ]; then
  echo "W1-A4 REGISTRY HARD STOP: ${failures} control(s) did not reach their property" >&2
  exit 1
fi
echo "every registry control reached its property"
