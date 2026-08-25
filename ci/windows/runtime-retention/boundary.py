"""The ownership contract for the path-filter-excluded retention subtree (#236, W1-A4-R1).

WHY THIS FILE EXISTS.

W1-A4 excluded `ci/windows/runtime-retention/**` from the W1 dual-runner
workflow's `paths:` filter, so a change under this subtree does not start two
five-hour metered native Windows builds. That exclusion is only safe while
nothing under the subtree can affect how the runtime is BUILT. Nothing
structural enforced that: a `runtime-retention/ffmpeg/build.sh`, a toolchain
lock, or a W1 build script that sourced a file from here would all have been
silently exempted from the proof that is supposed to cover them. The R0 review
named that as blocking finding F2.

So this file states, as a closed schema, exactly what may live under the
excluded subtree, and scans the W1 build workflow and its transitive local
script closure to prove nothing there reaches into it.

TWO INDEPENDENT PROPERTIES, BOTH REQUIRED.

  1. INVENTORY. Every tracked file under the subtree has exactly one permitted
     retention role. There is no "other" role and no wildcard: a file nobody
     classified is a finding, not a default. Roles are retention concerns only;
     build roles are named and forbidden by name so the message says which
     build role was attempted rather than only "unknown".

  2. CLOSURE. No build, verify, package or runtime-acceptance script reachable
     from the W1 workflow reads from the subtree, no glob in that workflow can
     traverse it, no build manifest names a path under it, and no environment
     variable redirects a build input there.

The inventory is taken from `git ls-files`, never from a filesystem walk.
`__pycache__/` is ignored-but-present on any tree where these scripts have run,
and a walk would report a pristine checkout as carrying unclassified content.
"""

from __future__ import annotations

import re
import subprocess
from pathlib import Path

SUBTREE = "ci/windows/runtime-retention"

#: The W1 build workflow whose closure must not reach into the subtree.
W1_WORKFLOW = ".github/workflows/w1-windows-ffmpeg-runtime.yml"

#: The retention validation workflow, which necessarily DOES include the subtree.
RETENTION_WORKFLOW = ".github/workflows/w1-windows-runtime-retention.yml"


class BoundaryError(Exception):
    """Fail-closed condition. Never caught to continue."""


# ── the closed role schema ──────────────────────────────────────────────────
#
# Retention concerns only. Adding a role here is a deliberate act that has to
# argue why the thing it describes cannot affect a build.
PERMITTED_ROLES: dict[str, str] = {
    "accepted-manifest": "the committed acceptance manifest for the accepted runtime",
    "accepted-schema": "the closed schema the acceptance manifest must survive",
    "deterministic-oci-assembly": "deterministic assembly of the retained OCI unit",
    "retained-unit-verification": "verification of an already-built retained unit",
    "digest-only-consumer": "the W2 consumer, which takes a digest and nothing else",
    "publication-boundary-validation": "the gates that keep this half unable to publish",
    "local-registry-protocol": "the registry client, write-restricted to loopback",
    "tests-and-fixtures": "fixtures and hostile controls for the above",
    "retention-documentation": "documentation of the retention contract",
}

#: Roles that are forbidden under the subtree, and the shape that betrays each.
#: These exist so a hostile control gets a message naming the BUILD ROLE it
#: attempted, not a generic "unclassified".
FORBIDDEN_ROLE_PATTERNS: list[tuple[str, str, re.Pattern[str]]] = [
    (
        "component-source",
        "FFmpeg or component source belongs to the build, which this subtree is excluded from",
        re.compile(r"(^|/)(ffmpeg|libav\w*|x26[45]|dav1d|zlib|openssl|srt|zimg)(/|$)", re.I),
    ),
    (
        "patch",
        "a patch changes what is built and must not live behind the exclusion",
        re.compile(r"\.(patch|diff)$|(^|/)patches?(/|$)", re.I),
    ),
    (
        "configure-flags",
        "configure flags decide the produced binary and must not live behind the exclusion",
        re.compile(r"(^|/)[\w.-]*(configure|flags|cflags|ldflags)[\w.-]*\.(txt|json|conf|cfg|list)$", re.I),
    ),
    (
        "toolchain-lock",
        "a compiler or toolchain lock pins what builds the binary and must not live behind the exclusion",
        re.compile(r"(^|/)[\w.-]*(toolchain|msys2|mingw|compiler|rustup|cargo|nuget|vcpkg)[\w.-]*\.(lock|json|txt|toml|sum)$", re.I),
    ),
    (
        "dependency-prefix",
        "a dependency prefix is a build input and must not live behind the exclusion",
        re.compile(r"(^|/)(prefix|deps|dependencies|third[_-]?party|vendor)(/|$)", re.I),
    ),
    (
        "build-script",
        "a build, package or acceptance script is a W1-A3 input and must not live behind the exclusion",
        re.compile(
            r"(^|/)(build|make|configure|package|acceptance|install|bootstrap|compile)"
            r"\.(sh|ps1|bat|cmd|mk|py)$|(^|/)(Makefile|CMakeLists\.txt|meson\.build)$",
            re.I,
        ),
    ),
]

#: What each role's members may LOOK like, and how many there may be.
#:
#: Without this, `check_inventory` only rejects a role outside PERMITTED_ROLES,
#: so any swap AMONG the nine permitted roles passes silently — `consume.ps1`
#: relabelled `accepted-manifest` would be accepted, and "exactly one permitted
#: role" would mean "one of the nine, whichever". A role is a claim about what a
#: file IS, so the claim is checked.
#:
#: Cardinality is the sharper half: three of these roles describe exactly one
#: artefact each. A second accepted manifest is not a classification mistake, it
#: is a second identity.
ROLE_SHAPES: dict[str, re.Pattern[str]] = {
    "accepted-manifest": re.compile(r"\.json$"),
    "accepted-schema": re.compile(r"\.py$"),
    "deterministic-oci-assembly": re.compile(r"\.py$"),
    "retained-unit-verification": re.compile(r"\.(py|sh)$"),
    "digest-only-consumer": re.compile(r"\.ps1$"),
    "publication-boundary-validation": re.compile(r"\.(py|sh)$"),
    "local-registry-protocol": re.compile(r"\.sh$"),
    "tests-and-fixtures": re.compile(r"\.(py|json|ps1|sh)$"),
    "retention-documentation": re.compile(r"\.md$"),
}

#: Roles that describe exactly one artefact.
SINGLETON_ROLES = ("accepted-manifest", "accepted-schema", "digest-only-consumer")

#: The closed inventory. Path relative to SUBTREE -> role.
#:
#: Every tracked file under the subtree must appear here exactly once, and every
#: entry here must exist. Both directions are checked: an entry for a deleted
#: file is as much a defect as a file with no entry, because it lets the schema
#: drift into describing a tree that no longer exists.
INVENTORY: dict[str, str] = {
    "accepted-runtime.json": "accepted-manifest",
    "contract.py": "accepted-schema",
    "retention.py": "deterministic-oci-assembly",
    "assemble.py": "deterministic-oci-assembly",
    "build-oci.py": "deterministic-oci-assembly",
    "build-twice.sh": "retained-unit-verification",
    "derive-accepted.py": "retained-unit-verification",
    "scan-secrets.sh": "retained-unit-verification",
    "consume.ps1": "digest-only-consumer",
    "assert-cannot-publish.sh": "publication-boundary-validation",
    "publication_policy.py": "publication-boundary-validation",
    "boundary.py": "publication-boundary-validation",
    "trigger_policy.py": "publication-boundary-validation",
    "oci-protocol.sh": "local-registry-protocol",
    "registry-controls.sh": "local-registry-protocol",
    "make-fixture.py": "tests-and-fixtures",
    "negative-controls.py": "tests-and-fixtures",
    "permission-fixtures.py": "tests-and-fixtures",
    "boundary-controls.py": "tests-and-fixtures",
    "reference-corpus.json": "tests-and-fixtures",
    "reference-corpus.py": "tests-and-fixtures",
}

#: The files that STATE the publication policy, or exercise it. They are never
#: the file under test.
#:
#: This is the same trick the original grep checker needed and for the same
#: reason, moved up a level. `publication_policy.py` names the patterns it looks
#: for; `permission-fixtures.py` constructs a `${{ secrets.GITHUB_TOKEN }}` and an
#: `oras manifest push` on purpose, so that the checker can be shown refusing
#: them. Following the workflow's closure into those two files reports the
#: checker's own vocabulary as the workflow's capability — the absence of a thing
#: read as its presence, exactly as before.
#:
#: This is a narrow exemption and it is not a hole: fixtures 08, 09 and 10 put the
#: same text in an inline `run:` body, in a helper, and in a helper reached only
#: through another helper, and all three are refused. Nothing here holds a
#: credential, and the permission and credential checks still read the workflow
#: itself, which is where a grant would have to appear.
POLICY_SELF = (
    "publication_policy.py",
    "permission-fixtures.py",
    "assert-cannot-publish.sh",
    "boundary.py",
    "boundary-controls.py",
)

#: The only files permitted to contain a registry WRITE verb. `publication_policy`
#: reads this: a push or a login anywhere else in a workflow's closure is a
#: finding. These two are exempt only because `oci-protocol.sh` refuses to write
#: to any non-loopback host without an explicit `--allow-remote`, and the policy
#: refuses any validation workflow that passes it.
LOCAL_REGISTRY_PROTOCOL = tuple(
    name for name, role in INVENTORY.items() if role == "local-registry-protocol"
)


def repo_root(start: Path | None = None) -> Path:
    here = (start or Path(__file__).resolve()).parent if start is None else start
    out = subprocess.run(
        ["git", "-C", str(here), "rev-parse", "--show-toplevel"],
        check=True, capture_output=True, text=True,
    )
    return Path(out.stdout.strip())


def tracked(root: Path, pathspec: str) -> list[str]:
    """Every file git would carry, tracked or newly added, never ignored.

    `--others --exclude-standard` matters twice. It makes a file DROPPED into the
    subtree visible to the inventory without a `git add` first, which is what the
    hostile controls do. And `--exclude-standard` keeps `__pycache__/` out: a
    plain filesystem walk reports a pristine checkout on which these scripts have
    once run as carrying three unclassified `.pyc` files.
    """
    out = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z", "--cached", "--others",
         "--exclude-standard", "--", pathspec],
        check=True, capture_output=True, text=True,
    )
    return sorted(dict.fromkeys(p for p in out.stdout.split("\0") if p))


class Finding:
    __slots__ = ("prop", "message")

    def __init__(self, prop: str, message: str) -> None:
        self.prop = prop
        self.message = message

    def __repr__(self) -> str:  # pragma: no cover - diagnostics only
        return f"{self.prop}: {self.message}"


# ── property 1: the inventory is closed ─────────────────────────────────────
def check_inventory(root: Path) -> list[Finding]:
    findings: list[Finding] = []
    present = [p[len(SUBTREE) + 1:] for p in tracked(root, SUBTREE)]

    for rel in present:
        role = INVENTORY.get(rel)
        if role is None:
            named = None
            for forbidden, why, pattern in FORBIDDEN_ROLE_PATTERNS:
                if pattern.search(rel):
                    named = (forbidden, why)
                    break
            if named is not None:
                findings.append(Finding(
                    "boundary.forbidden-build-role",
                    f"{SUBTREE}/{rel} carries the forbidden role {named[0]!r}: {named[1]}",
                ))
            else:
                findings.append(Finding(
                    "boundary.unclassified-content",
                    f"{SUBTREE}/{rel} has no role in the closed retention inventory; "
                    f"unclassified content behind the path-filter exclusion is refused",
                ))
            continue
        if role not in PERMITTED_ROLES:
            findings.append(Finding(
                "boundary.unknown-role",
                f"{SUBTREE}/{rel} claims role {role!r}, which is not a permitted retention role",
            ))
            continue
        # A permitted role is not a licence to be a build input in disguise.
        for forbidden, why, pattern in FORBIDDEN_ROLE_PATTERNS:
            if pattern.search(rel):
                findings.append(Finding(
                    "boundary.forbidden-build-role",
                    f"{SUBTREE}/{rel} is classified {role!r} but has the shape of the "
                    f"forbidden role {forbidden!r}: {why}",
                ))
                break

    for rel in present:
        role = INVENTORY.get(rel)
        shape = ROLE_SHAPES.get(role) if role else None
        if shape is not None and not shape.search(rel):
            findings.append(Finding(
                "boundary.role-shape-mismatch",
                f"{SUBTREE}/{rel} is classified {role!r}, but that role describes "
                f"{shape.pattern!r} files; a role is a claim about what a file is",
            ))

    for role in SINGLETON_ROLES:
        members = sorted(rel for rel in present if INVENTORY.get(rel) == role)
        if len(members) != 1:
            findings.append(Finding(
                "boundary.role-cardinality",
                f"the role {role!r} describes exactly one artefact, but {len(members)} "
                f"file(s) claim it: {members}",
            ))

    for rel in INVENTORY:
        if rel not in present:
            findings.append(Finding(
                "boundary.inventory-names-absent-file",
                f"the inventory names {SUBTREE}/{rel}, which is not tracked; the schema "
                f"has drifted from the tree it describes",
            ))
    return findings


# ── property 2: nothing in the W1 build closure reaches in ──────────────────
_SCRIPT_SUFFIXES = (".sh", ".ps1", ".py", ".bat", ".cmd", ".bash")


def strip_shell_comments(text: str) -> str:
    """Remove `#` comments without letting a quoted `#` hide executable text.

    Only whole-line and unquoted trailing comments go. A `#` inside single or
    double quotes is data and is kept: dropping it could only ever HIDE
    executable content from the scan, which is the failure mode this whole file
    exists to prevent.
    """
    out: list[str] = []
    for line in text.splitlines():
        single = double = False
        cut = None
        for i, ch in enumerate(line):
            if ch == "'" and not double:
                single = not single
            elif ch == '"' and not single:
                double = not double
            elif ch == "#" and not single and not double:
                if i == 0 or line[i - 1] in " \t":
                    cut = i
                    break
        out.append(line if cut is None else line[:cut])
    return "\n".join(out)


def strip_yaml_comments(text: str) -> str:
    return strip_shell_comments(text)


def strip_python_docstrings(text: str) -> str:
    """Blank out module, class and function docstrings, semantically.

    A docstring is not executable content, and prose explaining why a module uses
    `oras manifest push` rather than `oras push` should not read as the module
    doing either. This uses `ast`, so it cannot be fooled by quoting; a string
    that is USED — assigned, passed, returned — is not an `Expr` statement and is
    kept.
    """
    import ast

    try:
        tree = ast.parse(text)
    except SyntaxError:
        return text
    lines = text.splitlines()
    blank: set[int] = set()
    for node in ast.walk(tree):
        if not isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            continue
        body = getattr(node, "body", None)
        if not body:
            continue
        first = body[0]
        if isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant) \
                and isinstance(first.value.value, str):
            blank.update(range(first.lineno - 1, (first.end_lineno or first.lineno)))
    return "\n".join("" if i in blank else line for i, line in enumerate(lines))


def strip_comments(path: str, text: str) -> str:
    """Comment-strip `text` by the language `path` names."""
    if path.endswith(".py"):
        return strip_shell_comments(strip_python_docstrings(text))
    return strip_shell_comments(text)


def local_script_closure(root: Path, entry: str, extra_scripts: tuple[str, ...] = ()) -> list[str]:
    """Every repository-local script transitively reachable from `entry`.

    Scripts are found by BASENAME, not by literal path, because real callers
    build their paths (`PROTOCOL="${HERE}/oci-protocol.sh"`) and a literal-path
    scan would follow none of them. Python `import x` is resolved against the
    entry's own directory, so a heredoc that imports a module still pulls that
    module into the closure.
    """
    all_tracked = tracked(root, ".")
    # `extra_scripts` carries absolute paths that are not in the repository —
    # the throwaway helpers a permission fixture writes. They resolve by the same
    # longest-suffix rule as a repository script, so a fixture proves the real
    # resolver rather than a second one written for the fixture.
    scripts = [p for p in all_tracked if p.endswith(_SCRIPT_SUFFIXES)] + list(extra_scripts)

    def resolve(token: str, current: str) -> list[str]:
        token = token.lstrip("./").lstrip("/")
        if not token:
            return []
        exact = [p for p in scripts if p == token or p.endswith("/" + token)]
        if len(exact) <= 1:
            return exact
        # An ambiguous BASENAME is resolved in the referring file's own
        # directory when that disambiguates, and left ambiguous otherwise so
        # the closure stays fail-closed rather than guessing.
        here = str(Path(current).parent)
        same = [p for p in exact if str(Path(p).parent) == here]
        return same or exact

    seen: list[str] = []
    queue = [entry]
    while queue:
        current = queue.pop(0)
        if current in seen:
            continue
        seen.append(current)
        target = root / current
        if not target.is_file():
            continue
        body = strip_comments(current, target.read_text(encoding="utf-8", errors="replace"))
        for token in re.findall(r"[\w./-]+\.(?:sh|ps1|py|bat|cmd|bash)\b", body):
            for path in resolve(token, current):
                if path not in seen:
                    queue.append(path)
        for module in re.findall(r"^\s*(?:import|from)\s+([A-Za-z_]\w*)", body, re.M):
            candidate = str(Path(current).parent / f"{module}.py")
            if (candidate in all_tracked or (root / candidate).is_file()) and candidate not in seen:
                queue.append(candidate)
    return seen


_BROAD_GLOB = re.compile(r"ci/windows/(\*\*|\*)")


def check_w1_closure(root: Path) -> list[Finding]:
    findings: list[Finding] = []
    closure = local_script_closure(root, W1_WORKFLOW)

    for path in closure:
        if path.startswith(SUBTREE + "/"):
            findings.append(Finding(
                "boundary.cross-boundary-dependency",
                f"{W1_WORKFLOW} reaches {path} through its local script closure; a build "
                f"script must not read from the excluded retention subtree",
            ))
            continue
        target = root / path
        if not target.is_file():
            continue
        body = strip_shell_comments(target.read_text(encoding="utf-8", errors="replace"))
        for line_no, line in enumerate(body.splitlines(), start=1):
            if SUBTREE in line or "runtime-retention" in line:
                findings.append(Finding(
                    "boundary.build-closure-names-subtree",
                    f"{path}:{line_no} names the excluded retention subtree from inside the "
                    f"W1 build closure: {line.strip()!r}",
                ))
            if _BROAD_GLOB.search(line) and "runtime-retention" not in line:
                # A broad glob only matters where it can INGEST. `paths:` in the
                # trigger is a trigger filter and is handled by pathfilter.py,
                # which requires the ordered negation to follow it.
                if re.search(r"\b(cp|rsync|tar|zip|copy|Copy-Item|glob|iglob|rglob|find)\b", line, re.I) \
                        or re.search(r"(path|paths|src|source|include|context)\s*[:=]", line, re.I):
                    findings.append(Finding(
                        "boundary.broad-glob-can-traverse-subtree",
                        f"{path}:{line_no} uses a broad ci/windows glob that can traverse the "
                        f"excluded subtree: {line.strip()!r}",
                    ))
    return findings


def check_w1_environment(root: Path) -> list[Finding]:
    """No environment variable in the W1 workflow redirects a build input here."""
    findings: list[Finding] = []
    body = strip_yaml_comments((root / W1_WORKFLOW).read_text(encoding="utf-8"))
    for line_no, line in enumerate(body.splitlines(), start=1):
        if re.search(r"^\s*[A-Z0-9_]+\s*:\s*.*runtime-retention", line) or \
           re.search(r"(export|echo)\s+[A-Z0-9_]+=.*runtime-retention", line):
            findings.append(Finding(
                "boundary.environment-redirects-build-input",
                f"{W1_WORKFLOW}:{line_no} points a build environment variable into the "
                f"excluded subtree: {line.strip()!r}",
            ))
    return findings


def check_build_manifests(root: Path) -> list[Finding]:
    """No build manifest names a path under the subtree."""
    findings: list[Finding] = []
    for path in tracked(root, "ci"):
        if path.startswith(SUBTREE + "/"):
            continue
        if not path.endswith((".json", ".txt", ".toml", ".lock", ".yml", ".yaml", ".props", ".targets")):
            continue
        target = root / path
        try:
            body = target.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        if "runtime-retention" not in body:
            continue
        if path == RETENTION_WORKFLOW:
            continue  # the retention workflow necessarily includes the subtree
        for line_no, line in enumerate(strip_yaml_comments(body).splitlines(), start=1):
            if "runtime-retention" in line:
                findings.append(Finding(
                    "boundary.build-manifest-names-subtree",
                    f"{path}:{line_no} names a path under the excluded subtree: {line.strip()!r}",
                ))
    return findings


def check_gate_is_pinned(root: Path) -> list[Finding]:
    """The gate has to be RUN, not merely present."""
    body = (root / RETENTION_WORKFLOW).read_text(encoding="utf-8")
    if "boundary-controls.py" not in body or "boundary.py" not in body:
        return [Finding(
            "boundary.gate-not-pinned",
            f"{RETENTION_WORKFLOW} does not invoke the excluded-subtree ownership gate; "
            f"a gate that no workflow runs cannot refuse anything",
        )]
    return []


def check_all(root: Path) -> list[Finding]:
    return (
        check_inventory(root)
        + check_w1_closure(root)
        + check_w1_environment(root)
        + check_build_manifests(root)
        + check_gate_is_pinned(root)
    )


def main() -> int:
    root = repo_root()
    findings = check_all(root)
    if findings:
        print(f"W1-A4 BOUNDARY HARD STOP: {len(findings)} finding(s) under {SUBTREE}/")
        for finding in findings:
            print(f"  FAIL [{finding.prop}] {finding.message}")
        return 1
    inventory = tracked(root, SUBTREE)
    print(f"the excluded subtree holds {len(inventory)} tracked file(s), each with exactly one")
    print("permitted retention role, and the W1 build closure reaches none of them:")
    for path in inventory:
        print(f"  {INVENTORY[path[len(SUBTREE) + 1:]]:<32} {path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
