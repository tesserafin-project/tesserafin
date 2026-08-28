#!/usr/bin/env bash
# Assert that a workflow file has no publication path (#236, W1-A4-R1).
#
# Usage: assert-cannot-publish.sh <workflow.yml>
#
# This used to BE the check: six greps over a comment-stripped copy of the
# workflow. R0's blocking finding F1 is that a regex is not a permission model.
# `permissions: write-all` grants packages: write without containing the string.
# A quoted `"packages": "write"` and an inline `{ packages: write }` are the same
# grant written differently. A job-level block can widen what the workflow level
# narrowed. And "this workflow contains no push" is a claim about the workflow's
# own text, not about the scripts it runs.
#
# So the interpretation moved into `publication_policy.py`, which parses the file
# as YAML, evaluates permissions at both levels against a closed permitted set,
# and follows the workflow's local script closure. This file stays as the entry
# point every caller already names, and because it is not the file under test its
# own text still cannot be mistaken for configuration.
set -eu

WORKFLOW="${1:-}"
if [ -z "${WORKFLOW}" ] || [ ! -f "${WORKFLOW}" ]; then
  echo "usage: assert-cannot-publish.sh <workflow.yml>" >&2
  exit 2
fi

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "${HERE}/publication_policy.py" "${WORKFLOW}"
