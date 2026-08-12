#!/usr/bin/env bash
# Deterministic payload assembly for the native Linux server packages (#225 / [L0]).
#
# Produces ONE staged filesystem tree per architecture. The .deb, the .rpm and
# the .tar.gz are all built from that same tree, which is what makes
# "the three formats carry identical inputs" a fact rather than a claim.
#
# Every input is pinned:
#   * the server source            — the commit this checkout is on
#   * the Tesserafin Web payload   — the assets image digest declared in the Dockerfile
#   * the media encoder            — the accepted Tesserafin FFmpeg runtime, built
#                                    from the pinned sources by ci/package/ffmpeg-runtime.sh
#   * every timestamp              — SOURCE_DATE_EPOCH from the commit
#
# Any drift in the web or runtime provenance fails the build. Nothing here
# publishes, downloads a release asset, or consults a workflow run.
#
# Usage: ci/package/assemble-payload.sh --rid linux-x64|linux-arm64 --out DIR \
#            --ffmpeg-runtime DIR
#
# --ffmpeg-runtime is the packaged F0 output directory that
# ci/package/ffmpeg-runtime.sh produced and verified. It is a required argument
# rather than something this script builds, so one freshly built runtime can be
# shared by the .deb, the .rpm and the portable archive of the same architecture
# without any of them being able to substitute a different one.

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

RID=""
OUT=""
FFMPEG_RUNTIME=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)             RID="$2"; shift 2 ;;
        --out)             OUT="$2"; shift 2 ;;
        --ffmpeg-runtime)  FFMPEG_RUNTIME="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -n "${RID}" ]] || pkg_die "--rid is required"
[[ -n "${OUT}" ]] || pkg_die "--out is required"
[[ -d "${FFMPEG_RUNTIME}" ]] || pkg_die "--ffmpeg-runtime must be a packaged F0 runtime directory"
pkg_deb_arch "${RID}" >/dev/null   # validates the runtime identifier

pkg_load_pins
pkg_load_version_contract

RT="${FFMPEG_RUNTIME}/$(pkg_f0_runtime_dir_name "${RID}")"
RT_ARCHIVE="${FFMPEG_RUNTIME}/$(pkg_f0_runtime_archive_name "${RID}")"
RT_SOURCE="${FFMPEG_RUNTIME}/${F0_SOURCE_ARCHIVE}"
[[ -d "${RT}" ]]         || pkg_die "no ${RID} runtime under ${FFMPEG_RUNTIME}"
[[ -f "${RT_ARCHIVE}" ]] || pkg_die "no runtime archive $(pkg_f0_runtime_archive_name "${RID}") under ${FFMPEG_RUNTIME}"
[[ -f "${RT_SOURCE}" ]]  || pkg_die "no corresponding-source archive ${F0_SOURCE_ARCHIVE} under ${FFMPEG_RUNTIME}"

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

pkg_log "assembling ${RID} payload"
echo "  version           : ${VERSION}"
echo "  server commit     : ${VCS_REF}"
echo "  source_date_epoch : ${SOURCE_DATE_EPOCH}"
echo "  web commit        : ${WEB_VCS_REF}"
echo "  web assets image  : ${WEB_ASSETS_IMAGE}"
echo "  ffmpeg runtime    : ${F0_BUILD_REVISION} (${RID}), built from ${F0_FFMPEG_COMMIT}"
echo "  ffmpeg licence    : ${F0_RUNTIME_LICENSE}"

# =============================================================================
# 1. The server payload — self-contained, so the package needs no system .NET.
# =============================================================================
#
# The container image publishes framework-dependent because the IMAGE supplies
# the runtime. A .deb or .rpm has no such backstop, and depending on Microsoft's
# APT/DNF feed for a runtime is out of scope for this work, so the packages carry
# their own runtime. Deterministic/ContinuousIntegrationBuild normalise embedded
# paths and compiler output; DebugType=none keeps symbols out of the artifact.
pkg_log "publishing the server (self-contained, ${RID})"
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
dotnet publish "${PKG_REPO_ROOT}/Tesserafin.Server/Tesserafin.Server.csproj" \
    --configuration Release \
    --runtime "${RID}" \
    --self-contained true \
    --output "${WORK}/app" \
    -p:Deterministic=true \
    -p:ContinuousIntegrationBuild=true \
    -p:UseAppHost=true \
    -p:DebugType=none \
    >/dev/null

# The ASP.NET static-web-assets manifest stamps every pre-compressed asset's
# Last-Modified with the compression clock — the one non-deterministic file in
# the publish output. The container build normalises it the same way; without
# this, two clean builds differ in exactly one file. (ETags are content hashes
# and are already stable.)
endpoints="${WORK}/app/tesserafin.staticwebassets.endpoints.json"
if [[ -f "${endpoints}" ]]; then
    fixed="$(date -u -d "@${SOURCE_DATE_EPOCH}" +'%a, %d %b %Y %H:%M:%S GMT')"
    sed -i -E 's/("Name":"Last-Modified","Value":")[^"]*(")/\1'"${fixed}"'\2/g' "${endpoints}"
fi

# =============================================================================
# 2. The bundled Tesserafin Web payload — the exact pinned distribution input.
# =============================================================================
#
# Pulled BY DIGEST, so the registry cannot serve different bytes under the same
# reference. Two independent provenance assertions then have to hold: the commit
# recorded inside the payload, and the digest of the payload itself.
pkg_log "extracting the pinned Tesserafin Web payload"
docker pull --quiet "${WEB_ASSETS_IMAGE}" >/dev/null
web_cid="$(docker create "${WEB_ASSETS_IMAGE}" /bin/true)"
mkdir -p "${WORK}/web" "${WORK}/web-licenses"
docker cp "${web_cid}:/web/."      "${WORK}/web/"          >/dev/null
docker cp "${web_cid}:/licenses/." "${WORK}/web-licenses/" >/dev/null
docker cp "${web_cid}:/metadata/web-revision.json" "${WORK}/web-revision.json" >/dev/null
docker rm -f "${web_cid}" >/dev/null

web_revision="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["revision"])' \
                "${WORK}/web-revision.json")"
web_epoch="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["sourceDateEpoch"])' \
             "${WORK}/web-revision.json")"
[[ "${web_revision}" == "${WEB_VCS_REF}" ]] || pkg_die \
    "web provenance mismatch: payload says '${web_revision}', the pin says '${WEB_VCS_REF}'"

# Modes are normalised BEFORE hashing, with the same rule the staged tree uses,
# so the pinned digest describes exactly what ends up installed on disk and the
# same assertion can be re-run against an unpacked artifact.
find "${WORK}/web" -type d -exec chmod 0755 {} +
find "${WORK}/web" -type f -exec chmod 0644 {} +

# Hashed at the web build's OWN epoch, so the pinned digest identifies the web
# payload and does not move every time the server commit moves.
WEB_PAYLOAD_DIGEST="$(pkg_tree_digest "${WORK}/web" "${web_epoch}")"
pkg_clamp_mtimes "${WORK}/web"

if [[ "${WEB_PAYLOAD_SHA256}" == "@WEB_PAYLOAD_SHA256@" ]]; then
    pkg_die "ci/package/pins.env still holds the WEB_PAYLOAD_SHA256 placeholder; the
    payload for the pinned image digest hashes to ${WEB_PAYLOAD_DIGEST}"
fi
[[ "${WEB_PAYLOAD_DIGEST}" == "${WEB_PAYLOAD_SHA256}" ]] || pkg_die \
    "web payload digest mismatch: built ${WEB_PAYLOAD_DIGEST}, pinned ${WEB_PAYLOAD_SHA256}"

# =============================================================================
# 3. The media encoder — the accepted Tesserafin FFmpeg runtime, built from source.
# =============================================================================
#
# Nothing is fetched here. ci/package/ffmpeg-runtime.sh has already built this
# runtime from the pinned sources with the merged F0 scripts, verified its
# revision, architecture, ELF machine, RUNPATH, bundled libraries and closure,
# and compared its delivered digests against the accepted baseline. What remains
# is to install it, WHOLE.
#
# The runtime is not two executables. It is two executables, the bundled shared
# libraries their $ORIGIN RUNPATH resolves to, the licence texts of every
# statically linked component, the third-party notices, the SBOM, the source
# baseline and the compiled-capability record. Installing only bin/ would produce
# a binary that cannot load — and a distribution with no licence closure.
pkg_log "installing the accepted FFmpeg runtime ${F0_BUILD_REVISION} (${RID})"
RUNTIME_ARCHIVE_SHA="$(pkg_sha256 "${RT_ARCHIVE}")"
SOURCE_ARCHIVE_SHA="$(pkg_sha256 "${RT_SOURCE}")"
FFMPEG_SHA="$(pkg_sha256 "${RT}/bin/ffmpeg")"
FFPROBE_SHA="$(pkg_sha256 "${RT}/bin/ffprobe")"
CAPABILITY_SHA="$(pkg_sha256 "${RT}/capability.json")"
SBOM_SHA="$(pkg_sha256 "${RT}/sbom.cdx.json")"
SOURCE_JSON_SHA="$(pkg_sha256 "${RT}/SOURCE.json")"
NOTICES_SHA="$(pkg_sha256 "${RT}/THIRD_PARTY_NOTICES.md")"

# =============================================================================
# 4. The staged filesystem tree.
# =============================================================================
pkg_log "staging the filesystem tree"
rm -rf "${OUT}"
mkdir -p "${OUT}/usr/lib/tesserafin" \
         "${OUT}/usr/lib/tesserafin/ffmpeg/bin" \
         "${OUT}/usr/lib/tesserafin/ffmpeg/lib" \
         "${OUT}/usr/share/tesserafin" \
         "${OUT}/usr/share/tesserafin/ffmpeg" \
         "${OUT}/usr/lib/systemd/system" \
         "${OUT}/usr/share/doc/tesserafin-server" \
         "${OUT}/usr/share/licenses/tesserafin-server" \
         "${OUT}/usr/share/licenses/tesserafin-server/ffmpeg" \
         "${OUT}/usr/bin" \
         "${OUT}/etc/tesserafin" \
         "${OUT}/var/lib/tesserafin" \
         "${OUT}/var/cache/tesserafin" \
         "${OUT}/var/log/tesserafin"

cp -a "${WORK}/app/."          "${OUT}/usr/lib/tesserafin/"
cp -a "${WORK}/web"            "${OUT}/usr/share/tesserafin/web"
cp -a "${WORK}/web-licenses"   "${OUT}/usr/share/tesserafin/web-licenses"
cp -a "${WORK}/web-revision.json" "${OUT}/usr/share/tesserafin/web-revision.json"

# The FFmpeg runtime, installed with bin/ and lib/ as SIBLINGS. The binaries
# carry RUNPATH=$ORIGIN/../lib and resolve their bundled libraries relative to
# themselves; flattening the two directories, or moving the libraries to a
# system path, would silently hand the process to whatever libva the host has.
cp -a "${RT}/bin/ffmpeg"  "${OUT}/usr/lib/tesserafin/ffmpeg/bin/ffmpeg"
cp -a "${RT}/bin/ffprobe" "${OUT}/usr/lib/tesserafin/ffmpeg/bin/ffprobe"
cp -a "${RT}/lib/."       "${OUT}/usr/lib/tesserafin/ffmpeg/lib/"

# Runtime metadata, read-only, beside the application data rather than inside
# the executable prefix: a recipient must be able to identify every component,
# read what it was built from and reach its source.
cp -a "${RT}/SOURCE.json"              "${OUT}/usr/share/tesserafin/ffmpeg/SOURCE.json"
cp -a "${RT}/sbom.cdx.json"            "${OUT}/usr/share/tesserafin/ffmpeg/sbom.cdx.json"
cp -a "${RT}/capability.json"          "${OUT}/usr/share/tesserafin/ffmpeg/capability.json"
cp -a "${RT}/build-configuration.txt"  "${OUT}/usr/share/tesserafin/ffmpeg/build-configuration.txt"
cp -a "${RT}/THIRD_PARTY_NOTICES.md"   "${OUT}/usr/share/tesserafin/ffmpeg/THIRD_PARTY_NOTICES.md"

# The server licence and the runtime licences are installed SEPARATELY and
# neither replaces the other. The server is GPL-2.0-or-later; the runtime, built
# with --enable-gpl --enable-version3, is GPL-3.0-or-later. Collapsing the two
# would misstate one of them.
cp -a "${PKG_REPO_ROOT}/LICENSE" "${OUT}/usr/share/licenses/tesserafin-server/LICENSE"
cp -a "${RT}/LICENSES/."         "${OUT}/usr/share/licenses/tesserafin-server/ffmpeg/"

cp -a "${PKG_REPO_ROOT}/packaging/linux/tesserafin.service" \
      "${OUT}/usr/lib/systemd/system/tesserafin.service"
cp -a "${PKG_REPO_ROOT}/packaging/linux/tesserafin.conf" \
      "${OUT}/etc/tesserafin/tesserafin.conf"

# The Debian machine-readable copyright file, with one stanza per differently
# licensed component. It is a real DEP-5 document rather than a copy of the
# server LICENSE, because a package that bundles two differently licensed works
# and ships one licence text has not described itself.
sed -e "s|@SERVER_LICENSE_PATH@|/usr/share/licenses/tesserafin-server/LICENSE|g" \
    -e "s|@FFMPEG_LICENSE_PATH@|/usr/share/licenses/tesserafin-server/ffmpeg/|g" \
    -e "s|@F0_BUILD_REVISION@|${F0_BUILD_REVISION}|g" \
    -e "s|@F0_UPSTREAM_COMMIT@|${F0_FFMPEG_COMMIT}|g" \
    -e "s|@F0_UPSTREAM_REPOSITORY@|${F0_FFMPEG_REPOSITORY}|g" \
    -e "s|@WEB_VCS_REF@|${WEB_VCS_REF}|g" \
    "${PKG_REPO_ROOT}/packaging/linux/deb/copyright.in" \
    > "${OUT}/usr/share/doc/tesserafin-server/copyright"

# The corresponding-source notice. The ~232 MB source archive is NOT inside the
# package — it would be duplicated across three formats and two architectures for
# one architecture-independent tree. It travels beside the packages, and this
# notice names it and its digest so a recipient holding only the installed
# package knows exactly what to ask for.
sed -e "s|@F0_BUILD_REVISION@|${F0_BUILD_REVISION}|g" \
    -e "s|@F0_UPSTREAM_COMMIT@|${F0_FFMPEG_COMMIT}|g" \
    -e "s|@F0_UPSTREAM_REPOSITORY@|${F0_FFMPEG_REPOSITORY}|g" \
    -e "s|@F0_SOURCE_ARCHIVE@|${F0_SOURCE_ARCHIVE}|g" \
    -e "s|@F0_SOURCE_SHA256@|${SOURCE_ARCHIVE_SHA}|g" \
    -e "s|@SERVER_COMMIT@|${VCS_REF}|g" \
    -e "s|@WEB_VCS_REF@|${WEB_VCS_REF}|g" \
    "${PKG_REPO_ROOT}/packaging/linux/FFMPEG-CORRESPONDING-SOURCE.txt.in" \
    > "${OUT}/usr/share/doc/tesserafin-server/FFMPEG-CORRESPONDING-SOURCE.txt"

# /usr/bin/tesserafin is a RELATIVE symlink into the payload. The .NET apphost
# resolves its own directory through /proc/self/exe, so the symlink runs the
# application exactly as the real path does.
ln -sf ../lib/tesserafin/tesserafin "${OUT}/usr/bin/tesserafin"

# Normalise every mode, independent of the build user's umask and of whatever
# `dotnet publish` happened to emit. Application files are never writable by
# group or other — nothing under /usr is state — and the execute bit is kept
# only where the publish output already had it. rpmbuild would strip group and
# other write on its own, so without this the .deb and the .rpm would disagree
# on modes even with identical content.
#
# -type f, so the bundled SONAME symlinks under the runtime's lib/ keep pointing
# where they point: chmod on a symlink would follow it and change the target.
find "${OUT}" -type d -exec chmod 0755 {} +
find "${OUT}" -type f -perm -u+x -exec chmod 0755 {} +
find "${OUT}" -type f ! -perm -u+x -exec chmod 0644 {} +

chmod 0755 "${OUT}/usr/lib/tesserafin/tesserafin" \
           "${OUT}/usr/lib/tesserafin/ffmpeg/bin/ffmpeg" \
           "${OUT}/usr/lib/tesserafin/ffmpeg/bin/ffprobe"
chmod 0644 "${OUT}/etc/tesserafin/tesserafin.conf" \
           "${OUT}/usr/lib/systemd/system/tesserafin.service"

pkg_clamp_mtimes "${OUT}"

# =============================================================================
# 5. Provenance.
# =============================================================================
# The application payload digest deliberately covers /usr/lib/tesserafin only:
# the .deb, the .rpm and the .tar.gz each add their own metadata around it, and a
# digest that moved with packaging metadata could not prove the three formats
# carry the same application.
APP_PAYLOAD_DIGEST="$(pkg_tree_digest "${OUT}/usr/lib/tesserafin")"
STAGE_DIGEST="$(pkg_tree_digest "${OUT}")"

cat > "${OUT}.payload.json" <<JSON
{
  "packageName": "tesserafin-server",
  "packageVersion": "${VERSION}",
  "runtimeIdentifier": "${RID}",
  "debianArchitecture": "$(pkg_deb_arch "${RID}")",
  "rpmArchitecture": "$(pkg_rpm_arch "${RID}")",
  "serverCommit": "${VCS_REF}",
  "serverRepository": "https://github.com/tesserafin-project/tesserafin.git",
  "serverLicense": "GPL-2.0-or-later",
  "webCommit": "${WEB_VCS_REF}",
  "webVersion": "${WEB_VERSION}",
  "webRepository": "https://github.com/tesserafin-project/tesserafin-web.git",
  "webAssetsImage": "${WEB_ASSETS_IMAGE}",
  "webPayloadSha256": "${WEB_PAYLOAD_DIGEST}",
  "applicationPayloadSha256": "${APP_PAYLOAD_DIGEST}",
  "stagedTreeSha256": "${STAGE_DIGEST}",
  "ffmpegRuntime": {
    "buildRevision": "${F0_BUILD_REVISION}",
    "upstreamRepository": "${F0_FFMPEG_REPOSITORY}",
    "upstreamCommit": "${F0_FFMPEG_COMMIT}",
    "upstreamBaseline": "${F0_FFMPEG_BASELINE}",
    "architecture": "${RID}",
    "license": "${F0_RUNTIME_LICENSE}",
    "ffmpegSha256": "${FFMPEG_SHA}",
    "ffprobeSha256": "${FFPROBE_SHA}",
    "runtimeArchive": "$(pkg_f0_runtime_archive_name "${RID}")",
    "runtimeArchiveSha256": "${RUNTIME_ARCHIVE_SHA}",
    "capabilityManifestSha256": "${CAPABILITY_SHA}",
    "sbomSha256": "${SBOM_SHA}",
    "sourceManifestSha256": "${SOURCE_JSON_SHA}",
    "noticesSha256": "${NOTICES_SHA}",
    "correspondingSource": "${F0_SOURCE_ARCHIVE}",
    "correspondingSourceSha256": "${SOURCE_ARCHIVE_SHA}"
  },
  "sourceDateEpoch": "${SOURCE_DATE_EPOCH}",
  "buildTimestamp": "$(date -u -d "@${SOURCE_DATE_EPOCH}" +'%Y-%m-%dT%H:%M:%SZ')",
  "toolchain": {
    "dotnetSdk": "$(dotnet --version)",
    "tar": "$(tar --version | head -1)",
    "dpkgDeb": "$(dpkg-deb --version | head -1)",
    "rpmbuild": "${RPM_BUILDER_RPM_VERSION}",
    "rpmBuilderImage": "${RPM_BUILDER_IMAGE}"
  }
}
JSON

pkg_log "staged ${RID}: ${STAGE_DIGEST}"
