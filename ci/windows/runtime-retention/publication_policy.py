"""Semantic cannot-publish policy for a workflow file (#236, W1-A4-R1).

WHAT THIS REPLACES, AND WHY.

`assert-cannot-publish.sh` interpreted permissions with six greps over a
comment-stripped copy of the workflow. R0's blocking finding F1 is that this is
not a permission model: `permissions: write-all` grants `packages: write`
without containing the string, a quoted `"packages": "write"` or an inline flow
mapping `{ packages: write }` reads differently to a line-oriented pattern, and
a job-level block can widen what the workflow level narrowed. A regex over text
cannot answer "what does this workflow RESOLVE to"; a parser can.

So the workflow is parsed as YAML — PyYAML, which the validation workflow
guarantees explicitly rather than hoping the runner image supplies it — and the
permission set is evaluated at BOTH levels against a closed permitted set.
Comments disappear in the parse rather than being stripped by hand, so prose
about `packages: write` no longer reads as configuration and no comment can
hide executable content either.

THE DISCRIMINATOR IS CREDENTIAL AND WRITE AUTHORITY, NOT THE VERB `push`.

This is the part worth stating plainly, because the obvious rule is wrong. The
retention workflow legitimately runs `registry-controls.sh`, which calls
`oci-protocol.sh push` against a LOCAL registry on localhost:5000 over plain
HTTP. A policy that rejects the token `push` anywhere in the workflow's script
closure rejects the very workflow it is supposed to accept, and the only way out
of that would be to weaken the rule until it stopped catching anything — the
exact drift R0 flagged.

The property that actually gates publication is authority:

  * no write permission at any level, under any spelling;
  * no credential — no `secrets.*`, no `${{ github.token }}`;
  * no deployment `environment:`, which is where a protected credential lives;
  * no registry LOGIN anywhere in the closure, that being the operation that
    turns a credential into registry write authority;
  * registry WRITE verbs only inside the two files carrying the
    `local-registry-protocol` role in `boundary.INVENTORY`, and those refuse any
    non-loopback host unless handed `--allow-remote`;
  * so: no `--allow-remote`, and no non-loopback registry literal on any command
    line that reaches a registry client.

Each of those is a separately named property, so a finding says which one, and a
fixture can require that its own property is the one that fired.

The closure is followed into repository-local scripts, and into Python modules
those scripts import, because "the workflow contains no push" is a claim about
the workflow's text and not about what it runs.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

import yaml

import boundary
from boundary import Finding, strip_comments

#: The complete read-only permission set a validation workflow may hold. Any
#: scope absent from this mapping is refused, at either level, at any value.
PERMITTED_PERMISSIONS: dict[str, str] = {"contents": "read"}

#: Scalar `permissions:` forms. `read-all` is refused as well as `write-all`:
#: the contract permits an exact set, and "every read scope there is" is not it.
_PERMITTED_SCALARS: frozenset[str] = frozenset()

_REGISTRY_CLIENTS = r"(?:oras|docker|podman|buildah|skopeo|crane|helm|nerdctl|regctl)"
_LOGIN = re.compile(rf"\b{_REGISTRY_CLIENTS}\s+(?:registry\s+)?login\b", re.I)
_LOGIN_ACTION = re.compile(r"\b(?:docker/login-action|redhat-actions/podman-login)\b", re.I)
_PASSWORD_FLAG = re.compile(r"--password-stdin\b|--password[ =]|(?<!\w)-p\s+\$", re.I)
_WRITE_VERB = re.compile(
    rf"\b{_REGISTRY_CLIENTS}\s+(?:(?:blob|manifest|repo|index)\s+)?"
    r"(?:push|cp|copy|tag|attach|attest|sign)\b",
    re.I,
)
_PROTOCOL_WRITE = re.compile(r"oci-protocol\.sh\s+(?:\S+\s+)*?(push|tag)\b")
_ALLOW_REMOTE = re.compile(r"--allow-remote\b")
_GH_RELEASE_UPLOAD = re.compile(r"\bgh\s+release\s+(?:upload|create)\b", re.I)

#: A registry reference literal. Loopback is the only permitted host.
_REGISTRY_LITERAL = re.compile(
    r"\b(?P<host>(?:[a-z0-9-]+\.)+[a-z]{2,}(?::\d+)?|localhost(?::\d+)?|127\.0\.0\.1(?::\d+)?)"
    r"/(?:[a-z0-9._-]+/)+[a-z0-9._-]+",
    re.I,
)
_LOOPBACK_HOSTS = {"localhost", "127.0.0.1", "::1", "[::1]"}

_SECRETS = re.compile(r"\$\{\{[^}]*\bsecrets\s*\.\s*([A-Za-z_]\w*)", re.I)
_GITHUB_TOKEN = re.compile(r"\$\{\{[^}]*\bgithub\s*\.\s*token\b", re.I)


def _is_loopback(host: str) -> bool:
    return host.split(":")[0].lower() in _LOOPBACK_HOSTS


# ── walking the parsed document ─────────────────────────────────────────────
def _scalars(node, path: tuple[str, ...] = ()):
    """Every scalar in the parsed document, with the key path that reached it.

    Keys are yielded as well as values: an action input named after a secret, or
    a `run` body used as a mapping key, is still text this workflow carries.
    """
    if isinstance(node, dict):
        for key, value in node.items():
            yield from _scalars(key, path + ("<key>",))
            yield from _scalars(value, path + (str(key),))
    elif isinstance(node, (list, tuple)):
        for index, value in enumerate(node):
            yield from _scalars(value, path + (str(index),))
    elif isinstance(node, str):
        yield path, node
    elif node is not None and not isinstance(node, bool):
        yield path, str(node)


def _jobs(doc) -> dict:
    jobs = doc.get("jobs") if isinstance(doc, dict) else None
    return jobs if isinstance(jobs, dict) else {}


# ── property: permissions ───────────────────────────────────────────────────
def _check_permission_block(block, where: str, findings: list[Finding]) -> None:
    if block is None:
        return
    if isinstance(block, str):
        scalar = block.strip().lower()
        if scalar not in _PERMITTED_SCALARS:
            prop = ("permissions.scalar-write-all" if scalar == "write-all"
                    else "permissions.scalar-read-all" if scalar == "read-all"
                    else "permissions.scalar-form")
            findings.append(Finding(
                prop,
                f"{where} sets `permissions: {block}`, a scalar form the contract does not "
                f"permit; the permitted set is exactly {PERMITTED_PERMISSIONS}",
            ))
        return
    if not isinstance(block, dict):
        findings.append(Finding(
            "permissions.unparseable",
            f"{where} has a `permissions:` block that is neither a scalar nor a mapping",
        ))
        return
    for raw_scope, raw_value in block.items():
        scope = str(raw_scope).strip().lower()
        value = str(raw_value).strip().lower()
        permitted = PERMITTED_PERMISSIONS.get(scope)
        if value == "write":
            prop = ("permissions.packages-write" if scope == "packages"
                    else "permissions.write-scope")
            findings.append(Finding(
                prop,
                f"{where} grants `{raw_scope}: {raw_value}`; no write permission is "
                f"permitted at any level",
            ))
        elif permitted is None:
            findings.append(Finding(
                "permissions.scope-outside-permitted-set",
                f"{where} names the scope `{raw_scope}`, which is outside the permitted "
                f"read-only set {PERMITTED_PERMISSIONS}",
            ))
        elif value != permitted:
            findings.append(Finding(
                "permissions.value-outside-permitted-set",
                f"{where} sets `{raw_scope}: {raw_value}`, but the permitted set requires "
                f"`{scope}: {permitted}`",
            ))


def check_permissions(doc, workflow: str) -> list[Finding]:
    findings: list[Finding] = []
    top = doc.get("permissions") if isinstance(doc, dict) else None
    if top is None:
        findings.append(Finding(
            "permissions.absent-at-workflow-level",
            f"{workflow} declares no workflow-level `permissions:`; the default token is "
            f"whatever the repository setting happens to be, which is not a closed set",
        ))
    else:
        _check_permission_block(top, f"{workflow} (workflow level)", findings)
        if isinstance(top, dict):
            resolved = {str(k).lower(): str(v).lower() for k, v in top.items()}
            if resolved != PERMITTED_PERMISSIONS:
                findings.append(Finding(
                    "permissions.workflow-level-not-exact",
                    f"{workflow} resolves to {resolved} at workflow level, not exactly "
                    f"{PERMITTED_PERMISSIONS}",
                ))

    for name, job in _jobs(doc).items():
        if not isinstance(job, dict):
            continue
        _check_permission_block(job.get("permissions"), f"{workflow} job `{name}`", findings)
        if isinstance(job.get("permissions"), dict) and isinstance(top, dict):
            widened = {
                str(k).lower(): str(v).lower() for k, v in job["permissions"].items()
            }
            base = {str(k).lower(): str(v).lower() for k, v in top.items()}
            for scope, value in widened.items():
                if base.get(scope) != value:
                    findings.append(Finding(
                        "permissions.job-widens-workflow",
                        f"{workflow} job `{name}` sets `{scope}: {value}`, which the "
                        f"workflow-level block does not grant",
                    ))
        if job.get("environment") is not None:
            findings.append(Finding(
                "permissions.deployment-environment",
                f"{workflow} job `{name}` declares a deployment environment "
                f"({job['environment']!r}); that is where a protected publication "
                f"credential lives",
            ))
    return findings


# ── property: credentials ───────────────────────────────────────────────────
def check_credentials(doc, workflow: str, closure_text: dict[str, str]) -> list[Finding]:
    findings: list[Finding] = []
    sources = [(f"{workflow} at {'.'.join(path) or '<root>'}", text)
               for path, text in _scalars(doc)]
    sources += [(path, text) for path, text in closure_text.items()]

    for where, text in sources:
        if _GITHUB_TOKEN.search(text):
            findings.append(Finding(
                "credential.github-token",
                f"{where} references ${{{{ github.token }}}}",
            ))
        for name in _SECRETS.findall(text):
            prop = ("credential.secrets-github-token" if name.upper() == "GITHUB_TOKEN"
                    else "credential.secrets-reference")
            findings.append(Finding(
                prop, f"{where} references ${{{{ secrets.{name} }}}}"))
    return findings


# ── property: registry authority ────────────────────────────────────────────
def _run_bodies(doc, workflow: str) -> list[tuple[str, str]]:
    """Every inline `run:` body, with the job and step that carries it."""
    bodies: list[tuple[str, str]] = []
    for name, job in _jobs(doc).items():
        if not isinstance(job, dict):
            continue
        for index, step in enumerate(job.get("steps") or []):
            if isinstance(step, dict) and isinstance(step.get("run"), str):
                label = step.get("name") or f"step {index}"
                bodies.append((f"{workflow} job `{name}` / {label}", step["run"]))
    return bodies


def check_registry_authority(
    doc, workflow: str, closure_text: dict[str, str], exempt: set[str]
) -> list[Finding]:
    findings: list[Finding] = []
    inline = _run_bodies(doc, workflow)
    everything = inline + [(path, text) for path, text in closure_text.items()]

    for where, text in everything:
        body = strip_comments(where, text)
        if _LOGIN.search(body) or _LOGIN_ACTION.search(body):
            findings.append(Finding(
                "registry.login",
                f"{where} logs in to a registry; a login is what turns a credential into "
                f"registry write authority",
            ))
        if _PASSWORD_FLAG.search(body):
            findings.append(Finding(
                "registry.credential-to-client",
                f"{where} pipes a credential to a registry client",
            ))
        if _ALLOW_REMOTE.search(body) and Path(where).name not in exempt:
            findings.append(Finding(
                "registry.allow-remote",
                f"{where} passes --allow-remote, which lifts the loopback restriction "
                f"oci-protocol.sh applies to every registry write",
            ))
        if _GH_RELEASE_UPLOAD.search(body):
            findings.append(Finding(
                "registry.release-upload",
                f"{where} uploads a release asset",
            ))

    # A WRITE verb is permitted only in a file whose declared role is
    # local-registry-protocol. Never in an inline `run:` body, and never in any
    # other helper — including a helper reached only through another helper.
    for where, text in inline:
        body = strip_comments(where, text)
        if _WRITE_VERB.search(body) or _PROTOCOL_WRITE.search(body):
            findings.append(Finding(
                "registry.write-verb-inline",
                f"{where} runs a registry write operation inline; writes are permitted "
                f"only inside the local-registry-protocol files "
                f"{sorted(exempt)}, which refuse any non-loopback host",
            ))
    for path, text in closure_text.items():
        if Path(path).name in exempt:
            continue
        body = strip_comments(path, text)
        match = _WRITE_VERB.search(body) or _PROTOCOL_WRITE.search(body)
        if match:
            findings.append(Finding(
                "registry.write-verb-in-helper",
                f"{path} runs a registry write operation ({match.group(0)!r}) but does not "
                f"carry the local-registry-protocol role; only "
                f"{sorted(exempt)} may write to a registry",
            ))

    # A non-loopback registry literal on a line that reaches a registry client.
    #
    # The local-registry-protocol files are exempt HERE too, and for a reason
    # that is checked rather than asserted: `registry-controls.sh` proves the
    # loopback guard by offering `oci-protocol.sh` a non-loopback target and
    # requiring it to refuse. Reading that control as the workflow's capability
    # would make the guard unprovable — the only way to demonstrate a refusal is
    # to name the thing being refused.
    for where, text in everything:
        if Path(where).name in exempt:
            continue
        body = strip_comments(where, text)
        for line_no, line in enumerate(body.splitlines(), start=1):
            if not re.search(rf"{_REGISTRY_CLIENTS}|--repo\b|oci-protocol\.sh", line, re.I):
                continue
            for match in _REGISTRY_LITERAL.finditer(line):
                host = match.group("host")
                if not _is_loopback(host):
                    findings.append(Finding(
                        "registry.non-loopback-target",
                        f"{where}:{line_no} names the non-loopback registry "
                        f"{match.group(0)!r} on a registry command line",
                    ))
    return findings


# ── driving it ──────────────────────────────────────────────────────────────
def workflow_closure_text(
    root: Path, workflow: str, extra_scripts: tuple[str, ...] = ()
) -> dict[str, str]:
    text: dict[str, str] = {}
    for path in boundary.local_script_closure(root, workflow, extra_scripts):
        if path == workflow:
            continue
        if Path(path).name in boundary.POLICY_SELF:
            # The checker and its fixtures are never the file under test. See
            # boundary.POLICY_SELF for why this is narrow rather than a hole.
            continue
        target = root / path
        if target.is_file():
            text[path] = target.read_text(encoding="utf-8", errors="replace")
    return text


def evaluate(
    root: Path, workflow: str, extra_scripts: tuple[str, ...] = ()
) -> list[Finding]:
    """Every way `workflow` could publish, as a list of named findings.

    `workflow` may be an absolute path outside the repository — that is how a
    permission fixture evaluates a mutated copy without ever writing it into the
    tree under review.
    """
    source = (root / workflow).read_text(encoding="utf-8")
    try:
        doc = yaml.safe_load(source)
    except yaml.YAMLError as error:
        return [Finding("workflow.unparseable", f"{workflow} is not valid YAML: {error}")]
    if not isinstance(doc, dict):
        return [Finding("workflow.unparseable", f"{workflow} does not parse to a mapping")]

    closure_text = workflow_closure_text(root, workflow, extra_scripts)
    exempt = set(boundary.LOCAL_REGISTRY_PROTOCOL)
    return (
        check_permissions(doc, workflow)
        + check_credentials(doc, workflow, closure_text)
        + check_registry_authority(doc, workflow, closure_text, exempt)
    )


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: publication_policy.py <workflow.yml>", file=sys.stderr)
        return 2
    workflow = argv[1]
    root = boundary.repo_root()
    relative = str(Path(workflow).resolve().relative_to(root))
    findings = evaluate(root, relative)
    if findings:
        print(f"W1-A4 HARD STOP: {relative} has {len(findings)} publication capability "
              f"finding(s)", file=sys.stderr)
        for finding in findings:
            print(f"  FAIL [{finding.prop}] {finding.message}", file=sys.stderr)
        return 1
    print(f"{relative} resolves to exactly {PERMITTED_PERMISSIONS} at every level, holds no "
          f"credential, declares no environment, performs no registry login, and reaches no "
          f"registry write outside the loopback-restricted protocol")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
