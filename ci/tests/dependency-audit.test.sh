#!/usr/bin/env bash
# Deterministic controls for the fail-closed NuGet dependency gate (#95, C2).
#
# The point of these controls is that a green "Dependency Audit" means
# something. `dotnet list package --vulnerable` exits 0 whether it found eight
# High advisories or none, and emits a well-formed, *empty* report both when the
# tree is clean and when it never reached the advisory database — so every way
# the gate could quietly pass has to be pinned down by a case that proves it
# does not.
#
# Each RED case asserts the exit status of `ci/dependency-audit.sh` itself, not
# of this script. This suite passing means the evaluator refused; it never means
# the evaluator was not consulted.
#
#   0  CLEAN   1  POLICY VIOLATION   2  INDETERMINATE
#
# Two families of fixture:
#
#   synthetic  hand-written reports, one property per case, small enough that
#              the expected verdict is obvious from reading the fixture.
#   real       the committed tree's own scan output, produced live by
#              `--scan` when the .NET SDK is available. This is the control
#              that matters most: it proves the evaluator agrees with reality
#              on the exact report shape NuGet actually emits, rather than on
#              the shape this file imagines. Skipped, loudly, without an SDK.
#
# No fixture is ever written inside the repository, and no case introduces a
# real vulnerable dependency.
#
# Usage:
#   ./ci/tests/dependency-audit.test.sh [--no-live]
#
# Requires jq. The live control additionally requires the .NET SDK and a
# reachable api.nuget.org.

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPO_ROOT
readonly AUDIT="$REPO_ROOT/ci/dependency-audit.sh"

RUN_LIVE=1
while [ $# -gt 0 ]; do
    case "$1" in
        --no-live) RUN_LIVE=0; shift ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 2; }
[ -x "$AUDIT" ] || { echo "missing or non-executable $AUDIT" >&2; exit 2; }

PASS=0
FAIL=0
SKIP=0

ok()   { echo "  PASS: $*"; PASS=$((PASS + 1)); }
bad()  { echo "  FAIL: $*" >&2; FAIL=$((FAIL + 1)); }
skip() { echo "  SKIP: $*"; SKIP=$((SKIP + 1)); }

WORK="$(mktemp -d "${TMPDIR:-/tmp}/tesserafin-dependency-audit-controls.XXXXXX")" \
    || { echo "could not create a work directory" >&2; exit 2; }
# shellcheck disable=SC2317  # runs from the EXIT trap
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

echo "NuGet dependency gate controls"
echo "  fixture tree: $WORK"
echo

# ── Fixture builders ──────────────────────────────────────────────────────

# vuln <severity> <advisory-url>
vuln() { jq -n --arg s "$1" --arg u "$2" '{severity: $s, advisoryurl: $u}'; }

# pkg <id> <version> [vulnerability-json...]
pkg() {
    local id="$1" version="$2"; shift 2
    local vulns='[]'
    if [ $# -gt 0 ]; then vulns="$(printf '%s\n' "$@" | jq -s '.')"; fi
    jq -n --arg i "$id" --arg v "$version" --argjson vs "$vulns" \
        '{id: $i, resolvedVersion: $v} + (if ($vs|length) > 0 then {vulnerabilities: $vs} else {} end)'
}

# report <path> <version> <projects-json>
report() {
    jq -n --argjson v "$2" --argjson p "$3" \
        '{version: $v, parameters: "--vulnerable --include-transitive",
          sources: ["https://api.nuget.org/v3/index.json"], projects: $p}' > "$1"
}

# project <path> <top-level-json-array> <transitive-json-array>
project() {
    jq -n --arg p "$1" --argjson t "$2" --argjson r "$3" \
        '{path: $p, frameworks: [{framework: "net10.0", topLevelPackages: $t, transitivePackages: $r}]}'
}

# A project carrying no findings at all: `--vulnerable` omits `frameworks`
# entirely for these, which is exactly why an empty report cannot be read as
# proof that anything was inspected.
quiet_project() { jq -n --arg p "$1" '{path: $p}'; }

# A structurally sound inventory: two projects, real packages, no advisories.
# Every accepted case needs one, because the gate refuses to call a report
# clean without independent evidence that a graph was resolved.
GOOD_INVENTORY="$WORK/inventory-good.json"
report "$GOOD_INVENTORY" 1 "$(jq -s '.' <<EOF
$(project "/repo/A/A.csproj" "[$(pkg Microsoft.Data.Sqlite 10.0.9)]" "[$(pkg SQLitePCLRaw.core 2.1.12)]")
$(project "/repo/B/B.csproj" "[$(pkg Serilog.AspNetCore 10.0.0)]" "[$(pkg Serilog 4.0.0)]")
EOF
)"

# waiver <package> <advisory> <severity> <expires> [created]
waiver() {
    jq -n --arg p "$1" --arg a "$2" --arg s "$3" --arg e "$4" --arg c "${5:-2026-01-01}" \
        '{ecosystem: "nuget", package: $p, advisory: $a, severity: $s,
          justification: "control fixture", owner: "tesserafin-maintainer",
          issue: "https://github.com/tesserafin-project/tesserafin/issues/1",
          created: $c, expires: $e}'
}

# waiver_file <path> [waiver-json...]
waiver_file() {
    local path="$1"; shift
    local body='[]'
    if [ $# -gt 0 ]; then body="$(printf '%s\n' "$@" | jq -s '.')"; fi
    jq -n --argjson w "$body" '{version: 1, waivers: $w}' > "$path"
}

readonly GHSA_A="https://github.com/advisories/GHSA-aaaa-aaaa-aaaa"
readonly GHSA_B="https://github.com/advisories/GHSA-bbbb-bbbb-bbbb"

# ── Assertion ─────────────────────────────────────────────────────────────

CASE_LOG=""
# expect <exit> <description> -- <audit args...>
expect() {
    local want="$1"; shift
    local desc="$1"; shift
    [ "$1" = "--" ] && shift
    local got=0
    CASE_LOG="$("$AUDIT" "$@" 2>&1)" || got=$?
    if [ "$got" -eq "$want" ]; then
        ok "$desc (exit $got)"
    else
        bad "$desc: expected exit $want, got $got"
        printf '%s\n' "$CASE_LOG" | tail -n 6 | sed 's/^/        /' >&2
    fi
}

# assert_log <pattern> <description> — assert against the last case's output.
assert_log() {
    if printf '%s\n' "$CASE_LOG" | grep -qi -- "$1"; then
        ok "$2"
    else
        bad "$2"
    fi
}

# Shorthand: evaluate a vulnerability report against the known-good inventory.
# expect_report <exit> <description> <vulnerable-report> [extra args...]
expect_report() {
    local want="$1" desc="$2" rep="$3"; shift 3
    expect "$want" "$desc" -- \
        --vulnerable-report "$rep" --inventory-report "$GOOD_INVENTORY" "$@"
}

# ══ ACCEPTED ══════════════════════════════════════════════════════════════

echo "Accepted (exit 0)"

R="$WORK/clean.json"
report "$R" 1 "$(jq -s '.' <<EOF
$(quiet_project "/repo/A/A.csproj")
$(quiet_project "/repo/B/B.csproj")
EOF
)"
expect_report 0 "a valid report with no findings" "$R"

R="$WORK/low-only.json"
report "$R" 1 "[$(project "/repo/A/A.csproj" "[$(pkg Some.Package 1.0.0 "$(vuln Low "$GHSA_A")")]" '[]')]"
expect_report 0 "a Low finding is reported, not blocked" "$R"
assert_log '1 Low' "  ...and the Low finding is counted in the summary"

R="$WORK/moderate-only.json"
report "$R" 1 "[$(project "/repo/A/A.csproj" '[]' "[$(pkg Some.Package 1.0.0 "$(vuln Moderate "$GHSA_A")")]")]"
expect_report 0 "a Moderate finding is reported, not blocked" "$R"

R="$WORK/mixed-low-moderate.json"
report "$R" 1 "$(jq -s '.' <<EOF
$(project "/repo/A/A.csproj" "[$(pkg Direct.Package 1.0.0 "$(vuln Low "$GHSA_A")")]" "[$(pkg Transitive.Package 2.0.0 "$(vuln Moderate "$GHSA_B")")]")
$(quiet_project "/repo/B/B.csproj")
EOF
)"
expect_report 0 "complete direct and transitive graphs with only Low/Moderate" "$R"
assert_log 'direct' "  ...and direct findings are distinguished from transitive"

R="$WORK/high-waived.json"
report "$R" 1 "[$(project "/repo/A/A.csproj" '[]' "[$(pkg Waived.Package 1.0.0 "$(vuln High "$GHSA_A")")]")]"
W="$WORK/waivers-valid.json"
waiver_file "$W" "$(waiver Waived.Package GHSA-aaaa-aaaa-aaaa High 2099-01-01)"
expect_report 0 "a High finding under an exact, unexpired waiver" "$R" --waivers "$W"

W="$WORK/waivers-empty.json"
waiver_file "$W"
expect_report 0 "an empty waiver set alongside a clean report" "$WORK/clean.json" --waivers "$W"

echo

# ══ POLICY VIOLATIONS ═════════════════════════════════════════════════════

echo "Rejected as policy violations (exit 1)"

R="$WORK/high-direct.json"
report "$R" 1 "[$(project "/repo/A/A.csproj" "[$(pkg Direct.Package 1.0.0 "$(vuln High "$GHSA_A")")]" '[]')]"
expect_report 1 "one High direct dependency" "$R"

R="$WORK/high-transitive.json"
report "$R" 1 "[$(project "/repo/A/A.csproj" '[]' "[$(pkg Transitive.Package 1.0.0 "$(vuln High "$GHSA_A")")]")]"
expect_report 1 "one High transitive dependency" "$R"

R="$WORK/critical.json"
report "$R" 1 "[$(project "/repo/A/A.csproj" "[$(pkg Direct.Package 1.0.0 "$(vuln Critical "$GHSA_A")")]" '[]')]"
expect_report 1 "one Critical dependency" "$R"

R="$WORK/mixed-blocking.json"
report "$R" 1 "$(jq -s '.' <<EOF
$(project "/repo/A/A.csproj" "[$(pkg Direct.Package 1.0.0 "$(vuln Critical "$GHSA_A")" "$(vuln Low "$GHSA_B")")]" "[$(pkg Transitive.Package 2.0.0 "$(vuln High "$GHSA_B")")]")
$(project "/repo/B/B.csproj" "[$(pkg Other.Package 3.0.0 "$(vuln Moderate "$GHSA_A")")]" '[]')
EOF
)"
expect_report 1 "multiple mixed findings" "$R"
assert_log '1 Critical, 1 High, 1 Moderate, 1 Low' "  ...and every severity is counted, not only the blocking ones"

R="$WORK/two-high.json"
report "$R" 1 "[$(project "/repo/A/A.csproj" '[]' "$(jq -s '.' <<EOF
$(pkg Waived.Package 1.0.0 "$(vuln High "$GHSA_A")")
$(pkg Unwaived.Package 1.0.0 "$(vuln High "$GHSA_B")")
EOF
)")]"
expect_report 1 "an unwaived finding beside a valid waiver" "$R" --waivers "$WORK/waivers-valid.json"

W="$WORK/waivers-expired.json"
waiver_file "$W" "$(waiver Waived.Package GHSA-aaaa-aaaa-aaaa High 2020-01-01)"
expect_report 1 "an expired waiver does not silence its finding" "$WORK/high-waived.json" --waivers "$W"
assert_log 'EXPIRED WAIVER' "  ...and the summary says the waiver expired"

W="$WORK/waivers-wrong-advisory.json"
waiver_file "$W" "$(waiver Waived.Package GHSA-bbbb-bbbb-bbbb High 2099-01-01)"
expect_report 1 "a waiver for a different advisory" "$WORK/high-waived.json" --waivers "$W"

W="$WORK/waivers-wrong-package.json"
waiver_file "$W" "$(waiver Other.Package GHSA-aaaa-aaaa-aaaa High 2099-01-01)"
expect_report 1 "a waiver for a different package" "$WORK/high-waived.json" --waivers "$W"

W="$WORK/waivers-wrong-severity.json"
waiver_file "$W" "$(waiver Waived.Package GHSA-aaaa-aaaa-aaaa Critical 2099-01-01)"
expect_report 1 "a waiver whose severity does not match the finding" "$WORK/high-waived.json" --waivers "$W"

W="$WORK/waivers-stale.json"
waiver_file "$W" "$(waiver Vanished.Package GHSA-aaaa-aaaa-aaaa High 2099-01-01)"
expect_report 1 "a waiver that matches no current finding is stale" "$WORK/clean.json" --waivers "$W"

echo

# ══ INDETERMINATE ═════════════════════════════════════════════════════════

echo "Rejected as indeterminate (exit 2)"

expect_report 2 "a missing report" "$WORK/does-not-exist.json"

: > "$WORK/empty.json"
expect_report 2 "an empty report" "$WORK/empty.json"

printf 'error: Unable to load the service index for source\n' > "$WORK/malformed.json"
expect_report 2 "a malformed report (the scanner's error text)" "$WORK/malformed.json"

printf '{ "version": 1, "projects": [ \n' > "$WORK/truncated.json"
expect_report 2 "a truncated report" "$WORK/truncated.json"

R="$WORK/wrong-version.json"
report "$R" 99 "[$(quiet_project "/repo/A/A.csproj")]"
expect_report 2 "an unexpected report schema version" "$R"

R="$WORK/no-version.json"
jq -n '{projects: []}' > "$R"
expect_report 2 "a report with no schema version at all" "$R"

R="$WORK/zero-projects.json"
report "$R" 1 '[]'
expect_report 2 "a report covering zero projects" "$R"

R="$WORK/projects-not-array.json"
jq -n '{version: 1, projects: {}}' > "$R"
expect_report 2 "a report whose projects field is not an array" "$R"

R="$WORK/missing-severity.json"
report "$R" 1 "[$(project "/repo/A/A.csproj" "[$(jq -n '{id: "P", resolvedVersion: "1.0.0", vulnerabilities: [{advisoryurl: "'"$GHSA_A"'"}]}')]" '[]')]"
expect_report 2 "a finding with no severity" "$R"

R="$WORK/unknown-severity.json"
report "$R" 1 "[$(project "/repo/A/A.csproj" "[$(pkg P 1.0.0 "$(vuln Catastrophic "$GHSA_A")")]" '[]')]"
expect_report 2 "a finding with an unrecognised severity" "$R"

# The offline case that motivates the whole design: the report is well-formed
# and empty because the advisory database was never read, not because the tree
# is clean. Only the scanner log can tell those two apart.
printf 'warn : NU1900: Error occurred while getting package vulnerability data: The remote name could not be resolved.\n' \
    > "$WORK/nu1900.log"
expect_report 2 "a clean-looking report produced against an unreachable advisory feed (NU1900)" \
    "$WORK/clean.json" --scan-log "$WORK/nu1900.log"

printf 'Unhandled exception. System.NullReferenceException\n' > "$WORK/crash.log"
expect_report 0 "a scanner log with no NU1900 does not itself fail the gate" \
    "$WORK/clean.json" --scan-log "$WORK/crash.log"

expect 2 "a missing scan log" -- \
    --vulnerable-report "$WORK/clean.json" --inventory-report "$GOOD_INVENTORY" \
    --scan-log "$WORK/no-such.log"

# Inventory-side controls: this is the half that proves something was inspected.
INV="$WORK/inventory-zero-packages.json"
report "$INV" 1 "[$(project "/repo/A/A.csproj" '[]' '[]')]"
expect 2 "an inventory listing zero packages" -- \
    --vulnerable-report "$WORK/clean.json" --inventory-report "$INV"

INV="$WORK/inventory-unresolved.json"
report "$INV" 1 "$(jq -s '.' <<EOF
$(project "/repo/A/A.csproj" "[$(pkg P 1.0.0)]" '[]')
$(quiet_project "/repo/B/B.csproj")
EOF
)"
expect 2 "an inventory where a project resolved no frameworks (truncated graph)" -- \
    --vulnerable-report "$WORK/clean.json" --inventory-report "$INV"

expect 2 "an inventory that does not exist (absent dependency manifest)" -- \
    --vulnerable-report "$WORK/clean.json" --inventory-report "$WORK/no-inventory.json"

expect 2 "an inventory that is a directory, not a file" -- \
    --vulnerable-report "$WORK/clean.json" --inventory-report "$WORK"

expect 2 "an inventory covering fewer projects than the solution has" -- \
    --vulnerable-report "$WORK/clean.json" --inventory-report "$GOOD_INVENTORY" \
    --expected-projects 53

expect 0 "an inventory covering exactly the expected project count" -- \
    --vulnerable-report "$WORK/clean.json" --inventory-report "$GOOD_INVENTORY" \
    --expected-projects 2

echo
echo "Rejected as indeterminate — invalid arguments"

expect 2 "no arguments at all" --
expect 2 "an unknown flag" -- --vulnerable-report "$WORK/clean.json" \
    --inventory-report "$GOOD_INVENTORY" --pretty-please
expect 2 "a flag with no value" -- --vulnerable-report
expect 2 "a vulnerability report with no inventory" -- --vulnerable-report "$WORK/clean.json"
expect 2 "--scan combined with --vulnerable-report" -- --scan --vulnerable-report "$WORK/clean.json"
expect 2 "a non-numeric --expected-projects" -- \
    --vulnerable-report "$WORK/clean.json" --inventory-report "$GOOD_INVENTORY" \
    --expected-projects several

echo
echo "Rejected as indeterminate — structurally invalid waiver files"

expect_report 2 "a waiver file that does not exist" "$WORK/clean.json" --waivers "$WORK/no-waivers.json"

: > "$WORK/waivers-empty-file.json"
expect_report 2 "an empty waiver file" "$WORK/clean.json" --waivers "$WORK/waivers-empty-file.json"

printf 'not json\n' > "$WORK/waivers-malformed.json"
expect_report 2 "a malformed waiver file" "$WORK/clean.json" --waivers "$WORK/waivers-malformed.json"

jq -n '{version: 7, waivers: []}' > "$WORK/waivers-bad-version.json"
expect_report 2 "a waiver file with an unexpected schema version" "$WORK/clean.json" \
    --waivers "$WORK/waivers-bad-version.json"

W="$WORK/waivers-missing-field.json"
jq -n '{version: 1, waivers: [{ecosystem: "nuget", package: "P", advisory: "GHSA-aaaa-aaaa-aaaa", severity: "High"}]}' > "$W"
expect_report 2 "a waiver missing its justification, owner, issue and dates" "$WORK/high-waived.json" --waivers "$W"

W="$WORK/waivers-wildcard.json"
waiver_file "$W" "$(waiver '*' GHSA-aaaa-aaaa-aaaa High 2099-01-01)"
expect_report 2 "a package-only wildcard waiver" "$WORK/high-waived.json" --waivers "$W"

W="$WORK/waivers-bad-date.json"
waiver_file "$W" "$(waiver Waived.Package GHSA-aaaa-aaaa-aaaa High "next tuesday")"
expect_report 2 "a waiver with a malformed expiry date" "$WORK/high-waived.json" --waivers "$W"

W="$WORK/waivers-loose-date.json"
waiver_file "$W" "$(waiver Waived.Package GHSA-aaaa-aaaa-aaaa High "2099-1-1")"
expect_report 2 "a waiver whose date is not YYYY-MM-DD" "$WORK/high-waived.json" --waivers "$W"

W="$WORK/waivers-duplicate.json"
waiver_file "$W" \
    "$(waiver Waived.Package GHSA-aaaa-aaaa-aaaa High 2099-01-01)" \
    "$(waiver Waived.Package GHSA-aaaa-aaaa-aaaa High 2099-06-01)"
expect_report 2 "duplicate waivers for the same package and advisory" "$WORK/high-waived.json" --waivers "$W"

W="$WORK/waivers-no-issue.json"
jq -n '{version: 1, waivers: [{ecosystem: "nuget", package: "Waived.Package",
    advisory: "GHSA-aaaa-aaaa-aaaa", severity: "High", justification: "because",
    owner: "someone", issue: "we will file one later", created: "2026-01-01",
    expires: "2099-01-01"}]}' > "$W"
expect_report 2 "a waiver without a real tracking-issue URL" "$WORK/high-waived.json" --waivers "$W"

W="$WORK/waivers-bad-advisory.json"
waiver_file "$W" "$(waiver Waived.Package CVE-2025-6965 High 2099-01-01)"
expect_report 2 "a waiver whose advisory is not a GHSA identifier" "$WORK/high-waived.json" --waivers "$W"

W="$WORK/waivers-wrong-ecosystem.json"
jq -n '{version: 1, waivers: [{ecosystem: "npm", package: "Waived.Package",
    advisory: "GHSA-aaaa-aaaa-aaaa", severity: "High", justification: "because",
    owner: "someone", issue: "https://github.com/o/r/issues/1",
    created: "2026-01-01", expires: "2099-01-01"}]}' > "$W"
expect_report 2 "a waiver for another ecosystem" "$WORK/high-waived.json" --waivers "$W"

echo

# ══ LIVE ══════════════════════════════════════════════════════════════════
#
# The synthetic cases above all assume this file guessed NuGet's report shape
# correctly. This one does not assume it.

echo "Live control"

if [ "$RUN_LIVE" -eq 0 ]; then
    skip "live scan disabled by --no-live"
elif ! command -v dotnet >/dev/null 2>&1; then
    skip "live scan needs the .NET SDK"
else
    LIVE="$WORK/live"
    LIVE_LOG="$WORK/live.log"
    if "$AUDIT" --scan --work "$LIVE" > "$LIVE_LOG" 2>&1; then
        ok "a live scan of this repository is CLEAN (exit 0)"
    else
        live_status=$?
        if [ "$live_status" -eq 1 ]; then
            bad "a live scan of this repository reports an unresolved High/Critical finding"
        else
            bad "a live scan of this repository is INDETERMINATE (exit $live_status)"
        fi
        tail -n 15 "$LIVE_LOG" | sed 's/^/        /' >&2
    fi

    if [ -f "$LIVE/nuget-inventory.json" ]; then
        live_packages="$(jq -r '[.projects[].frameworks//[]|.[]|(.topLevelPackages//[]),(.transitivePackages//[])|.[].id]|unique|length' "$LIVE/nuget-inventory.json")"
        if [ "${live_packages:-0}" -gt 0 ]; then
            ok "the live inventory proves $live_packages distinct packages were inspected"
        else
            bad "the live inventory proves nothing was inspected"
        fi

        # Mutate the real report rather than a hand-written one: this is the
        # control that would catch the evaluator agreeing with a fixture but
        # not with NuGet.
        MUTATED="$WORK/live-mutated.json"
        jq --arg u "$GHSA_A" '
            .projects |= (
                map(select(has("frameworks") | not)) as $quiet
                | (.[0] // {path: "/repo/A/A.csproj"})
                | [ {path: .path, frameworks: [{framework: "net10.0",
                      topLevelPackages: [{id: "Injected.Package", resolvedVersion: "1.0.0",
                        vulnerabilities: [{severity: "Critical", advisoryurl: $u}]}],
                      transitivePackages: []}]} ]
                + ($quiet | map(select(.path != "/repo/A/A.csproj")))
            )' "$LIVE/nuget-vulnerable.json" > "$MUTATED" 2>/dev/null

        expect 1 "the real report, mutated to carry one Critical, is refused" -- \
            --vulnerable-report "$MUTATED" --inventory-report "$LIVE/nuget-inventory.json"
    else
        bad "the live scan produced no inventory report"
    fi
fi

echo
echo "  passed: $PASS   failed: $FAIL   skipped: $SKIP"
[ "$FAIL" -eq 0 ] || exit 1
