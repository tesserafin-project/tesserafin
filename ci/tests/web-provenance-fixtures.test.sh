#!/usr/bin/env bash
#
# Synthetic-history controls for ci/verify-web-provenance.sh (C4-LH, #246).
#
# WHAT THIS ANSWERS THAT THE REAL PAIR CANNOT. The live server/web pair is, by construction,
# always in the one state where everything agrees. It can demonstrate that the gate says yes; it
# can never demonstrate what the gate says no to, and least of all can it demonstrate the property
# the whole schema-2 change rests on:
#
#   a web pin whose sourceCommit is NOT an ancestor of the server commit under test, but whose
#   canonical contract bytes are identical, must FAIL under schema 1 and PASS under schema 2.
#
# That is exactly the shape GitHub produces every time it squashes or rebases a pull request onto
# a branch with `required_linear_history`, and it cannot be staged against the real repositories
# without actually merging something. So it is staged here, against throwaway git repositories
# built from scratch in a temporary directory.
#
# TWO POSITIVE CONTROLS ARE LOAD-BEARING (2 and 3). If either ever passes under schema 1, the
# legacy semantics have been weakened and the suite fails. If either ever fails under schema 2,
# the repair does not work and R1-P is still blocked.
#
# NOTHING HERE TOUCHES THE REAL WORLD. No remote ref, no repository setting, no network, no port,
# no DNS lookup. Every fixture is a directory under a `mktemp -d` that is removed on exit. The web
# fixtures are plain directories rather than clones, because ci/verify-web-provenance.sh reads a
# web checkout as files — deciding WHICH web checkout is allowed is ci/verify-sdk-provenance.sh's
# job and is controlled in ci/tests/sdk-provenance.test.sh instead.
#
# Usage: ./ci/tests/web-provenance-fixtures.test.sh
# Exit status: 0 every control behaved as required, 1 otherwise.

# NOT `set -e`: most controls are EXPECTED to exit non-zero.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERIFIER="$REPO_ROOT/ci/verify-web-provenance.sh"
[ -x "$VERIFIER" ] || { echo "fixtures: $VERIFIER is not executable" >&2; exit 1; }

for tool in git jq sha256sum; do
    command -v "$tool" >/dev/null 2>&1 || { echo "fixtures: '$tool' is not on PATH" >&2; exit 1; }
done

FIXTURES="$(mktemp -d)"
trap 'rm -rf "$FIXTURES"' EXIT

failures=0
note()  { printf '%s\n' "$*"; }
error() { printf '::error::%s\n' "$*" >&2; failures=$((failures + 1)); }

# Fixed identity and dates so a fixture repository's commit SHAs depend only on its content, and a
# rerun on another machine builds the same histories.
git_fixture() {
    git -c user.name='C4-LH fixtures' -c user.email='fixtures@invalid' \
        -c commit.gpgsign=false -c init.defaultBranch=master "$@"
}
commit_at() {
    local repo="$1" date="$2" message="$3"
    GIT_AUTHOR_DATE="$date" GIT_COMMITTER_DATE="$date" \
        git_fixture -C "$repo" commit --quiet --no-verify -a -m "$message"
}

# --------------------------------------------------------------------------
# The server fixture.
#
#   C1 (contract A)
#    └── C2 (contract B)                  on branch `candidate` — "the pull request branch"
#   C1 (contract A)
#    └── R2 (contract B, different SHA)   on `master`           — "what a rebase merge produced"
#    └── S2 (contract B, different SHA)   on `squashed`         — "what a squash merge produced"
#
# C2 is not an ancestor of R2 or of S2, and all three carry byte-identical canonical contracts.
# That is the entire situation schema 2 exists to verify and schema 1 cannot.
# --------------------------------------------------------------------------
CONTRACT_A='{"openapi":"3.0.4","info":{"title":"Fixture API","version":"1.0.0"},"paths":{}}'
CONTRACT_B='{"openapi":"3.0.4","info":{"title":"Fixture API","version":"1.0.0"},"paths":{"/x":{}}}'

write_contract() {
    local repo="$1" body="$2"
    mkdir -p "$repo/openapi"
    printf '%s\n' "$body" > "$repo/openapi/openapi.json"
    local sha
    sha="$(sha256sum "$repo/openapi/openapi.json" | cut -d' ' -f1)"
    jq -n --arg s "$sha" '{algorithm:"sha256", sha256:$s, spec:"openapi/openapi.json", version:"1.0.0"}' \
        > "$repo/openapi/contract.lock.json"
}

SERVER="$FIXTURES/server"
mkdir -p "$SERVER"
git_fixture init --quiet "$SERVER"
write_contract "$SERVER" "$CONTRACT_A"
git_fixture -C "$SERVER" add -A
commit_at "$SERVER" '2026-01-01T00:00:00Z' 'contract A'
C1="$(git -C "$SERVER" rev-parse HEAD)"

git_fixture -C "$SERVER" checkout --quiet -b candidate
write_contract "$SERVER" "$CONTRACT_B"
git_fixture -C "$SERVER" add -A
commit_at "$SERVER" '2026-01-02T00:00:00Z' 'contract B (pull request branch)'
C2="$(git -C "$SERVER" rev-parse HEAD)"

# A rebase merge: same tree, new committer date, therefore a new SHA.
git_fixture -C "$SERVER" checkout --quiet master
write_contract "$SERVER" "$CONTRACT_B"
git_fixture -C "$SERVER" add -A
commit_at "$SERVER" '2026-01-03T00:00:00Z' 'contract B (pull request branch)'
R2="$(git -C "$SERVER" rev-parse HEAD)"

# A squash merge: same tree, one commit, a pull-request-shaped message.
git_fixture -C "$SERVER" checkout --quiet -b squashed "$C1"
write_contract "$SERVER" "$CONTRACT_B"
git_fixture -C "$SERVER" add -A
commit_at "$SERVER" '2026-01-04T00:00:00Z' 'contract B (#999)'
S2="$(git -C "$SERVER" rev-parse HEAD)"
git_fixture -C "$SERVER" checkout --quiet master

CONTRACT_B_SHA="$(git -C "$SERVER" show "$R2:openapi/openapi.json" | sha256sum | cut -d' ' -f1)"

# The situation the fixtures exist to stage — assert it rather than assume it.
git -C "$SERVER" merge-base --is-ancestor "$C2" "$R2" 2>/dev/null \
    && error "fixture setup is wrong: C2 must NOT be an ancestor of the rebase-merged master"
git -C "$SERVER" merge-base --is-ancestor "$C2" "$S2" 2>/dev/null \
    && error "fixture setup is wrong: C2 must NOT be an ancestor of the squash-merged branch"
[ "$(git -C "$SERVER" show "$C2:openapi/openapi.json" | sha256sum | cut -d' ' -f1)" = "$CONTRACT_B_SHA" ] \
    || error "fixture setup is wrong: C2 and R2 must carry byte-identical canonical contracts"
note "fixtures: C1=$C1 C2=$C2 (branch) R2=$R2 (rebase) S2=$S2 (squash)"

# --------------------------------------------------------------------------
# The web fixture. A schema-2 pin that is internally consistent by construction; every control
# below is a single deliberate mutation of one copy of it.
# --------------------------------------------------------------------------
GENERATED_REL='src/lib/tesserafin-sdk/generated'

# rewrite_manifest <webdir> — rebuild generated-manifest.json from the tree and restamp
# version.json's generatedManifestSha256/generatedFileCount so the copy is self-consistent again.
# Used to build the baseline, and by controls that mutate the tree *and* honestly re-describe it,
# so the failure being demonstrated is the one named rather than a stale digest.
rewrite_manifest() {
    local web="$1"
    local files
    files="$(cd "$web/$GENERATED_REL" && find . -type f -printf '%P\n' | LC_ALL=C sort | while IFS= read -r f; do
        jq -n --arg p "$f" --arg s "$(sha256sum "$f" | cut -d' ' -f1)" '{path:$p, sha256:$s}'
    done | jq -s '.')"
    jq -n --argjson files "$files" --arg root "$GENERATED_REL" \
        '{provenanceSchema:2, root:$root, algorithm:"sha256", fileCount:($files|length), files:$files}' \
        > "$web/src/lib/tesserafin-sdk/spec/generated-manifest.json"
    local msha count
    msha="$(sha256sum "$web/src/lib/tesserafin-sdk/spec/generated-manifest.json" | cut -d' ' -f1)"
    count="$(jq -r '.fileCount' "$web/src/lib/tesserafin-sdk/spec/generated-manifest.json")"
    jq --arg m "$msha" --argjson c "$count" '.generatedManifestSha256 = $m | .generatedFileCount = $c' \
        "$web/src/lib/tesserafin-sdk/spec/version.json" > "$web/.v.tmp"
    mv "$web/.v.tmp" "$web/src/lib/tesserafin-sdk/spec/version.json"
}

# make_web <name> <schema> <sourceCommit> <canonicalSha> -> path
make_web() {
    local name="$1" schema="$2" source_commit="$3" canonical_sha="$4"
    local web="$FIXTURES/web-$name"
    mkdir -p "$web/$GENERATED_REL/api" "$web/$GENERATED_REL/models" "$web/src/lib/tesserafin-sdk/spec"

    printf 'export const api = 1;\n'   > "$web/$GENERATED_REL/api/x-api.ts"
    printf 'export const model = 1;\n' > "$web/$GENERATED_REL/models/x-model.ts"
    printf 'export * from "./api/x-api";\n' > "$web/$GENERATED_REL/index.ts"

    jq -n '{devDependencies:{"@openapitools/openapi-generator-cli":"2.40.1"}}' > "$web/package.json"
    jq -n '{"generator-cli":{version:"7.11.0"}}' > "$web/openapitools.json"

    # The transformed mirror. Its bytes need only be stable and self-consistent here: the
    # canonical-to-mirror transform equality is proved by the web repository's own gate, which
    # ci/verify-sdk-provenance.sh runs separately.
    printf '{"transformed":true}\n' > "$web/src/lib/tesserafin-sdk/spec/openapi.json"
    local spec_sha
    spec_sha="$(sha256sum "$web/src/lib/tesserafin-sdk/spec/openapi.json" | cut -d' ' -f1)"

    if [ "$schema" = "1" ]; then
        jq -n --arg sc "$source_commit" --arg ss "$spec_sha" \
            '{title:"Fixture API", version:"1.0.0", xTesserafinVersion:"1.0.0", serverVersion:"1.0.0",
              webAppVersion:"1.0.0", versionSkewNote:null, openapi:"3.0.4", pathCount:1, schemaCount:0,
              source:"fixture", sourceCommit:$sc, sourceRef:null, specSha256:$ss,
              generatedAt:"2026-01-01T00:00:00.000Z"}' \
            > "$web/src/lib/tesserafin-sdk/spec/version.json"
        printf '%s' "$web"
        return
    fi

    jq -n --arg sc "$source_commit" --arg ss "$spec_sha" --arg cs "$canonical_sha" \
        '{provenanceSchema:2, title:"Fixture API", version:"1.0.0", xTesserafinVersion:"1.0.0",
          serverVersion:"1.0.0", webAppVersion:"1.0.0", versionSkewNote:null, openapi:"3.0.4",
          pathCount:1, schemaCount:0, source:"fixture",
          sourceRepository:"tesserafin-project/tesserafin", sourceCommit:$sc, sourceRef:null,
          canonicalSpecSha256:$cs, specSha256:$ss, transformVersion:1,
          generator:{name:"typescript-axios", cliVersion:"2.40.1", generatorVersion:"7.11.0"},
          generatedManifestSha256:"", generatedFileCount:0,
          generatedAt:"2026-01-01T00:00:00.000Z"}' \
        > "$web/src/lib/tesserafin-sdk/spec/version.json"
    rewrite_manifest "$web"
    printf '%s' "$web"
}

VERSION_REL='src/lib/tesserafin-sdk/spec/version.json'
# patch_version <web> [jq args…] <jq filter> — one deliberate mutation of an otherwise
# internally consistent pin.
patch_version() {
    local web="$1"; shift
    jq "$@" "$web/$VERSION_REL" > "$web/.v.tmp" && mv "$web/.v.tmp" "$web/$VERSION_REL"
}

# expect <name> <PASS|RED|INDETERMINATE> <message fragment, or ANCESTOR/CONTENT_… on PASS> <args…>
expect() {
    local name="$1" want="$2" fragment="$3"; shift 3
    local out rc
    out="$("$VERIFIER" "$@" 2>&1)"; rc=$?

    case "$want" in
    PASS)
        if [ "$rc" -ne 0 ]; then
            error "control '$name' should have PASSED but exited $rc"
            printf '%s\n' "$out" | tail -3 >&2
            return
        fi
        if ! printf '%s' "$out" | grep -qF "$fragment"; then
            error "control '$name' passed but did not report '$fragment'"
            printf '%s\n' "$out" | tail -5 >&2
            return
        fi
        note "control '$name': VERIFIED as expected ($fragment)"
        ;;
    RED)
        if [ "$rc" -eq 0 ]; then
            error "control '$name' PASSED the gate; it must not"
            return
        fi
        if [ "$rc" -ne 1 ]; then
            error "control '$name' exited $rc, expected 1 (a property failure, not an unanswered question)"
            printf '%s\n' "$out" | tail -3 >&2
            return
        fi
        if ! printf '%s' "$out" | grep -qF "$fragment"; then
            error "control '$name' failed for the wrong reason (expected to mention: $fragment)"
            printf '%s\n' "$out" | tail -3 >&2
            return
        fi
        note "control '$name': RED as expected"
        ;;
    INDETERMINATE)
        if [ "$rc" -eq 0 ]; then
            error "control '$name' PASSED the gate; an unanswerable question must never be a pass"
            return
        fi
        if [ "$rc" -ne 2 ]; then
            error "control '$name' exited $rc, expected 2"
            printf '%s\n' "$out" | tail -3 >&2
            return
        fi
        note "control '$name': INDETERMINATE as expected (never a degraded pass)"
        ;;
    esac
}

# ==========================================================================
# 1-3. The positive controls. 2 and 3 are the point of the whole change.
# ==========================================================================
W_ANCESTOR="$(make_web ancestor 2 "$R2" "$CONTRACT_B_SHA")"
expect '01 ancestor + identical contract' PASS 'result schema=2 ancestry=ANCESTOR' \
    --web "$W_ANCESTOR" --server-repo "$SERVER" --server-commit "$R2"

W_REBASE="$(make_web rebase 2 "$C2" "$CONTRACT_B_SHA")"
expect '02 non-ancestor by simulated rebase' PASS 'result schema=2 ancestry=CONTENT_EQUIVALENT_NON_ANCESTOR' \
    --web "$W_REBASE" --server-repo "$SERVER" --server-commit "$R2"

W_SQUASH="$(make_web squash 2 "$C2" "$CONTRACT_B_SHA")"
expect '03 non-ancestor by simulated squash' PASS 'result schema=2 ancestry=CONTENT_EQUIVALENT_NON_ANCESTOR' \
    --web "$W_SQUASH" --server-repo "$SERVER" --server-commit "$S2"

# The same two fixtures, declared as schema 1, must be refused. This is what proves schema 2 is a
# new protocol rather than a hole punched in the old one.
W_V1_REBASE="$(make_web v1-rebase 1 "$C2" "$CONTRACT_B_SHA")"
expect '04 schema 1 non-ancestor (rebase shape)' RED 'is NOT an ancestor' \
    --web "$W_V1_REBASE" --server-repo "$SERVER" --server-commit "$R2"
W_V1_SQUASH="$(make_web v1-squash 1 "$C2" "$CONTRACT_B_SHA")"
expect '04b schema 1 non-ancestor (squash shape)' RED 'is NOT an ancestor' \
    --web "$W_V1_SQUASH" --server-repo "$SERVER" --server-commit "$S2"

# A schema-1 pin that IS an ancestor still passes, unchanged.
W_V1_OK="$(make_web v1-ok 1 "$R2" "$CONTRACT_B_SHA")"
expect '04c schema 1 ancestor still passes' PASS 'result schema=1 ancestry=ANCESTOR' \
    --web "$W_V1_OK" --server-repo "$SERVER" --server-commit "$R2"

# ==========================================================================
# 5-7. Contract content. Ancestry is gone; content is all that is left, so none of these may slip.
# ==========================================================================
W_BYTE="$(make_web changed-byte 2 "$C1" "$CONTRACT_B_SHA")"
expect '05 non-ancestor with a changed canonical byte' RED 'canonical contract MOVED' \
    --web "$W_BYTE" --server-repo "$SERVER" --server-commit "$R2"

SERVER_BAD_LOCK="$FIXTURES/server-bad-lock"
git_fixture clone --quiet "$SERVER" "$SERVER_BAD_LOCK" 2>/dev/null
git_fixture -C "$SERVER_BAD_LOCK" checkout --quiet master
printf '%s\n' "$CONTRACT_B" | sed 's/"paths":{"\/x":{}}/"paths":{"\/y":{}}/' > "$SERVER_BAD_LOCK/openapi/openapi.json"
git_fixture -C "$SERVER_BAD_LOCK" add -A
commit_at "$SERVER_BAD_LOCK" '2026-01-05T00:00:00Z' 'canonical bytes changed, lock left stale'
BAD_LOCK_COMMIT="$(git -C "$SERVER_BAD_LOCK" rev-parse HEAD)"
W_STALE_LOCK="$(make_web stale-lock 2 "$BAD_LOCK_COMMIT" "$CONTRACT_B_SHA")"
expect '06 canonical bytes changed, lock stale' RED 'openapi/contract.lock.json records' \
    --web "$W_STALE_LOCK" --server-repo "$SERVER_BAD_LOCK" --server-commit "$BAD_LOCK_COMMIT"

SERVER_LOCK_ONLY="$FIXTURES/server-lock-only"
git_fixture clone --quiet "$SERVER" "$SERVER_LOCK_ONLY" 2>/dev/null
git_fixture -C "$SERVER_LOCK_ONLY" checkout --quiet master
jq '.sha256 = "0000000000000000000000000000000000000000000000000000000000000000"' \
    "$SERVER_LOCK_ONLY/openapi/contract.lock.json" > "$SERVER_LOCK_ONLY/.l.tmp"
mv "$SERVER_LOCK_ONLY/.l.tmp" "$SERVER_LOCK_ONLY/openapi/contract.lock.json"
git_fixture -C "$SERVER_LOCK_ONLY" add -A
commit_at "$SERVER_LOCK_ONLY" '2026-01-05T00:00:00Z' 'lock changed, canonical bytes left alone'
LOCK_ONLY_COMMIT="$(git -C "$SERVER_LOCK_ONLY" rev-parse HEAD)"
W_LOCK_ONLY="$(make_web lock-only 2 "$LOCK_ONLY_COMMIT" "$CONTRACT_B_SHA")"
expect '07 lock changed, canonical bytes stale' RED 'openapi/contract.lock.json records' \
    --web "$W_LOCK_ONLY" --server-repo "$SERVER_LOCK_ONLY" --server-commit "$LOCK_ONLY_COMMIT"

# ==========================================================================
# 8-10. The source commit as audit evidence. Schema 2 stops requiring ancestry; it does not stop
#       requiring that the commit be real, resolvable and named unambiguously.
# ==========================================================================
W_MISSING="$(make_web missing-commit 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_MISSING" '.sourceCommit = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"'
expect '08 source commit does not resolve' RED 'does not resolve in the server repository' \
    --web "$W_MISSING" --server-repo "$SERVER" --server-commit "$R2"

W_REPO="$(make_web wrong-repo 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_REPO" '.sourceRepository = "someone-else/tesserafin"'
expect '09 source repository changed' RED 'this gate only accepts' \
    --web "$W_REPO" --server-repo "$SERVER" --server-commit "$R2"

W_SHORT="$(make_web short-sha 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_SHORT" --arg c "${C2:0:12}" '.sourceCommit = $c'
expect '10a source SHA abbreviated' RED 'characters; an abbreviated commit' \
    --web "$W_SHORT" --server-repo "$SERVER" --server-commit "$R2"

W_REF="$(make_web ref-not-sha 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_REF" '.sourceCommit = "refs/heads/candidate"'
expect '10b source SHA replaced by a ref' RED 'not lowercase hexadecimal' \
    --web "$W_REF" --server-repo "$SERVER" --server-commit "$R2"

# ==========================================================================
# 11-12. The transformed mirror.
# ==========================================================================
W_SPEC="$(make_web changed-spec 2 "$C2" "$CONTRACT_B_SHA")"
printf '{"transformed":false}\n' > "$W_SPEC/src/lib/tesserafin-sdk/spec/openapi.json"
expect '11 transformed specification changed' RED 'edited by hand' \
    --web "$W_SPEC" --server-repo "$SERVER" --server-commit "$R2"

W_SPECSHA="$(make_web changed-specsha 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_SPECSHA" '.specSha256 = "1111111111111111111111111111111111111111111111111111111111111111"'
expect '12 transformed digest changed without the bytes' RED 'edited by hand' \
    --web "$W_SPECSHA" --server-repo "$SERVER" --server-commit "$R2"

# The recorded content address itself, in both directions.
W_CANON="$(make_web changed-canonical 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_CANON" '.canonicalSpecSha256 = "2222222222222222222222222222222222222222222222222222222222222222"'
expect '12b recorded canonicalSpecSha256 does not describe the commit' RED \
    'does not describe the commit it names' \
    --web "$W_CANON" --server-repo "$SERVER" --server-commit "$R2"

# ==========================================================================
# 13-15. The generated tree. 15 is the one the web repository's own regeneration gate cannot see.
# ==========================================================================
W_EDIT="$(make_web edited-file 2 "$C2" "$CONTRACT_B_SHA")"
printf 'export const api = 2; // hand-edited\n' > "$W_EDIT/$GENERATED_REL/api/x-api.ts"
expect '13 generated file hand-edited' RED 'differing bytes' \
    --web "$W_EDIT" --server-repo "$SERVER" --server-commit "$R2"

W_OMIT="$(make_web omitted-file 2 "$C2" "$CONTRACT_B_SHA")"
rm "$W_OMIT/$GENERATED_REL/models/x-model.ts"
expect '14 generated file omitted' RED 'listed but absent' \
    --web "$W_OMIT" --server-repo "$SERVER" --server-commit "$R2"

W_EXTRA="$(make_web extra-file 2 "$C2" "$CONTRACT_B_SHA")"
printf 'export const smuggled = 1;\n' > "$W_EXTRA/$GENERATED_REL/api/smuggled.ts"
expect '15 extra generated file injected' RED 'present but unlisted' \
    --web "$W_EXTRA" --server-repo "$SERVER" --server-commit "$R2"

# The same injection, honestly re-described in the manifest and restamped in version.json, is
# self-consistent and therefore NOT an inner-script property: every digest here would agree with
# every other. It is caught one layer up instead, by regeneration — `generate-tesserafin-sdk.mjs`
# clears `generated/` before generating, so the injected file is deleted and the web repository's
# own freshness gate reports the deletion. That is deliberate division of labour, not a gap: see
# break control C4LH-BW1 in the web pull request body for the run that proves it red.

W_MANIFEST_SHA="$(make_web manifest-digest 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_MANIFEST_SHA" '.generatedManifestSha256 = "3333333333333333333333333333333333333333333333333333333333333333"'
expect '15c manifest digest restated by hand' RED 'edited by hand' \
    --web "$W_MANIFEST_SHA" --server-repo "$SERVER" --server-commit "$R2"

W_NO_MANIFEST="$(make_web no-manifest 2 "$C2" "$CONTRACT_B_SHA")"
rm "$W_NO_MANIFEST/src/lib/tesserafin-sdk/spec/generated-manifest.json"
expect '15d manifest absent entirely' RED 'the generated tree is unaddressed' \
    --web "$W_NO_MANIFEST" --server-repo "$SERVER" --server-commit "$R2"

# ==========================================================================
# 16. Generator drift.
# ==========================================================================
W_GEN="$(make_web generator-drift 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_GEN" '.generator.generatorVersion = "7.10.0"'
expect '16a generator version drift' RED 'openapitools.json' \
    --web "$W_GEN" --server-repo "$SERVER" --server-commit "$R2"

W_CLI="$(make_web cli-drift 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_CLI" '.generator.cliVersion = "2.0.0"'
expect '16b generator CLI drift' RED 'package.json' \
    --web "$W_CLI" --server-repo "$SERVER" --server-commit "$R2"

# ==========================================================================
# 18-19. The schema itself.
# ==========================================================================
W_FUTURE="$(make_web future-schema 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_FUTURE" '.provenanceSchema = 3'
expect '18a unknown (future) provenance schema' RED 'refused, not assumed compatible' \
    --web "$W_FUTURE" --server-repo "$SERVER" --server-commit "$R2"

W_STRSCHEMA="$(make_web string-schema 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_STRSCHEMA" '.provenanceSchema = "2"'
expect '18b provenanceSchema as a string, not a number' RED 'must be the number 2' \
    --web "$W_STRSCHEMA" --server-repo "$SERVER" --server-commit "$R2"

W_UNKNOWN="$(make_web unknown-key 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_UNKNOWN" '.provenanceOverride = "trust me"'
expect '19 unknown metadata key' RED 'closed key set' \
    --web "$W_UNKNOWN" --server-repo "$SERVER" --server-commit "$R2"

# A missing schema-2 field is a failure, not a field that defaults to something permissive.
for field in sourceRepository canonicalSpecSha256 transformVersion generator generatedManifestSha256 generatedFileCount; do
    W_MISS="$(make_web "missing-$field" 2 "$C2" "$CONTRACT_B_SHA")"
    patch_version "$W_MISS" "del(.$field)"
    expect "19b missing $field" RED "is missing $field" \
        --web "$W_MISS" --server-repo "$SERVER" --server-commit "$R2"
done

W_TRANSFORM="$(make_web transform-drift 2 "$C2" "$CONTRACT_B_SHA")"
patch_version "$W_TRANSFORM" '.transformVersion = 99'
expect '19c unknown transform pipeline version' RED 'cannot reason about' \
    --web "$W_TRANSFORM" --server-repo "$SERVER" --server-commit "$R2"

# ==========================================================================
# 20. An unanswerable question is never a pass.
# ==========================================================================
W_DEP="$(make_web missing-dependency 2 "$C2" "$CONTRACT_B_SHA")"
DEPDIR="$FIXTURES/nojq"; mkdir -p "$DEPDIR"
for tool in git sha256sum find sort bash comm awk cut head tr printf; do
    src="$(command -v "$tool" 2>/dev/null)" && ln -sf "$src" "$DEPDIR/$tool"
done
out="$(PATH="$DEPDIR" "$VERIFIER" --web "$W_DEP" --server-repo "$SERVER" --server-commit "$R2" 2>&1)"; rc=$?
if [ "$rc" -eq 0 ]; then
    error "control '20 verification dependency unavailable' PASSED; a missing tool must never be a pass"
elif [ "$rc" -ne 2 ]; then
    error "control '20 verification dependency unavailable' exited $rc, expected 2 (INDETERMINATE)"
elif ! printf '%s' "$out" | grep -qF "is not on PATH"; then
    error "control '20 verification dependency unavailable' failed for the wrong reason"
    printf '%s\n' "$out" | tail -3 >&2
else
    note "control '20 verification dependency unavailable': INDETERMINATE as expected (never a degraded pass)"
fi

W_NOWEB="$FIXTURES/web-does-not-exist"
expect '20b web checkout absent' INDETERMINATE '' \
    --web "$W_NOWEB" --server-repo "$SERVER" --server-commit "$R2"

# ==========================================================================
if [ "$failures" -ne 0 ]; then
    printf '\nweb-provenance fixtures: %d control(s) did not behave as required\n' "$failures" >&2
    exit 1
fi
printf '\nweb-provenance fixtures: every control behaved as required\n'
exit 0
