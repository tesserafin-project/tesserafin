#!/usr/bin/env bash
# Verify that the repository-local CodeQL barrier model pack is present, EXACT
# and ACTIVE in the analysis that is running right now (#203).
#
# WHY THIS SCRIPT EXISTS
#
# The pack at .github/codeql/extensions/csharp-log-barriers declares one thing:
# the value returned by
#
#   Tesserafin.Extensions.LogValueExtensions.ToSingleLogLine(System.String)
#
# is a barrier for the `log-injection` taint kind. cs/log-forging consumes that
# through `barrierNode(this, "log-injection")` in
# csharp/ql/lib/semmle/code/csharp/security/dataflow/LogForgingQuery.qll.
#
# Two of the three ways this can break are SILENT:
#
#   1. `extensionTargets: codeql/csharp-all` stops matching the bundled library
#      version. Measured against CodeQL CLI 2.26.0: the analysis does NOT fail.
#      It prints `WARNING: Extension pack '...' is unused.` and completes green,
#      with the barrier not applied and no alert re-opened until the next
#      default-branch scan.
#   2. Somebody adds a second row, widens a field to a wildcard, or flips
#      `subtypes` to true. Nothing in CodeQL objects: the model simply covers
#      more code than was ever reviewed.
#
# Only the third way — the pack being absent altogether — is loud, because
# `packs:` is declared in the workflow and `codeql database init` then dies with
# a registry 403.
#
# This script closes 1 and 2. It runs INSIDE the required `Analyze csharp` job,
# after `Initialize CodeQL`, and has no `continue-on-error`. It uses the pinned
# CodeQL CLI's own pack-resolution commands rather than reading YAML and hoping.
#
# Usage:
#   ./ci/verify-codeql-model-pack.sh <codeql-binary> <init-config-snapshot-dir>
#
# The snapshot directory is not optional: reading the analysis configuration that
# `codeql database init` actually wrote is the only check here that proves the
# pack is active in THIS analysis rather than merely resolvable on disk. The
# workflow copies it out of the database straight after init, because this script
# runs after `Perform CodeQL Analysis` and the action is free to clean the
# database up in between.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [ "$#" -ne 2 ]; then
    echo "usage: $0 <codeql-binary> <init-config-snapshot-dir>" >&2
    exit 2
fi

CODEQL="$1"
SNAPSHOT="$2"

EXT_DIR="$REPO_ROOT/.github/codeql/extensions"
PACK_DIR="$EXT_DIR/csharp-log-barriers"
PACK_NAME="tesserafin/csharp-log-barriers"
PACK_VERSION="0.0.1"
MODEL_FILE="$PACK_DIR/ext/log-value-extensions.model.yml"

# The one row this repository has reviewed and accepted. Any difference at all —
# a widened field, a second row, a changed kind — must fail this job.
EXPECTED_ROW='      - ["Tesserafin.Extensions", "LogValueExtensions", false, "ToSingleLogLine", "(System.String)", "", "ReturnValue", "log-injection", "manual"]'

# The library range the row was derived and reviewed against.
EXPECTED_TARGET_RANGE='^7.0.0'
EXPECTED_CSHARP_ALL_MAJOR='7'

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() {
    echo "MODEL PACK VERIFICATION FAILED: $*" >&2
    exit 1
}

echo "== CodeQL barrier model pack verification =="
"$CODEQL" version --format=terse

# ---------------------------------------------------------------------------
# 1. The pack resolves, exactly once, at the exact committed version.
# ---------------------------------------------------------------------------
"$CODEQL" resolve packs --additional-packs="$EXT_DIR" --format=json >"$WORK/packs.json" \
    || fail "'codeql resolve packs' exited non-zero"

FOUND_COUNT="$(jq --arg n "$PACK_NAME" \
    '[.steps[].scans[]?.found[$n]? // empty] | length' "$WORK/packs.json")"
[ "$FOUND_COUNT" = "1" ] \
    || fail "expected $PACK_NAME to resolve exactly once, resolved $FOUND_COUNT time(s)"

FOUND_VERSION="$(jq -r --arg n "$PACK_NAME" \
    'first(.steps[].scans[]?.found[$n]? // empty) | .version' "$WORK/packs.json")"
[ "$FOUND_VERSION" = "$PACK_VERSION" ] \
    || fail "resolved $PACK_NAME@$FOUND_VERSION, expected @$PACK_VERSION"

FOUND_PATH="$(jq -r --arg n "$PACK_NAME" \
    'first(.steps[].scans[]?.found[$n]? // empty) | .path' "$WORK/packs.json")"
case "$FOUND_PATH" in
    "$PACK_DIR"/*) : ;;
    *) fail "resolved $PACK_NAME from $FOUND_PATH, expected it inside $PACK_DIR" ;;
esac
echo "ok   pack resolves: $PACK_NAME@$FOUND_VERSION at $FOUND_PATH"

# ---------------------------------------------------------------------------
# 2. The pack targets the csharp-all major version this row was derived against,
#    and the CLI actually bundles that major. A mismatch is the silent failure
#    mode described above. The exact patch is deliberately not pinned: the action
#    pin is a floor and hosted runners resolve a newer CLI from the tool cache.
# ---------------------------------------------------------------------------
CSHARP_ALL_VERSION="$(jq -r \
    'first(.steps[].scans[]?.found["codeql/csharp-all"]? // empty) | .version' "$WORK/packs.json")"
if [ -z "$CSHARP_ALL_VERSION" ] || [ "$CSHARP_ALL_VERSION" = "null" ]; then
    fail "could not determine the bundled codeql/csharp-all version"
fi

TARGET_RANGE="$(sed -n 's/^  codeql\/csharp-all: *//p' "$PACK_DIR/codeql-pack.yml")"
[ "$TARGET_RANGE" = "$EXPECTED_TARGET_RANGE" ] \
    || fail "pack targets codeql/csharp-all '$TARGET_RANGE', expected '$EXPECTED_TARGET_RANGE'.
         Widening this range lets the model apply to a library version nobody
         re-derived it against."
case "$CSHARP_ALL_VERSION" in
    "$EXPECTED_CSHARP_ALL_MAJOR".*) : ;;
    *) fail "the CLI bundles codeql/csharp-all $CSHARP_ALL_VERSION, outside the
         reviewed major $EXPECTED_CSHARP_ALL_MAJOR.x. Re-read LogForgingQuery.qll and
         ExternalFlowExtensions.qll at the new version, re-derive the signature from a
         fresh database, then update this script and
         docs/codeql-log-barrier-model.md." ;;
esac
echo "ok   pack targets $TARGET_RANGE, CLI bundles codeql/csharp-all $CSHARP_ALL_VERSION"

# ---------------------------------------------------------------------------
# 3. The data extension resolves to exactly one row, of exactly one predicate,
#    from exactly the committed file — asked of the CLI, not of the YAML.
# ---------------------------------------------------------------------------
QUERY_PACK="$("$CODEQL" resolve qlpacks --format=json \
    | jq -r '.["codeql/csharp-queries"][0] // empty')"
[ -n "$QUERY_PACK" ] || fail "could not locate the codeql/csharp-queries pack root"

"$CODEQL" resolve extensions-by-pack \
    --additional-packs="$EXT_DIR" \
    --model-packs="$PACK_NAME@$PACK_VERSION" \
    -- "$QUERY_PACK" >"$WORK/extensions.json" 2>"$WORK/extensions.err" \
    || { cat "$WORK/extensions.err" >&2; fail "'codeql resolve extensions-by-pack' exited non-zero"; }

if grep -q "is unused" "$WORK/extensions.err"; then
    grep "is unused" "$WORK/extensions.err" >&2
    fail "the CLI reports the extension pack as unused; the barrier is NOT applied"
fi

LOCAL_ENTRIES="$(jq --arg d "$EXT_DIR/" \
    '[.data[][] | select(.file | startswith($d))]' "$WORK/extensions.json")"
LOCAL_COUNT="$(jq 'length' <<<"$LOCAL_ENTRIES")"
[ "$LOCAL_COUNT" = "1" ] \
    || fail "expected exactly 1 resolved data extension from $EXT_DIR, got $LOCAL_COUNT"

RESOLVED_FILE="$(jq -r '.[0].file' <<<"$LOCAL_ENTRIES")"
RESOLVED_PREDICATE="$(jq -r '.[0].predicate' <<<"$LOCAL_ENTRIES")"
RESOLVED_ROWS="$(jq -r '.[0].rowCount' <<<"$LOCAL_ENTRIES")"
[ "$RESOLVED_FILE" = "$MODEL_FILE" ] \
    || fail "resolved extension came from $RESOLVED_FILE, expected $MODEL_FILE"
[ "$RESOLVED_PREDICATE" = "barrierModel" ] \
    || fail "resolved extensible predicate is '$RESOLVED_PREDICATE', expected 'barrierModel'"
[ "$RESOLVED_ROWS" = "1" ] \
    || fail "resolved $RESOLVED_ROWS rows, expected exactly 1"
echo "ok   CLI resolves exactly 1 barrierModel row from the committed model file"

# ---------------------------------------------------------------------------
# 4. That single row is byte-for-byte the tuple this repository reviewed, and
#    carries no wildcard and no subtype matching.
# ---------------------------------------------------------------------------
ACTUAL_ROWS="$(grep -c '^      - \[' "$MODEL_FILE" || true)"
[ "$ACTUAL_ROWS" = "1" ] \
    || fail "$MODEL_FILE contains $ACTUAL_ROWS data rows, expected exactly 1"

ACTUAL_ROW="$(grep '^      - \[' "$MODEL_FILE")"
[ "$ACTUAL_ROW" = "$EXPECTED_ROW" ] || fail "the committed model row changed.
         expected: $EXPECTED_ROW
         found:    $ACTUAL_ROW
         Widening this tuple widens the barrier. Update this script deliberately,
         with the hosted negative controls re-run, or not at all."

case "$ACTUAL_ROW" in
    *'*'*) fail "the model row contains a wildcard" ;;
esac
case "$ACTUAL_ROW" in
    *', true, '*) fail "the model row enables subtype matching" ;;
esac

EXTENSIBLE_COUNT="$(grep -c 'extensible:' "$MODEL_FILE" || true)"
[ "$EXTENSIBLE_COUNT" = "1" ] \
    || fail "$MODEL_FILE declares $EXTENSIBLE_COUNT extensible predicates, expected exactly 1"
grep -q 'extensible: barrierModel' "$MODEL_FILE" \
    || fail "$MODEL_FILE does not declare 'extensible: barrierModel'"
grep -q 'pack: codeql/csharp-all' "$MODEL_FILE" \
    || fail "$MODEL_FILE does not add to codeql/csharp-all"

MODEL_FILE_COUNT="$(find "$PACK_DIR/ext" -name '*.model.yml' -type f | wc -l)"
[ "$MODEL_FILE_COUNT" = "1" ] \
    || fail "$PACK_DIR/ext contains $MODEL_FILE_COUNT model files, expected exactly 1"
echo "ok   the single row is exactly the reviewed tuple, no wildcard, no subtypes"

# ---------------------------------------------------------------------------
# 5. The pack is ACTIVE in this analysis: `codeql database init` recorded it in
#    the database's own analysis configuration and materialised its rows.
# ---------------------------------------------------------------------------
ANALYSIS_CONFIG="$SNAPSHOT/analysisConfig.json"
[ -f "$ANALYSIS_CONFIG" ] \
    || fail "no analysis configuration at $ANALYSIS_CONFIG; the pack was not discovered by init"

ACTIVE_PACKS="$(jq -c '.extensionPacks // []' "$ANALYSIS_CONFIG")"
[ "$ACTIVE_PACKS" = "[\"$PACK_NAME@$PACK_VERSION\"]" ] \
    || fail "the analysis lists extension packs $ACTIVE_PACKS, expected [\"$PACK_NAME@$PACK_VERSION\"]"

MATERIALISED="$SNAPSHOT/extension-pack/extensions/csharp-log-barriers/ext/log-value-extensions.model.yml"
[ -f "$MATERIALISED" ] \
    || fail "init did not materialise the model file into the analysis's extension pack"
diff -u "$MODEL_FILE" "$MATERIALISED" >/dev/null \
    || fail "the model file materialised into the database differs from the committed one"
echo "ok   the pack is active in this analysis: $ACTIVE_PACKS"

echo "== CodeQL barrier model pack verification passed =="
