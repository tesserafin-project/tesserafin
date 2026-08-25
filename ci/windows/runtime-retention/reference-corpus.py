"""One corpus, three parsers, identical decisions (#236, W1-A4-R1).

R0's adjacent finding was that `consume.ps1` still carried the permissive
`[^@]+` repository shape that `oci-protocol.sh` had already been repaired for,
so the PowerShell consumer accepted references the Python contract and the shell
protocol both refused. A rule stated three times is worth nothing unless the
three statements agree, and nothing was checking that they did.

This runs every corpus entry through all three implementations and requires:

  * the same VERDICT — accept or reject;
  * the same REASON TOKEN when rejecting.

A parser that refuses the right reference for the wrong reason is graded INERT,
not RED. Repairing an over-permissive parser by leaning on a later
canonical-package equality check would show up here as a reason-token
disagreement, which is exactly what it is.

The canonical-package authority is checked separately, against the two parsers
that carry it. `oci-protocol.sh` deliberately does not: the local-registry
controls address `localhost:5000`, and forcing our one package on the protocol
script would make it unable to run them.

PowerShell is not optional. Without `pwsh` this refuses to report a result at
all rather than reporting two-thirds of a corpus as a pass — a silently skipped
leg is the failure mode this file exists to prevent. Pass
`--allow-missing-pwsh` to grade it SKIPPED explicitly; the retention workflow
never does.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

import boundary
import contract

HERE = Path(__file__).resolve().parent
CORPUS = HERE / "reference-corpus.json"
PROTOCOL = HERE / "oci-protocol.sh"
CONSUMER = HERE / "consume.ps1"


def _python_grammar(reference: str) -> str | None:
    return contract.classify_reference(reference)


def _python_canonical(reference: str) -> str | None:
    try:
        contract.parse_reference(reference)
    except contract.ContractError as error:
        return _token(str(error))
    return None


_ANSI = __import__("re").compile(r"\x1b\[[0-9;]*m")
_TOKEN = __import__("re").compile(r"REFERENCE-REJECTED:([a-z-]+)")


def _token(text: str) -> str | None:
    """The reason token a parser named, whatever decoration surrounds it.

    PowerShell's `Write-Error` wraps the message in ANSI colour and may fold it,
    so a whitespace split found `\x1b[31;1mREFERENCE-REJECTED:tag-only` and
    reported every PowerShell rejection as unnamed — a corpus disagreement that
    was an artefact of reading the output, not of the parser.
    """
    match = _TOKEN.search(_ANSI.sub("", text).replace("\n", " "))
    return match.group(1) if match else "unnamed-rejection"


def _run(argv: list[str]) -> tuple[int, str]:
    done = subprocess.run(argv, capture_output=True, text=True)
    return done.returncode, done.stdout + done.stderr


def _shell_grammar(reference: str) -> str | None:
    code, output = _run([str(PROTOCOL), "check-reference", "--repo", reference])
    return None if code == 0 else _token(output)


def _pwsh_grammar(pwsh: str, reference: str) -> str | None:
    code, output = _run([pwsh, "-NoProfile", "-File", str(CONSUMER),
                         "-GrammarCheck", reference])
    return None if code == 0 else _token(output)


def _pwsh_canonical(pwsh: str, reference: str) -> str | None:
    code, output = _run([pwsh, "-NoProfile", "-File", str(CONSUMER),
                         "-GrammarCheck", reference, "-Canonical"])
    return None if code == 0 else _token(output)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", type=Path)
    parser.add_argument("--allow-missing-pwsh", action="store_true")
    args = parser.parse_args()

    corpus = json.loads(CORPUS.read_text(encoding="utf-8"))
    pwsh = shutil.which("pwsh")
    if pwsh is None and not args.allow_missing_pwsh:
        print("W1-A4 CORPUS HARD STOP: pwsh is not on PATH, so the PowerShell leg cannot "
              "run. Two of three parsers agreeing is not the property under test.",
              file=sys.stderr)
        return 1

    failures = 0
    results = []
    print(f"cross-language reference corpus: {len(corpus['entries'])} entries, "
          f"{'3' if pwsh else '2'} parsers")
    print(f"  {'entry':<30} {'expected':<20} python  shell   pwsh")

    for entry in corpus["entries"]:
        reference = entry["reference"]
        expected = entry["grammar"]
        verdicts = {
            "python": _python_grammar(reference),
            "shell": _shell_grammar(reference),
            "pwsh": _pwsh_grammar(pwsh, reference) if pwsh else "SKIPPED",
        }
        active = {k: v for k, v in verdicts.items() if v != "SKIPPED"}
        agree = len(set(active.values())) == 1
        correct = all(v == expected for v in active.values())
        grade = "OK" if (agree and correct) else ("DISAGREE" if not agree else "WRONG")
        if grade != "OK":
            failures += 1
        print(f"  {grade:<4} {entry['name']:<30} {str(expected):<20} "
              f"{str(verdicts['python']):<7} {str(verdicts['shell']):<7} {verdicts['pwsh']}")
        results.append({"entry": entry["name"], "leg": "grammar", "expected": expected,
                        "verdicts": verdicts, "grade": grade})

    print("\n  canonical-package authority (python contract and the PowerShell consumer only)")
    for entry in corpus["entries"]:
        expected = entry["canonical"]
        if expected is None:
            continue
        reference = entry["reference"]
        verdicts = {
            "python": _python_canonical(reference),
            "pwsh": _pwsh_canonical(pwsh, reference) if pwsh else "SKIPPED",
        }
        active = {k: v for k, v in verdicts.items() if v != "SKIPPED"}
        agree = len(set(active.values())) == 1
        correct = all(v == expected for v in active.values())
        grade = "OK" if (agree and correct) else ("DISAGREE" if not agree else "WRONG")
        if grade != "OK":
            failures += 1
        print(f"  {grade:<4} {entry['name']:<30} {str(expected):<20} "
              f"{str(verdicts['python']):<7} {'':<7} {verdicts['pwsh']}")
        results.append({"entry": entry["name"], "leg": "canonical", "expected": expected,
                        "verdicts": verdicts, "grade": grade})

    if pwsh is None:
        print("\n  SKIPPED  the PowerShell leg — pwsh is not installed on this host")
        results.append({"entry": "pwsh-leg", "grade": "SKIPPED"})

    if args.json:
        args.json.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    print()
    if failures:
        print(f"W1-A4 CORPUS HARD STOP: {failures} corpus decision(s) disagree", file=sys.stderr)
        return 1
    print("every parser reaches the same verdict for the same named reason on every entry")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
