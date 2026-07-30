#!/usr/bin/env bash
# Fail-closed ABI comparison of the protected Tesserafin assemblies (#94).
#
# Usage:
#   ./ci/abi-compat.sh <base-directory> <head-directory> [manifest]
#
# Compares the PUBLIC API of every assembly named in the manifest (default
# ci/abi-assemblies.txt) between two staged build outputs, using the pinned
# Microsoft.DotNet.ApiCompat.Tool from .config/dotnet-tools.json.
#
# Exit status
#   0  every protected assembly is present and compatible (or is a declared
#      newly introduced assembly, see ci/abi-new-assemblies.txt)
#   1  a breaking change, a missing assembly, a malformed manifest, a tool
#      failure, or any other outcome that does not PROVE compatibility
#
# There is no fail-open path. The predecessor of this script ran ApiCompat
# with `|| true` and then compared its stdout against one exact English
# sentence, so a missing file, a crashed tool and a genuine breaking change
# all became report text in a step that exited 0. Compatibility is proven
# here by ApiCompat's exit status, which is 0 only when it found no breaking
# changes (verified empirically against fixtures; see ci/tests/abi-compat.test.sh).
#
# Environment
#   ABI_NEW_ASSEMBLIES_MANIFEST  path to the newly-introduced-assembly list
#                                (default ci/abi-new-assemblies.txt)
#   ABI_EXPECTED_ASSEMBLY_COUNT  override the protected-assembly count assertion
#   ABI_REPORT_FILE              also write the Markdown report to this path
#   ABI_APICOMPAT_CMD            TEST ONLY — replaces the ApiCompat invocation.
#                                Never set by the workflow.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# shellcheck source-path=SCRIPTDIR
# shellcheck source=lib/abi-manifest.sh
source "$REPO_ROOT/ci/lib/abi-manifest.sh"

# The protected-assembly count deliberately lives HERE and not in the
# manifest. Deleting a manifest line to dodge a comparison then requires a
# second, visible edit in this file, so a shrinking ABI scope cannot pass as
# a one-line data change. Keep in sync with ci/abi-assemblies.txt.
ABI_EXPECTED_ASSEMBLY_COUNT_DEFAULT=8

# ApiCompat emits its CP#### diagnostic codes unlocalised, which is why the
# breaking/infrastructure classification below keys on them and never on
# prose. The prose itself follows the ambient locale, so the retained report
# would otherwise differ between a hosted runner and a French workstation.
# The tool resolves its resources from the process culture, not from
# DOTNET_CLI_UI_LANGUAGE alone — both are pinned.
export DOTNET_CLI_UI_LANGUAGE=en
export LC_ALL=C

usage() {
    echo "usage: $0 <base-directory> <head-directory> [manifest]" >&2
}

die() {
    echo "ABI: $*" >&2
    exit 1
}

if [ "$#" -lt 2 ] || [ "$#" -gt 3 ]; then
    usage
    exit 1
fi

BASE_DIR="$1"
HEAD_DIR="$2"
MANIFEST="${3:-$REPO_ROOT/ci/abi-assemblies.txt}"
NEW_MANIFEST="${ABI_NEW_ASSEMBLIES_MANIFEST:-$REPO_ROOT/ci/abi-new-assemblies.txt}"
EXPECTED_COUNT="${ABI_EXPECTED_ASSEMBLY_COUNT:-$ABI_EXPECTED_ASSEMBLY_COUNT_DEFAULT}"

[ -d "$BASE_DIR" ] || die "base directory not found: $BASE_DIR"
[ -d "$HEAD_DIR" ] || die "head directory not found: $HEAD_DIR"

if [[ ! "$EXPECTED_COUNT" =~ ^[0-9]+$ ]] || [ "$EXPECTED_COUNT" -lt 1 ]; then
    die "expected assembly count must be a positive integer, got '$EXPECTED_COUNT'"
fi

mapfile -t ASSEMBLIES < <(abi_manifest_read "$MANIFEST") \
    || die "manifest rejected: $MANIFEST"

if [ "${#ASSEMBLIES[@]}" -ne "$EXPECTED_COUNT" ]; then
    die "manifest declares ${#ASSEMBLIES[@]} assemblies but $EXPECTED_COUNT are expected." \
        "Changing the protected ABI scope requires updating BOTH $MANIFEST and" \
        "ABI_EXPECTED_ASSEMBLY_COUNT_DEFAULT in $0."
fi

NEW_ASSEMBLIES=()
if [ -f "$NEW_MANIFEST" ]; then
    mapfile -t NEW_ASSEMBLIES < <(abi_manifest_read "$NEW_MANIFEST" --allow-empty) \
        || die "newly-introduced-assembly manifest rejected: $NEW_MANIFEST"
fi

is_declared_new() {
    local needle="$1" candidate
    for candidate in ${NEW_ASSEMBLIES[@]+"${NEW_ASSEMBLIES[@]}"}; do
        [ "$candidate" = "$needle" ] && return 0
    done
    return 1
}

is_protected() {
    local needle="$1" candidate
    for candidate in "${ASSEMBLIES[@]}"; do
        [ "$candidate" = "$needle" ] && return 0
    done
    return 1
}

# A newly-introduced entry that is not itself protected is a typo or a stale
# leftover, and silently ignoring it would weaken the base-side check.
for entry in ${NEW_ASSEMBLIES[@]+"${NEW_ASSEMBLIES[@]}"}; do
    is_protected "$entry" \
        || die "$NEW_MANIFEST lists '$entry', which is not in $MANIFEST"
done

run_apicompat() {
    if [ -n "${ABI_APICOMPAT_CMD:-}" ]; then
        # Word splitting is intended: this is a test-only command override.
        # shellcheck disable=SC2086
        $ABI_APICOMPAT_CMD "$@"
    else
        ( cd "$REPO_ROOT" && dotnet tool run apicompat "$@" )
    fi
}

REPORT="$(mktemp)"
trap 'rm -f "$REPORT"' EXIT

emit() { printf '%s\n' "$*" >> "$REPORT"; }

emit "## ABI compatibility"
emit ""
emit "Base: \`$BASE_DIR\`"
emit "Head: \`$HEAD_DIR\`"
emit "Manifest: \`${MANIFEST#"$REPO_ROOT/"}\` (${#ASSEMBLIES[@]} protected assemblies)"
emit ""

# Six mutually exclusive outcomes. `compatible` and `breaking` are the only
# two that mean ApiCompat actually rendered a verdict on a pair of files, and
# they are the only two the "actually compared" figure may count: a protected
# assembly that is missing is fatal, but it was never compared, and reporting
# it as compared would overstate what the run proved.
compatible=0
breaking=0
missing_head=0
missing_base=0
tool_failure=0
introduced=0
unresolved=0
details=""

for assembly in "${ASSEMBLIES[@]}"; do
    base_dll="$BASE_DIR/$assembly"
    head_dll="$HEAD_DIR/$assembly"

    # Head is unconditional: a protected assembly that the pull request no
    # longer produces IS a breaking change, and there is no exemption for it.
    if [ ! -f "$head_dll" ]; then
        missing_head=$((missing_head + 1))
        details+=$'\n'"### ❌ $assembly — missing from head (not compared)"$'\n\n'"Expected \`$head_dll\`. A protected assembly that head no longer produces is a breaking change."$'\n'
        continue
    fi

    if [ ! -f "$base_dll" ]; then
        if is_declared_new "$assembly"; then
            introduced=$((introduced + 1))
            details+=$'\n'"### ➕ $assembly — newly introduced (not compared)"$'\n\n'"Absent from base and declared in \`${NEW_MANIFEST#"$REPO_ROOT/"}\`; nothing to compare."$'\n'
            continue
        fi
        missing_base=$((missing_base + 1))
        details+=$'\n'"### ❌ $assembly — missing from base (not compared)"$'\n\n'"Expected \`$base_dll\`. Compatibility cannot be proven. If this assembly is genuinely new, declare it in \`${NEW_MANIFEST#"$REPO_ROOT/"}\` in the same pull request."$'\n'
        continue
    fi

    # Capture the status inside the `||` list: reading $? after an `if` whose
    # condition failed would yield the status of the `if` itself, not of
    # ApiCompat, and would silently report every failure as exit 0.
    rc=0
    output="$(run_apicompat --left "$base_dll" --right "$head_dll" 2>&1)" || rc=$?

    if [ "$rc" -eq 0 ]; then
        compatible=$((compatible + 1))
        # ApiCompat is invoked without --lref/--rref, so it resolves
        # references the same way the pre-rename workflow did: from the
        # runtime it is executing on. In that mode it reports nothing about
        # references. Providing an INCOMPLETE search directory instead makes
        # it drop the default resolution and complain about every BCL and
        # third-party assembly, which is why the staged artifact deliberately
        # carries only the protected assemblies and no search path is passed.
        # This tripwire exists so that a future change which does pass one
        # cannot quietly degrade the analysis behind a green verdict.
        if printf '%s' "$output" | grep -qF "Could not resolve reference"; then
            unresolved=$((unresolved + 1))
            details+=$'\n'"### ⚠ $assembly — compatible, but references were unresolved"$'\n\n'"ApiCompat reported unresolved references; the comparison is weaker than it looks."$'\n\n'"\`\`\`"$'\n'"$(printf '%s' "$output" | grep -F 'Could not resolve reference' | head -20)"$'\n'"\`\`\`"$'\n'
        else
            details+=$'\n'"### ✅ $assembly — compatible"$'\n'
        fi
        continue
    fi

    # ApiCompat exits non-zero both for a genuine breaking change and for its
    # own failures. The CP#### diagnostic codes are the reliable, unlocalised
    # marker of a real API verdict; anything else is infrastructure and is
    # just as fatal, only reported differently.
    if printf '%s' "$output" | grep -qE '^[[:space:]]*CP[0-9]{4}:'; then
        breaking=$((breaking + 1))
        details+=$'\n'"### ❌ $assembly — breaking change (exit $rc)"$'\n\n'"\`\`\`"$'\n'"$output"$'\n'"\`\`\`"$'\n'
    else
        tool_failure=$((tool_failure + 1))
        details+=$'\n'"### ❌ $assembly — ApiCompat failed (exit $rc)"$'\n\n'"Compatibility could not be determined."$'\n\n'"\`\`\`"$'\n'"$output"$'\n'"\`\`\`"$'\n'
    fi
done

accounted=$((compatible + breaking + missing_head + missing_base + tool_failure + introduced))

if [ "$accounted" -ne "${#ASSEMBLIES[@]}" ]; then
    die "internal error: accounted for $accounted of ${#ASSEMBLIES[@]} assemblies"
fi

# Only a pair of files that ApiCompat actually judged counts as compared.
compared=$((compatible + breaking))
zero_compared=0
if [ "$compared" -eq 0 ]; then
    zero_compared=1
    details+=$'\n'"### ❌ zero assemblies compared"$'\n\n'"No protected assembly reached an ApiCompat verdict, so nothing was proven."$'\n'
fi

indeterminate=$((missing_base + tool_failure + zero_compared))

emit "| outcome | assemblies |"
emit "| --- | ---: |"
emit "| compatible | $compatible |"
emit "| breaking change | $breaking |"
emit "| missing from head (breaking, not compared) | $missing_head |"
emit "| missing from base (indeterminate, not compared) | $missing_base |"
emit "| ApiCompat failure (indeterminate) | $tool_failure |"
emit "| newly introduced (not compared) | $introduced |"
emit ""
if [ "$unresolved" -ne 0 ]; then
    emit "⚠ $unresolved of the compatible verdicts were produced with unresolved references and are weaker than they look."
    emit ""
fi
emit "**$compared of ${#ASSEMBLIES[@]} protected assemblies reached an ApiCompat verdict.**"
emit ""

if [ "$breaking" -eq 0 ] && [ "$missing_head" -eq 0 ] && [ "$indeterminate" -eq 0 ]; then
    emit "**Verdict: COMPATIBLE.**"
else
    emit "**Verdict: FAILED** — $((breaking + missing_head)) breaking, $indeterminate indeterminate."
fi

if [ -n "$details" ]; then
    emit "$details"
fi

cat "$REPORT"
if [ -n "${ABI_REPORT_FILE:-}" ]; then
    cat "$REPORT" >> "$ABI_REPORT_FILE"
fi

if [ "$breaking" -ne 0 ] || [ "$missing_head" -ne 0 ] || [ "$indeterminate" -ne 0 ]; then
    exit 1
fi

exit 0
