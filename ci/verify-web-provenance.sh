#!/usr/bin/env bash
#
# The provenance proofs, as a pure function of two checkouts (C4-LH, #246).
#
# WHY THIS IS A SEPARATE SCRIPT. `ci/verify-sdk-provenance.sh` derives the web checkout it
# analyses from the pair lock and clones it anonymously from GitHub. That is deliberate and is
# most of its value: it cannot be aimed at a checkout someone hands it. The consequence recorded
# in `ci/tests/sdk-provenance.test.sh` was that the web-side failure modes could not be exercised
# as negative controls, because doing so would have meant adding exactly the override that makes
# the gate aimable.
#
# This script is that override, without the hole. It takes a web checkout and a server checkout as
# arguments and proves everything that is a property OF THOSE BYTES. It is not a gate on its own —
# nothing invokes it with a checkout of its own choosing in CI. `verify-sdk-provenance.sh` still
# owns "which web commit are we even allowed to look at", clones it itself, and then calls this
# script on the result. The fixture suite calls it directly on synthetic repositories. Neither
# path can weaken the other.
#
# SCHEMA 1 (legacy) and SCHEMA 2 (content-addressed) are both implemented, explicitly.
#
#   Schema 1 identifies a generated SDK by the git commit its contract came from, and requires
#   that commit to be an ANCESTOR of the server commit under test. Unchanged; a schema-1
#   non-ancestor pin still fails here exactly as it always did.
#
#   Schema 2 identifies it by CONTENT. The web generator consumes exactly one thing from the
#   server — the canonical `openapi/openapi.json` bytes — so two server commits carrying
#   byte-identical locked canonical contracts produce a byte-identical transport boundary,
#   whatever GitHub did to the commit identity when it squashed or rebased the branch. That is not
#   a relaxation dressed up as a principle: schema 2 proves strictly MORE than schema 1 did, and
#   every digest it reads is recomputed here from bytes rather than accepted as written.
#
#   Ancestry is still computed under schema 2 and reported, as ANCESTOR or as
#   CONTENT_EQUIVALENT_NON_ANCESTOR. The second is a successful result only after every other
#   proof has already passed; it is a label on a verified pin, never a reason to skip a check.
#
# FAIL-CLOSED. Every exit path that is not "every property held" is non-zero. There is no skip, no
# soft warning that passes, and no degraded mode: a missing tool or an unreadable input is
# INDETERMINATE (exit 2), never a pass.
#
# Exit codes: 0 verified, 1 a property failed, 2 the question was not answered.

set -uo pipefail

WEB=""
SERVER_REPO=""
SERVER_COMMIT=""

# The one server repository a pinned SDK may name. Hard-coded rather than derived from whatever
# remote the checkout happens to have: a gate that asks the artefact under test which repository
# it belongs to has not checked anything.
EXPECTED_SERVER_REPOSITORY="tesserafin-project/tesserafin"

# The transform pipeline versions this script knows how to reason about. A pin produced by an
# unknown pipeline is refused rather than compared against transforms it was never produced by.
KNOWN_TRANSFORM_VERSIONS="1"

CANONICAL_SPEC_PATH="openapi/openapi.json"
CANONICAL_LOCK_PATH="openapi/contract.lock.json"
SDK_REL="src/lib/tesserafin-sdk"
GENERATED_REL="$SDK_REL/generated"

die_indeterminate() { printf 'web-provenance: INDETERMINATE: %s\n' "$1" >&2; exit 2; }
die_fail()          { printf 'web-provenance: FAIL: %s\n' "$1" >&2; exit 1; }
ok()                { printf 'web-provenance: ok  — %s\n' "$1"; }

usage() {
    cat <<'EOF'
Usage: ci/verify-web-provenance.sh --web PATH --server-repo PATH --server-commit SHA

  --web PATH            web checkout to analyse (a working tree, already at the commit under test)
  --server-repo PATH    server checkout the contract is read from
  --server-commit SHA   full 40-character server commit under test

Exit codes: 0 verified, 1 property failed, 2 question not answered.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
    --web)           WEB="${2:?--web needs a value}"; shift 2 ;;
    --server-repo)   SERVER_REPO="${2:?--server-repo needs a value}"; shift 2 ;;
    --server-commit) SERVER_COMMIT="${2:?--server-commit needs a value}"; shift 2 ;;
    -h|--help)       usage; exit 0 ;;
    *)               die_indeterminate "unknown option $1" ;;
    esac
done

[ -n "$WEB" ]           || die_indeterminate "--web is required"
[ -n "$SERVER_REPO" ]   || die_indeterminate "--server-repo is required"
[ -n "$SERVER_COMMIT" ] || die_indeterminate "--server-commit is required"

for tool in git jq sha256sum find sort; do
    command -v "$tool" >/dev/null 2>&1 || die_indeterminate "required tool '$tool' is not on PATH"
done

[ -d "$WEB" ] || die_indeterminate "web checkout $WEB does not exist"
git -C "$SERVER_REPO" rev-parse --git-dir >/dev/null 2>&1 \
    || die_indeterminate "$SERVER_REPO is not a git checkout"

WEB="$(cd "$WEB" && pwd)"
SERVER_REPO="$(cd "$SERVER_REPO" && pwd)"

case "$SERVER_COMMIT" in
    *[!0-9a-f]* | "") die_fail "server commit '$SERVER_COMMIT' is not lowercase hexadecimal" ;;
esac
[ "${#SERVER_COMMIT}" -eq 40 ] || die_fail "server commit '$SERVER_COMMIT' is not a full 40-character SHA"

VERSION_JSON="$WEB/$SDK_REL/spec/version.json"
PINNED_SPEC="$WEB/$SDK_REL/spec/openapi.json"
MANIFEST_JSON="$WEB/$SDK_REL/spec/generated-manifest.json"
GENERATED_DIR="$WEB/$GENERATED_REL"

[ -f "$VERSION_JSON" ] || die_fail "web checkout has no $SDK_REL/spec/version.json"
[ -f "$PINNED_SPEC" ]  || die_fail "web checkout has no $SDK_REL/spec/openapi.json"
[ -d "$GENERATED_DIR" ] || die_fail "web checkout has no $GENERATED_REL/ tree"
jq -e . "$VERSION_JSON" >/dev/null 2>&1 || die_fail "web spec/version.json is not valid JSON"

# --------------------------------------------------------------------------
# 0. Which protocol does this pin speak?
#
# Absent means schema 1: the field postdates it. Anything this script does not implement is a hard
# failure and never a "assume it is the newest one I know" guess — a pin written by a future,
# stricter schema must not be validated by today's looser rules.
# --------------------------------------------------------------------------
SCHEMA="$(jq -r 'if has("provenanceSchema") and (.provenanceSchema != null) then (.provenanceSchema|tostring) else "1" end' "$VERSION_JSON")"
case "$SCHEMA" in
    1|2) : ;;
    *)   die_fail "web spec/version.json declares provenanceSchema '$SCHEMA'; this verifier implements 1 and 2. An unrecognised provenance schema is refused, not assumed compatible." ;;
esac
# `1` must be the absence of the field or the literal number 1, not a string that looks like one.
if [ "$SCHEMA" = "2" ]; then
    jq -e '.provenanceSchema == 2' "$VERSION_JSON" >/dev/null \
        || die_fail "web spec/version.json provenanceSchema must be the number 2, not a string"
fi
ok "provenance schema $SCHEMA"

# --------------------------------------------------------------------------
# 1. The server contract, against its own lock, at the commit under test.
# --------------------------------------------------------------------------
LOCKED_SHA="$(git -C "$SERVER_REPO" show "$SERVER_COMMIT:$CANONICAL_LOCK_PATH" 2>/dev/null | jq -r '.sha256 // empty')"
[ -n "$LOCKED_SHA" ] || die_fail "cannot read $CANONICAL_LOCK_PATH at $SERVER_COMMIT, or it records no sha256"
SERVER_CONTRACT_SHA="$(git -C "$SERVER_REPO" show "$SERVER_COMMIT:$CANONICAL_SPEC_PATH" 2>/dev/null | sha256sum | cut -d' ' -f1)"
[ -n "$SERVER_CONTRACT_SHA" ] || die_fail "cannot read $CANONICAL_SPEC_PATH at $SERVER_COMMIT"
[ "$SERVER_CONTRACT_SHA" = "$LOCKED_SHA" ] \
    || die_fail "canonical contract at $SERVER_COMMIT is sha256 $SERVER_CONTRACT_SHA but $CANONICAL_LOCK_PATH records $LOCKED_SHA"
ok "server contract at $SERVER_COMMIT matches its own lock (sha256 $SERVER_CONTRACT_SHA)"

# --------------------------------------------------------------------------
# 2. The pinned mirror, against its own recorded digest. Catches a hand-edit of either file with
#    no server checkout involved at all.
# --------------------------------------------------------------------------
SOURCE_COMMIT="$(jq -r '.sourceCommit // empty' "$VERSION_JSON")"
SPEC_SHA="$(jq -r '.specSha256 // empty' "$VERSION_JSON")"
PIN_VERSION="$(jq -r '.version // empty' "$VERSION_JSON")"

for field in sourceCommit specSha256 version; do
    jq -e --arg f "$field" 'has($f) and (.[$f] != null)' "$VERSION_JSON" >/dev/null \
        || die_fail "web spec/version.json records no $field"
done
case "$SOURCE_COMMIT" in
    *[!0-9a-f]* | "") die_fail "web sourceCommit '$SOURCE_COMMIT' is not lowercase hexadecimal — a branch name, a tag or an abbreviated SHA is never accepted here" ;;
esac
[ "${#SOURCE_COMMIT}" -eq 40 ] \
    || die_fail "web sourceCommit '$SOURCE_COMMIT' is ${#SOURCE_COMMIT} characters; an abbreviated commit cannot be resolved unambiguously"

ACTUAL_SPEC_SHA="$(sha256sum "$PINNED_SPEC" | cut -d' ' -f1)"
[ "$ACTUAL_SPEC_SHA" = "$SPEC_SHA" ] \
    || die_fail "web pinned spec is sha256 $ACTUAL_SPEC_SHA but version.json records $SPEC_SHA — the pinned spec was edited by hand"
ok "pinned mirror matches its recorded specSha256 ($SPEC_SHA)"

LOCKED_VERSION="$(git -C "$SERVER_REPO" show "$SERVER_COMMIT:$CANONICAL_LOCK_PATH" 2>/dev/null | jq -r '.version // empty')"
if [ -n "$LOCKED_VERSION" ] && [ "$PIN_VERSION" != "$LOCKED_VERSION" ]; then
    die_fail "web version.json records version '$PIN_VERSION' but the server contract lock records '$LOCKED_VERSION'"
fi
ok "version.json version '$PIN_VERSION' agrees with the server contract lock"

# --------------------------------------------------------------------------
# 3. The source commit must resolve, and the canonical contract there must be byte-identical to
#    the one under test. This is the check both schemas share and neither may skip: schema 2 does
#    not trade this away, it trades away only the ANCESTRY of the same commit.
# --------------------------------------------------------------------------
git -C "$SERVER_REPO" cat-file -e "$SOURCE_COMMIT^{commit}" 2>/dev/null \
    || die_fail "web sourceCommit $SOURCE_COMMIT does not resolve in the server repository"

PIN_CONTRACT_SHA="$(git -C "$SERVER_REPO" show "$SOURCE_COMMIT:$CANONICAL_SPEC_PATH" 2>/dev/null | sha256sum | cut -d' ' -f1)"
[ -n "$PIN_CONTRACT_SHA" ] || die_fail "cannot read $CANONICAL_SPEC_PATH at the pinned commit $SOURCE_COMMIT"
if [ "$PIN_CONTRACT_SHA" != "$SERVER_CONTRACT_SHA" ]; then
    die_fail "the canonical contract MOVED after the web pin: sha256 $PIN_CONTRACT_SHA at $SOURCE_COMMIT versus $SERVER_CONTRACT_SHA at $SERVER_COMMIT. Regenerate the web SDK against the current contract and re-pin; do not move the lock without regenerating."
fi
ok "canonical contract is byte-identical at the pin and at the commit under test"

# Ancestry is computed for BOTH schemas. Schema 1 requires it; schema 2 reports it.
ANCESTRY="CONTENT_EQUIVALENT_NON_ANCESTOR"
if git -C "$SERVER_REPO" merge-base --is-ancestor "$SOURCE_COMMIT" "$SERVER_COMMIT" 2>/dev/null; then
    ANCESTRY="ANCESTOR"
fi

if [ "$SCHEMA" = "1" ]; then
    [ "$ANCESTRY" = "ANCESTOR" ] \
        || die_fail "web sourceCommit $SOURCE_COMMIT is NOT an ancestor of $SERVER_COMMIT — the pin names a commit that is not on this history. (Schema 1 requires ancestry. A pin regenerated under provenance schema 2 is verified by content instead; this one declares schema 1.)"
    ok "schema 1: sourceCommit is an ancestor of the commit under test"
    # A machine-readable result line, so no consumer has to parse the aligned block below and
    # nothing depends on its column widths.
    printf '\nweb-provenance: result schema=1 ancestry=ANCESTOR\n'
    printf 'web-provenance: VERIFIED\n  schema                 1\n  ancestry               ANCESTOR\n  server commit          %s\n  server contract sha256 %s\n  web sourceCommit       %s\n  web pinned spec sha256 %s\n' \
        "$SERVER_COMMIT" "$SERVER_CONTRACT_SHA" "$SOURCE_COMMIT" "$SPEC_SHA"
    exit 0
fi

# ==========================================================================
# Schema 2 only, from here down. Everything above has already passed.
# ==========================================================================

# --------------------------------------------------------------------------
# 4. A CLOSED key set. An unrecognised field is rejected, not ignored: a field this verifier skips
#    over is a field it cannot enforce, and "it was in the metadata and nobody looked at it" is
#    the failure mode a provenance gate exists to prevent.
# --------------------------------------------------------------------------
ALLOWED_KEYS='["provenanceSchema","title","version","xTesserafinVersion","serverVersion","webAppVersion","versionSkewNote","openapi","pathCount","schemaCount","source","sourceRepository","sourceCommit","sourceRef","canonicalSpecSha256","specSha256","transformVersion","generator","generatedManifestSha256","generatedFileCount","generatedAt"]'
UNKNOWN_KEYS="$(jq -r --argjson allowed "$ALLOWED_KEYS" '[keys[] | select(. as $k | ($allowed | index($k)) | not)] | join(", ")' "$VERSION_JSON")"
[ -z "$UNKNOWN_KEYS" ] \
    || die_fail "web spec/version.json contains key(s) this verifier does not know: $UNKNOWN_KEYS. Schema 2 has a closed key set."
ok "version.json key set is exactly the schema-2 vocabulary"

for field in provenanceSchema sourceRepository canonicalSpecSha256 transformVersion generator generatedManifestSha256 generatedFileCount; do
    jq -e --arg f "$field" 'has($f) and (.[$f] != null)' "$VERSION_JSON" >/dev/null \
        || die_fail "schema-2 pin is missing $field — this field is part of what replaces git ancestry as the compatibility predicate"
done

# --------------------------------------------------------------------------
# 5. The repository. A pin that names a different server repository is not this pair, whatever its
#    digests say.
# --------------------------------------------------------------------------
SOURCE_REPOSITORY="$(jq -r '.sourceRepository' "$VERSION_JSON")"
[ "$SOURCE_REPOSITORY" = "$EXPECTED_SERVER_REPOSITORY" ] \
    || die_fail "web version.json names sourceRepository '$SOURCE_REPOSITORY'; this gate only accepts '$EXPECTED_SERVER_REPOSITORY'"
ok "sourceRepository is $SOURCE_REPOSITORY"

# --------------------------------------------------------------------------
# 6. THE CONTENT ADDRESS. The digest the web repository recorded must equal the canonical contract
#    digest — both at the commit it names AND at the commit under test. Recomputed here from the
#    bytes git holds; the recorded value is evidence to check, never a number to trust.
# --------------------------------------------------------------------------
RECORDED_CANONICAL_SHA="$(jq -r '.canonicalSpecSha256' "$VERSION_JSON")"
case "$RECORDED_CANONICAL_SHA" in
    *[!0-9a-f]* | "") die_fail "canonicalSpecSha256 '$RECORDED_CANONICAL_SHA' is not lowercase hexadecimal" ;;
esac
[ "${#RECORDED_CANONICAL_SHA}" -eq 64 ] || die_fail "canonicalSpecSha256 '$RECORDED_CANONICAL_SHA' is not a 64-character sha256 digest"

[ "$RECORDED_CANONICAL_SHA" = "$PIN_CONTRACT_SHA" ] \
    || die_fail "web records canonicalSpecSha256 $RECORDED_CANONICAL_SHA but the canonical contract at the commit it names ($SOURCE_COMMIT) is sha256 $PIN_CONTRACT_SHA — the recorded content address does not describe the commit it names"
[ "$RECORDED_CANONICAL_SHA" = "$SERVER_CONTRACT_SHA" ] \
    || die_fail "web records canonicalSpecSha256 $RECORDED_CANONICAL_SHA but the canonical contract at $SERVER_COMMIT is sha256 $SERVER_CONTRACT_SHA"
ok "recorded canonicalSpecSha256 equals the canonical contract at BOTH $SOURCE_COMMIT and $SERVER_COMMIT"

# The contract lock AT THE PINNED COMMIT must name those same bytes. Without this, a source commit
# whose spec and lock disagreed with each other could still satisfy every other digest comparison.
PIN_LOCK_SHA="$(git -C "$SERVER_REPO" show "$SOURCE_COMMIT:$CANONICAL_LOCK_PATH" 2>/dev/null | jq -r '.sha256 // empty')"
[ -n "$PIN_LOCK_SHA" ] || die_fail "cannot read $CANONICAL_LOCK_PATH at the pinned commit $SOURCE_COMMIT"
[ "$PIN_LOCK_SHA" = "$RECORDED_CANONICAL_SHA" ] \
    || die_fail "the contract lock at $SOURCE_COMMIT records sha256 $PIN_LOCK_SHA, not $RECORDED_CANONICAL_SHA — the pinned commit's own spec and lock disagree"
ok "contract lock at $SOURCE_COMMIT names the same bytes"

# --------------------------------------------------------------------------
# 7. The transform pipeline that produced the mirror from the canonical bytes.
# --------------------------------------------------------------------------
TRANSFORM_VERSION="$(jq -r '.transformVersion' "$VERSION_JSON")"
jq -e '.transformVersion | type == "number"' "$VERSION_JSON" >/dev/null \
    || die_fail "transformVersion must be a number, not $(jq -r '.transformVersion | type' "$VERSION_JSON")"
case " $KNOWN_TRANSFORM_VERSIONS " in
    *" $TRANSFORM_VERSION "*) : ;;
    *) die_fail "web pin declares transformVersion $TRANSFORM_VERSION; this gate knows [$KNOWN_TRANSFORM_VERSIONS]. The mirror was produced by a canonical-to-mirror pipeline this verifier cannot reason about." ;;
esac
ok "transform pipeline version $TRANSFORM_VERSION is known"

# --------------------------------------------------------------------------
# 8. The generator, read from the two files in the WEB checkout that actually control it. Drift
#    here would make the regeneration proof meaningless rather than merely stale.
# --------------------------------------------------------------------------
WEB_PKG="$WEB/package.json"
WEB_TOOLS="$WEB/openapitools.json"
[ -f "$WEB_PKG" ]   || die_fail "web checkout has no package.json"
[ -f "$WEB_TOOLS" ] || die_fail "web checkout has no openapitools.json"

EXPECTED_CLI="$(jq -r '.devDependencies["@openapitools/openapi-generator-cli"] // .dependencies["@openapitools/openapi-generator-cli"] // empty' "$WEB_PKG")"
EXPECTED_GEN="$(jq -r '.["generator-cli"].version // empty' "$WEB_TOOLS")"
[ -n "$EXPECTED_CLI" ] || die_fail "web package.json pins no @openapitools/openapi-generator-cli version"
[ -n "$EXPECTED_GEN" ] || die_fail "web openapitools.json pins no generator-cli version"

RECORDED_GEN_KEYS="$(jq -r '.generator | keys_unsorted | sort | join(",")' "$VERSION_JSON")"
[ "$RECORDED_GEN_KEYS" = "cliVersion,generatorVersion,name" ] \
    || die_fail "version.json generator has keys [$RECORDED_GEN_KEYS], expected exactly [cliVersion,generatorVersion,name]"

RECORDED_NAME="$(jq -r '.generator.name' "$VERSION_JSON")"
RECORDED_CLI="$(jq -r '.generator.cliVersion' "$VERSION_JSON")"
RECORDED_GEN="$(jq -r '.generator.generatorVersion' "$VERSION_JSON")"
[ "$RECORDED_NAME" = "typescript-axios" ] \
    || die_fail "version.json records generator.name '$RECORDED_NAME'; this SDK is generated with typescript-axios"
[ "$RECORDED_CLI" = "$EXPECTED_CLI" ] \
    || die_fail "version.json records generator.cliVersion '$RECORDED_CLI' but the web checkout pins '$EXPECTED_CLI' in package.json"
[ "$RECORDED_GEN" = "$EXPECTED_GEN" ] \
    || die_fail "version.json records generator.generatorVersion '$RECORDED_GEN' but the web checkout pins '$EXPECTED_GEN' in openapitools.json"
ok "generator identity matches the web checkout's own pins ($RECORDED_NAME, cli $RECORDED_CLI, generator $RECORDED_GEN)"

# --------------------------------------------------------------------------
# 9. THE GENERATED TREE, addressed by content.
#
# Recomputed here in shell, from the files on disk, WITHOUT running any code from the web checkout.
# That independence is the point: a manifest computed by a helper the analysed repository ships
# could be made to describe a tree that is not there.
#
# This is also the only proof that catches an EXTRA file. The web repository's own freshness gate
# regenerates and compares with `git status`, but `generate-tesserafin-sdk.mjs` does not clear
# `generated/` first, so a file nobody generates is never removed and nothing reports it.
# --------------------------------------------------------------------------
[ -f "$MANIFEST_JSON" ] \
    || die_fail "schema-2 pin has no $SDK_REL/spec/generated-manifest.json — the generated tree is unaddressed"
jq -e . "$MANIFEST_JSON" >/dev/null 2>&1 || die_fail "generated-manifest.json is not valid JSON"

MANIFEST_SHA="$(sha256sum "$MANIFEST_JSON" | cut -d' ' -f1)"
RECORDED_MANIFEST_SHA="$(jq -r '.generatedManifestSha256' "$VERSION_JSON")"
[ "$MANIFEST_SHA" = "$RECORDED_MANIFEST_SHA" ] \
    || die_fail "generated-manifest.json is sha256 $MANIFEST_SHA but version.json records generatedManifestSha256 $RECORDED_MANIFEST_SHA — one of the two was edited by hand"

MANIFEST_ROOT="$(jq -r '.root // empty' "$MANIFEST_JSON")"
[ "$MANIFEST_ROOT" = "$GENERATED_REL" ] \
    || die_fail "generated-manifest.json covers root '$MANIFEST_ROOT', expected '$GENERATED_REL'"
[ "$(jq -r '.algorithm // empty' "$MANIFEST_JSON")" = "sha256" ] \
    || die_fail "generated-manifest.json does not declare algorithm sha256"

MANIFEST_COUNT="$(jq -r '.fileCount' "$MANIFEST_JSON")"
LISTED_COUNT="$(jq -r '.files | length' "$MANIFEST_JSON")"
[ "$MANIFEST_COUNT" = "$LISTED_COUNT" ] \
    || die_fail "generated-manifest.json declares fileCount $MANIFEST_COUNT but lists $LISTED_COUNT files"
RECORDED_FILE_COUNT="$(jq -r '.generatedFileCount' "$VERSION_JSON")"
[ "$RECORDED_FILE_COUNT" = "$MANIFEST_COUNT" ] \
    || die_fail "version.json records generatedFileCount $RECORDED_FILE_COUNT but the manifest lists $MANIFEST_COUNT files"

# `<path>\t<sha256>`, sorted, for both sides. `find -type f` includes dotfiles, which is correct:
# the manifest excludes nothing under its root.
DECLARED="$(jq -r '.files[] | "\(.path)\t\(.sha256)"' "$MANIFEST_JSON" | LC_ALL=C sort)"
ACTUAL="$(cd "$GENERATED_DIR" && find . -type f -printf '%P\n' | LC_ALL=C sort | while IFS= read -r f; do
    printf '%s\t%s\n' "$f" "$(sha256sum "$f" | cut -d' ' -f1)"
done)"

if [ "$DECLARED" != "$ACTUAL" ]; then
    EXTRA="$(comm -13 <(printf '%s\n' "$DECLARED" | cut -f1) <(printf '%s\n' "$ACTUAL" | cut -f1) | head -5 | tr '\n' ' ')"
    ABSENT="$(comm -23 <(printf '%s\n' "$DECLARED" | cut -f1) <(printf '%s\n' "$ACTUAL" | cut -f1) | head -5 | tr '\n' ' ')"
    EDITED="$(comm -13 <(printf '%s\n' "$DECLARED") <(printf '%s\n' "$ACTUAL") | cut -f1 | head -5 | tr '\n' ' ')"
    die_fail "the generated tree does not match its manifest — present but unlisted: [${EXTRA:-none}]; listed but absent: [${ABSENT:-none}]; differing bytes: [${EDITED:-none}]"
fi
ok "generated tree matches its manifest exactly ($MANIFEST_COUNT files, manifest sha256 $MANIFEST_SHA)"

# --------------------------------------------------------------------------
# 10. Result. The ancestry label is attached last, to a pin that has already satisfied every proof
#     above. It never shortens this list — a CONTENT_EQUIVALENT_NON_ANCESTOR pin reached this line
#     by passing exactly the same checks an ANCESTOR pin does, plus the ancestry computation.
# --------------------------------------------------------------------------
cat <<EOF

web-provenance: result schema=2 ancestry=$ANCESTRY
web-provenance: VERIFIED
  schema                 2
  ancestry               $ANCESTRY
  server commit          $SERVER_COMMIT
  server contract sha256 $SERVER_CONTRACT_SHA
  source repository      $SOURCE_REPOSITORY
  web sourceCommit       $SOURCE_COMMIT
  canonical sha256       $RECORDED_CANONICAL_SHA
  web pinned spec sha256 $SPEC_SHA
  transform version      $TRANSFORM_VERSION
  generator              $RECORDED_NAME cli $RECORDED_CLI generator $RECORDED_GEN
  generated files        $MANIFEST_COUNT
  manifest sha256        $MANIFEST_SHA
EOF
exit 0
