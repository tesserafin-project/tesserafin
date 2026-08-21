#!/usr/bin/env bash
# HLS job-backed route inventory and ownership gate (#153-LTV-R3).
#
# WHY THIS EXISTS. #153-LTV-R2 found that the legacy HLS video segment route authorized any
# authenticated caller: the action read no principal at all, so a second user holding only their
# own durable token read the first user's live segment bytes. The repair is an authorizer that
# every job-backed HLS resource goes through. An authorizer applied to one route and forgotten on
# its siblings is worth nothing, so this gate enumerates the routes and asserts the boundary on
# each of them.
#
# TWO HALVES.
#   1. ANTI-VACUITY. Every route this gate claims to cover must still exist in the source. A
#      renamed or deleted route makes the corresponding check silently unverifiable, so a zero
#      match is a FAILURE, never a pass.
#   2. DISCOVERY. Every route literal declared by the two controllers that serve transcode output
#      must appear in the classification table below. An unclassified route is a HARD STOP: the
#      mission's words, and the only way this inventory can be trusted to be complete.
#
# Exits non-zero on any failure. Structural only: it asserts the boundary EXISTS, the xUnit
# suites assert it is correct.
set -uo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || exit 2

LEGACY="Tesserafin.Api/Controllers/HlsSegmentController.cs"
DYNAMIC="Tesserafin.Api/Controllers/DynamicHlsController.cs"

FAILED=0
CHECKS=0

fail() {
    printf 'FAIL   %s\n' "$1"
    FAILED=$((FAILED + 1))
}

ok() {
    printf 'OK     %s\n' "$1"
}

# count <file> <extended-regex>
# grep -c into a variable rather than `grep -q` in a pipeline: under `set -o pipefail` a `grep -q`
# that matches early can exit 141 (SIGPIPE), which inverts the assertion it is standing in for.
count() {
    local file="$1" pattern="$2" n
    [ -f "$file" ] || { echo 0; return; }
    n="$(grep -c -E -- "$pattern" "$file" 2>/dev/null || true)"
    echo "${n:-0}"
}

# present <label> <file> <pattern>   -- anti-vacuity: zero matches is a failure.
present() {
    local label="$1" file="$2" pattern="$3" n
    CHECKS=$((CHECKS + 1))
    n="$(count "$file" "$pattern")"
    if [ "$n" -eq 0 ]; then
        fail "$label (no match for /$pattern/ in $file)"
    else
        ok "$label ($n match(es))"
    fi
}

# absent <label> <file> <pattern>    -- the file must exist, and must not contain the pattern.
absent() {
    local label="$1" file="$2" pattern="$3" n
    CHECKS=$((CHECKS + 1))
    if [ ! -f "$file" ]; then
        fail "$label (missing file: $file)"
        return
    fi
    n="$(count "$file" "$pattern")"
    if [ "$n" -ne 0 ]; then
        fail "$label ($n forbidden match(es) for /$pattern/ in $file)"
    else
        ok "$label"
    fi
}

echo "HLS job-backed route inventory (#153-LTV-R3)"
echo "==========================================="
echo

# ---------------------------------------------------------------------------------------------
# CLASSIFICATION TABLE
#
# Every route literal declared by the two controllers below is listed here with its class:
#
#   DATA-PLANE   reads a file a transcoding job wrote. Must go through the ownership authorizer.
#   GENERATED    returns text this server composes in-process. Reads no job output, so there is no
#                job binding to authorize against; the caller is still bound by the route's own
#                capability demand.
#   CONTROL      acts on a job's lifetime rather than its bytes. Authorized separately and
#                deliberately NOT routed through the byte authorizer (see below).
#   UNREACHABLE  cannot be reached by any caller. Pinned by a test, not by an authorizer.
#
# The list is duplicated as a shell array so the discovery half can compare against it.
# ---------------------------------------------------------------------------------------------
declare -a DECLARED_ROUTES=(
    'Audio/{itemId}/hls/{segmentId}/stream.mp3|DATA-PLANE|legacy audio segment, resolved from segmentId alone before R3 (finding R2-2)'
    'Audio/{itemId}/hls/{segmentId}/stream.aac|DATA-PLANE|same action as the .mp3 literal'
    'Videos/{itemId}/hls/{playlistId}/stream.m3u8|UNREACHABLE|legacy playlist; its own guard rejects every request (see TheLegacyPlaylistRoute_RefusesEveryCaller)'
    'Videos/{itemId}/hls/{playlistId}/{segmentId}.{segmentContainer}|DATA-PLANE|legacy video segment; the route R2-1 was measured on'
    'Videos/ActiveEncodings|CONTROL|DELETE; kills jobs by caller-named deviceId/playSessionId'
    'Videos/{itemId}/live.m3u8|DATA-PLANE|reads the live job own playlist file off disk'
    'Videos/{itemId}/master.m3u8|GENERATED|master playlist composed in-process'
    'Audio/{itemId}/master.m3u8|GENERATED|master playlist composed in-process'
    'Videos/{itemId}/main.m3u8|GENERATED|variant playlist composed in-process by DynamicHlsPlaylistGenerator'
    'Audio/{itemId}/main.m3u8|GENERATED|variant playlist composed in-process by DynamicHlsPlaylistGenerator'
    'Videos/{itemId}/hls1/{playlistId}/{segmentId}.{container}|DATA-PLANE|dynamic video segment and, at segmentId -1, the fMP4 init map'
    'Audio/{itemId}/hls1/{playlistId}/{segmentId}.{container}|DATA-PLANE|dynamic audio segment and, at segmentId -1, the fMP4 init map'
)

echo "-- 1. anti-vacuity: every classified route still exists in the source"
for entry in "${DECLARED_ROUTES[@]}"; do
    route="${entry%%|*}"
    rest="${entry#*|}"
    class="${rest%%|*}"
    # The route literal appears inside an Http* attribute. Escape the regex metacharacters that
    # occur in route templates: { } . |
    # shellcheck disable=SC2016  # the single quotes are the point: this is a sed script, not a string to expand.
    escaped="$(printf '%s' "$route" | sed -e 's/[.[\*^$()+?{}|]/\\&/g')"
    file="$DYNAMIC"
    if grep -q -F -- "\"$route\"" "$LEGACY" 2>/dev/null; then
        file="$LEGACY"
    fi
    present "[$class] $route" "$file" "\"$escaped\""
done
echo

echo "-- 2. discovery: no route literal in these controllers is left unclassified"
CHECKS=$((CHECKS + 1))
UNCLASSIFIED=0
DISCOVERED=0
while IFS= read -r route; do
    [ -n "$route" ] || continue
    DISCOVERED=$((DISCOVERED + 1))
    found=0
    for entry in "${DECLARED_ROUTES[@]}"; do
        [ "${entry%%|*}" = "$route" ] && { found=1; break; }
    done
    if [ "$found" -eq 0 ]; then
        printf '       UNCLASSIFIED ROUTE: %s\n' "$route"
        UNCLASSIFIED=$((UNCLASSIFIED + 1))
    fi
done < <(grep -h -o -E '\[Http(Get|Post|Delete|Put|Head)\("[^"]+"' "$LEGACY" "$DYNAMIC" 2>/dev/null \
           | sed -e 's/.*("//' -e 's/"$//' | sort -u)

if [ "$DISCOVERED" -eq 0 ]; then
    fail "discovery found no route literals at all — the grep no longer matches the source"
elif [ "$UNCLASSIFIED" -ne 0 ]; then
    fail "discovery found $UNCLASSIFIED unclassified route literal(s) — HARD STOP"
else
    ok "discovery: $DISCOVERED route literal(s), all classified"
fi
echo

echo "-- 3. the ownership authorizer exists and every DATA-PLANE action goes through it"
present "authorizer type"                 "Tesserafin.Api/Auth/HlsJobOwnership/HlsJobOwnershipAuthorizer.cs" 'class HlsJobOwnershipAuthorizer'
present "authorizer interface"            "Tesserafin.Controller/MediaEncoding/IHlsJobOwnershipAuthorizer.cs" 'interface IHlsJobOwnershipAuthorizer'
present "legacy video segment authorized" "$LEGACY"  '_jobOwnership\.Authorize'
present "legacy audio segment authorized" "$LEGACY"  'ResolveBySegmentName'
present "dynamic segment authorized"      "$DYNAMIC" '_jobOwnership\.Authorize'
present "live playlist authorized"        "$DYNAMIC" '_jobOwnership\.Authorize'
echo

echo "-- 4. no DATA-PLANE action resolves a file from a caller-named id, and none bypasses"
absent "legacy: no transcode-folder resolution left" "$LEGACY" 'GetTranscodePath\(\)'
absent "legacy: no administrator bypass"             "$LEGACY" 'IsInRole\(|UserRoles\.Administrator'
absent "legacy: no api-key bypass"                   "$LEGACY" 'GetIsApiKey\(\)'
absent "dynamic: no administrator bypass"            "$DYNAMIC" 'UserRoles\.Administrator'
absent "dynamic: no api-key bypass"                  "$DYNAMIC" 'GetIsApiKey\(\)'
echo

echo "-- 5. the binding carries the owner, and the owner is captured from the server, not a query"
present "binding carries UserId"    "Tesserafin.Controller/MediaEncoding/HlsSegmentBinding.cs" 'Guid UserId'
present "binding carries DeviceId"  "Tesserafin.Controller/MediaEncoding/HlsSegmentBinding.cs" 'string\? DeviceId'
present "job carries UserId"        "Tesserafin.Controller/MediaEncoding/TranscodingJob.cs"    'Guid UserId'
present "owner captured at start"   "Tesserafin.MediaEncoding/Transcoding/TranscodeManager.cs" 'UserId = ownerUserId'
absent  "owner never from a query"  "Tesserafin.MediaEncoding/Transcoding/TranscodeManager.cs" 'UserId = state\.Request'
echo

echo "==========================================="
if [ "$FAILED" -ne 0 ]; then
    printf 'FAILED: %s of %s checks\n' "$FAILED" "$CHECKS"
    exit 1
fi
printf 'PASS: %s checks\n' "$CHECKS"
