#!/usr/bin/env bash
# Deterministic controls for the fail-closed OpenAPI compatibility gate (#162).
#
# The point of these controls is that a green "OpenAPI Check" means something.
# The workflow this replaces ran `openapi-diff` and took its exit status at face
# value; when that tool stack-overflowed on the real contract (run 30230606338)
# the only available reading was "red forever", so the whole check was parked.
# Each RED case below proves one specific way the gate must refuse to pass; each
# GREEN case proves it does not cry wolf.
#
# Two families of fixture:
#
#   synthetic  a minimal hand-written contract, one mutation per policy rule.
#              Small enough that the expected classification is obvious by
#              reading it.
#   real       the committed openapi/openapi.json itself, copied under $TMPDIR
#              and mutated there. This is the control that matters most: it
#              proves the selected engine can process the exact schema graph
#              that made openapi-diff 2.1.6 (and 2.1.7) overflow the stack.
#
# Every mutation happens in a throwaway tree. The tracked contract is copied,
# never edited, and nothing this script produces can reach the repository.
#
# Usage:
#   ./ci/tests/openapi-compat.test.sh
#
# Requires docker and jq.

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPAT="$REPO_ROOT/ci/openapi-compat.sh"
REAL_SPEC="$REPO_ROOT/openapi/openapi.json"
REAL_LOCK="$REPO_ROOT/openapi/contract.lock.json"

PASS=0
FAIL=0

ok()  { echo "  PASS: $*"; PASS=$((PASS + 1)); }
bad() { echo "  FAIL: $*" >&2; FAIL=$((FAIL + 1)); }

command -v docker >/dev/null 2>&1 || { echo "docker is required" >&2; exit 1; }
command -v jq     >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }
[ -f "$COMPAT" ]    || { echo "missing $COMPAT" >&2; exit 1; }
[ -f "$REAL_SPEC" ] || { echo "missing $REAL_SPEC" >&2; exit 1; }
[ -f "$REAL_LOCK" ] || { echo "missing $REAL_LOCK" >&2; exit 1; }

WORK="$(mktemp -d "${TMPDIR:-/tmp}/tesserafin-openapi-controls.XXXXXX")"
cleanup() {
    docker image rm -f "$STUB_SILENT" "$STUB_CRASH" >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
STUB_SILENT="tesserafin-openapi-stub-silent"
STUB_CRASH="tesserafin-openapi-stub-crash"
trap cleanup EXIT

echo "OpenAPI compatibility gate controls"
echo "  fixture tree: $WORK"
echo

# ── Helpers ───────────────────────────────────────────────────────────────

# Regenerate the sidecar so a mutated contract is internally consistent. Any
# control that does NOT call this is deliberately proving the hash guard.
write_lock() {
    local spec="$1" lock="$2"
    printf '{\n  "algorithm": "sha256",\n  "sha256": "%s",\n  "spec": "openapi/openapi.json",\n  "version": "1.0.0"\n}\n' \
        "$(sha256sum "$spec" | cut -d' ' -f1)" > "$lock"
}

CASE_LOG=""
# expect <exit> <description> -- <compat args...>
expect() {
    local want="$1"; shift
    local desc="$1"; shift
    [ "$1" = "--" ] && shift
    local report="$WORK/reports/$((PASS + FAIL + 1)).md"
    local got=0
    CASE_LOG="$("$COMPAT" "$@" --report "$report" 2>&1)" || got=$?
    if [ "$got" -eq "$want" ]; then
        ok "$desc (exit $got)"
    else
        bad "$desc — expected exit $want, got $got"
        printf '%s\n' "$CASE_LOG" | tail -5 >&2
    fi
}

# ── Synthetic fixtures: one mutation per policy rule ──────────────────────

SYN="$WORK/synthetic"
mkdir -p "$SYN"
cat > "$SYN/base.json" <<'JSON'
{
  "openapi": "3.0.4",
  "info": { "title": "Compatibility control fixture", "version": "1.0.0" },
  "paths": {
    "/things": {
      "get": {
        "operationId": "ListThings",
        "responses": {
          "200": {
            "description": "ok",
            "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Thing" } } }
          },
          "404": { "description": "missing" }
        }
      },
      "post": {
        "operationId": "CreateThing",
        "requestBody": {
          "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ThingInput" } } }
        },
        "responses": { "200": { "description": "ok" } }
      }
    },
    "/legacy": {
      "get": { "operationId": "GetLegacy", "responses": { "200": { "description": "ok" } } }
    }
  },
  "components": {
    "schemas": {
      "Thing": {
        "type": "object",
        "properties": {
          "id": { "type": "string" },
          "kind": { "type": "string", "enum": ["a", "b"] }
        }
      },
      "ThingInput": {
        "type": "object",
        "required": ["id"],
        "properties": {
          "id": { "type": "string" },
          "note": { "type": "string" }
        }
      }
    }
  }
}
JSON
write_lock "$SYN/base.json" "$SYN/base.lock.json"

# mutate <name> <jq-program>
mutate() {
    local name="$1" program="$2"
    jq "$program" "$SYN/base.json" > "$SYN/$name.json" || { echo "fixture mutation failed: $name" >&2; exit 1; }
    write_lock "$SYN/$name.json" "$SYN/$name.lock.json"
}

# Compatible mutations.
mutate additive-property   '.components.schemas.Thing.properties.extra = {"type": "string"}'
mutate additive-path       '.paths["/others"] = {"get": {"operationId": "ListOthers", "responses": {"200": {"description": "ok"}}}}'
mutate additive-operation  '.paths["/things"].delete = {"operationId": "DeleteThings", "responses": {"200": {"description": "ok"}}}'
mutate additive-response   '.paths["/things"].get.responses["418"] = {"description": "teapot"}'
mutate description-only    '.paths["/things"].get.description = "Now documented."'

# Breaking mutations.
mutate removed-path        'del(.paths["/legacy"])'
mutate removed-operation   'del(.paths["/things"].post)'
mutate removed-response    'del(.paths["/things"].get.responses["404"])'
mutate removed-property    'del(.components.schemas.Thing.properties.kind)'
mutate newly-required      '.components.schemas.ThingInput.required += ["note"]'
mutate changed-type        '.components.schemas.Thing.properties.id = {"type": "integer"}'
mutate narrowed-enum       '.components.schemas.Thing.properties.kind.enum = ["a"]'

syn_case() {
    local want="$1" name="$2" desc="$3"
    expect "$want" "$desc" -- \
        --base "$SYN/base.json"      --base-lock "$SYN/base.lock.json" \
        --head "$SYN/$name.json"     --head-lock "$SYN/$name.lock.json"
}

echo "-- synthetic: compatible --"
expect 0 "identical documents" -- \
    --base "$SYN/base.json" --base-lock "$SYN/base.lock.json" \
    --head "$SYN/base.json" --head-lock "$SYN/base.lock.json"
syn_case 0 additive-property  "additive optional schema property"
syn_case 0 additive-path      "additive endpoint"
syn_case 0 additive-operation "additive operation on an existing path"
syn_case 0 additive-response  "additive response code"
syn_case 0 description-only   "documentation-only description change"

echo
echo "-- synthetic: breaking --"
syn_case 1 removed-path       "removed endpoint"
syn_case 1 removed-operation  "removed operation"
syn_case 1 removed-response   "removed response previously in the contract"
syn_case 1 removed-property   "removed public schema property"
syn_case 1 newly-required     "newly required request property"
syn_case 1 changed-type       "incompatible response property type change"
syn_case 1 narrowed-enum      "narrowed enum, an existing value disappears"

# ── Real-contract fixtures ────────────────────────────────────────────────
#
# openapi-diff 2.1.6 died with java.lang.StackOverflowError on exactly this
# document, and 2.1.7 still does. A gate that only ever sees a 40-line
# synthetic fixture would not have caught that.

echo
echo "-- real contract --"

REAL="$WORK/real"
mkdir -p "$REAL"
cp "$REAL_SPEC" "$REAL/base.json"
cp "$REAL_LOCK" "$REAL/base.lock.json"

expect 0 "real contract versus itself" -- \
    --base "$REAL/base.json" --base-lock "$REAL/base.lock.json" \
    --head "$REAL/base.json" --head-lock "$REAL/base.lock.json"

VICTIM_PATH="$(jq -r '.paths | keys_unsorted | .[0]' "$REAL/base.json")"
jq --arg p "$VICTIM_PATH" 'del(.paths[$p])' "$REAL/base.json" > "$REAL/removed.json"
write_lock "$REAL/removed.json" "$REAL/removed.lock.json"
expect 1 "real contract with '$VICTIM_PATH' removed" -- \
    --base "$REAL/base.json"    --base-lock "$REAL/base.lock.json" \
    --head "$REAL/removed.json" --head-lock "$REAL/removed.lock.json"

VICTIM_SCHEMA="$(jq -r '[.components.schemas | to_entries[] | select(.value.properties != null) | .key][0]' "$REAL/base.json")"
jq --arg s "$VICTIM_SCHEMA" \
   '.components.schemas[$s].properties.TesserafinCompatControlProperty = {"type": "string"}' \
   "$REAL/base.json" > "$REAL/added.json"
write_lock "$REAL/added.json" "$REAL/added.lock.json"
expect 0 "real contract with an optional property added to '$VICTIM_SCHEMA'" -- \
    --base "$REAL/base.json"  --base-lock "$REAL/base.lock.json" \
    --head "$REAL/added.json" --head-lock "$REAL/added.lock.json"

# ── Indeterminate: everything that means "we do not know" ─────────────────

echo
echo "-- indeterminate (must be red) --"

expect 2 "missing base contract" -- \
    --base "$REAL/does-not-exist.json" --base-lock "$REAL/base.lock.json" \
    --head "$REAL/base.json"           --head-lock "$REAL/base.lock.json"

expect 2 "missing head contract" -- \
    --base "$REAL/base.json"           --base-lock "$REAL/base.lock.json" \
    --head "$REAL/does-not-exist.json" --head-lock "$REAL/base.lock.json"

expect 2 "missing lock file" -- \
    --base "$REAL/base.json" --base-lock "$REAL/no-such.lock.json" \
    --head "$REAL/base.json" --head-lock "$REAL/base.lock.json"

BAD="$WORK/bad"
mkdir -p "$BAD"
printf '{ "openapi": "3.0.4", "paths": {' > "$BAD/truncated.json"
write_lock "$BAD/truncated.json" "$BAD/truncated.lock.json"
expect 2 "malformed JSON" -- \
    --base "$REAL/base.json"      --base-lock "$REAL/base.lock.json" \
    --head "$BAD/truncated.json"  --head-lock "$BAD/truncated.lock.json"

printf '{ "swagger": "2.0", "paths": { "/x": {} } }\n' > "$BAD/not-openapi3.json"
write_lock "$BAD/not-openapi3.json" "$BAD/not-openapi3.lock.json"
expect 2 "unsupported specification form (Swagger 2.0)" -- \
    --base "$REAL/base.json"          --base-lock "$REAL/base.lock.json" \
    --head "$BAD/not-openapi3.json"   --head-lock "$BAD/not-openapi3.lock.json"

printf '{ "openapi": "3.0.4", "info": { "title": "t", "version": "1" }, "paths": {} }\n' > "$BAD/no-paths.json"
write_lock "$BAD/no-paths.json" "$BAD/no-paths.lock.json"
expect 2 "contract declaring zero paths" -- \
    --base "$REAL/base.json"      --base-lock "$REAL/base.lock.json" \
    --head "$BAD/no-paths.json"   --head-lock "$BAD/no-paths.lock.json"

# The lock says one thing, the bytes say another. This is the guard that stops
# a hand-edited contract from being compared as if it were generated.
jq '.sha256 = "0000000000000000000000000000000000000000000000000000000000000000"' \
    "$REAL/base.lock.json" > "$REAL/wrong.lock.json"
expect 2 "real contract with a deliberately mismatched lock" -- \
    --base "$REAL/base.json" --base-lock "$REAL/base.lock.json" \
    --head "$REAL/base.json" --head-lock "$REAL/wrong.lock.json"

printf 'not json at all\n' > "$BAD/lock-garbage.json"
expect 2 "unparsable lock file" -- \
    --base "$REAL/base.json" --base-lock "$BAD/lock-garbage.json" \
    --head "$REAL/base.json" --head-lock "$REAL/base.lock.json"

jq 'del(.algorithm)' "$REAL/base.lock.json" > "$BAD/lock-no-algorithm.json"
expect 2 "lock file without an algorithm field" -- \
    --base "$REAL/base.json" --base-lock "$BAD/lock-no-algorithm.json" \
    --head "$REAL/base.json" --head-lock "$REAL/base.lock.json"

# A directory where an ordinary file is required.
mkdir -p "$BAD/a-directory.json"
expect 2 "contract path that is not an ordinary file" -- \
    --base "$BAD/a-directory.json" --base-lock "$REAL/base.lock.json" \
    --head "$REAL/base.json"       --head-lock "$REAL/base.lock.json"

# A report destination that cannot exist: its parent is a regular file, so
# `mkdir -p` fails for every user including root.
REPORT_BLOCKED="$REAL/base.json/report.md"
got=0
"$COMPAT" --base "$REAL/base.json" --base-lock "$REAL/base.lock.json" \
          --head "$REAL/base.json" --head-lock "$REAL/base.lock.json" \
          --report "$REPORT_BLOCKED" >/dev/null 2>&1 || got=$?
if [ "$got" -eq 2 ]; then
    ok "unwritable report destination (exit 2)"
else
    bad "unwritable report destination — expected exit 2, got $got"
fi

echo
echo "-- indeterminate: engine failures --"

# Both stubs are built FROM the pinned engine image, so they need no extra
# registry pull and cannot drift to a different base. OASDIFF_IMAGE selects
# which image runs and nothing else; the verdict logic is untouched, which is
# precisely what these two cases assert.
PINNED_IMAGE="$(
    # shellcheck disable=SC2046
    eval $(grep -E '^OASDIFF_(VERSION|DIGEST)=' "$COMPAT")
    printf 'tufin/oasdiff:%s@%s' "$OASDIFF_VERSION" "$OASDIFF_DIGEST"
)"
docker image inspect "$PINNED_IMAGE" >/dev/null 2>&1 || docker pull "$PINNED_IMAGE" >/dev/null

# Exits 0, prints nothing: the "tool claims success but there is no report"
# path. A gate that trusted the exit status alone would call this green.
printf 'FROM %s\nENTRYPOINT ["/bin/true"]\n' "$PINNED_IMAGE" | docker build -q -t "$STUB_SILENT" - >/dev/null
# Exits 1 with no output: an engine crash, which must not be read as "breaking".
printf 'FROM %s\nENTRYPOINT ["/bin/false"]\n' "$PINNED_IMAGE" | docker build -q -t "$STUB_CRASH" - >/dev/null

export OASDIFF_IMAGE="$STUB_SILENT"
expect 2 "engine exits 0 but produces no findings document" -- \
    --base "$REAL/base.json" --base-lock "$REAL/base.lock.json" \
    --head "$REAL/base.json" --head-lock "$REAL/base.lock.json"

export OASDIFF_IMAGE="$STUB_CRASH"
expect 2 "engine crashes" -- \
    --base "$REAL/base.json" --base-lock "$REAL/base.lock.json" \
    --head "$REAL/base.json" --head-lock "$REAL/base.lock.json"

export OASDIFF_IMAGE="tesserafin-openapi-no-such-image:v0"
expect 2 "engine image cannot be resolved" -- \
    --base "$REAL/base.json" --base-lock "$REAL/base.lock.json" \
    --head "$REAL/base.json" --head-lock "$REAL/base.lock.json"
unset OASDIFF_IMAGE

echo
echo "-- argument validation --"

expect 2 "missing --head" -- \
    --base "$REAL/base.json" --base-lock "$REAL/base.lock.json" \
    --head-lock "$REAL/base.lock.json"

expect 2 "unknown argument" -- \
    --base "$REAL/base.json" --base-lock "$REAL/base.lock.json" \
    --head "$REAL/base.json" --head-lock "$REAL/base.lock.json" \
    --allow-anything

echo
echo "-- repository invariants --"

# The tracked contract must still be exactly what the tracked lock pins. If
# this fails, every control above compared something that is not the contract.
if [ "$(sha256sum "$REAL_SPEC" | cut -d' ' -f1)" = "$(jq -r '.sha256' "$REAL_LOCK")" ]; then
    ok "committed contract matches committed contract.lock.json"
else
    bad "committed contract does not match committed contract.lock.json"
fi

if git -C "$REPO_ROOT" diff --quiet -- openapi/; then
    ok "controls left openapi/ untouched"
else
    bad "controls modified something under openapi/"
fi

echo
echo "OpenAPI compatibility gate controls: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
