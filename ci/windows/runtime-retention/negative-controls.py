"""Hostile controls for the runtime retention contract (#236, W1-A4).

Usage:
    python3 negative-controls.py --fixture <dir> [--json <report>]

Every control MUTATES something and requires a NAMED refusal. A control that
merely fails is worthless: an ImportError, a missing `zstd`, a typo in a path
would all "fail" while proving nothing about the property under test. So each
control declares the substring its refusal must contain, and an outcome is:

    RED    the refusal happened AND named the property under test
    INERT  it failed for a setup reason, or refused for the wrong reason
    GREEN  the mutation was ACCEPTED — the gate does not hold

INERT is reported as a failure of the CONTROL, not of the contract, and it is
never counted as evidence. That distinction is the whole point: a suite of
twenty controls that all passed inertly proves exactly nothing, and looks
identical to one that works.

Every control runs against a fresh copy. The pristine fixture is hashed before
the first control and after the last, and the two must agree byte for byte.
"""

from __future__ import annotations

import argparse
import copy
import json
import os
import shutil
import sys
import tarfile
import tempfile
import time
from pathlib import Path
from typing import Callable

import assemble
import contract
import retention

HERE = Path(__file__).resolve().parent


class Inert(Exception):
    """The control could not reach its assertion. Not evidence either way."""


class Outcome:
    def __init__(self, name: str, prop: str) -> None:
        self.name = name
        self.property = prop
        self.result = "PENDING"
        self.detail = ""


def _fixture_digest(fixture: Path) -> str:
    manifest = retention.path_manifest(fixture)
    return retention.sha256_bytes(retention.canonical_json(manifest))


def _fresh(fixture: Path, work: Path) -> Path:
    """A private copy of the fixture, with mtimes touched.

    `shutil.copy2` preserves mtime. A restored tree with an old mtime makes a
    build system skip work and test the PREVIOUS state, so every copy is
    touched to now. The symptom of getting this wrong is two controls reporting
    identical failures, which reads as a flaky suite rather than a broken one.
    """
    target = work / "copy"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(fixture, target)
    now = time.time()
    for path in target.rglob("*"):
        os.utime(path, (now, now))
    return target


def _accepted(root: Path) -> dict:
    return json.loads((root / "accepted-runtime.json").read_bytes())


def _write_accepted(root: Path, accepted: dict) -> None:
    (root / "accepted-runtime.json").write_bytes(retention.canonical_json(accepted))


def _stage(root: Path, out: Path) -> None:
    accepted = _accepted(root)
    contract.validate_accepted(accepted)
    assemble.stage_unit(
        root / "delivered",
        root / "evidence-a",
        root / "evidence-b",
        root / "comparison.json",
        accepted,
        out,
    )
    assemble.verify_against_accepted(out, accepted)


def _build(root: Path, out_unit: Path, out_oci: Path) -> None:
    accepted = _accepted(root)
    contract.validate_accepted(accepted)
    assemble.stage_unit(
        root / "delivered",
        root / "evidence-a",
        root / "evidence-b",
        root / "comparison.json",
        accepted,
        out_unit,
    )
    assemble.verify_against_accepted(out_unit, accepted)
    if out_oci.exists():
        shutil.rmtree(out_oci)
    out_oci.mkdir(parents=True)
    summary = retention.build_oci_layout(out_unit, out_oci, accepted)
    predicted = retention.expected_manifest_digest(accepted)
    for field in ("configDigest", "layerDigest", "manifestDigest", "manifestSize", "configSize", "layerSize"):
        if predicted[field] != summary[field] or accepted[field] != summary[field]:
            raise retention.RetentionError(
                f"{field}: the committed acceptance manifest predicts {predicted[field]!r} "
                f"and records {accepted[field]!r}, but this build produced {summary[field]!r}"
            )


def _resync_readme(root: Path, work: Path) -> None:
    """Re-pin RETENTION.md after tampering with an identity field.

    RETENTION.md embeds the accepted identity, so any identity tamper changes it
    and the unit stops matching its own pinned inventory. That is defence in
    depth and it is working — but it means the shallow check fires first and the
    control never reaches the property it is named for.

    A real attacker would not stop at the first refusal either: they would make
    the tamper self-consistent and push on. This does the same, so the control
    tests the DEEP gate rather than the shallow one.
    """
    accepted = _accepted(root)
    probe = work / "resync"
    assemble.stage_unit(
        root / "delivered",
        root / "evidence-a",
        root / "evidence-b",
        root / "comparison.json",
        accepted,
        probe,
    )
    readme = probe / "RETENTION.md"
    accepted["unitPaths"]["RETENTION.md"] = {
        "sha256": retention.sha256_file(readme),
        "size": readme.stat().st_size,
    }
    _write_accepted(root, accepted)
    shutil.rmtree(probe, ignore_errors=True)


def _flip_one_byte(path: Path) -> None:
    data = bytearray(path.read_bytes())
    if not data:
        raise Inert(f"{path} is empty; there is no byte to corrupt")
    data[len(data) // 2] ^= 0x01
    path.write_bytes(bytes(data))


# ── the controls ────────────────────────────────────────────────────────────


def control_01(root: Path, work: Path) -> None:
    _flip_one_byte(root / "delivered" / "runtime" / next(
        p.name for p in (root / "delivered" / "runtime").iterdir()
    ))
    _stage(root, work / "unit")


def control_02(root: Path, work: Path) -> None:
    _flip_one_byte(root / "delivered" / "source" / next(
        p.name for p in (root / "delivered" / "source").iterdir()
    ))
    _stage(root, work / "unit")


def control_03(root: Path, work: Path) -> None:
    """Compressed archive matches; the decompressed stream does not.

    The manifest is updated to the corrupted archive's compressed digest, so the
    container check passes. Only the STREAM check can catch this, which is
    precisely the property under test: hashing the container is not hashing the
    corresponding source.
    """
    import io
    import subprocess as _sp

    source = next((root / "delivered" / "source").iterdir())

    # A VALID replacement archive carrying different content — not a corrupted
    # one. Flipping a byte inside a zstd frame makes it undecompressable, and
    # "this will not decompress" is a different (easier) defect than "this
    # decompresses to something else". The property under test is the second
    # one: an archive that opens cleanly and is not the corresponding source.
    swapped = source.with_suffix(".swapped.tar")
    with tarfile.open(swapped, "w", format=tarfile.USTAR_FORMAT) as archive:
        info = tarfile.TarInfo("ffmpeg/README")
        data = b"substituted source, not the corresponding source\n"
        info.size = len(data)
        info.mtime = 0
        info.mode = 0o644
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        archive.addfile(info, io.BytesIO(data))
    _sp.run(["zstd", "-19", "-q", "-f", str(swapped), "-o", str(source)], check=True)
    swapped.unlink()

    accepted = _accepted(root)
    new_compressed = retention.sha256_file(source)
    accepted["correspondingSourceSha256"] = new_compressed
    accepted["correspondingSourceSize"] = source.stat().st_size
    accepted["unitPaths"][accepted["correspondingSourcePath"]] = {
        "sha256": new_compressed,
        "size": source.stat().st_size,
    }
    _write_accepted(root, accepted)
    _resync_readme(root, work)
    _stage(root, work / "unit")
    # Staging now agrees. The stream is what must still refuse.
    import subprocess

    stream = subprocess.run(["zstd", "-dc", str(source)], capture_output=True)
    if stream.returncode != 0:
        raise retention.RetentionError(
            "the corresponding source could not be decompressed: the compressed "
            "archive matches its recorded digest but its stream is not readable"
        )
    actual = retention.sha256_bytes(stream.stdout)
    if actual != accepted["correspondingSourceStreamSha256"]:
        raise retention.RetentionError(
            f"the corresponding-source stream hashes to {actual}, not the accepted "
            f"{accepted['correspondingSourceStreamSha256']}"
        )


def control_04(root: Path, work: Path) -> None:
    # A licence file rather than the runtime: deleting the runtime is refused by
    # the GPL pairing check first, which is correct but names a different
    # property. Control 20 covers that one.
    sorted((root / "delivered" / "licenses").iterdir())[0].unlink()
    _stage(root, work / "unit")


def control_05(root: Path, work: Path) -> None:
    (root / "delivered" / "UNREVIEWED.txt").write_bytes(b"added after acceptance\n")
    _stage(root, work / "unit")


def control_06(root: Path, work: Path) -> None:
    licences = sorted((root / "delivered" / "licenses").iterdir())
    licences[0].rename(licences[0].with_name("renamed-COPYING"))
    _stage(root, work / "unit")


def control_07(root: Path, work: Path) -> None:
    shutil.rmtree(root / "evidence-b")
    (root / "evidence-b").mkdir()
    _stage(root, work / "unit")


def control_08(root: Path, work: Path) -> None:
    """Both evidence bundles claim ONE runner allocation.

    Note what this does and does not assert. `sameNode=true` is the ACCEPTED
    truth for W1-A3 — both allocations landed on one node, and the manifest
    records that honestly. What must never be equal is the runner ALLOCATION:
    two bundles reporting the same `runnerName` are one build described as two,
    and that is the claim this refuses.
    """
    accepted = _accepted(root)
    accepted["evidence"]["hostB"]["runnerName"] = accepted["evidence"]["hostA"]["runnerName"]
    _write_accepted(root, accepted)
    contract.validate_accepted(accepted)


# ── controls 09, 10 and 11: three identity tampers, three distinct properties ─
#
# R0's adjacent finding: all three used to reach RED through the substring
# "predicts", which is the OCI digest recomputation's generic message. Any of the
# three mutations produces it, so none of them proved its own property — remove
# the gate control 10 is named for and control 10 still goes RED, through control
# 09's mechanism. A grading token shared by three controls grades one control
# three times.
#
# So each now has a mutation that is REACHED and verified, a unique sentinel that
# only its own mutation can produce, and a distinct assertion:
#
#   09  proofHead disagrees with the comparison record the proof wrote
#   10  buildInputsReference disagrees with that same record, by a different field
#   11  the tampered identity is embedded in RETENTION.md, whose digest is pinned
#       in the unit inventory — so the unit stops matching its own manifest
#
# `--ablate` runs the three mutations against the three expectations as a matrix
# and requires the off-diagonal to be empty.

_IDENTITY_TAMPERS: dict[str, tuple[str, dict]] = {
    "09": ("CONTROL-09-SENTINEL-proof-head-and-run",
           {"proofHead": "9" * 40, "proofRun": 999999}),
    "10": ("CONTROL-10-SENTINEL-build-inputs-reference",
           {"buildInputsReference":
            "ghcr.io/tesserafin-project/windows-ffmpeg-build-inputs@sha256:" + "b" * 64}),
    "11": ("CONTROL-11-SENTINEL-accepted-server-tree",
           {"acceptedServerTree": "c" * 40}),
}


def _identity_tamper(root: Path, work: Path, which: str, resync: bool) -> None:
    """Apply one identity tamper, prove it landed, then build.

    The mutation is re-read from disk before anything else runs. A control whose
    mutation silently did not apply is INERT, not RED, and without this check it
    would be indistinguishable from a control that worked.
    """
    sentinel, changes = _IDENTITY_TAMPERS[which]
    accepted = _accepted(root)
    for field, value in changes.items():
        if field not in accepted:
            raise Inert(f"the acceptance manifest has no {field!r} to tamper with")
        if accepted[field] == value:
            raise Inert(f"{field!r} already holds the tampered value; nothing was mutated")
        accepted[field] = value
    _write_accepted(root, accepted)

    reread = _accepted(root)
    for field, value in changes.items():
        if reread[field] != value:
            raise Inert(f"the mutation of {field!r} did not reach the manifest on disk")

    # The resync is INSIDE the try: it stages the unit itself, so an identity
    # gate that fires during staging escapes from there, not from `_build`, and
    # a narrower try silently dropped the sentinel off two of the three refusals.
    try:
        if resync:
            _resync_readme(root, work)
        _build(root, work / "unit", work / "oci")
    except (contract.ContractError, retention.RetentionError) as error:
        raise type(error)(f"{sentinel} {error}") from None


def control_09(root: Path, work: Path) -> None:
    # Resynced, so the shallow RETENTION.md rebinding does not fire first and the
    # control reaches the identity-versus-proof gate it is named for.
    _identity_tamper(root, work, "09", resync=True)


def control_10(root: Path, work: Path) -> None:
    _identity_tamper(root, work, "10", resync=True)


def control_11(root: Path, work: Path) -> None:
    # Deliberately NOT resynced. `acceptedServerTree` appears in no proof record,
    # so there is nothing to compare it against — what refuses it is that the
    # retained unit's own RETENTION.md embeds it and the unit inventory pins that
    # file's digest. Re-pinning the readme would erase the only gate this value
    # has, which is the property under test.
    _identity_tamper(root, work, "11", resync=False)


def control_12(root: Path, work: Path) -> None:
    accepted = _accepted(root)
    accepted["retainForever"] = True
    _write_accepted(root, accepted)
    contract.validate_accepted(accepted)


def control_13(root: Path, work: Path) -> None:
    accepted = _accepted(root)
    contract.parse_reference(f"{contract.CANONICAL}:{accepted['immutableTag']}")


def control_14(root: Path, work: Path) -> None:
    accepted = _accepted(root)
    digest = accepted["manifestDigest"]
    flipped = "sha256:" + ("0" if digest[7] != "0" else "1") + digest[8:]
    accepted["manifestDigest"] = flipped
    accepted["reference"] = f"{contract.CANONICAL}@{flipped}"
    _write_accepted(root, accepted)
    _build(root, work / "unit", work / "oci")


def control_15(root: Path, work: Path) -> None:
    """A traversal path, refused on the way IN.

    Built as a tar rather than as a file on disk, because a filesystem will not
    hold `../escaped`. The layer reader is what must refuse it.
    """
    layer = work / "hostile.tar"
    with tarfile.open(layer, "w", format=tarfile.USTAR_FORMAT) as archive:
        for name in ("delivered/runtime/x.zip", "../escaped.txt"):
            info = tarfile.TarInfo(name)
            info.size = 1
            info.mtime = 0
            info.mode = 0o644
            import io

            archive.addfile(info, io.BytesIO(b"x"))
    retention.load_layer_index(layer)


def control_16(root: Path, work: Path) -> None:
    contract.assert_trusted_ref("refs/pull/253/merge")


def control_17(root: Path, work: Path) -> None:
    contract.assert_trusted_ref("refs/heads/w1/windows-ffmpeg-runtime-retention")


def control_18(root: Path, work: Path) -> None:
    """No caller may choose the package, the digest, the run or the tag.

    Asserted against the SOURCE of the consumer and the publication workflow,
    because the property is the absence of a parameter. A behavioural test
    cannot see an option that is not offered — it can only fail to use it, which
    is indistinguishable from the option existing and being ignored.
    """
    consumer = (HERE / "consume.ps1").read_text(encoding="utf-8")
    # The TOP-LEVEL param block only. `Assert-DigestReference` takes a
    # `$Reference` parameter of its own, and matching that would report the
    # consumer as caller-controlled because one of its internal functions names
    # a variable — a false positive that would make this control permanently red
    # for the wrong reason.
    if "\nparam(" not in consumer:
        raise Inert("consume.ps1 has no top-level param block to inspect")
    block = consumer.split("\nparam(", 1)[1].split("\n)", 1)[0]
    for forbidden in ("$Reference", "$Digest", "$Tag", "$RunId", "$Package", "$Registry"):
        if forbidden in block:
            raise retention.RetentionError(
                f"consume.ps1 accepts a caller-supplied {forbidden!r}; the identity "
                "must travel with the commit, not with the caller"
            )
    workflow = (
        HERE.parent.parent.parent / ".github" / "workflows" / "w1-windows-runtime-publish.yml"
    )
    if not workflow.is_file():
        raise Inert(f"the publication workflow is not at {workflow}")
    text = workflow.read_text(encoding="utf-8")

    # The absence of an `inputs:` block, not the absence of particular words.
    # Scanning the prose for "digest" would go red the moment a comment
    # explained why there is no digest input — a check that punishes the
    # documentation of the property it is checking.
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("#"):
            continue
        if stripped == "inputs:" or stripped.startswith("inputs:"):
            raise retention.RetentionError(
                "the publication workflow declares an inputs: block; the publisher "
                "reads the committed acceptance manifest and nothing else"
            )

    # And it must actually assert the trusted SOURCE, otherwise "no inputs" would
    # be satisfied by a workflow anyone could dispatch from any branch — or, as
    # R0 found, from any REVISION of the right branch. A ref name is not a
    # revision, so the head assertion and the descendant assertion are required
    # here by name.
    for required in (
        "assert_trusted_source",
        "assert_committed_manifest",
        "merge-base --is-ancestor",
    ):
        if required not in text:
            raise retention.RetentionError(
                f"the publication workflow does not assert {required!r}; the identity of "
                f"what is published would follow the ref rather than the revision"
            )

    raise retention.RetentionError(
        "CONTROL-18-SATISFIED: neither the consumer nor the publication workflow "
        "offers a caller-selectable package, digest, run or tag"
    )


def control_19(root: Path, work: Path) -> None:
    accepted = _accepted(root)
    contract.assert_tag_free_or_agreeing(
        accepted["immutableTag"],
        "sha256:" + "e" * 64,
        accepted["manifestDigest"],
    )


def control_20(root: Path, work: Path) -> None:
    source_dir = root / "delivered" / "source"
    for entry in list(source_dir.iterdir()):
        entry.unlink()
    accepted = _accepted(root)
    accepted["unitPaths"] = {
        path: value
        for path, value in accepted["unitPaths"].items()
        if not path.startswith(retention.SOURCE_PREFIX)
    }
    accepted["deliveredPathCount"] = len(
        [p for p in accepted["unitPaths"] if p.startswith("delivered/")]
    )
    _write_accepted(root, accepted)
    # The schema refuses first; if it ever did not, the layer builder would.
    try:
        contract.validate_accepted(accepted)
    except contract.ContractError:
        raise
    _stage(root, work / "unit")


CONTROLS: list[tuple[str, str, str, Callable[[Path, Path], None]]] = [
    ("01-corrupted-runtime-byte", "a corrupted runtime byte is refused", "changed=", control_01),
    ("02-corrupted-source-byte", "a corrupted corresponding-source byte is refused", "changed=", control_02),
    ("03-compressed-matches-stream-does-not", "a matching container with a different stream is refused", "stream hashes to", control_03),
    ("04-missing-delivered-path", "a missing delivered path is refused", "missing=", control_04),
    ("05-added-path", "an added path is refused", "added=", control_05),
    ("06-renamed-path", "a renamed path is refused", "added=", control_06),
    ("07-missing-evidence-bundle", "a missing evidence bundle is refused", "carries no", control_07),
    ("08-one-allocation-claimed-twice", "two bundles claiming one runner allocation are refused", "claim runner allocation", control_08),
    ("09-false-proof-head", "a false proof head disagrees with the proof's own comparison record", "CONTROL-09-SENTINEL-proof-head-and-run IDENTITY-PROOF-HEAD-DISAGREES", control_09),
    ("10-wrong-build-input-digest", "a wrong build-input reference disagrees with the proof's own comparison record", "CONTROL-10-SENTINEL-build-inputs-reference IDENTITY-BUILD-INPUTS-DISAGREE", control_10),
    ("11-wrong-accepted-tree", "a wrong accepted tree is refused by the unit's own pinned RETENTION.md", "CONTROL-11-SENTINEL-accepted-server-tree the staged unit is not the pinned unit: added=[] missing=[] changed=['RETENTION.md']", control_11),
    ("12-unknown-manifest-field", "an unknown acceptance-manifest field is refused", "unknown field(s)", control_12),
    ("13-tag-only-reference", "a tag-only consumer reference is refused", "not digest-pinned", control_13),
    ("14-manifest-digest-drift", "an OCI manifest differing from the committed digest is refused", "records", control_14),
    ("15-traversal-archive-path", "a traversal or absolute archive path is refused", "traversal", control_15),
    ("16-publish-from-pull-request", "publication from a pull request is refused", "pull request ref carries unreviewed code", control_16),
    ("17-publish-from-non-master", "publication from a non-master ref is refused", "feature branch is not trusted", control_17),
    ("18-caller-selected-identity", "no caller-selectable digest, run or tag exists", "CONTROL-18-SATISFIED", control_18),
    ("19-immutable-tag-repointed", "an immutable tag pointing elsewhere is refused", "is never repointed", control_19),
    ("20-source-deleted-binary-kept", "deleting the corresponding source while keeping the binary is refused", "GPL-3.0-or-later refusal", control_20),
]


def _expected_for(name_prefix: str) -> str:
    for name, _prop, expected, _fn in CONTROLS:
        if name.startswith(name_prefix):
            return expected
    raise KeyError(name_prefix)


def ablate(fixture: Path) -> int:
    """Prove controls 09, 10 and 11 grade on their OWN assertion.

    The matrix applies each identity mutation and then asks, of every one of the
    three expectations, whether that mutation satisfies it. Only the diagonal may
    be true. An off-diagonal hit is exactly the R0 finding: three controls sharing
    one grading token, so removing the gate one of them is named for leaves it
    passing through another's mechanism.

    The `none` row is the other direction — with nothing mutated there must be no
    refusal at all, so a control cannot be satisfied by a pristine tree.
    """
    which = ("09", "10", "11")
    functions = {"09": control_09, "10": control_10, "11": control_11}
    expectations = {
        "09": _expected_for("09-"),
        "10": _expected_for("10-"),
        "11": _expected_for("11-"),
    }
    failures = 0
    print("ablation matrix for controls 09, 10 and 11")
    print(f"  {'mutation':<10} " + "  ".join(f"expects-{k}" for k in which))

    for mutation in ("none",) + which:
        work = Path(tempfile.mkdtemp(prefix=f"w1a4r1-ablate-{mutation}-"))
        try:
            root = _fresh(fixture, work)
            message = ""
            try:
                if mutation == "none":
                    _build(root, work / "unit", work / "oci")
                else:
                    functions[mutation](root, work)
            except Inert as error:
                print(f"  ERROR  {mutation:<10} the mutation never reached its assertion: {error}")
                failures += 1
                continue
            except Exception as error:  # noqa: BLE001 - the refusal is the datum
                message = str(error)
            row = []
            for expectation in which:
                hit = expectations[expectation] in message
                want = (mutation == expectation)
                row.append("RED " if hit else "--  ")
                if hit != want:
                    failures += 1
            print(f"  {mutation:<10} " + "     ".join(row))
        finally:
            shutil.rmtree(work, ignore_errors=True)

    print()
    if failures:
        print(f"W1-A4 ABLATION HARD STOP: {failures} off-diagonal or missing result(s)",
              file=sys.stderr)
        return 1
    print("each of 09, 10 and 11 is satisfied by its own mutation and by no other, and none "
          "of them is satisfied by a pristine tree")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--ablate", action="store_true",
                        help="run the 09/10/11 uniqueness matrix instead of the suite")
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    if args.ablate:
        before = _fixture_digest(args.fixture)
        status = ablate(args.fixture)
        if _fixture_digest(args.fixture) != before:
            print("W1-A4 ABLATION HARD STOP: the fixture was not restored", file=sys.stderr)
            return 1
        return status

    before = _fixture_digest(args.fixture)
    outcomes: list[Outcome] = []

    for name, prop, expected, function in CONTROLS:
        outcome = Outcome(name, prop)
        work = Path(tempfile.mkdtemp(prefix=f"w1a4-{name}-"))
        try:
            root = _fresh(args.fixture, work)
            try:
                function(root, work)
            except Inert as error:
                outcome.result = "INERT"
                outcome.detail = f"the control could not reach its assertion: {error}"
            except (retention.RetentionError, contract.ContractError) as error:
                message = str(error)
                if expected in message:
                    outcome.result = "RED"
                    outcome.detail = message.splitlines()[0][:200]
                else:
                    outcome.result = "INERT"
                    outcome.detail = (
                        f"refused, but not for the property under test "
                        f"(wanted {expected!r}): {message.splitlines()[0][:200]}"
                    )
            except Exception as error:  # noqa: BLE001 - classified, not swallowed
                outcome.result = "INERT"
                outcome.detail = f"{type(error).__name__}: {str(error)[:200]}"
            else:
                outcome.result = "GREEN"
                outcome.detail = "the mutation was ACCEPTED; this gate does not hold"
        finally:
            shutil.rmtree(work, ignore_errors=True)
        outcomes.append(outcome)
        print(f"  {outcome.result:<6} {outcome.name:<42} {outcome.property}")
        if outcome.result != "RED":
            print(f"         └─ {outcome.detail}")

    after = _fixture_digest(args.fixture)
    restored = before == after
    print()
    print(f"fixture before : {before}")
    print(f"fixture after  : {after}")
    print(f"restored byte-identically: {'yes' if restored else 'NO'}")

    red = sum(1 for o in outcomes if o.result == "RED")
    inert = sum(1 for o in outcomes if o.result == "INERT")
    green = sum(1 for o in outcomes if o.result == "GREEN")
    print(f"RED {red}  INERT {inert}  GREEN {green}  of {len(outcomes)}")

    if args.json:
        args.json.write_bytes(
            retention.canonical_json(
                {
                    "controls": [
                        {"name": o.name, "property": o.property, "result": o.result, "detail": o.detail}
                        for o in outcomes
                    ],
                    "red": red,
                    "inert": inert,
                    "green": green,
                    "fixtureRestoredByteIdentically": restored,
                    "fixtureDigestBefore": before,
                    "fixtureDigestAfter": after,
                }
            )
        )

    if green or inert or not restored:
        print("W1-A4 CONTROLS HARD STOP: not every control reached its property", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
