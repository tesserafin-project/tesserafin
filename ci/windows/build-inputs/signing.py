"""Cryptographically authenticate MSYS2 package signatures (#236, W1-R-B §2.2).

Presence of a `.sig` file, and a SHA-256 of it, prove nothing about who signed
the archive. This module verifies the detached OpenPGP signature itself, against
a committed root of trust, and refuses everything else.

THE THREE PROPERTIES THAT MAKE THIS FAIL CLOSED
-----------------------------------------------
1. **Hermetic.** GnuPG runs in a throwaway home directory created here, with
   `--no-default-keyring`, no keyserver, no auto key location and no dirmngr.
   `GNUPGHOME` and every other inherited GnuPG variable is stripped from the
   child environment, so a key that happens to sit in the runner's ambient
   keyring cannot make a signature verify.
2. **Byte-pinned.** The trust root is a committed armoured key file whose
   SHA-256 is recorded in `trust-root.json` and checked before import. After
   import, the fingerprints GnuPG actually holds must equal the recorded
   allowlist EXACTLY — not be a superset of it — so neither editing the key
   bytes nor editing the allowlist can widen what is accepted.
3. **Positively asserted.** A signature is accepted only on a `VALIDSIG` status
   line whose fingerprint is in the allowlist. Everything else is a refusal:
   `BADSIG`, `ERRSIG`, `EXPSIG`, `EXPKEYSIG`, `REVKEYSIG`, `NO_PUBKEY`, a
   missing signature file, a signature GnuPG cannot parse, and a zero exit code
   with no `VALIDSIG` at all.

WHAT IT DOES NOT DECIDE
-----------------------
The committed lock remains the integrity decision: an archive is admitted only
if its SHA-256 equals the reviewed value. Signature verification is the
ATTRIBUTION decision — it proves those exact bytes came from an MSYS2 packager
we accept. A valid signature never admits an archive whose digest disagrees with
the lock, and a matching digest never admits an archive whose signature does not
verify. `ingest.py` runs this BEFORE writing anything into the bundle.

Usage:
    python3 signing.py --bundle <bundle-root> [--trust <trust dir>]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Dict, List, Optional

HERE = Path(__file__).resolve().parent
DEFAULT_TRUST = HERE / "trust"

# Every status line that means "do not accept this archive". Listed rather than
# inferred from the exit code: gpg exits 0 for an expired-key signature.
REFUSAL_STATUS = (
    "BADSIG",
    "ERRSIG",
    "EXPSIG",
    "EXPKEYSIG",
    "REVKEYSIG",
    "NO_PUBKEY",
    "NODATA",
    "UNEXPECTED",
)

# Hosted Windows runners carry Git for Windows' gpg even before MSYS2's own
# `gnupg` package is installed, and installing it is exactly what this gate
# guards. Searched in order; the first that runs wins.
GPG_CANDIDATES = (
    "gpg",
    "gpg2",
    r"C:\Program Files\Git\usr\bin\gpg.exe",
    r"C:\msys64\usr\bin\gpg.exe",
)


class SignatureError(Exception):
    """Fail-closed condition. Never caught to continue."""


def find_gpg() -> str:
    for candidate in GPG_CANDIDATES:
        resolved = shutil.which(candidate) or (
            candidate if Path(candidate).is_file() else None
        )
        if resolved:
            return resolved
    raise SignatureError(
        "no gpg executable found. Signature verification cannot be skipped: "
        "refusing to admit archives that nothing authenticated."
    )


class PathNamespace:
    """Translate this interpreter's paths into the ones the chosen gpg reads.

    On a hosted Windows runner the gpg that exists before MSYS2's own `gnupg`
    package is installed is Git for Windows', which is an MSYS binary: it reads
    `C:\\Users\\RUNNER~1\\...` as a RELATIVE POSIX path and silently prefixes the
    working directory, so `--homedir` lands somewhere that does not exist. The
    interpreter running this file, meanwhile, is native Windows Python and hands
    out exactly those paths. Every path argument therefore crosses a namespace
    boundary and every one of them has to be translated -- `--homedir`, the key
    file, the signature and the archive alike. Fixing only the first turns the
    next failure into a missing `VALIDSIG`, which reads like a signature finding
    rather than a path bug.

    The translator is `cygpath` from the SAME `usr/bin` as the selected gpg, so
    it shares that gpg's mount table. When there is no such sibling the gpg is
    native and paths pass through unchanged.
    """

    def __init__(self, gpg: str):
        sibling = Path(gpg).with_name("cygpath.exe")
        self.cygpath: Optional[Path] = sibling if sibling.is_file() else None
        # Reported in the summary. A `null` translator next to an MSYS gpg is the
        # signature of this bug returning under a different disguise, so it is
        # evidence rather than an internal detail.
        self.description = str(self.cygpath) if self.cygpath else "none (native paths)"
        # Directories, not files: four lookups instead of one per archive.
        self._directories: Dict[str, str] = {}

    def __call__(self, path: Path) -> str:
        if self.cygpath is None:
            return str(path)
        path = Path(path)
        parent = str(path.parent)
        if parent not in self._directories:
            result = subprocess.run(
                [str(self.cygpath), "-u", parent],
                capture_output=True,
                text=True,
                check=False,
            )
            if result.returncode != 0:
                raise SignatureError(
                    f"cygpath could not translate {parent!r} into the namespace "
                    f"of {self.cygpath}: {result.stderr.strip()}"
                )
            translated = result.stdout.strip()
            if not translated:
                raise SignatureError(f"cygpath returned nothing for {parent!r}")
            self._directories[parent] = translated.rstrip("/")
        return f"{self._directories[parent]}/{path.name}"


def load_trust_root(trust_dir: Path) -> dict:
    """Read and validate `trust-root.json` and the key bytes it pins."""
    root_path = trust_dir / "trust-root.json"
    if not root_path.is_file():
        raise SignatureError(f"no trust root at {root_path}")
    root = json.loads(root_path.read_text(encoding="utf-8"))

    fingerprints = root.get("acceptedFingerprints") or []
    if not fingerprints:
        raise SignatureError(
            "the trust root accepts no fingerprints; that would refuse every "
            "signature, which is not the same as verifying one"
        )
    for fingerprint in fingerprints:
        if len(fingerprint) != 40 or any(c not in "0123456789ABCDEF" for c in fingerprint):
            raise SignatureError(f"malformed fingerprint in the trust root: {fingerprint!r}")
    if len(set(fingerprints)) != len(fingerprints):
        raise SignatureError("the trust root lists a fingerprint twice")

    key_path = trust_dir / root["keyFile"]
    if not key_path.is_file():
        raise SignatureError(f"the trust root names {root['keyFile']}, which is absent")
    key_bytes = key_path.read_bytes()
    actual = hashlib.sha256(key_bytes).hexdigest()
    if actual != root["keyFileSha256"]:
        raise SignatureError(
            f"{root['keyFile']} hashes to {actual}, the trust root records "
            f"{root['keyFileSha256']}. The signing material has been altered."
        )
    if len(key_bytes) != root["keyFileBytes"]:
        raise SignatureError(
            f"{root['keyFile']} is {len(key_bytes)} bytes, the trust root records "
            f"{root['keyFileBytes']}"
        )
    root["_keyPath"] = key_path
    root["_sha256"] = actual
    return root


class Verifier:
    """A hermetic GnuPG home holding exactly the accepted keys."""

    def __init__(self, trust_dir: Path = DEFAULT_TRUST):
        self.root = load_trust_root(Path(trust_dir))
        self.accepted = set(self.root["acceptedFingerprints"])
        self.gpg = find_gpg()
        self.native_path = PathNamespace(self.gpg)
        self.home: Optional[Path] = None

    def __enter__(self) -> "Verifier":
        self.home = Path(tempfile.mkdtemp(prefix="w1r-gnupg-"))
        os.chmod(self.home, 0o700)
        self._run(["--import", self.native_path(self.root["_keyPath"])])

        held = self._imported_fingerprints()
        if held != self.accepted:
            raise SignatureError(
                "the keys GnuPG actually holds do not equal the accepted "
                f"allowlist: holds {sorted(held)}, accepts {sorted(self.accepted)}. "
                "Either the key file or the fingerprint list was altered."
            )
        return self

    def __exit__(self, *_) -> None:
        if self.home is not None:
            shutil.rmtree(self.home, ignore_errors=True)
            self.home = None

    # ── the hermetic invocation ────────────────────────────────────────────
    def _run(self, arguments: List[str]) -> subprocess.CompletedProcess:
        # A clean environment, not the inherited one: GNUPGHOME, GPG_AGENT_INFO
        # and friends are exactly how an ambient keyring would get consulted.
        environment = {
            key: value
            for key, value in os.environ.items()
            if not key.startswith("GNUPG") and not key.startswith("GPG")
        }
        environment["LC_ALL"] = "C"
        command = [
            self.gpg,
            "--homedir",
            self.native_path(self.home),
            "--no-default-keyring",
            "--no-options",
            "--batch",
            "--no-tty",
            "--quiet",
            "--trust-model",
            "always",
            "--disable-dirmngr",
            "--keyserver-options",
            "no-auto-key-retrieve",
            "--no-auto-key-locate",
            *arguments,
        ]
        return subprocess.run(
            command, capture_output=True, text=True, check=False, env=environment
        )

    def _imported_fingerprints(self) -> set:
        result = self._run(["--list-keys", "--with-colons"])
        if result.returncode != 0:
            raise SignatureError(f"gpg could not list the imported keys: {result.stderr}")
        fingerprints = set()
        primary = False
        for line in result.stdout.splitlines():
            fields = line.split(":")
            if fields[0] == "pub":
                primary = True
            elif fields[0] == "fpr" and primary:
                fingerprints.add(fields[9])
                primary = False
        return fingerprints

    # ── the decision ───────────────────────────────────────────────────────
    def verify(self, archive: Path, signature: Path) -> str:
        """Return the accepted signer fingerprint, or raise."""
        if not signature.is_file():
            raise SignatureError(
                f"{archive.name}: no detached signature. MSYS2 signs every package; "
                "an unsigned archive is not admitted."
            )
        result = self._run(
            [
                "--status-fd",
                "1",
                "--verify",
                self.native_path(signature),
                self.native_path(archive),
            ]
        )
        status = [
            line[len("[GNUPG:] ") :]
            for line in result.stdout.splitlines()
            if line.startswith("[GNUPG:] ")
        ]

        for line in status:
            keyword = line.split(" ", 1)[0]
            if keyword in REFUSAL_STATUS:
                raise SignatureError(f"{archive.name}: gpg reported {line.strip()}")

        valid = [line.split() for line in status if line.startswith("VALIDSIG ")]
        if not valid:
            raise SignatureError(
                f"{archive.name}: gpg produced no VALIDSIG "
                f"(exit {result.returncode}); {result.stderr.strip() or 'no detail'}"
            )
        if len(valid) != 1:
            raise SignatureError(
                f"{archive.name}: {len(valid)} signatures; exactly one is expected"
            )
        fingerprint = valid[0][1]
        if fingerprint not in self.accepted:
            raise SignatureError(
                f"{archive.name}: signed by {fingerprint}, which is not an accepted "
                "MSYS2 signing key. A valid signature from an unaccepted key is "
                "still a refusal."
            )
        if result.returncode != 0:
            raise SignatureError(
                f"{archive.name}: VALIDSIG {fingerprint} but gpg exited "
                f"{result.returncode}: {result.stderr.strip()}"
            )
        return fingerprint


def verify_bundle(bundle_root: Path, trust_dir: Path = DEFAULT_TRUST) -> dict:
    """Verify every archive named by the bundle's lock. Returns a summary."""
    lock = json.loads((bundle_root / "msys2-lock.json").read_text(encoding="utf-8"))
    signers: Dict[str, int] = {}
    with Verifier(trust_dir) as verifier:
        for package in lock["packages"]:
            filename = package["filename"]
            fingerprint = verifier.verify(
                bundle_root / "packages" / filename,
                bundle_root / "signatures" / (filename + ".sig"),
            )
            signers[fingerprint] = signers.get(fingerprint, 0) + 1
        summary = {
            "verified": sum(signers.values()),
            "packageCount": lock["packageCount"],
            "signers": dict(sorted(signers.items())),
            "trustRootSha256": verifier.root["_sha256"],
            "acceptedFingerprints": sorted(verifier.accepted),
            "gpg": verifier.gpg,
            "pathTranslator": verifier.native_path.description,
        }
    if summary["verified"] != lock["packageCount"]:
        raise SignatureError(
            f"verified {summary['verified']} signatures for "
            f"{lock['packageCount']} locked packages"
        )
    return summary


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", required=True, type=Path)
    parser.add_argument("--trust", type=Path, default=DEFAULT_TRUST)
    args = parser.parse_args()
    summary = verify_bundle(args.bundle, args.trust)
    print(json.dumps(summary, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except SignatureError as error:
        print(f"W1-R SIGNATURE HARD STOP: {error}", file=sys.stderr)
        sys.exit(1)
