#!/usr/bin/env bash
# Licence and provenance gate for the FFmpeg inside a built server image (#342).
#
# Usage: docker/assert-ffmpeg-licence.sh IMAGE
#
# Asks the image itself — its OCI labels, its CMD and the FFmpeg binary it
# actually ships — and fails if any of these hold:
#
#   * -buildconf contains --enable-nonfree or --enable-libfdk-aac
#     (FDK-AAC is not GPL-compatible under any GPL version; no label fixes that);
#   * -encoders or -decoders list libfdk_aac;
#   * org.opencontainers.image.licenses disagrees with -buildconf:
#       --enable-gpl + --enable-version3  =>  GPL-3.0-or-later
#       --enable-gpl only                 =>  GPL-2.0-or-later (or GPL-3.0-or-later)
#       neither                           =>  LGPL is possible; refused here, the
#                                             image has never shipped that and a
#                                             change to it must be reviewed;
#   * the image's FFmpeg is not the accepted F0 runtime: CMD does not name
#     /opt/tesserafin-ffmpeg/bin/ffmpeg, /opt/tesserafin-ffmpeg/ARCHIVE.sha256 is
#     missing or names an archive that is not in ci/package/f0-accepted-digests.txt,
#     or LICENSES/ / THIRD_PARTY_NOTICES.md / SOURCE.json are absent;
#   * a second FFmpeg is present (/usr/lib/jellyfin-ffmpeg, /usr/bin/ffmpeg).
#
# This is a technical gate, not legal advice.
set -euo pipefail

IMAGE="${1:?usage: assert-ffmpeg-licence.sh IMAGE}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ACCEPTED="${REPO_ROOT}/ci/package/f0-accepted-digests.txt"
RT=/opt/tesserafin-ffmpeg

FAILS=0
pass() { echo "  ok  : $*"; }
fail() { echo "  FAIL: $*"; FAILS=$((FAILS + 1)); }

in_image() { docker run --rm --network none --user 0:0 --entrypoint "$1" "${IMAGE}" "${@:2}"; }

echo "== FFmpeg licence gate: ${IMAGE}"

# --- what the image says it is ------------------------------------------------
LABEL="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.licenses" }}' "${IMAGE}")"
CMD_JSON="$(docker image inspect --format '{{ json .Config.Cmd }}' "${IMAGE}")"
echo "  label licences : ${LABEL:-<none>}"
echo "  CMD            : ${CMD_JSON}"

# The binary judged is the one the server is actually told to run.
FF="$(python3 -c 'import json,sys
c=json.loads(sys.argv[1]) or []
print(c[c.index("--ffmpeg")+1] if "--ffmpeg" in c[:-1] else "")' "${CMD_JSON}")"
[[ -n "${FF}" ]] || { fail "CMD passes no --ffmpeg"; exit 1; }
if [[ "${FF}" == "${RT}/bin/ffmpeg" ]]; then
  pass "CMD points the server at the F0 runtime (${FF})"
else
  fail "CMD points the server at ${FF}, not ${RT}/bin/ffmpeg"
fi

# --- what the binary says it is -----------------------------------------------
BUILDCONF="$(in_image "${FF}" -hide_banner -buildconf 2>&1)" || { fail "ffmpeg -buildconf failed"; echo "${BUILDCONF}"; exit 1; }
ENCODERS="$(in_image "${FF}" -hide_banner -encoders 2>/dev/null)"
DECODERS="$(in_image "${FF}" -hide_banner -decoders 2>/dev/null)"

has() { grep -qE -- "(^|[[:space:]])$1([[:space:]]|$)" <<<"${BUILDCONF}"; }

has --enable-nonfree     && fail "-buildconf has --enable-nonfree"     || pass "no --enable-nonfree"
has --enable-libfdk-aac  && fail "-buildconf has --enable-libfdk-aac"  || pass "no --enable-libfdk-aac"
grep -qE '[[:space:]]libfdk_aac[[:space:]]' <<<"${ENCODERS}" && fail "encoder libfdk_aac present" || pass "no libfdk_aac encoder"
grep -qE '[[:space:]]libfdk_aac[[:space:]]' <<<"${DECODERS}" && fail "decoder libfdk_aac present" || pass "no libfdk_aac decoder"

if has --enable-gpl && has --enable-version3; then
  want="GPL-3.0-or-later"
  [[ "${LABEL}" == "${want}" ]] && pass "label ${LABEL} matches --enable-gpl --enable-version3" \
                                || fail "label '${LABEL}' but -buildconf is --enable-gpl --enable-version3 (want ${want})"
elif has --enable-gpl; then
  [[ "${LABEL}" == "GPL-2.0-or-later" || "${LABEL}" == "GPL-3.0-or-later" ]] \
    && pass "label ${LABEL} compatible with --enable-gpl" \
    || fail "label '${LABEL}' but -buildconf is --enable-gpl"
else
  fail "-buildconf has no --enable-gpl: an LGPL image is a licensing change that needs review"
fi

# --- provenance: is it the accepted runtime? ----------------------------------
ARCHIVE_LINE="$(in_image cat "${RT}/ARCHIVE.sha256" 2>/dev/null || true)"
if [[ -z "${ARCHIVE_LINE}" ]]; then
  fail "${RT}/ARCHIVE.sha256 missing"
else
  if [[ -f "${ACCEPTED}" ]] && grep -qxF "${ARCHIVE_LINE}" "${ACCEPTED}"; then
    pass "runtime archive is accepted: ${ARCHIVE_LINE}"
  else
    fail "runtime archive '${ARCHIVE_LINE}' is not in ci/package/f0-accepted-digests.txt"
  fi
fi
if in_image test -e "${RT}/COMPRESSOR-DRIFT"; then
  fail "image was built with FFMPEG_ALLOW_COMPRESSOR_DRIFT=1 (local validation only): $(in_image cat "${RT}/COMPRESSOR-DRIFT")"
else
  pass "no compressor-drift marker"
fi
for f in LICENSES THIRD_PARTY_NOTICES.md SOURCE.json sbom.cdx.json; do
  in_image test -e "${RT}/${f}" && pass "${RT}/${f} shipped" || fail "${RT}/${f} missing"
done

# --- no second FFmpeg -----------------------------------------------------------
for p in /usr/lib/jellyfin-ffmpeg /usr/bin/ffmpeg /usr/local/bin/ffmpeg; do
  in_image test -e "${p}" && fail "a second FFmpeg is present at ${p}" || pass "no ${p}"
done
if in_image sh -c 'find / -xdev -name "libfdk-aac.so*" 2>/dev/null | grep -q .'; then
  fail "libfdk-aac.so present somewhere in the image"
else
  pass "no libfdk-aac.so anywhere in the image"
fi

echo
if [[ "${FAILS}" -eq 0 ]]; then
  echo "FFMPEG LICENCE GATE: PASS"
else
  echo "FFMPEG LICENCE GATE: FAIL (${FAILS})"
  exit 1
fi
