#!/usr/bin/env bash
# Shared, fail-closed parsing of the protected ABI manifests (#94).
#
# Sourced by ci/abi-compat.sh and ci/abi-stage.sh so that both sides of the
# hosted ABI workflow agree, byte for byte, on which assemblies are protected.
# Every rejection here is deliberate: a manifest this parser cannot fully
# validate must never degrade into "compare fewer assemblies".
#
# shellcheck shell=bash

# abi_manifest_read <manifest> [--allow-empty]
#
# Prints the validated entries, one per line, on stdout. Diagnostics go to
# stderr and the function returns non-zero. Rejects: a missing or unreadable
# file, an entry that is not a plain `<name>.dll` file name, duplicate
# entries, entries that are not sorted under LC_ALL=C, and — unless
# --allow-empty is given — an empty entry set.
abi_manifest_read() {
    local manifest="${1:-}"
    local allow_empty="${2:-}"

    if [ -z "$manifest" ]; then
        echo "abi_manifest_read: no manifest path given" >&2
        return 1
    fi

    if [ ! -f "$manifest" ]; then
        echo "abi_manifest_read: manifest not found: $manifest" >&2
        return 1
    fi

    if [ ! -r "$manifest" ]; then
        echo "abi_manifest_read: manifest not readable: $manifest" >&2
        return 1
    fi

    local -a entries=()
    local line entry lineno=0
    while IFS= read -r line || [ -n "$line" ]; do
        lineno=$((lineno + 1))
        # Strip comments, then surrounding whitespace.
        entry="${line%%#*}"
        entry="${entry#"${entry%%[![:space:]]*}"}"
        entry="${entry%"${entry##*[![:space:]]}"}"
        [ -n "$entry" ] || continue

        if [[ ! "$entry" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*\.dll$ ]]; then
            echo "abi_manifest_read: $manifest:$lineno: not a plain DLL file name: '$entry'" >&2
            return 1
        fi

        entries+=("$entry")
    done < "$manifest"

    if [ "${#entries[@]}" -eq 0 ] && [ "$allow_empty" != "--allow-empty" ]; then
        echo "abi_manifest_read: $manifest declares zero assemblies" >&2
        return 1
    fi

    if [ "${#entries[@]}" -gt 0 ]; then
        local duplicates
        duplicates="$(printf '%s\n' "${entries[@]}" | LC_ALL=C sort | LC_ALL=C uniq -d)"
        if [ -n "$duplicates" ]; then
            echo "abi_manifest_read: $manifest has duplicate entries:" >&2
            printf '%s\n' "$duplicates" >&2
            return 1
        fi

        if ! printf '%s\n' "${entries[@]}" | LC_ALL=C sort -c 2>/dev/null; then
            echo "abi_manifest_read: $manifest is not sorted (LC_ALL=C)" >&2
            return 1
        fi

        printf '%s\n' "${entries[@]}"
    fi

    return 0
}
