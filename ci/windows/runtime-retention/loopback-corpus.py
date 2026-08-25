"""One authority corpus, two parsers, identical verdicts (#236, W1-A4-R2).

Two fail-closed observations from the W1-A4-R1 review land here.

FIRST, the Python side could not SEE a bracketed authority. `_REGISTRY_LITERAL`
offered three alternatives — dotted name, `localhost`, `127.0.0.1` — and none of
them matches `[2001:db8::1]:5000/tesserafin/runtime`. A remote IPv6 registry on
an `oras` command line was therefore not a non-loopback target; it was not a
registry literal at all, and `registry.non-loopback-target` could never fire on
it. A classification bug is visible; a recognition bug is not, which is why the
recognizer is fixed first and the corpus contains the case that proves it.

SECOND, both sides decided loopback by comparing a string to a small set. The
Python side used `host.split(":")[0]`, which yields `[` for `[::1]:5000`, the
empty string for `::1:5000` and `user` for `user:pass@evil.example`; `[::1]` was
accepted only because that exact literal happened to be in the set, and would
have stopped being accepted the moment a port was appended. The shell side kept
its own list, in its own file, in a different language.

Two hand-written parsers drift. So there is one corpus, both parsers answer
every entry, and the suite requires the SAME NAMED verdict from each — not
merely the same permit/refuse decision, because two parsers that refuse the same
authority for different reasons have already begun to disagree.

The supported local set is closed and textual on both sides: `localhost`, an
IPv4 address in 127.0.0.0/8, and `[::1]` or `[0:0:0:0:0:0:0:1]`. A VALID IPv6
loopback in any other spelling is refused as `ipv6-form-not-supported` rather
than permitted by whichever parser is the more generous — an intentionally
unsupported form fails closed and says so.

The shell verdict is taken from `oci-protocol.sh check-authority`, which calls
the same `local_authority_verdict` every write path calls. A corpus entry cannot
pass here while the write path decides otherwise.

The recognition probe line lives in the corpus JSON rather than here. Naming a
module in `boundary.INVENTORY` puts it in the retention workflow's script
closure, where `publication_policy` reads its text as the workflow's own
capability; a control module carrying a literal registry write verb would fail
the pristine tree. Data files are not scripts and are never walked, so the
adversarial vocabulary lives in the corpus. The alternative — adding this module
to `boundary.POLICY_SELF` — would widen the one exemption in this design that a
reviewer should be attacking, and is not worth a string.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

import boundary
import publication_policy

HERE = Path(__file__).resolve().parent
CORPUS = HERE / "loopback-corpus.json"
PROTOCOL = HERE / "oci-protocol.sh"

#: The verdicts that permit a registry WRITE. Everything else refuses one.
PERMITTING = frozenset({"localhost", "loopback-address"})


def python_verdict(authority: str) -> str:
    return publication_policy.local_authority_verdict(authority)[1]


def shell_verdict(authority: str) -> str:
    out = subprocess.run(
        [str(PROTOCOL), "check-authority", "--repo", f"{authority}/tesserafin/runtime"],
        capture_output=True, text=True,
    )
    if out.returncode != 0:
        return f"<exit {out.returncode}: {out.stderr.strip()}>"
    return out.stdout.strip()


def recognition_findings() -> list[boundary.Finding]:
    """Every corpus authority must be RECOGNISED as a registry literal.

    This is the check R1 did not have, and its absence is what let the IPv6 case
    pass. An authority the pattern cannot match is never handed to any parser,
    so the two parsers agreeing about it proves nothing at all.
    """
    findings: list[boundary.Finding] = []
    corpus = json.loads(CORPUS.read_text(encoding="utf-8"))
    for entry in corpus["entries"]:
        authority = entry["authority"]
        line = corpus["probeLine"].format(authority=authority)
        match = publication_policy._REGISTRY_LITERAL.search(line)
        if match is None:
            findings.append(boundary.Finding(
                "loopback.authority-not-recognised",
                f"{entry['name']}: {authority!r} is not matched as a registry literal, so "
                f"no verdict is ever reached for it",
            ))
        elif match.group("host") != authority:
            findings.append(boundary.Finding(
                "loopback.authority-partially-recognised",
                f"{entry['name']}: {authority!r} is matched as {match.group('host')!r}; a "
                f"partial authority is classified as something the line does not say",
            ))
    return findings


def check_all(root: Path | None = None) -> list[boundary.Finding]:
    findings = recognition_findings()
    corpus = json.loads(CORPUS.read_text(encoding="utf-8"))
    for entry in corpus["entries"]:
        authority, expected = entry["authority"], entry["verdict"]
        got_python = python_verdict(authority)
        got_shell = shell_verdict(authority)
        if got_python != expected:
            findings.append(boundary.Finding(
                "loopback.python-verdict",
                f"{entry['name']}: python says {got_python!r} for {authority!r}, corpus "
                f"says {expected!r}",
            ))
        if got_shell != expected:
            findings.append(boundary.Finding(
                "loopback.shell-verdict",
                f"{entry['name']}: shell says {got_shell!r} for {authority!r}, corpus "
                f"says {expected!r}",
            ))
        if got_python != got_shell:
            findings.append(boundary.Finding(
                "loopback.parsers-disagree",
                f"{entry['name']}: python says {got_python!r} and shell says {got_shell!r} "
                f"for {authority!r}",
            ))
    return findings


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()

    corpus = json.loads(CORPUS.read_text(encoding="utf-8"))
    results = []
    print(f"{'':6} {'entry':<38} {'python':<24} {'shell':<24} expected")
    for entry in corpus["entries"]:
        authority, expected = entry["authority"], entry["verdict"]
        got_python, got_shell = python_verdict(authority), shell_verdict(authority)
        ok = got_python == got_shell == expected
        print(f"  {'OK' if ok else 'FAIL':<4} {entry['name']:<38} {got_python:<24} "
              f"{got_shell:<24} {expected}")
        results.append({
            "entry": entry["name"], "authority": authority, "expected": expected,
            "python": got_python, "shell": got_shell,
            "writePermitted": expected in PERMITTING, "why": entry["why"],
        })

    findings = check_all()
    if args.json:
        args.json.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")

    print()
    if findings:
        print(f"W1-A4 LOOPBACK HARD STOP: {len(findings)} finding(s)", file=sys.stderr)
        for finding in findings:
            print(f"  FAIL [{finding.prop}] {finding.message}", file=sys.stderr)
        return 1
    permitting = sum(1 for e in corpus["entries"] if e["verdict"] in PERMITTING)
    print(f"every one of {len(corpus['entries'])} authorities is recognised as a registry "
          f"literal and reaches the same named verdict in both parsers; {permitting} of them "
          f"permit a registry write and {len(corpus['entries']) - permitting} refuse one")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
