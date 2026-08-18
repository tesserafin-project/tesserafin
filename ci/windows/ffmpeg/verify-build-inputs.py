#!/usr/bin/env python3
"""Refuse build inputs that are not the ones W1-R accepted (W1-A2, #236).

The W1-R consumer already refuses a tag, a wrong manifest digest, a wrong layer
digest and an unsigned package. This is the second, independent statement of the
same contract, made against the EVIDENCE the consumer produced rather than
inside it — so a consumer that was pointed at a different package, or that was
edited to accept one, is caught by a file the pull request reviews on its own.

Usage:
    verify-build-inputs.py --consume-evidence consume.json
    verify-build-inputs.py --reference <ref>      # reference-shape check only
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ACCEPTED = json.loads((HERE / "accepted-build-inputs.json").read_text())

REFERENCE_RE = re.compile(r"^(?P<name>[^@:]+)@(?P<digest>sha256:[0-9a-f]{64})$")


class Refusal(Exception):
    pass


def check_reference(reference: str) -> None:
    m = REFERENCE_RE.match(reference)
    if not m:
        raise Refusal(
            f"'{reference}' is not digest-pinned. A tag is never an accepted "
            f"identity; use {ACCEPTED['package']}@sha256:<digest>")
    name = m.group("name")
    expected_name = f"{ACCEPTED['registry']}/{ACCEPTED['package']}"
    if name != expected_name:
        raise Refusal(f"'{name}' is not the authorised package; W1-A2 accepts "
                      f"exactly one: {expected_name}")
    if m.group("digest") != ACCEPTED["manifestDigest"]:
        raise Refusal(f"manifest digest {m.group('digest')} is not the accepted "
                      f"{ACCEPTED['manifestDigest']}")


def check_evidence(evidence: dict) -> None:
    check_reference(evidence.get("reference", ""))

    for field, accepted_key in (("manifestDigest", "manifestDigest"),
                                ("layerDigest", "layerDigest"),
                                ("lockSha256", "lockSha256"),
                                ("trustRootSha256", "trustRootSha256")):
        got = evidence.get(field)
        want = ACCEPTED[accepted_key]
        if got != want:
            raise Refusal(f"{field} is {got}, the accepted value is {want}")

    if evidence.get("packageCount") != ACCEPTED["packageCount"]:
        raise Refusal(f"the lock declares {evidence.get('packageCount')} packages, "
                      f"W1-R accepted {ACCEPTED['packageCount']}")
    if evidence.get("installedPackages") != ACCEPTED["packageCount"]:
        raise Refusal(f"{evidence.get('installedPackages')} packages were installed, "
                      f"the lock declares {ACCEPTED['packageCount']}")
    if evidence.get("signaturesVerified") != ACCEPTED["signaturesRequired"]:
        raise Refusal(f"{evidence.get('signaturesVerified')} signatures verified, "
                      f"{ACCEPTED['signaturesRequired']} required")

    fingerprints = {f.replace(" ", "").upper()
                    for f in evidence.get("acceptedFingerprints", [])}
    if ACCEPTED["acceptedSigner"] not in fingerprints:
        raise Refusal(f"the accepted signer {ACCEPTED['acceptedSigner']} is not "
                      f"among the verified fingerprints {sorted(fingerprints)}")

    for field, want, message in (
        ("installedSetEqualsLock", True,
         "the installed set is not exactly the locked set"),
        ("upstreamConsulted", False, "upstream was consulted"),
        ("tagUsed", False, "a tag was used somewhere in the acquisition"),
    ):
        if evidence.get(field) is not want:
            raise Refusal(f"{message} ({field}={evidence.get(field)!r})")

    # `mirrorsEmptied` is the LIST of mirrorlist files install-locked.ps1
    # emptied, not a flag — it reports `["mirrorlist.mingw", "mirrorlist.msys"]`.
    # The names are not hardcoded here: that script empties every `mirrorlist*`
    # it finds and hard-stops when it finds none, so the property that matters is
    # that the list is non-empty. Naming the two files MSYS2 ships today would
    # turn a future third mirrorlist into a passing check that emptied only two.
    mirrors = evidence.get("mirrorsEmptied")
    if not isinstance(mirrors, (list, bool)) or mirrors is False or not mirrors:
        raise Refusal("the mirrors were not emptied, so live pacman resolution "
                      f"was still possible (mirrorsEmptied={mirrors!r})")

    if "pacman -U" not in str(evidence.get("pacmanMode", "")):
        raise Refusal(f"pacmanMode is {evidence.get('pacmanMode')!r}; only "
                      "`pacman -U` over local files is accepted")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--consume-evidence")
    ap.add_argument("--reference")
    args = ap.parse_args(argv)

    try:
        if args.reference:
            check_reference(args.reference)
            print(f"accepted reference {args.reference}")
        if args.consume_evidence:
            check_evidence(json.loads(Path(args.consume_evidence).read_text()))
            print(f"accepted build inputs: {ACCEPTED['packageCount']} packages, "
                  f"{ACCEPTED['signaturesRequired']} signatures, "
                  f"layer {ACCEPTED['layerDigest'][:23]}…")
        if not args.reference and not args.consume_evidence:
            ap.error("nothing to check")
    except Refusal as exc:
        print(f"W1-A2 BUILD-INPUT HARD STOP: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
