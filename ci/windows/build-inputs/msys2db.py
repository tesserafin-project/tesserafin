"""Read an MSYS2/pacman repository database.

The repository database is the authoritative source for every field the lock
records. It is a zstd-compressed tar of one `<pkg>/desc` file per package, and
each `desc` publishes `%NAME%`, `%VERSION%`, `%ARCH%`, `%FILENAME%`,
`%SHA256SUM%`, `%CSIZE%`, `%ISIZE%`, `%LICENSE%`, `%DEPENDS%`, `%PROVIDES%` and
`%GROUPS%`. W0 verified the digest mechanism end to end (§7.4 of
docs/distribution/W0-windows-server.md); this module is the parser that turns it
into a lock.

Nothing here contacts the network. The caller supplies database bytes it has
already fetched and digest-verified.
"""

from __future__ import annotations

import io
import re
import tarfile
from typing import Dict, Iterable, List

# A dependency expression may carry a version constraint (`bash>=4.2.045`) or a
# soname suffix. Only the package name resolves against the database.
_CONSTRAINT = re.compile(r"[<>=]")


class LockError(Exception):
    """Raised for every fail-closed condition. Never caught to continue."""


def _decompress(raw: bytes) -> bytes:
    """Return the tar bytes of a repository database.

    pacman databases are zstd-compressed regardless of the `.db` extension.
    Uncompressed tar is accepted too so a test fixture need not depend on a
    zstd implementation being present.
    """
    if raw[:4] == b"\x28\xb5\x2f\xfd":
        try:
            import zstandard
        except ImportError:
            import subprocess

            proc = subprocess.run(
                ["zstd", "-d", "-c"], input=raw, capture_output=True, check=False
            )
            if proc.returncode != 0:
                raise LockError(
                    "cannot decompress the repository database: neither the "
                    "zstandard module nor a working `zstd` binary is available"
                )
            return proc.stdout
        return zstandard.ZstdDecompressor().decompress(raw, max_output_size=1 << 30)
    return raw


def parse(raw: bytes, repo: str) -> Dict[str, dict]:
    """Parse one repository database into `{name: record}`.

    `repo` is the repository identity the lock records (`msys`, `clang64`). It
    is supplied rather than inferred: the database file does not name itself,
    and a package silently attributed to the wrong repository would be fetched
    from the wrong URL.
    """
    packages: Dict[str, dict] = {}
    with tarfile.open(fileobj=io.BytesIO(_decompress(raw)), mode="r:") as archive:
        for member in archive.getmembers():
            if not member.isfile() or not member.name.endswith("/desc"):
                continue
            handle = archive.extractfile(member)
            if handle is None:
                raise LockError(f"unreadable database member: {member.name}")
            fields = _parse_desc(handle.read().decode("utf-8"))
            name = _one(fields, "NAME", member.name)
            if name in packages:
                raise LockError(f"{repo}: package declared twice in the database: {name}")
            packages[name] = {
                "repository": repo,
                "name": name,
                "version": _one(fields, "VERSION", member.name),
                "architecture": _one(fields, "ARCH", member.name),
                "filename": _one(fields, "FILENAME", member.name),
                "sha256": _one(fields, "SHA256SUM", member.name),
                "compressedBytes": int(_one(fields, "CSIZE", member.name)),
                "installedBytes": int(_one(fields, "ISIZE", member.name)),
                "license": fields.get("LICENSE", []),
                "depends": fields.get("DEPENDS", []),
                "provides": fields.get("PROVIDES", []),
                "groups": fields.get("GROUPS", []),
            }
    if not packages:
        raise LockError(f"{repo}: the repository database contains no packages")
    return packages


def _parse_desc(text: str) -> Dict[str, List[str]]:
    fields: Dict[str, List[str]] = {}
    key = None
    for line in text.splitlines():
        if line.startswith("%") and line.endswith("%") and len(line) > 2:
            key = line[1:-1]
            fields[key] = []
        elif line.strip():
            if key is None:
                raise LockError(f"value outside any %FIELD%: {line!r}")
            fields[key].append(line.strip())
    return fields


def _one(fields: Dict[str, List[str]], key: str, where: str) -> str:
    """Return a field that must exist exactly once.

    Fail closed: a package missing `%SHA256SUM%` cannot be verified, and a
    package missing `%FILENAME%` cannot be fetched. Neither may be defaulted.
    """
    values = fields.get(key)
    if not values:
        raise LockError(f"{where}: required field %{key}% is missing")
    if len(values) != 1:
        raise LockError(f"{where}: field %{key}% has {len(values)} values, expected 1")
    return values[0]


def index(*databases: Dict[str, dict]) -> tuple:
    """Build `(packages, provides, groups)` across several databases.

    Later databases win on a name collision, which matches how pacman resolves a
    name present in more than one repository for the same install root. The
    collision is recorded rather than hidden: the caller can compare the merged
    map against each input.
    """
    packages: Dict[str, dict] = {}
    for database in databases:
        packages.update(database)

    # TWO passes, and the order is load-bearing. A real package must always win
    # the name it actually has, before any other package's virtual %PROVIDES%
    # can claim it.
    #
    # Measured, not theoretical: `base` depends on `msys2-runtime`, and the
    # compatibility package `msys2-runtime-3.3` declares
    # `provides: msys2-runtime=3.3.6`. A single pass let whichever package was
    # visited first take the name, and the compat package won -- so the closure
    # carried an OLDER runtime instead of the real one. Installing that
    # downgraded the runtime out from under the running MSYS2 and every
    # subsequent process failed to fork.
    provides: Dict[str, str] = {}
    groups: Dict[str, List[str]] = {}
    for name in packages:
        provides[name] = name
    for name, record in packages.items():
        for expression in record["provides"]:
            provides.setdefault(_CONSTRAINT.split(expression)[0], name)
        for group in record["groups"]:
            groups.setdefault(group, []).append(name)
    for members in groups.values():
        members.sort()
    return packages, provides, groups


def resolve(roots: Iterable[str], packages, provides, groups) -> List[str]:
    """Resolve the transitive closure of `roots`.

    A root may name a package, a virtual `%PROVIDES%` entry or a group; a group
    expands to its members. Every unresolved name is an error, not a warning:
    a closure with a hole is exactly the thing that would send `pacman -U`
    looking at a live repository.
    """
    seen: set = set()
    unresolved: set = set()
    pending = list(roots)
    while pending:
        name = _CONSTRAINT.split(pending.pop())[0]
        if name in groups:
            pending.extend(groups[name])
            continue
        real = provides.get(name)
        if real is None:
            unresolved.add(name)
            continue
        if real in seen:
            continue
        seen.add(real)
        pending.extend(packages[real]["depends"])
    if unresolved:
        raise LockError(
            "unresolved dependencies: " + ", ".join(sorted(unresolved))
        )
    return sorted(seen)
