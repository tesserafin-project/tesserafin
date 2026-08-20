#!/usr/bin/env bash
# Live TV HLS capability propagation - structural inventory gate (#153-LTV-S1).
#
# The browser only reaches a Live TV segment because six distinct places cooperate. Any one of
# them disappearing - renamed, refactored away, or quietly deleted - reverts the path to "the
# playlist names uris the client cannot credential", which reads as a 401 on every fragment rather
# than as a compile error.
#
# Structural, not behavioural: it asserts the chain still EXISTS. Whether it is correct is what
# HlsManifestCredentialPropagatorTests, RequiresPlaybackCapabilityStashTests and the media
# authorization boundary suite are for.
#
# Exits non-zero if any category has zero matches, or if a forbidden pattern has any.
set -uo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || exit 2

FAILED=0
TOTAL=0

# count_category <name> <file> <pattern>
# grep -c into a variable, never `grep -q` in a pipeline: under `set -o pipefail` a `grep -q` that
# matches early exits 141 (SIGPIPE), which inverts the assertion.
count_category() {
    local name="$1" file="$2" pattern="$3"
    local count=0
    TOTAL=$((TOTAL + 1))

    if [ ! -f "$file" ]; then
        printf 'EMPTY  %-40s (missing file: %s)\n' "$name" "$file"
        FAILED=$((FAILED + 1))
        return
    fi

    count="$(grep -c -E -- "$pattern" "$file" || true)"
    count="${count:-0}"

    if [ "$count" -eq 0 ]; then
        printf 'EMPTY  %-40s (no match for /%s/ in %s)\n' "$name" "$pattern" "$file"
        FAILED=$((FAILED + 1))
    else
        printf 'OK     %-40s %s match(es) in %s\n' "$name" "$count" "$file"
    fi
}

# forbid_category <name> <file> <pattern>
forbid_category() {
    local name="$1" file="$2" pattern="$3"
    local count=0
    TOTAL=$((TOTAL + 1))

    if [ ! -f "$file" ]; then
        printf 'EMPTY  %-40s (missing file: %s)\n' "$name" "$file"
        FAILED=$((FAILED + 1))
        return
    fi

    count="$(grep -c -E -- "$pattern" "$file" || true)"
    count="${count:-0}"

    if [ "$count" -ne 0 ]; then
        printf 'FOUND  %-40s %s forbidden match(es) in %s\n' "$name" "$count" "$file"
        FAILED=$((FAILED + 1))
    else
        printf 'OK     %-40s no match for /%s/\n' "$name" "$pattern"
    fi
}

echo "Live TV HLS capability propagation inventory"
echo "============================================"

# 1. The proof that a capability was validated is written, and written on one branch only.
count_category "ValidatedPlaybackCapability record" \
    "Tesserafin.Api/Auth/PlaybackCapabilityPolicy/ValidatedPlaybackCapability.cs" \
    'public sealed record ValidatedPlaybackCapability'

count_category "stash on the accepted branch" \
    "Tesserafin.Api/Attributes/RequiresPlaybackCapabilityAttribute.cs" \
    'context\.HttpContext\.Items\[ValidatedPlaybackCapability\.ItemsKey\] = new ValidatedPlaybackCapability'

# 2. The transformer exists and handles both observed uri forms.
count_category "propagator entry point" \
    "Tesserafin.Api/Helpers/HlsManifestCredentialPropagator.cs" \
    'public static string Propagate\('

count_category "EXT-X-MAP handling" \
    "Tesserafin.Api/Helpers/HlsManifestCredentialPropagator.cs" \
    'MapTag = "#EXT-X-MAP:"'

count_category "unclassified uri refusal" \
    "Tesserafin.Api/Helpers/HlsManifestCredentialPropagator.cs" \
    'does not classify'

# 3. The live playlist actually calls it, and marks the response uncacheable.
count_category "live playlist calls the propagator" \
    "Tesserafin.Api/Controllers/DynamicHlsController.cs" \
    'HlsManifestCredentialPropagator\.Propagate\('

count_category "credential-bearing response is no-store" \
    "Tesserafin.Api/Controllers/DynamicHlsController.cs" \
    'Response\.Headers\.CacheControl = "private, no-store"'

# 4. The segment route's demand names the media source the capability is bound to.
count_category "segment route names mediaSourceId" \
    "Tesserafin.Api/Controllers/HlsSegmentController.cs" \
    'RequiresPlaybackCapability\(PlaybackCapabilityScope\.Media, "itemId", "mediaSourceId"\)'

# 5. Nothing on this path may hand a durable credential to a manifest. The forbidden-name guard is
#    the mechanism; this asserts the names are still listed, and that the propagator never writes
#    one of them as a literal parameter.
count_category "forbidden parameter names listed" \
    "Tesserafin.Api/Helpers/HlsManifestCredentialPropagator.cs" \
    '"webSocketTicket"'

forbid_category "no durable key appended" \
    "Tesserafin.Api/Helpers/HlsManifestCredentialPropagator.cs" \
    'Append\("(ApiKey|api_key|Authorization|webSocketTicket)"\)'

# 6. LTV-S0 must not be weakened: the pipe handoff is still the input selection.
count_category "S0 pipe handoff intact" \
    "Tesserafin.Controller/MediaEncoding/EncodingHelper.cs" \
    'ReadsFromDirectStreamPipe'

echo ""
if [ "$FAILED" -ne 0 ]; then
    echo "RESULT: FAIL — $FAILED of $TOTAL categories failed"
    exit 1
fi

echo "RESULT: PASS — $TOTAL of $TOTAL categories populated"
