#!/usr/bin/env bash
# Guard against NEWLY introduced references to the former GitHub organisation.
#
# The canonical namespace is `tesserafin` (github.com/tesserafin/…,
# ghcr.io/tesserafin/…). It used to be `tesserafin-project`. Every ACTIVE
# operational reference — installation defaults, registry constants, workflow
# inputs, OCI labels, CODEOWNERS, clone URLs and current operator documentation —
# was migrated by the namespace cutover (#147).
#
# This gate fails when `tesserafin-project` reappears in a tracked file that is
# not on the historical allowlist below, so the old namespace cannot silently
# return through a copy/paste, a stale branch or a bad merge.
#
# It is NOT a blanket "no tesserafin-project anywhere" scan. The allowlist names
# the records that must keep stating where an artifact was originally published:
# renaming the organisation does not retroactively change the registry path a
# past release was pushed to, and rewriting those records would destroy the
# evidence. Each allowlisted path carries a "Namespace note" blockquote saying
# exactly that.
#
# Usage:
#   ./ci/verify-namespace.sh
#
# Exit status: 0 when clean, 1 on any unallowlisted occurrence.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

FORBIDDEN='tesserafin-project'

# Historical records. Preserved verbatim, deliberately, with a namespace note.
#
#   docs/container/A1,A2,A5      implementation notes for shipped deliverables;
#                                the recorded pull transcripts and tag schemes
#                                name the path each image was published to.
#   docs/container/A6            MIXED. Sections 1-5 are the live upgrade
#                                contract and were migrated. Sections 5b, 5c
#                                and 6 are recorded validation and audit
#                                evidence and keep the old path.
#   docs/container/A7            the server<->web release-pair evidence log:
#                                immutable identities and the runs made against
#                                them.
#   docs/local-ci.md             a dated archive of the July 2026 hosted-runner
#                                outage, including the run URL that proved it
#                                was over.
#   docker/browser-gate/…        a regression-guard comment citing the web
#                                issue that motivated the assertion.
#   ci/verify-namespace.sh       this gate names the forbidden string.
ALLOWLIST=(
    'ci/verify-namespace.sh'
    'docs/container/A1-implementation-note.md'
    'docs/container/A2-persistent-state.md'
    'docs/container/A5-observability.md'
    'docs/container/A6-versioning-and-upgrades.md'
    'docs/container/A7-server-web-release-pair.md'
    'docs/local-ci.md'
    'docker/browser-gate/tests/onboarding.spec.ts'
)

is_allowlisted() {
    local candidate="$1" allowed
    for allowed in "${ALLOWLIST[@]}"; do
        [[ "$candidate" == "$allowed" ]] && return 0
    done
    return 1
}

violations=()
scanned=0
while IFS= read -r file; do
    [[ -f "$file" ]] || continue
    scanned=$((scanned + 1))
    is_allowlisted "$file" && continue
    while IFS= read -r hit; do
        violations+=("${file}:${hit}")
    done < <(grep -n -a -F -- "$FORBIDDEN" "$file" || true)
done < <(git ls-files)

# An allowlist entry that no longer contains the old namespace is stale: the
# record was rewritten, or the path moved. Fail rather than let the list rot
# into a set of silent exemptions for files nobody checks any more.
stale=()
for allowed in "${ALLOWLIST[@]}"; do
    [[ "$allowed" == 'ci/verify-namespace.sh' ]] && continue
    if [[ ! -f "$allowed" ]]; then
        stale+=("$allowed (missing)")
    elif ! grep -q -a -F -- "$FORBIDDEN" "$allowed"; then
        stale+=("$allowed (no longer contains the old namespace)")
    fi
done

status=0
if [[ ${#violations[@]} -gt 0 ]]; then
    echo "[verify:namespace] FAIL — ${#violations[@]} active reference(s) to '${FORBIDDEN}':" >&2
    printf '  %s\n' "${violations[@]}" >&2
    echo "" >&2
    echo "The canonical namespace is 'tesserafin'. If an occurrence is genuinely a" >&2
    echo "historical record, add its path to ALLOWLIST in this script WITH a reason." >&2
    status=1
fi
if [[ ${#stale[@]} -gt 0 ]]; then
    echo "[verify:namespace] FAIL — ${#stale[@]} stale allowlist entr(y|ies):" >&2
    printf '  %s\n' "${stale[@]}" >&2
    status=1
fi

if [[ $status -eq 0 ]]; then
    echo "[verify:namespace] OK — scanned ${scanned} tracked files, no active '${FORBIDDEN}' reference (${#ALLOWLIST[@]} historical records allowlisted)."
fi
exit "$status"
