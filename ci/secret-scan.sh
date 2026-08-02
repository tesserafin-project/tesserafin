#!/usr/bin/env bash
#
# Fail-closed secret scanning — repository-owned slice of the C3 gate.
#
# THREE VERDICTS, AND ONLY THREE.
#
#   0  CLEAN          the question was asked and the answer is "no secret"
#   1  FINDINGS       the question was asked and the answer is "a secret"
#   2  INDETERMINATE  the question was NOT answered
#
# The third verdict is the point of the whole script. A scanner that crashed, a
# missing report, a truncated history, a baseline that no longer describes
# reality — every one of those produces "no findings" from a naive wrapper, and
# every one of them is a lie. They exit 2 here, and both 1 and 2 are red.
#
# WHY GITLEAKS RUNS WITH `--exit-code 7`. Gitleaks' default findings exit code is
# 1, which is also what Cobra returns for an unknown flag and what the binary
# returns for several internal errors. With the default, "you typed a flag this
# version does not have" and "there is a secret in your repository" are the same
# integer. 7 is reserved for findings alone, so anything that is neither 0 nor 7
# is definitionally a scanner problem and becomes INDETERMINATE.
#
# WHY THE EVALUATOR ONLY READS JSON. Gitleaks' human-readable output is not a
# contract: it is formatted for a terminal, it changes between releases, and
# matching on it would make this gate's verdict depend on a log string. Every
# decision below is taken from the structured report, and a report that is
# missing, unparseable, or contradicts the process exit status is INDETERMINATE.
#
# WHAT THIS DOES NOT COVER, stated so that a green run is not over-read:
#
#   * .NET metadata heaps. A C# `const string` is inlined into the `#US` user
#     string heap as UTF-16LE. Gitleaks does not decode it, and neither does an
#     ASCII `strings` pass. The provider-authentication structural audit
#     (ci/provider-auth-inventory.json, docs/provider-auth-audit.md, run inside
#     `dotnet test`) is what covers that, and it is a separate, authoritative
#     gate. "Gitleaks was clean" never means "no credential is compiled in".
#   * Short, low-entropy provider credentials. Two of the three credentials this
#     project removed were six and eight characters. No entropy or length rule
#     reaches those without firing constantly. Same answer: the structural audit.
#   * GitHub-native push protection. This gate detects a commit after GitHub has
#     already accepted it. It cannot refuse the push.

set -euo pipefail

# --------------------------------------------------------------------------
# Pinned scanner identity. Must agree with ci/install-gitleaks.sh.
# --------------------------------------------------------------------------
EXPECTED_VERSION="8.30.0"
EXPECTED_RELEASE_COMMIT="6eaad039603a4de39fddd1cf5f727391efe9974e"
EXPECTED_ARCHIVE_SHA256="79a3ab579b53f71efd634f3aaf7e04a0fa0cf206b7ed434638d1547a2470a66e"

# Findings exit code reserved for Gitleaks. See the header.
GITLEAKS_FINDINGS_EXIT=7

# --------------------------------------------------------------------------
# Bounded scan limits. Recorded in every sanitized report, because a limit that
# is not published is indistinguishable from a blind spot.
# --------------------------------------------------------------------------
# Recursive decoding of encoded secrets. Gitleaks' own default is 5; 3 is chosen
# because it is proven sufficient here (a doubly base64-encoded credential is
# detected at 3 and missed at 0) and because each level multiplies the work.
MAX_DECODE_DEPTH=3
# Nested archive traversal. Gitleaks defaults to 0 — no traversal at all — which
# would let a credential inside a committed .tar.gz pass unseen.
MAX_ARCHIVE_DEPTH=2
# Files above this are skipped BY GITLEAKS. They are enumerated and reported
# rather than dropped, so the limit is visible in the verdict.
MAX_TARGET_MEGABYTES=100
# Hard wall-clock ceiling, in seconds, applied both by Gitleaks and by an
# external `timeout` a little above it, so a wedged scanner cannot hang a job.
SCAN_TIMEOUT_SECONDS=1800

# --------------------------------------------------------------------------
# The fields a sanitized finding may carry, and the fields it may never carry.
# --------------------------------------------------------------------------
SANITIZED_KEYS='["baselineStatus","classification","commit","fingerprint","line","path","ruleId"]'
FORBIDDEN_KEYS="Secret Match Line Entropy Author Email Message Description Link StartColumn EndColumn SymlinkFile"

MODE=""
REPO=""
REPO_NAME=""
BIN=""
CONFIG=""
BASELINE=""
IGNORE_FILE=""
OUT_DIR=""
SUMMARY=""
DEFAULT_BRANCH=""
MIN_COMMITS=0
MIN_REFS=0
SKIP_BUILD_OUTPUT_CHECK=0

VERDICT="INDETERMINATE"

usage() {
    cat <<'EOF'
Usage: ci/secret-scan.sh --mode MODE [options]

  --mode tree        scan the checked-out working tree; the historical baseline
                     is deliberately NOT applied, so a historical fingerprint
                     can never suppress a current-tree finding
  --mode history     scan every fetched ref plus the detached HEAD, twice: once
                     raw (no .gitleaksignore) compared exactly against the
                     committed baseline, and once with the baseline applied,
                     which must be empty
  --mode baseline    validate the baseline artefacts alone; no scan is run

  --repo PATH            repository to scan (default: the repository containing
                         this script)
  --repo-name NAME       name recorded in the sanitized report
  --bin PATH             gitleaks binary (default: ci/install-gitleaks.sh's path)
  --config PATH          .gitleaks.toml (default: <repo>/.gitleaks.toml)
  --baseline PATH        ci/secret-history-baseline.json
  --ignore PATH          .gitleaksignore
  --out DIR              directory for the sanitized report (default: a
                         temporary directory)
  --summary FILE         write a Markdown summary here
  --default-branch NAME  branch that must exist locally in history mode
  --min-commits N        floor for reachable commits (incomplete-fetch check)
  --min-refs N           floor for local refs (incomplete-fetch check)
  --allow-build-output   permit node_modules/bin/obj during a tree scan
  --timeout-seconds N    override the scan timeout (deterministic controls only)

Exit codes: 0 CLEAN, 1 FINDINGS, 2 INDETERMINATE.
EOF
}

# --------------------------------------------------------------------------
# Verdict emitters. `indeterminate` is used for everything that means the
# question was not answered; it is never used for "a secret was found".
# --------------------------------------------------------------------------
indeterminate() {
    VERDICT="INDETERMINATE"
    printf 'secret-scan: INDETERMINATE: %s\n' "$1" >&2
    write_summary "$1"
    exit 2
}

findings() {
    VERDICT="FINDINGS"
    printf 'secret-scan: FINDINGS: %s\n' "$1" >&2
    write_summary "$1"
    exit 1
}

clean() {
    VERDICT="CLEAN"
    printf 'secret-scan: CLEAN: %s\n' "$1"
    write_summary "$1"
    exit 0
}

while [ $# -gt 0 ]; do
    case "$1" in
        --mode) MODE="${2:?--mode needs a value}"; shift 2 ;;
        --repo) REPO="${2:?--repo needs a value}"; shift 2 ;;
        --repo-name) REPO_NAME="${2:?--repo-name needs a value}"; shift 2 ;;
        --bin) BIN="${2:?--bin needs a value}"; shift 2 ;;
        --config) CONFIG="${2:?--config needs a value}"; shift 2 ;;
        --baseline) BASELINE="${2:?--baseline needs a value}"; shift 2 ;;
        --ignore) IGNORE_FILE="${2:?--ignore needs a value}"; shift 2 ;;
        --out) OUT_DIR="${2:?--out needs a value}"; shift 2 ;;
        --summary) SUMMARY="${2:?--summary needs a value}"; shift 2 ;;
        --default-branch) DEFAULT_BRANCH="${2:?--default-branch needs a value}"; shift 2 ;;
        --min-commits) MIN_COMMITS="${2:?--min-commits needs a value}"; shift 2 ;;
        --min-refs) MIN_REFS="${2:?--min-refs needs a value}"; shift 2 ;;
        --allow-build-output) SKIP_BUILD_OUTPUT_CHECK=1; shift ;;
        # Controls only. Production callers use the pinned default above; a
        # control needs a ceiling it can actually reach inside a test run.
        --timeout-seconds) SCAN_TIMEOUT_SECONDS="${2:?--timeout-seconds needs a value}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'secret-scan: INDETERMINATE: unknown option %s\n' "$1" >&2; exit 2 ;;
    esac
done

# --------------------------------------------------------------------------
# Raw reports live in a private temporary directory, mode 0600, and are removed
# by a trap on every exit path including a signal. They are never printed, never
# written into the repository, and never uploaded.
# --------------------------------------------------------------------------
RAW_DIR="$(mktemp -d)"
chmod 0700 "$RAW_DIR"
EMPTY_IGNORE_DIR="$(mktemp -d)"
# shellcheck disable=SC2317  # invoked by the trap below, not inline
cleanup() { rm -rf "$RAW_DIR" "$EMPTY_IGNORE_DIR"; }
trap cleanup EXIT INT TERM

SUMMARY_BODY=""
add_summary() { SUMMARY_BODY="${SUMMARY_BODY}$1"$'\n'; }

write_summary() {
    [ -n "$SUMMARY" ] || return 0
    # shellcheck disable=SC2016  # the backticks are Markdown, not command substitution
    {
        printf '## Secret scan — %s (%s)\n\n' "$VERDICT" "${MODE:-unknown mode}"
        printf '%s\n' "$1"
        printf '\n| | |\n| --- | --- |\n'
        printf '| repository | `%s` |\n' "$REPO_NAME"
        printf '| mode | `%s` |\n' "$MODE"
        printf '| verdict | **%s** |\n' "$VERDICT"
        printf '| gitleaks | `%s` (release `%s`) |\n' "$EXPECTED_VERSION" "$EXPECTED_RELEASE_COMMIT"
        printf '| archive sha256 | `%s` |\n' "$EXPECTED_ARCHIVE_SHA256"
        printf '| findings exit code | `%s` |\n' "$GITLEAKS_FINDINGS_EXIT"
        printf '| max decode depth | `%s` |\n' "$MAX_DECODE_DEPTH"
        printf '| max archive depth | `%s` |\n' "$MAX_ARCHIVE_DEPTH"
        printf '| max target megabytes | `%s` |\n' "$MAX_TARGET_MEGABYTES"
        printf '| timeout (s) | `%s` |\n' "$SCAN_TIMEOUT_SECONDS"
        printf '\n%s\n' "$SUMMARY_BODY"
    } >"$SUMMARY" 2>/dev/null || true
}

# --------------------------------------------------------------------------
# Preflight
# --------------------------------------------------------------------------
case "$MODE" in
    tree|history|baseline) ;;
    "") printf 'secret-scan: INDETERMINATE: --mode is required\n' >&2; exit 2 ;;
    *) printf 'secret-scan: INDETERMINATE: unknown mode %s\n' "$MODE" >&2; exit 2 ;;
esac

for tool in jq git sha256sum timeout; do
    command -v "$tool" >/dev/null 2>&1 || indeterminate "required tool '$tool' is not on PATH"
done

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[ -n "$REPO" ] || REPO="$(cd -- "$SCRIPT_DIR/.." && pwd)"
[ -d "$REPO" ] || indeterminate "repository path '$REPO' does not exist"
REPO="$(cd -- "$REPO" && pwd)"
git -C "$REPO" rev-parse --git-dir >/dev/null 2>&1 \
    || indeterminate "'$REPO' is not a git repository"

[ -n "$REPO_NAME" ] || REPO_NAME="$(basename -- "$REPO")"
[ -n "$CONFIG" ] || CONFIG="$REPO/.gitleaks.toml"
[ -n "$IGNORE_FILE" ] || IGNORE_FILE="$REPO/.gitleaksignore"
[ -n "$BASELINE" ] || BASELINE="$REPO/ci/secret-history-baseline.json"
[ -n "$OUT_DIR" ] || OUT_DIR="$(mktemp -d)"

# The configuration is part of the verdict. A missing or truncated .gitleaks.toml
# silently reduces the ruleset, and a reduced ruleset finds less.
[ -f "$CONFIG" ] || indeterminate "configuration '$CONFIG' is missing"
[ -s "$CONFIG" ] || indeterminate "configuration '$CONFIG' is empty"
grep -Eq '^[[:space:]]*useDefault[[:space:]]*=[[:space:]]*true[[:space:]]*$' "$CONFIG" \
    || indeterminate "configuration '$CONFIG' does not extend the complete built-in ruleset (useDefault = true)"
grep -Eq '^[[:space:]]*\[extend\][[:space:]]*$' "$CONFIG" \
    || indeterminate "configuration '$CONFIG' has no [extend] table"

mkdir -p "$OUT_DIR" 2>/dev/null || indeterminate "cannot create output directory '$OUT_DIR'"
[ -w "$OUT_DIR" ] || indeterminate "output directory '$OUT_DIR' is not writable"

# --------------------------------------------------------------------------
# Scanner identity: present, runnable, exactly the pinned version, and still the
# bytes ci/install-gitleaks.sh verified.
# --------------------------------------------------------------------------
if [ -z "$BIN" ]; then
    BIN="$("$SCRIPT_DIR/install-gitleaks.sh" --print-bin 2>/dev/null || true)"
fi
[ -n "$BIN" ] || indeterminate "no gitleaks binary path resolved"
[ -x "$BIN" ] || indeterminate "gitleaks binary '$BIN' is missing or not executable"

REPORTED_VERSION="$("$BIN" version 2>/dev/null | tr -d '[:space:]' || true)"
[ -n "$REPORTED_VERSION" ] || indeterminate "gitleaks binary '$BIN' will not report a version"
[ "$REPORTED_VERSION" = "$EXPECTED_VERSION" ] \
    || indeterminate "gitleaks version mismatch: binary reports '$REPORTED_VERSION', pin is '$EXPECTED_VERSION'"

PROVENANCE="$(dirname -- "$BIN")/gitleaks.provenance"
if [ -f "$PROVENANCE" ]; then
    recorded_binary="$(awk '$1 == "binary_sha256" { print $2 }' "$PROVENANCE")"
    recorded_archive="$(awk '$1 == "archive_sha256" { print $2 }' "$PROVENANCE")"
    actual_binary="$(sha256sum "$BIN" | cut -d' ' -f1)"
    [ "$recorded_binary" = "$actual_binary" ] \
        || indeterminate "gitleaks binary digest does not match its provenance record (binary replaced after installation)"
    [ "$recorded_archive" = "$EXPECTED_ARCHIVE_SHA256" ] \
        || indeterminate "gitleaks provenance records archive digest '$recorded_archive', pin is '$EXPECTED_ARCHIVE_SHA256'"
else
    indeterminate "gitleaks provenance file '$PROVENANCE' is missing; the binary's origin cannot be verified"
fi

# --------------------------------------------------------------------------
# Baseline validation. Runs in every mode: an invalid baseline invalidates the
# history verdict, and a tree scan that shipped alongside a broken baseline
# would still be misread as "the gate is fine".
# --------------------------------------------------------------------------
BASELINE_FINGERPRINTS="$RAW_DIR/baseline-fingerprints.txt"
IGNORE_FINGERPRINTS="$RAW_DIR/ignore-fingerprints.txt"

validate_baseline() {
    [ -f "$BASELINE" ] || indeterminate "baseline '$BASELINE' is missing"
    jq -e 'type == "object"' "$BASELINE" >/dev/null 2>&1 \
        || indeterminate "baseline '$BASELINE' is not a JSON object"
    jq -e '.repository and .authorizedDisposition and (.gitleaks|type == "array") and (.structural|type == "array")' \
        "$BASELINE" >/dev/null 2>&1 \
        || indeterminate "baseline '$BASELINE' is missing a required top-level field"

    # No entry may carry a value, and every entry must carry its provenance.
    jq -e '[.gitleaks[] | select((.fingerprint|type) != "string"
             or (.ruleId|type) != "string"
             or (.path|type) != "string"
             or (.commit|type) != "string"
             or (.commitDate|type) != "string"
             or (.provenance|type) != "string"
             or (.classification|type) != "string"
             or (.disposition|type) != "string"
             or (.line|type) != "number")] | length == 0' "$BASELINE" >/dev/null 2>&1 \
        || indeterminate "baseline '$BASELINE' has an entry with a missing or wrongly typed field"
    jq -e '[.gitleaks[], .structural[] | keys[]] | index("secret") == null
             and index("match") == null and index("value") == null' "$BASELINE" >/dev/null 2>&1 \
        || indeterminate "baseline '$BASELINE' carries a value; no baseline entry may store one"

    jq -r '.gitleaks[].fingerprint' "$BASELINE" >"$BASELINE_FINGERPRINTS"

    # Exact entries only. A glob, a regex metacharacter or a bare path would be a
    # path-wide or rule-wide exception wearing a fingerprint's clothes.
    # A Gitleaks fingerprint is <commit>:<path>:<rule>:<line>.
    while IFS= read -r fp; do
        case "$fp" in
            *'*'*|*'?'*|*'['*|*'('*|*'|'*|*'^'*|*'$'*|*'+'*)
                indeterminate "baseline fingerprint is not an exact entry: it contains a glob or regex metacharacter" ;;
        esac
        printf '%s' "$fp" | grep -Eq '^[0-9a-f]{40}:[^:]+:[A-Za-z0-9._-]+:[0-9]+$' \
            || indeterminate "baseline fingerprint is malformed (expected <commit>:<path>:<rule>:<line>)"
    done <"$BASELINE_FINGERPRINTS"

    if [ "$(sort <"$BASELINE_FINGERPRINTS" | uniq -d | wc -l)" -ne 0 ]; then
        indeterminate "baseline contains duplicate fingerprints"
    fi
    if ! LC_ALL=C sort -c "$BASELINE_FINGERPRINTS" 2>/dev/null; then
        indeterminate "baseline fingerprints are not sorted"
    fi

    # .gitleaksignore must describe exactly the same set. Two artefacts that
    # disagree mean one of them is stale, and neither can be trusted.
    [ -f "$IGNORE_FILE" ] || indeterminate "'$IGNORE_FILE' is missing"
    grep -vE '^[[:space:]]*(#|$)' "$IGNORE_FILE" >"$IGNORE_FINGERPRINTS" || true
    if [ "$(sort <"$IGNORE_FINGERPRINTS" | uniq -d | wc -l)" -ne 0 ]; then
        indeterminate "'$IGNORE_FILE' contains duplicate fingerprints"
    fi
    if ! LC_ALL=C sort -c "$IGNORE_FINGERPRINTS" 2>/dev/null; then
        indeterminate "'$IGNORE_FILE' fingerprints are not sorted"
    fi
    if ! diff -q <(LC_ALL=C sort "$BASELINE_FINGERPRINTS") <(LC_ALL=C sort "$IGNORE_FINGERPRINTS") >/dev/null; then
        indeterminate "'$IGNORE_FILE' and '$BASELINE' describe different fingerprint sets"
    fi

    # No production file may carry an in-band allow comment. That is a
    # reviewer-invisible suppression living inside the very line it excuses, and
    # it is exactly what an exact-fingerprint baseline exists to replace.
    #
    # The marker is assembled here rather than written literally: this script is
    # itself a tracked file, so a literal would both trip this check and make
    # Gitleaks ignore findings on the line carrying it.
    local marker offenders
    marker="$(printf 'gitleaks%sallow' ':')"
    offenders="$(git -C "$REPO" grep -lI --cached -e "$marker" -- . || true)"
    [ -z "$offenders" ] || indeterminate "an in-band allow comment was found in tracked files: $offenders"
}

validate_baseline

BASELINE_COUNT="$(wc -l <"$BASELINE_FINGERPRINTS" | tr -d ' ')"
STRUCTURAL_COUNT="$(jq '.structural | length' "$BASELINE")"

if [ "$MODE" = "baseline" ]; then
    add_summary "Baseline validated: ${BASELINE_COUNT} exact Gitleaks fingerprints, ${STRUCTURAL_COUNT} structural entries, sorted, unique, no glob, no regex, no path-wide or rule-wide exception, no in-band allow comment, no value stored."
    clean "the baseline artefacts are internally consistent"
fi

# --------------------------------------------------------------------------
# Sanitizing. The raw report is the only thing that ever holds a secret, and it
# never leaves $RAW_DIR. Everything downstream reads the sanitized copy.
# --------------------------------------------------------------------------
sanitize() {
    local raw="$1" out="$2" scan_mode="$3" status_default="$4"
    jq --arg repo "$REPO_NAME" \
       --arg mode "$scan_mode" \
       --arg statusDefault "$status_default" \
       --slurpfile baseline "$BASELINE" '
        ($baseline[0].gitleaks | map({key: .fingerprint, value: .classification}) | from_entries) as $class
        | [ .[] | {
              ruleId:        (.RuleID // ""),
              path:          (.File // ""),
              line:          (.StartLine // 0),
              commit:        (.Commit // ""),
              fingerprint:   (.Fingerprint // ""),
              classification: ($class[(.Fingerprint // "")] // "unclassified"),
              baselineStatus: (if $class[(.Fingerprint // "")] then "baseline" else $statusDefault end)
            } ]
        | sort_by(.fingerprint, .path, .line)
       ' "$raw" >"$out" 2>/dev/null || return 1

    # Assert, rather than assume, that nothing forbidden survived. A sanitizer
    # that silently produced a raw copy is the one failure that would publish a
    # credential, so it is checked mechanically on every run.
    jq -e --argjson allowed "$SANITIZED_KEYS" \
        '[.[] | keys | sort] | all(. == ($allowed|sort))' "$out" >/dev/null 2>&1 || return 1
    local key
    for key in $FORBIDDEN_KEYS; do
        if jq -e --arg k "$key" '[.[] | has($k)] | any' "$out" >/dev/null 2>&1; then
            return 1
        fi
    done
    return 0
}

# Run gitleaks and classify the process outcome BEFORE looking at the report.
# $1 sub-command, $2 report path, $3 ignore path, $4.. extra arguments.
run_gitleaks() {
    local sub="$1" report="$2" ignore_path="$3" target="$4"; shift 4
    local rc=0
    # A report that pre-exists would make "the scanner never wrote one"
    # indistinguishable from "the scanner wrote an empty one".
    rm -f "$report"
    set +e
    timeout --signal=TERM --kill-after=30 "$((SCAN_TIMEOUT_SECONDS + 60))" \
        "$BIN" "$sub" "$target" \
            --config "$CONFIG" \
            --gitleaks-ignore-path "$ignore_path" \
            --report-format json \
            --report-path "$report" \
            --exit-code "$GITLEAKS_FINDINGS_EXIT" \
            --max-decode-depth "$MAX_DECODE_DEPTH" \
            --max-archive-depth "$MAX_ARCHIVE_DEPTH" \
            --max-target-megabytes "$MAX_TARGET_MEGABYTES" \
            --timeout "$SCAN_TIMEOUT_SECONDS" \
            --redact \
            --no-banner \
            --no-color \
            --log-level error \
            "$@" >/dev/null 2>"$RAW_DIR/stderr.txt"
    rc=$?
    set -e
    if [ -f "$report" ]; then chmod 0600 "$report"; fi
    printf '%s' "$rc"
}

# Turn (exit status, report) into a trustworthy finding count, or refuse.
# Sets COUNT. Any contradiction between the two is INDETERMINATE.
COUNT=0
evaluate() {
    local rc="$1" report="$2" label="$3"
    case "$rc" in
        0|"$GITLEAKS_FINDINGS_EXIT") ;;
        124|137) indeterminate "$label: the scanner exceeded its ${SCAN_TIMEOUT_SECONDS}s timeout" ;;
        *) indeterminate "$label: the scanner exited $rc, which is neither 0 (clean) nor $GITLEAKS_FINDINGS_EXIT (the reserved findings code); it did not complete: $(tr '\n' ' ' <"$RAW_DIR/stderr.txt" 2>/dev/null | tail -c 300)" ;;
    esac
    [ -f "$report" ] || indeterminate "$label: the scanner produced no report at all"
    jq -e 'type == "array"' "$report" >/dev/null 2>&1 \
        || indeterminate "$label: the report is not a JSON array (malformed report)"
    COUNT="$(jq 'length' "$report")"
    if [ "$rc" -eq 0 ] && [ "$COUNT" -ne 0 ]; then
        indeterminate "$label: the scanner exited 0 while reporting $COUNT findings; exit status and report contradict each other"
    fi
    if [ "$rc" -eq "$GITLEAKS_FINDINGS_EXIT" ] && [ "$COUNT" -eq 0 ]; then
        indeterminate "$label: the scanner signalled findings but the report is empty; exit status and report contradict each other"
    fi
}

# --------------------------------------------------------------------------
# Repository preconditions specific to a history scan.
# --------------------------------------------------------------------------
if [ "$MODE" = "history" ]; then
    [ "$(git -C "$REPO" rev-parse --is-shallow-repository)" = "false" ] \
        || indeterminate "the repository is a shallow clone; a truncated history cannot be scanned completely"
    # --absolute-git-dir, not --git-dir. The latter answers relatively (".git"),
    # so the test would resolve against the CALLER's working directory rather
    # than the repository being scanned — and a caller whose own checkout is
    # shallow would make every scan report the wrong repository as truncated.
    if [ -f "$(git -C "$REPO" rev-parse --absolute-git-dir)/shallow" ]; then
        indeterminate "the repository has a shallow boundary file; its history is incomplete"
    fi
    [ -n "$DEFAULT_BRANCH" ] || indeterminate "history mode requires --default-branch"
    if ! git -C "$REPO" rev-parse --verify --quiet "refs/heads/$DEFAULT_BRANCH" >/dev/null \
       && ! git -C "$REPO" rev-parse --verify --quiet "refs/remotes/origin/$DEFAULT_BRANCH" >/dev/null; then
        indeterminate "the expected default-branch ref '$DEFAULT_BRANCH' is not present locally"
    fi
    git -C "$REPO" rev-parse --verify --quiet HEAD >/dev/null \
        || indeterminate "HEAD does not resolve; the checked-out commit cannot be scanned"

    HEAD_SHA="$(git -C "$REPO" rev-parse HEAD)"
    REF_COUNT="$(git -C "$REPO" for-each-ref --format='%(refname)' | wc -l | tr -d ' ')"
    COMMIT_COUNT="$(git -C "$REPO" rev-list --all --count)"
    if [ "$MIN_REFS" -gt 0 ] && [ "$REF_COUNT" -lt "$MIN_REFS" ]; then
        indeterminate "only $REF_COUNT refs are present, fewer than the expected floor of $MIN_REFS; the ref fetch was incomplete"
    fi
    if [ "$MIN_COMMITS" -gt 0 ] && [ "$COMMIT_COUNT" -lt "$MIN_COMMITS" ]; then
        indeterminate "only $COMMIT_COUNT commits are reachable, fewer than the expected floor of $MIN_COMMITS; the history is incomplete"
    fi
fi

# --------------------------------------------------------------------------
# Tree mode
# --------------------------------------------------------------------------
if [ "$MODE" = "tree" ]; then
    # A tree scan is meaningful only on a pristine checkout. node_modules, bin/
    # and obj/ hold third-party and compiled content that is not this
    # repository's, and scanning them answers a different question.
    if [ "$SKIP_BUILD_OUTPUT_CHECK" -eq 0 ]; then
        for d in node_modules bin obj; do
            if find "$REPO" -maxdepth 3 -type d -name "$d" -not -path '*/.git/*' -print -quit | grep -q .; then
                indeterminate "build output '$d' is present in the tree; a current-tree scan must run on a pristine checkout (pass --allow-build-output to override deliberately)"
            fi
        done
    fi

    # Oversized files are enumerated so the limit is reported rather than
    # applied silently.
    OVERSIZED="$(find "$REPO" -type f -not -path '*/.git/*' -size "+${MAX_TARGET_MEGABYTES}M" -printf '%P\n' 2>/dev/null | LC_ALL=C sort || true)"

    # The historical baseline does not apply here, and that is structural rather
    # than a matter of which flag is passed. A `git`-mode fingerprint is
    # <commit>:<path>:<rule>:<line>; a `dir`-mode fingerprint is
    # <path>:<rule>:<line> with no commit. The baseline validator above refuses
    # any entry that does not begin with a 40-character commit hash, so no
    # committed ignore entry can be shaped like anything this scan produces. The
    # control suite proves it rather than trusting the argument.
    RAW="$RAW_DIR/tree.json"
    rc="$(run_gitleaks dir "$RAW" "$EMPTY_IGNORE_DIR" "$REPO")"
    evaluate "$rc" "$RAW" "current-tree scan"
    TREE_COUNT="$COUNT"

    SANITIZED="$OUT_DIR/secret-scan-tree.json"
    sanitize "$RAW" "$RAW_DIR/tree-findings.json" "tree" "new" \
        || indeterminate "the sanitizer failed; no report may be published unsanitized"

    jq -n --arg repo "$REPO_NAME" --arg mode tree \
          --arg verdict "$([ "$TREE_COUNT" -eq 0 ] && echo CLEAN || echo FINDINGS)" \
          --arg version "$EXPECTED_VERSION" --arg release "$EXPECTED_RELEASE_COMMIT" \
          --arg archive "$EXPECTED_ARCHIVE_SHA256" --arg head "$(git -C "$REPO" rev-parse HEAD)" \
          --argjson decode "$MAX_DECODE_DEPTH" --argjson archiveDepth "$MAX_ARCHIVE_DEPTH" \
          --argjson megabytes "$MAX_TARGET_MEGABYTES" --argjson timeout "$SCAN_TIMEOUT_SECONDS" \
          --argjson findings "$(cat "$RAW_DIR/tree-findings.json")" \
          --arg oversized "$OVERSIZED" '
        {repository: $repo, scanMode: $mode, verdict: $verdict, headCommit: $head,
         scanner: {version: $version, releaseCommit: $release, archiveSha256: $archive},
         limits: {maxDecodeDepth: $decode, maxArchiveDepth: $archiveDepth,
                  maxTargetMegabytes: $megabytes, timeoutSeconds: $timeout},
         baselineApplied: false,
         oversizedSkipped: ($oversized | split("\n") | map(select(length > 0))),
         counts: {findings: ($findings|length)},
         findings: $findings}' >"$SANITIZED" \
        || indeterminate "could not write the sanitized report"

    add_summary "Independent \`dir\` scan of the checked-out tree at \`$(git -C "$REPO" rev-parse HEAD)\`. The historical baseline does **not** apply: every baselined entry is commit-scoped and every finding here is path-scoped, so the two can never match. Enforced by the baseline validator and proven by a control."
    add_summary ""
    add_summary "- findings: **${TREE_COUNT}**"
    add_summary "- files above the ${MAX_TARGET_MEGABYTES} MB limit, skipped by the scanner and listed here rather than dropped silently: $([ -z "$OVERSIZED" ] && echo "none" || printf '%s' "$(printf '%s' "$OVERSIZED" | grep -c . || true) — see \`oversizedSkipped\` in the sanitized report")"
    add_summary ""
    add_summary "Not covered by this scan, and not claimed: .NET metadata heaps and short provider credentials. The provider-authentication structural audit covers those."

    if [ "$TREE_COUNT" -ne 0 ]; then
        findings "the current tree contains $TREE_COUNT finding(s); see the sanitized report, which carries no value"
    fi
    clean "the current tree contains no findings"
fi

# --------------------------------------------------------------------------
# History mode
# --------------------------------------------------------------------------
# BOTH history passes run against a scratch MIRROR of the repository rather than
# against the checkout itself, and the reason is a real Gitleaks behaviour rather
# than tidiness: `--gitleaks-ignore-path` ADDS an ignore file, it does not
# replace the one Gitleaks reads implicitly from the scan target's own root. On a
# checkout that has just committed .gitleaksignore, a "raw" scan pointed at that
# checkout silently applies the very baseline it is supposed to be compared
# against, and reports zero findings — which would then be read as "history is
# clean" instead of "the comparison never happened".
#
# A `--mirror --shared` clone has no working tree, so no file is picked up
# implicitly, and it copies every ref verbatim (`refs/*`), so the ref set is
# identical. `--shared` means objects are not copied.
#
# The detached HEAD is then written into the mirror as an explicit ref. A mirror
# clone does not reliably preserve a detached HEAD, and a detached HEAD is
# exactly what actions/checkout leaves behind for a pull request — so the commit
# under review would be the one commit the scan missed.
MIRROR="$RAW_DIR/mirror.git"
if ! git clone --quiet --mirror --shared "$REPO" "$MIRROR" 2>"$RAW_DIR/mirror-stderr.txt"; then
    # git's own diagnosis, not a guess. The mirror is scratch state built from
    # already-public refs, so its stderr carries no secret.
    indeterminate "could not create the scratch mirror needed for an unsuppressed history scan: $(tr '\n' ' ' <"$RAW_DIR/mirror-stderr.txt")"
fi
git -C "$MIRROR" update-ref refs/scan/detached-head "$HEAD_SHA" \
    || indeterminate "could not record the checked-out HEAD in the scratch mirror"
[ "$(git -C "$MIRROR" rev-parse refs/scan/detached-head)" = "$HEAD_SHA" ] \
    || indeterminate "the checked-out HEAD is not present in the scanned ref set"

# Every OTHER linked worktree's HEAD too. `git rev-list --all` walks them, so a
# mirror that omitted them would reach fewer commits than the checkout and the
# completeness assertion below would fail for a reason that has nothing to do
# with secrets. Recording them instead of excusing them also means a credential
# sitting on a colleague's local worktree HEAD is scanned rather than skipped.
worktree_index=0
while read -r worktree_head; do
    [ -n "$worktree_head" ] || continue
    worktree_index=$((worktree_index + 1))
    git -C "$MIRROR" update-ref "refs/scan/worktree-$worktree_index" "$worktree_head" \
        || indeterminate "could not record linked worktree HEAD $worktree_head in the scratch mirror"
done <<EOF
$(git -C "$REPO" worktree list --porcelain 2>/dev/null | awk '$1 == "HEAD" { print $2 }')
EOF
MIRROR_COMMITS="$(git -C "$MIRROR" rev-list --all --count)"
[ "$MIRROR_COMMITS" -ge "$COMMIT_COUNT" ] \
    || indeterminate "the scratch mirror reaches $MIRROR_COMMITS commits but the checkout reaches $COMMIT_COUNT; the mirror is incomplete"

# `--log-opts --all` makes git list every ref under refs/ AND HEAD, so every
# fetched branch, every tag and the recorded detached HEAD are all included.
RAW_RAWSCAN="$RAW_DIR/history-raw.json"
rc="$(run_gitleaks git "$RAW_RAWSCAN" "$EMPTY_IGNORE_DIR" "$MIRROR" --log-opts "--all")"
evaluate "$rc" "$RAW_RAWSCAN" "complete-history scan"
OBSERVED_COUNT="$COUNT"

OBSERVED_FP="$RAW_DIR/observed.txt"
jq -r '.[].Fingerprint' "$RAW_RAWSCAN" | LC_ALL=C sort -u >"$OBSERVED_FP"
OBSERVED_UNIQUE="$(wc -l <"$OBSERVED_FP" | tr -d ' ')"

BASELINE_SORTED="$RAW_DIR/baseline-sorted.txt"
LC_ALL=C sort -u "$BASELINE_FINGERPRINTS" >"$BASELINE_SORTED"

NEW_FP="$(LC_ALL=C comm -23 "$OBSERVED_FP" "$BASELINE_SORTED" | wc -l | tr -d ' ')"
STALE_FP="$(LC_ALL=C comm -13 "$OBSERVED_FP" "$BASELINE_SORTED" | wc -l | tr -d ' ')"

SANITIZED="$OUT_DIR/secret-scan-history.json"
sanitize "$RAW_RAWSCAN" "$RAW_DIR/history-findings.json" "history" "new" \
    || indeterminate "the sanitizer failed; no report may be published unsanitized"

jq -n --arg repo "$REPO_NAME" --arg mode history \
      --arg verdict "$([ "$NEW_FP" -eq 0 ] && [ "$STALE_FP" -eq 0 ] && echo CLEAN || echo FINDINGS)" \
      --arg version "$EXPECTED_VERSION" --arg release "$EXPECTED_RELEASE_COMMIT" \
      --arg archive "$EXPECTED_ARCHIVE_SHA256" --arg head "$HEAD_SHA" \
      --argjson decode "$MAX_DECODE_DEPTH" --argjson archiveDepth "$MAX_ARCHIVE_DEPTH" \
      --argjson megabytes "$MAX_TARGET_MEGABYTES" --argjson timeout "$SCAN_TIMEOUT_SECONDS" \
      --argjson observed "$OBSERVED_COUNT" --argjson unique "$OBSERVED_UNIQUE" \
      --argjson baseline "$BASELINE_COUNT" --argjson structural "$STRUCTURAL_COUNT" \
      --argjson new "$NEW_FP" --argjson stale "$STALE_FP" \
      --argjson refs "$REF_COUNT" --argjson commits "$COMMIT_COUNT" \
      --argjson findings "$(cat "$RAW_DIR/history-findings.json")" '
    {repository: $repo, scanMode: $mode, verdict: $verdict, headCommit: $head,
     scanner: {version: $version, releaseCommit: $release, archiveSha256: $archive},
     limits: {maxDecodeDepth: $decode, maxArchiveDepth: $archiveDepth,
              maxTargetMegabytes: $megabytes, timeoutSeconds: $timeout},
     refs: {localRefs: $refs, reachableCommits: $commits},
     baselineApplied: false,
     counts: {observed: $observed, observedUnique: $unique, baseline: $baseline,
              structuralEntries: $structural, new: $new, stale: $stale},
     findings: $findings}' >"$SANITIZED" \
    || indeterminate "could not write the sanitized report"

add_summary "Complete-history scan of every fetched ref plus the detached HEAD (\`git log --all\`), run **without** \`.gitleaksignore\` so the raw fingerprint set can be compared exactly against the committed baseline."
add_summary ""
add_summary "- reachable commits: ${COMMIT_COUNT} across ${REF_COUNT} local refs"
add_summary "- raw findings: **${OBSERVED_COUNT}** (${OBSERVED_UNIQUE} unique fingerprints)"
add_summary "- committed baseline: **${BASELINE_COUNT}** exact fingerprints, plus ${STRUCTURAL_COUNT} structural entries the scanner is blind to"
add_summary "- observed but not baselined: **${NEW_FP}**"
add_summary "- baselined but no longer observed (stale): **${STALE_FP}**"

if [ "$NEW_FP" -ne 0 ]; then
    findings "$NEW_FP historical finding(s) are not in the committed baseline; each needs an owner disposition"
fi
if [ "$STALE_FP" -ne 0 ]; then
    indeterminate "$STALE_FP baseline fingerprint(s) no longer match any observed finding; the baseline is stale and cannot be trusted"
fi

# Second pass, with .gitleaksignore applied. This is the invocation an ordinary
# `gitleaks` run performs, and it must be empty — otherwise the committed
# suppression does not actually cover the history it claims to.
RAW_BASELINED="$RAW_DIR/history-baselined.json"
rc="$(run_gitleaks git "$RAW_BASELINED" "$IGNORE_FILE" "$MIRROR" --log-opts "--all")"
evaluate "$rc" "$RAW_BASELINED" "baselined history scan"
BASELINED_COUNT="$COUNT"

add_summary "- with \`.gitleaksignore\` applied: **${BASELINED_COUNT}**"

if [ "$BASELINED_COUNT" -ne 0 ]; then
    findings "$BASELINED_COUNT finding(s) survive the committed baseline"
fi

add_summary ""
add_summary "Every baselined entry is an exact \`<commit>:<path>:<rule>:<line>\` fingerprint. There is no glob, no path-wide exception, no rule-wide exception, no regex, and no in-band allow comment in any tracked file."
clean "the complete history matches the committed baseline exactly and nothing survives it"
