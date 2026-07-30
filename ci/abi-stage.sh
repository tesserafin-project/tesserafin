#!/usr/bin/env bash
# Stage exactly the protected ABI assemblies out of a build output (#94).
#
# Usage:
#   ./ci/abi-stage.sh <build-directory> <stage-directory> <head|base> [manifest]
#
# The hosted workflow builds the whole server (~108 DLLs, mostly third-party)
# and uploads only what this script stages: the assemblies named in
# ci/abi-assemblies.txt. Staging fails BEFORE the upload if a protected
# assembly is missing, so a truncated artifact can never reach the comparison
# job and be mistaken for "nothing to compare".
#
# `head` requires every protected assembly. `base` additionally tolerates the
# assemblies declared in ci/abi-new-assemblies.txt, which by definition do not
# exist yet in the merge base; ci/abi-compat.sh re-checks that exemption.
#
# Exit status: 0 when the stage directory holds exactly the expected set,
# 1 otherwise. There is no partial-success path.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# shellcheck source-path=SCRIPTDIR
# shellcheck source=lib/abi-manifest.sh
source "$REPO_ROOT/ci/lib/abi-manifest.sh"

die() {
    echo "ABI stage: $*" >&2
    exit 1
}

if [ "$#" -lt 3 ] || [ "$#" -gt 4 ]; then
    echo "usage: $0 <build-directory> <stage-directory> <head|base> [manifest]" >&2
    exit 1
fi

BUILD_DIR="$1"
STAGE_DIR="$2"
SIDE="$3"
MANIFEST="${4:-$REPO_ROOT/ci/abi-assemblies.txt}"
NEW_MANIFEST="${ABI_NEW_ASSEMBLIES_MANIFEST:-$REPO_ROOT/ci/abi-new-assemblies.txt}"

case "$SIDE" in
    head|base) ;;
    *) die "side must be 'head' or 'base', got '$SIDE'" ;;
esac

[ -d "$BUILD_DIR" ] || die "build directory not found: $BUILD_DIR"

mapfile -t ASSEMBLIES < <(abi_manifest_read "$MANIFEST") \
    || die "manifest rejected: $MANIFEST"

NEW_ASSEMBLIES=()
if [ "$SIDE" = "base" ] && [ -f "$NEW_MANIFEST" ]; then
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

mkdir -p "$STAGE_DIR"

staged=0
skipped=0
missing=()

for assembly in "${ASSEMBLIES[@]}"; do
    if [ -f "$BUILD_DIR/$assembly" ]; then
        cp "$BUILD_DIR/$assembly" "$STAGE_DIR/$assembly"
        staged=$((staged + 1))
        continue
    fi

    if [ "$SIDE" = "base" ] && is_declared_new "$assembly"; then
        echo "ABI stage: $assembly declared newly introduced; absent from base as expected"
        skipped=$((skipped + 1))
        continue
    fi

    missing+=("$assembly")
done

if [ "${#missing[@]}" -ne 0 ]; then
    echo "ABI stage: $SIDE build is missing ${#missing[@]} protected assemblies:" >&2
    printf '  %s\n' "${missing[@]}" >&2
    die "refusing to upload an incomplete ABI artifact"
fi

echo "ABI stage ($SIDE): staged $staged of ${#ASSEMBLIES[@]} protected assemblies into $STAGE_DIR (skipped $skipped as newly introduced)"
