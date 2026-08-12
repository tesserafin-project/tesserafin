#!/usr/bin/env bash
# Strict gate for the package provenance manifests (#225 / [L0]).
#
# Usage: ci/package/verify-provenance.sh --artifacts DIR --rid RID
#
# Validates every <artifact>.provenance.json against the committed schema
# ci/package/provenance.schema.json, then checks the things a schema cannot: that
# the manifest agrees with the artifact it describes, with the checksum manifest
# beside it, and with the other two formats.
#
# The validator is implemented here rather than pulled from PyPI. It covers the
# subset of JSON Schema the committed schema actually uses — type, const, enum,
# pattern, required, additionalProperties, minimum and $ref into $defs — and it
# fails closed on any keyword it does not implement, so the schema cannot quietly
# grow a constraint that is never enforced. A validation gate whose availability
# depends on a network install is a gate that skips itself on the day it matters.

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

echo "== provenance manifests (${RID})"

python3 - "${PKG_REPO_ROOT}/ci/package/provenance.schema.json" "${ARTIFACTS}" "${RID}" \
          "$(pkg_deb_arch "${RID}")" "$(pkg_rpm_arch "${RID}")" "${VERSION}" \
          "${F0_SOURCE_ARCHIVE}" <<'PY'
import hashlib, json, os, re, sys

schema_path, artifacts, rid, deb_arch, rpm_arch, version, source_archive = sys.argv[1:8]
schema = json.load(open(schema_path))
defs = schema.get("$defs", {})
failures = []

SUPPORTED = {
    "$schema", "$id", "title", "description", "$defs", "$comment",
    "type", "const", "enum", "pattern", "required", "additionalProperties",
    "properties", "minimum", "$ref", "format",
}

TYPES = {
    "object": dict, "array": list, "string": str,
    "integer": int, "number": (int, float), "boolean": bool,
}


def resolve(node):
    if "$ref" in node:
        ref = node["$ref"]
        if not ref.startswith("#/$defs/"):
            raise SystemExit(f"unsupported $ref target: {ref}")
        return defs[ref[len("#/$defs/"):]]
    return node


def validate(node, value, path):
    node = resolve(node)
    unsupported = set(node) - SUPPORTED
    if unsupported:
        # Fail closed: an unimplemented keyword must not read as satisfied.
        failures.append(f"{path}: schema uses keyword(s) this validator does not "
                        f"implement: {sorted(unsupported)}")
        return

    if "type" in node:
        expected = TYPES[node["type"]]
        # bool is a subclass of int in Python; an integer field must not accept True.
        if isinstance(value, bool) and node["type"] in ("integer", "number"):
            failures.append(f"{path}: expected {node['type']}, got boolean")
            return
        if not isinstance(value, expected):
            failures.append(f"{path}: expected {node['type']}, got {type(value).__name__}")
            return

    if "const" in node and value != node["const"]:
        failures.append(f"{path}: expected the constant {node['const']!r}, got {value!r}")
    if "enum" in node and value not in node["enum"]:
        failures.append(f"{path}: {value!r} is not one of {node['enum']}")
    if "pattern" in node:
        if not isinstance(value, str) or not re.search(node["pattern"], value):
            failures.append(f"{path}: {value!r} does not match /{node['pattern']}/")
    if "minimum" in node and isinstance(value, (int, float)) and value < node["minimum"]:
        failures.append(f"{path}: {value} is below the minimum {node['minimum']}")

    if node.get("type") == "object" and isinstance(value, dict):
        props = node.get("properties", {})
        for key in node.get("required", []):
            if key not in value:
                failures.append(f"{path}: required key '{key}' is missing")
        if node.get("additionalProperties") is False:
            for key in value:
                if key not in props:
                    failures.append(f"{path}: unknown key '{key}' "
                                    f"(the schema declares no such field)")
        for key, sub in props.items():
            if key in value:
                validate(sub, value[key], f"{path}.{key}")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


manifests = sorted(f for f in os.listdir(artifacts) if f.endswith(".provenance.json"))
if len(manifests) != 3:
    failures.append(f"expected exactly 3 provenance manifests, found {len(manifests)}: {manifests}")

seen_formats = set()
shared = {}
expected_arch = {"deb": deb_arch, "rpm": rpm_arch, "tar.gz": rid}

for name in manifests:
    path = os.path.join(artifacts, name)
    p = f"{name}"
    try:
        m = json.load(open(path))
    except json.JSONDecodeError as exc:
        failures.append(f"{p}: not valid JSON ({exc})")
        continue

    validate(schema, m, p)

    fmt = m.get("packageFormat")
    seen_formats.add(fmt)

    # --- the schema cannot check these -------------------------------------

    # 1. Architecture agreement: the format's own spelling, the RID, and the
    #    runtime's architecture must all describe one machine.
    if fmt in expected_arch and m.get("architecture") != expected_arch[fmt]:
        failures.append(f"{p}: architecture '{m.get('architecture')}' is not the "
                        f"{fmt} spelling of {rid} ('{expected_arch[fmt]}')")
    if m.get("runtimeIdentifier") != rid:
        failures.append(f"{p}: runtimeIdentifier '{m.get('runtimeIdentifier')}' is not '{rid}'")
    rt = m.get("ffmpegRuntime", {})
    if rt.get("architecture") != rid:
        failures.append(f"{p}: the runtime was built for '{rt.get('architecture')}', "
                        f"but the package is {rid}")

    # 2. Filename agreement: the manifest names the artifact it sits beside, and
    #    that artifact exists with exactly the recorded size and digest.
    artifact_name = m.get("artifactFilename", "")
    if f"{artifact_name}.provenance.json" != name:
        failures.append(f"{p}: artifactFilename '{artifact_name}' does not match the manifest filename")
    artifact_path = os.path.join(artifacts, artifact_name)
    if not os.path.isfile(artifact_path):
        failures.append(f"{p}: names an artifact that is not present: {artifact_name}")
    else:
        actual_size = os.path.getsize(artifact_path)
        if actual_size != m.get("artifactSizeBytes"):
            failures.append(f"{p}: artifactSizeBytes {m.get('artifactSizeBytes')} != actual {actual_size}")
        actual_sha = sha256(artifact_path)
        if actual_sha != m.get("artifactSha256"):
            failures.append(f"{p}: artifactSha256 {m.get('artifactSha256')} != actual {actual_sha}")

    # 3. Source/runtime relationship: the corresponding source the manifest names
    #    is the one the sidecar actually is, and the runtime archive it names is
    #    present with the recorded digest.
    if rt.get("correspondingSource") != source_archive:
        failures.append(f"{p}: correspondingSource '{rt.get('correspondingSource')}' "
                        f"is not the expected '{source_archive}'")
    sidecar = os.path.join(artifacts, source_archive)
    if not os.path.isfile(sidecar):
        failures.append(f"{p}: the corresponding-source sidecar {source_archive} is not "
                        f"beside the artifacts")
    else:
        actual = sha256(sidecar)
        if actual != rt.get("correspondingSourceSha256"):
            failures.append(f"{p}: correspondingSourceSha256 {rt.get('correspondingSourceSha256')} "
                            f"!= the sidecar's actual {actual}")
    runtime_archive = os.path.join(artifacts, rt.get("runtimeArchive", ""))
    if not os.path.isfile(runtime_archive):
        failures.append(f"{p}: the runtime archive {rt.get('runtimeArchive')} is not beside the artifacts")
    elif sha256(runtime_archive) != rt.get("runtimeArchiveSha256"):
        failures.append(f"{p}: runtimeArchiveSha256 does not match the runtime archive present")

    # 4. Version agreement with the version contract.
    if m.get("packageVersion") != version:
        failures.append(f"{p}: packageVersion '{m.get('packageVersion')}' is not the "
                        f"contract version '{version}'")

    # 5. No obsolete upstream-asset field survives anywhere in the document.
    #    additionalProperties:false already rejects them at the declared levels;
    #    this catches one nested inside a field the schema allows to be free-form.
    flat = json.dumps(m)
    for obsolete in ("ffmpegAsset", "ffmpegVersion", "FFMPEG_PORTABLE_SHA256",
                     "portable_linux", "releases/download"):
        if obsolete in flat:
            failures.append(f"{p}: carries the obsolete upstream-asset reference '{obsolete}'")

    # 6. The three manifests must agree on everything that is not artifact-specific.
    for key in ("serverCommit", "webCommit", "webPayloadSha256",
                "applicationPayloadSha256", "stagedTreeSha256", "sourceDateEpoch"):
        if key in shared and shared[key] != m.get(key):
            failures.append(f"{p}: {key} '{m.get(key)}' disagrees with the other "
                            f"formats' '{shared[key]}'")
        shared.setdefault(key, m.get(key))
    if "runtime" in shared and shared["runtime"] != rt:
        failures.append(f"{p}: the ffmpegRuntime block differs from the other formats'")
    shared.setdefault("runtime", rt)

if seen_formats != {"deb", "rpm", "tar.gz"}:
    failures.append(f"the three package formats are not all described: {sorted(seen_formats)}")

# 7. The checksum manifest must list the sidecar, so a recipient verifying the
#    binaries can verify the source archive from the same file.
sums = os.path.join(artifacts, f"SHA256SUMS-{rid}.txt")
if not os.path.isfile(sums):
    failures.append(f"missing checksum manifest SHA256SUMS-{rid}.txt")
else:
    body = open(sums).read()
    if source_archive not in body:
        failures.append(f"SHA256SUMS-{rid}.txt does not list the corresponding-source "
                        f"archive {source_archive}")

for f in failures:
    print(f"  FAIL: {f}", file=sys.stderr)

if failures:
    print(f"\nPROVENANCE: FAIL — {len(failures)} finding(s)", file=sys.stderr)
    raise SystemExit(1)
print(f"  ok  : {len(manifests)} manifests valid against the committed schema")
print("  ok  : architecture, filename, size, digest, version and source relationships agree")
print("  ok  : no obsolete upstream-asset field in any manifest")
PY

echo
echo "PROVENANCE: PASS — ${RID}"
