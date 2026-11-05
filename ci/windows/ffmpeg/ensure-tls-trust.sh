#!/usr/bin/env bash
# Make sure the CLANG64 TLS trust bundle the source fetch relies on is populated.
#
# WHY. The pinned `mingw-w64-clang-x86_64-ca-certificates` package ships its PEM
# bundles EMPTY (0 bytes) and fills them in its own post_install, by running the
# pinned `p11-kit extract` over the trust source it carries
# (share/pki/ca-trust-source/ca-bundle.trust.crt). On windows-latest image
# 20261103 that post_install left /clang64/etc/ssl/certs/ca-bundle.crt empty,
# and every HTTPS fetch then failed with
#   curl: (77) error adding trust anchors from file: .../clang64/etc/ssl/certs/ca-bundle.crt
# (first seen on #381, 2026-11-04; green on image 20261021).
#
# WHAT THIS DOES. If the bundle holds fewer than MIN_CERTS certificates, it
# re-runs exactly the server-auth extraction from that post_install, with the
# same pinned p11-kit and the same pinned trust source, and copies the result to
# the same two paths. Then it REQUIRES at least MIN_CERTS certificates and fails
# closed otherwise.
#
# WHAT THIS NEVER DOES. It does not disable or weaken certificate verification
# (no -k, no --insecure, no CURL_CA_BUNDLE pointing elsewhere), does not fetch
# trust from the network and does not use the runner's own certificate store.
# The anchors are the ones the digest-pinned package set already carries.
#
# MIN_CERTS: the pinned trust source carries 350 entries; its server-auth CA
# anchors are well above 100. A bundle below that is broken, not small.
#
# Usage: ensure-tls-trust.sh            (in an msys2 shell, MSYSTEM=CLANG64)

set -euo pipefail

PREFIX=/clang64
BUNDLE="${PREFIX}/etc/ssl/certs/ca-bundle.crt"
CERT_PEM="${PREFIX}/etc/ssl/cert.pem"
EXTRACTED="${PREFIX}/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem"
P11KIT="${PREFIX}/bin/p11-kit.exe"
MIN_CERTS=100

count() {
    local n
    n="$(grep -c '^-----BEGIN CERTIFICATE-----' "$1" 2>/dev/null || true)"
    echo "${n:-0}"
}

before="$(count "${BUNDLE}")"
echo "tls-trust: ${BUNDLE} holds ${before} certificate(s) before the check"

if [ "${before}" -lt "${MIN_CERTS}" ]; then
    [ -x "${P11KIT}" ] || { echo "tls-trust: the pinned ${P11KIT} is missing" >&2; exit 1; }
    echo "tls-trust: re-running the pinned package's own server-auth extraction"
    mkdir -p "$(dirname "${EXTRACTED}")" "$(dirname "${BUNDLE}")"
    "${P11KIT}" extract --format=pem-bundle --filter=ca-anchors --overwrite --comment \
        --purpose server-auth "${EXTRACTED}"
    cp -f "${EXTRACTED}" "${BUNDLE}"
    cp -f "${EXTRACTED}" "${CERT_PEM}"
fi

after="$(count "${BUNDLE}")"
if [ "${after}" -lt "${MIN_CERTS}" ]; then
    echo "tls-trust: ${BUNDLE} still holds ${after} certificate(s), below ${MIN_CERTS}; refusing to fetch" >&2
    exit 1
fi
echo "tls-trust: ${BUNDLE} holds ${after} certificate(s)"
