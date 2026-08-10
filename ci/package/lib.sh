#!/usr/bin/env bash
# Shared helpers for the native Linux server packages (issue #225 / [L0]).
#
# Sourced by ci/package/*.sh. Nothing here builds or publishes anything; it
# resolves the pinned inputs, and it fails closed when any of them disagree.
#
# The rule this file exists to enforce: a pin has exactly ONE definition. The
# web assets image, the paired web commit and the ffmpeg version are declared in
# the Dockerfile and read back from it; ci/package/pins.env only adds what the
# container path has no reason to know.

set -euo pipefail

PKG_REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PKG_PINS_FILE="${PKG_REPO_ROOT}/ci/package/pins.env"
PKG_DOCKERFILE="${PKG_REPO_ROOT}/Dockerfile"

pkg_die() { echo "package: $*" >&2; exit 1; }
pkg_log() { echo "== $*" >&2; }

# --- architecture mapping ----------------------------------------------------
#
# The one table. Everything user-facing derives from it, so no script invents a
# second spelling of an architecture.
#
#   linux-x64    -> amd64 (Debian)  / x86_64  (RPM)
#   linux-arm64  -> arm64 (Debian)  / aarch64 (RPM)

pkg_deb_arch() {
    case "$1" in
        linux-x64)   printf 'amd64\n' ;;
        linux-arm64) printf 'arm64\n' ;;
        *) pkg_die "unsupported runtime identifier: $1" ;;
    esac
}

pkg_rpm_arch() {
    case "$1" in
        linux-x64)   printf 'x86_64\n' ;;
        linux-arm64) printf 'aarch64\n' ;;
        *) pkg_die "unsupported runtime identifier: $1" ;;
    esac
}

pkg_ffmpeg_asset() {
    case "$1" in
        linux-x64)   printf 'jellyfin-ffmpeg_%s_portable_linux64-gpl.tar.xz\n'    "${FFMPEG_VERSION}" ;;
        linux-arm64) printf 'jellyfin-ffmpeg_%s_portable_linuxarm64-gpl.tar.xz\n' "${FFMPEG_VERSION}" ;;
        *) pkg_die "unsupported runtime identifier: $1" ;;
    esac
}

pkg_ffmpeg_sha256() {
    case "$1" in
        linux-x64)   printf '%s\n' "${FFMPEG_PORTABLE_SHA256_LINUX_X64}" ;;
        linux-arm64) printf '%s\n' "${FFMPEG_PORTABLE_SHA256_LINUX_ARM64}" ;;
        *) pkg_die "unsupported runtime identifier: $1" ;;
    esac
}

# --- pinned inputs -----------------------------------------------------------

# Reads one `ARG NAME=value` default out of the Dockerfile. The Dockerfile is the
# authority for every input the container image also consumes.
pkg_dockerfile_arg() {
    local name="$1" value
    value="$(sed -nE "s/^ARG ${name}=(.*)$/\1/p" "${PKG_DOCKERFILE}" | head -1)"
    [[ -n "${value}" ]] || pkg_die "no 'ARG ${name}=' default in ${PKG_DOCKERFILE}"
    printf '%s\n' "${value}"
}

# Exports every pinned input, then asserts the two sources agree.
pkg_load_pins() {
    [[ -f "${PKG_PINS_FILE}" ]] || pkg_die "missing ${PKG_PINS_FILE}"
    # shellcheck disable=SC1090
    source "${PKG_PINS_FILE}"

    WEB_ASSETS_IMAGE="$(pkg_dockerfile_arg WEB_ASSETS_IMAGE)"
    WEB_ASSETS_TAG="$(pkg_dockerfile_arg WEB_ASSETS_TAG)"
    WEB_VCS_REF="$(pkg_dockerfile_arg WEB_VCS_REF)"
    WEB_VERSION="$(pkg_dockerfile_arg WEB_VERSION)"

    local dockerfile_ffmpeg
    dockerfile_ffmpeg="$(pkg_dockerfile_arg FFMPEG_VERSION)"
    [[ "${dockerfile_ffmpeg}" == "${FFMPEG_VERSION}" ]] || pkg_die \
        "ffmpeg pin drift: Dockerfile says '${dockerfile_ffmpeg}', pins.env says '${FFMPEG_VERSION}'"

    [[ "${WEB_ASSETS_IMAGE}" == *"@sha256:"* ]] || pkg_die \
        "the web assets image must be pinned by digest, got '${WEB_ASSETS_IMAGE}'"
    [[ "${WEB_VCS_REF}" =~ ^[0-9a-f]{40}$ ]] || pkg_die \
        "WEB_VCS_REF '${WEB_VCS_REF}' is not a full 40-character lowercase SHA"

    [[ "${RPM_BUILDER_IMAGE}" == *"@sha256:"* ]] || pkg_die \
        "the rpm builder image must be pinned by digest, got '${RPM_BUILDER_IMAGE}'"

    export WEB_ASSETS_IMAGE WEB_ASSETS_TAG WEB_VCS_REF WEB_VERSION
    export FFMPEG_VERSION WEB_PAYLOAD_SHA256 RPM_BUILDER_IMAGE RPM_BUILDER_RPM_VERSION
    export RPM_ACCEPT_IMAGE DEB_ACCEPT_IMAGE ARCHIVE_ACCEPT_IMAGE
    export FFMPEG_PORTABLE_SHA256_LINUX_X64 FFMPEG_PORTABLE_SHA256_LINUX_ARM64
}

# Builds (and caches) the rpm toolchain image, then asserts the tool version is
# the pinned one. Prints the image tag.
pkg_rpm_builder_image() {
    local tag="tesserafin-rpm-builder:${RPM_BUILDER_RPM_VERSION}"
    if ! docker image inspect "${tag}" >/dev/null 2>&1; then
        printf 'FROM %s\nRUN dnf -y install rpm-build && dnf clean all\n' "${RPM_BUILDER_IMAGE}" \
            | docker build --quiet --tag "${tag}" - >/dev/null
    fi
    local actual
    actual="$(docker run --rm "${tag}" rpmbuild --version | awk '{print $NF}')"
    [[ "${actual}" == "${RPM_BUILDER_RPM_VERSION}" ]] || pkg_die \
        "rpm toolchain drift: the builder image has rpm ${actual}, pins.env expects ${RPM_BUILDER_RPM_VERSION}"
    printf '%s\n' "${tag}"
}

# The lifecycle environments. Neither base image ships systemd, so it is layered
# on top of the digest-pinned base along with the test tooling the scripts use.
# These images are never build inputs and never touch an artifact.
pkg_build_env_image() { # <tag> <base image> <install command>
    local tag="$1" base="$2" install="$3"
    if ! docker image inspect "${tag}" >/dev/null 2>&1; then
        printf 'FROM %s\nRUN %s\n' "${base}" "${install}" \
            | docker build --quiet --tag "${tag}" - >/dev/null
    fi
    printf '%s\n' "${tag}"
}

pkg_deb_accept_image() {
    pkg_build_env_image "tesserafin-deb-accept:24.04" "${DEB_ACCEPT_IMAGE}" \
        "export DEBIAN_FRONTEND=noninteractive && apt-get update -qq && \
         apt-get install -y --no-install-recommends systemd systemd-sysv dbus \
             curl python3 procps ca-certificates && rm -rf /var/lib/apt/lists/*"
}

# Rocky 9 already carries python3, shadow-utils and curl-minimal (which provides
# /usr/bin/curl); asking for `curl` on top of curl-minimal is a hard conflict.
pkg_rpm_accept_image() {
    pkg_build_env_image "tesserafin-rpm-accept:9" "${RPM_ACCEPT_IMAGE}" \
        "dnf -y install systemd procps-ng && dnf clean all"
}

# Version, commit and SOURCE_DATE_EPOCH come from the existing single source of
# version truth, never from logic duplicated here.
pkg_load_version_contract() {
    # A lifecycle container sees the repository as a read-only bind mount with no
    # git metadata at all, so the contract cannot run there — it reads the commit
    # time out of the object database. The outer run, which IS in a checkout,
    # resolves the contract once and hands the result down through these three
    # variables. They are never a fallback: either all three are present, or the
    # contract is consulted.
    if [[ -n "${PKG_VERSION:-}" && -n "${PKG_VCS_REF:-}" && -n "${PKG_SOURCE_DATE_EPOCH:-}" ]]; then
        VERSION="${PKG_VERSION}"
        VCS_REF="${PKG_VCS_REF}"
        SOURCE_DATE_EPOCH="${PKG_SOURCE_DATE_EPOCH}"
        export VERSION VCS_REF SOURCE_DATE_EPOCH
        return 0
    fi

    local env_block
    env_block="$("${PKG_REPO_ROOT}/docker/version-contract.sh" env "$@")"
    while IFS='=' read -r key value; do
        [[ -n "${key}" ]] || continue
        printf -v "${key}" '%s' "${value}"
        export "${key?}"
    done <<<"${env_block}"

    [[ -n "${VERSION:-}" ]]           || pkg_die "version contract produced no VERSION"
    [[ -n "${VCS_REF:-}" ]]           || pkg_die "version contract produced no VCS_REF"
    [[ -n "${SOURCE_DATE_EPOCH:-}" ]] || pkg_die "version contract produced no SOURCE_DATE_EPOCH"
}

# --- deterministic helpers ---------------------------------------------------

# One definition of "a reproducible tar of this directory": sorted, no owner
# identity, every mtime clamped, no extended headers.
#
# The clamp epoch is a parameter rather than always SOURCE_DATE_EPOCH, because
# the digest of an INPUT must not move when the server commit moves. The web
# payload is hashed at the epoch the web build recorded for itself, so its pinned
# digest stays valid until the web payload itself changes.
pkg_deterministic_tar() { # <dir> <dest.tar> [epoch]
    local epoch="${3:-${SOURCE_DATE_EPOCH}}"
    tar --create --file "$2" \
        --directory "$1" \
        --sort=name \
        --owner=0 --group=0 --numeric-owner \
        --mtime="@${epoch}" \
        --format=gnu \
        --exclude-vcs \
        .
}

pkg_tree_digest() { # <dir> [epoch] -> sha256 of its deterministic tar
    local tmp
    tmp="$(mktemp)"
    pkg_deterministic_tar "$1" "${tmp}" "${2:-${SOURCE_DATE_EPOCH}}"
    sha256sum "${tmp}" | cut -d' ' -f1
    rm -f "${tmp}"
}

pkg_clamp_mtimes() { # <dir>
    find "$1" -exec touch --no-dereference --date="@${SOURCE_DATE_EPOCH}" {} +
}

pkg_sha256() { sha256sum "$1" | cut -d' ' -f1; }

# --- embedded build-path hygiene ---------------------------------------------
#
# Some shipped bytes carry an absolute path from the machine that compiled them.
# There are exactly two kinds, and a gate that cannot tell them apart is useless:
#
#   * a path from THIS build — its checkout, its building account's home. Those
#     must never reach an artifact.
#   * a path from an UPSTREAM dependency, baked into a NuGet assembly by its own
#     maintainer long before this packaging saw the bytes. Those are inputs, they
#     are not this packaging's to rewrite, and they are enumerated EXACTLY in
#     ci/package/embedded-build-paths.allow. Anything outside the enumeration
#     fails, so a new dependency that embeds a path is reviewed rather than
#     absorbed.
#
# `${HOME}` is deliberately NOT the discriminator. On a GitHub runner it is
# `/home/runner` for this build AND for every upstream project that was itself
# built on Actions, so it cannot distinguish a first-party leak from an upstream
# one — it flags exactly the assemblies the rest of the gate then tolerates. The
# discriminator is the full path, matched against a closed list.

PKG_EMBEDDED_ALLOW_FILE="${PKG_REPO_ROOT}/ci/package/embedded-build-paths.allow"

# A build path is a user home or one of root's caches. `${PKG_REPO_ROOT}` is
# checked separately, because a checkout is not necessarily under either.
PKG_BUILD_PATH_RE='(/home/[A-Za-z0-9_][A-Za-z0-9_.+-]*|/root)(/[A-Za-z0-9_.+-]+)+'

PKG_EMBEDDED_ALLOW=()

pkg_load_embedded_allowlist() {
    [[ -f "${PKG_EMBEDDED_ALLOW_FILE}" ]] || pkg_die "missing ${PKG_EMBEDDED_ALLOW_FILE}"
    PKG_EMBEDDED_ALLOW=()
    local line name path
    while IFS= read -r line || [[ -n "${line}" ]]; do
        line="${line%%#*}"
        read -r name path <<<"${line}"
        [[ -n "${name:-}" ]] || continue
        [[ -n "${path:-}" && "${path}" == /* ]] || pkg_die \
            "malformed entry in ${PKG_EMBEDDED_ALLOW_FILE}: '${line}'"
        # The enumeration exists for UPSTREAM paths only. It must never be usable
        # to excuse a first-party one, so an entry naming this project is itself
        # an error rather than an allowance.
        if grep -qi 'tesserafin' <<<"${name} ${path}"; then
            pkg_die "${PKG_EMBEDDED_ALLOW_FILE} names Tesserafin in '${line}': \
the enumeration covers upstream dependencies only"
        fi
        PKG_EMBEDDED_ALLOW+=("${name}"$'\t'"${path}")
    done < "${PKG_EMBEDDED_ALLOW_FILE}"
    [[ "${#PKG_EMBEDDED_ALLOW[@]}" -gt 0 ]] || pkg_die \
        "${PKG_EMBEDDED_ALLOW_FILE} enumerates nothing"
}

# Scans one or more unpacked artifact roots and prints one tab-separated finding
# per (file, embedded path) pair:
#
#   LEAK <file> <path>    a build path that is not an enumerated upstream one
#   KNOWN <file> <path>   exactly an enumerated upstream dependency path
#
# Prints nothing when no shipped file carries a build path at all.
pkg_scan_embedded_build_paths() { # <root>...
    pkg_load_embedded_allowlist
    local root file base path entry known
    for root in "$@"; do
        [[ -d "${root}" ]] || continue
        while IFS= read -r file; do
            [[ -n "${file}" ]] || continue
            base="$(basename "${file}")"
            while IFS= read -r path; do
                [[ -n "${path}" ]] || continue
                known=0
                for entry in "${PKG_EMBEDDED_ALLOW[@]}"; do
                    if [[ "${entry}" == "${base}"$'\t'"${path}" ]]; then known=1; break; fi
                done
                if [[ "${known}" -eq 1 ]]; then
                    printf 'KNOWN\t%s\t%s\n' "${file}" "${path}"
                else
                    printf 'LEAK\t%s\t%s\n' "${file}" "${path}"
                fi
            done < <(grep -oaE "${PKG_BUILD_PATH_RE}" "${file}" 2>/dev/null | LC_ALL=C sort -u)
        done < <(grep -rlaE "${PKG_BUILD_PATH_RE}" "${root}" 2>/dev/null || true)
    done
}
