#!/usr/bin/env bash
# Assert that a workflow file has no publication path (#236, W1-A4).
#
# Usage: assert-cannot-publish.sh <workflow.yml>
#
# This lives in its own file for a reason that is not stylistic. The first
# version of this check was written INLINE in the workflow it was checking, and
# it failed immediately — because the patterns it greps for (`packages: write`,
# `oras login`, `secrets.`) were themselves text in that file, as were the
# header comments explaining that none of them are present. A self-scanning
# check matches itself and reports the absence of a thing as its presence.
#
# So: the patterns live here, this file is never the file under test, and
# comments are stripped before anything is matched — otherwise documenting the
# property would break the check on it.
#
# Deliberately NOT `set -o pipefail` with `grep -q`: a match closes the pipe,
# the writer dies of SIGPIPE, and pipefail turns that into exit 141, which reads
# as an infrastructure flake rather than as the finding it is. Counts are
# compared instead.
set -eu

WORKFLOW="${1:-}"
if [ -z "${WORKFLOW}" ] || [ ! -f "${WORKFLOW}" ]; then
  echo "usage: assert-cannot-publish.sh <workflow.yml>" >&2
  exit 2
fi

# Strip whole-line comments and trailing ` # ...` comments. This is not a YAML
# parser and does not need to be: it only has to stop prose from being read as
# configuration. A `#` inside a quoted string would be mis-stripped, which can
# only ever make this check STRICTER, never weaker.
stripped="$(mktemp)"
trap 'rm -f "${stripped}"' EXIT
sed -e 's/[[:space:]]#.*$//' -e '/^[[:space:]]*#/d' "${WORKFLOW}" > "${stripped}"

fail=0
count() { grep -a -c -E "$1" "${stripped}" || true; }

check() {
  local pattern="$1" message="$2"
  if [ "$(count "${pattern}")" != "0" ]; then
    echo "FAIL: ${message}" >&2
    grep -a -n -E "${pattern}" "${stripped}" >&2 || true
    fail=1
  fi
}

check 'packages:[[:space:]]*write' "${WORKFLOW} grants packages: write"
check '(oras|docker|buildah|skopeo)[[:space:]]+login' "${WORKFLOW} logs in to a registry"
check 'password-stdin' "${WORKFLOW} pipes a credential to a client"
check 'oci-protocol\.sh[[:space:]]+(push|tag)' "${WORKFLOW} invokes a publishing subcommand"
check 'secrets\.[A-Za-z_]' "${WORKFLOW} references a secret"
check 'environment:' "${WORKFLOW} declares a deployment environment"

if [ "${fail}" -ne 0 ]; then
  echo "W1-A4 HARD STOP: ${WORKFLOW} has acquired a publication path" >&2
  exit 1
fi
echo "${WORKFLOW} has no write permission, no login, no secret and no publication path"
