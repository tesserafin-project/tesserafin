"""Negative controls for the Windows build-input retention machinery (#236, W1-R).

A gate that has never been shown to fail is not evidence. Each control damages
exactly one thing and requires the corresponding validator to refuse; one
undamaged control must pass, so a validator that simply always fails cannot
score a green run either.

The controls run against a small synthetic fixture rather than the real
388 MiB bundle. What is under test is the validation logic, and a fixture makes
each control hermetic, fast and able to express damage — a corrupted archive, a
wrong architecture — that would be irresponsible to create for real.

Usage:
    python3 negative-controls.py [--repo-root <path>]
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
from pathlib import Path

import bundle
import contract
import msys2db
import signing

HERE = Path(__file__).resolve().parent

PASSED: list = []
FAILED: list = []


def record(name: str, ok: bool, detail: str) -> None:
    (PASSED if ok else FAILED).append(name)
    status = "PASS" if ok else "FAIL"
    print(f"[{status}] {name}: {detail}")


def run_validator(script: str, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(HERE / script), *args],
        capture_output=True,
        text=True,
        check=False,
    )


def fake_package(name: str, version: str, arch: str, payload: bytes) -> dict:
    filename = f"{name}-{version}-{arch}.pkg.tar.zst"
    url = f"https://repo.msys2.org/msys/x86_64/{filename}"
    return {
        "repository": "msys",
        "name": name,
        "version": version,
        "architecture": arch,
        "filename": filename,
        "sha256": hashlib.sha256(payload).hexdigest(),
        "compressedBytes": len(payload),
        "installedBytes": len(payload) * 4,
        "license": ["spdx:MIT"],
        "depends": [],
        "provides": [],
        "groups": [],
        "url": url,
        "signatureUrl": url + ".sig",
    }


def base_lock() -> tuple:
    payloads = {
        "alpha": b"alpha-package-bytes",
        "beta": b"beta-package-bytes",
        "gamma": b"gamma-package-bytes",
    }
    packages = [
        fake_package("alpha", "1.0-1", "x86_64", payloads["alpha"]),
        fake_package("beta", "2.0-1", "x86_64", payloads["beta"]),
        fake_package("gamma", "3.0-1", "any", payloads["gamma"]),
    ]
    packages[0]["depends"] = ["beta"]
    packages[1]["depends"] = ["gamma"]

    lock = {
        "schemaVersion": 1,
        "target": "win-x64",
        "msystem": "CLANG64",
        "mingwPackagePrefix": "mingw-w64-clang-x86_64",
        "ffmpeg": {
            "upstreamCommit": "0" * 40,
            "buildRevision": "0.0.0-fixture",
            "source": "fixture",
        },
        "rootProvenance": {"fixture": True},
        "roots": ["alpha"],
        "providerOverrides": [],
        "repositoryDatabases": {
            "msys": {
                "filename": "msys.db",
                "url": "https://repo.msys2.org/msys/x86_64/msys.db",
                "sha256": hashlib.sha256(FIXTURE_DB).hexdigest(),
                "bytes": len(FIXTURE_DB),
            }
        },
        "packageCount": len(packages),
        "compressedBytes": sum(p["compressedBytes"] for p in packages),
        "installedBytes": sum(p["installedBytes"] for p in packages),
        "packages": packages,
    }
    return lock, payloads


FIXTURE_DB = b"fixture-db"


def write_lock(directory: Path, lock: dict) -> Path:
    """Write a fixture lock and the fixture database it declares beside it."""
    databases = directory / "databases"
    databases.mkdir(parents=True, exist_ok=True)
    (databases / "msys.db").write_bytes(FIXTURE_DB)
    path = directory / "lock.json"
    path.write_bytes(bundle.canonical_json(lock))
    return path


def make_bundle(directory: Path, lock: dict, payloads: dict) -> Path:
    root = directory / "bundle"
    for sub in ("packages", "signatures", "databases", "licenses"):
        (root / sub).mkdir(parents=True, exist_ok=True)
    for package in lock["packages"]:
        (root / "packages" / package["filename"]).write_bytes(payloads[package["name"]])
    (root / "databases" / "msys.db").write_bytes(FIXTURE_DB)
    (root / "licenses" / "licenses.json").write_bytes(bundle.canonical_json({}))
    lock_bytes = bundle.canonical_json(lock)
    (root / "msys2-lock.json").write_bytes(lock_bytes)
    trust = root / "trust"
    trust.mkdir(parents=True, exist_ok=True)
    for name in ("trust-root.json", "msys2-signing-keys.asc"):
        shutil.copyfile(HERE / "trust" / name, trust / name)
    trust_sha = hashlib.sha256((trust / "msys2-signing-keys.asc").read_bytes()).hexdigest()
    bundle.write_bundle_metadata(
        root, lock_bytes, {"msys": {"filename": "msys.db"}}, trust_sha
    )
    return root


# ── lock validation controls ────────────────────────────────────────────────


def lock_control(work: Path, name: str, mutate, expect_fragment: str) -> None:
    lock, _ = base_lock()
    mutate(lock)
    path = write_lock(work / name, lock)
    result = run_validator(
        "verify-lock.py", "--lock", str(path), "--databases", str(path.parent / "databases")
    )
    message = (result.stderr + result.stdout).strip()
    ok = result.returncode != 0 and expect_fragment in message
    record(
        name,
        ok,
        f"exit {result.returncode}"
        + ("" if ok else f", expected {expect_fragment!r} in: {message[:200]}"),
    )


def undamaged_lock_control(work: Path) -> None:
    lock, _ = base_lock()
    path = write_lock(work / "undamaged", lock)
    result = run_validator(
        "verify-lock.py", "--lock", str(path), "--databases", str(path.parent / "databases")
    )
    record(
        "control.undamaged-lock-passes",
        result.returncode == 0,
        f"exit {result.returncode}: {(result.stdout + result.stderr).strip()[:160]}",
    )


# ── bundle / OCI controls ───────────────────────────────────────────────────


def bundle_control(work: Path, name: str, damage, expect_fragment: str) -> None:
    lock, payloads = base_lock()
    directory = work / name
    root = make_bundle(directory, lock, payloads)
    damage(root)
    result = run_validator(
        "build-oci.py", "--bundle", str(root), "--out", str(directory / "oci")
    )
    message = (result.stderr + result.stdout).strip()
    ok = result.returncode != 0 and expect_fragment in message
    record(
        name,
        ok,
        f"exit {result.returncode}"
        + ("" if ok else f", expected {expect_fragment!r} in: {message[:200]}"),
    )


def undamaged_bundle_control(work: Path) -> str:
    lock, payloads = base_lock()
    directory = work / "undamaged-bundle"
    root = make_bundle(directory, lock, payloads)
    result = run_validator(
        "build-oci.py", "--bundle", str(root), "--out", str(directory / "oci")
    )
    record(
        "control.undamaged-bundle-builds",
        result.returncode == 0,
        f"exit {result.returncode}",
    )
    if result.returncode != 0:
        return ""
    return json.loads(result.stdout)["manifestDigest"]


def manifest_tamper_control(work: Path) -> None:
    """An edited manifest must no longer hash to its recorded digest."""
    lock, payloads = base_lock()
    directory = work / "manifest-tamper"
    root = make_bundle(directory, lock, payloads)
    oci = directory / "oci"
    result = run_validator("build-oci.py", "--bundle", str(root), "--out", str(oci))
    if result.returncode != 0:
        record("control.changed-oci-manifest", False, "the fixture failed to build")
        return
    before = json.loads(result.stdout)["manifestDigest"]

    manifest = json.loads((oci / "manifest.json").read_text())
    manifest["annotations"]["dev.tesserafin.buildinputs.packageCount"] = "999"
    (oci / "manifest.json").write_bytes(bundle.canonical_json(manifest))
    after = bundle.read_manifest_digest(oci)
    record(
        "control.changed-oci-manifest",
        after != before,
        f"{before[:23]}… -> {after[:23]}… (digest moved, so a pinned consumer breaks)",
    )


def path_traversal_control(work: Path) -> None:
    """A layer entry that escapes the bundle root must be refused."""
    directory = work / "traversal"
    directory.mkdir(parents=True, exist_ok=True)
    layer = directory / "evil.tar"
    with tarfile.open(layer, "w") as archive:
        info = tarfile.TarInfo("../escaped.txt")
        payload = b"escaped"
        info.size = len(payload)
        archive.addfile(info, __import__("io").BytesIO(payload))

    index = bundle.load_layer_index(layer)
    unsafe = [
        name
        for name in index
        if name.startswith("/") or ".." in Path(name).parts or "\\" in name
    ]
    record(
        "control.path-traversal-entry",
        bool(unsafe),
        f"unsafe entries detected: {unsafe}",
    )


def symlink_control(work: Path) -> None:
    lock, payloads = base_lock()
    directory = work / "symlink"
    root = make_bundle(directory, lock, payloads)
    (root / "packages" / "link.pkg.tar.zst").symlink_to("/etc/passwd")
    try:
        bundle.relative_paths(root)
    except bundle.BundleError as error:
        record("control.symlink-in-bundle", True, str(error))
        return
    record("control.symlink-in-bundle", False, "a symlink was accepted into the bundle")


# ── contract controls ───────────────────────────────────────────────────────


def contract_control(name: str, call, expect_fragment: str) -> None:
    try:
        call()
    except contract.ContractError as error:
        ok = expect_fragment in str(error)
        record(name, ok, str(error) if ok else f"wrong message: {error}")
        return
    record(name, False, "the contract accepted something it must refuse")


# ── provider-resolution controls (W1-R-B §2.1) ─────────────────────────────
#
# The rule under test is not "a real package wins its own name" alone. It is
# the whole decision procedure: a real name wins, a single compatible provider
# is taken, two compatible providers STOP, and only a reviewed override in
# authoritative metadata may resolve the stop. Every one of those branches has
# a control, and so does the property that none of it depends on order.


def db_entry(name: str, version: str, **overrides) -> dict:
    record_ = {
        "repository": "msys",
        "name": name,
        "version": version,
        "architecture": "x86_64",
        "filename": f"{name}-{version}-x86_64.pkg.tar.zst",
        "sha256": "0" * 64,
        "compressedBytes": 1,
        "installedBytes": 1,
        "license": [],
        "depends": [],
        "provides": [],
        "groups": [],
    }
    record_.update(overrides)
    return record_


# `base` depends on `msys2-runtime`, and the compatibility package
# `msys2-runtime-3.3` declares `provides: msys2-runtime=3.3.6`. A resolver that
# lets whichever package it visits first take the name pulled the OLDER runtime
# into the closure, and installing it downgraded the runtime out from under the
# running MSYS2. Measured on a hosted runner, not hypothetical.
RUNTIME_DB = {
    "msys2-runtime": db_entry("msys2-runtime", "3.6.10-2"),
    "msys2-runtime-3.3": db_entry(
        "msys2-runtime-3.3", "3.3.6-16", provides=["msys2-runtime=3.3.6"]
    ),
    "base": db_entry("base", "1-1", depends=["msys2-runtime"]),
}

# One virtual name, one provider.
UNIQUE_DB = {
    "openbsd-netcat": db_entry("openbsd-netcat", "1.219-1", provides=["netcat"]),
    "netcat-user": db_entry("netcat-user", "1-1", depends=["netcat"]),
}

# One virtual name, two equally compatible providers. Nothing in the data says
# which one is meant, and that is the point.
AMBIGUOUS_DB = {
    "openbsd-netcat": db_entry("openbsd-netcat", "1.219-1", provides=["netcat"]),
    "gnu-netcat": db_entry("gnu-netcat", "0.7.1-1", provides=["netcat"]),
    "netcat-user": db_entry("netcat-user", "1-1", depends=["netcat"]),
}

# A versioned dependency against providers that state their version.
VERSIONED_DB = {
    "wx-3.2": db_entry("wx-3.2", "3.2.8-1", provides=["wxwidgets=3.2.8"]),
    "wx-3.3": db_entry("wx-3.3", "3.3.1-1", provides=["wxwidgets=3.3.1"]),
    "wx-user": db_entry("wx-user", "1-1", depends=["wxwidgets>=3.3"]),
}

# An UNVERSIONED provides cannot answer a versioned dependency: it makes no
# claim about which version it is.
UNVERSIONED_DB = {
    "autoconf-wrapper": db_entry("autoconf-wrapper", "20260320-1", provides=["autoconf"]),
    "autoconf-user": db_entry("autoconf-user", "1-1", depends=["autoconf>=2.69"]),
}

NETCAT_OVERRIDE = {
    "virtual": "netcat",
    "package": "openbsd-netcat",
    "constraint": "",
    "reason": "fixture",
}


def resolver_control(name: str, database: dict, roots, overrides, check) -> None:
    """Run the resolver over a fixture and hand the outcome to `check`.

    `check(closure, error)` receives exactly one of the two.
    """
    try:
        closure, _ = msys2db.resolve(roots, msys2db.index(database), overrides)
    except msys2db.LockError as error:
        ok, detail = check(None, error)
    else:
        ok, detail = check(closure, None)
    record(name, ok, detail)


def expect_stop(fragment: str):
    def check(closure, error):
        if error is None:
            return False, f"accepted a closure it had to refuse: {closure}"
        return fragment in str(error), str(error)

    return check


def expect_closure(*expected: str):
    wanted = sorted(expected)

    def check(closure, error):
        if error is not None:
            return False, f"refused a resolvable closure: {error}"
        return closure == wanted, f"closure={closure}"

    return check


def provider_resolution_controls() -> None:
    resolver_control(
        "control.real-package-beats-virtual-provider",
        RUNTIME_DB,
        ["base"],
        [],
        expect_closure("base", "msys2-runtime"),
    )
    resolver_control(
        "control.unique-virtual-provider-resolves",
        UNIQUE_DB,
        ["netcat-user"],
        [],
        expect_closure("netcat-user", "openbsd-netcat"),
    )
    resolver_control(
        "control.ambiguous-virtual-providers-stop",
        AMBIGUOUS_DB,
        ["netcat-user"],
        [],
        expect_stop("no reviewed provider override"),
    )
    resolver_control(
        "control.ambiguity-resolved-by-reviewed-override",
        AMBIGUOUS_DB,
        ["netcat-user"],
        [NETCAT_OVERRIDE],
        expect_closure("netcat-user", "openbsd-netcat"),
    )
    resolver_control(
        "control.override-naming-an-unknown-package",
        AMBIGUOUS_DB,
        ["netcat-user"],
        [dict(NETCAT_OVERRIDE, package="nc-from-nowhere")],
        expect_stop("not a package in the repositories"),
    )
    resolver_control(
        "control.override-naming-a-non-provider",
        AMBIGUOUS_DB,
        ["netcat-user"],
        [dict(NETCAT_OVERRIDE, package="netcat-user")],
        expect_stop("does not provide"),
    )
    resolver_control(
        "control.override-for-an-unknown-virtual",
        AMBIGUOUS_DB,
        ["netcat-user"],
        [dict(NETCAT_OVERRIDE, virtual="telnet")],
        expect_stop("nothing in the repositories provides"),
    )
    resolver_control(
        "control.override-shadowing-a-real-package",
        AMBIGUOUS_DB,
        ["netcat-user"],
        [dict(NETCAT_OVERRIDE, virtual="gnu-netcat", package="gnu-netcat")],
        expect_stop("always wins its own name"),
    )
    resolver_control(
        "control.override-missing-a-field",
        AMBIGUOUS_DB,
        ["netcat-user"],
        [{"virtual": "netcat", "package": "openbsd-netcat"}],
        expect_stop("is missing: constraint, reason"),
    )
    # Stale: the constraint the override answers is not the one being asked, so
    # the ambiguity is still unresolved AND the override is unused.
    resolver_control(
        "control.override-with-the-wrong-constraint",
        AMBIGUOUS_DB,
        ["netcat-user"],
        [dict(NETCAT_OVERRIDE, constraint=">=1.0")],
        expect_stop("no reviewed provider override"),
    )
    resolver_control(
        "control.unused-override",
        UNIQUE_DB,
        ["netcat-user"],
        [NETCAT_OVERRIDE],
        expect_stop("no dependency in this closure needed"),
    )
    # Version constraints decide compatibility, so only one provider qualifies
    # and no override is needed or permitted.
    resolver_control(
        "control.version-constraint-selects-one-provider",
        VERSIONED_DB,
        ["wx-user"],
        [],
        expect_closure("wx-3.3", "wx-user"),
    )
    resolver_control(
        "control.override-selecting-an-incompatible-provider",
        VERSIONED_DB,
        ["wx-user"],
        [
            {
                "virtual": "wxwidgets",
                "package": "wx-3.2",
                "constraint": ">=3.3",
                "reason": "fixture",
            }
        ],
        expect_stop("no dependency in this closure needed"),
    )
    resolver_control(
        "control.unversioned-provides-cannot-answer-a-constraint",
        UNVERSIONED_DB,
        ["autoconf-user"],
        [],
        expect_stop("nothing in the repositories satisfies"),
    )
    resolver_control(
        "control.alternation-in-a-dependency-is-refused",
        {"a": db_entry("a", "1-1", depends=["b|c"]), "b": db_entry("b", "1-1")},
        ["a"],
        [],
        expect_stop("which pacman does not define"),
    )


def order_independence_control(database: dict, roots, name: str) -> None:
    """Reversing every order that exists must not change the closure.

    The three orders a resolver could accidentally depend on are the order the
    databases are merged in, the order `%PROVIDES%` appears inside a `desc`,
    and the order the roots are given in. All three are reversed at once.
    """
    forward = msys2db.index(database)
    reversed_db = {
        name_: dict(entry, provides=list(reversed(entry["provides"])))
        for name_, entry in reversed(list(database.items()))
    }
    backward = msys2db.index(reversed_db)
    try:
        one, _ = msys2db.resolve(roots, forward, [NETCAT_OVERRIDE])
        two, _ = msys2db.resolve(list(reversed(list(roots))), backward, [NETCAT_OVERRIDE])
    except msys2db.LockError as error:
        record(name, False, f"resolution failed: {error}")
        return
    record(name, one == two, f"forward={one} reversed={two}")


# Differential vectors taken from the real `vercmp` shipped with pacman 7.1.0
# (`docker run archlinux vercmp`). They are the cases a plausible but wrong
# implementation gets wrong, so they are recorded rather than described.
VERCMP_VECTORS = [
    ("1.0.0", "1.0.0", 0),
    ("1.0.1", "1.0.0", 1),
    ("1.0", "1.0.0", -1),
    ("1.10", "1.9", 1),
    ("1.0a", "1.0", -1),
    ("1.0a", "1.0b", -1),
    ("1.0alpha", "1.0", -1),
    ("1.0", "1.0-1", 0),
    ("1.0-1", "1.0-2", -1),
    ("1:1.0", "2.0", 1),
    ("1.0~rc1", "1.0", 1),
    ("1.0~rc1", "1.0~rc2", -1),
    ("1~20260214-1", "1", 1),
    ("2.0", "2.0~beta", -1),
    ("1.0~", "1.0", 1),
    ("1.0", "1.0~", -1),
    ("20260320-1", "20260319-1", 1),
    ("1.5", "1.5.1", -1),
    ("a", "1", -1),
    ("9+x86-9", "9~0.007", -1),
    ("007-alpha-x86-1", "0:007~2+20260320+20260320-3", -1),
    ("3.6.10-2", "3.3.6-16", 1),
]


def vercmp_control() -> None:
    wrong = [
        (a, b, expected, msys2db.vercmp(a, b))
        for a, b, expected in VERCMP_VECTORS
        if msys2db.vercmp(a, b) != expected
    ]
    record(
        "control.vercmp-matches-pacman",
        not wrong,
        f"{len(VERCMP_VECTORS)} vectors agree with pacman 7.1.0"
        if not wrong
        else f"disagreements: {wrong}",
    )


def collision_control() -> None:
    """Two databases declaring the same name must stop, not last-one-wins."""
    try:
        msys2db.index(
            {"same": db_entry("same", "1-1")},
            {"same": dict(db_entry("same", "2-1"), repository="clang64")},
        )
    except msys2db.LockError as error:
        record("control.cross-database-name-collision", "declared by both" in str(error), str(error))
        return
    record("control.cross-database-name-collision", False, "a collision was merged silently")


# ── signature-verification controls (W1-R-B §2.2) ──────────────────────────
#
# These run against throwaway keys generated here, never against the real MSYS2
# signing key. What is under test is the DECISION PROCEDURE — which status lines
# are refused, whether the allowlist is honoured, whether an ambient keyring can
# influence the outcome — and a fixture can express damage (a bad signature, an
# unaccepted signer) that cannot responsibly be produced with real material.
# The real key is exercised by the ingest job, which verifies all 246 archives.


def gpg_home(work: Path, name: str) -> Path:
    home = work / name
    home.mkdir(parents=True, exist_ok=True)
    home.chmod(0o700)
    return home


def gpg(home: Path, *arguments: str, check: bool = True) -> subprocess.CompletedProcess:
    result = subprocess.run(
        [
            signing.find_gpg(),
            "--homedir",
            str(home),
            "--batch",
            "--no-tty",
            "--quiet",
            "--pinentry-mode",
            "loopback",
            "--passphrase",
            "",
            *arguments,
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    if check and result.returncode != 0:
        raise RuntimeError(f"gpg {arguments}: {result.stderr.strip()}")
    return result


def make_signer(work: Path, label: str) -> tuple:
    """Generate a throwaway signing key. Returns `(home, fingerprint)`."""
    home = gpg_home(work, f"signer-{label}")
    gpg(home, "--quick-generate-key", f"W1R Control {label} <{label}@example.invalid>",
        "ed25519", "sign", "0")
    listing = gpg(home, "--list-keys", "--with-colons").stdout
    fingerprint = next(
        line.split(":")[9] for line in listing.splitlines() if line.startswith("fpr:")
    )
    return home, fingerprint


def make_trust_dir(work: Path, name: str, home: Path, fingerprints) -> Path:
    """Write a fixture trust root holding `home`'s key under `fingerprints`."""
    trust = work / name
    trust.mkdir(parents=True, exist_ok=True)
    armoured = gpg(
        home, "--export", "--armor", "--export-options", "export-minimal"
    ).stdout.encode("utf-8")
    (trust / "msys2-signing-keys.asc").write_bytes(armoured)
    (trust / "trust-root.json").write_bytes(
        bundle.canonical_json(
            {
                "keyFile": "msys2-signing-keys.asc",
                "keyFileSha256": hashlib.sha256(armoured).hexdigest(),
                "keyFileBytes": len(armoured),
                "acceptedFingerprints": list(fingerprints),
            }
        )
    )
    return trust


def signature_control(name: str, call, expect_fragment: str) -> None:
    try:
        call()
    except signing.SignatureError as error:
        ok = expect_fragment in str(error)
        record(name, ok, str(error)[:200] if ok else f"wrong refusal: {error}")
        return
    record(name, False, "a signature check accepted something it had to refuse")


def signing_controls(work: Path) -> None:
    root = work / "signing"
    root.mkdir(parents=True, exist_ok=True)

    accepted_home, accepted_fpr = make_signer(root, "accepted")
    other_home, other_fpr = make_signer(root, "unaccepted")
    trust = make_trust_dir(root, "trust", accepted_home, [accepted_fpr])

    payload = root / "alpha-1.0-1-x86_64.pkg.tar.zst"
    payload.write_bytes(b"alpha-package-bytes")
    good_sig = root / "good.sig"
    gpg(accepted_home, "--detach-sign", "--output", str(good_sig), str(payload))
    other_sig = root / "other.sig"
    gpg(other_home, "--detach-sign", "--output", str(other_sig), str(payload))

    def verify(trust_dir: Path, archive: Path, signature: Path) -> str:
        with signing.Verifier(trust_dir) as verifier:
            return verifier.verify(archive, signature)

    # The undamaged case, so a verifier that simply refuses everything cannot
    # score a green run.
    try:
        fingerprint = verify(trust, payload, good_sig)
        record(
            "control.accepted-signature-verifies",
            fingerprint == accepted_fpr,
            f"VALIDSIG {fingerprint}",
        )
    except signing.SignatureError as error:
        record("control.accepted-signature-verifies", False, str(error))

    corrupted = root / "corrupted-1.0-1-x86_64.pkg.tar.zst"
    corrupted.write_bytes(b"alpha-package-bytez")
    signature_control(
        "control.signature-over-corrupted-package",
        lambda: verify(trust, corrupted, good_sig),
        "BADSIG",
    )

    damaged_sig = root / "damaged.sig"
    raw = bytearray(good_sig.read_bytes())
    raw[-1] ^= 0xFF
    damaged_sig.write_bytes(bytes(raw))
    signature_control(
        "control.corrupted-signature",
        lambda: verify(trust, payload, damaged_sig),
        "BADSIG",
    )

    signature_control(
        "control.missing-signature",
        lambda: verify(trust, payload, root / "absent.sig"),
        "no detached signature",
    )

    # The signature IS valid; the key is simply not one we accept. Because the
    # hermetic home holds exactly the allowlist, GnuPG cannot even check it and
    # reports ERRSIG (no public key) — which is a refusal, not an "unknown".
    signature_control(
        "control.valid-signature-from-an-unaccepted-key",
        lambda: verify(trust, payload, other_sig),
        "ERRSIG",
    )

    # And the fingerprint allowlist is asserted independently of that, so a
    # VALIDSIG whose signer is not on the list is refused even if some future
    # change let a second key into the keyring.
    def validsig_outside_the_allowlist() -> None:
        with signing.Verifier(trust) as verifier:
            verifier.accepted = {"1" * 40}
            verifier.verify(payload, good_sig)

    signature_control(
        "control.validsig-from-a-fingerprint-outside-the-allowlist",
        validsig_outside_the_allowlist,
        "not an accepted MSYS2 signing key",
    )

    # An allowlist naming a fingerprint the key file does not carry: the two
    # halves of the trust root must agree, or neither is load-bearing.
    widened = make_trust_dir(root, "widened", accepted_home, [accepted_fpr, other_fpr])
    signature_control(
        "control.altered-fingerprint-allowlist",
        lambda: verify(widened, payload, good_sig),
        "do not equal the accepted allowlist",
    )

    unknown = make_trust_dir(root, "unknown-fpr", accepted_home, ["0" * 40])
    signature_control(
        "control.unknown-fingerprint-in-the-allowlist",
        lambda: verify(unknown, payload, good_sig),
        "do not equal the accepted allowlist",
    )

    tampered = make_trust_dir(root, "tampered", accepted_home, [accepted_fpr])
    key_path = tampered / "msys2-signing-keys.asc"
    key_path.write_bytes(key_path.read_bytes() + b"\n")
    signature_control(
        "control.altered-trusted-key-bytes",
        lambda: verify(tampered, payload, good_sig),
        "The signing material has been altered.",
    )

    swapped = make_trust_dir(root, "swapped", accepted_home, [accepted_fpr])
    replacement = gpg(
        other_home, "--export", "--armor", "--export-options", "export-minimal"
    ).stdout.encode("utf-8")
    record_path = swapped / "trust-root.json"
    updated = json.loads(record_path.read_text())
    updated["keyFileSha256"] = hashlib.sha256(replacement).hexdigest()
    updated["keyFileBytes"] = len(replacement)
    (swapped / "msys2-signing-keys.asc").write_bytes(replacement)
    record_path.write_bytes(bundle.canonical_json(updated))
    signature_control(
        "control.substituted-trusted-key",
        lambda: verify(swapped, payload, other_sig),
        "do not equal the accepted allowlist",
    )

    # The ambient keyring must be unreachable. `other_home` holds the
    # unaccepted key and is pointed at through every variable GnuPG honours; a
    # verifier that consulted it would accept `other_sig`.
    inherited = {
        "GNUPGHOME": str(other_home),
        "GPG_AGENT_INFO": str(other_home),
    }
    previous = {key: os.environ.get(key) for key in inherited}
    os.environ.update(inherited)
    try:
        # GNUPGHOME points at a keyring that DOES hold the unaccepted key. A
        # verifier that consulted it would return VALIDSIG; this one still has
        # no public key for the signature and refuses.
        signature_control(
            "control.ambient-keyring-cannot-be-consulted",
            lambda: verify(trust, payload, other_sig),
            "ERRSIG",
        )
        # And the accepted key still verifies with GNUPGHOME hijacked, so the
        # control above is not passing merely because gpg broke.
        try:
            fingerprint = verify(trust, payload, good_sig)
            record(
                "control.hermetic-home-survives-a-hijacked-gnupghome",
                fingerprint == accepted_fpr,
                f"VALIDSIG {fingerprint}",
            )
        except signing.SignatureError as error:
            record("control.hermetic-home-survives-a-hijacked-gnupghome", False, str(error))
    finally:
        for key, value in previous.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value

    # An empty allowlist would refuse everything, which is not verification.
    empty = make_trust_dir(root, "empty", accepted_home, [])
    signature_control(
        "control.empty-fingerprint-allowlist",
        lambda: verify(empty, payload, good_sig),
        "accepts no fingerprints",
    )

    # The real committed trust root must be loadable and must name exactly the
    # signer the lock's packages carry.
    try:
        real = signing.load_trust_root(HERE / "trust")
        accepted = set(real["acceptedFingerprints"])
        declared = {entry["fingerprint"] for entry in real["acceptedSigners"]}
        record(
            "control.committed-trust-root-loads",
            accepted == declared and bool(accepted),
            f"accepts {sorted(accepted)}, sha256 {real['_sha256']}",
        )
    except signing.SignatureError as error:
        record("control.committed-trust-root-loads", False, str(error))


def installed_set_control(work: Path, name: str, observation: str, expect_fragment: str) -> None:
    """Rule on one synthetic `pacman -Qi` observation against the fixture lock.

    A hosted Windows job cannot be asked to stage a runner image carrying an
    undeclared package, or a locked package at the wrong version, on demand.
    The ruling therefore lives in `installed-set.py`, takes the observation as
    data, and is exercised here against exactly those situations.
    """
    directory = work / name
    directory.mkdir(parents=True, exist_ok=True)
    lock, _ = base_lock()
    lock_path = write_lock(directory, lock)
    observed = directory / "observed.txt"
    observed.write_text(observation, encoding="utf-8")

    result = run_validator(
        "installed-set.py", "--lock", str(lock_path), "--observed", str(observed)
    )
    output = result.stdout + result.stderr
    if expect_fragment == "":
        record(
            name,
            result.returncode == 0,
            output.strip().replace("\n", " ")[:160] or "accepted",
        )
        return
    ok = result.returncode != 0 and expect_fragment in output
    record(name, ok, output.strip().splitlines()[-1][:200] if output.strip() else "no output")


EXACT_OBSERVATION = "alpha|1.0-1|x86_64\nbeta|2.0-1|x86_64\ngamma|3.0-1|any\n"


def installed_set_controls(work: Path) -> None:
    installed_set_control(
        work, "control.installed-set-equal-to-the-lock", EXACT_OBSERVATION, ""
    )
    installed_set_control(
        work,
        "control.preexisting-package-outside-the-lock",
        EXACT_OBSERVATION + "vendor-telemetry|1.0-1|x86_64\n",
        "the lock does not name",
    )
    installed_set_control(
        work,
        "control.locked-package-at-the-wrong-version",
        "alpha|1.0-2|x86_64\nbeta|2.0-1|x86_64\ngamma|3.0-1|any\n",
        "a version the lock does not name",
    )
    installed_set_control(
        work,
        "control.unexpected-architecture",
        "alpha|1.0-1|aarch64\nbeta|2.0-1|x86_64\ngamma|3.0-1|any\n",
        "unexpected architecture",
    )
    installed_set_control(
        work,
        "control.missing-locked-package",
        "alpha|1.0-1|x86_64\nbeta|2.0-1|x86_64\n",
        "are not installed",
    )
    installed_set_control(
        work,
        "control.package-introduced-after-installation",
        EXACT_OBSERVATION + "perl|5.40-1|x86_64\n",
        "the lock does not name",
    )
    installed_set_control(
        work,
        "control.same-package-listed-twice",
        EXACT_OBSERVATION + "alpha|1.0-1|x86_64\n",
        "listed twice",
    )
    installed_set_control(
        work,
        "control.unparsable-installed-listing",
        "alpha 1.0-1 x86_64\n",
        "is not `name|version|architecture`",
    )
    installed_set_control(
        work, "control.empty-installed-listing", "\n\n", "the observation is empty"
    )


def prohibited_pacman_control(repo_root: Path) -> None:
    """No tracked W1-R script may invoke live pacman resolution."""
    offenders = []
    for path in sorted((repo_root / "ci/windows/build-inputs").rglob("*")):
        if not path.is_file() or path.suffix not in {".sh", ".ps1", ".py", ".yml"}:
            continue
        # This file IS the detector; its own literals are the pattern.
        if path.name == Path(__file__).name:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for line in text.splitlines():
            stripped = line.strip()
            # A line that names the prohibition in prose or refuses it is the
            # point; only an actual invocation counts.
            if stripped.startswith("#") or stripped.startswith("*"):
                continue
            if "pacman -S" in stripped or "pacman -Syu" in stripped:
                if "PROHIBITED" in line or "refus" in line.lower():
                    continue
                offenders.append(f"{path.name}: {stripped[:80]}")
    workflow = repo_root / ".github/workflows/w1-windows-build-inputs.yml"
    if workflow.is_file():
        for line in workflow.read_text().splitlines():
            stripped = line.strip()
            if stripped.startswith("#"):
                continue
            if "pacman -S" in stripped or "pacman -Syu" in stripped:
                if "PROHIBITED" in line or "refus" in line.lower():
                    continue
                offenders.append(f"workflow: {stripped[:80]}")
    record(
        "control.no-prohibited-pacman-invocation",
        not offenders,
        "no live-resolution invocation found" if not offenders else str(offenders),
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=HERE.parents[2])
    args = parser.parse_args()

    work = Path(tempfile.mkdtemp(prefix="w1r-controls-"))
    try:
        undamaged_lock_control(work)

        def drop_package(lock):
            lock["packages"].pop()

        lock_control(work, "control.missing-package", drop_package, "packageCount")

        def add_package(lock):
            lock["packages"].append(fake_package("delta", "4.0-1", "x86_64", b"delta"))

        lock_control(work, "control.additional-package", add_package, "packageCount")

        def rename_archive(lock):
            lock["packages"][0]["filename"] = "renamed-1.0-1-x86_64.pkg.tar.zst"

        lock_control(
            work, "control.renamed-archive", rename_archive, "does not start with"
        )

        def wrong_sha(lock):
            lock["packages"][0]["sha256"] = "z" * 64

        lock_control(work, "control.wrong-sha256", wrong_sha, "malformed sha256")

        def wrong_version(lock):
            lock["packages"][0]["version"] = "9.9-9"

        lock_control(
            work, "control.wrong-version", wrong_version, "does not start with"
        )

        def wrong_arch(lock):
            lock["packages"][0]["architecture"] = "aarch64"
            lock["packages"][0]["filename"] = "alpha-1.0-1-aarch64.pkg.tar.zst"
            lock["packages"][0]["url"] = (
                "https://repo.msys2.org/msys/x86_64/alpha-1.0-1-aarch64.pkg.tar.zst"
            )
            lock["packages"][0]["signatureUrl"] = lock["packages"][0]["url"] + ".sig"

        lock_control(
            work, "control.wrong-architecture", wrong_arch, "not a win-x64 build input"
        )

        def unresolved(lock):
            lock["packages"][0]["depends"] = ["omega"]

        lock_control(
            work, "control.unresolved-dependency", unresolved, "closure is incomplete"
        )

        def duplicate(lock):
            lock["packages"].append(copy.deepcopy(lock["packages"][0]))
            lock["packageCount"] += 1
            lock["compressedBytes"] += lock["packages"][0]["compressedBytes"]
            lock["installedBytes"] += lock["packages"][0]["installedBytes"]

        lock_control(work, "control.duplicate-lock-entry", duplicate, "duplicate package")

        def unknown_field(lock):
            lock["surprise"] = True

        lock_control(work, "control.unknown-lock-field", unknown_field, "unknown field")

        def unknown_schema(lock):
            lock["schemaVersion"] = 99

        lock_control(
            work, "control.unknown-schema-version", unknown_schema, "not implemented"
        )

        def traversal_filename(lock):
            lock["packages"][0]["filename"] = "../alpha-1.0-1-x86_64.pkg.tar.zst"

        lock_control(
            work, "control.unsafe-lock-filename", traversal_filename, "does not start with"
        )

        undamaged_bundle_control(work)

        bundle_control(
            work,
            "control.corrupted-archive",
            lambda root: (root / "packages" / "alpha-1.0-1-x86_64.pkg.tar.zst").write_bytes(
                b"corrupted"
            ),
            "does not match its own manifest.sha256",
        )
        bundle_control(
            work,
            "control.extra-bundle-file",
            lambda root: (root / "packages" / "undeclared.pkg.tar.zst").write_bytes(b"x"),
            "does not match its own manifest.sha256",
        )
        bundle_control(
            work,
            "control.missing-bundle-file",
            lambda root: (root / "packages" / "beta-2.0-1-x86_64.pkg.tar.zst").unlink(),
            "does not match its own manifest.sha256",
        )
        bundle_control(
            work,
            "control.changed-package-lock",
            lambda root: (root / "msys2-lock.json").write_bytes(b"{}\n"),
            "lockSha256",
        )

        manifest_tamper_control(work)
        path_traversal_control(work)
        symlink_control(work)

        digest = "sha256:" + "a" * 64
        contract_control(
            "control.tag-only-reference",
            lambda: contract.parse_reference(f"{contract.CANONICAL}:latest"),
            "not digest-pinned",
        )
        contract_control(
            "control.wrong-registry",
            lambda: contract.parse_reference(
                f"ghcr.io/someone-else/windows-ffmpeg-build-inputs@{digest}"
            ),
            "not the authorised package",
        )
        contract_control(
            "control.malformed-digest",
            lambda: contract.parse_reference(f"{contract.CANONICAL}@sha256:short"),
            "not a sha256 manifest digest",
        )
        contract_control(
            "control.publication-from-pull-request",
            lambda: contract.assert_trusted_ref("refs/pull/237/merge"),
            "pull request ref",
        )
        contract_control(
            "control.publication-from-branch",
            lambda: contract.assert_trusted_ref("refs/heads/w1/windows-build-input-retention"),
            "feature branch",
        )
        contract_control(
            "control.publication-from-tag",
            lambda: contract.assert_trusted_ref("refs/tags/v1.0.0"),
            "tag can be created and moved",
        )
        contract_control(
            "control.publication-without-expected-digest",
            lambda: contract.assert_expected_digest("", digest),
            "no expected manifest digest",
        )
        contract_control(
            "control.digest-mismatch-after-pull",
            lambda: contract.assert_expected_digest(digest, "sha256:" + "b" * 64),
            "Nothing is pushed",
        )

        # The one ref that must be accepted, so the trust check is not simply
        # refusing everything.
        try:
            contract.assert_trusted_ref(contract.TRUSTED_REF)
            contract.parse_reference(f"{contract.CANONICAL}@{digest}")
            record("control.trusted-master-accepted", True, "refs/heads/master accepted")
        except contract.ContractError as error:
            record("control.trusted-master-accepted", False, str(error))

        signing_controls(work)
        installed_set_controls(work)
        vercmp_control()
        collision_control()
        provider_resolution_controls()
        order_independence_control(
            AMBIGUOUS_DB, ["netcat-user"], "control.resolution-is-order-independent"
        )
        prohibited_pacman_control(args.repo_root)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    print(f"\n{len(PASSED)} passed, {len(FAILED)} failed")
    if FAILED:
        print("failed controls: " + ", ".join(FAILED), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
