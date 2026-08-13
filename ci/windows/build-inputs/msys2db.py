"""Read an MSYS2/pacman repository database and resolve a closure from it.

The repository database is the authoritative source for every field the lock
records. It is a zstd-compressed tar of one `<pkg>/desc` file per package, and
each `desc` publishes `%NAME%`, `%VERSION%`, `%ARCH%`, `%FILENAME%`,
`%SHA256SUM%`, `%CSIZE%`, `%ISIZE%`, `%LICENSE%`, `%DEPENDS%`, `%PROVIDES%` and
`%GROUPS%`. W0 verified the digest mechanism end to end (§7.4 of
docs/distribution/W0-windows-server.md); this module is the parser that turns it
into a lock.

Nothing here contacts the network. The caller supplies database bytes it has
already fetched and digest-verified.

RESOLUTION IS FAIL-CLOSED AND ORDER-INDEPENDENT (W1-R-B §2.1)
-------------------------------------------------------------
"A real package wins its own name" is necessary but not sufficient. Three more
properties are enforced here, because each of them is a way the closure could
change without anyone changing the lock:

  * a dependency naming a real package resolves to that package, and its
    version constraint is evaluated against that package's real version;
  * a virtual name with exactly one COMPATIBLE provider resolves to it;
  * a virtual name with two or more compatible providers is AMBIGUOUS and stops
    the resolution. It may be resolved only by an explicit, reviewed entry in
    `provider-overrides.json`, which names the virtual, the selected real
    package and the constraint it applies to.

Nothing depends on iteration order: the two databases are checked to have
disjoint names rather than merged with a last-one-wins rule, provider lists are
sorted by package name, and the work queue is drained in sorted order. Reversing
the database arguments or the order of `%PROVIDES%` inside a `desc` cannot
change the result — `negative-controls.py` proves it by doing exactly that.

Version comparison is pacman's `vercmp`, not a string compare: `1~20260214-1`
sorts BELOW `1`, and `1.10` sorts ABOVE `1.9`. A naive compare is wrong on
exactly the entries that matter.

pacman's dependency grammar has no `|` alternation. "Alternative dependency
expressions" therefore means the provider alternatives handled above, together
with the `<`, `<=`, `=`, `>=`, `>` constraint forms handled by `parse_dep`.
A dependency string carrying a `|` is rejected rather than guessed at.
"""

from __future__ import annotations

import io
import re
import tarfile
from typing import Dict, Iterable, List, Optional, Tuple

# A dependency or provides expression may carry a version constraint
# (`bash>=4.2.045`). The operator is significant and is NOT discarded.
_DEP = re.compile(r"^(?P<name>[^<>=]+)(?:(?P<op><=|>=|=|<|>)(?P<version>.+))?$")
_ALPHA_NUM = re.compile(r"([0-9]+|[a-zA-Z]+)")


class LockError(Exception):
    """Raised for every fail-closed condition. Never caught to continue."""


# ── version comparison ─────────────────────────────────────────────────────


def _split_evr(value: str) -> Tuple[str, str, Optional[str]]:
    """Split `[epoch:]version[-pkgrel]` exactly the way pacman's `parseEVR` does.

    The details matter. An epoch is recognised only when the leading run of
    DIGITS is followed by `:`, so `a:1` has no epoch and is a version of its
    own; an empty epoch means `0`; and the release separator is the LAST `-`
    after that leading digit run.
    """
    end_of_digits = 0
    while end_of_digits < len(value) and value[end_of_digits].isdigit():
        end_of_digits += 1

    if end_of_digits < len(value) and value[end_of_digits] == ":":
        epoch = value[:end_of_digits] or "0"
        rest = value[end_of_digits + 1 :]
    else:
        epoch = "0"
        rest = value

    if "-" in rest:
        version, _, pkgrel = rest.rpartition("-")
    else:
        version, pkgrel = rest, None
    return epoch, version, pkgrel


def _rpmvercmp(a: str, b: str) -> int:
    """A faithful port of pacman's `rpmvercmp` (libalpm/version.c).

    Ported rather than re-derived, because the interesting cases are the
    counter-intuitive ones and a plausible re-derivation gets them wrong:

      * `1.0a` is OLDER than `1.0` — a trailing alphabetic segment never beats
        an exhausted string;
      * `1.0~rc1` is NEWER than `1.0` — the tilde only sorts low against
        another segment, not against the end of the string;
      * `2.0` is NEWER than `2.0~beta`, which is the same rule seen from the
        other side.

    Every case here is differentially tested against the real `vercmp` from
    pacman by `negative-controls.py`'s vector table.
    """
    if a == b:
        return 0

    one = ptr1 = 0
    two = ptr2 = 0
    length_a, length_b = len(a), len(b)

    while one < length_a and two < length_b:
        # `~` is NOT special-cased: pacman treats it as an ordinary separator
        # character, and the rule that makes `1.0~rc1` sort where it does is
        # the separator-RUN-LENGTH comparison two lines below.
        while one < length_a and not a[one].isalnum():
            one += 1
        while two < length_b and not b[two].isalnum():
            two += 1
        if one >= length_a or two >= length_b:
            break

        if (one - ptr1) != (two - ptr2):
            return -1 if (one - ptr1) < (two - ptr2) else 1

        ptr1, ptr2 = one, two

        numeric = a[one].isdigit()
        if numeric:
            while one < length_a and a[one].isdigit():
                one += 1
            while two < length_b and b[two].isdigit():
                two += 1
        else:
            while one < length_a and a[one].isalpha():
                one += 1
            while two < length_b and b[two].isalpha():
                two += 1

        if one == ptr1:
            # Cannot happen: the segment type was taken from `a`.
            return -1
        if two == ptr2:
            # Different segment types. A numeric segment always outranks an
            # alphabetic one.
            return 1 if numeric else -1

        if numeric:
            # Leading zeros are thrown away IN PLACE, so the discarded zeros
            # also shorten the separator run measured by the NEXT iteration.
            while ptr1 < one and a[ptr1] == "0":
                ptr1 += 1
            while ptr2 < two and b[ptr2] == "0":
                ptr2 += 1
            if (one - ptr1) != (two - ptr2):
                return 1 if (one - ptr1) > (two - ptr2) else -1

        segment_a = a[ptr1:one]
        segment_b = b[ptr2:two]
        if segment_a != segment_b:
            return 1 if segment_a > segment_b else -1

    if one >= length_a and two >= length_b:
        return 0

    # A remaining alphabetic string never beats an exhausted one; anything
    # else that remains wins.
    if (one >= length_a and not (two < length_b and b[two].isalpha())) or (
        one < length_a and a[one].isalpha()
    ):
        return -1
    return 1


def vercmp(a: str, b: str) -> int:
    """Compare two pacman versions. Returns -1, 0 or 1."""
    if a == b:
        return 0
    epoch_a, version_a, rel_a = _split_evr(a)
    epoch_b, version_b, rel_b = _split_evr(b)
    result = _rpmvercmp(epoch_a, epoch_b)
    if result != 0:
        return result
    result = _rpmvercmp(version_a, version_b)
    if result != 0:
        return result
    # A missing pkgrel on either side means the comparison ignores pkgrel, so
    # `1.0` satisfies `>=1.0-3`.
    if rel_a is None or rel_b is None:
        return 0
    return _rpmvercmp(rel_a, rel_b)


def parse_dep(expression: str) -> Tuple[str, Optional[str], Optional[str]]:
    """Split a dependency or provides expression into `(name, op, version)`."""
    text = expression.strip()
    if not text:
        raise LockError("empty dependency expression")
    if "|" in text:
        raise LockError(
            f"dependency {expression!r} uses `|`, which pacman does not define; "
            "refusing to guess its semantics"
        )
    match = _DEP.match(text)
    if match is None:
        raise LockError(f"unparseable dependency expression: {expression!r}")
    name = match.group("name").strip()
    if not name:
        raise LockError(f"dependency expression names nothing: {expression!r}")
    return name, match.group("op"), match.group("version")


def satisfies(available: Optional[str], op: Optional[str], wanted: Optional[str]) -> bool:
    """Does `available` satisfy the constraint `op wanted`?

    `available is None` means an UNVERSIONED `%PROVIDES%` entry. It satisfies an
    unconstrained dependency and nothing else: `provides: autoconf` makes no
    claim about which autoconf this is, so it cannot answer `autoconf>=2.69`.
    """
    if op is None:
        return True
    if available is None:
        return False
    result = vercmp(available, wanted)
    if op == "=":
        return result == 0
    if op == ">=":
        return result >= 0
    if op == "<=":
        return result <= 0
    if op == ">":
        return result > 0
    if op == "<":
        return result < 0
    raise LockError(f"unsupported version operator: {op!r}")


# ── database parsing ───────────────────────────────────────────────────────


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


# ── index and resolution ───────────────────────────────────────────────────


class Index:
    """A merged, order-independent view of one or more repository databases."""

    def __init__(self, packages, providers, groups):
        self.packages: Dict[str, dict] = packages
        # `{virtual name: [(package name, provided version or None), …]}`,
        # each list sorted by package name.
        self.providers: Dict[str, List[Tuple[str, Optional[str]]]] = providers
        self.groups: Dict[str, List[str]] = groups


def index(*databases: Dict[str, dict]) -> Index:
    """Build an `Index` across several databases.

    A name present in more than one database is a STOP rather than a
    last-one-wins merge: pacman would pick by repository order, and the lock
    would then depend on the order this function happened to be called with.
    The two MSYS2 repositories in scope have disjoint namespaces, so this
    condition means upstream changed something that has to be re-reviewed.
    """
    packages: Dict[str, dict] = {}
    origin: Dict[str, str] = {}
    for database in databases:
        for name, record in database.items():
            if name in packages:
                raise LockError(
                    f"{name} is declared by both {origin[name]} and "
                    f"{record['repository']}; the closure would depend on which "
                    "database was read last. Re-review the lock."
                )
            packages[name] = record
            origin[name] = record["repository"]

    providers: Dict[str, List[Tuple[str, Optional[str]]]] = {}
    groups: Dict[str, List[str]] = {}
    for name in sorted(packages):
        record = packages[name]
        for expression in record["provides"]:
            virtual, op, version = parse_dep(expression)
            if op not in (None, "="):
                raise LockError(
                    f"{name}: %PROVIDES% entry {expression!r} uses {op!r}; a "
                    "provides entry states a version, it does not constrain one"
                )
            providers.setdefault(virtual, []).append((name, version))
        for group in record["groups"]:
            groups.setdefault(group, []).append(name)
    for entries in providers.values():
        entries.sort()
    for members in groups.values():
        members.sort()
    return Index(packages, providers, groups)


def _candidates(name, op, version, catalogue: Index) -> List[str]:
    """Every real package that could satisfy `name op version`.

    A real package always wins its own name, and no virtual provider may
    contest it: `msys2-runtime-3.3` declares `provides: msys2-runtime=3.3.6`,
    and letting it answer a `msys2-runtime` dependency put an OLDER runtime in
    the closure. Installing that downgraded the runtime under the running
    MSYS2 and every later process failed to fork. Measured, not theoretical.
    """
    record = catalogue.packages.get(name)
    if record is not None:
        if not satisfies(record["version"], op, version):
            raise LockError(
                f"{name} is present at {record['version']}, which does not satisfy "
                f"{name}{op}{version}. The repository cannot answer this dependency."
            )
        return [name]
    return sorted(
        provider
        for provider, provided in catalogue.providers.get(name, [])
        if satisfies(provided, op, version)
    )


def resolve(
    roots: Iterable[str],
    catalogue: Index,
    overrides: Optional[List[dict]] = None,
) -> Tuple[List[str], List[dict]]:
    """Resolve the transitive closure of `roots`.

    Returns `(sorted package names, the override entries that were used)`.

    A root may name a package, a virtual `%PROVIDES%` entry or a group; a group
    expands to its members. Every unresolved name is an error, not a warning:
    a closure with a hole is exactly the thing that would send `pacman -U`
    looking at a live repository. Every ambiguous name is an error too, for the
    same reason in a subtler form — a silently chosen provider is a package
    nobody reviewed.
    """
    table = _override_table(overrides or [], catalogue)
    used: set = set()

    seen: set = set()
    pending = sorted(set(roots))
    while pending:
        # Sorted, so neither the database order nor the %DEPENDS% order inside
        # a desc can influence which ambiguity is reported first, nor the
        # result.
        pending.sort()
        expression = pending.pop(0)
        name, op, version = parse_dep(expression)

        if name in catalogue.groups and name not in catalogue.packages:
            pending.extend(catalogue.groups[name])
            continue

        candidates = _candidates(name, op, version, catalogue)
        if not candidates:
            raise LockError(
                f"nothing in the repositories satisfies {expression!r}. Regenerate "
                "and re-review the lock; do not install a partial closure."
            )
        if len(candidates) == 1:
            chosen = candidates[0]
        else:
            key = (name, f"{op}{version}" if op else "")
            entry = table.get(key)
            if entry is None:
                raise LockError(
                    f"{expression!r} is satisfied by {len(candidates)} providers "
                    f"({', '.join(candidates)}) and no reviewed provider override "
                    "selects one. Add an entry to provider-overrides.json naming the "
                    "virtual, the package and the constraint, or drop the dependency."
                )
            if entry["package"] not in candidates:
                raise LockError(
                    f"provider override for {expression!r} selects "
                    f"{entry['package']!r}, which is not among the compatible "
                    f"providers ({', '.join(candidates)})"
                )
            chosen = entry["package"]
            used.add(key)

        if chosen in seen:
            continue
        seen.add(chosen)
        pending.extend(catalogue.packages[chosen]["depends"])

    unused = sorted(key for key in table if key not in used)
    if unused:
        raise LockError(
            "provider override(s) that no dependency in this closure needed: "
            + ", ".join(f"{name}{constraint}" for name, constraint in unused)
            + ". An override nobody uses is stale metadata claiming a decision "
            "that is no longer being made."
        )
    return sorted(seen), [table[key] for key in sorted(used)]


def _override_table(overrides: List[dict], catalogue: Index) -> Dict[tuple, dict]:
    """Validate the reviewed provider overrides and key them for lookup.

    Every field is mandatory, because an override that does not say which
    constraint it answers would silently apply to a different dependency than
    the one that was reviewed.
    """
    table: Dict[tuple, dict] = {}
    for entry in overrides:
        missing = sorted({"virtual", "package", "constraint", "reason"} - set(entry))
        if missing:
            raise LockError(
                f"provider override {entry!r} is missing: {', '.join(missing)}"
            )
        virtual = entry["virtual"]
        package = entry["package"]
        constraint = entry["constraint"]
        if virtual in catalogue.packages:
            raise LockError(
                f"provider override for {virtual!r} is meaningless: {virtual} is a "
                "real package and always wins its own name"
            )
        if virtual not in catalogue.providers:
            raise LockError(
                f"provider override names {virtual!r}, which nothing in the "
                "repositories provides"
            )
        if package not in catalogue.packages:
            raise LockError(
                f"provider override selects {package!r}, which is not a package in "
                "the repositories"
            )
        if package not in {name for name, _ in catalogue.providers[virtual]}:
            raise LockError(
                f"provider override selects {package!r}, which does not provide "
                f"{virtual!r}"
            )
        if constraint:
            parse_dep(virtual + constraint)
        key = (virtual, constraint)
        if key in table:
            raise LockError(
                f"provider override for {virtual!r}{constraint} is declared twice"
            )
        table[key] = entry
    return table
