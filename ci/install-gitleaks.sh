#!/usr/bin/env bash
#
# Install the pinned Gitleaks release, verifying the archive BEFORE anything in
# it is executed.
#
# WHY A PINNED RELEASE ARCHIVE AND NOTHING ELSE.
#
#   * Not `gitleaks/gitleaks-action`. It is a mutable third-party action, and
#     since v2 it calls home for a licence key for organisation-owned
#     repositories. This gate takes no licence secret, so it cannot use it.
#   * Not `latest`, not a floating tag, not an unpinned container. A scanner
#     that can change under the gate makes every past green verdict unfalsifiable.
#   * Not a locally rebuilt binary. A scanner built here is not the scanner CI
#     runs, and the two would diverge silently.
#   * Not v8.30.1. Its published assets changed after the release was cut, so the
#     bytes a verifier fetched at one moment were not the bytes another verifier
#     fetched later. The current downloads do now agree with the current
#     checksums file, and that is precisely the problem: a checksum file that
#     tracks a mutated asset cannot detect the mutation. Verify-before-execute
#     requires an archive digest fixed at review time, so this gate stays on
#     v8.30.0, whose digest below was recorded and re-verified independently.
#
# The archive SHA-256 is committed here rather than fetched from the release's
# own checksums.txt. Fetching the expected value from the same server that
# serves the artifact proves only that the server is self-consistent.
#
# Exit codes: 0 installed, 2 anything else. 2 is this repository's INDETERMINATE
# verdict — an installation that did not happen must never read as a clean scan.

set -euo pipefail

GITLEAKS_VERSION="8.30.0"
GITLEAKS_RELEASE_COMMIT="6eaad039603a4de39fddd1cf5f727391efe9974e"
GITLEAKS_ARCHIVE="gitleaks_${GITLEAKS_VERSION}_linux_x64.tar.gz"
GITLEAKS_ARCHIVE_SHA256="79a3ab579b53f71efd634f3aaf7e04a0fa0cf206b7ed434638d1547a2470a66e"
GITLEAKS_URL="https://github.com/gitleaks/gitleaks/releases/download/v${GITLEAKS_VERSION}/${GITLEAKS_ARCHIVE}"

# Deliberately outside any repository working tree: an installed scanner inside
# the tree would be scanned by the tree scan and would dirty `git status`.
BIN_DIR="${GITLEAKS_BIN_DIR:-${TMPDIR:-/tmp}/tesserafin-gitleaks-${GITLEAKS_VERSION}}"

# Overridable so the deterministic controls can point the installer at a
# corrupted archive and assert it refuses. Nothing else may set them.
EXPECTED_SHA256="$GITLEAKS_ARCHIVE_SHA256"
SOURCE_URL="$GITLEAKS_URL"
FORCE=0

die() {
    printf 'install-gitleaks: INDETERMINATE: %s\n' "$1" >&2
    exit 2
}

usage() {
    cat <<'EOF'
Usage: ci/install-gitleaks.sh [options]

  --bin-dir DIR        install directory (default $GITLEAKS_BIN_DIR, else a
                       versioned directory under $TMPDIR)
  --source URL|PATH    archive source; a local path is copied, not downloaded
  --expected-sha256 H  override the expected archive digest (controls only)
  --force              reinstall even if the provenance file already verifies
  --print-bin          print the resolved binary path and exit 0

Exit codes: 0 installed and verified, 2 indeterminate.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --bin-dir) BIN_DIR="${2:?--bin-dir needs a value}"; shift 2 ;;
        --source) SOURCE_URL="${2:?--source needs a value}"; shift 2 ;;
        --expected-sha256) EXPECTED_SHA256="${2:?--expected-sha256 needs a value}"; shift 2 ;;
        --force) FORCE=1; shift ;;
        --print-bin) printf '%s/gitleaks\n' "$BIN_DIR"; exit 0 ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

for tool in curl tar sha256sum; do
    command -v "$tool" >/dev/null 2>&1 || die "required tool '$tool' is not on PATH"
done

BIN="$BIN_DIR/gitleaks"
PROVENANCE="$BIN_DIR/gitleaks.provenance"

# Already installed and still the bytes we verified? Then this is a no-op.
if [ "$FORCE" -eq 0 ] && [ -x "$BIN" ] && [ -f "$PROVENANCE" ]; then
    recorded="$(awk '$1 == "binary_sha256" { print $2 }' "$PROVENANCE" 2>/dev/null || true)"
    actual="$(sha256sum "$BIN" | cut -d' ' -f1)"
    if [ -n "$recorded" ] && [ "$recorded" = "$actual" ]; then
        printf 'install-gitleaks: %s already installed at %s\n' "$GITLEAKS_VERSION" "$BIN"
        exit 0
    fi
fi

WORK="$(mktemp -d)"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

ARCHIVE="$WORK/$GITLEAKS_ARCHIVE"

if [ -f "$SOURCE_URL" ]; then
    cp -- "$SOURCE_URL" "$ARCHIVE" || die "could not read local archive '$SOURCE_URL'"
else
    curl --fail --silent --show-error --location --retry 3 --retry-delay 2 \
        --max-time 300 --output "$ARCHIVE" "$SOURCE_URL" \
        || die "download failed from $SOURCE_URL"
fi

[ -s "$ARCHIVE" ] || die "downloaded archive is empty"

ACTUAL_SHA256="$(sha256sum "$ARCHIVE" | cut -d' ' -f1)"
if [ "$ACTUAL_SHA256" != "$EXPECTED_SHA256" ]; then
    # The digests are not secrets; printing them is what makes the refusal
    # auditable. Nothing from inside the archive has been executed.
    die "archive digest mismatch: expected $EXPECTED_SHA256, got $ACTUAL_SHA256"
fi

mkdir -p "$BIN_DIR" || die "could not create '$BIN_DIR'"
tar -xzf "$ARCHIVE" -C "$WORK" gitleaks || die "archive does not contain a 'gitleaks' entry"
install -m 0755 "$WORK/gitleaks" "$BIN" || die "could not install to '$BIN'"

REPORTED="$("$BIN" version 2>/dev/null | tr -d '[:space:]')" || die "installed binary will not run"
[ "$REPORTED" = "$GITLEAKS_VERSION" ] \
    || die "installed binary reports '$REPORTED', expected '$GITLEAKS_VERSION'"

BINARY_SHA256="$(sha256sum "$BIN" | cut -d' ' -f1)"

# The provenance file is what ci/secret-scan.sh re-verifies before it trusts the
# binary. Without it, a scanner replaced after installation would go unnoticed.
cat >"$PROVENANCE" <<EOF
version $GITLEAKS_VERSION
release_commit $GITLEAKS_RELEASE_COMMIT
archive $GITLEAKS_ARCHIVE
archive_sha256 $ACTUAL_SHA256
binary_sha256 $BINARY_SHA256
EOF
chmod 0644 "$PROVENANCE"

printf 'install-gitleaks: gitleaks %s installed at %s\n' "$GITLEAKS_VERSION" "$BIN"
printf 'install-gitleaks: archive sha256 %s (verified before extraction)\n' "$ACTUAL_SHA256"
