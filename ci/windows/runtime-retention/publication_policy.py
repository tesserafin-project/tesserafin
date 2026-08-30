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

import ipaddress
import re
import hashlib
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

#: A registry reference literal.
#:
#: R1 spelled the authority as a three-way alternation of dotted name, the
#: literal `localhost` and the literal `127.0.0.1`. Nothing in it could match a
#: BRACKETED authority, so `[2001:db8::1]:5000/tesserafin/runtime` was not
#: recognised as a registry literal at all and `registry.non-loopback-target`
#: could never fire on it — a remote IPv6 registry passed the check by not
#: being seen. The recognition problem has to be fixed before the classification
#: problem: an authority that is never matched is never classified.
#:
#: So every authority shape reaches the parser, INCLUDING the malformed ones.
#: `[::1` with no closing bracket must be matched and then refused, not skipped.
#: Every branch runs to the `/` that starts the repository path, so a MALFORMED
#: authority is matched whole and handed to the parser rather than being trimmed
#: into a well-formed prefix. `[::1]x` must arrive as `[::1]x`, not as `[::1]`.
_AUTHORITY = (
    r"(?:"
    r"\[[0-9A-Za-z:.%_-]*\][^\s/'\"]*"           # bracketed, closed, and any tail
    r"|\[[^\s/'\"]*"                             # bracketed, UNCLOSED: malformed
    r"|[^\s/@'\"]+@[^\s/'\"]+"                    # anything carrying userinfo
    r"|(?:[a-z0-9-]+\.)+[a-z0-9-]+(?::[^\s/'\"]*)?"  # dotted name or dotted quad
    r"|localhost(?::[^\s/'\"]*)?"
    r"|(?:[0-9A-Fa-f]*:){2,}[0-9A-Fa-f]*(?::\d+)?" # bare, UNBRACKETED IPv6
    r"|:\d+"                                      # a port with no host in front
    r")"
)
_REGISTRY_LITERAL = re.compile(
    rf"(?<![\w.-])(?P<host>{_AUTHORITY})/(?:[a-z0-9._-]+/)+[a-z0-9._-]+",
    re.I,
)


class Authority:
    """A registry authority, parsed structurally rather than string-matched."""

    __slots__ = ("raw", "userinfo", "host", "port", "bracketed", "malformed")

    def __init__(self, raw: str) -> None:
        self.raw = raw
        self.userinfo: str | None = None
        self.host = ""
        self.port: str | None = None
        self.bracketed = False
        self.malformed: str | None = None

    def __repr__(self) -> str:  # pragma: no cover - diagnostics only
        return f"Authority({self.raw!r} host={self.host!r} port={self.port!r})"


def parse_authority(raw: str) -> Authority:
    """Split `[userinfo@]host[:port]` without guessing.

    `host.split(":")[0]`, which R1 used, is wrong for every authority a colon
    can legitimately appear inside. On `[::1]:5000` it yields `[`, on `::1:5000`
    it yields the empty string, and on `user:pass@evil.example` it yields
    `user`. Each of those then compares unequal to every loopback spelling and
    the caller reads the result as "not loopback" for the right reason by
    accident, or — worse, for `[::1]` — as loopback because the literal
    `"[::1]"` happened to be in the set.
    """
    authority = Authority(raw)
    rest = raw

    # Userinfo is whatever precedes the LAST `@`; a host may not contain one.
    if "@" in rest:
        authority.userinfo, _, rest = rest.rpartition("@")

    if rest.startswith("["):
        end = rest.find("]")
        if end == -1:
            authority.malformed = "unclosed-bracket"
            authority.host = rest[1:]
            return authority
        authority.bracketed = True
        authority.host = rest[1:end]
        tail = rest[end + 1:]
        if tail:
            if not tail.startswith(":"):
                authority.malformed = "junk-after-bracket"
                return authority
            authority.port = tail[1:]
    elif rest.count(":") > 1:
        # Unbracketed and carrying more than one colon. `::1:5000` cannot be
        # split into host and port without guessing which colon is the
        # separator, and RFC 3986 requires the brackets precisely so that
        # nobody has to. Refused as malformed rather than parsed.
        authority.malformed = "unbracketed-ipv6"
        authority.host = rest
        return authority
    else:
        authority.host, sep, port = rest.partition(":")
        if sep:
            authority.port = port

    if authority.port is not None and not authority.port.isdigit():
        authority.malformed = "non-numeric-port"
    if not authority.host:
        authority.malformed = authority.malformed or "empty-host"
    return authority


#: The only local registry forms this contract supports. IPv6 IS supported, so
#: a valid `[::1]` is accepted rather than refused for want of a rule — and any
#: other valid IPv6 form fails closed, which is the point of naming the
#: supported set instead of the refused one.
SUPPORTED_LOCAL_FORMS = (
    "the name `localhost`",
    "an IPv4 address in 127.0.0.0/8",
    "a bracketed IPv6 loopback, written `[::1]` or `[0:0:0:0:0:0:0:1]`",
)

#: The IPv6 loopback SPELLINGS this contract supports, as text.
#:
#: `ipaddress` would happily call `::0001` and `0:0::1` loopback too. They are
#: not accepted, because the same decision has to be reachable by
#: `oci-protocol.sh` in bash, which has no address parser, and two independent
#: hand-written parsers that agree only on the cases someone remembered to test
#: are a drift waiting to happen. `loopback-corpus.py` runs one corpus through
#: both and requires identical verdicts, so the supported set is textual on both
#: sides and a valid-but-unsupported IPv6 form fails CLOSED under its own reason
#: rather than being silently permitted by whichever side is more generous.
SUPPORTED_IPV6_LOOPBACK: frozenset[str] = frozenset({"::1", "0:0:0:0:0:0:0:1"})


def local_authority_verdict(raw: str) -> tuple[bool, str]:
    """(permitted, reason). A permitted authority is a supported LOCAL one."""
    authority = parse_authority(raw)
    if authority.userinfo is not None:
        return False, "embedded-credentials"
    if authority.malformed is not None:
        return False, authority.malformed
    host = authority.host.lower()
    if host == "localhost":
        return True, "localhost"
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        # Not an address at all: a name that merely CONTAINS a loopback
        # spelling. `localhost.evil` and `127.0.0.1.evil` both land here, and
        # both resolve wherever their owner points them.
        return False, "non-loopback-name"
    if address.version == 6:
        if not authority.bracketed:
            return False, "unbracketed-ipv6"
        if host not in SUPPORTED_IPV6_LOOPBACK:
            # Fail closed, under a reason that says which of the two it is.
            return False, ("ipv6-form-not-supported" if address.is_loopback
                           else "non-loopback-address")
        return True, "loopback-address"
    if authority.bracketed:
        return False, "bracketed-ipv4"
    if not address.is_loopback:
        return False, "non-loopback-address"
    return True, "loopback-address"


def _is_loopback(host: str) -> bool:
    return local_authority_verdict(host)[0]

_SECRETS = re.compile(r"\$\{\{[^}]*\bsecrets\s*\.\s*([A-Za-z_]\w*)", re.I)
_GITHUB_TOKEN = re.compile(r"\$\{\{[^}]*\bgithub\s*\.\s*token\b", re.I)


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


def check_permissions(doc, workflow: str, called: bool = False) -> list[Finding]:
    findings: list[Finding] = []
    top = doc.get("permissions") if isinstance(doc, dict) else None
    if top is None:
        inherited = ("whatever the CALLER grants it" if called
                     else "whatever the repository setting happens to be")
        findings.append(Finding(
            "permissions.absent-at-workflow-level",
            f"{workflow} declares no workflow-level `permissions:`; the token it runs with "
            f"is {inherited}, which is not a closed set. A permission set inherited from a "
            f"caller is not a reason to stop reading the callee: the callee is where the "
            f"token is USED",
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
                permitted, reason = local_authority_verdict(host)
                if not permitted:
                    findings.append(Finding(
                        "registry.non-loopback-target",
                        f"{where}:{line_no} names the registry {match.group(0)!r} on a "
                        f"registry command line; its authority {host!r} is refused as "
                        f"{reason}. The supported local forms are "
                        f"{', '.join(SUPPORTED_LOCAL_FORMS)}",
                    ))
    return findings


# ── property: the reusable-workflow graph ───────────────────────────────────
#
# R1's finding D1. Everything above reads ONE workflow file. `jobs.<id>.uses`
# hands the whole job to another workflow, and that other workflow has its own
# `permissions:`, its own steps, its own `run:` bodies and its own `uses:`. A
# validation workflow that grants itself nothing and calls a local reusable
# workflow holding `packages: write` publishes exactly as effectively as one
# that held the grant itself — and R1 would have reported it as closed and
# read-only, because it never opened the second file.
#
# So the closure is the GRAPH, not the file. Every node reachable from the
# validation workflow is parsed and subjected to the same three property
# checks, and the edges themselves are checked too: a reference that leaves
# `.github/workflows/`, a symlinked workflow file, an external `uses`, a cycle
# and a callee that is not a `workflow_call` workflow are each a named finding.
#
# `secrets:` is an edge property and is refused at the edge. `secrets: inherit`
# hands the caller's entire secret context to the callee in three words, and no
# amount of reading the callee's text can tell you what that context contains.

WORKFLOWS_DIR = ".github/workflows"

#: External reusable workflows permitted in the validation closure. Empty, and
#: emptiness is the policy: an external callee is somebody else's file at
#: somebody else's revision, and nothing in this repository gates what it
#: becomes. A future exception belongs in a separately frozen allowlist with its
#: own argument, not in a widened regex here.
PERMITTED_EXTERNAL_REUSABLE: frozenset[str] = frozenset()


def _uses_edges(doc, workflow: str) -> list[tuple[str, str, dict]]:
    """Every `jobs.<id>.uses` in `doc`, as (job id, uses string, job mapping)."""
    edges = []
    for name, job in _jobs(doc).items():
        if isinstance(job, dict) and isinstance(job.get("uses"), str):
            edges.append((str(name), job["uses"], job))
    return edges


def _check_edge_credentials(job: dict, where: str, findings: list[Finding]) -> None:
    secrets = job.get("secrets")
    if isinstance(secrets, str) and secrets.strip().lower() == "inherit":
        findings.append(Finding(
            "credential.secrets-inherit",
            f"{where} passes `secrets: inherit`, which hands the caller's entire secret "
            f"context to the called workflow. Nothing in the callee's text can bound what "
            f"that contains, so it is forbidden in the validation closure outright",
        ))
    elif isinstance(secrets, dict) and secrets:
        findings.append(Finding(
            "credential.secrets-mapping",
            f"{where} maps {sorted(str(k) for k in secrets)} into the called workflow; a "
            f"validation workflow passes no credential to anything",
        ))
    elif secrets is not None and not isinstance(secrets, (str, dict)):
        findings.append(Finding(
            "credential.secrets-unparseable",
            f"{where} has a `secrets:` value that is neither `inherit` nor a mapping",
        ))


def _resolve_local_uses(
    uses: str, uses_root: Path, where: str, findings: list[Finding]
) -> str | None:
    """The repository-relative path a local `uses:` names, or None with findings."""
    if uses.startswith("/"):
        findings.append(Finding(
            "workflow.absolute-uses",
            f"{where} references {uses!r} by absolute path; a local reusable workflow is "
            f"named relative to the repository root and nothing else",
        ))
        return None
    if not uses.startswith("./"):
        allowed = uses.split("@")[0] in PERMITTED_EXTERNAL_REUSABLE
        findings.append(Finding(
            "workflow.external-reusable" if not allowed else "workflow.external-permitted",
            f"{where} calls the external reusable workflow {uses!r}; an external callee is "
            f"another repository's file at another repository's revision, and no gate here "
            f"decides what it becomes. The permitted set is "
            f"{sorted(PERMITTED_EXTERNAL_REUSABLE) or 'empty'}",
        ))
        return None

    relative = uses[2:]
    if ".." in Path(relative).parts:
        findings.append(Finding(
            "workflow.traversal-uses",
            f"{where} references {uses!r}, which traverses out of the directory it names",
        ))
        return None
    if not relative.startswith(WORKFLOWS_DIR + "/"):
        findings.append(Finding(
            "workflow.uses-outside-workflows-dir",
            f"{where} references {uses!r}, which is not under {WORKFLOWS_DIR}/",
        ))
        return None

    target = uses_root / relative
    workflows_dir = (uses_root / WORKFLOWS_DIR).resolve()

    # The file AND every directory between it and the workflows directory. A
    # symlinked parent leaves the file itself a perfectly ordinary regular file.
    component = target
    while True:
        if component.is_symlink():
            findings.append(Finding(
                "workflow.symlinked-workflow",
                f"{where} references {uses!r}, and {component} is a symbolic link; a "
                f"workflow reached through a link carries a name from inside "
                f"{WORKFLOWS_DIR}/ and content from wherever the link points",
            ))
            return None
        if component.parent == component or component.resolve() == workflows_dir:
            break
        component = component.parent

    if not target.is_file():
        findings.append(Finding(
            "workflow.uses-target-missing",
            f"{where} references {uses!r}, which is not a file",
        ))
        return None
    try:
        target.resolve().relative_to(workflows_dir)
    except ValueError:
        findings.append(Finding(
            "workflow.uses-escapes-workflows-dir",
            f"{where} references {uses!r}, which resolves to {target.resolve()}, outside "
            f"{workflows_dir}",
        ))
        return None
    return relative


def _declares_workflow_call(doc) -> bool:
    # `on:` is a YAML 1.1 boolean, so PyYAML hands the key back as True.
    triggers = doc.get("on", doc.get(True)) if isinstance(doc, dict) else None
    if isinstance(triggers, str):
        return triggers == "workflow_call"
    if isinstance(triggers, list):
        return "workflow_call" in triggers
    if isinstance(triggers, dict):
        return "workflow_call" in triggers
    return False


def workflow_graph(
    root: Path, entry: str, uses_root: Path
) -> tuple[list[tuple[str, dict]], list[Finding]]:
    """Every workflow reachable from `entry`, with the findings the edges produced.

    Nodes are returned in discovery order, each exactly once. A node reached by
    two different callers is inspected once — its findings do not depend on who
    called it — but EVERY edge is validated, so a second alias cannot smuggle a
    `secrets: inherit` or a traversal past the check by pointing at a file that
    has already been seen.
    """
    findings: list[Finding] = []
    nodes: list[tuple[str, dict]] = []
    inspected: set[str] = set()

    def visit(path: str, doc, stack: tuple[str, ...]) -> None:
        for job_id, uses, job in _uses_edges(doc, path):
            where = f"{path} job `{job_id}`"
            _check_edge_credentials(job, where, findings)
            target = _resolve_local_uses(uses, uses_root, where, findings)
            if target is None:
                continue
            if target in stack:
                findings.append(Finding(
                    "workflow.reusable-cycle",
                    f"{where} calls {uses!r}, closing the cycle "
                    f"{' -> '.join(stack + (target,))}",
                ))
                continue
            source = (uses_root / target).read_text(encoding="utf-8", errors="replace")
            try:
                called = yaml.safe_load(source)
            except yaml.YAMLError as error:
                findings.append(Finding(
                    "workflow.called-unparseable",
                    f"{where} calls {uses!r}, which is not valid YAML: {error}",
                ))
                continue
            if not isinstance(called, dict):
                findings.append(Finding(
                    "workflow.called-unparseable",
                    f"{where} calls {uses!r}, which does not parse to a mapping",
                ))
                continue
            if not _declares_workflow_call(called):
                findings.append(Finding(
                    "workflow.not-workflow-call",
                    f"{where} calls {uses!r}, which does not declare `on: workflow_call`; "
                    f"a file that is not a reusable workflow cannot be reasoned about as "
                    f"one",
                ))
                # Still traversed. A callee GitHub would refuse is not a callee
                # this policy may skip: skipping it is how a node leaves the
                # closure while still being named by it.
            if target in inspected:
                continue
            inspected.add(target)
            nodes.append((target, called))
            visit(target, called, stack + (target,))

    visit(entry, _load(uses_root, entry), (entry,))
    return nodes, findings


def _load(root: Path, workflow: str):
    return yaml.safe_load((root / workflow).read_text(encoding="utf-8"))


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
    root: Path,
    workflow: str,
    extra_scripts: tuple[str, ...] = (),
    uses_root: Path | None = None,
) -> list[Finding]:
    """Every way `workflow` could publish, as a list of named findings.

    `workflow` may be an absolute path outside the repository — that is how a
    permission fixture evaluates a mutated copy without ever writing it into the
    tree under review.

    `uses_root` is the root a local `jobs.<id>.uses` resolves against, and
    defaults to `root`. A reusable-workflow fixture builds a complete throwaway
    `.github/workflows/` tree and points this at it, so the graph traversal
    under test is the real one rather than a second resolver written for the
    fixture. Script closure resolution keeps using `root`, because that is where
    the repository's scripts are.
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
    findings = (
        check_permissions(doc, workflow)
        + check_credentials(doc, workflow, closure_text)
        + check_registry_authority(doc, workflow, closure_text, exempt)
    )

    # The rest of the graph. Each node gets the SAME three checks, because a
    # grant, a credential or a push is no less effective for being one file
    # further away from the trigger.
    nodes, graph_findings = workflow_graph(root, workflow, uses_root or root)
    findings += graph_findings
    for path, called in nodes:
        label = f"{path} (reached from {workflow})"
        called_text = workflow_closure_text(root, path) if (root / path).is_file() else {}
        findings += check_permissions(called, label, called=True)
        findings += check_credentials(called, label, called_text)
        findings += check_registry_authority(called, label, called_text, exempt)
    return findings


# ── property: the publication summary is the approved prose, byte for byte ──
#
# W1-A5-V1-R0 found that nothing stopped the publication workflow's step summary
# from regaining an arbitrary visibility assertion. The workflow's own prose is
# the only place where a claim about the package's registry visibility can be
# reintroduced into the record without touching a single reviewed byte of the
# accepted unit, and a reviewer reading a later diff has nothing to compare it
# against.
#
# This pins it. The approved `run:` scalar of exactly one step, in exactly one
# job, of exactly one workflow, is frozen by SHA-256.
#
# It is deliberately NOT a content check. No keyword list, no `PRIVATE`/`PUBLIC`
# regex, no blacklist of known bad phrasings, and nothing that tries to tell
# "derived shell text" from "prose". Every one of those approaches has to be
# extended each time somebody invents a new sentence, and each of them is a
# second parser that can disagree with the first. Hashing the whole scalar has
# the property the finding actually needs: a DIFFERENTLY WORDED assertion fails
# for the same named reason as a restored old one, because the only thing being
# asserted is "these are the reviewed bytes".
#
# The comparison is made against the ACTIVE YAML SCALAR that `yaml.safe_load`
# produces, not against the file's source text. Comments are already absent from
# the parsed tree, so a comment cannot smuggle text past this, and a source
# substring cannot satisfy it. Nothing is executed to decide equality.
#
# This is deliberately separate from the cannot-publish evaluation. The
# publication workflow is SUPPOSED to publish; `check_all` never runs
# `evaluate()` over it, and folding it in would make the gate satisfiable only
# by ignoring its own legitimate `packages: write` findings. This property makes
# a different, orthogonal claim: whatever that workflow publishes, the sentences
# it writes into the run summary are the reviewed ones.

#: The workflow whose summary prose is frozen.
PUBLICATION_WORKFLOW = ".github/workflows/w1-windows-runtime-publish.yml"

#: The one job, and the one step inside it, that writes the run summary.
SUMMARY_JOB = "publish"
SUMMARY_STEP = "Record what was published"

#: SHA-256 of the approved `run:` scalar, as `yaml.safe_load` yields it — block
#: indentation stripped and clip-chomped, which is what makes this stable under
#: reindentation of the surrounding YAML and unstable under any change to the
#: text itself. Regenerate deliberately, by review, never by copying whatever
#: the tree currently holds:
#:
#:     python3 -c "import publication_policy as p, boundary; \
#:                 print(p.approved_summary_sha256(boundary.repo_root()))"
APPROVED_SUMMARY_SHA256 = (
    "0dadcc185d75965aecd83001e957d64d15c0fbbcada4c06f2b57139c89cbb5cb"
)


def _summary_scalar(doc, workflow: str) -> tuple[str | None, Finding | None]:
    """The one summary step's `run:` scalar, or the finding that says why not.

    Every refusal below is the same property. A missing job, a duplicated step,
    a `run:` that is a list instead of a string and an altered sentence are all
    the same failure from this gate's point of view: the reviewed summary is not
    what this workflow would print.
    """
    prop = "summary.frozen-prose-drift"
    if not isinstance(doc, dict):
        return None, Finding(prop, f"{workflow} does not parse to a mapping")

    jobs = doc.get("jobs")
    if not isinstance(jobs, dict):
        return None, Finding(prop, f"{workflow} declares no jobs mapping")
    if SUMMARY_JOB not in jobs:
        return None, Finding(
            prop, f"{workflow} has no `{SUMMARY_JOB}` job to carry the approved summary")
    # A YAML mapping cannot hold a duplicate key by the time it is parsed, so
    # "exactly one publish job" is asserted where duplication CAN survive: the
    # step list.
    job = jobs[SUMMARY_JOB]
    if not isinstance(job, dict):
        return None, Finding(prop, f"{workflow}: `{SUMMARY_JOB}` is not a job mapping")

    steps = job.get("steps")
    if not isinstance(steps, list):
        return None, Finding(
            prop, f"{workflow}: `{SUMMARY_JOB}` declares no step list")

    matches = [
        step for step in steps
        if isinstance(step, dict) and step.get("name") == SUMMARY_STEP
    ]
    if not matches:
        return None, Finding(
            prop,
            f"{workflow}: `{SUMMARY_JOB}` has no step named {SUMMARY_STEP!r}; the "
            f"approved publication summary is missing")
    if len(matches) > 1:
        return None, Finding(
            prop,
            f"{workflow}: `{SUMMARY_JOB}` has {len(matches)} steps named "
            f"{SUMMARY_STEP!r}; which one writes the summary is not decidable")

    run = matches[0].get("run")
    if not isinstance(run, str):
        return None, Finding(
            prop,
            f"{workflow}: the {SUMMARY_STEP!r} step's `run:` is "
            f"{type(run).__name__}, not a string scalar")
    return run, None


def approved_summary_sha256(root: Path, workflow: str = PUBLICATION_WORKFLOW) -> str:
    """The hash of the summary scalar as it stands. Used to regenerate the pin."""
    doc = yaml.safe_load((root / workflow).read_text(encoding="utf-8"))
    run, finding = _summary_scalar(doc, workflow)
    if finding is not None:
        raise SystemExit(f"cannot hash the summary: {finding.message}")
    return hashlib.sha256(run.encode("utf-8")).hexdigest()


def check_summary_identity(
    root: Path, workflow: str = PUBLICATION_WORKFLOW
) -> list[Finding]:
    """`summary.frozen-prose-drift` — the publication summary is the reviewed one.

    `workflow` may be an absolute path outside the repository, exactly as in
    `evaluate`. That is how the tier-2 controls grade a mutated COPY without the
    reviewed file ever changing on disk.
    """
    prop = "summary.frozen-prose-drift"
    target = root / workflow
    try:
        source = target.read_text(encoding="utf-8")
    except OSError as error:
        return [Finding(prop, f"{workflow} cannot be read: {error}")]
    try:
        doc = yaml.safe_load(source)
    except yaml.YAMLError as error:
        return [Finding(prop, f"{workflow} is not valid YAML: {error}")]

    run, finding = _summary_scalar(doc, workflow)
    if finding is not None:
        return [finding]

    actual = hashlib.sha256(run.encode("utf-8")).hexdigest()
    if actual != APPROVED_SUMMARY_SHA256:
        return [Finding(
            prop,
            f"{workflow}: the {SUMMARY_STEP!r} summary is not the approved text. "
            f"Approved sha256:{APPROVED_SUMMARY_SHA256}, this workflow would print "
            f"sha256:{actual}. The summary prose is reviewed content: change it by "
            f"review, and move the pin in the same commit.")]
    return []


# ── property: the publication workflow is the reviewed file, byte for byte ──
#
# W1-A5-V1-R2 measured the summary pin above and found it sound in what it
# claims and useless against what it does not. Nine syntactically harmless
# additions were accepted as a live publication workflow: a second summary-
# writing step AFTER the pinned one, the same writer BEFORE it, a writer in a
# second job, an approved hash-matching DECOY beside a differently-named writer,
# a writer reached through a shell variable, a duplicate `publish:` job key with
# the rogue job FIRST, a duplicate `run:` key in the pinned step, a second
# publication-capable job carrying its own prose, and a hoist of
# `packages: write` to workflow scope.
#
# The mechanism is the same in all nine: the summary property names ONE step, so
# everything that is not that step is outside it. The three duplicate-key cases
# add a second mechanism — `yaml.safe_load` resolves a duplicate key
# last-wins and silently, so a rogue FIRST job or run scalar is not visible to
# any check that reads the parsed tree.
#
# Narrowing the search does not close either. "No other step may write
# `GITHUB_STEP_SUMMARY`" is false of the reviewed file itself — the readback
# step legitimately tees the stored-manifest comparison into the summary — and
# a name-based rule is answered by the indirection this very review used. A
# canonical re-dump of the parsed tree is a SECOND parser, which is what the
# W1-A4-R3 doctrine in verify-retention-gate-pinned.py refuses, and PyYAML's
# emitted style is not stable enough to pin across versions.
#
# So the whole file is pinned, as raw bytes, by SHA-256. Nothing is parsed,
# nothing is searched, and no property of YAML is relied upon, which is exactly
# why duplicate keys, comments, indirection and steps nobody has thought of yet
# all fail here for one reason: these are not the reviewed bytes.
#
# The summary property above is KEPT rather than replaced. It is narrower and
# strictly implied by this one, and that is its value: when only the prose
# moves, the refusal still names the prose. R1's rationale for comparing the
# ACTIVE SCALAR rather than the source text stands for that property and is
# SUPERSEDED for this one, which has no parse to be smuggled past.

#: SHA-256 of the reviewed publication workflow, as bytes on disk. Regenerate
#: deliberately, by review, never by copying whatever the tree currently holds:
#:
#:     python3 -c "import publication_policy as p, boundary; \
#:                 print(p.approved_workflow_sha256(boundary.repo_root()))"
APPROVED_WORKFLOW_SHA256 = (
    "892fdcc7badb429adcd821294421faee9995c4ac8161d2972e9062de7de7b526"
)


def approved_workflow_sha256(root: Path, workflow: str = PUBLICATION_WORKFLOW) -> str:
    """The digest `APPROVED_WORKFLOW_SHA256` must carry, for regeneration."""
    return hashlib.sha256((root / workflow).read_bytes()).hexdigest()


def check_workflow_identity(
    root: Path, workflow: str = PUBLICATION_WORKFLOW
) -> list[Finding]:
    """`publication.frozen-workflow-drift` — the whole file is the reviewed one.

    `workflow` may be an absolute path outside the repository, exactly as in
    `evaluate` and `check_summary_identity`. That is how the H controls grade a
    mutated COPY without the reviewed file ever changing on disk.
    """
    prop = "publication.frozen-workflow-drift"
    try:
        raw = (root / workflow).read_bytes()
    except OSError as error:
        return [Finding(prop, f"{workflow} cannot be read: {error}")]
    actual = hashlib.sha256(raw).hexdigest()
    if actual != APPROVED_WORKFLOW_SHA256:
        return [Finding(
            prop,
            f"{workflow} is not the reviewed publication workflow. Approved "
            f"sha256:{APPROVED_WORKFLOW_SHA256}, this tree holds sha256:{actual}. "
            f"Every step, every job, every permission grant and every sentence this "
            f"workflow would print is reviewed content: change it by review, and move "
            f"the pin in the same commit.")]
    return []


def check_all(root: Path) -> list[Finding]:
    """The gate entry point the retention orchestrator holds in its roster.

    The reviewed workflow is the RETENTION workflow, and the publication
    workflow is deliberately not evaluated here: it is supposed to publish, and
    folding it in would mean this gate could only ever be satisfied by ignoring
    its findings. `permission-fixtures.py` fixture 12 is where the publication
    workflow is required to be REFUSED, which is the assertion that keeps this
    checker honest without making the gate meaningless.

    `summary.frozen-prose-drift` and `publication.frozen-workflow-drift` are
    folded in here on purpose. They are the two claims this gate makes ABOUT the
    publication workflow, and both are orthogonal to whether that workflow may
    publish: the first says the sentences it writes into the run summary are the
    reviewed ones, the second says every byte of the file is. Keeping them
    separate from `evaluate` is what lets the publication workflow keep its
    legitimate `packages: write` findings without any check weakening another.

    Both are reported by THIS callable, which is the identity the canonical
    roster manifest pins and which `ci/windows/verify-retention-gate-pinned.py`
    calls against a witness violation for each property it declares. W1-A5-V1-R2
    finding B2 was that removing the one-line call site below left every control
    still red, because the controls called the implementation directly; B3 was
    that co-removing the call, the implementation and the controls left the
    roster passing 14/14. Neither is reachable now without moving a pin that
    lives outside this subtree.
    """
    return (evaluate(root, boundary.RETENTION_WORKFLOW)
            + check_summary_identity(root)
            + check_workflow_identity(root))


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
    nodes, _ = workflow_graph(root, relative, root)
    reached = ", ".join(path for path, _ in nodes) or "no reusable workflow"
    print(f"{relative} resolves to exactly {PERMITTED_PERMISSIONS} at every level, holds no "
          f"credential, declares no environment, performs no registry login, and reaches no "
          f"registry write outside the loopback-restricted protocol")
    print(f"the same holds for every node its reusable-workflow graph reaches: {reached}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
