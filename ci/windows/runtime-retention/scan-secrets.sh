#!/usr/bin/env bash
# Refuse a retention unit that carries a credential (#236, W1-A4).
#
# The unit travels to a registry and, eventually, to the public. Everything in
# it was produced on a CI runner that HAD a token, so "no secret is in here" is
# a claim that has to be checked rather than assumed.
#
# Deliberately NOT `set -o pipefail` around the greps below. A `grep -q` that
# matches closes the pipe, the writer dies of SIGPIPE, and pipefail turns that
# into exit 141 — which reads as an infrastructure flake rather than as the
# finding it actually is. Exit codes are read directly instead.
set -eu

UNIT="${1:-}"
if [ -z "${UNIT}" ] || [ ! -d "${UNIT}" ]; then
  echo "usage: scan-secrets.sh <unit-root>" >&2
  exit 2
fi

# Patterns, and why each one is here:
#   ghp_/gho_/ghu_/ghs_/ghr_  GitHub token prefixes
#   github_pat_               fine-grained PAT prefix
#   ACTIONS_RUNTIME_TOKEN     the runner's own artifact token
#   ACTIONS_ID_TOKEN_REQUEST  the OIDC request URL and its token
#   x-access-token            how a token is smuggled into a git remote
#   AKIA/ASIA                 AWS access key ids
#   BEGIN .* PRIVATE KEY      any PEM private key
PATTERNS='ghp_[A-Za-z0-9]{20}|gho_[A-Za-z0-9]{20}|ghu_[A-Za-z0-9]{20}|ghs_[A-Za-z0-9]{20}|ghr_[A-Za-z0-9]{20}|github_pat_[A-Za-z0-9_]{20}|ACTIONS_RUNTIME_TOKEN|ACTIONS_ID_TOKEN_REQUEST|x-access-token:|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|BEGIN [A-Z ]*PRIVATE KEY'

findings=0

while IFS= read -r file; do
  # Text-shaped files only. The runtime zip and the source tarball are
  # compressed streams; grepping them yields noise, and their digests are
  # pinned by the acceptance manifest anyway.
  case "${file}" in
    *.zip|*.tar.zst|*.tar.gz|*.7z) continue ;;
  esac

  set +e
  matches="$(grep -a -n -E -m 3 "${PATTERNS}" "${file}" 2>/dev/null)"
  status=$?
  set -e

  if [ "${status}" -eq 0 ] && [ -n "${matches}" ]; then
    echo "CREDENTIAL-SHAPED CONTENT in ${file#"${UNIT}"/}:" >&2
    # Print the line number and the matched PATTERN NAME only. Echoing the
    # matched text would copy a live credential into a CI log, which is the
    # thing this check exists to prevent.
    echo "${matches}" | sed -E 's/^([0-9]+):.*/  line \1/' >&2
    findings=$((findings + 1))
  fi
done < <(find "${UNIT}" -type f | sort)

if [ "${findings}" -ne 0 ]; then
  echo "W1-A4 RETENTION HARD STOP: ${findings} file(s) carry credential-shaped content" >&2
  exit 1
fi

echo "no credential-shaped content in the retained unit"
