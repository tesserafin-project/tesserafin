"""Deterministic retention of the ACCEPTED win-x64 FFmpeg runtime (#236, W1-A4).

This is the sibling of `ci/windows/build-inputs/bundle.py`, and it deliberately
repeats that module's conventions rather than importing them: the two artifacts
have different identities, different media types and different lifetimes, and a
shared helper would let a change made for one silently redefine the other.

What is different here, and why it matters more:

  * the build-input bundle carries build INFRASTRUCTURE. This one carries a
    GPL-3.0-or-later BINARY. The corresponding source is therefore not an
    attachment, it is a condition of retaining the binary at all, and
    `assert_source_availability` refuses a unit that separates them;
  * nothing in this module rebuilds, re-accepts or re-derives the runtime. The
    bytes it retains are the bytes W1-A3 proved. Every digest is checked against
    the committed acceptance manifest, never recomputed into it.

The identity of the retained runtime is the OCI **manifest digest**. That digest
is only meaningful if the same inputs always produce the same bytes, so every
field an implementation is free to vary is fixed here rather than left to a tool:

  * tar entries are emitted in sorted path order, with uid/gid 0, owner/group
    empty, mode 0644, mtime `SOURCE_DATE_EPOCH` and no PAX or GNU extension
    headers;
  * the layer is UNCOMPRESSED tar. The runtime archive and the corresponding
    source are already compressed; compressing them again would buy nothing and
    would make the digest depend on a compressor's defaults;
  * JSON is written with sorted keys, two-space indent, `\n` endings and a
    trailing newline;
  * annotations carry no timestamp, workflow id, runner path or branch name.
    `org.opencontainers.image.created` is deliberately ABSENT — it is the single
    most common reason an "identical" artifact has two digests.

The manifest bytes produced here are the bytes that get pushed. Publication uses
`oras manifest push`, never `oras push`, because the latter constructs its own
manifest and would silently replace these bytes.
"""

from __future__ import annotations

import hashlib
import json
import tarfile
from pathlib import Path
from typing import Dict, List

RETENTION_FORMAT = "tesserafin.windows-ffmpeg-runtime"
RETENTION_FORMAT_VERSION = 1

ARTIFACT_TYPE = "application/vnd.tesserafin.windows-ffmpeg-runtime.v1+json"
CONFIG_MEDIA_TYPE = "application/vnd.tesserafin.windows-ffmpeg-runtime.config.v1+json"
LAYER_MEDIA_TYPE = "application/vnd.tesserafin.windows-ffmpeg-runtime.layer.v1.tar"
MANIFEST_MEDIA_TYPE = "application/vnd.oci.image.manifest.v1+json"

LAYER_TITLE = "windows-ffmpeg-runtime.tar"

# Fixed for every build of this retention format. A real clock in the bytes
# would make the digest a function of when it was retained rather than of what
# it holds.
SOURCE_DATE_EPOCH = 0

# The delivered set W1-A3 accepted, as a count. A retention unit that carries a
# different number of delivered paths is not retaining the accepted runtime.
ACCEPTED_DELIVERED_PATHS = 31

# Where the two halves of the GPL obligation live inside the unit. Named here
# because `assert_source_availability` is what makes retaining the binary
# without the source impossible, and it must not be a string typed twice.
RUNTIME_PREFIX = "delivered/runtime/"
SOURCE_PREFIX = "delivered/source/"


class RetentionError(Exception):
    """Fail-closed condition. Never caught to continue."""


def canonical_json(value) -> bytes:
    """Serialise deterministically: sorted keys, fixed indent, LF, trailing LF."""
    return (
        json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    ).encode("utf-8")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _reject_unsafe(path: str) -> None:
    """Refuse any archive path that could escape extraction.

    Checked on the way IN, not on the way out. A unit that cannot be built with
    a traversal path in it cannot be published with one either, so the consumer
    never has to be the only thing standing between a registry and the
    filesystem.
    """
    if not path:
        raise RetentionError("empty path in the retention unit")
    if path.startswith("/"):
        raise RetentionError(f"absolute path in the retention unit: {path!r}")
    if "\\" in path:
        raise RetentionError(
            f"backslash in the retention unit path {path!r}: it would extract as "
            "a different name on Windows than on Linux"
        )
    parts = path.split("/")
    if any(part in ("", ".", "..") for part in parts):
        raise RetentionError(f"traversal or empty segment in path: {path!r}")
    if path.endswith("/"):
        raise RetentionError(f"directory entry in the retention unit: {path!r}")


def relative_paths(root: Path) -> List[str]:
    """Every regular file under `root`, as sorted POSIX-relative paths.

    Directories are not listed: they carry no content, and an empty directory
    that existed on one machine and not the other would otherwise change the
    digest without changing anything retained.

    A symlink is refused rather than followed. A link that resolves inside the
    unit would duplicate bytes the manifest already names; a link that resolves
    outside it is a way to retain something nobody reviewed.
    """
    paths: List[str] = []
    for entry in sorted(root.rglob("*")):
        if entry.is_symlink():
            raise RetentionError(
                f"symlink in the retention unit: {entry.relative_to(root)} — a link "
                "is not a retained byte"
            )
        if entry.is_file():
            relative = entry.relative_to(root).as_posix()
            _reject_unsafe(relative)
            paths.append(relative)
    if len(set(paths)) != len(paths):
        duplicates = sorted({p for p in paths if paths.count(p) > 1})
        raise RetentionError(f"duplicate paths in the retention unit: {duplicates}")
    return sorted(paths)


def path_manifest(root: Path) -> Dict[str, str]:
    """`{relative path: sha256}` for every file in the unit."""
    return {path: sha256_file(root / path) for path in relative_paths(root)}


def path_sizes(root: Path) -> Dict[str, int]:
    """`{relative path: size in bytes}` for every file in the unit."""
    return {path: (root / path).stat().st_size for path in relative_paths(root)}


def assert_source_availability(paths: List[str]) -> None:
    """Refuse a unit that retains the binary without its corresponding source.

    This is the GPL-3.0-or-later condition expressed as code rather than as a
    sentence in a document. The build-input pattern this module is modelled on
    has no equivalent, because build inputs are not a conveyed binary. A runtime
    is, so separating the two is not a degraded unit — it is a unit that must
    not exist.
    """
    runtime = [p for p in paths if p.startswith(RUNTIME_PREFIX)]
    source = [p for p in paths if p.startswith(SOURCE_PREFIX)]
    if not runtime:
        raise RetentionError(
            f"no runtime archive under {RUNTIME_PREFIX!r}: this is not a runtime "
            "retention unit"
        )
    if not source:
        raise RetentionError(
            "GPL-3.0-or-later refusal: the unit retains a runtime binary "
            f"({runtime[0]}) with no corresponding source under {SOURCE_PREFIX!r}. "
            "Retaining the binary while the corresponding source is absent is not "
            "a permitted state at any point."
        )


def make_layer(root: Path, out: Path) -> str:
    """Write the deterministic uncompressed tar layer. Returns its sha256."""
    paths = relative_paths(root)
    if not paths:
        raise RetentionError("refusing to build an empty layer")
    assert_source_availability(paths)

    with out.open("wb") as raw:
        with tarfile.open(fileobj=raw, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            for path in paths:
                source = root / path
                info = tarfile.TarInfo(name=path)
                info.size = source.stat().st_size
                info.mtime = SOURCE_DATE_EPOCH
                info.mode = 0o644
                info.uid = 0
                info.gid = 0
                info.uname = ""
                info.gname = ""
                info.type = tarfile.REGTYPE
                with source.open("rb") as handle:
                    archive.addfile(info, handle)
    return sha256_file(out)


def build_config(accepted: dict) -> dict:
    """The config blob: the accepted identity, and nothing that varies.

    Every field here becomes part of the manifest digest, so the set is chosen
    deliberately. It is drawn ENTIRELY from the committed acceptance manifest —
    never from the staged tree — so that a unit assembled from tampered bytes
    cannot describe itself into agreement.
    """
    return {
        "retentionFormat": RETENTION_FORMAT,
        "retentionFormatVersion": RETENTION_FORMAT_VERSION,
        "platform": accepted["platform"],
        "acceptedServerCommit": accepted["acceptedServerCommit"],
        "acceptedServerTree": accepted["acceptedServerTree"],
        "proofHead": accepted["proofHead"],
        "proofRun": accepted["proofRun"],
        "ffmpegUpstreamCommit": accepted["ffmpegUpstreamCommit"],
        "ffmpegBuildRevision": accepted["ffmpegBuildRevision"],
        "buildInputsReference": accepted["buildInputsReference"],
        "runtimeSha256": accepted["runtimeSha256"],
        "correspondingSourceSha256": accepted["correspondingSourceSha256"],
        "correspondingSourceStreamSha256": accepted["correspondingSourceStreamSha256"],
        "deliveredPathCount": accepted["deliveredPathCount"],
        "topology": accepted["topology"],
        "sameNode": accepted["sameNode"],
        "independenceClaim": accepted["independenceClaim"],
        "signed": accepted["signed"],
        "licence": accepted["licence"],
    }


def build_manifest(
    accepted: dict, config_digest: str, config_size: int, layer_digest: str, layer_size: int
) -> dict:
    """The manifest, as a pure function of the accepted identity and two blobs.

    Pure on purpose. Because nothing here reads the staged tree, the expected
    manifest digest can be reconstructed from the committed acceptance manifest
    ALONE — no 260 MB of artifacts, no registry, no expiring Actions download.
    That is what lets a pull-request gate keep verifying this contract long
    after the proof run's artifacts have expired.
    """
    return {
        "schemaVersion": 2,
        "mediaType": MANIFEST_MEDIA_TYPE,
        "artifactType": ARTIFACT_TYPE,
        "config": {
            "mediaType": CONFIG_MEDIA_TYPE,
            "digest": f"sha256:{config_digest}",
            "size": config_size,
        },
        "layers": [
            {
                "mediaType": LAYER_MEDIA_TYPE,
                "digest": f"sha256:{layer_digest}",
                "size": layer_size,
                "annotations": {
                    "org.opencontainers.image.title": LAYER_TITLE,
                },
            }
        ],
        "annotations": {
            # Stable identity only. No created timestamp, no run id, no branch.
            "org.opencontainers.image.description": (
                "Accepted native win-x64 FFmpeg runtime, its complete corresponding "
                "source and its acceptance evidence, retained as one unit"
            ),
            "org.opencontainers.image.licenses": accepted["licence"],
            "org.opencontainers.image.source": (
                "https://github.com/tesserafin-project/tesserafin"
            ),
            "org.opencontainers.image.revision": accepted["acceptedServerCommit"],
            "dev.tesserafin.runtime.proofHead": accepted["proofHead"],
            "dev.tesserafin.runtime.proofRun": str(accepted["proofRun"]),
            "dev.tesserafin.runtime.runtimeSha256": accepted["runtimeSha256"],
            "dev.tesserafin.runtime.correspondingSourceSha256": accepted[
                "correspondingSourceSha256"
            ],
            "dev.tesserafin.runtime.buildInputsReference": accepted[
                "buildInputsReference"
            ],
            "dev.tesserafin.runtime.topology": accepted["topology"],
            "dev.tesserafin.runtime.independenceClaim": accepted["independenceClaim"],
            "dev.tesserafin.runtime.signed": str(accepted["signed"]).lower(),
        },
    }


def expected_manifest_digest(accepted: dict) -> dict:
    """Reconstruct the expected manifest identity from committed data alone.

    Returns the same shape the builder reports, so the two can be compared field
    by field. Reads no file from the staged unit.
    """
    config_bytes = canonical_json(build_config(accepted))
    config_digest = sha256_bytes(config_bytes)
    manifest_bytes = canonical_json(
        build_manifest(
            accepted,
            config_digest,
            len(config_bytes),
            accepted["layerDigest"].removeprefix("sha256:"),
            accepted["layerSize"],
        )
    )
    return {
        "configDigest": f"sha256:{config_digest}",
        "configSize": len(config_bytes),
        "layerDigest": accepted["layerDigest"],
        "layerSize": accepted["layerSize"],
        "manifestDigest": f"sha256:{sha256_bytes(manifest_bytes)}",
        "manifestSize": len(manifest_bytes),
    }


def build_oci_layout(unit_root: Path, out_dir: Path, accepted: dict) -> dict:
    """Build a digest-addressed OCI layout. Returns the descriptor summary.

    The layout is written by hand rather than by a registry client so that the
    manifest bytes are known BEFORE anything touches a registry: publication can
    then assert that what the registry stored is byte-identical to what was
    reviewed.
    """
    blobs = out_dir / "blobs" / "sha256"
    blobs.mkdir(parents=True, exist_ok=True)

    layer_path = out_dir / "layer.tar"
    layer_digest = make_layer(unit_root, layer_path)
    layer_size = layer_path.stat().st_size
    layer_path.replace(blobs / layer_digest)

    config_bytes = canonical_json(build_config(accepted))
    config_digest = sha256_bytes(config_bytes)
    (blobs / config_digest).write_bytes(config_bytes)

    manifest = build_manifest(
        accepted, config_digest, len(config_bytes), layer_digest, layer_size
    )
    manifest_bytes = canonical_json(manifest)
    manifest_digest = sha256_bytes(manifest_bytes)
    (out_dir / "manifest.json").write_bytes(manifest_bytes)

    summary = {
        "retentionFormat": RETENTION_FORMAT,
        "retentionFormatVersion": RETENTION_FORMAT_VERSION,
        "configDigest": f"sha256:{config_digest}",
        "configSize": len(config_bytes),
        "layerDigest": f"sha256:{layer_digest}",
        "layerSize": layer_size,
        "manifestDigest": f"sha256:{manifest_digest}",
        "manifestSize": len(manifest_bytes),
    }
    (out_dir / "descriptor.json").write_bytes(canonical_json(summary))
    return summary


def read_manifest_digest(out_dir: Path) -> str:
    """Recompute the manifest digest from the stored bytes.

    Recomputed rather than read back from `descriptor.json`, so a descriptor
    that disagrees with its own manifest cannot pass.
    """
    manifest_bytes = (out_dir / "manifest.json").read_bytes()
    return "sha256:" + sha256_bytes(manifest_bytes)


def load_layer_index(layer: Path) -> Dict[str, str]:
    """`{path: sha256}` for every entry of a built layer, for comparison."""
    index: Dict[str, str] = {}
    with tarfile.open(layer, mode="r:") as archive:
        for member in archive.getmembers():
            if not member.isfile():
                raise RetentionError(f"non-file entry in the layer: {member.name}")
            _reject_unsafe(member.name)
            if member.name in index:
                raise RetentionError(f"duplicate entry in the layer: {member.name}")
            handle = archive.extractfile(member)
            if handle is None:
                raise RetentionError(f"unreadable layer entry: {member.name}")
            index[member.name] = hashlib.sha256(handle.read()).hexdigest()
    return index


def check_all(root):
    """The deterministic-layout gate, as the retention orchestrator's roster holds it.

    This is the part of determinism that is provable from committed data alone:
    the config and manifest bytes the layout builder emits are a pure function
    of the acceptance manifest's CONTENT, not of the order its keys happen to
    arrive in, nor of the run that produced them. Two evaluations must agree
    byte for byte, and so must an evaluation over a re-ordered copy — a builder
    that serialised a mapping in insertion order would pass the first and fail
    the second.

    It does NOT replace `build-twice.sh`. That builds the whole unit in two
    differently named directories and compares the file inventory, every
    per-file digest, the layer/config/manifest bytes and the manifest digest,
    which needs a staged unit and stays its own workflow job. What is here is
    the half that needs no fixture, so that a tree whose layout builder has
    become order-dependent is refused by the one canonical command rather than
    only by the job that happens to build a fixture.
    """
    import json as _json

    import boundary as _boundary

    findings = []
    path = root / _boundary.SUBTREE / "accepted-runtime.json"
    try:
        accepted = _json.loads(path.read_bytes())
    except (OSError, ValueError) as error:
        return [_boundary.Finding("layout.manifest-unreadable",
                                  f"{path} cannot be read as JSON: {error}")]

    try:
        first = expected_manifest_digest(accepted)
        second = expected_manifest_digest(_json.loads(_json.dumps(accepted)))
        reordered = expected_manifest_digest(
            {key: accepted[key] for key in sorted(accepted, reverse=True)})
        config_a = canonical_json(build_config(accepted))
        config_b = canonical_json(build_config(
            {key: accepted[key] for key in sorted(accepted, reverse=True)}))
    except Exception as error:  # noqa: BLE001 - a finding, not a traceback
        return [_boundary.Finding("layout.not-buildable",
                                  f"the layout cannot be derived from the committed "
                                  f"manifest: {type(error).__name__}: {error}")]

    if first != second:
        findings.append(_boundary.Finding(
            "layout.not-reproducible",
            f"two evaluations of the same manifest disagree: {first} vs {second}",
        ))
    if first != reordered:
        findings.append(_boundary.Finding(
            "layout.depends-on-mapping-order",
            f"re-ordering the manifest's keys changes the layout: {first} vs {reordered}; "
            f"a digest that depends on insertion order is not reproducible anywhere else",
        ))
    if config_a != config_b:
        findings.append(_boundary.Finding(
            "layout.config-depends-on-mapping-order",
            "the config blob's bytes change when the manifest's keys are re-ordered",
        ))
    if first["manifestDigest"] != accepted.get("manifestDigest"):
        findings.append(_boundary.Finding(
            "layout.digest-disagrees-with-committed",
            f"the layout derives {first['manifestDigest']!r}, but accepted-runtime.json "
            f"records {accepted.get('manifestDigest')!r}",
        ))
    return findings
