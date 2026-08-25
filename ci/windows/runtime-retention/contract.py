"""The W1-A4 retention contract, as code (#236).

Three properties live here, and each is hardcoded rather than parameterised.
That hardcoding IS the security property: a caller cannot redirect this
machinery at another package, another branch or another digest by passing a
different argument, because there is no argument to pass.

  1. exactly one authorised package, consumed only by `sha256:` manifest digest;
  2. publication only from trusted `master`;
  3. the publisher is TOLD what it may push, and pushes nothing else.

It also closes a gap the build-input pattern left open: `expected-digest.json`
there is only ever read field by field and has no schema validator, so an
unknown or missing field in it is silently ignored. Here the acceptance manifest
is validated against a CLOSED schema before any value in it is believed.
"""

from __future__ import annotations

import re
from typing import Any, Dict

REGISTRY = "ghcr.io"
REPOSITORY = "tesserafin-project/windows-ffmpeg-runtime"
CANONICAL = f"{REGISTRY}/{REPOSITORY}"
TRUSTED_REF = "refs/heads/master"

SUPPORTED_SCHEMA_VERSIONS = {1}

_DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
_BARE_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_COMMIT = re.compile(r"^[0-9a-f]{40}$")

# The immutable convenience tag the owner ruling permits. It is derived from the
# accepted commit, it never moves, and it is NEVER a consumer authority: nothing
# in this repository resolves it. It exists so a human can find the package.
_IMMUTABLE_TAG = re.compile(r"^accepted-[0-9a-f]{12}$")

# Closed schema. Every key the acceptance manifest may carry, and nothing else.
# `$comment` keys are prose and are allowed anywhere at the top level.
_REQUIRED: Dict[str, type | tuple] = {
    "schemaVersion": int,
    "platform": str,
    "acceptedServerCommit": str,
    "acceptedServerTree": str,
    "proofHead": str,
    "proofRun": int,
    "ffmpegUpstreamCommit": str,
    "ffmpegBuildRevision": str,
    "buildInputsReference": str,
    "runtimeSha256": str,
    "runtimeSize": int,
    "runtimePath": str,
    "correspondingSourceSha256": str,
    "correspondingSourceSize": int,
    "correspondingSourceStreamSha256": str,
    "correspondingSourcePath": str,
    "checksumManifestSha256": str,
    "checksumManifestPath": str,
    "deliveredPathCount": int,
    "provenanceSha256": str,
    "sbomSha256": str,
    "noticesSha256": str,
    "capabilitySha256": str,
    "peClosureSha256": str,
    "buildConfigurationSha256": str,
    "licenceFileCount": int,
    "evidence": dict,
    "topology": str,
    "sameNode": bool,
    "independenceClaim": str,
    "signed": bool,
    "licence": str,
    "registry": str,
    "repository": str,
    "artifactType": str,
    "configMediaType": str,
    "layerMediaType": str,
    "manifestMediaType": str,
    "immutableTag": str,
    "layerDigest": str,
    "layerSize": int,
    "configDigest": str,
    "configSize": int,
    "manifestDigest": str,
    "manifestSize": int,
    "reference": str,
    "unitPaths": dict,
    "published": bool,
}

_EVIDENCE_REQUIRED = {
    "comparisonSha256": str,
    "hostA": dict,
    "hostB": dict,
}

_HOST_REQUIRED = {
    "acceptRuntimeSha256": str,
    "runnerJsonSha256": str,
    "runnerName": str,
    "node": str,
    "imageOs": str,
    "imageVersion": str,
    "bundlePathCount": int,
}


class ContractError(Exception):
    """Fail-closed condition. Never caught to continue."""


def parse_reference(reference: str) -> str:
    """Return the digest of `reference`, or refuse it.

    A tag is never an accepted identity. This is the first of two independent
    statements of that rule; `oci-protocol.sh` makes the second one, because
    that script is what actually talks to the registry.
    """
    if "@" not in reference:
        raise ContractError(
            f"{reference!r} is not digest-pinned. A tag is never an accepted "
            f"identity; use {CANONICAL}@sha256:<digest>"
        )
    name, _, digest = reference.partition("@")
    if ":" in name.rsplit("/", 1)[-1]:
        raise ContractError(
            f"{reference!r} carries both a tag and a digest; use the digest alone"
        )
    if name != CANONICAL:
        raise ContractError(
            f"{name!r} is not the authorised package. W1-A4 authorises exactly one: "
            f"{CANONICAL}"
        )
    if not _DIGEST.match(digest):
        raise ContractError(f"{digest!r} is not a sha256 manifest digest")
    return digest


def assert_trusted_ref(github_ref: str) -> None:
    """Publication runs from trusted master, or it does not run."""
    if github_ref == TRUSTED_REF:
        return
    if github_ref.startswith("refs/pull/"):
        reason = "a pull request ref carries unreviewed code"
    elif github_ref.startswith("refs/tags/"):
        reason = "a tag can be created and moved without review"
    elif github_ref.startswith("refs/heads/"):
        reason = "a feature branch is not trusted"
    else:
        reason = "the ref is not a branch this repository trusts"
    raise ContractError(
        f"publication refused: github.ref is {github_ref!r} and not {TRUSTED_REF!r} "
        f"({reason})"
    )


def assert_expected_digest(expected: str, actual: str) -> None:
    """The publisher must be told, in advance, exactly what it is allowed to push."""
    if not expected:
        raise ContractError(
            "publication refused: no expected manifest digest was supplied. The "
            "publisher pushes a reviewed digest; it does not publish whatever it "
            "happens to have built."
        )
    if not _DIGEST.match(expected):
        raise ContractError(f"expected digest {expected!r} is not a sha256 digest")
    if not _DIGEST.match(actual):
        raise ContractError(f"built digest {actual!r} is not a sha256 digest")
    if expected != actual:
        raise ContractError(
            f"publication refused: this run built {actual}, but the reviewed digest "
            f"is {expected}. Nothing is pushed."
        )


def assert_tag_free_or_agreeing(tag: str, resolved_digest: str | None, expected: str) -> None:
    """An immutable tag may exist. It may never point somewhere else.

    Three outcomes, and only three: the tag is absent and will be created; the
    tag already resolves to exactly the reviewed digest, which makes a second
    publication an idempotent no-op; or the tag resolves elsewhere, which is
    refused. There is no fourth branch that moves it.
    """
    if not _IMMUTABLE_TAG.match(tag):
        raise ContractError(
            f"{tag!r} is not an immutable acceptance tag. The owner ruling permits "
            "one immutable tag derived from the accepted commit "
            "(accepted-<12 hex>), and no moving, version or architecture tag."
        )
    if resolved_digest is None:
        return
    if not _DIGEST.match(resolved_digest):
        raise ContractError(f"{resolved_digest!r} is not a sha256 manifest digest")
    if resolved_digest != expected:
        raise ContractError(
            f"publication refused: tag {tag!r} already resolves to {resolved_digest}, "
            f"but the reviewed digest is {expected}. An immutable tag is never "
            "repointed; publish a new digest and record it instead."
        )


def _check_keys(where: str, value: Dict[str, Any], required: Dict[str, Any]) -> None:
    keys = {k for k in value if not k.startswith("$")}
    missing = set(required) - keys
    unknown = keys - set(required)
    if missing:
        raise ContractError(f"{where}: missing required field(s): {sorted(missing)}")
    if unknown:
        raise ContractError(
            f"{where}: unknown field(s) {sorted(unknown)} — this validator does not "
            "implement their semantics and refuses to report the manifest as validated"
        )
    for key, kind in required.items():
        actual = value[key]
        # `bool` is a subclass of `int`, so a plain isinstance check would let
        # `true` pass wherever a count is required. Booleans are matched
        # exactly, in both directions.
        if isinstance(actual, bool) != (kind is bool) or not isinstance(actual, kind):
            raise ContractError(
                f"{where}: field {key!r} is {type(actual).__name__}, expected "
                f"{kind.__name__}"
            )


def _require_sha256(where: str, value: str) -> None:
    if not _BARE_SHA256.match(value):
        raise ContractError(f"{where}: {value!r} is not a bare sha256 hex digest")


def _require_commit(where: str, value: str) -> None:
    if not _COMMIT.match(value):
        raise ContractError(f"{where}: {value!r} is not a 40-character commit sha")


def validate_accepted(accepted: Dict[str, Any]) -> None:
    """Validate the acceptance manifest against the closed schema.

    Called before ANY value in the manifest is believed, by the builder, by the
    validator, by the consumer and by the publisher. A manifest that does not
    survive this is not a weaker manifest, it is not a manifest.
    """
    _check_keys("accepted-runtime.json", accepted, _REQUIRED)

    # First, before any count, digest or scalar comparison: a unit that retains
    # the binary without its corresponding source is not a manifest with a bad
    # field, it is a licence violation, and it must be reported as one. Checking
    # deliveredPathCount first would refuse it as "30 is not 31", which names
    # the wrong defect and sends a reader looking for a miscount.
    from retention import RUNTIME_PREFIX, SOURCE_PREFIX  # local: avoids a cycle

    _paths = accepted.get("unitPaths")
    if isinstance(_paths, dict) and any(p.startswith(RUNTIME_PREFIX) for p in _paths):
        if not any(p.startswith(SOURCE_PREFIX) for p in _paths):
            raise ContractError(
                "GPL-3.0-or-later refusal: the manifest retains a runtime binary with "
                f"no corresponding source under {SOURCE_PREFIX!r}. Retaining the binary "
                "while the corresponding source is absent is not a permitted state at "
                "any point."
            )

    if accepted["schemaVersion"] not in SUPPORTED_SCHEMA_VERSIONS:
        raise ContractError(
            f"schemaVersion {accepted['schemaVersion']} is not implemented by this "
            f"validator (supported: {sorted(SUPPORTED_SCHEMA_VERSIONS)})"
        )
    if accepted["platform"] != "win-x64":
        raise ContractError(
            f"platform {accepted['platform']!r} is not win-x64; this contract retains "
            "the native Windows runtime and nothing else"
        )
    if accepted["registry"] != REGISTRY or accepted["repository"] != REPOSITORY:
        raise ContractError(
            f"the manifest names {accepted['registry']}/{accepted['repository']}, but "
            f"W1-A4 authorises exactly one package: {CANONICAL}"
        )
    if accepted["published"] is not False:
        raise ContractError(
            "published must be false in the tree: publication is a separately "
            "reviewed step, and a committed 'true' would assert something no "
            "reviewer of this branch can see"
        )
    if accepted["signed"] is not False:
        raise ContractError(
            "signed must be false: W1-A3 accepted an UNSIGNED runtime, and the owner "
            "ruling authorises no signature"
        )
    if accepted["topology"] != "dual-runner":
        raise ContractError(
            f"topology {accepted['topology']!r} is not the accepted 'dual-runner'"
        )
    if accepted["sameNode"] is not True:
        raise ContractError(
            "sameNode must be true: W1-A3 measured both allocations on one node, and "
            "recording otherwise would claim a separation the proof does not have"
        )
    if accepted["independenceClaim"] != "none":
        raise ContractError(
            f"independenceClaim {accepted['independenceClaim']!r} is not 'none'; this "
            "runtime is dual-runner reproducible, not two-host independent"
        )
    if accepted["deliveredPathCount"] != 31:
        raise ContractError(
            f"deliveredPathCount {accepted['deliveredPathCount']} is not the accepted 31"
        )
    if not accepted["licence"].startswith("GPL-3.0-or-later"):
        raise ContractError(
            f"licence {accepted['licence']!r} does not declare GPL-3.0-or-later; the "
            "corresponding-source obligation is derived from it"
        )

    for field in ("acceptedServerCommit", "acceptedServerTree", "proofHead", "ffmpegUpstreamCommit"):
        _require_commit(field, accepted[field])
    for field in (
        "runtimeSha256",
        "correspondingSourceSha256",
        "correspondingSourceStreamSha256",
        "checksumManifestSha256",
        "provenanceSha256",
        "sbomSha256",
        "noticesSha256",
        "capabilitySha256",
        "peClosureSha256",
        "buildConfigurationSha256",
    ):
        _require_sha256(field, accepted[field])
    for field in ("layerDigest", "configDigest", "manifestDigest"):
        if not _DIGEST.match(accepted[field]):
            raise ContractError(f"{field}: {accepted[field]!r} is not a sha256: digest")

    if "@sha256:" not in accepted["buildInputsReference"]:
        raise ContractError(
            f"buildInputsReference {accepted['buildInputsReference']!r} is not "
            "digest-pinned"
        )

    expected_reference = f"{CANONICAL}@{accepted['manifestDigest']}"
    if accepted["reference"] != expected_reference:
        raise ContractError(
            f"reference {accepted['reference']!r} disagrees with the manifest digest; "
            f"expected {expected_reference}"
        )
    if parse_reference(accepted["reference"]) != accepted["manifestDigest"]:
        raise ContractError("reference and manifestDigest disagree")

    assert_tag_free_or_agreeing(accepted["immutableTag"], None, accepted["manifestDigest"])
    if accepted["immutableTag"] != f"accepted-{accepted['acceptedServerCommit'][:12]}":
        raise ContractError(
            f"immutableTag {accepted['immutableTag']!r} is not derived from the "
            f"accepted commit {accepted['acceptedServerCommit'][:12]}"
        )

    _check_keys("accepted-runtime.json:evidence", accepted["evidence"], _EVIDENCE_REQUIRED)
    _require_sha256("evidence.comparisonSha256", accepted["evidence"]["comparisonSha256"])
    for host in ("hostA", "hostB"):
        _check_keys(f"evidence.{host}", accepted["evidence"][host], _HOST_REQUIRED)
        _require_sha256(
            f"evidence.{host}.acceptRuntimeSha256",
            accepted["evidence"][host]["acceptRuntimeSha256"],
        )
        _require_sha256(
            f"evidence.{host}.runnerJsonSha256",
            accepted["evidence"][host]["runnerJsonSha256"],
        )
    a = accepted["evidence"]["hostA"]
    b = accepted["evidence"]["hostB"]
    if a["runnerName"] == b["runnerName"]:
        raise ContractError(
            "evidence: both bundles claim runner allocation "
            f"{a['runnerName']!r}. Two allocations are what the dual-runner topology "
            "asserts; one allocation reported twice is a single build described "
            "as two."
        )

    _validate_unit_paths(accepted)


def _validate_unit_paths(accepted: Dict[str, Any]) -> None:
    """The pinned inventory: every retained path, its size and its digest.

    Sorted order is the layer order. Without it the expected manifest digest
    would not be reconstructible from the committed manifest alone, and the
    pull-request gate would have to hold 260 MB of expiring artifacts to check
    anything.
    """
    unit = accepted["unitPaths"]
    if not unit:
        raise ContractError("unitPaths is empty; the retention unit has no inventory")
    seen_lower = {}
    for path, entry in unit.items():
        if path.startswith("/"):
            raise ContractError(f"unitPaths: absolute path {path!r}")
        if "\\" in path:
            raise ContractError(f"unitPaths: backslash in {path!r}")
        if any(part in ("", ".", "..") for part in path.split("/")):
            raise ContractError(f"unitPaths: traversal or empty segment in {path!r}")
        lowered = path.lower()
        if lowered in seen_lower:
            raise ContractError(
                f"unitPaths: {path!r} and {seen_lower[lowered]!r} differ only by case "
                "and cannot both be extracted on Windows"
            )
        seen_lower[lowered] = path
        _check_keys(f"unitPaths[{path}]", entry, {"sha256": str, "size": int})
        _require_sha256(f"unitPaths[{path}].sha256", entry["sha256"])
        if entry["size"] < 0:
            raise ContractError(f"unitPaths[{path}]: negative size")

    from retention import RUNTIME_PREFIX, SOURCE_PREFIX  # local: avoids a cycle

    if not any(p.startswith(RUNTIME_PREFIX) for p in unit):
        raise ContractError(f"unitPaths carries no runtime under {RUNTIME_PREFIX!r}")
    if not any(p.startswith(SOURCE_PREFIX) for p in unit):
        raise ContractError(
            "GPL-3.0-or-later refusal: unitPaths retains a runtime binary with no "
            f"corresponding source under {SOURCE_PREFIX!r}"
        )

    runtime_entries = [p for p in unit if p.startswith(RUNTIME_PREFIX)]
    if len(runtime_entries) != 1:
        raise ContractError(f"expected exactly one runtime archive, found {runtime_entries}")
    if unit[runtime_entries[0]]["sha256"] != accepted["runtimeSha256"]:
        raise ContractError(
            f"unitPaths[{runtime_entries[0]}] does not carry the accepted runtime digest"
        )
    if unit[runtime_entries[0]]["size"] != accepted["runtimeSize"]:
        raise ContractError(
            f"unitPaths[{runtime_entries[0]}] does not carry the accepted runtime size"
        )
    if runtime_entries[0] != accepted["runtimePath"]:
        raise ContractError(
            f"runtimePath {accepted['runtimePath']!r} is not the retained runtime "
            f"{runtime_entries[0]!r}"
        )

    source_entries = [p for p in unit if p.startswith(SOURCE_PREFIX)]
    if len(source_entries) != 1:
        raise ContractError(f"expected exactly one source archive, found {source_entries}")
    if unit[source_entries[0]]["sha256"] != accepted["correspondingSourceSha256"]:
        raise ContractError(
            f"unitPaths[{source_entries[0]}] does not carry the accepted source digest"
        )
    if source_entries[0] != accepted["correspondingSourcePath"]:
        raise ContractError(
            f"correspondingSourcePath {accepted['correspondingSourcePath']!r} is not "
            f"the retained source {source_entries[0]!r}"
        )

    # licenceFileCount is cross-checked against unitPaths rather than against a
    # second copy of the same digests. An earlier revision carried a
    # `licenceDigests` map of path -> digest; it was redundant with unitPaths,
    # and its bare `"...nvEncodeAPI.h": "<64 hex>"` lines were read by
    # gitleaks' generic-api-key rule as an API key next to its value. One
    # source of truth for per-file digests removes both problems.
    licences = [p for p in unit if p.startswith("delivered/licenses/")]
    if len(licences) != accepted["licenceFileCount"]:
        raise ContractError(
            f"unitPaths carries {len(licences)} licence files, but licenceFileCount "
            f"says {accepted['licenceFileCount']}"
        )

    delivered = [p for p in unit if p.startswith("delivered/")]
    if len(delivered) != accepted["deliveredPathCount"]:
        raise ContractError(
            f"unitPaths carries {len(delivered)} delivered paths, but "
            f"deliveredPathCount says {accepted['deliveredPathCount']}"
        )
