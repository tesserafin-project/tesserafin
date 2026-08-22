#!/usr/bin/env bash
# HLS ownership GUARD inventory — a reviewer-facing drift check (#153-LTV-R5, finding F4).
#
# WHAT THIS IS FOR. R4 finding F4: the comment at HlsJobOwnershipAuthorizer.cs:133-135 asserted
# that removing either the api-key guard or the owner-absent check "is a visible change rather than
# a silently redundant one". R5 reproduced both removals and both left the suite at 69/69 green, so
# the sentence was measurably false. The repair rewrites the description to say which guards exist,
# which one is authoritative, which are corroborative, in what order they run, and where the
# durable and capability paths diverge.
#
# WHAT THIS IS NOT. It is NOT evidence that the boundary is correct, and it must never be quoted as
# such. A comment cannot be a behavioural proof of anything. The behavioural evidence is
# HlsOwnershipMatrixTests' three families and the hostile controls in
# ci/hostile-controls/manifest.json. All this gate does is refuse to let the description drift away
# from the branches it describes: every guard the comment names must exist at the site it names,
# with the role it claims, and no guard may exist without being described.
#
# ANTI-VACUITY. Every check counts matches and a ZERO match is a FAILURE, never a pass. A renamed
# member or a deleted branch makes the corresponding claim unverifiable, and unverifiable is not
# satisfied.
#
# Exits non-zero on any failure.
set -uo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT" || exit 2

AUTHORIZER="Tesserafin.Api/Auth/HlsJobOwnership/HlsJobOwnershipAuthorizer.cs"

FAILED=0
CHECKS=0

fail() {
    printf 'FAIL  %s\n' "$1"
    FAILED=$((FAILED + 1))
}

pass() {
    printf 'ok    %s\n' "$1"
}

# Assert that a fixed string occurs exactly N times in the authorizer.
expect_count() {
    local needle="$1" want="$2" label="$3" got
    CHECKS=$((CHECKS + 1))
    got=$(grep -cF -- "$needle" "$AUTHORIZER")
    if [ "$got" != "$want" ]; then
        fail "$label — expected $want occurrence(s), found $got"
    else
        pass "$label ($got)"
    fi
}

# Assert that a guard's annotation is followed, within a short window, by the branch it describes.
# The window is what makes this a DRIFT check rather than a "both strings appear somewhere" check:
# moving the annotation away from its branch breaks it.
expect_annotated_branch() {
    local annotation="$1" branch="$2" label="$3" line window
    CHECKS=$((CHECKS + 1))
    line=$(grep -nF -- "$annotation" "$AUTHORIZER" | head -n 1 | cut -d: -f1)
    if [ -z "$line" ]; then
        fail "$label — the annotation '$annotation' is not in $AUTHORIZER"
        return
    fi
    window=$(sed -n "${line},$((line + 14))p" "$AUTHORIZER" | grep -cF -- "$branch")
    if [ "$window" = "0" ]; then
        fail "$label — '$annotation' is at line $line but the branch it describes is not within 14 lines of it"
    else
        pass "$label (annotation at line $line, branch within 14 lines)"
    fi
}

if [ ! -f "$AUTHORIZER" ]; then
    printf 'FAIL  %s does not exist; this gate cannot verify anything.\n' "$AUTHORIZER"
    exit 2
fi

printf '=== the four guards of CallerIsTheOwner, each at its own site ===\n'
expect_count 'GUARD 1 of 4' 1 'guard 1 is annotated exactly once'
expect_count 'GUARD 2 of 4' 1 'guard 2 is annotated exactly once'
expect_count 'GUARD 3 of 4' 1 'guard 3 is annotated exactly once'
expect_count 'GUARD 4 of 4' 1 'guard 4 is annotated exactly once'

expect_annotated_branch 'GUARD 1 of 4' '!user.Identity.IsAuthenticated' \
    'guard 1 describes the authentication branch'
expect_annotated_branch 'GUARD 2 of 4' 'user.GetIsApiKey()' \
    'guard 2 describes the api-key branch'
expect_annotated_branch 'GUARD 3 of 4' '!callerUserId.Equals(ownerUserId)' \
    'guard 3 describes the user-id comparison'
expect_annotated_branch 'GUARD 4 of 4' 'string.Equals(callerDeviceId, ownerDeviceId' \
    'guard 4 describes the device comparison'

printf '\n=== the roles, and they agree between the summary list and the sites ===\n'
expect_count 'GUARD 1 of 4, CORROBORATIVE' 1 'guard 1 is declared corroborative at its site'
expect_count 'GUARD 2 of 4, CORROBORATIVE' 1 'guard 2 is declared corroborative at its site'
expect_count 'GUARD 3 of 4, AUTHORITATIVE' 1 'guard 3 is declared authoritative at its site'
expect_count 'GUARD 4 of 4, AUTHORITATIVE' 1 'guard 4 is declared authoritative at its site'
expect_count 'THE FOUR GUARDS, IN THE ORDER THEY RUN' 1 'the summary list exists'
expect_count 'item>authentication — CORROBORATIVE' 1 'the summary lists guard 1 as corroborative'
expect_count 'item>api key — CORROBORATIVE' 1 'the summary lists guard 2 as corroborative'
expect_count 'item>user id — AUTHORITATIVE' 1 'the summary lists guard 3 as authoritative'
expect_count 'item>device — AUTHORITATIVE' 1 'the summary lists guard 4 as authoritative'

printf '\n=== the two divergences between the durable and the capability path ===\n'
expect_count 'WHERE THE DURABLE AND CAPABILITY PATHS DIVERGE — exactly twice' 1 \
    'the divergence claim is made'
expect_count 'capability?.Validation.UserId ?? user.GetUserId()' 1 \
    'divergence 1 exists in code: the user id has two sources and only two'
expect_count 'HlsJobOwnerDevice.Resolve(' 1 \
    'divergence 2 exists in code: the device is resolved by one shared function'

printf '\n=== the corroborative job-side check, and the claim R4 measured to be false ===\n'
expect_count 'STEP 2, CORROBORATIVE' 1 'the owner-absent check is described as corroborative'
expect_annotated_branch 'STEP 2, CORROBORATIVE' 'binding.UserId.IsEmpty() || string.IsNullOrEmpty(binding.DeviceId)' \
    'the owner-absent annotation describes the owner-absent branch'
expect_count 'is a visible change rather than a silently redundant one' 0 \
    'the sentence R4 measured to be false is gone'

printf '\n=== this gate does not claim to be behavioural evidence ===\n'
expect_count 'A COMMENT IS NOT A PROOF' 1 \
    'the remarks say so themselves, so a reader cannot mistake the annotation for a measurement'

printf '\n%s checks, %s failure(s)\n' "$CHECKS" "$FAILED"
printf 'This gate compares a description with the branches it describes. It is NOT evidence that\n'
printf 'the ownership boundary is correct: that is HlsOwnershipMatrixTests and the hostile controls.\n'

if [ "$FAILED" != "0" ]; then
    exit 1
fi
exit 0
