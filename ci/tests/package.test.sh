#!/usr/bin/env bash
# Unit tests for the native Linux packaging logic (#225 / [L0]).
#
# Pure logic only: architecture mapping, pin agreement, template completeness,
# determinism helpers and the workflow's publication safety. No docker, no
# dotnet, no network — the heavy end-to-end evidence is the hosted lifecycle and
# reproducibility jobs, and duplicating it here would only make it slower.
#
# Usage: ci/tests/package.test.sh

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PASSED=0
FAILED=0

ok()   { PASSED=$((PASSED + 1)); echo "  ok   $*"; }
bad()  { FAILED=$((FAILED + 1)); echo "  FAIL $*" >&2; }
check() { # <description> <expected> <actual>
    if [[ "$2" == "$3" ]]; then ok "$1"; else bad "$1 — expected '$2', got '$3'"; fi
}

# The library sets `set -euo pipefail`; the harness deliberately does not, so a
# single failing assertion reports and the rest still run.
# shellcheck source=ci/package/lib.sh
source "${REPO_ROOT}/ci/package/lib.sh"
set +e

echo "== architecture mapping"
check "linux-x64 is amd64 on Debian"    "amd64"   "$(pkg_deb_arch linux-x64)"
check "linux-arm64 is arm64 on Debian"  "arm64"   "$(pkg_deb_arch linux-arm64)"
check "linux-x64 is x86_64 on RPM"      "x86_64"  "$(pkg_rpm_arch linux-x64)"
check "linux-arm64 is aarch64 on RPM"   "aarch64" "$(pkg_rpm_arch linux-arm64)"

if ( pkg_deb_arch linux-riscv64 >/dev/null 2>&1 ); then
    bad "an unsupported runtime identifier was accepted"
else
    ok "an unsupported runtime identifier is rejected rather than guessed"
fi

echo "== pinned inputs"
( pkg_load_pins ) >/dev/null 2>&1 && ok "pins load and agree with the Dockerfile" \
    || bad "pkg_load_pins failed: the Dockerfile and pins.env disagree"

pkg_load_pins >/dev/null 2>&1
check "the ffmpeg asset name follows the runtime identifier" \
      "jellyfin-ffmpeg_${FFMPEG_VERSION}_portable_linux64-gpl.tar.xz" \
      "$(pkg_ffmpeg_asset linux-x64)"
check "the arm64 ffmpeg asset name follows the runtime identifier" \
      "jellyfin-ffmpeg_${FFMPEG_VERSION}_portable_linuxarm64-gpl.tar.xz" \
      "$(pkg_ffmpeg_asset linux-arm64)"

for pin in WEB_ASSETS_IMAGE WEB_VCS_REF FFMPEG_VERSION WEB_PAYLOAD_SHA256 \
           FFMPEG_PORTABLE_SHA256_LINUX_X64 FFMPEG_PORTABLE_SHA256_LINUX_ARM64 \
           RPM_BUILDER_IMAGE RPM_ACCEPT_IMAGE ARCHIVE_ACCEPT_IMAGE; do
    if [[ -n "${!pin:-}" ]]; then ok "${pin} is pinned"; else bad "${pin} is empty"; fi
done

for sha in "${WEB_PAYLOAD_SHA256}" "${FFMPEG_PORTABLE_SHA256_LINUX_X64}" \
           "${FFMPEG_PORTABLE_SHA256_LINUX_ARM64}"; do
    if [[ "${sha}" =~ ^[0-9a-f]{64}$ ]]; then
        ok "a pinned digest is a full lowercase SHA-256"
    else
        bad "'${sha}' is not a full lowercase SHA-256"
    fi
done

# A drifting pin must break the build rather than silently win.
echo "== pin drift fails closed"
DRIFT_DIR="$(mktemp -d)"
cp -a "${REPO_ROOT}/Dockerfile" "${REPO_ROOT}/ci" "${REPO_ROOT}/docker" "${DRIFT_DIR}/" 2>/dev/null
sed -i -E 's/^ARG FFMPEG_VERSION=.*/ARG FFMPEG_VERSION=0.0.0-drift/' "${DRIFT_DIR}/Dockerfile"
if ( cd "${DRIFT_DIR}" && bash -c 'source ci/package/lib.sh; PKG_REPO_ROOT="'"${DRIFT_DIR}"'"; PKG_PINS_FILE="$PKG_REPO_ROOT/ci/package/pins.env"; PKG_DOCKERFILE="$PKG_REPO_ROOT/Dockerfile"; pkg_load_pins' ) >/dev/null 2>&1; then
    bad "an ffmpeg version disagreement between the Dockerfile and pins.env was accepted"
else
    ok "an ffmpeg version disagreement fails closed"
fi
rm -rf "${DRIFT_DIR}"

echo "== packaging templates are complete"
for template in packaging/linux/deb/control.in packaging/linux/rpm/tesserafin-server.spec.in \
                packaging/linux/archive/README.md.in; do
    placeholders="$(grep -oE '@[A-Z_]+@' "${REPO_ROOT}/${template}" | sort -u | tr '\n' ' ')"
    if [[ -n "${placeholders}" ]]; then
        ok "${template} declares placeholders: ${placeholders}"
    else
        bad "${template} has no placeholders, so nothing is substituted"
    fi
done

# Every placeholder a template declares must be substituted by some build script,
# or an artifact ships a literal @TOKEN@.
declare -A SUBSTITUTED=()
while read -r token; do SUBSTITUTED["${token}"]=1; done < <(
    grep -ohE 's\|@[A-Z_]+@\|' "${REPO_ROOT}"/ci/package/build-*.sh \
      | sed -E 's/^s\|(@[A-Z_]+@)\|$/\1/' | sort -u
)
for template in packaging/linux/deb/control.in packaging/linux/rpm/tesserafin-server.spec.in \
                packaging/linux/archive/README.md.in; do
    while read -r token; do
        [[ -n "${token}" ]] || continue
        if [[ -n "${SUBSTITUTED[${token}]:-}" ]]; then
            ok "${token} in ${template} is substituted by a build script"
        else
            bad "${token} in ${template} is never substituted"
        fi
    done < <(grep -oE '@[A-Z_]+@' "${REPO_ROOT}/${template}" | sort -u)
done

echo "== the service contract"
UNIT="${REPO_ROOT}/packaging/linux/tesserafin.service"
grep -q '^User=tesserafin$'  "${UNIT}" && ok "the unit runs as the tesserafin user" \
    || bad "the unit does not set User=tesserafin"
grep -q -- '--webdir' "${UNIT}" && ok "the unit hosts the bundled web client" \
    || bad "the unit does not pass --webdir"
# Comments discuss --nowebclient on purpose; only executable directives count.
if grep -v '^\s*#' "${UNIT}" | grep -q -- '--nowebclient'; then
    bad "the unit uses --nowebclient"
else
    ok "the unit never uses --nowebclient"
fi
grep -q -- '--ffmpeg' "${UNIT}" && ok "the unit pins the encoder explicitly" \
    || bad "the unit does not pass --ffmpeg, so the server could fall back to \$PATH"

for directive in PrivateDevices DevicePolicy DeviceAllow ProtectClock PrivateMounts; do
    if grep -qE "^${directive}=" "${UNIT}"; then
        bad "the unit sets ${directive}, which can block VAAPI/NVIDIA device access"
    else
        ok "the unit does not set ${directive}"
    fi
done

echo "== maintainer scripts never delete state"
for script in packaging/linux/deb/postinst packaging/linux/deb/prerm packaging/linux/deb/postrm; do
    if grep -qE 'rm[[:space:]]+-[a-zA-Z]*r[a-zA-Z]*[[:space:]]+/(var|etc)/' "${REPO_ROOT}/${script}"; then
        bad "${script} recursively removes a state or configuration path"
    else
        ok "${script} does not recursively remove state or configuration"
    fi
done
if grep -qE '^\s*(userdel|groupdel)' "${REPO_ROOT}"/packaging/linux/deb/post* ; then
    bad "a maintainer script deletes the service account, orphaning its files"
else
    ok "no maintainer script deletes the service account"
fi
if grep -qE '(userdel|groupdel|rm -rf /var/lib/tesserafin)' \
        "${REPO_ROOT}/packaging/linux/rpm/tesserafin-server.spec.in"; then
    bad "the spec deletes state or the service account"
else
    ok "the spec deletes neither state nor the service account"
fi

echo "== the workflow cannot publish"
WF="${REPO_ROOT}/.github/workflows/linux-packages.yml"
# The header comment explains what the workflow deliberately does NOT do, so the
# assertion has to read directives rather than prose.
WF_DIRECTIVES="$(grep -v '^\s*#' "${WF}")"
for forbidden in 'release:' 'tags:' 'docker push' 'buildx.*--push' 'gh release'; do
    if grep -qE "${forbidden}" <<<"${WF_DIRECTIVES}"; then
        bad "the workflow contains '${forbidden}', which could publish"
    else
        ok "the workflow contains no '${forbidden}'"
    fi
done
if grep -qE 'uses: [^@]+@[0-9a-f]{40}' "${WF}"; then
    unpinned="$(grep -E '^\s+uses:' "${WF}" | grep -vE '@[0-9a-f]{40}' || true)"
    if [[ -n "${unpinned}" ]]; then
        bad "unpinned action reference: ${unpinned}"
    else
        ok "every action is pinned to a full commit SHA"
    fi
else
    bad "the workflow pins no actions by SHA"
fi
grep -q 'ubuntu-24.04-arm' "${WF}" && ok "arm64 acceptance runs on a native arm64 runner" \
    || bad "no native arm64 runner in the workflow"

echo "== embedded build-path classification"
#
# Regression control for the hygiene gate. It used to reject any artifact
# containing `${HOME}`, which on a hosted runner is `/home/runner` — the same
# prefix several upstream NuGet assemblies embed from their OWN Actions
# workspace. The gate therefore failed on every hosted build while passing on a
# workstation, and the verdict depended on who was building rather than on what
# was built. These assertions pin both halves: the enumerated upstream paths are
# accepted, a Tesserafin path never is, and neither answer moves with ${HOME}.

EMB="$(mktemp -d)"
mkdir -p "${EMB}/root" "${EMB}/empty"
# A real assembly stores the path NUL-terminated; the fixtures do the same, so
# the extracted string is the whole path and nothing adjacent to it.
embed() { printf '%s\000' "$2" > "${EMB}/root/$1"; }
# Exactly the first enumerated upstream pair.
ALLOWED_NAME="$(grep -vE '^\s*(#|$)' "${REPO_ROOT}/ci/package/embedded-build-paths.allow" \
                | head -1 | awk '{print $1}')"
ALLOWED_PATH="$(grep -vE '^\s*(#|$)' "${REPO_ROOT}/ci/package/embedded-build-paths.allow" \
                | head -1 | awk '{print $2}')"
embed "${ALLOWED_NAME}" "${ALLOWED_PATH}"

scan_verdicts() { # <home> -> the sorted set of verdicts produced
    HOME="$1" pkg_scan_embedded_build_paths "${EMB}/root" | cut -f1 | LC_ALL=C sort -u | tr '\n' ' '
}

check "an enumerated upstream path is accepted on a hosted runner's HOME" \
      "KNOWN " "$(scan_verdicts /home/runner)"
check "the same tree gets the same verdict on a workstation HOME" \
      "KNOWN " "$(scan_verdicts /home/somebody-else)"

# A first-party path is a leak no matter which account produced it.
embed Tesserafin.Server.dll /home/runner/work/tesserafin/tesserafin/obj/Release/Tesserafin.Server.pdb
if HOME=/home/runner pkg_scan_embedded_build_paths "${EMB}/root" | grep -q '^LEAK'; then
    ok "a Tesserafin workspace path is reported as a leak"
else
    bad "a Tesserafin workspace path was not reported as a leak"
fi
rm -f "${EMB}/root/Tesserafin.Server.dll"

# An upstream-looking path that is not enumerated is a leak too: the list is
# closed, so a new dependency embedding a path is reviewed, not absorbed.
embed SomeNewDep.dll /home/runner/work/SomeNewDep/SomeNewDep/obj/Release/SomeNewDep.pdb
if HOME=/home/runner pkg_scan_embedded_build_paths "${EMB}/root" | grep -q '^LEAK'; then
    ok "an unenumerated upstream path is reported as a leak"
else
    bad "an unenumerated upstream path was accepted"
fi
rm -f "${EMB}/root/SomeNewDep.dll"

# The enumeration must not be usable to excuse a first-party path.
FORGED="${EMB}/forged.allow"
printf 'Tesserafin.Server.dll /home/runner/work/tesserafin/tesserafin/x.pdb\n' > "${FORGED}"
if ( PKG_EMBEDDED_ALLOW_FILE="${FORGED}" pkg_load_embedded_allowlist ) >/dev/null 2>&1; then
    bad "the enumeration accepted an entry naming Tesserafin"
else
    ok "an enumeration entry naming Tesserafin fails closed"
fi

# A tree with no build paths at all produces no findings, so "silence" cannot be
# confused with "the scan did not run".
check "a clean tree produces no findings" "" \
      "$(pkg_scan_embedded_build_paths "${EMB}/empty" | tr -d '\n')"
rm -rf "${EMB}"

echo "== deterministic tar"
SOURCE_DATE_EPOCH=1000000000
T1="$(mktemp -d)"; printf 'a' > "${T1}/a"; printf 'b' > "${T1}/b"
D1="$(pkg_tree_digest "${T1}")"
touch -d '2030-01-01' "${T1}/a" "${T1}/b"
D2="$(pkg_tree_digest "${T1}")"
check "the tree digest ignores mtimes" "${D1}" "${D2}"
D3="$(pkg_tree_digest "${T1}" 2000000000)"
if [[ "${D1}" == "${D3}" ]]; then
    bad "the clamp epoch does not affect the digest, so it is not being applied"
else
    ok "the clamp epoch is applied to the digest"
fi
rm -rf "${T1}"

echo
echo "passed: ${PASSED}  failed: ${FAILED}"
[[ "${FAILED}" -eq 0 ]] || exit 1
