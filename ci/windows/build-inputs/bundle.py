"""Deterministic bundle and OCI layout construction (#236, W1-R).

The identity of the retained build inputs is the OCI **manifest digest**. That
digest is only meaningful if the same inputs always produce the same bytes, so
every field an implementation is free to vary is fixed here rather than left to
a tool:

  * tar entries are emitted in sorted path order, with uid/gid 0, owner/group
    empty, mode 0644 for files and 0755 for directories, mtime `SOURCE_DATE_EPOCH`
    and no PAX or GNU extension headers;
  * the layer is UNCOMPRESSED tar. A compressor is another implementation whose
    output can change between versions; a layer that is only reproducible while
    zstd's defaults hold is not reproducible;
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
import io
import json
import tarfile
from pathlib import Path
from typing import Dict, List

BUNDLE_FORMAT = "tesserafin.windows-ffmpeg-build-inputs"
BUNDLE_FORMAT_VERSION = 1

ARTIFACT_TYPE = "application/vnd.tesserafin.windows-build-inputs.v1+json"
CONFIG_MEDIA_TYPE = "application/vnd.tesserafin.windows-build-inputs.config.v1+json"
LAYER_MEDIA_TYPE = "application/vnd.tesserafin.windows-build-inputs.layer.v1.tar"
MANIFEST_MEDIA_TYPE = "application/vnd.oci.image.manifest.v1+json"

# Fixed for every build of this bundle format. A real clock in the bytes would
# make the digest a function of when it was built rather than of what it holds.
SOURCE_DATE_EPOCH = 0


class BundleError(Exception):
    """Fail-closed condition. Never caught to continue."""


def canonical_json(value) -> bytes:
    """Serialise deterministically: sorted keys, fixed indent, LF, trailing LF."""
    return (json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode(
        "utf-8"
    )


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def relative_paths(root: Path) -> List[str]:
    """Every regular file under `root`, as sorted POSIX-relative paths.

    Directories are not listed: they carry no content, and an empty directory
    that existed on one machine and not the other would otherwise change the
    digest without changing anything delivered.
    """
    paths = []
    for entry in sorted(root.rglob("*")):
        if entry.is_symlink():
            raise BundleError(f"symlink in the bundle: {entry.relative_to(root)}")
        if entry.is_file():
            paths.append(entry.relative_to(root).as_posix())
    return sorted(paths)


def path_manifest(root: Path, exclude: str) -> Dict[str, str]:
    """`{relative path: sha256}` for every file except `exclude` itself."""
    return {
        path: sha256_file(root / path)
        for path in relative_paths(root)
        if path != exclude
    }


def make_layer(root: Path, out: Path) -> str:
    """Write the deterministic uncompressed tar layer. Returns its sha256."""
    paths = relative_paths(root)
    if not paths:
        raise BundleError("refusing to build an empty layer")

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


def build_oci_layout(
    bundle_root: Path,
    out_dir: Path,
    lock: dict,
    lock_sha256: str,
    trust_root_sha256: str,
) -> dict:
    """Build a digest-addressed OCI layout. Returns the descriptor summary.

    The layout is written by hand rather than by a registry client so that the
    manifest bytes are known BEFORE anything touches a registry: publication can
    then assert that what the registry stored is byte-identical to what was
    reviewed.
    """
    blobs = out_dir / "blobs" / "sha256"
    blobs.mkdir(parents=True, exist_ok=True)

    layer_path = out_dir / "layer.tar"
    layer_digest = make_layer(bundle_root, layer_path)
    layer_size = layer_path.stat().st_size
    layer_path.replace(blobs / layer_digest)

    config = {
        "bundleFormat": BUNDLE_FORMAT,
        "bundleFormatVersion": BUNDLE_FORMAT_VERSION,
        "target": lock["target"],
        "msystem": lock["msystem"],
        "ffmpegUpstreamCommit": lock["ffmpeg"]["upstreamCommit"],
        "ffmpegBuildRevision": lock["ffmpeg"]["buildRevision"],
        "lockSchemaVersion": lock["schemaVersion"],
        "lockSha256": lock_sha256,
        # The signing root the archives were authenticated against. It is part
        # of the artifact's identity: a bundle admitted under a different
        # allowlist is a different artifact, even if every archive matches.
        "trustRootSha256": trust_root_sha256,
        "packageCount": lock["packageCount"],
        "compressedBytes": lock["compressedBytes"],
        "installedBytes": lock["installedBytes"],
    }
    config_bytes = canonical_json(config)
    config_digest = hashlib.sha256(config_bytes).hexdigest()
    (blobs / config_digest).write_bytes(config_bytes)

    manifest = {
        "schemaVersion": 2,
        "mediaType": MANIFEST_MEDIA_TYPE,
        "artifactType": ARTIFACT_TYPE,
        "config": {
            "mediaType": CONFIG_MEDIA_TYPE,
            "digest": f"sha256:{config_digest}",
            "size": len(config_bytes),
        },
        "layers": [
            {
                "mediaType": LAYER_MEDIA_TYPE,
                "digest": f"sha256:{layer_digest}",
                "size": layer_size,
                "annotations": {
                    "org.opencontainers.image.title": "msys2-build-inputs.tar",
                },
            }
        ],
        "annotations": {
            # Stable identity only. No created timestamp, no run id, no branch.
            "org.opencontainers.image.description": (
                "Immutable MSYS2 build inputs for the native Windows FFmpeg runtime"
            ),
            "org.opencontainers.image.licenses": "see licenses/licenses.json in the layer",
            "org.opencontainers.image.source": (
                "https://github.com/tesserafin-project/tesserafin"
            ),
            "dev.tesserafin.buildinputs.ffmpegUpstreamCommit": lock["ffmpeg"][
                "upstreamCommit"
            ],
            "dev.tesserafin.buildinputs.lockSha256": lock_sha256,
            "dev.tesserafin.buildinputs.trustRootSha256": trust_root_sha256,
            "dev.tesserafin.buildinputs.packageCount": str(lock["packageCount"]),
        },
    }
    manifest_bytes = canonical_json(manifest)
    manifest_digest = hashlib.sha256(manifest_bytes).hexdigest()
    (out_dir / "manifest.json").write_bytes(manifest_bytes)

    summary = {
        "bundleFormat": BUNDLE_FORMAT,
        "bundleFormatVersion": BUNDLE_FORMAT_VERSION,
        "configDigest": f"sha256:{config_digest}",
        "configSize": len(config_bytes),
        "layerDigest": f"sha256:{layer_digest}",
        "layerSize": layer_size,
        "manifestDigest": f"sha256:{manifest_digest}",
        "manifestSize": len(manifest_bytes),
        "lockSha256": lock_sha256,
        "trustRootSha256": trust_root_sha256,
    }
    (out_dir / "descriptor.json").write_bytes(canonical_json(summary))
    return summary


def read_manifest_digest(out_dir: Path) -> str:
    """Recompute the manifest digest from the stored bytes.

    Recomputed rather than read back from `descriptor.json`, so a descriptor
    that disagrees with its own manifest cannot pass.
    """
    manifest_bytes = (out_dir / "manifest.json").read_bytes()
    return "sha256:" + hashlib.sha256(manifest_bytes).hexdigest()


def load_layer_index(layer: Path) -> Dict[str, str]:
    """`{path: sha256}` for every entry of a built layer, for comparison."""
    index: Dict[str, str] = {}
    with tarfile.open(layer, mode="r:") as archive:
        for member in archive.getmembers():
            if not member.isfile():
                raise BundleError(f"non-file entry in the layer: {member.name}")
            handle = archive.extractfile(member)
            if handle is None:
                raise BundleError(f"unreadable layer entry: {member.name}")
            index[member.name] = hashlib.sha256(handle.read()).hexdigest()
    return index


def write_bundle_metadata(
    bundle_root: Path, lock_bytes: bytes, databases: dict, trust_root_sha256: str
) -> str:
    """Write `bundle.json` and `manifest.sha256`. Returns the lock sha256."""
    lock_sha256 = hashlib.sha256(lock_bytes).hexdigest()
    lock = json.loads(lock_bytes)

    metadata = {
        "bundleFormat": BUNDLE_FORMAT,
        "bundleFormatVersion": BUNDLE_FORMAT_VERSION,
        "target": lock["target"],
        "msystem": lock["msystem"],
        "lockSha256": lock_sha256,
        "lockSchemaVersion": lock["schemaVersion"],
        "packageCount": lock["packageCount"],
        "repositoryDatabases": databases,
        "trustRootSha256": trust_root_sha256,
        "sourceDateEpoch": SOURCE_DATE_EPOCH,
        "notes": (
            "Build inputs only. Contains no FFmpeg binary, no Tesserafin server "
            "runtime and nothing that is shipped to a user. Consume by OCI "
            "manifest digest; a tag is never an accepted identity."
        ),
    }
    (bundle_root / "bundle.json").write_bytes(canonical_json(metadata))

    manifest = path_manifest(bundle_root, exclude="manifest.sha256")
    lines = [f"{digest}  {path}\n" for path, digest in sorted(manifest.items())]
    (bundle_root / "manifest.sha256").write_text("".join(lines), newline="\n")
    return lock_sha256
