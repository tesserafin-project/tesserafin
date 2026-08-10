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
#   * the media encoder            — the jellyfin-ffmpeg release declared in the Dockerfile
#   * every timestamp              — SOURCE_DATE_EPOCH from the commit
#
# Any drift in the web provenance fails the build. Nothing here publishes.
#
# Usage: ci/package/assemble-payload.sh --rid linux-x64|linux-arm64 --out DIR

set -euo pipefail

# shellcheck source=ci/package/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

RID=""
OUT=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid) RID="$2"; shift 2 ;;
        --out) OUT="$2"; shift 2 ;;
        *) pkg_die "unknown argument: $1" ;;
    esac
done

[[ -n "${RID}" ]] || pkg_die "--rid is required"
[[ -n "${OUT}" ]] || pkg_die "--out is required"
pkg_deb_arch "${RID}" >/dev/null   # validates the runtime identifier

pkg_load_pins
pkg_load_version_contract

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

pkg_log "assembling ${RID} payload"
echo "  version           : ${VERSION}"
echo "  server commit     : ${VCS_REF}"
echo "  source_date_epoch : ${SOURCE_DATE_EPOCH}"
echo "  web commit        : ${WEB_VCS_REF}"
echo "  web assets image  : ${WEB_ASSETS_IMAGE}"
echo "  ffmpeg            : ${FFMPEG_VERSION} ($(pkg_ffmpeg_asset "${RID}"))"

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
# 3. The media encoder — the same upstream release the container pins.
# =============================================================================
#
# The container installs the Ubuntu-noble .deb of jellyfin-ffmpeg. That binary
# hard-codes RUNPATH=/usr/lib/jellyfin-ffmpeg/lib and needs sixteen external
# sonames, so it is not redistributable across distributions. The SAME upstream
# release also publishes a portable build with no external dependencies, and that
# is what the packages carry — same project, same version tag, same GPL terms,
# pinned by SHA-256. docs/distribution/L0-linux-packages.md records the
# capability comparison that was measured between the two.
pkg_log "fetching the pinned jellyfin-ffmpeg portable build"
ffmpeg_asset="$(pkg_ffmpeg_asset "${RID}")"
ffmpeg_url="https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v${FFMPEG_VERSION}/${ffmpeg_asset}"
curl --fail --silent --show-error --location --output "${WORK}/${ffmpeg_asset}" "${ffmpeg_url}"
echo "$(pkg_ffmpeg_sha256 "${RID}")  ${WORK}/${ffmpeg_asset}" | sha256sum --check --status \
    || pkg_die "checksum mismatch for ${ffmpeg_asset}"
mkdir -p "${WORK}/ffmpeg"
tar --extract --xz --file "${WORK}/${ffmpeg_asset}" --directory "${WORK}/ffmpeg"
[[ -x "${WORK}/ffmpeg/ffmpeg" && -x "${WORK}/ffmpeg/ffprobe" ]] \
    || pkg_die "the ffmpeg archive did not contain both ffmpeg and ffprobe"

# =============================================================================
# 4. The staged filesystem tree.
# =============================================================================
pkg_log "staging the filesystem tree"
rm -rf "${OUT}"
mkdir -p "${OUT}/usr/lib/tesserafin" \
         "${OUT}/usr/lib/tesserafin/ffmpeg" \
         "${OUT}/usr/share/tesserafin" \
         "${OUT}/usr/lib/systemd/system" \
         "${OUT}/usr/share/doc/tesserafin-server" \
         "${OUT}/usr/share/licenses/tesserafin-server" \
         "${OUT}/usr/bin" \
         "${OUT}/etc/tesserafin" \
         "${OUT}/var/lib/tesserafin" \
         "${OUT}/var/cache/tesserafin" \
         "${OUT}/var/log/tesserafin"

cp -a "${WORK}/app/."          "${OUT}/usr/lib/tesserafin/"
cp -a "${WORK}/ffmpeg/ffmpeg"  "${OUT}/usr/lib/tesserafin/ffmpeg/ffmpeg"
cp -a "${WORK}/ffmpeg/ffprobe" "${OUT}/usr/lib/tesserafin/ffmpeg/ffprobe"
cp -a "${WORK}/web"            "${OUT}/usr/share/tesserafin/web"
cp -a "${WORK}/web-licenses"   "${OUT}/usr/share/tesserafin/web-licenses"
cp -a "${WORK}/web-revision.json" "${OUT}/usr/share/tesserafin/web-revision.json"

cp -a "${PKG_REPO_ROOT}/packaging/linux/tesserafin.service" \
      "${OUT}/usr/lib/systemd/system/tesserafin.service"
cp -a "${PKG_REPO_ROOT}/packaging/linux/tesserafin.conf" \
      "${OUT}/etc/tesserafin/tesserafin.conf"
cp -a "${PKG_REPO_ROOT}/LICENSE" "${OUT}/usr/share/licenses/tesserafin-server/LICENSE"
cp -a "${PKG_REPO_ROOT}/LICENSE" "${OUT}/usr/share/doc/tesserafin-server/copyright"

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
find "${OUT}" -type d -exec chmod 0755 {} +
find "${OUT}" -type f -perm -u+x -exec chmod 0755 {} +
find "${OUT}" -type f ! -perm -u+x -exec chmod 0644 {} +

chmod 0755 "${OUT}/usr/lib/tesserafin/tesserafin" \
           "${OUT}/usr/lib/tesserafin/ffmpeg/ffmpeg" \
           "${OUT}/usr/lib/tesserafin/ffmpeg/ffprobe"
chmod 0644 "${OUT}/etc/tesserafin/tesserafin.conf" \
           "${OUT}/usr/lib/systemd/system/tesserafin.service"

pkg_clamp_mtimes "${OUT}"

# =============================================================================
# 5. Provenance.
# =============================================================================
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
  "webCommit": "${WEB_VCS_REF}",
  "webVersion": "${WEB_VERSION}",
  "webAssetsImage": "${WEB_ASSETS_IMAGE}",
  "webPayloadSha256": "${WEB_PAYLOAD_DIGEST}",
  "applicationPayloadSha256": "${APP_PAYLOAD_DIGEST}",
  "stagedTreeSha256": "${STAGE_DIGEST}",
  "ffmpegVersion": "${FFMPEG_VERSION}",
  "ffmpegAsset": "${ffmpeg_asset}",
  "ffmpegSha256": "$(pkg_ffmpeg_sha256 "${RID}")",
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
