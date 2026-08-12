#!/usr/bin/env bash
# Equivalence and hygiene gate for the native Linux artifacts (#225 / [L0]).
#
# Usage: ci/package/verify-artifacts.sh --artifacts DIR --rid RID
#
# Proves, from the ARTIFACTS themselves rather than from the build that produced
# them:
#   1. the .deb, the .rpm and the .tar.gz carry byte-identical application, web
#      AND FFmpeg-runtime payloads for this architecture;
#   2. the bundled web payload matches the pinned commit and digest;
#   3. no artifact carries build-host absolute paths, key material, credentials
#      or unexpectedly writable application files;
#   4. no user-facing package or service metadata carries Jellyfin branding at
#      all — including in the description. The packages no longer bundle an
#      upstream binary, so the exemption that used to permit "jellyfin-ffmpeg"
#      there describes something that is no longer true;
#   5. the installed FFmpeg runtime is the accepted one, complete, at the
#      documented path, with its RUNPATH and bundled libraries intact.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

ARTIFACTS=""; RID=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --artifacts) ARTIFACTS="$2"; shift 2 ;;
        --rid)       RID="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -d "${ARTIFACTS}" ]] || pkg_die "--artifacts must be an existing directory"
[[ -n "${RID}" ]]       || pkg_die "--rid is required"

pkg_load_pins
pkg_load_version_contract

DEB_ARCH="$(pkg_deb_arch "${RID}")"
RPM_ARCH="$(pkg_rpm_arch "${RID}")"

DEB="${ARTIFACTS}/tesserafin-server_${VERSION}-1_${DEB_ARCH}.deb"
RPM="${ARTIFACTS}/tesserafin-server-${VERSION}-1.${RPM_ARCH}.rpm"
TGZ="${ARTIFACTS}/tesserafin-server-${VERSION}-${RID}.tar.gz"

for artifact in "${DEB}" "${RPM}" "${TGZ}"; do
    [[ -f "${artifact}" ]] || pkg_die "missing artifact: ${artifact}"
done

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

FAILURES=0
fail() { echo "  FAIL: $*" >&2; FAILURES=$((FAILURES + 1)); }
pass() { echo "  ok  : $*"; }

# --- unpack ------------------------------------------------------------------
mkdir -p "${WORK}/deb" "${WORK}/rpm" "${WORK}/tgz"
dpkg-deb -x "${DEB}" "${WORK}/deb"
# rpm2cpio comes from the same pinned image the package was built with, so no
# workstation rpm tooling participates in the verdict.
RPM_TOOLS="$(pkg_rpm_builder_image)"
docker run --rm --user "$(id -u):$(id -g)" \
    --volume "${ARTIFACTS}:/artifacts:ro" --volume "${WORK}/rpm:/out" \
    "${RPM_TOOLS}" \
    sh -c "cd /out && rpm2cpio '/artifacts/$(basename "${RPM}")' | cpio -idm --quiet"
tar -xzf "${TGZ}" -C "${WORK}/tgz"
TGZ_ROOT="${WORK}/tgz/tesserafin-server-${VERSION}-${RID}"

# --- 1. payload equivalence --------------------------------------------------
echo "== payload equivalence (${RID})"
EPOCH_FOR_COMPARE="${SOURCE_DATE_EPOCH}"

app_deb="$(pkg_tree_digest "${WORK}/deb/usr/lib/tesserafin"  "${EPOCH_FOR_COMPARE}")"
app_rpm="$(pkg_tree_digest "${WORK}/rpm/usr/lib/tesserafin"  "${EPOCH_FOR_COMPARE}")"
app_tgz="$(pkg_tree_digest "${TGZ_ROOT}/lib/tesserafin"      "${EPOCH_FOR_COMPARE}")"

if [[ "${app_deb}" == "${app_rpm}" && "${app_deb}" == "${app_tgz}" ]]; then
    pass "application payload identical across .deb/.rpm/.tar.gz (${app_deb})"
else
    fail "application payload differs: deb=${app_deb} rpm=${app_rpm} tgz=${app_tgz}"
fi

web_deb="$(pkg_tree_digest "${WORK}/deb/usr/share/tesserafin/web" "${EPOCH_FOR_COMPARE}")"
web_rpm="$(pkg_tree_digest "${WORK}/rpm/usr/share/tesserafin/web" "${EPOCH_FOR_COMPARE}")"
web_tgz="$(pkg_tree_digest "${TGZ_ROOT}/share/tesserafin/web"     "${EPOCH_FOR_COMPARE}")"

if [[ "${web_deb}" == "${web_rpm}" && "${web_deb}" == "${web_tgz}" ]]; then
    pass "web payload identical across .deb/.rpm/.tar.gz (${web_deb})"
else
    fail "web payload differs: deb=${web_deb} rpm=${web_rpm} tgz=${web_tgz}"
fi

# The FFmpeg runtime is the whole point of this packaging work, so it is compared
# explicitly rather than left inside the /usr/lib/tesserafin comparison. Both its
# subtrees are covered: the executable prefix (bin/ + lib/) and the read-only
# metadata (SBOM, source manifest, capability record, notices).
rt_deb="$(pkg_tree_digest "${WORK}/deb/usr/lib/tesserafin/ffmpeg" "${EPOCH_FOR_COMPARE}")"
rt_rpm="$(pkg_tree_digest "${WORK}/rpm/usr/lib/tesserafin/ffmpeg" "${EPOCH_FOR_COMPARE}")"
rt_tgz="$(pkg_tree_digest "${TGZ_ROOT}/lib/tesserafin/ffmpeg"     "${EPOCH_FOR_COMPARE}")"

if [[ "${rt_deb}" == "${rt_rpm}" && "${rt_deb}" == "${rt_tgz}" ]]; then
    pass "FFmpeg runtime identical across .deb/.rpm/.tar.gz (${rt_deb})"
else
    fail "FFmpeg runtime differs: deb=${rt_deb} rpm=${rt_rpm} tgz=${rt_tgz}"
fi

meta_deb="$(pkg_tree_digest "${WORK}/deb/usr/share/tesserafin/ffmpeg" "${EPOCH_FOR_COMPARE}")"
meta_rpm="$(pkg_tree_digest "${WORK}/rpm/usr/share/tesserafin/ffmpeg" "${EPOCH_FOR_COMPARE}")"
meta_tgz="$(pkg_tree_digest "${TGZ_ROOT}/share/tesserafin/ffmpeg"     "${EPOCH_FOR_COMPARE}")"

if [[ "${meta_deb}" == "${meta_rpm}" && "${meta_deb}" == "${meta_tgz}" ]]; then
    pass "FFmpeg runtime metadata identical across .deb/.rpm/.tar.gz (${meta_deb})"
else
    fail "FFmpeg runtime metadata differs: deb=${meta_deb} rpm=${meta_rpm} tgz=${meta_tgz}"
fi

# --- 2. web provenance -------------------------------------------------------
echo "== web provenance"
revision="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["revision"])' \
            "${WORK}/deb/usr/share/tesserafin/web-revision.json")"
web_epoch="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["sourceDateEpoch"])' \
             "${WORK}/deb/usr/share/tesserafin/web-revision.json")"
if [[ "${revision}" == "${WEB_VCS_REF}" ]]; then
    pass "bundled web commit is the pinned commit (${revision})"
else
    fail "bundled web commit ${revision} is not the pinned ${WEB_VCS_REF}"
fi

installed_web_digest="$(pkg_tree_digest "${WORK}/deb/usr/share/tesserafin/web" "${web_epoch}")"
if [[ "${installed_web_digest}" == "${WEB_PAYLOAD_SHA256}" ]]; then
    pass "bundled web payload digest is the pinned digest (${installed_web_digest})"
else
    fail "bundled web payload digest ${installed_web_digest} is not the pinned ${WEB_PAYLOAD_SHA256}"
fi

# --- 3. content hygiene ------------------------------------------------------
echo "== content hygiene"

# 3a. The directory the packages were assembled in. A checkout path is this
#     build's alone, so it is matched literally and is never excusable.
leaks=0
for root in "${WORK}/deb" "${WORK}/rpm" "${TGZ_ROOT}"; do
    [[ -n "${PKG_REPO_ROOT}" && "${PKG_REPO_ROOT}" != "/" ]] || continue
    hits="$(grep -rlaF -- "${PKG_REPO_ROOT}" "${root}" 2>/dev/null || true)"
    if [[ -n "${hits}" ]]; then
        fail "this build's checkout path '${PKG_REPO_ROOT}' is present under $(basename "${root}"): ${hits}"
        leaks=$((leaks + 1))
    fi
done
if [[ "${leaks}" -eq 0 ]]; then
    pass "no artifact carries this build's checkout path"
fi

# 3b. Every OTHER absolute build path found anywhere in the three artifacts —
#     across the whole tree, not just the application directory. Each one must be
#     exactly an enumerated upstream dependency path; see
#     ci/package/embedded-build-paths.allow for why that enumeration is closed
#     and why `${HOME}` cannot be the discriminator on a hosted runner.
embedded_leak=0
known_files=""
while IFS=$'\t' read -r verdict file path; do
    [[ -n "${verdict}" ]] || continue
    if [[ "${verdict}" == "LEAK" ]]; then
        embedded_leak=$((embedded_leak + 1))
        fail "unenumerated embedded build path: ${file} carries '${path}'"
    else
        known_files+="$(basename "${file}")"$'\n'
    fi
done < <(pkg_scan_embedded_build_paths "${WORK}/deb" "${WORK}/rpm" "${TGZ_ROOT}")

if [[ "${embedded_leak}" -eq 0 ]]; then
    pass "every embedded build path is an enumerated upstream dependency path"
fi
if [[ -n "${known_files}" ]]; then
    echo "  note: third-party dependency assemblies carrying their own upstream build paths:" \
         "$(LC_ALL=C sort -u <<<"${known_files}" | tr '\n' ' ')"
fi

# 3c. Key material and credential files.
secrets="$(find "${WORK}/deb" "${WORK}/rpm" "${TGZ_ROOT}" \
        \( -name '*.pem' -o -name '*.key' -o -name '*.pfx' -o -name '*.p12' \
        -o -name '.env' -o -name '.npmrc' -o -name '.netrc' -o -name 'id_rsa*' \) \
        -print 2>/dev/null || true)"
if [[ -n "${secrets}" ]]; then
    fail "key material or credential file present in an artifact: ${secrets}"
else
    pass "no key material or credential files"
fi

# 3d. Application files must not be writable by group or other: nothing under
#     /usr is state, and a writable application file is a privilege path.
# No `| head`: a closed pipe would raise SIGPIPE, and with pipefail that turns a
# real finding into a silently skipped check.
writable="$(find "${WORK}/deb/usr" "${WORK}/rpm/usr" "${TGZ_ROOT}/lib" "${TGZ_ROOT}/share" \
                 -type f -perm /022 -print 2>/dev/null || true)"
if [[ -n "${writable}" ]]; then
    fail "group/other-writable application files: ${writable}"
else
    pass "no group/other-writable application files"
fi

# 3e. The archive must not pretend to be an installer.
archive_units="$(find "${TGZ_ROOT}" -name '*.service' -print 2>/dev/null || true)"
if [[ -e "${TGZ_ROOT}/etc" || -n "${archive_units}" ]]; then
    fail "the portable archive ships service or /etc content"
else
    pass "the portable archive ships no service unit and no /etc content"
fi

# 3f. No path escapes in the archive.
tgz_members="$(tar -tzf "${TGZ}")"
if grep -qE '(^/|(^|/)\.\.(/|$))' <<<"${tgz_members}"; then
    fail "the portable archive contains an absolute path or a '..' component"
else
    pass "the portable archive has no absolute or parent-relative members"
fi

# --- 4. user-facing naming ---------------------------------------------------
echo "== user-facing naming"

# The identity fields — what a user sees in a package manager listing — must be
# Tesserafin.
identity="$(dpkg-deb -f "${DEB}" Package Source Maintainer Homepage Section)"
identity+=$'\n'"$(docker run --rm --volume "${ARTIFACTS}:/artifacts:ro" "${RPM_TOOLS}" \
                  rpm -qp --nosignature --qf '%{NAME}\n%{SUMMARY}\n%{URL}\n%{PACKAGER}\n' \
                  "/artifacts/$(basename "${RPM}")" 2>/dev/null)"

if grep -qi 'jellyfin' <<<"${identity}"; then
    fail "package identity metadata carries Jellyfin branding: $(grep -i jellyfin <<<"${identity}")"
else
    pass "package identity metadata is Tesserafin-branded"
fi

# The descriptions too. This used to carry an exemption permitting the exact
# string "jellyfin-ffmpeg", because the package really did bundle a downloaded
# upstream binary and saying so was honest. It no longer does: the encoder is
# built from source in this repository, so the exemption would now describe
# something untrue and is deliberately gone.
#
# The runtime's OWN metadata — SOURCE.json, the SBOM, THIRD_PARTY_NOTICES.md —
# still names the Jellyfin fork and its upstream baseline tag, because that is
# where the source genuinely comes from. That is provenance inside the accepted
# F0 contract, it is not user-facing package metadata, and erasing it to quiet a
# grep would be the dishonest fix. This check reads package metadata only.
descriptions="$(dpkg-deb -f "${DEB}" Description)"
descriptions+=$'\n'"$(docker run --rm --volume "${ARTIFACTS}:/artifacts:ro" "${RPM_TOOLS}" \
                      rpm -qp --nosignature --qf '%{DESCRIPTION}\n' \
                      "/artifacts/$(basename "${RPM}")" 2>/dev/null)"
stray="$(grep -oiE 'jellyfin[a-z0-9-]*' <<<"${descriptions}" || true)"
if [[ -n "${stray}" ]]; then
    fail "package descriptions carry Jellyfin branding: ${stray}"
else
    pass "no package description carries Jellyfin branding"
fi

unit="${WORK}/deb/usr/lib/systemd/system/tesserafin.service"
if grep -qi 'jellyfin' "${unit}"; then
    fail "the systemd unit carries Jellyfin branding"
else
    pass "the systemd unit is Tesserafin-branded"
fi

# --- 5. the installed FFmpeg runtime -----------------------------------------
echo "== installed FFmpeg runtime"

for root_spec in "deb:${WORK}/deb/usr/lib/tesserafin/ffmpeg:${WORK}/deb/usr/share/tesserafin/ffmpeg" \
                 "rpm:${WORK}/rpm/usr/lib/tesserafin/ffmpeg:${WORK}/rpm/usr/share/tesserafin/ffmpeg" \
                 "tar.gz:${TGZ_ROOT}/lib/tesserafin/ffmpeg:${TGZ_ROOT}/share/tesserafin/ffmpeg"; do
    IFS=':' read -r fmt rt meta <<<"${root_spec}"

    for binary in ffmpeg ffprobe; do
        if [[ ! -x "${rt}/bin/${binary}" ]]; then
            fail "${fmt}: ${binary} is missing from ${rt#"${WORK}/"}/bin"
            continue
        fi
        runpath="$(readelf -d "${rt}/bin/${binary}" | sed -nE 's/.*\(RUNPATH\).*\[(.*)\]/\1/p')"
        [[ "${runpath}" == '$ORIGIN/../lib' ]] \
            || fail "${fmt}: ${binary} RUNPATH is '${runpath}', expected '\$ORIGIN/../lib'"
        machine="$(readelf -h "${rt}/bin/${binary}" | sed -nE 's/^ *Machine: *//p')"
        [[ "${machine}" == "$(pkg_elf_machine "${RID}")" ]] \
            || fail "${fmt}: ${binary} ELF machine '${machine}' is not $(pkg_elf_machine "${RID}")"
    done

    # The bundled libraries the RUNPATH resolves to, and their SONAME symlinks.
    # A relative symlink that does not resolve inside the artifact means the
    # encoder loads a HOST library or nothing at all.
    if [[ ! -d "${rt}/lib" ]]; then
        fail "${fmt}: the runtime has no lib/ directory beside bin/"
    else
        broken=0
        while IFS= read -r link; do
            [[ -n "${link}" ]] || continue
            [[ -e "${link}" && "$(readlink "${link}")" != /* ]] || { broken=$((broken + 1)); \
                fail "${fmt}: SONAME symlink does not resolve inside the artifact: ${link##*/}"; }
        done < <(find "${rt}/lib" -type l)
        if [[ "${broken}" -eq 0 ]]; then
            pass "${fmt}: bin/ and lib/ are siblings, RUNPATH intact, every SONAME symlink resolves inside the artifact"
        fi
    fi

    # The runtime describes itself, and describes the accepted revision.
    if [[ -f "${meta}/SOURCE.json" ]]; then
        rev="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["buildRevision"])' "${meta}/SOURCE.json")"
        [[ "${rev}" == "${F0_RUNTIME_REVISION}" ]] \
            && pass "${fmt}: installed runtime is the accepted revision ${rev}" \
            || fail "${fmt}: installed runtime revision '${rev}' is not the accepted ${F0_RUNTIME_REVISION}"
    else
        fail "${fmt}: the installed runtime carries no SOURCE.json"
    fi

    for required in sbom.cdx.json capability.json THIRD_PARTY_NOTICES.md; do
        [[ -f "${meta}/${required}" ]] || fail "${fmt}: the runtime metadata is missing ${required}"
    done
done

# Nothing may DEPEND on the F0 build prefix. build-in-container.sh stages the
# dependencies at /opt/tesserafin-ffmpeg inside the builder, so several accepted
# binaries carry that prefix as a compiled-in default CONFIG SEARCH PATH:
# fontconfig's cachedir and conf.d, OpenSSL's engines-3, the libmfx dispatcher's
# lookup list, and libva.conf. Those are optional, ignore-if-missing lookups —
# not load-bearing paths — and they are bytes of the ACCEPTED runtime, which this
# work must not change.
#
# So the assertion is the one that actually matters: nothing is INSTALLED under
# that prefix, no RPATH or RUNPATH names it, and the encoder runs with the prefix
# absent. A string in a config-search list is not a dependency; a search path in
# RUNPATH would be.
opt_installed="$(find "${WORK}/deb" "${WORK}/rpm" "${TGZ_ROOT}" -path '*opt/tesserafin-ffmpeg*' -print 2>/dev/null || true)"
if [[ -n "${opt_installed}" ]]; then
    fail "a packaged file is installed under /opt/tesserafin-ffmpeg: ${opt_installed}"
else
    pass "no packaged file is installed under /opt/tesserafin-ffmpeg"
fi

opt_rpath=0
while IFS= read -r elf; do
    [[ -n "${elf}" ]] || continue
    if readelf -d "${elf}" 2>/dev/null | grep -E '\((RPATH|RUNPATH)\)' | grep -q '/opt/tesserafin-ffmpeg'; then
        fail "$(basename "${elf}") has /opt/tesserafin-ffmpeg in its RPATH/RUNPATH"
        opt_rpath=$((opt_rpath + 1))
    fi
done < <(find "${WORK}/deb/usr/lib/tesserafin/ffmpeg" "${WORK}/rpm/usr/lib/tesserafin/ffmpeg" \
              "${TGZ_ROOT}/lib/tesserafin/ffmpeg" -type f 2>/dev/null)
if [[ "${opt_rpath}" -eq 0 ]]; then
    pass "no packaged ELF names /opt/tesserafin-ffmpeg in RPATH or RUNPATH"
fi

# The decisive check: does it run where the prefix does not exist? On a machine
# that has it, this proves nothing and says so rather than passing quietly.
if [[ -e /opt/tesserafin-ffmpeg ]]; then
    echo "  note: /opt/tesserafin-ffmpeg exists on THIS machine, so 'runs without it'"
    echo "        cannot be demonstrated here. The hosted runners have no such path."
elif "${WORK}/deb/usr/lib/tesserafin/ffmpeg/bin/ffmpeg" -hide_banner -version >/dev/null 2>&1; then
    pass "the packaged encoder runs on a machine with no /opt/tesserafin-ffmpeg at all"
else
    fail "the packaged encoder does not run without /opt/tesserafin-ffmpeg present"
fi

# --- 6. licensing closure ----------------------------------------------------
echo "== licensing"

for root_spec in "deb:${WORK}/deb/usr/share/licenses/tesserafin-server:${WORK}/deb/usr/share/doc/tesserafin-server" \
                 "rpm:${WORK}/rpm/usr/share/licenses/tesserafin-server:${WORK}/rpm/usr/share/doc/tesserafin-server"; do
    IFS=':' read -r fmt lic doc <<<"${root_spec}"
    [[ -f "${lic}/LICENSE" ]] || fail "${fmt}: the server licence is missing"
    # The server licence must be the project's own file, byte for byte. A build
    # that "helpfully" substituted the runtime's GPL-3 text would relicense the
    # server by accident.
    if [[ -f "${lic}/LICENSE" ]]; then
        [[ "$(pkg_sha256 "${lic}/LICENSE")" == "$(pkg_sha256 "${PKG_REPO_ROOT}/LICENSE")" ]] \
            && pass "${fmt}: the installed server licence is byte-identical to the project LICENSE" \
            || fail "${fmt}: the installed server licence is not the project LICENSE"
    fi
    count="$(find "${lic}/ffmpeg" -type f 2>/dev/null | wc -l)"
    [[ "${count}" -ge 20 ]] \
        && pass "${fmt}: ${count} FFmpeg component licence texts installed" \
        || fail "${fmt}: only ${count} FFmpeg licence texts installed; the runtime's licence set is incomplete"
    [[ -f "${doc}/FFMPEG-CORRESPONDING-SOURCE.txt" ]] \
        || fail "${fmt}: no corresponding-source notice in the package"
    grep -q "${F0_SOURCE_ARCHIVE}" "${doc}/FFMPEG-CORRESPONDING-SOURCE.txt" 2>/dev/null \
        || fail "${fmt}: the corresponding-source notice does not name ${F0_SOURCE_ARCHIVE}"
done

# The .deb copyright must be a real per-component document, not a copy of the
# server licence, and must state both boundaries.
copyright="${WORK}/deb/usr/share/doc/tesserafin-server/copyright"
if [[ -f "${copyright}" ]] \
   && grep -q '^Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/' "${copyright}" \
   && grep -q 'License: GPL-2.0-or-later' "${copyright}" \
   && grep -q 'License: GPL-3.0-or-later' "${copyright}"; then
    pass "the Debian copyright is DEP-5 and states both licence boundaries"
else
    fail "the Debian copyright is not a DEP-5 document stating both licence boundaries"
fi

# No metadata may describe the whole package as GPL-2-only, and none may claim
# the server itself is GPL-3.
rpm_license="$(docker run --rm --volume "${ARTIFACTS}:/artifacts:ro" "${RPM_TOOLS}" \
               rpm -qp --nosignature --qf '%{LICENSE}' "/artifacts/$(basename "${RPM}")" 2>/dev/null)"
if [[ "${rpm_license}" == "GPL-2.0-or-later AND GPL-3.0-or-later" ]]; then
    pass "the RPM License field states both boundaries: ${rpm_license}"
elif [[ "${rpm_license}" == "GPL-2.0-or-later" ]]; then
    fail "the RPM describes the whole package as GPL-2.0-or-later only, but it bundles a GPL-3.0-or-later runtime"
elif [[ "${rpm_license}" == "GPL-3.0-or-later" ]]; then
    fail "the RPM describes the whole package as GPL-3.0-or-later, which misstates the server's licence"
else
    fail "unexpected RPM License field: '${rpm_license}'"
fi

# Capability drift: the runtime must remain free of nonfree components and of
# FDK AAC. Read from the capability record the runtime itself produced.
#
# The manifest's buildConfiguration records the configure line, which NAMES both
# --disable-nonfree and --disable-libfdk-aac. A grep for the bare strings would
# therefore fail on the very evidence that they are switched off. What is checked
# instead: the two disable flags are present, and no ENABLED encoder, decoder or
# filter is an FDK or otherwise nonfree one.
cap="${WORK}/deb/usr/share/tesserafin/ffmpeg/capability.json"
if [[ -f "${cap}" ]]; then
    # Wrapped in `if`, not followed by a `$?` test: under `set -e` a failing
    # python3 would abort this script before the test could run, turning a
    # finding into a crash.
    if python3 - "${cap}" <<'PY'
import json, re, sys
cap = json.load(open(sys.argv[1]))
config = " ".join(cap.get("buildConfiguration", []))
problems = []
for flag in ("--disable-nonfree", "--disable-libfdk-aac"):
    if flag not in config:
        problems.append(f"the build configuration does not carry {flag}")
for flag in ("--enable-nonfree", "--enable-libfdk-aac"):
    if flag in config:
        problems.append(f"the build configuration carries {flag}")
bad = re.compile(r"fdk|nonfree", re.I)
for kind in ("encoders", "decoders", "filters", "protocols", "hwaccels"):
    hits = [x for x in cap.get(kind, []) if bad.search(x)]
    if hits:
        problems.append(f"{kind} include a nonfree/FDK entry: {hits}")
for p in problems:
    print(f"  FAIL: {p}", file=sys.stderr)
raise SystemExit(1 if problems else 0)
PY
    then
        pass "nonfree and FDK AAC are disabled in the build configuration, and no capability is one"
    else
        fail "nonfree or FDK AAC capability drift in the installed capability manifest"
    fi
else
    fail "no capability manifest installed"
fi

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "VERIFY: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "VERIFY: PASS — ${RID}"
