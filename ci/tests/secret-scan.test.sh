#!/usr/bin/env bash
#
# Deterministic controls for ci/secret-scan.sh.
#
# A secret-scanning gate is worth exactly as much as its willingness to refuse.
# Every control below asserts BOTH the exit status and the classification the
# script printed, because a script that returns the right number for the wrong
# reason is not a gate — it is a coincidence.
#
# WHERE THE FIXTURES LIVE, AND WHY IT MATTERS.
#
# Nothing credential-shaped is committed to this repository. Every synthetic
# credential below is assembled at run time from fragments held in separate
# variables, and every one is written only into a `mktemp -d` repository that is
# deleted on exit. A test file containing a literal credential-shaped token would
# make the gate flag its own control suite, and — worse — that token would then
# live in this repository's history, reachable from a fetched ref, where it would
# appear as a new historical fingerprint and destroy the baseline's exactness.
#
# Shell tracing is deliberately NOT enabled anywhere near a fixture. `set -x`
# around these functions would print every synthetic value into the job log,
# which is precisely the failure this gate exists to prevent.
#
# THE AWS FIXTURE IS NOT ARBITRARY. Two upstream behaviours were established by
# experiment against the pinned binary, not assumed from the rule text, and the
# EFFICACY controls below re-prove both on every run:
#
#   * the tail of the access-key rule accepts a restricted alphabet — a body
#     containing 0, 1, 8 or 9 does not match at all;
#   * the upstream default configuration allowlists the canonical documentation
#     key, so the value every tutorial uses is silently not a finding.
#
# A control built on either shape would go green while proving nothing, and would
# read as "the scanner is broken" the first time someone looked.

set -uo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
CI_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd -- "$CI_DIR/.." && pwd)"
SCAN="$CI_DIR/secret-scan.sh"
INSTALL="$CI_DIR/install-gitleaks.sh"

RUN_LIVE=1
[ "${1:-}" = "--no-live" ] && RUN_LIVE=0

PASSED=0
FAILED=0
FAILURES=()

WORK="$(mktemp -d)"
# shellcheck disable=SC2317  # invoked by the trap below, not inline
cleanup() { chmod -R u+w "$WORK" 2>/dev/null; rm -rf "$WORK"; }
trap cleanup EXIT INT TERM

pass() { PASSED=$((PASSED + 1)); printf '  ok    %s\n' "$1"; }
fail() {
    FAILED=$((FAILED + 1))
    FAILURES+=("$1")
    printf '  FAIL  %s\n     -> %s\n' "$1" "$2"
}

# Assert both halves of the contract: the exit status AND the classification the
# script announced. Checking only the number would let INDETERMINATE masquerade
# as a considered verdict.
#
#   assert_verdict <label> <expected-code> <expected-class> -- <command...>
assert_verdict() {
    local label="$1" want_code="$2" want_class="$3"; shift 4
    local out rc
    out="$("$@" 2>&1)"
    rc=$?
    if [ "$rc" -ne "$want_code" ]; then
        # The reason matters as much as the number. A control that reports only
        # "expected 0, got 2" cannot be diagnosed from a CI log without a second
        # round trip, so the evaluator's own last line is carried through.
        fail "$label" "expected exit $want_code, got $rc; script said: $(printf '%s' "$out" | tail -1)"
        return
    fi
    if ! printf '%s' "$out" | grep -q "secret-scan: ${want_class}:"; then
        case "$want_class" in
            INDETERMINATE)
                # install-gitleaks.sh announces itself under its own name.
                if printf '%s' "$out" | grep -q "install-gitleaks: INDETERMINATE:"; then
                    pass "$label"; return
                fi ;;
        esac
        fail "$label" "expected classification $want_class; script said: $(printf '%s' "$out" | tail -1)"
        return
    fi
    pass "$label"
}

assert_no_control_value_leaked() {
    local label="$1" file="$2"; shift 2
    local value
    for value in "$@"; do
        if grep -qF -- "$value" "$file" 2>/dev/null; then
            fail "$label" "a synthetic control value appears in $file"
            return
        fi
    done
    pass "$label"
}

# --------------------------------------------------------------------------
# Synthetic credential fixtures. Assembled, never written literally.
# --------------------------------------------------------------------------
aws_key() {           local a='AK' b='IA' c='QRSTUVWXYZ' d='234567';  printf '%s%s%s%s' "$a" "$b" "$c" "$d"; }
aws_key_alt() {       local a='AK' b='IA' c='ZXCVBNMASDF' d='GHJKL';  printf '%s%s%s%s' "$a" "$b" "$c" "$d"; }
aws_key_bad_alpha() { local a='AK' b='IA' c='QRSTUVWXYZ' d='012345';  printf '%s%s%s%s' "$a" "$b" "$c" "$d"; }
aws_key_doc() {       local a='AK' b='IA' c='IOSFODNN7' d='EXAMPLE';  printf '%s%s%s%s' "$a" "$b" "$c" "$d"; }
github_pat() {
    local a='gh' b='p_' c='A1b2C3d4E5f6G7h8' d='I9j0K1l2M3n4O5p6' e='Q7r8'
    printf '%s%s%s%s%s' "$a" "$b" "$c" "$d" "$e"
}
private_key() {
    openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 2>/dev/null
}

# --------------------------------------------------------------------------
# Disposable repositories. Nothing here ever touches the project's own history.
# --------------------------------------------------------------------------
# Each call must yield a genuinely new repository. The name comes from mktemp
# rather than a counter: new_repo is invoked inside a command substitution, so a
# counter would increment in a subshell, every control would silently share one
# directory, and findings would accumulate across unrelated controls.
new_repo() {
    local dir
    dir="$(mktemp -d "$WORK/repo-XXXXXXXX")"
    mkdir -p "$dir/ci"
    git init --quiet --initial-branch=main "$dir" >/dev/null 2>&1
    git -C "$dir" config user.email "controls@example.invalid" >/dev/null 2>&1
    git -C "$dir" config user.name "secret-scan controls" >/dev/null 2>&1
    git -C "$dir" config commit.gpgsign false >/dev/null 2>&1
    printf 'title = "control"\n\n[extend]\nuseDefault = true\n' >"$dir/.gitleaks.toml"
    : >"$dir/.gitleaksignore"
    write_baseline "$dir"
    printf 'ordinary project content\n' >"$dir/README.md"
    git -C "$dir" add -A >/dev/null 2>&1
    git -C "$dir" commit --quiet -m "initial" >/dev/null 2>&1
    printf '%s' "$dir"
}

# $1 repo, $2.. fingerprints (already sorted by the caller)
write_baseline() {
    local dir="$1"; shift
    local entries='[]'
    local fp
    for fp in "$@"; do
        entries="$(printf '%s' "$entries" | jq --arg fp "$fp" '. + [{
            fingerprint: $fp, ruleId: ($fp|split(":")[2]), path: ($fp|split(":")[1]),
            line: ($fp|split(":")[3]|tonumber), commit: ($fp|split(":")[0]),
            commitDate: "1970-01-01T00:00:00Z", provenance: "synthetic control",
            classification: "control", disposition: "synthetic control fixture"}]')"
    done
    jq -n --argjson g "$entries" '{repository: "control", authorizedDisposition: "synthetic control fixture",
        gitleaks: $g, structural: []}' >"$dir/ci/secret-history-baseline.json"
    printf '%s\n' "$@" | grep -v '^$' >"$dir/.gitleaksignore" || : >"$dir/.gitleaksignore"
}

commit_file() {
    local dir="$1" path="$2" content="$3" message="$4"
    mkdir -p "$(dirname -- "$dir/$path")"
    printf '%s\n' "$content" >"$dir/$path"
    git -C "$dir" add -A >/dev/null 2>&1
    git -C "$dir" commit --quiet -m "$message" >/dev/null 2>&1
}

# shellcheck disable=SC2317  # invoked indirectly, through assert_verdict's "$@"
scan() {
    local mode="$1" dir="$2"; shift 2
    "$SCAN" --mode "$mode" --repo "$dir" --repo-name control --bin "$GITLEAKS_BIN" \
        --config "$dir/.gitleaks.toml" --baseline "$dir/ci/secret-history-baseline.json" \
        --ignore "$dir/.gitleaksignore" --out "$WORK/out" --default-branch main \
        --timeout-seconds 60 "$@"
}

# A stub that impersonates the pinned scanner, with its provenance file written
# to match, so that everything DOWNSTREAM of the identity check can be exercised.
# The identity check itself has its own controls.
#   $1 behaviour, $2 reported version
make_stub() {
    local behaviour="$1" version="${2:-8.30.0}"
    local dir="$WORK/stub-$behaviour-$version"
    mkdir -p "$dir"
    cat >"$dir/gitleaks" <<STUB
#!/usr/bin/env bash
if [ "\${1:-}" = "version" ]; then printf '$version\n'; exit 0; fi
report=""
prev=""
for a in "\$@"; do
  if [ "\$prev" = "--report-path" ]; then report="\$a"; fi
  prev="\$a"
done
case "$behaviour" in
  crash)        printf 'stub: internal error\n' >&2; exit 3 ;;
  hang)         sleep 3600 ;;
  no-report)    exit 7 ;;
  malformed)    printf '{ this is not json' >"\$report"; exit 7 ;;
  zero-with-findings)
                printf '[{"RuleID":"x","File":"f","StartLine":1,"Commit":"","Fingerprint":"f:x:1"}]' >"\$report"; exit 0 ;;
  findings-empty)
                printf '[]' >"\$report"; exit 7 ;;
  leaky)        cat >"\$report" <<'JSON'
[{"RuleID":"aws-access-token","File":"leak.txt","StartLine":3,"Commit":"1111111111111111111111111111111111111111","Fingerprint":"1111111111111111111111111111111111111111:leak.txt:aws-access-token:3","Secret":"__CONTROL_VALUE__","Match":"key = __CONTROL_VALUE__","Line":"key = __CONTROL_VALUE__","Entropy":4.2,"Author":"Someone Real","Email":"someone@example.invalid","Message":"add the key","Description":"AWS Access Token","Link":"https://example.invalid","StartColumn":7,"EndColumn":27}]
JSON
                exit 7 ;;
  *)            printf '[]' >"\$report"; exit 0 ;;
esac
STUB
    chmod 0755 "$dir/gitleaks"
    printf 'version %s\nrelease_commit 6eaad039603a4de39fddd1cf5f727391efe9974e\narchive gitleaks_8.30.0_linux_x64.tar.gz\narchive_sha256 79a3ab579b53f71efd634f3aaf7e04a0fa0cf206b7ed434638d1547a2470a66e\nbinary_sha256 %s\n' \
        "$version" "$(sha256sum "$dir/gitleaks" | cut -d' ' -f1)" >"$dir/gitleaks.provenance"
    printf '%s' "$dir/gitleaks"
}

# --------------------------------------------------------------------------
# The scanner under test
# --------------------------------------------------------------------------
printf '\n== scanner ==\n'
GITLEAKS_BIN="${GITLEAKS_BIN:-$("$INSTALL" --print-bin)}"
if [ ! -x "$GITLEAKS_BIN" ]; then
    "$INSTALL" >/dev/null || { printf 'cannot install the pinned scanner\n' >&2; exit 2; }
    GITLEAKS_BIN="$("$INSTALL" --print-bin)"
fi
for tool in jq git openssl sha256sum timeout; do
    command -v "$tool" >/dev/null 2>&1 || { printf 'missing required tool: %s\n' "$tool" >&2; exit 2; }
done
printf '  using %s (%s)\n' "$GITLEAKS_BIN" "$("$GITLEAKS_BIN" version)"
mkdir -p "$WORK/out"

# --------------------------------------------------------------------------
# EFFICACY — does the pinned binary detect what we claim it detects?
# Run first: every FINDINGS control below depends on these answers.
# --------------------------------------------------------------------------
printf '\n== efficacy: the pinned scanner detects the shapes the controls rely on ==\n'
efficacy_dir="$WORK/efficacy"
mkdir -p "$efficacy_dir"
printf 'title="e"\n\n[extend]\nuseDefault = true\n' >"$efficacy_dir/.gitleaks.toml"
probe() {
    local label="$1" content="$2" want="$3" rule="${4:-}"
    local d="$WORK/probe-$RANDOM"
    mkdir -p "$d"
    printf 'title="e"\n\n[extend]\nuseDefault = true\n' >"$d/.gitleaks.toml"
    printf '%s\n' "$content" >"$d/probe.txt"
    "$GITLEAKS_BIN" dir "$d" --config "$d/.gitleaks.toml" --report-format json \
        --report-path "$d/r.json" --exit-code 7 --no-banner --redact --log-level error \
        --max-decode-depth 3 >/dev/null 2>&1
    local n
    if [ -n "$rule" ]; then
        n="$(jq --arg r "$rule" '[.[] | select(.RuleID == $r)] | length' "$d/r.json" 2>/dev/null || echo -1)"
    else
        n="$(jq 'length' "$d/r.json" 2>/dev/null || echo -1)"
    fi
    if [ "$want" = "detected" ] && [ "$n" -ge 1 ]; then pass "$label"
    elif [ "$want" = "missed" ] && [ "$n" -eq 0 ]; then pass "$label"
    else fail "$label" "expected $want, report held $n finding(s)"; fi
    rm -rf "$d"
}
probe "efficacy: a valid synthetic AWS access key is detected" "key = \"$(aws_key)\"" detected aws-access-token
probe "efficacy: a valid synthetic GitHub PAT is detected" "token = \"$(github_pat)\"" detected github-pat
probe "efficacy: a generated private key is detected" "$(private_key)" detected private-key
probe "efficacy: a base64-encoded credential is detected at decode depth 3" \
      "blob = \"$(printf 'aws = "%s"' "$(aws_key)" | base64 -w0)\"" detected
probe "efficacy: a doubly base64-encoded credential is detected at decode depth 3" \
      "blob = \"$(printf 'aws = "%s"' "$(aws_key)" | base64 -w0 | base64 -w0)\"" detected
# The two negatives that make the positives meaningful.
probe "efficacy: an AWS key whose body leaves the accepted alphabet is NOT matched by the AWS rule" \
      "key = \"$(aws_key_bad_alpha)\"" missed aws-access-token
probe "efficacy: the upstream-allowlisted documentation AWS key is NOT matched by the AWS rule" \
      "key = \"$(aws_key_doc)\"" missed aws-access-token

# --------------------------------------------------------------------------
# CLEAN
# --------------------------------------------------------------------------
printf '\n== CLEAN ==\n'
clean_repo="$(new_repo)"
assert_verdict "CLEAN: a clean current tree" 0 CLEAN -- scan tree "$clean_repo"
assert_verdict "CLEAN: a clean history" 0 CLEAN -- scan history "$clean_repo"

# An exact historical baseline: commit a credential, baseline its precise
# fingerprint, and require the history scan to go green on that and only that.
baselined_repo="$(new_repo)"
commit_file "$baselined_repo" "legacy/config.txt" "aws_key = \"$(aws_key)\"" "inherited"
legacy_fp="$("$GITLEAKS_BIN" git "$baselined_repo" --config "$baselined_repo/.gitleaks.toml" \
    --log-opts "--all" --report-format json --report-path "$WORK/legacy.json" --exit-code 7 \
    --no-banner --redact --log-level error >/dev/null 2>&1; jq -r '.[0].Fingerprint' "$WORK/legacy.json")"
write_baseline "$baselined_repo" "$legacy_fp"
assert_verdict "CLEAN: history matching an exact baseline entry for entry" 0 CLEAN -- scan history "$baselined_repo"

# Provider endpoints that are NOT credentials. These are the shapes this project
# actually ships, and a gate that reddened on them would be turned off within a
# week.
endpoints_repo="$(new_repo)"
commit_file "$endpoints_repo" "src/anonymous.txt" \
    'const base = "https://musicbrainz.org/ws/2/artist/?fmt=json&query=name:example";' "anonymous endpoint"
commit_file "$endpoints_repo" "src/configured.txt" \
    'var url = $"{Host}/3/movie/{id}?api_key={configuration.TmdbApiKey}&language={lang}";' "operator-configured endpoint"
commit_file "$endpoints_repo" "src/params.txt" \
    'GET /Items?SortBy=SortName&Recursive=true&Limit=100&Fields=Overview,Genres&userId=abc' "ordinary query parameters"
assert_verdict "CLEAN: an anonymous provider endpoint is not a credential" 0 CLEAN -- scan tree "$endpoints_repo"
assert_verdict "CLEAN: an operator-configured provider endpoint is not a credential" 0 CLEAN -- scan history "$endpoints_repo"
assert_verdict "CLEAN: ordinary non-authentication query parameters are not credentials" 0 CLEAN -- scan tree "$endpoints_repo"

# --------------------------------------------------------------------------
# FINDINGS
# --------------------------------------------------------------------------
printf '\n== FINDINGS ==\n'
aws_repo="$(new_repo)"
commit_file "$aws_repo" "src/aws.txt" "aws_access_key_id = $(aws_key)" "aws"
assert_verdict "FINDINGS: a valid synthetic AWS key" 1 FINDINGS -- scan tree "$aws_repo"

pat_repo="$(new_repo)"
commit_file "$pat_repo" "src/pat.txt" "token: $(github_pat)" "pat"
assert_verdict "FINDINGS: a valid synthetic GitHub PAT" 1 FINDINGS -- scan tree "$pat_repo"

pk_repo="$(new_repo)"
commit_file "$pk_repo" "src/id_rsa" "$(private_key)" "private key"
assert_verdict "FINDINGS: a generated private key" 1 FINDINGS -- scan tree "$pk_repo"

b64_repo="$(new_repo)"
commit_file "$b64_repo" "src/blob.txt" "payload = \"$(printf 'aws = "%s"' "$(aws_key)" | base64 -w0)\"" "encoded"
assert_verdict "FINDINGS: a base64-encoded credential, found by bounded decoding" 1 FINDINGS -- scan tree "$b64_repo"

# Uncommitted, working tree only — the case a history scan cannot see.
tree_only_repo="$(new_repo)"
printf 'key = %s\n' "$(aws_key)" >"$tree_only_repo/src-uncommitted.txt"
assert_verdict "FINDINGS: a credential present only in the current tree" 1 FINDINGS -- scan tree "$tree_only_repo"

# Committed then deleted — invisible to a tree scan, which is why history is scanned.
deleted_repo="$(new_repo)"
commit_file "$deleted_repo" "src/oops.txt" "key = $(aws_key)" "add"
git -C "$deleted_repo" rm --quiet "src/oops.txt" && git -C "$deleted_repo" commit --quiet -m "remove"
assert_verdict "FINDINGS: a credential committed and then deleted" 1 FINDINGS -- scan history "$deleted_repo"
assert_verdict "CLEAN: the same repository's current tree, after the deletion" 0 CLEAN -- scan tree "$deleted_repo"

# On a detached HEAD — what actions/checkout produces for a pull request, and the
# one commit a naive `--branches` scan would miss.
detached_repo="$(new_repo)"
commit_file "$detached_repo" "src/detached.txt" "key = $(aws_key_alt)" "on a side commit"
detached_sha="$(git -C "$detached_repo" rev-parse HEAD)"
git -C "$detached_repo" reset --quiet --hard HEAD~1
git -C "$detached_repo" checkout --quiet --detach "$detached_sha"
assert_verdict "FINDINGS: a credential on a detached HEAD reachable from no branch" 1 FINDINGS -- scan history "$detached_repo"

branch_repo="$(new_repo)"
git -C "$branch_repo" checkout --quiet -b side
commit_file "$branch_repo" "src/side.txt" "key = $(aws_key)" "on a side branch"
git -C "$branch_repo" checkout --quiet main
assert_verdict "FINDINGS: a credential on another fetched branch" 1 FINDINGS -- scan history "$branch_repo"

# A new credential standing beside a baselined historical one. The baseline must
# excuse exactly one fingerprint and no more.
beside_repo="$(new_repo)"
commit_file "$beside_repo" "legacy/old.txt" "key = $(aws_key)" "inherited"
old_fp="$("$GITLEAKS_BIN" git "$beside_repo" --config "$beside_repo/.gitleaks.toml" --log-opts "--all" \
    --report-format json --report-path "$WORK/beside.json" --exit-code 7 --no-banner --redact \
    --log-level error >/dev/null 2>&1; jq -r '.[0].Fingerprint' "$WORK/beside.json")"
write_baseline "$beside_repo" "$old_fp"
assert_verdict "CLEAN: the baselined historical finding alone" 0 CLEAN -- scan history "$beside_repo"
commit_file "$beside_repo" "src/new.txt" "key = $(aws_key_alt)" "newly introduced"
assert_verdict "FINDINGS: a new credential beside a baselined historical finding" 1 FINDINGS -- scan history "$beside_repo"

# The same historical VALUE reappearing in today's tree. Its historical
# fingerprint is baselined; the tree scan must still report it. This is the
# control the whole baseline design rests on.
recur_repo="$(new_repo)"
commit_file "$recur_repo" "legacy/old.txt" "key = $(aws_key)" "inherited"
recur_fp="$("$GITLEAKS_BIN" git "$recur_repo" --config "$recur_repo/.gitleaks.toml" --log-opts "--all" \
    --report-format json --report-path "$WORK/recur.json" --exit-code 7 --no-banner --redact \
    --log-level error >/dev/null 2>&1; jq -r '.[0].Fingerprint' "$WORK/recur.json")"
write_baseline "$recur_repo" "$recur_fp"
git -C "$recur_repo" rm --quiet "legacy/old.txt" && git -C "$recur_repo" commit --quiet -m "remove"
printf 'key = %s\n' "$(aws_key)" >"$recur_repo/src/today.txt" 2>/dev/null \
    || { mkdir -p "$recur_repo/src"; printf 'key = %s\n' "$(aws_key)" >"$recur_repo/src/today.txt"; }
assert_verdict "FINDINGS: a current-tree recurrence of an otherwise baselined historical value" \
    1 FINDINGS -- scan tree "$recur_repo"

# The short provider credential. This is NOT a Gitleaks control and must not be
# presented as one: six and eight character keys are beneath any entropy or
# length rule. It is proved by the provider-authentication structural audit, and
# this control asserts that the audit is present and wired into the normal test
# run rather than duplicating its red/green evidence here.
if [ -f "$REPO_ROOT/ci/provider-auth-inventory.json" ]; then
    if jq -e '(.providers | length) >= 1' "$REPO_ROOT/ci/provider-auth-inventory.json" >/dev/null 2>&1 \
       || jq -e 'length >= 1' "$REPO_ROOT/ci/provider-auth-inventory.json" >/dev/null 2>&1; then
        pass "FINDINGS: short provider credentials are delegated to the provider-authentication structural audit (inventory present)"
    else
        fail "FINDINGS: short provider credential delegation" "ci/provider-auth-inventory.json is empty"
    fi
    probe "efficacy: a six-character provider key is NOT reachable by any Gitleaks rule (hence the structural audit)" \
          'BaseUrl = "https://theaudiodb.com/api/v1/json/" + "abc123" + "/";' missed
else
    pass "FINDINGS: short provider credentials — no provider-authentication inventory in this repository; the server repository owns that gate"
fi

# --------------------------------------------------------------------------
# INDETERMINATE — the refusals
# --------------------------------------------------------------------------
printf '\n== INDETERMINATE ==\n'

shallow_src="$(new_repo)"
commit_file "$shallow_src" "a.txt" "one" "second"
commit_file "$shallow_src" "b.txt" "two" "third"
shallow_repo="$WORK/shallow"
git clone --quiet --depth 1 "file://$shallow_src" "$shallow_repo" 2>/dev/null
cp "$shallow_src/.gitleaks.toml" "$shallow_repo/.gitleaks.toml" 2>/dev/null
mkdir -p "$shallow_repo/ci"; cp "$shallow_src/ci/secret-history-baseline.json" "$shallow_repo/ci/" 2>/dev/null
: >"$shallow_repo/.gitleaksignore"
assert_verdict "INDETERMINATE: a shallow clone" 2 INDETERMINATE -- scan history "$shallow_repo"

# Regression control, and it caught a real defect: the shallow-boundary probe
# built its path from `rev-parse --git-dir`, which answers RELATIVELY, so it
# tested the CALLER's working directory instead of the repository under scan.
# Running from inside a shallow checkout therefore made every history scan
# report a perfectly complete repository as truncated. The Controls job checks
# out at depth 1, which is exactly that situation, so this runs in it for real.
shallow_cwd="$WORK/shallow-cwd"
git clone --quiet --depth 1 "file://$shallow_src" "$shallow_cwd" >/dev/null 2>&1
if [ -f "$shallow_cwd/.git/shallow" ]; then
    caller_cwd="$PWD"
    cd "$shallow_cwd" || { fail "CLEAN: caller-directory independence" "cannot enter the shallow working directory"; exit 1; }
    assert_verdict "CLEAN: a complete repository is not called shallow just because the CALLER's directory is" \
        0 CLEAN -- scan history "$clean_repo"
    cd "$caller_cwd" || exit 1
else
    fail "CLEAN: caller-directory independence" "could not build a shallow working directory to run from"
fi

missing_ref_repo="$(new_repo)"
assert_verdict "INDETERMINATE: the expected default-branch ref is absent" 2 INDETERMINATE -- \
    "$SCAN" --mode history --repo "$missing_ref_repo" --repo-name control --bin "$GITLEAKS_BIN" \
    --config "$missing_ref_repo/.gitleaks.toml" --baseline "$missing_ref_repo/ci/secret-history-baseline.json" \
    --ignore "$missing_ref_repo/.gitleaksignore" --out "$WORK/out" --default-branch no-such-branch

assert_verdict "INDETERMINATE: an incomplete history, below the expected commit floor" 2 INDETERMINATE -- \
    scan history "$clean_repo" --min-commits 100000

assert_verdict "INDETERMINATE: too few refs, an incomplete ref fetch" 2 INDETERMINATE -- \
    scan history "$clean_repo" --min-refs 500

assert_verdict "INDETERMINATE: a missing repository" 2 INDETERMINATE -- \
    scan tree "$WORK/does-not-exist"

# Installer refusals — verify BEFORE execute.
corrupt="$WORK/corrupt.tar.gz"
printf 'not the pinned archive' >"$corrupt"
assert_verdict "INDETERMINATE: an archive whose checksum does not match the pin" 2 INDETERMINATE -- \
    "$INSTALL" --bin-dir "$WORK/badinstall" --source "$corrupt" --force

assert_verdict "INDETERMINATE: a scanner reporting the wrong version" 2 INDETERMINATE -- \
    scan tree "$clean_repo" --bin "$(make_stub quiet 8.29.0)"

# The binary replaced after installation: provenance no longer matches its bytes.
tampered_dir="$WORK/tampered"; mkdir -p "$tampered_dir"
cp "$GITLEAKS_BIN" "$tampered_dir/gitleaks"
printf 'version 8.30.0\nrelease_commit 6eaad039603a4de39fddd1cf5f727391efe9974e\narchive gitleaks_8.30.0_linux_x64.tar.gz\narchive_sha256 79a3ab579b53f71efd634f3aaf7e04a0fa0cf206b7ed434638d1547a2470a66e\nbinary_sha256 %s\n' \
    "0000000000000000000000000000000000000000000000000000000000000000" >"$tampered_dir/gitleaks.provenance"
assert_verdict "INDETERMINATE: a scanner binary that no longer matches its provenance record" 2 INDETERMINATE -- \
    scan tree "$clean_repo" --bin "$tampered_dir/gitleaks"

assert_verdict "INDETERMINATE: a scanner that crashes" 2 INDETERMINATE -- \
    scan tree "$clean_repo" --bin "$(make_stub crash)"

assert_verdict "INDETERMINATE: a scanner that exceeds its timeout" 2 INDETERMINATE -- \
    "$SCAN" --mode tree --repo "$clean_repo" --repo-name control --bin "$(make_stub hang)" \
    --config "$clean_repo/.gitleaks.toml" --baseline "$clean_repo/ci/secret-history-baseline.json" \
    --ignore "$clean_repo/.gitleaksignore" --out "$WORK/out" --timeout-seconds 2

assert_verdict "INDETERMINATE: an invalid invocation of the evaluator" 2 INDETERMINATE -- \
    "$SCAN" --mode tree --repo "$clean_repo" --no-such-flag

# The reason Gitleaks runs with a reserved findings exit code: an unknown flag
# must never be mistakable for a leak.
unknown_rc=0
"$GITLEAKS_BIN" dir "$clean_repo" --this-flag-does-not-exist >/dev/null 2>&1 || unknown_rc=$?
if [ "$unknown_rc" -ne 7 ] && [ "$unknown_rc" -ne 0 ]; then
    pass "INDETERMINATE: the scanner's unknown-flag status ($unknown_rc) is distinguishable from its findings status (7)"
else
    fail "INDETERMINATE: unknown-flag status" "an unknown flag exited $unknown_rc, which collides with a findings or clean verdict"
fi

assert_verdict "INDETERMINATE: a scanner that writes no report at all" 2 INDETERMINATE -- \
    scan tree "$clean_repo" --bin "$(make_stub no-report)"

assert_verdict "INDETERMINATE: a malformed, unparseable report" 2 INDETERMINATE -- \
    scan tree "$clean_repo" --bin "$(make_stub malformed)"

assert_verdict "INDETERMINATE: exit zero contradicted by a non-empty report" 2 INDETERMINATE -- \
    scan tree "$clean_repo" --bin "$(make_stub zero-with-findings)"

assert_verdict "INDETERMINATE: a findings exit contradicted by an empty report" 2 INDETERMINATE -- \
    scan tree "$clean_repo" --bin "$(make_stub findings-empty)"

# Baseline integrity.
bad_repo="$(new_repo)"
printf '{ "repository": "control", ' >"$bad_repo/ci/secret-history-baseline.json"
assert_verdict "INDETERMINATE: a malformed baseline" 2 INDETERMINATE -- scan tree "$bad_repo"

dup_repo="$(new_repo)"
dup_fp="1111111111111111111111111111111111111111:a.txt:generic-api-key:1"
write_baseline "$dup_repo" "$dup_fp" "$dup_fp"
assert_verdict "INDETERMINATE: a baseline with duplicate fingerprints" 2 INDETERMINATE -- scan tree "$dup_repo"

unsorted_repo="$(new_repo)"
write_baseline "$unsorted_repo" \
    "2222222222222222222222222222222222222222:b.txt:generic-api-key:1" \
    "1111111111111111111111111111111111111111:a.txt:generic-api-key:1"
assert_verdict "INDETERMINATE: an unsorted baseline" 2 INDETERMINATE -- scan tree "$unsorted_repo"

stale_repo="$(new_repo)"
write_baseline "$stale_repo" "3333333333333333333333333333333333333333:gone.txt:generic-api-key:9"
assert_verdict "INDETERMINATE: a stale baseline entry matching nothing observed" 2 INDETERMINATE -- \
    scan history "$stale_repo"

glob_repo="$(new_repo)"
jq -n '{repository:"control", authorizedDisposition:"x", gitleaks:[{fingerprint:"*:*:generic-api-key:*",
    ruleId:"generic-api-key", path:"*", line:1, commit:"*", commitDate:"1970-01-01T00:00:00Z",
    provenance:"x", classification:"x", disposition:"x"}], structural:[]}' \
    >"$glob_repo/ci/secret-history-baseline.json"
printf '*:*:generic-api-key:*\n' >"$glob_repo/.gitleaksignore"
assert_verdict "INDETERMINATE: a baseline entry that is a glob rather than an exact fingerprint" 2 INDETERMINATE -- \
    scan tree "$glob_repo"

drift_repo="$(new_repo)"
write_baseline "$drift_repo" "1111111111111111111111111111111111111111:a.txt:generic-api-key:1"
printf '2222222222222222222222222222222222222222:b.txt:generic-api-key:2\n' >"$drift_repo/.gitleaksignore"
assert_verdict "INDETERMINATE: .gitleaksignore and the baseline describing different sets" 2 INDETERMINATE -- \
    scan tree "$drift_repo"

allow_repo="$(new_repo)"
printf 'key = notasecret # %s%s\n' 'gitleaks:' 'allow' >"$allow_repo/src-allow.txt"
git -C "$allow_repo" add -A >/dev/null && git -C "$allow_repo" commit --quiet -m "in-band allow"
assert_verdict "INDETERMINATE: an in-band allow comment in a tracked file" 2 INDETERMINATE -- \
    scan tree "$allow_repo"

nocfg_repo="$(new_repo)"
rm -f "$nocfg_repo/.gitleaks.toml"
assert_verdict "INDETERMINATE: a missing configuration" 2 INDETERMINATE -- scan tree "$nocfg_repo"

weakcfg_repo="$(new_repo)"
printf 'title = "weak"\n\n[extend]\nuseDefault = false\n' >"$weakcfg_repo/.gitleaks.toml"
assert_verdict "INDETERMINATE: a configuration that does not extend the complete built-in ruleset" 2 INDETERMINATE -- \
    scan tree "$weakcfg_repo"

# Publication failure. The sanitized report cannot be written, so nothing is
# published — and the verdict refuses rather than silently going green.
blocked_out="$WORK/blocked-out"
mkdir -p "$blocked_out/secret-scan-tree.json"
assert_verdict "INDETERMINATE: the sanitized report cannot be written" 2 INDETERMINATE -- \
    "$SCAN" --mode tree --repo "$clean_repo" --repo-name control --bin "$GITLEAKS_BIN" \
    --config "$clean_repo/.gitleaks.toml" --baseline "$clean_repo/ci/secret-history-baseline.json" \
    --ignore "$clean_repo/.gitleaksignore" --out "$blocked_out"

# --------------------------------------------------------------------------
# REDACTION — the published report must carry no value, ever.
# --------------------------------------------------------------------------
printf '\n== redaction ==\n'
leak_out="$WORK/leak-out"; mkdir -p "$leak_out"
leak_bin="$(make_stub leaky)"
"$SCAN" --mode tree --repo "$clean_repo" --repo-name control --bin "$leak_bin" \
    --config "$clean_repo/.gitleaks.toml" --baseline "$clean_repo/ci/secret-history-baseline.json" \
    --ignore "$clean_repo/.gitleaksignore" --out "$leak_out" --summary "$leak_out/summary.md" \
    >"$leak_out/stdout.txt" 2>"$leak_out/stderr.txt"
if [ -f "$leak_out/secret-scan-tree.json" ]; then
    if jq -e '[.findings[] | keys[]] | unique | inside(["baselineStatus","classification","commit","fingerprint","line","path","ruleId"])' \
        "$leak_out/secret-scan-tree.json" >/dev/null 2>&1; then
        pass "redaction: the sanitized report carries only the permitted fields"
    else
        fail "redaction: permitted fields" "the sanitized report carries a field outside the allowed set"
    fi
    for f in "$leak_out/secret-scan-tree.json" "$leak_out/summary.md" "$leak_out/stdout.txt" "$leak_out/stderr.txt"; do
        assert_no_control_value_leaked "redaction: no control value in $(basename "$f")" "$f" \
            "__CONTROL_VALUE__" "Someone Real" "someone@example.invalid" "add the key"
    done
else
    fail "redaction: sanitized report" "no sanitized report was produced"
fi

# The raw report must not survive anywhere the job can reach.
if find "$leak_out" -name '*raw*' -o -name '*.tmp' | grep -q .; then
    fail "redaction: raw report" "a raw report artefact was published"
else
    pass "redaction: no raw report survives in the published directory"
fi

# --------------------------------------------------------------------------
# LIVE — this repository itself
# --------------------------------------------------------------------------
if [ "$RUN_LIVE" -eq 1 ]; then
    printf '\n== live: this repository ==\n'
    assert_verdict "live: the baseline artefacts are internally consistent" 0 CLEAN -- \
        "$SCAN" --mode baseline --repo "$REPO_ROOT" --bin "$GITLEAKS_BIN"
    assert_verdict "live: the current tree is clean" 0 CLEAN -- \
        "$SCAN" --mode tree --repo "$REPO_ROOT" --bin "$GITLEAKS_BIN" --out "$WORK/out"
else
    printf '\n== live: skipped (--no-live) ==\n'
fi

# --------------------------------------------------------------------------
printf '\n== summary ==\n'
printf '  passed: %s\n  failed: %s\n' "$PASSED" "$FAILED"
if [ "$FAILED" -ne 0 ]; then
    printf '\nfailing controls:\n'
    for f in "${FAILURES[@]}"; do printf '  - %s\n' "$f"; done
    exit 1
fi
exit 0
