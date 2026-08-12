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

for pin in WEB_ASSETS_IMAGE WEB_VCS_REF WEB_PAYLOAD_SHA256 \
           F0_RUNTIME_REVISION F0_UPSTREAM_COMMIT F0_RUNTIME_LICENSE F0_ACCEPTED_CI_TREE \
           RPM_BUILDER_IMAGE RPM_ACCEPT_IMAGE ARCHIVE_ACCEPT_IMAGE; do
    if [[ -n "${!pin:-}" ]]; then ok "${pin} is pinned"; else bad "${pin} is empty"; fi
done

if [[ "${WEB_PAYLOAD_SHA256}" =~ ^[0-9a-f]{64}$ ]]; then
    ok "the pinned web payload digest is a full lowercase SHA-256"
else
    bad "'${WEB_PAYLOAD_SHA256}' is not a full lowercase SHA-256"
fi

# The packages must not carry a pin for a downloaded encoder any more. These are
# not decorative: reintroducing either name is exactly how the inherited runtime
# would come back, and ci/package/verify-no-inherited-ffmpeg.sh enforces the same
# boundary over the whole package surface.
echo "== the inherited portable-runtime pins are gone"
for gone in FFMPEG_VERSION FFMPEG_PORTABLE_SHA256_LINUX_X64 FFMPEG_PORTABLE_SHA256_LINUX_ARM64; do
    if [[ -n "${!gone:-}" ]]; then
        bad "${gone} is still set after pkg_load_pins"
    else
        ok "${gone} is no longer a package pin"
    fi
done
for gone_fn in pkg_ffmpeg_asset pkg_ffmpeg_sha256; do
    if declare -F "${gone_fn}" >/dev/null; then
        bad "${gone_fn} still exists, so a release asset can still be constructed"
    else
        ok "${gone_fn} no longer exists"
    fi
done

echo "== the F0 runtime is derived from ci/ffmpeg, not restated"
check "the runtime revision comes from components.json" \
      "$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["buildRevision"])' \
         "${REPO_ROOT}/ci/ffmpeg/components.json")" \
      "${F0_BUILD_REVISION}"
check "the upstream commit comes from components.json" \
      "$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["ffmpeg"]["commit"])' \
         "${REPO_ROOT}/ci/ffmpeg/components.json")" \
      "${F0_FFMPEG_COMMIT}"
check "the runtime directory name is derived, not spelled out" \
      "tesserafin-ffmpeg-${F0_BUILD_REVISION}-linux-arm64" \
      "$(pkg_f0_runtime_dir_name linux-arm64)"
check "the runtime archive name is derived, not spelled out" \
      "tesserafin-ffmpeg-${F0_BUILD_REVISION}-linux-x64.tar.xz" \
      "$(pkg_f0_runtime_archive_name linux-x64)"
check "the corresponding-source name is derived, not spelled out" \
      "tesserafin-ffmpeg-${F0_BUILD_REVISION}-corresponding-source.tar.zst" \
      "${F0_SOURCE_ARCHIVE}"
check "the ELF machine is declared per architecture" \
      "AArch64" "$(pkg_elf_machine linux-arm64)"

# The adapter must not have grown a copy of any F0 configuration. If it ever
# restates a configure flag or a component pin, the two definitions can drift and
# the packages stop shipping the accepted runtime.
if grep -qE -- '--enable-|--disable-|libx26|libvpx|nv-codec' "${REPO_ROOT}/ci/package/ffmpeg-runtime.sh"; then
    bad "the package adapter restates FFmpeg configuration that belongs to ci/ffmpeg"
else
    ok "the package adapter restates no FFmpeg configuration"
fi

# A drifting pin must break the build rather than silently win.
echo "== pin drift fails closed"
DRIFT_DIR="$(mktemp -d)"
cp -a "${REPO_ROOT}/Dockerfile" "${REPO_ROOT}/ci" "${REPO_ROOT}/docker" "${DRIFT_DIR}/" 2>/dev/null
sed -i -E 's/"buildRevision": *"[^"]*"/"buildRevision": "0.0.0-drift"/' \
    "${DRIFT_DIR}/ci/ffmpeg/components.json"
if ( cd "${DRIFT_DIR}" && bash -c 'source ci/package/lib.sh; PKG_REPO_ROOT="'"${DRIFT_DIR}"'"; PKG_PINS_FILE="$PKG_REPO_ROOT/ci/package/pins.env"; PKG_DOCKERFILE="$PKG_REPO_ROOT/Dockerfile"; PKG_F0_COMPONENTS="$PKG_REPO_ROOT/ci/ffmpeg/components.json"; pkg_load_pins' ) >/dev/null 2>&1; then
    bad "a runtime revision disagreement between components.json and pins.env was accepted"
else
    ok "a runtime revision disagreement fails closed"
fi

sed -i -E 's/"commit": *"[0-9a-f]{40}"/"commit": "0000000000000000000000000000000000000000"/' \
    "${DRIFT_DIR}/ci/ffmpeg/components.json"
sed -i -E 's/"buildRevision": *"[^"]*"/"buildRevision": "7.1.4-tesserafin.1"/' \
    "${DRIFT_DIR}/ci/ffmpeg/components.json"
if ( cd "${DRIFT_DIR}" && bash -c 'source ci/package/lib.sh; PKG_REPO_ROOT="'"${DRIFT_DIR}"'"; PKG_PINS_FILE="$PKG_REPO_ROOT/ci/package/pins.env"; PKG_DOCKERFILE="$PKG_REPO_ROOT/Dockerfile"; PKG_F0_COMPONENTS="$PKG_REPO_ROOT/ci/ffmpeg/components.json"; pkg_load_pins' ) >/dev/null 2>&1; then
    bad "an upstream commit disagreement between components.json and pins.env was accepted"
else
    ok "an upstream commit disagreement fails closed"
fi
rm -rf "${DRIFT_DIR}"

echo "== the negative gate rejects a reintroduced portable asset"
GATE_DIR="$(mktemp -d)"
cp -a "${REPO_ROOT}/ci" "${REPO_ROOT}/packaging" "${REPO_ROOT}/docs" "${REPO_ROOT}/.github" "${GATE_DIR}/" 2>/dev/null
if ( "${REPO_ROOT}/ci/package/verify-no-inherited-ffmpeg.sh" --root "${GATE_DIR}" ) >/dev/null 2>&1; then
    ok "the current package surface passes the inherited-runtime gate"
else
    bad "the current package surface fails its own inherited-runtime gate"
fi
printf 'FFMPEG_PORTABLE_SHA256_LINUX_X64=cab9ff40a47e4232d231e4eb7e4e85fabfeec56c6905266bc94291fc0881f83f\n' \
    >> "${GATE_DIR}/ci/package/pins.env"
if ( "${REPO_ROOT}/ci/package/verify-no-inherited-ffmpeg.sh" --root "${GATE_DIR}" ) >/dev/null 2>&1; then
    bad "the gate accepted a reintroduced portable checksum pin"
else
    ok "a reintroduced portable checksum pin is rejected"
fi
rm -rf "${GATE_DIR}"

echo "== packaging templates are complete"
for template in packaging/linux/deb/control.in packaging/linux/deb/copyright.in \
                packaging/linux/rpm/tesserafin-server.spec.in \
                packaging/linux/archive/README.md.in packaging/linux/archive/LICENSES.md.in \
                packaging/linux/FFMPEG-CORRESPONDING-SOURCE.txt.in; do
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
                               "${REPO_ROOT}"/ci/package/assemble-payload.sh \
      | sed -E 's/^s\|(@[A-Z_]+@)\|$/\1/' | sort -u
)
for template in packaging/linux/deb/control.in packaging/linux/deb/copyright.in \
                packaging/linux/rpm/tesserafin-server.spec.in \
                packaging/linux/archive/README.md.in packaging/linux/archive/LICENSES.md.in \
                packaging/linux/FFMPEG-CORRESPONDING-SOURCE.txt.in; do
    while read -r token; do
        [[ -n "${token}" ]] || continue
        if [[ -n "${SUBSTITUTED[${token}]:-}" ]]; then
            ok "${token} in ${template} is substituted by a build script"
        else
            bad "${token} in ${template} is never substituted"
        fi
    done < <(grep -oE '@[A-Z_]+@' "${REPO_ROOT}/${template}" | sort -u)
done

echo "== licence metadata states both boundaries"
SPEC="${REPO_ROOT}/packaging/linux/rpm/tesserafin-server.spec.in"
check "the RPM License is the two-component SPDX expression" \
      "GPL-2.0-or-later AND GPL-3.0-or-later" \
      "$(sed -nE 's/^License: +(.*)$/\1/p' "${SPEC}")"

# The .deb must not invent a License control field: dpkg has no such field, and a
# reader parsing the control file would simply not see it. The licence statement
# belongs in the copyright file.
if grep -qE '^License:' "${REPO_ROOT}/packaging/linux/deb/control.in"; then
    bad "control.in declares a nonstandard License field"
else
    ok "control.in declares no nonstandard License field"
fi

COPY="${REPO_ROOT}/packaging/linux/deb/copyright.in"
if head -1 "${COPY}" | grep -q '^Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/'; then
    ok "the Debian copyright is a machine-readable DEP-5 document"
else
    bad "the Debian copyright is not DEP-5"
fi
for boundary in 'License: GPL-2.0-or-later' 'License: GPL-3.0-or-later'; do
    if grep -q "^${boundary}$" "${COPY}"; then
        ok "the copyright declares '${boundary}'"
    else
        bad "the copyright never declares '${boundary}'"
    fi
done
if grep -qE '^Files: usr/lib/tesserafin/ffmpeg/' "${COPY}"; then
    ok "the copyright gives the FFmpeg runtime its own Files stanza"
else
    bad "the copyright has no separate stanza for the FFmpeg runtime"
fi
# The server must never be described as GPL-3-only anywhere in the packaging.
# The server's own licence must never be restated as GPL-3. Matched inside a
# single sentence, because the accurate text "The server is GPL-2.0-or-later. The
# bundled FFmpeg runtime is GPL-3.0-or-later." legitimately contains both strings
# and a looser pattern would reject the correct wording.
if grep -rhoE '[^.]*\b[Ss]erver\b[^.]*' "${REPO_ROOT}/packaging/linux" 2>/dev/null \
     | grep -qE 'GPL-3'; then
    bad "packaging metadata describes the server itself as GPL-3: $(grep -rhoE '[^.]*\b[Ss]erver\b[^.]*' "${REPO_ROOT}/packaging/linux" | grep -E 'GPL-3' | head -1)"
else
    ok "no sentence about the server states GPL-3"
fi
# Likewise for nonfree/FDK: the copyright legitimately says the runtime contains
# NEITHER, so only an affirmative claim is a finding. Sentence-scoped in python
# rather than with grep, because these documents wrap and a line-based check
# splits "no nonfree component and no / FDK AAC" into a fragment that looks
# affirmative.
nonfree_claims="$(python3 - "${REPO_ROOT}/packaging/linux" <<'PYEOF'
import os, re, sys
root = sys.argv[1]
negation = re.compile(r"\b(no|not|never|without|free of|absent|neither|nor)\b", re.I)
target = re.compile(r"nonfree|libfdk|fdk[- ]aac", re.I)
for dirpath, _, names in os.walk(root):
    for name in names:
        path = os.path.join(dirpath, name)
        try:
            text = open(path, encoding="utf-8", errors="replace").read()
        except OSError:
            continue
        for sentence in re.split(r"(?<=[.!?])\s+", " ".join(text.split())):
            if target.search(sentence) and not negation.search(sentence):
                print(f"{os.path.relpath(path, root)}: {sentence}")
PYEOF
)"
if [[ -n "${nonfree_claims}" ]]; then
    bad "packaging metadata affirmatively claims a nonfree component or FDK AAC: ${nonfree_claims}"
else
    ok "packaging metadata makes no affirmative nonfree or FDK AAC claim"
fi

echo "== the corresponding source is a sidecar, not package content"
# The name is derived from the F0 manifest rather than spelled out, so the
# assertion looks for the derivation, not for a literal filename.
if grep -q 'F0_SOURCE_ARCHIVE' "${REPO_ROOT}/ci/package/build-all.sh" \
   && grep -q 'SIDECAR=' "${REPO_ROOT}/ci/package/build-all.sh"; then
    ok "build-all.sh emits the corresponding-source sidecar beside the artifacts"
else
    bad "build-all.sh never emits the corresponding-source sidecar"
fi
if grep -qE 'cp .*corresponding-source.*\$\{OUT\}/usr' "${REPO_ROOT}/ci/package/assemble-payload.sh"; then
    bad "the corresponding-source archive is copied INTO the staged package tree"
else
    ok "the corresponding-source archive is not copied into the package payload"
fi
if grep -q 'FFMPEG-CORRESPONDING-SOURCE.txt' "${REPO_ROOT}/ci/package/assemble-payload.sh"; then
    ok "every package carries the notice naming the sidecar"
else
    bad "no package carries a notice naming the sidecar"
fi

echo "== the runtime layout keeps bin/ and lib/ siblings"
for expected in 'ffmpeg/bin/ffmpeg' 'ffmpeg/bin/ffprobe' 'ffmpeg/lib/'; do
    if grep -q "${expected}" "${REPO_ROOT}/ci/package/assemble-payload.sh"; then
        ok "the staged tree installs ${expected}"
    else
        bad "the staged tree does not install ${expected}"
    fi
done
check "the service points at the packaged runtime binary" \
      "TESSERAFIN_FFMPEG=/usr/lib/tesserafin/ffmpeg/bin/ffmpeg" \
      "$(grep '^TESSERAFIN_FFMPEG=' "${REPO_ROOT}/packaging/linux/tesserafin.conf")"
# verify-artifacts.sh names the path in order to REJECT it, so it is excluded
# here for the same reason the inherited-runtime gate excludes its own source.
if grep -l '/opt/tesserafin-ffmpeg' "${REPO_ROOT}/ci/package"/*.sh "${REPO_ROOT}/packaging/linux"/* 2>/dev/null \
     | grep -qv 'verify-artifacts.sh'; then
    bad "the packaging expects something under /opt/tesserafin-ffmpeg"
else
    ok "the packaging expects nothing under /opt/tesserafin-ffmpeg"
fi

echo "== the provenance schema is strict"
SCHEMA="${REPO_ROOT}/ci/package/provenance.schema.json"
if python3 -c 'import json,sys;json.load(open(sys.argv[1]))' "${SCHEMA}" 2>/dev/null; then
    ok "the provenance schema is valid JSON"
else
    bad "the provenance schema is not valid JSON"
fi
schema_text="$(cat "${SCHEMA}")"
for obsolete in '"ffmpegVersion"' '"ffmpegAsset"'; do
    if grep -q -- "${obsolete}" <<<"${schema_text}"; then
        bad "the schema still declares ${obsolete}"
    else
        ok "the schema declares no ${obsolete}"
    fi
done
if python3 -c '
import json,sys
s=json.load(open(sys.argv[1]))
assert s["additionalProperties"] is False
assert s["properties"]["ffmpegRuntime"]["additionalProperties"] is False
' "${SCHEMA}" 2>/dev/null; then
    ok "the schema rejects unknown keys at both levels"
else
    bad "the schema tolerates unknown keys"
fi

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
# Collected into a variable rather than piped: `grep -q` closes the pipe on its
# first match, and with pipefail that turns the producer's SIGPIPE into a
# spurious verdict. Same hazard the artifact gate documents about `| head`.
scan_out="$(HOME=/home/runner pkg_scan_embedded_build_paths "${EMB}/root")"
if grep -q '^LEAK' <<<"${scan_out}"; then
    ok "a Tesserafin workspace path is reported as a leak"
else
    bad "a Tesserafin workspace path was not reported as a leak"
fi
rm -f "${EMB}/root/Tesserafin.Server.dll"

# An upstream-looking path that is not enumerated is a leak too: the list is
# closed, so a new dependency embedding a path is reviewed, not absorbed.
embed SomeNewDep.dll /home/runner/work/SomeNewDep/SomeNewDep/obj/Release/SomeNewDep.pdb
scan_out="$(HOME=/home/runner pkg_scan_embedded_build_paths "${EMB}/root")"
if grep -q '^LEAK' <<<"${scan_out}"; then
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
