#!/usr/bin/env bash
# SC2016: the single-quoted printf formats contain Markdown backticks, not
#         shell expansions.
# shellcheck disable=SC2016
#
# Fail-closed NuGet dependency vulnerability gate — repository-local slice of
# #95 (C2).
#
# WHAT THIS IS NOT. It is not a vulnerability database and it is not a
# scanner. It never decides whether a package is vulnerable; NuGet's own
# `dotnet list package --vulnerable` asks the GitHub Advisory Database through
# api.nuget.org and answers that. No advisory identifier, no severity and no
# affected range is ever hard-coded here — if this file had to be edited every
# time an advisory was published the gate would be reporting yesterday's world.
#
# WHAT THIS IS. It is the policy layer over that scanner's machine-readable
# report, and the reason the policy layer exists is that the scanner's exit
# status is useless: on the tree this gate was written against,
# `dotnet list package --vulnerable` exited **0** while reporting eight High
# findings. A workflow that trusted `$?` would have been green with a known
# High vulnerability in the graph. So the verdict is computed from the JSON
# document's structure, never from an exit status and never from grepping
# console prose.
#
# The same reasoning drives the third verdict. "The scanner said nothing" and
# "the scanner could not reach the advisory database" produce the *same*
# well-formed, empty report — `--vulnerable` only ever emits packages that have
# advisories, so a clean repository and an offline runner are textually
# identical. That is why a second, independent inventory report is mandatory:
# it is the only evidence that a package graph was resolved at all, and why
# NuGet's NU1900-family warnings ("error occurred while getting package
# vulnerability data") are promoted to a hard INDETERMINATE.
#
#   0  CLEAN            valid, complete reports; no unresolved High/Critical
#   1  POLICY VIOLATION at least one unresolved High/Critical finding, or a
#                       waiver that does not hold up (expired, mismatched,
#                       stale)
#   2  INDETERMINATE    anything that means the question was not answered:
#                       scanner failure, unreachable feed, invalid arguments,
#                       missing/empty/malformed report, unexpected schema
#                       version, empty project graph, zero packages inspected,
#                       unknown severity, structurally invalid waiver file
#
# Low and Moderate findings are reported in full and never fail the gate.
#
# Usage:
#   ci/dependency-audit.sh --scan [--work DIR] [--waivers FILE] [--summary FILE]
#   ci/dependency-audit.sh --vulnerable-report FILE --inventory-report FILE \
#                          [--scan-log FILE] [--waivers FILE] [--summary FILE] \
#                          [--expected-projects N]
#
# `--scan` runs the two `dotnet list package` commands itself and then
# evaluates them; the split form evaluates reports produced earlier (that is
# what the deterministic controls drive, and what a workflow uses when the scan
# and the verdict are separate steps).
#
# Requires jq. `--scan` additionally requires the .NET SDK pinned by
# global.json.

set -uo pipefail

readonly EXIT_CLEAN=0
readonly EXIT_POLICY=1
readonly EXIT_INDETERMINATE=2

# The `--output-version` this evaluator understands. A future NuGet that bumps
# it changes field meanings underneath us, so an unexpected value is refused
# rather than parsed hopefully.
readonly SUPPORTED_REPORT_VERSION=1

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly REPO_ROOT
readonly SOLUTION="$REPO_ROOT/Tesserafin.sln"

MODE=""
VULNERABLE_REPORT=""
INVENTORY_REPORT=""
SCAN_LOG=""
WAIVERS=""
SUMMARY=""
WORK=""
EXPECTED_PROJECTS=""

die_indeterminate() {
    printf 'dependency-audit: INDETERMINATE: %s\n' "$*" >&2
    exit "$EXIT_INDETERMINATE"
}

usage() {
    # The header comment block is the documentation; print it rather than
    # keeping a second copy that can drift away from it.
    awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "${BASH_SOURCE[0]}"
}

# ── Arguments ─────────────────────────────────────────────────────────────
#
# Every unknown flag, missing value and mode conflict is INDETERMINATE, not a
# usage exit: a workflow that mistypes a flag must go red, not silently pass.

while [ $# -gt 0 ]; do
    case "$1" in
        --scan)
            [ -n "$MODE" ] && die_indeterminate "--scan cannot be combined with --vulnerable-report"
            MODE="scan"
            shift
            ;;
        --vulnerable-report)
            [ "$MODE" = "scan" ] && die_indeterminate "--vulnerable-report cannot be combined with --scan"
            [ $# -ge 2 ] || die_indeterminate "--vulnerable-report requires a value"
            MODE="evaluate"
            VULNERABLE_REPORT="$2"
            shift 2
            ;;
        --inventory-report)
            [ $# -ge 2 ] || die_indeterminate "--inventory-report requires a value"
            INVENTORY_REPORT="$2"
            shift 2
            ;;
        --scan-log)
            [ $# -ge 2 ] || die_indeterminate "--scan-log requires a value"
            SCAN_LOG="$2"
            shift 2
            ;;
        --waivers)
            [ $# -ge 2 ] || die_indeterminate "--waivers requires a value"
            WAIVERS="$2"
            shift 2
            ;;
        --summary)
            [ $# -ge 2 ] || die_indeterminate "--summary requires a value"
            SUMMARY="$2"
            shift 2
            ;;
        --work)
            [ $# -ge 2 ] || die_indeterminate "--work requires a value"
            WORK="$2"
            shift 2
            ;;
        --expected-projects)
            [ $# -ge 2 ] || die_indeterminate "--expected-projects requires a value"
            EXPECTED_PROJECTS="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit "$EXIT_CLEAN"
            ;;
        *)
            die_indeterminate "unknown argument: $1"
            ;;
    esac
done

command -v jq >/dev/null 2>&1 || die_indeterminate "jq is required"

case "$MODE" in
    scan)
        [ -n "$VULNERABLE_REPORT" ] && die_indeterminate "--vulnerable-report is not valid with --scan"
        [ -n "$INVENTORY_REPORT" ] && die_indeterminate "--inventory-report is not valid with --scan"
        [ -n "$SCAN_LOG" ] && die_indeterminate "--scan-log is not valid with --scan"
        ;;
    evaluate)
        [ -n "$INVENTORY_REPORT" ] || die_indeterminate "--inventory-report is required alongside --vulnerable-report"
        ;;
    *)
        die_indeterminate "one of --scan or --vulnerable-report is required"
        ;;
esac

if [ -n "$EXPECTED_PROJECTS" ]; then
    case "$EXPECTED_PROJECTS" in
        ''|*[!0-9]*) die_indeterminate "--expected-projects must be a non-negative integer, got: $EXPECTED_PROJECTS" ;;
    esac
fi

# ── Scan ──────────────────────────────────────────────────────────────────

run_scan() {
    command -v dotnet >/dev/null 2>&1 || die_indeterminate "the .NET SDK is required by --scan"
    [ -f "$SOLUTION" ] || die_indeterminate "missing solution: $SOLUTION"

    if [ -z "$WORK" ]; then
        WORK="$(mktemp -d "${TMPDIR:-/tmp}/tesserafin-dependency-audit.XXXXXX")" \
            || die_indeterminate "could not create a work directory"
    fi
    mkdir -p "$WORK" || die_indeterminate "could not create work directory: $WORK"

    VULNERABLE_REPORT="$WORK/nuget-vulnerable.json"
    INVENTORY_REPORT="$WORK/nuget-inventory.json"
    SCAN_LOG="$WORK/nuget-scan.log"

    # en-US so the NU1900-family detection below matches a stable string, and
    # so a localised runner produces the same evidence as a localised laptop.
    export DOTNET_CLI_UI_LANGUAGE=en-US
    export DOTNET_CLI_TELEMETRY_OPTOUT=1
    export DOTNET_NOLOGO=1

    : > "$SCAN_LOG"

    printf 'dependency-audit: restoring %s\n' "${SOLUTION#"$REPO_ROOT"/}"
    if ! dotnet restore "$SOLUTION" >> "$SCAN_LOG" 2>&1; then
        tail -n 40 "$SCAN_LOG" >&2
        die_indeterminate "restore failed; the package graph was never resolved"
    fi

    # Two commands, deliberately. `--vulnerable` emits ONLY packages carrying
    # advisories, so on a clean tree it cannot say how much it looked at; the
    # inventory run is the proof that a non-empty graph was inspected.
    printf 'dependency-audit: auditing (vulnerable)\n'
    dotnet list "$SOLUTION" package --vulnerable --include-transitive \
        --format json --output-version "$SUPPORTED_REPORT_VERSION" \
        > "$VULNERABLE_REPORT" 2>> "$SCAN_LOG"
    local vulnerable_status=$?

    printf 'dependency-audit: inventorying (all packages)\n'
    dotnet list "$SOLUTION" package --include-transitive \
        --format json --output-version "$SUPPORTED_REPORT_VERSION" \
        > "$INVENTORY_REPORT" 2>> "$SCAN_LOG"
    local inventory_status=$?

    # The status is recorded, never trusted as the verdict: the reference run
    # for this gate exited 0 with eight High findings.
    printf 'dependency-audit: scanner exit status vulnerable=%d inventory=%d (recorded, not the verdict)\n' \
        "$vulnerable_status" "$inventory_status"

    if [ "$inventory_status" -ne 0 ]; then
        tail -n 40 "$SCAN_LOG" >&2
        die_indeterminate "the inventory command failed (exit $inventory_status)"
    fi
    if [ "$vulnerable_status" -ne 0 ]; then
        tail -n 40 "$SCAN_LOG" >&2
        die_indeterminate "the vulnerability command failed (exit $vulnerable_status)"
    fi
}

# ── Report validation ─────────────────────────────────────────────────────

# Sets REPORT_PROJECTS rather than echoing it: `$(...)` would run
# `die_indeterminate` in a subshell, where its `exit 2` reaches no further than
# the substitution and the caller sails on with an empty count.
REPORT_PROJECTS=0

require_report() {
    local label="$1" path="$2"

    [ -n "$path" ] || die_indeterminate "no $label report was given"
    # An exact file, never a directory or a dangling symlink: a report that is
    # not a readable regular file cannot be evidence of anything.
    [ -e "$path" ] || die_indeterminate "missing $label report: $path"
    [ -f "$path" ] || die_indeterminate "$label report is not a regular file: $path"
    [ -r "$path" ] || die_indeterminate "$label report is not readable: $path"
    [ -s "$path" ] || die_indeterminate "$label report is empty: $path"

    jq -e . "$path" >/dev/null 2>&1 \
        || die_indeterminate "$label report is not valid JSON: $path"

    local version
    version="$(jq -r 'if has("version") then (.version|tostring) else "<absent>" end' "$path")"
    [ "$version" = "$SUPPORTED_REPORT_VERSION" ] \
        || die_indeterminate "$label report schema version is $version, expected $SUPPORTED_REPORT_VERSION: $path"

    jq -e 'has("projects") and (.projects | type == "array")' "$path" >/dev/null 2>&1 \
        || die_indeterminate "$label report has no projects array: $path"

    REPORT_PROJECTS="$(jq -r '.projects | length' "$path")"
    [ "$REPORT_PROJECTS" -gt 0 ] \
        || die_indeterminate "$label report inspected zero projects: $path"
}

# NuGet reports an unreachable or failing advisory feed as an NU1900-family
# warning and still exits 0 with a well-formed, empty `--vulnerable` report.
# Left alone, that is indistinguishable from a clean tree — so it is fatal.
check_scan_log() {
    [ -n "$SCAN_LOG" ] || return 0
    [ -e "$SCAN_LOG" ] || die_indeterminate "missing scan log: $SCAN_LOG"
    [ -f "$SCAN_LOG" ] || die_indeterminate "scan log is not a regular file: $SCAN_LOG"

    if grep -qE 'NU190[0-9]' "$SCAN_LOG"; then
        grep -E 'NU190[0-9]' "$SCAN_LOG" | head -n 5 >&2
        die_indeterminate "the scanner could not read package vulnerability data (NU1900-family warning)"
    fi
}

# ── Waivers ───────────────────────────────────────────────────────────────
#
# The waiver file is an integrity surface: a malformed one means the policy
# cannot be evaluated (2), while a waiver that simply does not hold up —
# expired, aimed at the wrong package, severity or advisory, or covering
# nothing at all — means the policy was violated (1). Nothing here can be
# implied, defaulted or inferred; every field is mandatory and exact.

readonly WAIVER_FIELDS='ecosystem package advisory severity justification issue owner created expires'

validate_waivers() {
    [ -n "$WAIVERS" ] || return 0

    [ -e "$WAIVERS" ] || die_indeterminate "missing waiver file: $WAIVERS"
    [ -f "$WAIVERS" ] || die_indeterminate "waiver file is not a regular file: $WAIVERS"
    [ -r "$WAIVERS" ] || die_indeterminate "waiver file is not readable: $WAIVERS"
    [ -s "$WAIVERS" ] || die_indeterminate "waiver file is empty: $WAIVERS"

    jq -e . "$WAIVERS" >/dev/null 2>&1 \
        || die_indeterminate "waiver file is not valid JSON: $WAIVERS"

    local version
    version="$(jq -r 'if has("version") then (.version|tostring) else "<absent>" end' "$WAIVERS")"
    [ "$version" = "1" ] \
        || die_indeterminate "waiver file schema version is $version, expected 1: $WAIVERS"

    jq -e 'has("waivers") and (.waivers | type == "array")' "$WAIVERS" >/dev/null 2>&1 \
        || die_indeterminate "waiver file has no waivers array: $WAIVERS"

    local count index field value
    count="$(jq -r '.waivers | length' "$WAIVERS")"

    index=0
    while [ "$index" -lt "$count" ]; do
        for field in $WAIVER_FIELDS; do
            value="$(jq -r --argjson i "$index" --arg f "$field" \
                '.waivers[$i] | if has($f) then (.[$f]|tostring) else "" end' "$WAIVERS")"
            [ -n "$value" ] \
                || die_indeterminate "waiver #$index is missing a non-empty \"$field\""
        done

        value="$(jq -r --argjson i "$index" '.waivers[$i].package' "$WAIVERS")"
        case "$value" in
            *'*'*|*'?'*) die_indeterminate "waiver #$index uses a wildcard package pattern: $value" ;;
        esac

        value="$(jq -r --argjson i "$index" '.waivers[$i].ecosystem' "$WAIVERS")"
        [ "$value" = "nuget" ] \
            || die_indeterminate "waiver #$index targets ecosystem \"$value\"; this gate only evaluates \"nuget\""

        value="$(jq -r --argjson i "$index" '.waivers[$i].advisory' "$WAIVERS")"
        case "$value" in
            GHSA-*) ;;
            *) die_indeterminate "waiver #$index advisory must be a GHSA identifier, got: $value" ;;
        esac

        # A tracking issue is checked for shape only. Resolving it would make
        # the gate depend on GitHub's availability and on a token, which would
        # turn a network blip into a policy verdict.
        value="$(jq -r --argjson i "$index" '.waivers[$i].issue' "$WAIVERS")"
        case "$value" in
            https://github.com/*/*/issues/[0-9]*) ;;
            *) die_indeterminate "waiver #$index issue must be a GitHub issue URL, got: $value" ;;
        esac

        for field in created expires; do
            value="$(jq -r --argjson i "$index" --arg f "$field" '.waivers[$i][$f]' "$WAIVERS")"
            date -u -d "$value" +%Y-%m-%d >/dev/null 2>&1 \
                || die_indeterminate "waiver #$index has a malformed $field date: $value"
            printf '%s' "$value" | grep -qE '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' \
                || die_indeterminate "waiver #$index $field must be YYYY-MM-DD, got: $value"
        done

        index=$((index + 1))
    done

    local duplicates
    duplicates="$(jq -r '[.waivers[] | "\(.ecosystem)|\(.package)|\(.advisory)"]
                         | group_by(.) | map(select(length > 1) | .[0]) | .[]' "$WAIVERS")"
    [ -z "$duplicates" ] \
        || die_indeterminate "duplicate waivers for: $(printf '%s' "$duplicates" | tr '\n' ' ')"
}

# ── Evaluation ────────────────────────────────────────────────────────────

main() {
    [ "$MODE" = "scan" ] && run_scan

    check_scan_log

    local vulnerable_projects inventory_projects
    require_report "vulnerability" "$VULNERABLE_REPORT"
    vulnerable_projects="$REPORT_PROJECTS"
    require_report "inventory" "$INVENTORY_REPORT"
    inventory_projects="$REPORT_PROJECTS"

    if [ -n "$EXPECTED_PROJECTS" ] && [ "$inventory_projects" -ne "$EXPECTED_PROJECTS" ]; then
        die_indeterminate "inventory covers $inventory_projects projects, expected $EXPECTED_PROJECTS (truncated graph)"
    fi

    # Package counts come from the inventory report alone. This is the check
    # that separates "nothing is vulnerable" from "nothing was looked at".
    local direct_count transitive_count total_count
    direct_count="$(jq -r '[.projects[].frameworks // [] | .[].topLevelPackages // [] | .[].id] | unique | length' "$INVENTORY_REPORT")"
    transitive_count="$(jq -r '[.projects[].frameworks // [] | .[].transitivePackages // [] | .[].id] | unique | length' "$INVENTORY_REPORT")"
    total_count="$(jq -r '[.projects[].frameworks // [] | .[] | (.topLevelPackages // []), (.transitivePackages // []) | .[].id] | unique | length' "$INVENTORY_REPORT")"

    [ "$total_count" -gt 0 ] \
        || die_indeterminate "the inventory report lists zero packages; the graph was not resolved"

    # A framework-less inventory project means the graph for that project was
    # never resolved. (The *vulnerable* report legitimately omits frameworks
    # for projects with no findings — only the inventory is held to this.)
    local unresolved
    unresolved="$(jq -r '[.projects[] | select((.frameworks // []) | length == 0) | .path] | length' "$INVENTORY_REPORT")"
    [ "$unresolved" -eq 0 ] \
        || die_indeterminate "$unresolved project(s) in the inventory resolved no frameworks (truncated graph)"

    validate_waivers

    # Flatten every finding to one tab-separated row. `kind` is derived from
    # which array the package came out of, which is how "direct" and
    # "transitive" are distinguished all the way to the summary.
    local findings
    findings="$(jq -r '
        .projects[] as $p
        | ($p.frameworks // [])[] as $f
        | ( ($f.topLevelPackages   // [] | map(. + {kind: "direct"}))
          + ($f.transitivePackages // [] | map(. + {kind: "transitive"})) )[] as $pkg
        | ($pkg.vulnerabilities // [])[] as $v
        | [ $pkg.kind,
            ($v.severity // "<absent>"),
            $pkg.id,
            ($pkg.resolvedVersion // "<absent>"),
            ($v.advisoryurl // "<absent>"),
            ($p.path | split("/") | last),
            ($f.framework // "<absent>")
          ] | @tsv' "$VULNERABLE_REPORT")" \
        || die_indeterminate "the vulnerability report could not be flattened; its structure is not the expected shape"

    # Severity is the field the verdict turns on, so an absent or unrecognised
    # one is never quietly bucketed as harmless.
    local kind severity package version advisory project framework
    local -a rows=()
    if [ -n "$findings" ]; then
        while IFS=$'\t' read -r kind severity package version advisory project framework; do
            case "$severity" in
                Low|Moderate|High|Critical) ;;
                '<absent>') die_indeterminate "a finding for $package has no severity" ;;
                *) die_indeterminate "a finding for $package has an unknown severity: $severity" ;;
            esac
            case "$kind" in
                direct|transitive) ;;
                *) die_indeterminate "a finding for $package has an unknown kind: $kind" ;;
            esac
            rows+=("$kind	$severity	$package	$version	$advisory	$project	$framework")
        done <<< "$findings"
    fi

    # ── Policy ────────────────────────────────────────────────────────────

    local today
    today="$(date -u +%Y-%m-%d)"

    local -a blocking=() waived=() informational=()
    local -A waiver_used=()
    local row w_index w_count w_package w_advisory w_severity w_expires

    w_count=0
    [ -n "$WAIVERS" ] && w_count="$(jq -r '.waivers | length' "$WAIVERS")"

    for row in "${rows[@]:-}"; do
        [ -n "$row" ] || continue
        IFS=$'\t' read -r kind severity package version advisory project framework <<< "$row"

        if [ "$severity" != "High" ] && [ "$severity" != "Critical" ]; then
            informational+=("$row")
            continue
        fi

        local matched=""
        w_index=0
        while [ "$w_index" -lt "$w_count" ]; do
            w_package="$(jq -r --argjson i "$w_index" '.waivers[$i].package' "$WAIVERS")"
            w_advisory="$(jq -r --argjson i "$w_index" '.waivers[$i].advisory' "$WAIVERS")"
            w_severity="$(jq -r --argjson i "$w_index" '.waivers[$i].severity' "$WAIVERS")"
            w_expires="$(jq -r --argjson i "$w_index" '.waivers[$i].expires' "$WAIVERS")"

            # All three must line up. A waiver written for a Moderate finding
            # does not silence the Critical that later lands on the same
            # package, and a waiver for advisory A does not cover advisory B.
            if [ "$w_package" = "$package" ] \
                && [ "$w_severity" = "$severity" ] \
                && [ "${advisory##*/}" = "$w_advisory" ]; then
                waiver_used["$w_index"]=1
                if [[ "$w_expires" < "$today" ]]; then
                    blocking+=("$row	EXPIRED WAIVER (expired $w_expires)")
                else
                    waived+=("$row	waived until $w_expires")
                fi
                matched=1
                break
            fi
            w_index=$((w_index + 1))
        done

        [ -n "$matched" ] || blocking+=("$row	unwaived")
    done

    # A waiver that matches nothing is a lie about the current graph — either
    # the finding was fixed and nobody removed the waiver, or it never applied.
    local -a stale=()
    w_index=0
    while [ "$w_index" -lt "$w_count" ]; do
        if [ -z "${waiver_used[$w_index]:-}" ]; then
            stale+=("$(jq -r --argjson i "$w_index" '"\(.waivers[$i].package) \(.waivers[$i].advisory)"' "$WAIVERS")")
        fi
        w_index=$((w_index + 1))
    done

    # ── Output ────────────────────────────────────────────────────────────

    local low moderate high critical
    low="$(printf '%s\n' "${rows[@]:-}" | grep -c '	Low	' || true)"
    moderate="$(printf '%s\n' "${rows[@]:-}" | grep -c '	Moderate	' || true)"
    high="$(printf '%s\n' "${rows[@]:-}" | grep -c '	High	' || true)"
    critical="$(printf '%s\n' "${rows[@]:-}" | grep -c '	Critical	' || true)"

    local verdict="$EXIT_CLEAN" label="CLEAN"
    if [ "${#blocking[@]}" -gt 0 ] || [ "${#stale[@]}" -gt 0 ]; then
        verdict="$EXIT_POLICY"
        label="POLICY VIOLATION"
    fi

    {
        printf '## NuGet dependency audit — %s\n\n' "$label"
        printf '| | |\n|---|---|\n'
        printf '| Projects inspected | %s (vulnerability report), %s (inventory) |\n' \
            "$vulnerable_projects" "$inventory_projects"
        printf '| Distinct packages inspected | %s (%s direct, %s transitive) |\n' \
            "$total_count" "$direct_count" "$transitive_count"
        printf '| Findings | %s Critical, %s High, %s Moderate, %s Low |\n' \
            "$critical" "$high" "$moderate" "$low"
        printf '| Blocking | %s |\n' "${#blocking[@]}"
        printf '| Waived | %s |\n' "${#waived[@]}"
        printf '| Stale waivers | %s |\n\n' "${#stale[@]}"

        if [ "${#blocking[@]}" -gt 0 ]; then
            printf '### Blocking (High/Critical, unresolved)\n\n'
            printf '| Severity | Package | Version | Kind | Project | Advisory | Note |\n'
            printf '|---|---|---|---|---|---|---|\n'
            for row in "${blocking[@]}"; do
                IFS=$'\t' read -r kind severity package version advisory project framework note <<< "$row"
                printf '| %s | `%s` | %s | %s | `%s` | %s | %s |\n' \
                    "$severity" "$package" "$version" "$kind" "$project" "$advisory" "$note"
            done
            printf '\n'
        fi

        if [ "${#waived[@]}" -gt 0 ]; then
            printf '### Waived\n\n'
            for row in "${waived[@]}"; do
                IFS=$'\t' read -r kind severity package version advisory project framework note <<< "$row"
                printf -- '- %s `%s` %s (%s, `%s`) %s — %s\n' \
                    "$severity" "$package" "$version" "$kind" "$project" "$advisory" "$note"
            done
            printf '\n'
        fi

        if [ "${#stale[@]}" -gt 0 ]; then
            printf '### Stale waivers (match no current finding)\n\n'
            for row in "${stale[@]}"; do printf -- '- %s\n' "$row"; done
            printf '\n'
        fi

        if [ "${#informational[@]}" -gt 0 ]; then
            printf '### Reported, not blocking (Low/Moderate)\n\n'
            for row in "${informational[@]}"; do
                IFS=$'\t' read -r kind severity package version advisory project framework <<< "$row"
                printf -- '- %s `%s` %s (%s, `%s`) %s\n' \
                    "$severity" "$package" "$version" "$kind" "$project" "$advisory"
            done
            printf '\n'
        fi
    } > "${SUMMARY:-/dev/stdout}"

    [ -n "$SUMMARY" ] && cat "$SUMMARY"

    printf '\ndependency-audit: %s (exit %d)\n' "$label" "$verdict"
    [ -n "$VULNERABLE_REPORT" ] && printf 'dependency-audit: vulnerability report retained at %s\n' "$VULNERABLE_REPORT"
    [ -n "$INVENTORY_REPORT" ] && printf 'dependency-audit: inventory report retained at %s\n' "$INVENTORY_REPORT"

    exit "$verdict"
}

main
