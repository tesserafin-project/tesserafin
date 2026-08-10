#!/usr/bin/env bash
# SC2016: the single-quoted printf formats contain Markdown backticks, not
#         shell expansions. SC2317: `cleanup` runs from a trap.
# shellcheck disable=SC2016,SC2317
#
# Fail-closed semantic compatibility check between two canonical OpenAPI
# contracts — issue #162, foundation slice of #94 (C1) and #97 (C4).
#
# WHAT THIS IS NOT. It is not the drift check. `Tests` already runs
# `OpenApiContractTests.CommittedContract_MatchesRunningServer`, which proves
# that the committed `openapi/openapi.json` is byte-for-byte what the HEAD
# server generates. That answers "is the committed contract real?".
#
# WHAT THIS IS. It answers a different question: "does the contract move in a
# way that breaks existing clients?". It takes the canonical contract of the
# merge base and the canonical contract of the pull-request head, validates
# both against their `contract.lock.json` sidecars, and classifies the change.
# A byte difference is NOT automatically breaking; a byte-identical pair is
# compatible, but the engine still has to prove it can process the document.
#
# It never regenerates, edits or canonicalises either document. That belongs to
# `ci/openapi-generate.sh` alone (docs/openapi-contract.md).
#
# ENGINE. oasdiff, pinned by version AND immutable digest (see OASDIFF_IMAGE
# below). The previous engine, `openapitools/openapi-diff:2.1.6`, cannot parse
# this contract: run 30230606338 died with `java.lang.StackOverflowError`, and
# 2.1.7 — the current release — still overflows on the same document.
#
# POLICY. `ci/openapi-severity-levels.txt` raises four checks that oasdiff
# ships as `info`/`warning` but this project treats as breaking. Nothing is ever
# lowered, and there is no pre-1.0 waiver: an intentional breaking change stays
# red and gets argued for in review.
#
# VERDICT. Derived from oasdiff's structured JSON, never from matching strings
# in human-readable output:
#
#   0  COMPATIBLE     no change at or above ERR severity
#   1  BREAKING       at least one ERR-level change
#   2  INDETERMINATE  anything that means "we do not know" — missing or
#                     malformed input, invalid lock, hash mismatch, engine
#                     crash, engine timeout, unparsable engine output, missing
#                     report. Indeterminate is red on purpose.
#
# Usage:
#   ci/openapi-compat.sh \
#       --base      <base-openapi.json>       --base-lock <base-contract.lock.json> \
#       --head      <head-openapi.json>       --head-lock <head-contract.lock.json> \
#       --report    <report.md>               [--json <findings.json>]
#       [--severity-levels <policy.txt>]
#       [--corrective-ledger <ledger.json>] [--corrective-consumer <script>]
#
# CORRECTIVE TRANSITION. A recorded, one-shot, digest-scoped maintainer ruling
# may account for a specific breaking finding set — see
# ci/openapi-corrective-transition.json and ci/openapi-corrective-ledger.sh.
# It is consulted ONLY after the ordinary comparison has run with no exclusions
# and its complete structured output has been preserved, and only ever for a
# finding set matching the ruling verbatim between two named contract digests.
# It lowers no check, edits no severity, and excludes nothing from the run; a
# consumer that cannot run is INDETERMINATE, never a pass. When it applies the
# verdict is reported distinctly as "COMPATIBLE — CORRECTIVE TRANSITION" and
# every accepted finding is printed in full.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Pinned engine. Tag and digest together: the tag is what a human reads, the
# digest is what Docker actually resolves. Apache-2.0, https://github.com/Tufin/oasdiff.
OASDIFF_VERSION="v1.26.1"
OASDIFF_DIGEST="sha256:aae8cfcf7d18d3b0ebce6bdf407623bf8788ca318c7a0440627aaf583ed3e9f4"
OASDIFF_DEFAULT_IMAGE="tufin/oasdiff:${OASDIFF_VERSION}@${OASDIFF_DIGEST}"

# Image selection ONLY. This exists so the control suite can point the script
# at a deliberately broken image and prove that an engine failure lands on
# INDETERMINATE. It selects which image runs; it touches no verdict logic, so a
# bogus image can only ever produce red. It is not a bypass.
OASDIFF_IMAGE="${OASDIFF_IMAGE:-$OASDIFF_DEFAULT_IMAGE}"

# Hard ceiling. A hung engine must not hold a pull request open for six hours;
# it is an indeterminate result like any other.
OASDIFF_TIMEOUT_SECONDS="${OASDIFF_TIMEOUT_SECONDS:-300}"

EXIT_COMPATIBLE=0
EXIT_BREAKING=1
EXIT_INDETERMINATE=2

BASE_SPEC=""
BASE_LOCK=""
HEAD_SPEC=""
HEAD_LOCK=""
REPORT_PATH=""
JSON_PATH=""
SEVERITY_LEVELS="$SCRIPT_DIR/openapi-severity-levels.txt"

# One-shot corrective-transition ledger (#226/#227). Consulted ONLY after the
# ordinary comparison has already produced breaking findings, and only ever able
# to account for a finding set that matches a named ruling verbatim for two
# named digests. It cannot lower a check and it excludes nothing from the run.
CORRECTIVE_LEDGER="$SCRIPT_DIR/openapi-corrective-transition.json"
CORRECTIVE_CONSUMER="$SCRIPT_DIR/openapi-corrective-ledger.sh"

WORKDIR=""
cleanup() {
    [ -n "$WORKDIR" ] && rm -rf "$WORKDIR"
}
trap cleanup EXIT

log() { printf '%s\n' "$*"; }
err() { printf '%s\n' "$*" >&2; }

# Every abort funnels through here so no failure path can accidentally return 0.
indeterminate() {
    err "RESULT: INDETERMINATE — $*"
    write_report "INDETERMINATE" "$*"
    exit "$EXIT_INDETERMINATE"
}

usage() {
    err "usage: $0 --base <spec> --base-lock <lock> --head <spec> --head-lock <lock> --report <md> [--json <json>] [--severity-levels <policy.txt>] [--corrective-ledger <ledger.json>] [--corrective-consumer <script>]"
}

# The report is written on EVERY outcome, including the ones that abort early.
# "The engine crashed so there is nothing to show" is exactly the case where a
# reviewer needs something to read.
REPORT_BODY=""
write_report() {
    local verdict="$1" detail="$2"
    [ -n "$REPORT_PATH" ] || return 0
    mkdir -p "$(dirname "$REPORT_PATH")" 2>/dev/null || return 0
    {
        printf '## OpenAPI semantic compatibility: %s\n\n' "$verdict"
        [ -n "$detail" ] && printf '%s\n\n' "$detail"
        printf '| Field | Value |\n| --- | --- |\n'
        printf '| Engine | `%s` |\n' "$OASDIFF_IMAGE"
        printf '| Base contract | `%s` |\n' "${BASE_SPEC:-<unset>}"
        printf '| Base sha256 | `%s` |\n' "${BASE_SHA:-<not computed>}"
        printf '| Head contract | `%s` |\n' "${HEAD_SPEC:-<unset>}"
        printf '| Head sha256 | `%s` |\n' "${HEAD_SHA:-<not computed>}"
        printf '| Breaking findings | %s |\n' "${BREAKING_COUNT:-<not computed>}"
        printf '| Non-breaking findings | %s |\n' "${WARNING_COUNT:-<not computed>}"
        printf '\n'
        [ -n "$REPORT_BODY" ] && printf '%s\n' "$REPORT_BODY"
    } > "$REPORT_PATH"
}

BASE_SHA=""
HEAD_SHA=""
BREAKING_COUNT=""
WARNING_COUNT=""

while [ $# -gt 0 ]; do
    case "$1" in
        --base)      BASE_SPEC="${2:-}"; shift 2 || indeterminate "--base needs a value" ;;
        --base-lock) BASE_LOCK="${2:-}"; shift 2 || indeterminate "--base-lock needs a value" ;;
        --head)      HEAD_SPEC="${2:-}"; shift 2 || indeterminate "--head needs a value" ;;
        --head-lock) HEAD_LOCK="${2:-}"; shift 2 || indeterminate "--head-lock needs a value" ;;
        --report)    REPORT_PATH="${2:-}"; shift 2 || indeterminate "--report needs a value" ;;
        --json)      JSON_PATH="${2:-}"; shift 2 || indeterminate "--json needs a value" ;;
        --severity-levels) SEVERITY_LEVELS="${2:-}"; shift 2 || indeterminate "--severity-levels needs a value" ;;
        --corrective-ledger) CORRECTIVE_LEDGER="${2:-}"; shift 2 || indeterminate "--corrective-ledger needs a value" ;;
        --corrective-consumer) CORRECTIVE_CONSUMER="${2:-}"; shift 2 || indeterminate "--corrective-consumer needs a value" ;;
        -h|--help)   usage; exit "$EXIT_INDETERMINATE" ;;
        *)           usage; indeterminate "unknown argument: $1" ;;
    esac
done

for pair in "base:$BASE_SPEC" "base-lock:$BASE_LOCK" "head:$HEAD_SPEC" "head-lock:$HEAD_LOCK" "report:$REPORT_PATH"; do
    if [ -z "${pair#*:}" ]; then
        usage
        indeterminate "missing required argument --${pair%%:*}"
    fi
done

command -v docker  >/dev/null 2>&1 || indeterminate "docker is not available"
command -v jq      >/dev/null 2>&1 || indeterminate "jq is not available"
command -v sha256sum >/dev/null 2>&1 || indeterminate "sha256sum is not available"

WORKDIR="$(mktemp -d)" || indeterminate "cannot create a temporary directory"
STAGE="$WORKDIR/stage"
mkdir -p "$STAGE" || indeterminate "cannot create the staging directory"

# --- input validation -------------------------------------------------------

require_regular_file() {
    local label="$1" path="$2"
    [ -e "$path" ] || indeterminate "$label does not exist: $path"
    [ -f "$path" ] || indeterminate "$label is not an ordinary file: $path"
    [ -r "$path" ] || indeterminate "$label is not readable: $path"
    [ -s "$path" ] || indeterminate "$label is empty: $path"
}

require_regular_file "base contract"    "$BASE_SPEC"
require_regular_file "base lock file"   "$BASE_LOCK"
require_regular_file "head contract"    "$HEAD_SPEC"
require_regular_file "head lock file"   "$HEAD_LOCK"
require_regular_file "severity policy"  "$SEVERITY_LEVELS"

# A report that cannot be written is indistinguishable, to a reviewer, from a
# check that never ran. Prove the destination is writable BEFORE doing the
# work, so "no report" can never coexist with a green exit.
mkdir -p "$(dirname "$REPORT_PATH")" 2>/dev/null \
    || indeterminate "cannot create the report directory: $(dirname "$REPORT_PATH")"
: > "$REPORT_PATH" 2>/dev/null \
    || indeterminate "the report path is not writable: $REPORT_PATH"

# Parse, then check this is an OpenAPI 3 document with a non-empty `paths`
# object. The `paths` count is also the "did the engine actually have anything
# to inspect?" guard: an empty result over an empty input proves nothing.
#
# These validators publish their result through a global rather than through
# stdout on purpose. `x="$(validate ...)"` would run the body in a SUBSHELL,
# where `indeterminate`'s `exit 2` would kill only that subshell and let the
# caller sail on — a fail-open path in the one place that must fail closed.
SPEC_PATH_COUNT=""
validate_spec() {
    local label="$1" path="$2"
    jq -e 'type == "object"' "$path" >/dev/null 2>&1 \
        || indeterminate "$label is not parsable JSON: $path"
    jq -e '(.openapi // "") | test("^3\\.")' "$path" >/dev/null 2>&1 \
        || indeterminate "$label is not an OpenAPI 3 document (missing or unsupported \`openapi\` field): $path"
    jq -e '(.paths | type) == "object"' "$path" >/dev/null 2>&1 \
        || indeterminate "$label has no \`paths\` object: $path"
    local count
    count="$(jq -r '.paths | length' "$path" 2>/dev/null)" \
        || indeterminate "$label: cannot count paths: $path"
    [ "$count" -gt 0 ] 2>/dev/null \
        || indeterminate "$label declares zero paths, there is nothing to compare: $path"
    SPEC_PATH_COUNT="$count"
}

validate_spec "base contract" "$BASE_SPEC"
BASE_PATH_COUNT="$SPEC_PATH_COUNT"
validate_spec "head contract" "$HEAD_SPEC"
HEAD_PATH_COUNT="$SPEC_PATH_COUNT"

# The lock is the repository's own pin: {algorithm, sha256, spec, version}.
# A lock that does not describe the document it sits next to means the pair is
# untrustworthy, and an untrustworthy pair cannot yield a trustworthy verdict.
LOCK_ACTUAL_SHA=""
validate_lock() {
    local label="$1" lock="$2" spec="$3"
    jq -e 'type == "object"' "$lock" >/dev/null 2>&1 \
        || indeterminate "$label is not parsable JSON: $lock"
    jq -e '.algorithm == "sha256"' "$lock" >/dev/null 2>&1 \
        || indeterminate "$label does not declare \`\"algorithm\": \"sha256\"\`: $lock"
    jq -e '(.sha256 | type) == "string" and (.sha256 | test("^[0-9a-f]{64}$"))' "$lock" >/dev/null 2>&1 \
        || indeterminate "$label has no valid 64-hex \`sha256\` field: $lock"
    jq -e '(.spec | type) == "string" and (.spec | length) > 0' "$lock" >/dev/null 2>&1 \
        || indeterminate "$label has no \`spec\` field: $lock"
    jq -e '(.version | type) == "string" and (.version | length) > 0' "$lock" >/dev/null 2>&1 \
        || indeterminate "$label has no \`version\` field: $lock"

    local expected actual
    expected="$(jq -r '.sha256' "$lock")"
    actual="$(sha256sum "$spec" | cut -d' ' -f1)"
    if [ "$expected" != "$actual" ]; then
        indeterminate "$label sha256 mismatch: lock says $expected, $spec hashes to $actual"
    fi
    LOCK_ACTUAL_SHA="$actual"
}

validate_lock "base lock file" "$BASE_LOCK" "$BASE_SPEC"
BASE_SHA="$LOCK_ACTUAL_SHA"
validate_lock "head lock file" "$HEAD_LOCK" "$HEAD_SPEC"
HEAD_SHA="$LOCK_ACTUAL_SHA"

log "engine        : $OASDIFF_IMAGE"
log "base contract : $BASE_SPEC"
log "base sha256   : $BASE_SHA  (${BASE_PATH_COUNT} paths)"
log "head contract : $HEAD_SPEC"
log "head sha256   : $HEAD_SHA  (${HEAD_PATH_COUNT} paths)"

# --- semantic comparison ----------------------------------------------------

cp -- "$BASE_SPEC" "$STAGE/base.json" || indeterminate "cannot stage the base contract"
cp -- "$HEAD_SPEC" "$STAGE/head.json" || indeterminate "cannot stage the head contract"

# oasdiff's severity parser rejects comments and blank lines; the committed
# policy file carries its rationale inline, so strip both on the way in.
grep -vE '^[[:space:]]*(#|$)' "$SEVERITY_LEVELS" > "$STAGE/severity-levels.txt"
[ -s "$STAGE/severity-levels.txt" ] \
    || indeterminate "the severity policy declares no rule: $SEVERITY_LEVELS"

chmod a+r "$STAGE/base.json" "$STAGE/head.json" "$STAGE/severity-levels.txt" 2>/dev/null || true

# Bounded privileges: no network, read-only rootfs, read-only input mount, all
# capabilities dropped, no privilege escalation, container removed on exit.
# Output is captured from stdout on the host, so nothing needs a writable mount.
run_oasdiff() {
    timeout --signal=TERM --kill-after=10 "$OASDIFF_TIMEOUT_SECONDS" \
        docker run --rm \
            --network none \
            --read-only \
            --cap-drop ALL \
            --security-opt no-new-privileges \
            -v "$STAGE:/data:ro" \
            "$OASDIFF_IMAGE" \
            "$@"
}

FINDINGS="$WORKDIR/findings.json"
FINDINGS_ERR="$WORKDIR/findings.err"

# No `--fail-on`: oasdiff would then exit 1 both for "breaking changes found"
# and for "I failed", and the two must not be confused. Without it, exit 0 means
# the engine completed and stdout carries the structured answer.
# `--allow-external-refs=false` keeps a contract from pulling a remote $ref.
run_oasdiff breaking --allow-external-refs=false \
    --severity-levels /data/severity-levels.txt \
    -f json /data/base.json /data/head.json \
    >"$FINDINGS" 2>"$FINDINGS_ERR"
ENGINE_STATUS=$?

if [ "$ENGINE_STATUS" -eq 124 ] || [ "$ENGINE_STATUS" -eq 137 ]; then
    REPORT_BODY="$(printf '### Engine diagnostics\n\n```\n%s\n```\n' "$(tail -c 4000 "$FINDINGS_ERR")")"
    indeterminate "the semantic engine timed out after ${OASDIFF_TIMEOUT_SECONDS}s"
fi
if [ "$ENGINE_STATUS" -ne 0 ]; then
    REPORT_BODY="$(printf '### Engine diagnostics\n\n```\n%s\n```\n' "$(tail -c 4000 "$FINDINGS_ERR")")"
    indeterminate "the semantic engine exited $ENGINE_STATUS"
fi

# A completed engine returns a JSON array. Anything else — truncated output,
# a usage banner, an empty stdout — means we did not get an answer.
jq -e 'type == "array"' "$FINDINGS" >/dev/null 2>&1 \
    || indeterminate "the semantic engine claimed success but did not produce a JSON array"

BREAKING_COUNT="$(jq -r '[.[] | select((.level // 0) >= 3)] | length' "$FINDINGS")" \
    || indeterminate "cannot classify the engine findings"
WARNING_COUNT="$(jq -r '[.[] | select((.level // 0) == 2)] | length' "$FINDINGS")" \
    || indeterminate "cannot classify the engine findings"

if [ -n "$JSON_PATH" ]; then
    mkdir -p "$(dirname "$JSON_PATH")" 2>/dev/null || true
    cp -- "$FINDINGS" "$JSON_PATH" || indeterminate "cannot write the findings file: $JSON_PATH"
fi

# The human-readable half. Separate invocation, separate failure mode: a
# changelog that does not appear is an indeterminate result, never a green one.
CHANGELOG="$WORKDIR/changelog.md"
run_oasdiff changelog --allow-external-refs=false \
    --severity-levels /data/severity-levels.txt \
    -f markup /data/base.json /data/head.json \
    >"$CHANGELOG" 2>>"$FINDINGS_ERR"
CHANGELOG_STATUS=$?

if [ "$CHANGELOG_STATUS" -ne 0 ]; then
    indeterminate "the semantic engine exited $CHANGELOG_STATUS while rendering the changelog"
fi
[ -s "$CHANGELOG" ] || indeterminate "the semantic engine reported success but produced no changelog report"

REPORT_BODY="$(printf '### Changelog\n\n%s\n' "$(cat "$CHANGELOG")")"

if [ "$BREAKING_COUNT" -gt 0 ]; then
    log ""
    log "$(jq -r '.[] | select((.level // 0) >= 3) | "BREAKING  \(.operation // "-") \(.path // "-")  \(.id): \(.text)"' "$FINDINGS")"
    log ""
    # The ordinary comparison is complete and its full structured output is
    # already preserved. Only now may a recorded corrective transition be
    # consulted, and only for a finding set that matches a named ruling exactly
    # for the two named digests. Every unexpected status from the consumer —
    # including 126 (not executable) and 127 (not found) — is indeterminate:
    # a consumer that could not run must never read as "no exception applied".
    CORRECTIVE_OUT="$WORKDIR/corrective.log"
    "$CORRECTIVE_CONSUMER" \
        --ledger   "$CORRECTIVE_LEDGER" \
        --findings "$FINDINGS" \
        --base-sha "$BASE_SHA" \
        --head-sha "$HEAD_SHA" >"$CORRECTIVE_OUT" 2>&1
    CORRECTIVE_STATUS=$?

    case "$CORRECTIVE_STATUS" in
        0)
            log ""
            log "$(cat "$CORRECTIVE_OUT")"
            log ""
            REPORT_BODY="$(printf '### Accepted corrective transition\n\n```\n%s\n```\n\n%s' \
                "$(cat "$CORRECTIVE_OUT")" "$REPORT_BODY")"
            write_report "COMPATIBLE — CORRECTIVE TRANSITION" \
                "All $BREAKING_COUNT breaking finding(s) are accounted for by the recorded one-shot corrective transition between \`$BASE_SHA\` and \`$HEAD_SHA\`. No check was lowered and nothing was excluded from the comparison."
            [ -s "$REPORT_PATH" ] || indeterminate "the run completed but no report was written to $REPORT_PATH"
            log "RESULT: COMPATIBLE — CORRECTIVE TRANSITION — $BREAKING_COUNT accepted corrective finding(s), 0 unaccounted, $WARNING_COUNT non-breaking finding(s)"
            exit "$EXIT_COMPATIBLE"
            ;;
        1)
            log ""
            log "$(cat "$CORRECTIVE_OUT")"
            log ""
            ;;
        2)
            REPORT_BODY="$(printf '### Corrective-ledger diagnostics\n\n```\n%s\n```\n\n%s' \
                "$(cat "$CORRECTIVE_OUT")" "$REPORT_BODY")"
            indeterminate "the corrective-transition ledger could not be evaluated: $(tail -1 "$CORRECTIVE_OUT")"
            ;;
        *)
            REPORT_BODY="$(printf '### Corrective-ledger diagnostics\n\n```\n%s\n```\n\n%s' \
                "$(cat "$CORRECTIVE_OUT")" "$REPORT_BODY")"
            indeterminate "the corrective-transition ledger consumer exited $CORRECTIVE_STATUS (not a verdict)"
            ;;
    esac

    write_report "BREAKING" "$BREAKING_COUNT breaking change(s) detected between the base and head contracts."
    err "RESULT: BREAKING — $BREAKING_COUNT breaking change(s)"
    exit "$EXIT_BREAKING"
fi

if [ "$BASE_SHA" = "$HEAD_SHA" ]; then
    write_report "COMPATIBLE" "The base and head contracts are byte-identical (sha256 \`$HEAD_SHA\`), and the engine processed the full document."
else
    write_report "COMPATIBLE" "The contract changed but no breaking change was detected. $WARNING_COUNT non-breaking finding(s)."
fi
[ -s "$REPORT_PATH" ] || indeterminate "the run completed but no report was written to $REPORT_PATH"
log "RESULT: COMPATIBLE — 0 breaking change(s), $WARNING_COUNT non-breaking finding(s)"
exit "$EXIT_COMPATIBLE"
