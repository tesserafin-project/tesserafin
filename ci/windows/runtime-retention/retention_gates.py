"""The one canonical retention validation command (#236, W1-A4-R2).

    python3 ci/windows/runtime-retention/retention_gates.py --validate

R1's blocking finding D3. The retention workflow invoked its gates as seven
separate `run:` steps, and `boundary.check_gate_is_pinned` proved they were
invoked by testing

    if "boundary-controls.py" not in body or "boundary.py" not in body

against the workflow's raw text. A COMMENT naming either file satisfies that.
Commenting out the invocation while leaving the step's name and its comment in
place left a workflow that read exactly as before, ran nothing, and reported the
gate as pinned.

Two things had to change, and one alone would not have been enough.

FIRST, the gates are a closed ROSTER, resolved by exact module and function
identity and CALLED. Nothing is located by searching text for a filename.
Deleting an entry removes a gate that the roster's own required set then reports
as missing; adding an unknown one fails closed; naming one twice fails closed.

SECOND, the invocation is pinned STRUCTURALLY, from outside this subtree, by
`ci/windows/verify-retention-gate-pinned.py` — which `ci/run.sh` runs. It parses
the workflow as YAML and requires the exact job, the exact command as an ACTIVE
`run` command, no `continue-on-error`, no unreachable condition and no success
masking. A pin that lives inside the thing it pins is not a pin.

WHY BOTH TIERS RUN.

The roster has GATES, which answer "is this tree acceptable", and PROOFS, which
answer "could these gates still refuse anything". A gate replaced by a no-op
passes every tree; its proof suite then grades every hostile control GREEN —
"ACCEPTED what must be refused" — and fails. That is what makes "replacing a
gate with a no-op fails its self-proof" a mechanism rather than a hope.

The registry protocol appears here as its AUTHORITY property, which
`oci-protocol.sh` decides without a network. The live-registry controls need a
service container and stay their own workflow job; `build-twice.sh` needs a
staged unit and stays its own job too. Both are named in NOT_IN_ROSTER below
rather than left to be noticed, because a roster that quietly omits something is
the same defect one level up.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import sys
import tempfile
from pathlib import Path

import boundary

HERE = Path(__file__).resolve().parent


class RosterError(Exception):
    """A roster that cannot be trusted to describe itself. Never caught."""


class Gate:
    """One roster entry, identified by exact file and function name."""

    __slots__ = ("gate_id", "filename", "function", "kind", "argv", "description")

    def __init__(self, gate_id: str, filename: str, function: str, kind: str,
                 argv: tuple[str, ...], description: str) -> None:
        self.gate_id = gate_id
        self.filename = filename
        self.function = function
        self.kind = kind
        self.argv = argv
        self.description = description

    @property
    def identity(self) -> tuple[str, str, tuple[str, ...]]:
        # The argv is part of the identity. `negative-controls.py::main` is the
        # acceptance suite and the 09/10/11 ablation matrix depending on
        # `--ablate`, and those are two gates, not one named twice.
        return (self.filename, self.function, self.argv)


#: The gates. `findings` gates return a list of `boundary.Finding`; an empty
#: list is a pass.
GATES: tuple[Gate, ...] = (
    Gate("accepted-contract", "contract.py", "check_all", "findings", (),
         "the committed acceptance manifest survives the closed schema and describes "
         "its own digest consistently"),
    Gate("deterministic-layout", "retention.py", "check_all", "findings", (),
         "the OCI layout is a pure function of the manifest's content, not of the order "
         "its keys arrive in"),
    Gate("publication-policy", "publication_policy.py", "check_all", "findings", (),
         "the validation workflow and every node of its reusable-workflow graph hold no "
         "write permission, no credential and no registry write authority"),
    Gate("excluded-subtree-ownership", "boundary.py", "check_all", "findings", (),
         "every tracked path under the subtree is a regular file with exactly one "
         "retention role, and the W1 build closure reaches none of them"),
    Gate("proof-trigger", "trigger_policy.py", "check_all", "findings", (),
         "the W1 proof trigger is positive-only and every retention change crosses it"),
    Gate("registry-authority", "loopback-corpus.py", "check_all", "findings", (),
         "one authority corpus reaches the same named verdict in the Python policy and "
         "in oci-protocol.sh"),
)

#: The proofs. `exit-code` entries are invoked with `sys.argv` set and their
#: integer return is the verdict, which is how the existing suites already work.
PROOFS: tuple[Gate, ...] = (
    Gate("ownership-self-proof", "boundary-controls.py", "main", "exit-code", (),
         "fourteen hostile controls each reach their own named ownership property"),
    Gate("publication-self-proof", "permission-fixtures.py", "main", "exit-code", (),
         "twelve semantic permission fixtures each reach their own named property"),
    Gate("reusable-workflow-self-proof", "reusable-workflow-controls.py", "main",
         "exit-code", (),
         "sixteen reusable-workflow graph controls each reach their own named property"),
    Gate("trusted-source-self-proof", "trusted-source-controls.py", "main", "exit-code", (),
         "the commit-sha shape and the HEAD equality are each load-bearing alone"),
    Gate("reference-grammar-self-proof", "reference-corpus.py", "main", "exit-code",
         ("--allow-missing-pwsh",),
         "one reference corpus reaches the same verdict in every parser present"),
    Gate("hostile-controls-self-proof", "negative-controls.py", "main", "exit-code",
         ("--fixture", "{fixture}"),
         "the twenty acceptance controls each reach their own named property"),
    Gate("hostile-controls-ablation", "negative-controls.py", "main", "exit-code",
         ("--fixture", "{fixture}", "--ablate"),
         "controls 09, 10 and 11 are each satisfied by their own mutation and no other"),
    Gate("gate-roster-self-proof", "gate-roster-controls.py", "main", "exit-code", (),
         "D3 replayed: deleting, duplicating, no-opping or unpinning a roster entry is "
         "refused, and the structural pin refuses every way of making the canonical "
         "command inert"),
)

#: The gate ids this command MUST run, WITH the exact identity each must have.
#:
#: Stating the identity and not merely the id is what closes the substitution
#: the roster would otherwise permit. A roster entry keeping its id while its
#: function name changes is a gate replaced, and an entry pointing at
#: `check_all_no_op` has a perfectly good identity of its own — the required set
#: is where that stops being acceptable. The set is written out separately from
#: the roster on purpose: removing a roster line cannot remove the requirement
#: with it.
REQUIRED: dict[str, tuple[str, str, tuple[str, ...]]] = {
    "accepted-contract": ("contract.py", "check_all", ()),
    "deterministic-layout": ("retention.py", "check_all", ()),
    "publication-policy": ("publication_policy.py", "check_all", ()),
    "excluded-subtree-ownership": ("boundary.py", "check_all", ()),
    "proof-trigger": ("trigger_policy.py", "check_all", ()),
    "registry-authority": ("loopback-corpus.py", "check_all", ()),
    "ownership-self-proof": ("boundary-controls.py", "main", ()),
    "publication-self-proof": ("permission-fixtures.py", "main", ()),
    "reusable-workflow-self-proof": ("reusable-workflow-controls.py", "main", ()),
    "trusted-source-self-proof": ("trusted-source-controls.py", "main", ()),
    "reference-grammar-self-proof": ("reference-corpus.py", "main", ("--allow-missing-pwsh",)),
    "hostile-controls-self-proof": ("negative-controls.py", "main", ("--fixture", "{fixture}")),
    "hostile-controls-ablation": ("negative-controls.py", "main",
                                  ("--fixture", "{fixture}", "--ablate")),
    "gate-roster-self-proof": ("gate-roster-controls.py", "main", ()),
}

#: Named here so the omission is a statement rather than an oversight.
NOT_IN_ROSTER: dict[str, str] = {
    "registry-controls.sh": "needs a live registry service container; its own workflow job. "
                            "Its security-relevant half — that oci-protocol.sh refuses every "
                            "non-loopback authority — is in the roster as registry-authority",
    "build-twice.sh": "needs a staged unit and two clean directories; its own workflow job. "
                      "The half provable from committed data alone is in the roster as "
                      "deterministic-layout",
    "consume.ps1": "a consumer, exercised by reference-grammar-self-proof through the "
                   "reference corpus rather than run against a registry",
}


#: Loaded roster modules, by filename. A cache, not an optimisation: without it
#: every resolution re-executes the module and returns a fresh object, so a
#: control that substitutes a gate would be substituting it into a copy nothing
#: else ever sees, and would grade ERROR instead of proving anything.
_MODULES: dict = {}


def _load(filename: str):
    cached = _MODULES.get(filename)
    if cached is not None:
        return cached
    path = HERE / filename
    if not path.is_file():
        raise RosterError(f"the roster names {filename}, which is not a file under {HERE}")
    if path.is_symlink():
        raise RosterError(f"the roster names {filename}, which is a symbolic link")
    spec = importlib.util.spec_from_file_location(path.stem.replace("-", "_"), path)
    if spec is None or spec.loader is None:
        raise RosterError(f"{filename} cannot be loaded as a module")
    module = importlib.util.module_from_spec(spec)
    sys.modules.setdefault(spec.name, module)
    spec.loader.exec_module(module)
    _MODULES[filename] = module
    return module


def validate_roster() -> None:
    """The roster describes itself, or nothing runs.

    Fail-closed in four directions: an unknown identity, a duplicate id, a
    duplicate identity, and a required id the roster no longer carries. The
    last is the one that matters most — it is what makes DELETING the ownership
    gate a refusal instead of a shorter, still-green run.
    """
    seen_ids: set[str] = set()
    seen_identities: set[tuple[str, str, tuple[str, ...]]] = set()
    for gate in GATES + PROOFS:
        if gate.gate_id in seen_ids:
            raise RosterError(f"the roster names the gate id {gate.gate_id!r} twice")
        seen_ids.add(gate.gate_id)
        if gate.identity in seen_identities:
            raise RosterError(
                f"the roster names {gate.filename}::{gate.function} with argv {gate.argv} "
                f"twice; a gate invoked twice is not two gates")
        seen_identities.add(gate.identity)
        if gate.kind not in ("findings", "exit-code"):
            raise RosterError(f"{gate.gate_id!r} declares the unknown kind {gate.kind!r}")
        module = _load(gate.filename)
        target = getattr(module, gate.function, None)
        if not callable(target):
            raise RosterError(
                f"the roster names {gate.filename}::{gate.function}, which is not a "
                f"callable in that module")

    missing = sorted(set(REQUIRED) - seen_ids)
    if missing:
        raise RosterError(
            f"the roster no longer carries the required gate(s) {missing}; a gate deleted "
            f"from the roster is a gate that stops running, and this command refuses to be "
            f"the thing that made that quiet")
    unknown = sorted(seen_ids - set(REQUIRED))
    if unknown:
        raise RosterError(
            f"the roster carries {unknown}, which the required set does not name; a gate "
            f"added without being required is a gate that can be removed without notice")
    for gate in GATES + PROOFS:
        expected = REQUIRED[gate.gate_id]
        if gate.identity != expected:
            raise RosterError(
                f"the roster resolves {gate.gate_id!r} to {gate.identity}, but the required "
                f"identity is {expected}; a gate that keeps its id while its module or "
                f"function changes is a gate replaced, not a gate configured")


def _run(gate: Gate, root: Path, fixture: Path) -> tuple[bool, str]:
    module = _load(gate.filename)
    target = getattr(module, gate.function)
    if gate.kind == "findings":
        findings = target(root)
        if not isinstance(findings, list):
            raise RosterError(
                f"{gate.filename}::{gate.function} returned {type(findings).__name__}, not "
                f"a list of findings; a gate that cannot report cannot refuse")
        if findings:
            return False, "\n".join(f"      FAIL [{f.prop}] {f.message}" for f in findings)
        return True, ""

    argv = [gate.filename] + [a.format(fixture=fixture) for a in gate.argv]
    saved = sys.argv
    sys.argv = argv
    try:
        status = target()
    finally:
        sys.argv = saved
    if status != 0:
        return False, f"      exit {status}"
    return True, ""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--validate", action="store_true",
                        help="run the complete closed roster")
    parser.add_argument("--list", action="store_true", help="print the roster and stop")
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    validate_roster()

    if args.list:
        for gate in GATES + PROOFS:
            print(f"  {gate.gate_id:<32} {gate.filename}::{gate.function}")
        for name, why in NOT_IN_ROSTER.items():
            print(f"  {'(not in roster)':<32} {name}: {why}")
        return 0
    if not args.validate:
        parser.error("--validate is required")

    root = boundary.repo_root()
    work = Path(tempfile.mkdtemp(prefix="w1a4r2-gates-"))
    fixture = work / "fixture"
    make_fixture = _load("make-fixture.py")
    saved = sys.argv
    sys.argv = ["make-fixture.py", "--out", str(fixture)]
    try:
        if make_fixture.main() != 0:
            print("W1-A4 GATE HARD STOP: the fixture could not be built, so the control "
                  "suites cannot prove anything", file=sys.stderr)
            return 2
    finally:
        sys.argv = saved

    results = []
    failures = 0
    for tier, roster in (("gate", GATES), ("proof", PROOFS)):
        print(f"\n{'═' * 4} {tier}s")
        for gate in roster:
            print(f"\n── {gate.gate_id}  ({gate.filename}::{gate.function})")
            print(f"   {gate.description}")
            try:
                passed, detail = _run(gate, root, fixture)
            except RosterError:
                raise
            except Exception as error:  # noqa: BLE001 - an ERROR is a failure
                passed, detail = False, f"      {type(error).__name__}: {error}"
            if not passed:
                failures += 1
                print(f"   FAIL")
                if detail:
                    print(detail)
            else:
                print(f"   PASS")
            results.append({"gate": gate.gate_id, "tier": tier,
                            "identity": f"{gate.filename}::{gate.function}",
                            "passed": passed, "detail": detail})

    if args.json:
        args.json.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    print()
    if failures:
        print(f"W1-A4 RETENTION HARD STOP: {failures} of {len(results)} roster entries "
              f"failed", file=sys.stderr)
        return 1
    print(f"all {len(GATES)} gates and {len(PROOFS)} self-proofs in the closed roster passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
