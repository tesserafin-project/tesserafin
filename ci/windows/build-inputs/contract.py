"""The consumption and publication contract, as testable functions (#236, W1-R).

These rules are the security boundary, so they live in one place and are unit
tested rather than being spelled out in shell in two workflows. A rule that is
only expressed as a workflow `if:` is untestable and, worse, a job-level `if:`
that evaluates false reports SUCCESS — which is why `assert_trusted_ref` is
called from inside the publisher's own steps as well.
"""

from __future__ import annotations

import re

REGISTRY = "ghcr.io"
REPOSITORY = "tesserafin-project/windows-ffmpeg-build-inputs"
CANONICAL = f"{REGISTRY}/{REPOSITORY}"
TRUSTED_REF = "refs/heads/master"

_DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")


class ContractError(Exception):
    """Fail-closed condition. Never caught to continue."""


def parse_reference(reference: str) -> str:
    """Validate a digest-pinned reference and return its digest.

    A tag is refused rather than resolved. The whole retention argument rests on
    the identity being the manifest digest: a consumer that accepts
    `…build-inputs:latest` has silently reintroduced a mutable input, and it
    would keep working right up until the day the tag moved.
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
            f"{name!r} is not the authorised package. W1-R authorises exactly one: "
            f"{CANONICAL}"
        )
    if not _DIGEST.match(digest):
        raise ContractError(f"{digest!r} is not a sha256 manifest digest")
    return digest


def assert_trusted_ref(github_ref: str) -> None:
    """Publication may only ever run from trusted `master`.

    Everything else is refused by name so the failure says what happened: a pull
    request merge ref, a feature branch, a tag and a detached SHA are all things
    a contributor can cause to exist, and none of them has been reviewed.
    """
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
