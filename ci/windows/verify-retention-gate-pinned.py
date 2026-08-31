#!/usr/bin/env python3
"""The retention gate roster is INVOKED, it is the roster we accepted, and the
thing that says so is reached.

R1 finding D3, its R2 successor, and the three R3 findings (F1, F3, F4) that
this file is the repair for. It is the external authority for five things the
retention subtree is not allowed to decide about itself:

  1. COMMAND IDENTITY. `.github/workflows/w1-windows-runtime-retention.yml`
     must run the canonical orchestrator command, as one exact YAML scalar, in
     a named job, in a named step, with nothing around it that could make it
     inert.

  2. EXECUTION ROOT (R3 finding F1). The command's EFFECTIVE execution
     contract, across every YAML scope that can change it: workflow `env`, job
     `env`, workflow and job `defaults.run`, step `env`, step `shell`, step
     `working-directory`, `if` and `continue-on-error`. `BASH_ENV` is the
     reason this exists as its own contract rather than one more forbidden
     name — see WHY THE ENVIRONMENT IS TWO RULES below.

  3. ROSTER AUTHORITY (R3 finding F3). `retention_gates.py` may report which
     gates it HAS. It may not decide which gates it MUST have. Neither may this
     file: until R4 the frozen roster was a tuple written down HERE, so the
     orchestrator and this file were a bilateral agreement and one edit to each
     weakened both. The roster is now the canonical manifest
     `ci/windows/w1a4-roster-manifest.v1.json`, and the values that
     AUTHENTICATE that manifest — its content digest and its fourteen member
     identities in order — are pinned in `ci/run.sh`, which is neither the
     orchestrator nor this file. This file consumes both and can redefine
     neither.

  4. TRUST-ROOT REACHABILITY (R3 finding F4). `ci/run.sh` must actually reach
     the block that runs this file. Structurally: the block is top-level,
     unconditional, fail-closed, and preceded by nothing but prologue. The
     dynamic half — running ci/run.sh with a stubbed verifier and requiring the
     sentinel, the failure and the un-reached build stage — is
     `gate-roster-controls.py`, because a structural claim and a dynamic claim
     are different claims and neither implies the other.

  5. PROPERTY OWNERSHIP (W1-A5-V1-R2 findings B2 and B3). Identity is not
     behaviour. A member may keep its name, its file and its position while
     silently ceasing to report a property: R2 deleted one line from
     `publication_policy.check_all` and every check above still agreed. The
     canonical manifest now records, per member, the properties it must still
     REPORT; `ci/run.sh` pins the set of obligations in `W1A4_ROSTER_PROPERTIES`
     so the manifest cannot drop one on its own; and this file DEMONSTRATES each
     one by calling the member against a witness violation, and against an
     unmodified tree, so neither a deleted property nor an unconditional one
     satisfies it. See PROPERTY OWNERSHIP below.

WHY THE COMMAND CHECK IS EQUALITY AND NOT A REGEX.

R2 decided "is this command a no-op?" with a prefix regex (`_NO_OP_PREFIX`), a
success-mask regex and a substring containment test. Every such list is a list
of the bypasses someone thought of. Measured against the R2 file, eleven
syntactically harmless wrappers were accepted as a live invocation: `#cmd`,
`##cmd`, `cmd || :`, `cmd || echo x`, `cmd &`, `if false; then cmd; fi`,
`(cmd)`, a block scalar containing the command, a padded scalar,
`working-directory:` and a `shell:` override.

So the question stops being "is this command inert?", which needs a shell
semantics model, and becomes "is this string the canonical string?", which
needs `==`. Any deviation is refused, including deviations that would in fact
have run the command. A gate step is not a place for expressive shell.

Nothing here executes a shell, and no rule depends on spacing after `#`.

WHY THE ENVIRONMENT IS TWO RULES AND NOT ONE LIST.

R3 finding F1 measured the following against the R3 file, using GitHub's own
effective template `bash --noprofile --norc -eo pipefail {0}`:

    env:
      BASH_ENV: mal.sh          # mal.sh defines `python3() { return 0; }`

bash sources `BASH_ENV` BEFORE the first line of the step's script. The
canonical command then resolved to a shell function, never ran, and the step
succeeded. Adding `BASH_ENV` to a forbidden-name list does not close that: any
check run by a shell this workflow configures is itself downstream of the same
sourcing.

So the environment is two rules with different directions.

  * FORBIDDEN, at workflow, job and `defaults.run` scope, and at step scope for
    every step: `INTERPRETER_ENV`. Nothing wider than the gate step may name a
    variable that can decide which interpreter runs or which file it reads.

  * MANDATORY, at the gate step, with exact values: `REQUIRED_STEP_ENV`. Step
    `env:` is the highest-precedence scope GitHub offers and it is applied to
    the process environment before the shell is created, so bash starts with
    `BASH_ENV` already pointing at `/dev/null` and sources nothing — whatever a
    wider scope tried to set, and whether or not the structural rule above was
    the thing that caught it.

The interpreter is ABSOLUTE (`/usr/bin/python3`), so `PATH` is not load-bearing
for the gate command and no shell function or alias can stand in for it.

And this file is launched by `ci/run.sh` through `/usr/bin/env -i`, so it does
not inherit `BASH_ENV`, `PYTHONPATH` or anything else from whatever invoked the
gate — the safe environment is established before the interpreter starts, not
argued about afterwards.

TRUST ROOT. `ci/run.sh` is where this stops. It is a merge gate for every
branch, it invokes this file unconditionally and before it dispatches anything
else, and a non-zero exit here fails it. Nothing pins `ci/run.sh` in turn: a
chain of scripts each pinning the next has no last link, and pretending
otherwise would be the same defect one level further out. This file names
ci/run.sh as its root, checks that the root really reaches it, and the chain
ends.

Exit 0 if every contract holds, 1 otherwise. Nothing is written.
"""

from __future__ import annotations

import hashlib
import importlib.util
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import yaml

WORKFLOW = ".github/workflows/w1-windows-runtime-retention.yml"
ORCHESTRATOR = "ci/windows/runtime-retention/retention_gates.py"
SUBTREE = "ci/windows/runtime-retention"
TRUST_ROOT = "ci/run.sh"
MANIFEST = "ci/windows/w1a4-roster-manifest.v1.json"

#: The job, the step and the command, exactly as they must appear.
REQUIRED_JOB = "gates"
REQUIRED_STEP_ID = "retention-gate-roster"
CANONICAL_COMMAND = "/usr/bin/python3 ci/windows/runtime-retention/retention_gates.py --validate"

#: The one shell an invocation may declare. GitHub expands `bash` to
#: `bash --noprofile --norc -eo pipefail {0}`, which fails fast; `sh` does not,
#: and neither does `pwsh` or a custom command template.
PERMITTED_SHELL = "bash"

#: The one working directory the gate step may resolve against. GitHub's own
#: default is the workspace root and the canonical command names a
#: repository-relative path, so the only permitted state is "not declared".
PERMITTED_WORKING_DIRECTORY = None

#: Environment names that can change which interpreter runs, which file it
#: reads as the script, or what a shell executes before the script begins.
#: Forbidden at every scope wider than the gate step, and at every step.
INTERPRETER_ENV = frozenset({
    # Shell startup interception. BASH_ENV is R3 finding F1.
    "BASH_ENV", "ENV", "SHELLOPTS", "BASHOPTS", "BASH_XTRACEFD", "PS4",
    # Interpreter resolution.
    "PATH", "LD_PRELOAD", "LD_LIBRARY_PATH", "VIRTUAL_ENV", "CONDA_PREFIX",
    # Python startup and import redirection.
    "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "PYTHONEXECUTABLE",
    "PYTHONSAFEPATH", "PYTHONNOUSERSITE", "PYTHONWARNINGS", "PYTHONMALLOC",
    "PYTHONINSPECT", "PYTHONUSERBASE",
})

#: What the gate step MUST declare, with these exact values. This is the
#: runtime half of the F1 repair: step `env:` is the highest-precedence scope
#: and is applied before the shell is created.
#:
#: `/dev/null` rather than `''` for the two startup files, because an empty
#: BASH_ENV is a value a reader has to reason about and an existing empty file
#: is not. `''` for PYTHONSAFEPATH rather than `1`: the orchestrator does
#: `import boundary`, which resolves through the script's own directory, and
#: `PYTHONSAFEPATH=1` removes it — a "hardening" that would stop the validator
#: importing at all. Measured, not assumed.
REQUIRED_STEP_ENV: dict[str, str] = {
    "BASH_ENV": "/dev/null",
    "ENV": "/dev/null",
    "PYTHONSTARTUP": "/dev/null",
    "PYTHONPATH": "",
    "PYTHONHOME": "",
    "PYTHONEXECUTABLE": "",
    "PYTHONSAFEPATH": "",
    "PYTHONNOUSERSITE": "",
    "PYTHONWARNINGS": "",
    "LD_PRELOAD": "",
    "LD_LIBRARY_PATH": "",
    "VIRTUAL_ENV": "",
    "CONDA_PREFIX": "",
}

#: The path filters that must reach the job, so a change to the subtree, to the
#: workflow, to this file, to the canonical manifest or to the trust root
#: cannot land without running it.
REQUIRED_TRIGGER_PATHS = (
    f"{SUBTREE}/**",
    WORKFLOW,
    # W1-A5-V1-R3. `publication.frozen-workflow-drift` pins this file as bytes,
    # so a pull request that edits ONLY the publication workflow is exactly the
    # change the pin exists to refuse, and it must not be able to skip the job
    # that evaluates it. The retention workflow already listed it; until R3
    # nothing required it to keep listing it.
    ".github/workflows/w1-windows-runtime-publish.yml",
    "ci/windows/verify-retention-gate-pinned.py",
    MANIFEST,
    TRUST_ROOT,
)


# ── a YAML loader that refuses an ambiguous document ────────────────────────
class StrictLoader(yaml.SafeLoader):
    """A duplicate mapping key is refused because THIS repository requires a
    unique one, not because of any claim about another parser.

    W1-A5-V1-R5 corrects the rationale this docstring used to carry. Measured on
    PyYAML 6.0.1, `yaml.SafeLoader` MAY resolve a duplicate mapping key
    last-wins and silently; this loader raises `ConstructorError` on the same
    document. What GitHub's own parser does with a duplicate key was never
    established by any review in this series and is NOT asserted here.

    The reason to refuse is that a decision taken from a last-wins tree is a
    decision about one of two readings of an ambiguous file, and a gate may not
    silently pick one. Refusing is fail-closed under every parser, including the
    ones nobody here has measured, which is exactly where a second `env:` or a
    second `run:` would otherwise hide."""


def _no_duplicate_keys(loader, node, deep=False):
    mapping = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=deep)
        if key in mapping:
            raise yaml.constructor.ConstructorError(
                "while constructing a mapping", node.start_mark,
                f"found a duplicate key {key!r}; this repository requires unique "
                f"mapping keys, because a decision taken from one of two readings of "
                f"an ambiguous document is not a decision about the file",
                key_node.start_mark)
        mapping[key] = loader.construct_object(value_node, deep=deep)
    return mapping


StrictLoader.add_constructor(
    yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, _no_duplicate_keys)


class Entry:
    """One authoritative roster member. Position is part of it."""

    __slots__ = ("gate_id", "module", "callable_name", "kind", "argv", "tier",
                 "properties")

    def __init__(self, gate_id: str, module: str, callable_name: str, kind: str,
                 argv: tuple[str, ...], tier: str,
                 properties: tuple[str, ...] = ()) -> None:
        self.gate_id = gate_id
        self.module = module
        self.callable_name = callable_name
        self.kind = kind
        self.argv = argv
        self.tier = tier
        #: The named properties this member must still REPORT. Identity is not
        #: behaviour: W1-A5-V1-R2 finding B2 deleted one line — the call to
        #: `check_summary_identity` inside `publication_policy.check_all` — and
        #: every identity check here, and every control in the subtree, stayed
        #: green. Finding B3 co-removed the call, the implementation and the
        #: controls and the roster still passed 14/14, because the property was
        #: declared only by the file that implemented it.
        self.properties = properties

    @property
    def identity(self) -> tuple[str, str, str, tuple[str, ...], str]:
        return (self.module, self.callable_name, self.kind, self.argv, self.tier)

    def __repr__(self) -> str:  # pragma: no cover - diagnostics only
        return (f"{self.gate_id} = {self.tier}:{self.module}::{self.callable_name}"
                f"[{self.kind}]{list(self.argv)}")


#: Populated from the canonical manifest by `install_roster`. Deliberately NOT
#: a literal: a roster written down here is a roster this file can edit, and
#: R3 finding F3 is that editing it here and in the orchestrator together
#: leaves every check agreeing with every other check.
EXPECTED_ROSTER: tuple[Entry, ...] = ()
EXPECTED_BY_ID: dict[str, Entry] = {}


class Finding:
    __slots__ = ("prop", "message")

    def __init__(self, prop: str, message: str) -> None:
        self.prop, self.message = prop, message


def repo_root() -> Path:
    out = subprocess.run(
        ["git", "-C", str(Path(__file__).resolve().parent), "rev-parse", "--show-toplevel"],
        check=True, capture_output=True, text=True,
    )
    return Path(out.stdout.strip())


# ── the independent anchor ──────────────────────────────────────────────────
_PIN_DIGEST = re.compile(
    r'^W1A4_ROSTER_MANIFEST_SHA256="([0-9a-f]{64})"$', re.MULTILINE)
_PIN_PATH = re.compile(r'^W1A4_ROSTER_MANIFEST="([^"\n]+)"$', re.MULTILINE)
_PIN_IDS = re.compile(r'^W1A4_ROSTER_IDS=\(\n((?:[ \t]*[A-Za-z0-9._-]+\n)+)\)$',
                      re.MULTILINE)
_PIN_PROPERTIES = re.compile(
    r'^W1A4_ROSTER_PROPERTIES=\(\n((?:[ \t]*[A-Za-z0-9._-]+\n)+)\)$', re.MULTILINE)


class Anchor:
    """What `ci/run.sh` pins, read out of `ci/run.sh`.

    This file cannot redefine any of it. That is the whole point: the roster
    lives in a manifest, the manifest is authenticated from the trust root, and
    the verifier is a consumer of both.
    """

    __slots__ = ("manifest_path", "digest", "ids", "properties")

    def __init__(self, manifest_path: str, digest: str, ids: tuple[str, ...],
                 properties: tuple[str, ...]) -> None:
        self.manifest_path = manifest_path
        self.digest = digest
        self.ids = ids
        #: Every property obligation the manifest may carry, in manifest order.
        #: W1-A5-V1-R2 finding B3: an obligation that the file owing it can
        #: delete is not an obligation, and neither is one the manifest alone
        #: can drop, because the manifest is a data file this contract reads.
        self.properties = properties


def read_anchor(root: Path) -> tuple[Anchor | None, list[Finding]]:
    path = root / TRUST_ROOT
    if not path.is_file():
        return None, [Finding("anchor.trust-root-missing",
                              f"{TRUST_ROOT} does not exist; there is no authority to read")]
    if path.is_symlink():
        return None, [Finding("anchor.trust-root-symlinked",
                              f"{TRUST_ROOT} is a symbolic link")]
    text = path.read_text(encoding="utf-8")
    findings: list[Finding] = []

    match_path = _PIN_PATH.search(text)
    match_digest = _PIN_DIGEST.search(text)
    match_ids = _PIN_IDS.search(text)
    match_properties = _PIN_PROPERTIES.search(text)
    if match_path is None:
        findings.append(Finding(
            "anchor.manifest-unpinned",
            f"{TRUST_ROOT} declares no top-level `W1A4_ROSTER_MANIFEST=\"...\"`; the "
            f"canonical roster manifest is not named by the trust root"))
    if match_digest is None:
        findings.append(Finding(
            "anchor.digest-unpinned",
            f"{TRUST_ROOT} declares no top-level "
            f"`W1A4_ROSTER_MANIFEST_SHA256=\"<64 hex>\"`; without it the manifest is a "
            f"file anyone may rewrite, and this file would be authenticating it against "
            f"a value it holds itself"))
    if match_properties is None:
        findings.append(Finding(
            "anchor.properties-unpinned",
            f"{TRUST_ROOT} declares no top-level `W1A4_ROSTER_PROPERTIES=( ... )` array; "
            f"without it the manifest could drop a member's obligation and re-pin its own "
            f"digest in one edit to a data file"))
    if match_ids is None:
        findings.append(Finding(
            "anchor.ids-unpinned",
            f"{TRUST_ROOT} declares no top-level `W1A4_ROSTER_IDS=( ... )` array; the "
            f"member identities are what lets a missing member be NAMED rather than "
            f"merely detected as a digest that no longer matches"))
    if findings:
        return None, findings

    ids = tuple(line.strip() for line in match_ids.group(1).splitlines() if line.strip())
    properties = tuple(line.strip() for line in match_properties.group(1).splitlines()
                       if line.strip())
    if len(set(ids)) != len(ids):
        duplicates = sorted({i for i in ids if ids.count(i) > 1})
        findings.append(Finding(
            "anchor.ids-duplicated",
            f"{TRUST_ROOT} names {duplicates} more than once in W1A4_ROSTER_IDS"))
        return None, findings
    if len(set(properties)) != len(properties):
        repeated = sorted({p for p in properties if properties.count(p) > 1})
        return None, findings + [Finding(
            "anchor.properties-duplicated",
            f"{TRUST_ROOT} names {repeated} more than once in W1A4_ROSTER_PROPERTIES")]
    return Anchor(match_path.group(1), match_digest.group(1), ids, properties), []


def load_manifest(root: Path, anchor: Anchor) -> tuple[tuple[Entry, ...], list[Finding]]:
    """The canonical roster, authenticated against the trust root before use."""
    path = root / anchor.manifest_path
    if not path.is_file():
        return (), [Finding("anchor.manifest-missing",
                            f"{anchor.manifest_path}, which {TRUST_ROOT} pins, does not exist")]
    if path.is_symlink():
        return (), [Finding("anchor.manifest-symlinked",
                            f"{anchor.manifest_path} is a symbolic link")]

    raw = path.read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    unauthenticated: list[Finding] = []
    if digest != anchor.digest:
        # NOT a return. A digest mismatch says the manifest is not the accepted
        # one; it does not say WHICH members went missing, and "the three
        # identities are named" is a property this contract owes the reviewer.
        # So the mismatch is recorded and the comparison against the identities
        # `ci/run.sh` pins runs anyway. Nothing is installed either way.
        unauthenticated.append(Finding(
            "anchor.manifest-digest",
            f"{anchor.manifest_path} hashes to {digest}; {TRUST_ROOT} pins "
            f"{anchor.digest}. The canonical roster was changed without the authority "
            f"that authenticates it, so the manifest is not evidence of anything"))

    try:
        doc = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        return (), unauthenticated + [Finding(
            "anchor.manifest-unparseable", f"{anchor.manifest_path} is not JSON: {error}")]
    if not isinstance(doc, dict) or doc.get("schema") != "tesserafin.w1a4.roster-manifest":
        return (), unauthenticated + [Finding(
            "anchor.manifest-schema",
            f"{anchor.manifest_path} is not a W1-A4 roster manifest")]
    if doc.get("version") != 1:
        return (), unauthenticated + [Finding(
            "anchor.manifest-schema",
            f"{anchor.manifest_path} declares version {doc.get('version')!r}; "
            f"this file understands version 1")]
    rows = doc.get("roster")
    if not isinstance(rows, list) or not rows:
        return (), unauthenticated + [Finding(
            "anchor.manifest-schema", f"{anchor.manifest_path} carries no `roster` array")]

    entries: list[Entry] = []
    findings: list[Finding] = list(unauthenticated)
    for index, row in enumerate(rows):
        if not isinstance(row, dict):
            findings.append(Finding("anchor.manifest-schema",
                                    f"{anchor.manifest_path} roster[{index}] is not an object"))
            continue
        missing = [k for k in ("position", "id", "module", "callable", "kind", "argv",
                               "tier", "properties")
                   if k not in row]
        if missing:
            findings.append(Finding(
                "anchor.manifest-schema",
                f"{anchor.manifest_path} roster[{index}] omits {missing}; the contract is "
                f"id, module, callable, kind, argv, tier, properties and position"))
            continue
        if row["position"] != index:
            findings.append(Finding(
                "anchor.manifest-schema",
                f"{anchor.manifest_path} roster[{index}] declares position "
                f"{row['position']!r}; position is the index and is part of the contract"))
            continue
        if not isinstance(row["argv"], list) or \
                not all(isinstance(a, str) for a in row["argv"]):
            findings.append(Finding("anchor.manifest-schema",
                                    f"{anchor.manifest_path} roster[{index}] has a non-string "
                                    f"argv"))
            continue
        if not isinstance(row["properties"], list) or \
                not all(isinstance(a, str) for a in row["properties"]):
            findings.append(Finding("anchor.manifest-schema",
                                    f"{anchor.manifest_path} roster[{index}] has a non-string "
                                    f"properties list"))
            continue
        unknown = [name for name in row["properties"] if name not in WITNESSES]
        if unknown:
            findings.append(Finding(
                "anchor.manifest-witnessless",
                f"{anchor.manifest_path} roster[{index}] declares {unknown}, for which this "
                f"file holds no witness violation. A property nothing can demonstrate is a "
                f"name, not an obligation"))
            continue
        entries.append(Entry(row["id"], row["module"], row["callable"], row["kind"],
                             tuple(row["argv"]), row["tier"],
                             tuple(row["properties"])))
    if any(f.prop != "anchor.manifest-digest" for f in findings):
        return (), findings

    # The manifest agrees with the identities the trust root names, in order.
    # This is what turns "the digest no longer matches" into "these three
    # members are gone, by name".
    manifest_ids = tuple(e.gate_id for e in entries)
    if manifest_ids != anchor.ids:
        absent = [i for i in anchor.ids if i not in manifest_ids]
        extra = [i for i in manifest_ids if i not in anchor.ids]
        detail = []
        if absent:
            detail.append(f"{TRUST_ROOT} requires {absent}, which the manifest no longer "
                          f"carries")
        if extra:
            detail.append(f"the manifest carries {extra}, which {TRUST_ROOT} does not name")
        if not detail:
            first = next(i for i, (a, b) in enumerate(zip(manifest_ids, anchor.ids)) if a != b)
            detail.append(f"the same members in a different order, first differing at "
                          f"position {first}: manifest has {manifest_ids[first]!r} where "
                          f"{TRUST_ROOT} has {anchor.ids[first]!r}")
        return (), findings + [Finding(
            "anchor.manifest-roster-drift",
            f"{anchor.manifest_path} does not carry the roster {TRUST_ROOT} pins: "
            + "; ".join(detail))]

    # The obligations the manifest carries are the obligations the trust root
    # pins, in the same order. W1-A5-V1-R2 finding B3's residue: without this,
    # deleting `"properties": [...]` from a member and re-pinning the manifest
    # digest is one edit to two data files and removes the obligation silently.
    manifest_properties = tuple(name for e in entries for name in e.properties)
    if manifest_properties != anchor.properties:
        absent = [p for p in anchor.properties if p not in manifest_properties]
        extra = [p for p in manifest_properties if p not in anchor.properties]
        detail = []
        if absent:
            detail.append(f"{TRUST_ROOT} requires {absent}, which no member in the manifest "
                          f"still owes")
        if extra:
            detail.append(f"the manifest obliges {extra}, which {TRUST_ROOT} does not name")
        if not detail:
            detail.append(f"the same obligations in a different order: manifest has "
                          f"{list(manifest_properties)}, {TRUST_ROOT} has "
                          f"{list(anchor.properties)}")
        return (), findings + [Finding(
            "anchor.properties-drift",
            f"{anchor.manifest_path} does not carry the property obligations "
            f"{TRUST_ROOT} pins: " + "; ".join(detail))]
    if findings:
        return (), findings
    return tuple(entries), []


def install_roster(root: Path) -> list[Finding]:
    """Resolve the authoritative roster from the anchor. Idempotent."""
    global EXPECTED_ROSTER, EXPECTED_BY_ID
    anchor, findings = read_anchor(root)
    if anchor is None:
        EXPECTED_ROSTER, EXPECTED_BY_ID = (), {}
        return findings
    entries, more = load_manifest(root, anchor)
    if more:
        EXPECTED_ROSTER, EXPECTED_BY_ID = (), {}
        return more
    EXPECTED_ROSTER = entries
    EXPECTED_BY_ID = {e.gate_id: e for e in entries}
    return []


# ── trust-root reachability ─────────────────────────────────────────────────
PIN_BLOCK_START = "# >>> W1A4-PIN-BLOCK"
PIN_BLOCK_END = "# <<< W1A4-PIN-BLOCK"

#: The only line shapes permitted BEFORE the pin block. Anything else — an
#: `exit`, a `return`, a conditional, a loop, a subshell, `set +e`, a test
#: dispatch, a status assignment — is refused by name rather than modelled.
#: This is deliberately an allowlist: a denylist of relocations is a list of
#: the relocations someone thought of, which is the shape of defect R2 shipped.
_PROLOGUE_SHEBANG = re.compile(r"^#!/")
_PROLOGUE_SET = re.compile(r"^set -[a-zA-Z]+( -[a-zA-Z]+)*( [a-z]+)*$")
_PROLOGUE_ASSIGN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")
_PROLOGUE_ARRAY_OPEN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=\($")
_PROLOGUE_ARRAY_ITEM = re.compile(r"^[A-Za-z0-9._/-]+$")
_PROLOGUE_CD = re.compile(r'^cd "?\$')
_PROLOGUE_SOURCE = re.compile(r"^(source|\.) ")
_PROLOGUE_FUNCTION = re.compile(r"^[A-Za-z_][A-Za-z0-9_-]*\(\) \{$")


def _prologue_findings(lines: list[str]) -> list[Finding]:
    """Every line before the block is prologue, or this is a finding."""
    findings: list[Finding] = []
    in_function = False
    in_array = False
    open_function = 0
    open_array = 0
    for number, raw in enumerate(lines, start=1):
        line = raw.rstrip("\n")
        stripped = line.strip()
        if in_function:
            if line == "}":
                in_function = False
            continue
        if in_array:
            if line.rstrip() == ")":
                in_array = False
            elif not _PROLOGUE_ARRAY_ITEM.match(stripped):
                findings.append(Finding(
                    "trustroot.pin-block-not-top-level",
                    f"{TRUST_ROOT}:{number} is inside an array literal and is not a bare "
                    f"word: {stripped!r}"))
            continue
        if not stripped or stripped.startswith("#"):
            if _PROLOGUE_SHEBANG.match(line):
                continue
            continue
        if line != stripped:
            findings.append(Finding(
                "trustroot.pin-block-not-top-level",
                f"{TRUST_ROOT}:{number} is indented ({stripped!r}), so it is nested inside "
                f"something, and the pin block that follows it is nested too"))
            continue
        if "set +" in stripped and stripped.startswith("set "):
            findings.append(Finding(
                "trustroot.errexit-disabled-before-pin",
                f"{TRUST_ROOT}:{number} disables a shell option before the pin block: "
                f"{stripped!r}. With errexit off, a failure between here and the block is "
                f"not a failure, and the block's own guarantees start later than they read"))
            continue
        if _PROLOGUE_FUNCTION.match(line):
            in_function = True
            open_function = number
            continue
        if _PROLOGUE_ARRAY_OPEN.match(line):
            in_array = True
            open_array = number
            continue
        if (_PROLOGUE_SET.match(line) or _PROLOGUE_ASSIGN.match(line)
                or _PROLOGUE_CD.match(line) or _PROLOGUE_SOURCE.match(line)):
            if "set +" in line:
                findings.append(Finding(
                    "trustroot.errexit-disabled-before-pin",
                    f"{TRUST_ROOT}:{number} disables a shell option before the pin block: "
                    f"{stripped!r}"))
            continue
        findings.append(Finding(
            "trustroot.pin-block-not-top-level",
            f"{TRUST_ROOT}:{number} runs {stripped!r} before the pin block. Only comments, "
            f"blank lines, `set`, simple assignments, `cd`, `source` and closed function "
            f"definitions may precede it, so that no reachable path through {TRUST_ROOT} "
            f"— an early exit, a conditional, a subshell, a function that is never called, "
            f"a status that is aggregated later — can skip it"))
    if in_function:
        findings.append(Finding(
            "trustroot.pin-block-not-top-level",
            f"{TRUST_ROOT}:{open_function} opens a function that is still open where the "
            f"pin block begins, so the block is a function BODY. A function nobody calls "
            f"runs nothing, and this contract cannot tell whether anybody calls it"))
    if in_array:
        findings.append(Finding(
            "trustroot.pin-block-not-top-level",
            f"{TRUST_ROOT}:{open_array} opens an array literal that is still open where "
            f"the pin block begins"))
    return findings


#: What the block itself may not contain. A block that runs and cannot fail is
#: not a gate.
_BLOCK_MASKING = (
    ("|| true", "tolerates its own failure"),
    ("|| :", "tolerates its own failure"),
    ("set +e", "disables errexit inside the gate"),
    ("set +o errexit", "disables errexit inside the gate"),
)


def check_trust_root(root: Path) -> list[Finding]:
    """`ci/run.sh` reaches the pin block, and the pin block is fail-closed.

    R3 finding F4: the block used to live after `STATUS=$?`, and the only proof
    that it ran was a control that extracted the block and executed the
    extracted text. Moving the intact block below `exit "$STATUS"` passed that
    control unchanged, because extracting a block says nothing about whether
    the script reaches it.
    """
    path = root / TRUST_ROOT
    if not path.is_file():
        return [Finding("trustroot.missing", f"{TRUST_ROOT} does not exist")]
    if path.is_symlink():
        return [Finding("trustroot.symlinked", f"{TRUST_ROOT} is a symbolic link")]

    text = path.read_text(encoding="utf-8")
    starts = [i for i, l in enumerate(text.splitlines()) if l.strip() == PIN_BLOCK_START]
    ends = [i for i, l in enumerate(text.splitlines()) if l.strip() == PIN_BLOCK_END]
    if len(starts) != 1 or len(ends) != 1:
        return [Finding(
            "trustroot.pin-block-missing",
            f"{TRUST_ROOT} carries {len(starts)} `{PIN_BLOCK_START}` marker(s) and "
            f"{len(ends)} `{PIN_BLOCK_END}` marker(s); exactly one of each is the "
            f"contract. A block moved into a file that is sourced but never invoked, or "
            f"deleted outright, is this finding")]
    start, end = starts[0], ends[0]
    if end <= start:
        return [Finding("trustroot.pin-block-missing",
                        f"{TRUST_ROOT} closes the pin block before it opens it")]

    lines = text.splitlines()
    findings = _prologue_findings(lines[:start])

    body = lines[start + 1:end]
    for number, line in enumerate(body, start=start + 2):
        if line and not line[0].isspace():
            continue
        if line.strip() and line != line.strip() and not line.startswith(" " * 4):
            findings.append(Finding(
                "trustroot.pin-block-not-top-level",
                f"{TRUST_ROOT}:{number} is indented inside the pin block in a way that is "
                f"not a conditional body: {line.strip()!r}"))
    joined = "\n".join(body)

    for needle, why in _BLOCK_MASKING:
        if needle in joined:
            findings.append(Finding(
                "trustroot.pin-block-masked",
                f"the pin block contains {needle!r}, which {why}"))
    if re.search(r"(?m)&\s*$", joined):
        findings.append(Finding(
            "trustroot.pin-block-masked",
            "the pin block backgrounds a command; a backgrounded gate reports the shell's "
            "status, not its own"))
    if re.search(r"(?m)^\s*STATUS=", joined):
        findings.append(Finding(
            "trustroot.pin-block-masked",
            "the pin block assigns to STATUS; an accumulated status is replaced by a later "
            "stage, and this gate must be fail-closed on its own"))
    if not re.search(r"(?m)^\s*exit 1$", joined):
        findings.append(Finding(
            "trustroot.pin-block-not-fail-closed",
            "the pin block never runs `exit 1`. Folding its verdict into an accumulated "
            "status would let a later stage reassign it, and $STATUS does not exist yet at "
            "this point in the script"))
    if "/usr/bin/env -i" not in joined:
        findings.append(Finding(
            "trustroot.pin-block-environment",
            "the pin block does not launch the verifier through `/usr/bin/env -i`; an "
            "inherited BASH_ENV, PYTHONPATH or PATH would then decide what the "
            "structural check actually is"))
    if "/usr/bin/python3" not in joined or \
            "ci/windows/verify-retention-gate-pinned.py" not in joined:
        findings.append(Finding(
            "trustroot.pin-block-does-not-invoke",
            "the pin block does not invoke ci/windows/verify-retention-gate-pinned.py "
            "with an absolute interpreter"))
    if "W1A4-PIN-BLOCK-REACHED" not in joined:
        findings.append(Finding(
            "trustroot.pin-block-no-sentinel",
            "the pin block prints no W1A4-PIN-BLOCK-REACHED sentinel, so a dynamic run "
            "cannot tell that it was reached rather than merely present"))

    after = "\n".join(lines[end + 1:])
    if PIN_BLOCK_START in after or PIN_BLOCK_END in after:
        findings.append(Finding("trustroot.pin-block-missing",
                                f"{TRUST_ROOT} repeats a pin-block marker after the block"))
    return findings


# ── command identity and the execution root ─────────────────────────────────
def _env_findings(where: str, env, prop: str) -> list[Finding]:
    if env is None:
        return []
    if not isinstance(env, dict):
        return [Finding(prop, f"{where} declares a non-mapping `env:`, which cannot be read")]
    offending = sorted(set(env) & INTERPRETER_ENV)
    if offending:
        return [Finding(
            prop,
            f"{where} sets {offending} — an environment that can change which python3 runs, "
            f"which file it reads, or what the shell executes before the command begins. "
            f"BASH_ENV alone is enough: bash sources it before the first line of the "
            f"step's script, so a function defined there answers to the command's name")]
    return []


def _defaults_findings(where: str, defaults, prop_prefix: str) -> list[Finding]:
    """`defaults.run` is inherited by every step that does not override it.

    R3 finding F2: the step-level rules said nothing about this, so
    `defaults: {run: {shell: sh}}` at workflow or job scope changed the shell
    the canonical command ran under while the step itself stayed byte-identical.
    """
    if defaults is None:
        return []
    if not isinstance(defaults, dict):
        return [Finding(f"{prop_prefix}-shape",
                        f"{where} declares a non-mapping `defaults:`")]
    run = defaults.get("run")
    if run is None:
        return []
    if not isinstance(run, dict):
        return [Finding(f"{prop_prefix}-shape",
                        f"{where} declares a non-mapping `defaults.run:`")]
    findings: list[Finding] = []
    if "shell" in run and run["shell"] != PERMITTED_SHELL:
        findings.append(Finding(
            f"{prop_prefix}-shell",
            f"{where} sets `defaults.run.shell: {run['shell']!r}`, which every step that "
            f"does not override it inherits. The one permitted effective shell is "
            f"{PERMITTED_SHELL!r}, which GitHub expands with `-eo pipefail`"))
    if "working-directory" in run:
        findings.append(Finding(
            f"{prop_prefix}-working-directory",
            f"{where} sets `defaults.run.working-directory: "
            f"{run['working-directory']!r}`, which every step that does not override it "
            f"inherits. The canonical command names a repository-relative path and must "
            f"resolve it against the checkout root"))
    for key in sorted(set(run) - {"shell", "working-directory"}):
        findings.append(Finding(
            f"{prop_prefix}-unknown",
            f"{where} sets an unrecognised `defaults.run.{key}`; this contract enumerates "
            f"what may be inherited and refuses what it cannot reason about"))
    return findings


def _step_env_findings(label: str, env) -> list[Finding]:
    """The gate step declares the neutralising environment, exactly."""
    if env is None:
        return [Finding(
            "cmd.step-env-absent",
            f"{label} declares no `env:`. The step environment is the highest-precedence "
            f"scope GitHub offers and is applied before the shell is created, so it is "
            f"where BASH_ENV is forced to /dev/null. Without it a wider scope decides what "
            f"bash sources before the command runs")]
    if not isinstance(env, dict):
        return [Finding("cmd.step-env-shape",
                        f"{label} declares a non-mapping `env:`")]
    findings: list[Finding] = []
    for name, value in REQUIRED_STEP_ENV.items():
        if name not in env:
            findings.append(Finding(
                "cmd.step-env-incomplete",
                f"{label} does not neutralise {name}; the required value is {value!r}"))
            continue
        actual = env[name]
        if actual is None:
            actual = ""
        if not isinstance(actual, str) or actual != value:
            findings.append(Finding(
                "cmd.step-env-not-neutral",
                f"{label} sets {name} to {env[name]!r}; the one permitted value is "
                f"{value!r}. A neutralising variable set to anything else is the attack it "
                f"exists to stop"))
    extra = sorted(set(env) - set(REQUIRED_STEP_ENV))
    if extra:
        findings.append(Finding(
            "cmd.step-env-extra",
            f"{label} declares {extra} beside the neutralising set. The gate step's "
            f"environment is closed: a name this contract does not enumerate is a name "
            f"nobody has reasoned about"))
    return findings


def check_command(root: Path) -> list[Finding]:
    """The canonical command is this step's `run`, byte for byte, under one
    effective execution contract. No shell runs."""
    findings: list[Finding] = []
    path = root / WORKFLOW
    if not path.is_file():
        return [Finding("cmd.workflow-missing", f"{WORKFLOW} does not exist")]
    if path.is_symlink():
        return [Finding("cmd.workflow-symlinked", f"{WORKFLOW} is a symbolic link")]

    try:
        doc = yaml.load(path.read_text(encoding="utf-8"), Loader=StrictLoader)
    except yaml.YAMLError as error:
        return [Finding("cmd.workflow-unparseable", f"{WORKFLOW} is not valid YAML: {error}")]
    if not isinstance(doc, dict) or not isinstance(doc.get("jobs"), dict):
        return [Finding("cmd.workflow-unparseable", f"{WORKFLOW} has no `jobs:` mapping")]

    # Reachability. PyYAML reads the bare key `on` as the boolean True.
    triggers = doc.get("on", doc.get(True))
    if not isinstance(triggers, dict) or "pull_request" not in triggers:
        findings.append(Finding(
            "cmd.trigger-missing",
            f"{WORKFLOW} does not declare a `pull_request:` trigger, so the job the gate "
            f"lives in is not reachable from a pull request"))
    else:
        pull_request = triggers["pull_request"] or {}
        paths = pull_request.get("paths") if isinstance(pull_request, dict) else None
        if not isinstance(paths, list):
            findings.append(Finding(
                "cmd.trigger-paths-missing",
                f"{WORKFLOW} declares `pull_request:` with no `paths:` allowlist"))
        else:
            absent = [p for p in REQUIRED_TRIGGER_PATHS if p not in paths]
            if absent:
                findings.append(Finding(
                    "cmd.trigger-paths-narrowed",
                    f"{WORKFLOW} no longer triggers on {absent}; a change there would not "
                    f"start the job the gate runs in"))

    findings.extend(_env_findings(
        f"{WORKFLOW} workflow-level `env:`", doc.get("env"), "cmd.workflow-env"))
    findings.extend(_defaults_findings(
        f"{WORKFLOW} workflow-level `defaults:`", doc.get("defaults"),
        "cmd.workflow-defaults"))

    jobs = doc["jobs"]
    job = jobs.get(REQUIRED_JOB)
    if not isinstance(job, dict):
        return findings + [Finding(
            "cmd.job-missing",
            f"{WORKFLOW} has no job `{REQUIRED_JOB}`; the roster is invoked from that job "
            f"and nowhere else. Jobs present: {sorted(jobs)}")]

    if "if" in job:
        findings.append(Finding(
            "cmd.job-conditional",
            f"job `{REQUIRED_JOB}` carries `if: {job['if']!r}`. A gate job runs or it is "
            f"not a gate; no condition is permitted, true ones included"))
    if "continue-on-error" in job:
        findings.append(Finding(
            "cmd.job-continue-on-error",
            f"job `{REQUIRED_JOB}` declares continue-on-error; a failure that does not fail "
            f"the run is not a refusal"))
    if "strategy" in job:
        findings.append(Finding(
            "cmd.job-matrix",
            f"job `{REQUIRED_JOB}` declares a `strategy:`; a matrix can vary the shell, the "
            f"environment and the working directory under one unchanged command"))
    findings.extend(_env_findings(f"job `{REQUIRED_JOB}` `env:`", job.get("env"),
                                  "cmd.job-env"))
    findings.extend(_defaults_findings(f"job `{REQUIRED_JOB}` `defaults:`",
                                       job.get("defaults"), "cmd.job-defaults"))
    for needed in (job.get("needs") or []) if isinstance(job.get("needs"), list) else \
            ([job["needs"]] if isinstance(job.get("needs"), str) else []):
        upstream = jobs.get(needed)
        if not isinstance(upstream, dict):
            findings.append(Finding(
                "cmd.job-needs-missing",
                f"job `{REQUIRED_JOB}` needs `{needed}`, which is not a job"))
        elif "if" in upstream or upstream.get("continue-on-error"):
            findings.append(Finding(
                "cmd.job-needs-skippable",
                f"job `{REQUIRED_JOB}` needs `{needed}`, which is conditional or tolerated; "
                f"a skipped dependency skips the gate"))

    steps = job.get("steps")
    if not isinstance(steps, list):
        return findings + [Finding(
            "cmd.job-has-no-steps", f"job `{REQUIRED_JOB}` declares no steps")]

    # Every step in the job, not only the gate step: a preceding step that sets
    # BASH_ENV in the process environment through GITHUB_ENV would reach the
    # gate step, and a step-scoped interpreter variable anywhere in this job is
    # a thing nobody needs.
    for index, other in enumerate(steps):
        if not isinstance(other, dict) or other.get("id") == REQUIRED_STEP_ID:
            continue
        findings.extend(_env_findings(
            f"job `{REQUIRED_JOB}` step {index} ({other.get('name', '<unnamed>')!r})",
            other.get("env"), "cmd.sibling-step-env"))

    identified = [(i, s) for i, s in enumerate(steps)
                  if isinstance(s, dict) and s.get("id") == REQUIRED_STEP_ID]
    if not identified:
        return findings + [Finding(
            "cmd.step-missing",
            f"job `{REQUIRED_JOB}` has no step with `id: {REQUIRED_STEP_ID}`. The step is "
            f"named by id and not by position or prose, so commenting it out, renaming it "
            f"or moving it elsewhere is this finding and not a silent pass")]
    if len(identified) > 1:
        findings.append(Finding(
            "cmd.step-duplicated",
            f"job `{REQUIRED_JOB}` declares `id: {REQUIRED_STEP_ID}` {len(identified)} "
            f"times; which one is the gate is then a matter of opinion"))

    index, step = identified[0]
    label = f"job `{REQUIRED_JOB}` step {index} (`{REQUIRED_STEP_ID}`)"

    if "uses" in step:
        findings.append(Finding(
            "cmd.step-uses-action",
            f"{label} is an action invocation, not the canonical command"))

    run = step.get("run")
    if not isinstance(run, str):
        findings.append(Finding(
            "cmd.run-missing",
            f"{label} has no `run:` string; its keys are {sorted(step)}"))
    elif run != CANONICAL_COMMAND:
        findings.append(Finding(
            "cmd.run-not-canonical",
            f"{label} runs {run!r}. The contract is EQUALITY with "
            f"{CANONICAL_COMMAND!r} — one plain scalar, an ABSOLUTE interpreter, no "
            f"wrapper, no composition, no padding, no trailing newline. A deviation that "
            f"would still run the command is refused too: deciding that would take a "
            f"shell, and this decides it with `==`"))

    if "if" in step:
        findings.append(Finding(
            "cmd.step-conditional",
            f"{label} carries `if: {step['if']!r}`; the gate step is unconditional"))
    if "continue-on-error" in step:
        findings.append(Finding(
            "cmd.step-continue-on-error",
            f"{label} declares continue-on-error; the gate's refusal would not fail the run"))
    if "working-directory" in step:
        findings.append(Finding(
            "cmd.step-working-directory",
            f"{label} sets `working-directory: {step['working-directory']!r}`; the command "
            f"names a repository-relative path and resolves it against the checkout root. "
            f"The one permitted effective value is the workspace root, which is what "
            f"declaring nothing means"))
    if "shell" not in step:
        findings.append(Finding(
            "cmd.step-shell-absent",
            f"{label} declares no `shell:`. The effective shell would then be whatever "
            f"`defaults.run.shell` resolves to, which is a value this step does not "
            f"control; the contract is one exact permitted effective value"))
    elif step["shell"] != PERMITTED_SHELL:
        findings.append(Finding(
            "cmd.step-shell-override",
            f"{label} sets `shell: {step['shell']!r}`. The one permitted shell is "
            f"{PERMITTED_SHELL!r}, which GitHub expands with `-eo pipefail`"))
    findings.extend(_step_env_findings(label, step.get("env")))

    return findings


# ── roster authority ────────────────────────────────────────────────────────
def load_orchestrator(path: Path):
    """Load `retention_gates.py` as the object whose real bindings are checked.

    Its own directory goes on `sys.path` for the duration, because the
    orchestrator imports `boundary` by name; it is removed again so this file
    leaves no way for the subtree to be imported implicitly afterwards.
    """
    spec = importlib.util.spec_from_file_location("w1a4_orchestrator_under_check", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"{path} cannot be loaded as a module")
    module = importlib.util.module_from_spec(spec)
    here = str(path.resolve().parent)
    sys.path.insert(0, here)
    try:
        spec.loader.exec_module(module)
    finally:
        try:
            sys.path.remove(here)
        except ValueError:
            pass
    return module


def _actual(orchestrator) -> tuple[list[tuple[str, object]], list[Finding]]:
    """The roster the orchestrator really carries, as (tier, gate) pairs."""
    out: list[tuple[str, object]] = []
    problems: list[Finding] = []
    for attribute, tier in (("GATES", "gate"), ("PROOFS", "proof")):
        roster = getattr(orchestrator, attribute, None)
        if not isinstance(roster, (tuple, list)):
            problems.append(Finding(
                "roster.orchestrator-shape",
                f"{ORCHESTRATOR} does not expose `{attribute}` as a sequence"))
            continue
        for gate in roster:
            out.append((tier, gate))
    return out, problems


def _describe(tier: str, gate) -> tuple[str, str, str, tuple[str, ...], str]:
    return (getattr(gate, "filename", "?"), getattr(gate, "function", "?"),
            getattr(gate, "kind", "?"), tuple(getattr(gate, "argv", ())), tier)


def check_roster(orchestrator) -> list[Finding]:
    """The orchestrator carries exactly the roster the anchor authenticates."""
    findings: list[Finding] = []
    if not EXPECTED_ROSTER:
        return [Finding(
            "roster.no-authority",
            "no authenticated roster is installed; the anchor findings above say why, and "
            "nothing may be compared against a roster that was never authenticated")]
    actual, problems = _actual(orchestrator)
    findings.extend(problems)
    if problems:
        return findings

    seen: dict[str, int] = {}
    for _tier, gate in actual:
        gate_id = getattr(gate, "gate_id", None)
        if not isinstance(gate_id, str):
            findings.append(Finding(
                "roster.entry-unidentifiable",
                f"a roster entry has no string `gate_id`: {gate!r}"))
            continue
        seen[gate_id] = seen.get(gate_id, 0) + 1

    duplicates = sorted(i for i, n in seen.items() if n > 1)
    if duplicates:
        findings.append(Finding(
            "roster.duplicate",
            f"the orchestrator names {duplicates} more than once; a gate invoked twice is "
            f"not two gates, and the second invocation can be quietly repointed"))

    missing = [e.gate_id for e in EXPECTED_ROSTER if e.gate_id not in seen]
    if missing:
        findings.append(Finding(
            "roster.missing",
            f"the orchestrator no longer carries {missing}. That set is not written down "
            f"in this file and not written down in `{SUBTREE}/`: it is the canonical "
            f"manifest `{MANIFEST}`, whose content digest and member identities are pinned "
            f"in `{TRUST_ROOT}`. Deleting the entry here and the expectation there is two "
            f"edits that still leave a third party disagreeing"))
    unknown = sorted(i for i in seen if i not in EXPECTED_BY_ID)
    if unknown:
        findings.append(Finding(
            "roster.unknown",
            f"the orchestrator carries {unknown}, which the authenticated manifest does "
            f"not name; a member nobody required is a member that can be removed unnoticed"))

    if len(actual) != len(EXPECTED_ROSTER):
        findings.append(Finding(
            "roster.cardinality",
            f"the orchestrator carries {len(actual)} members; the contract is exactly "
            f"{len(EXPECTED_ROSTER)}"))

    for tier, gate in actual:
        gate_id = getattr(gate, "gate_id", None)
        expected = EXPECTED_BY_ID.get(gate_id)
        if expected is None:
            continue
        found = _describe(tier, gate)
        if found[4] != expected.tier:
            findings.append(Finding(
                "roster.tier",
                f"{gate_id!r} is declared as a {found[4]} and must be a {expected.tier}; a "
                f"gate moved into the proof collection stops deciding whether the tree is "
                f"acceptable while the roster still looks complete"))
        if found[0] != expected.module:
            findings.append(Finding(
                "roster.module",
                f"{gate_id!r} resolves to module {found[0]!r}; the contract is "
                f"{expected.module!r}. The same function name in another file is another "
                f"function"))
        if found[1] != expected.callable_name:
            findings.append(Finding(
                "roster.callable",
                f"{gate_id!r} resolves to {found[0]}::{found[1]}; the contract is "
                f"{expected.module}::{expected.callable_name}. An entry keeping its id "
                f"while its callable changes is a gate replaced, not a gate configured"))
        if found[2] != expected.kind:
            findings.append(Finding(
                "roster.kind",
                f"{gate_id!r} declares kind {found[2]!r}; the contract is "
                f"{expected.kind!r}"))
        if found[3] != expected.argv:
            findings.append(Finding(
                "roster.argv",
                f"{gate_id!r} is invoked with argv {list(found[3])}; the contract is "
                f"{list(expected.argv)}. The argv is part of the identity: the same entry "
                f"point with and without `--ablate` is two members, not one named twice"))

    if not missing and not unknown and not duplicates and len(actual) == len(EXPECTED_ROSTER):
        order = [getattr(g, "gate_id", "?") for _t, g in actual]
        contract = [e.gate_id for e in EXPECTED_ROSTER]
        if order != contract:
            first = next(i for i, (a, b) in enumerate(zip(order, contract)) if a != b)
            findings.append(Finding(
                "roster.order",
                f"the orchestrator presents its roster in a different order, first "
                f"differing at position {first}: {order[first]!r} where the contract has "
                f"{contract[first]!r}. Order is part of this contract so that a gate and a "
                f"proof cannot trade places under equal sets"))

    findings.extend(_check_bindings(orchestrator))
    return findings


def _check_bindings(orchestrator) -> list[Finding]:
    """Resolve each expected callable through the orchestrator's own loader.

    A lambda, an alias or a generic no-op bound over the expected name keeps the
    roster's declared identity and changes what runs. `__name__` catches the
    lambda and the aliased no-op; `__code__.co_filename` catches a callable
    imported from somewhere else and re-exported under the right name.
    """
    findings: list[Finding] = []
    loader = getattr(orchestrator, "_load", None)
    here = getattr(orchestrator, "HERE", None)
    if not callable(loader) or not isinstance(here, Path):
        return [Finding(
            "roster.orchestrator-shape",
            f"{ORCHESTRATOR} exposes no `_load`/`HERE`, so its real bindings cannot be "
            f"resolved")]

    on_path = str(Path(here).resolve())
    sys.path.insert(0, on_path)
    try:
        findings.extend(_resolve_bindings(loader, Path(here)))
    finally:
        try:
            sys.path.remove(on_path)
        except ValueError:
            pass
    return findings


def _resolve_bindings(loader, here: Path) -> list[Finding]:
    findings: list[Finding] = []
    for expected in EXPECTED_ROSTER:
        try:
            module = loader(expected.module)
        except Exception as error:  # noqa: BLE001 - a module that will not load is a finding
            findings.append(Finding(
                "roster.module-unloadable",
                f"{expected.gate_id!r} names {expected.module}, which does not load: "
                f"{type(error).__name__}: {error}"))
            continue
        target = getattr(module, expected.callable_name, None)
        if not callable(target):
            findings.append(Finding(
                "roster.callable-absent",
                f"{expected.module}::{expected.callable_name} is not a callable"))
            continue
        name = getattr(target, "__name__", "<unnamed>")
        if name != expected.callable_name:
            findings.append(Finding(
                "roster.callable-substituted",
                f"{expected.module}::{expected.callable_name} is bound to a callable named "
                f"{name!r}. A lambda or an alias satisfies the roster's declared identity "
                f"and runs something else"))
            continue
        code = getattr(target, "__code__", None)
        origin = Path(code.co_filename).resolve() if code is not None else None
        wanted = (here / expected.module).resolve()
        if origin is not None and origin != wanted:
            findings.append(Finding(
                "roster.callable-foreign",
                f"{expected.module}::{expected.callable_name} is defined in {origin}, not "
                f"in {wanted}; it has been re-exported under the expected name"))
    return findings


# ── property ownership (W1-A5-V1-R2 findings B2 and B3) ─────────────────────
#
# Identity is not behaviour. Everything above this point establishes that the
# roster runs `publication_policy.py::check_all`, defined in that file, under
# that name. None of it notices that the function no longer reports a property
# it used to report.
#
# R2 measured both halves of that gap:
#
#   B2. Delete the single line `+ check_summary_identity(root)` from
#       `check_all`. Every identity check here passes, the orchestrator runs
#       14/14, and the subtree's own controls stay RED — because they called
#       the implementation directly, so they graded a function the gate no
#       longer invoked.
#
#   B3. Co-remove the call, the implementation, the four controls and their
#       invocation. Nothing anywhere refuses, because the property was declared
#       only by the file that implemented it. A self-declared obligation is not
#       an obligation.
#
# So the obligation is moved out. The canonical manifest names, per member, the
# properties that member must still REPORT; the manifest's digest and member
# identities are pinned in `ci/run.sh`; and this file — which is neither the
# manifest nor the subtree — demonstrates each one by CALLING the member.
#
# The demonstration is a witness violation, not a source scan. A grep for the
# property name is satisfied by the string in a comment, which is the D3 defect
# in a new place. Each witness builds a disposable repository root, breaks the
# thing the property is about, calls the pinned callable, and requires the
# property to be named. It then calls the same callable on an UNBROKEN root and
# requires the property to be absent, so a gate that returns the property
# unconditionally — which would pass the first half — fails the second.
#
# The witnesses live HERE, not in the subtree. A witness the subtree could edit
# is the bilateral agreement R3 finding F3 already refused once.

#: `properties` values the manifest may name. A member may not declare an
#: obligation this file cannot demonstrate; `load_manifest` refuses it as
#: `anchor.manifest-witnessless`.
PUBLICATION_WORKFLOW = ".github/workflows/w1-windows-runtime-publish.yml"

#: A line inside the approved summary's `run:` scalar. The prose witness
#: inserts ahead of it, which changes the scalar and therefore the summary
#: property. The byte witness deliberately does NOT touch the scalar.
_SUMMARY_ANCHOR = (
    '            echo "This workflow does not set, change or verify the '
    'package\'s registry"\n'
)


def _disposable_root(work: Path, root: Path, publication: str | None) -> Path:
    """A throwaway repository root the gates can be called against.

    The reviewed retention workflow so the cannot-publish evaluation inside
    `check_all` has the real file and contributes nothing; the publication
    workflow, pristine or broken; a symlink to the real `ci/` tree, where the
    script closure lives; and `git init`, because the closure resolver
    enumerates tracked paths. Nothing is written inside the repository.
    """
    fake = work / "root"
    (fake / ".github" / "workflows").mkdir(parents=True)
    for relative in (WORKFLOW, PUBLICATION_WORKFLOW):
        (fake / relative).write_bytes((root / relative).read_bytes())
    if publication is not None:
        (fake / PUBLICATION_WORKFLOW).write_text(publication, encoding="utf-8")
    (fake / "ci").symlink_to(root / "ci")
    subprocess.run(["git", "init", "-q", str(fake)], check=True, capture_output=True)
    return fake


def _witness_summary_prose(root: Path, work: Path) -> tuple[Path, str] | str:
    """Break the reviewed summary PROSE without touching anything else."""
    source = (root / PUBLICATION_WORKFLOW).read_text(encoding="utf-8")
    if source.count(_SUMMARY_ANCHOR) != 1:
        return (f"{PUBLICATION_WORKFLOW} no longer carries the summary anchor this "
                f"witness inserts before, so the witness cannot be built")
    inserted = '            echo "The package is PUBLIC."\n'
    broken = source.replace(_SUMMARY_ANCHOR, inserted + _SUMMARY_ANCHOR, 1)
    return _disposable_root(work, root, broken), "an inserted visibility assertion"


def _witness_workflow_bytes(root: Path, work: Path) -> tuple[Path, str] | str:
    """Break the reviewed BYTES without changing the parsed summary scalar.

    A trailing comment. `yaml.safe_load` never yields it, so the summary
    property cannot see it and only the file property can. That is what makes
    the two obligations distinguishable rather than one obligation named twice.
    """
    source = (root / PUBLICATION_WORKFLOW).read_text(encoding="utf-8")
    broken = source + "# witness: not the reviewed bytes\n"
    return _disposable_root(work, root, broken), "an appended comment line"


WITNESSES = {
    "summary.frozen-prose-drift": _witness_summary_prose,
    "publication.frozen-workflow-drift": _witness_workflow_bytes,
}


def _call_for_properties(target, where: Path) -> tuple[frozenset[str], str | None]:
    """Every property `target` names for `where`, or why it could not be asked."""
    try:
        findings = target(where)
    except Exception as error:  # noqa: BLE001 - a gate that raises is a finding
        return frozenset(), f"{type(error).__name__}: {error}"
    try:
        return frozenset(f.prop for f in findings), None
    except (TypeError, AttributeError) as error:
        return frozenset(), f"the return value is not a list of findings: {error}"


# ── the execution receipt (W1-A5-V1-R4 finding O9b) ─────────────────────────
#
# `check_properties` returning no findings and `check_properties` never having
# run are the same thing to a caller that only counts findings. R4 unwired the
# call from `check()` and the run still printed "every one of the 2 properties
# the manifest obliges is REPORTED ..." over zero work.
#
# So the success line is no longer reachable from an empty finding list. It is
# reachable only from a RECEIPT that `check_properties` writes as it goes: it
# ran, it reached the end, and the set of (member, property) pairs it actually
# DEMONSTRATED equals the set the manifest obliges. A pair is recorded only
# after both halves of its witness succeeded — the property named for the
# broken tree AND absent for the unmodified one.
#
# This is an execution invariant, not self-protection. An author who may
# rewrite this file may also rewrite the receipt; the claim is about what these
# reviewed bytes do, and nothing more.
class _PropertyReceipt:
    """What `check_properties` actually did, for `main` to verify."""

    __slots__ = ("ran", "completed", "demonstrated")

    def __init__(self) -> None:
        self.reset()

    def reset(self) -> None:
        self.ran = False
        self.completed = False
        self.demonstrated: set[tuple[str, str]] = set()


RECEIPT = _PropertyReceipt()


def obliged_pairs() -> frozenset[tuple[str, str]]:
    """Every (member, property) pair the canonical manifest obliges."""
    return frozenset((e.gate_id, name) for e in EXPECTED_ROSTER for name in e.properties)


def receipt_findings() -> list[Finding]:
    """The ownership run did what the success line is about to claim it did."""
    obliged = obliged_pairs()
    if not RECEIPT.ran:
        return [Finding(
            "ownership.properties-unchecked",
            f"the {len(obliged)} obligation(s) the canonical manifest records were never "
            f"checked: `check_properties` did not run. An empty finding list from a check "
            f"that never ran is not a passing check")]
    if not RECEIPT.completed:
        return [Finding(
            "ownership.properties-incomplete",
            f"`check_properties` ran but did not reach the end, so the obligations it did "
            f"not get to were neither demonstrated nor reported")]
    missing = sorted(obliged - RECEIPT.demonstrated)
    if missing:
        return [Finding(
            "ownership.properties-partial",
            f"`check_properties` completed having demonstrated "
            f"{len(RECEIPT.demonstrated)} of {len(obliged)} obligation(s); not "
            f"demonstrated: {[f'{g}::{n}' for g, n in missing]}")]
    unexpected = sorted(RECEIPT.demonstrated - obliged)
    if unexpected:
        return [Finding(
            "ownership.properties-unexpected",
            f"`check_properties` demonstrated obligations the canonical manifest does not "
            f"record: {[f'{g}::{n}' for g, n in unexpected]}")]
    return []


def check_properties(root: Path, orchestrator) -> list[Finding]:
    """Each member still reports the properties the canonical manifest names."""
    findings: list[Finding] = []
    RECEIPT.ran = True
    obliged = [e for e in EXPECTED_ROSTER if e.properties]
    if not obliged:
        RECEIPT.completed = True
        return findings
    loader = getattr(orchestrator, "_load", None)
    here = getattr(orchestrator, "HERE", None)
    if not callable(loader) or not isinstance(here, Path):
        return [Finding(
            "ownership.orchestrator-shape",
            f"{ORCHESTRATOR} exposes no `_load`/`HERE`, so the obliged members cannot be "
            f"called")]

    on_path = str(Path(here).resolve())
    sys.path.insert(0, on_path)
    work = Path(tempfile.mkdtemp(prefix="w1a5r3-ownership-"))
    try:
        for entry in obliged:
            if entry.kind != "findings":
                findings.append(Finding(
                    "ownership.kind-unsupported",
                    f"{entry.gate_id!r} declares properties but is a {entry.kind!r} member; "
                    f"only a findings member can be asked which properties it names"))
                continue
            try:
                module = loader(entry.module)
                target = getattr(module, entry.callable_name)
            except Exception as error:  # noqa: BLE001
                findings.append(Finding(
                    "ownership.member-unreachable",
                    f"{entry.module}::{entry.callable_name} cannot be resolved to ask it "
                    f"for its properties: {type(error).__name__}: {error}"))
                continue

            clean = work / f"{entry.gate_id}-clean"
            clean.mkdir()
            unbroken = _disposable_root(clean, root, None)
            baseline, problem = _call_for_properties(target, unbroken)
            if problem is not None:
                findings.append(Finding(
                    "ownership.member-unusable",
                    f"{entry.module}::{entry.callable_name} could not be called against an "
                    f"unmodified tree: {problem}"))
                continue

            for name in entry.properties:
                built = WITNESSES[name](root, work / f"{entry.gate_id}-{len(findings)}-"
                                        f"{name.replace('.', '-')}")
                if isinstance(built, str):
                    findings.append(Finding("ownership.witness-unanchored", built))
                    continue
                broken_root, description = built
                named, problem = _call_for_properties(target, broken_root)
                if problem is not None:
                    findings.append(Finding(
                        "ownership.member-unusable",
                        f"{entry.module}::{entry.callable_name} could not be called against "
                        f"the {name} witness: {problem}"))
                    continue
                if name not in named:
                    findings.append(Finding(
                        "ownership.property-not-reported",
                        f"{MANIFEST} obliges {entry.gate_id!r} to report {name!r}. Given "
                        f"{description}, {entry.module}::{entry.callable_name} named "
                        f"{sorted(named) or 'nothing'} instead. The property is no longer "
                        f"on the canonical path, whether it was unwired, deleted or never "
                        f"reached"))
                    continue
                if name in baseline:
                    findings.append(Finding(
                        "ownership.property-always-reported",
                        f"{entry.gate_id!r} names {name!r} for an UNMODIFIED tree as well as "
                        f"for the witness, so the witness demonstrates nothing"))
                    continue
                RECEIPT.demonstrated.add((entry.gate_id, name))
        RECEIPT.completed = True
    finally:
        shutil.rmtree(work, ignore_errors=True)
        try:
            sys.path.remove(on_path)
        except ValueError:
            pass
    return findings


# ── entry point ─────────────────────────────────────────────────────────────
def check(root: Path) -> list[Finding]:
    RECEIPT.reset()
    findings = install_roster(root)
    findings += check_trust_root(root)
    findings += check_command(root)
    path = root / ORCHESTRATOR
    if not path.is_file():
        return findings + [Finding(
            "roster.orchestrator-missing", f"{ORCHESTRATOR} does not exist")]
    if path.is_symlink():
        return findings + [Finding(
            "roster.orchestrator-symlinked", f"{ORCHESTRATOR} is a symbolic link")]
    try:
        orchestrator = load_orchestrator(path)
    except Exception as error:  # noqa: BLE001
        return findings + [Finding(
            "roster.orchestrator-unloadable",
            f"{ORCHESTRATOR} does not import: {type(error).__name__}: {error}")]
    return findings + check_roster(orchestrator) + check_properties(root, orchestrator)


def main() -> int:
    root = repo_root()
    findings = check(root) + receipt_findings()
    if findings:
        print(f"W1-A4 RETENTION AUTHORITY HARD STOP: {len(findings)} finding(s)",
              file=sys.stderr)
        for finding in findings:
            print(f"  FAIL [{finding.prop}] {finding.message}", file=sys.stderr)
        return 1
    print(f"{TRUST_ROOT} reaches its pin block before it dispatches anything, and the "
          f"block is fail-closed")
    print(f"{MANIFEST} matches the digest and the {len(EXPECTED_ROSTER)} identities "
          f"{TRUST_ROOT} pins")
    print(f"{WORKFLOW} job `{REQUIRED_JOB}` step `{REQUIRED_STEP_ID}` runs exactly "
          f"{CANONICAL_COMMAND!r}, under `shell: {PERMITTED_SHELL}`, with "
          f"{len(REQUIRED_STEP_ENV)} startup variables neutralised at step scope and none "
          f"declared at any wider scope")
    print(f"{ORCHESTRATOR} carries exactly the {len(EXPECTED_ROSTER)} members the "
          f"authenticated manifest requires, in order, each resolving to its expected "
          f"callable")
    # Counted from the receipt, not from the manifest: this sentence may only
    # describe work that was actually done. `receipt_findings` above has
    # already established that the two sets are equal.
    print(f"every one of the {len(RECEIPT.demonstrated)} properties the manifest obliges "
          f"is REPORTED by the member that owns it when called against a witness "
          f"violation, and by none of them against an unmodified tree")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
