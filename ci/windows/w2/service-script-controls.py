#!/usr/bin/env python3
"""Hostile controls for W2's first-party ZIP service script (W2-A5, #256).

W2-A5's claim is one sentence, and it is W0 §6's last bullet:

    the ZIP carries a first-party PowerShell script that registers, starts,
    stops and removes the Windows service for operators who prefer the ZIP, and
    that script is a convenience over the §4 service contract -- NOT a second
    installer, with no repair, no rollback and no Add/Remove Programs entry.

Half of that sentence is about what the script DOES and half is about what it
must never become. The two halves need different evidence, and the ruling
already fixes what evidence is available: registering a service needs an
administrator and a Service Control Manager, and this slice is explicitly not
authorised to start one on a runner. So:

  * OBSERVED REFUSALS, PLANT FIRST. Every refusal that can be reached without
    an SCM is driven through the REAL script -- the platform gate, the verb
    surface, the four state directories, the package layout, the quoting rule --
    and each is paired with a live INERT-proof: the same input is handed to a
    MUTATED COPY with that one check defeated, and the mutant is required to do
    the thing the real script refused. A refusal that cannot be shown to be
    load-bearing is a comment.

  * THE REAL ARGUMENT CONSTRUCTION, DRY. The script's `-Plan` parameter set
    resolves every path and builds the exact argv it would hand `sc.exe`,
    without contacting the SCM. §4's service contract is then asserted against
    that argv and against `docs/distribution/W0-windows-server.md` §4 itself,
    rather than against a transcription of it kept here.

  * A STRUCTURAL AUDIT OF THE VERB BODIES. "No rollback" and "`remove` deletes
    no file" cannot be observed on a host with no SCM: a test can only fail to
    watch a service being deleted, which is indistinguishable from the deletion
    existing and being unreachable. Those are asserted over the PowerShell AST
    -- clause by clause, over executable text only -- so that a comment
    explaining why there is no rollback cannot be mistaken for one.

  * A REAL PACK. The frozen packer's own pack-only parameter set is driven over
    a synthetic stage, and the produced ZIP is opened and read: the script must
    be a member at the frozen relative path, with the checkout's bytes.

  * RAW-BYTE PINS over every accepted W2 file this slice may not change.

Nothing here reaches a registry, publishes a server, assembles a real archive,
registers a service or modifies the repository. The RESTORE row asserts the last
of those rather than assuming it.

    python3 ci/windows/w2/service-script-controls.py
    python3 ci/windows/w2/service-script-controls.py --only M07
"""

import argparse
import hashlib
import importlib.util
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import tokenize
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))

# The one file W2-A5 adds to the production path.
SCRIPT = os.path.join(HERE, "tesserafin-server-service.ps1")

# The two files W2-A5 is authorised to change beside it, and the design record.
ASSEMBLER = os.path.join(HERE, "assemble-server-zip.ps1")
A1_CONTROLS = os.path.join(HERE, "ffmpeg-consume-controls.py")
DOC = os.path.join(REPO_ROOT, "docs", "distribution", "W2-A5-service-script.md")

# The contract this slice implements, read rather than paraphrased.
W0_DOC = os.path.join(REPO_ROOT, "docs", "distribution", "W0-windows-server.md")

# ---------------------------------------------------------------------------
# The frozen relative path. W0 §6 requires the ZIP to CARRY the script; W2-A5
# freezes where. The top level of the package directory is not a preference: the
# script derives every path it gives the SCM from its own $PSScriptRoot, so any
# other depth would make the package root an arithmetic result rather than the
# directory the operator is standing in.
# ---------------------------------------------------------------------------
FROZEN_RELATIVE_PATH = "tesserafin-server-service.ps1"

# W0 §4, "The service contract". M07 asserts the document still says each of
# these, so that a drift in §4 fails here rather than being silently outvoted by
# this file.
SERVICE_NAME = "Tesserafin"
SERVICE_DISPLAY_NAME = "Tesserafin Server"
SERVICE_DESCRIPTION = "Tesserafin media server. Manage it at http://localhost:8096."
SERVICE_START_TYPE = "delayed-auto"
FAILURE_ACTIONS = "restart/60000/restart/60000//0"

VERBS = ("register", "start", "stop", "remove")

# The script names its service through this variable, so M12 can require
# `Get-ServiceRecord`'s sc.exe call to still be the read-only query over it and
# nothing else.
SERVICE_NAME_VARIABLE = "$SERVICE_NAME"

# The four state directories §6 requires to be "always given by argument".
STATE_PARAMETERS = ("-DataDir", "-ConfigDir", "-CacheDir", "-LogDir")

# Raw-byte pins over every previously accepted W2 file this slice may NOT
# change. `.gitattributes` normalises this repository to LF, so a Windows
# checkout delivers the same bytes a Linux one does and these are safe to assert
# literally. The four files W2-A5 is authorised to touch -- the new script, the
# assembler, A1's controls and A3's/A4's pin files -- are deliberately absent:
# a pin over a file this slice edits could only ever be a pin over its own work.
FROZEN_PINS = {
    "ci/windows/w2/consume-web-payload.ps1":
        "db49f21001067a8f55ae71432ff9d47830daa454704a09800bb0e1eadf3b117c",
    "ci/windows/w2/relocate-and-start.ps1":
        "637095a09ae2e845f5359bbe57e960727cb30bf7d198efc731ba07463cae6b94",
    "ci/windows/w2/pkg-tree-digest.py":
        "0c70114c69e85d06bc3d95249cc1a86f917eb2b8deb44718cc05ad6f3afa70b4",
    "ci/windows/w2/zip-controls.py":
        "1cdd22612db0ae34b2234c73e57aa6b345fec931266fd94868a0bb37a94353c2",
    "ci/windows/w2/web-payload-controls.py":
        "60466ae4da90d9ed876e709c29c90fef025dc287ad8ffbaf5d64d1f053b6e9ea",
    "ci/windows/runtime-retention/consume.ps1":
        "f19fefcc48de9ae2175aa49ecff6e732762219a3d76c38067ba4114a1924646d",
    "ci/windows/runtime-retention/accepted-runtime.json":
        "593c21f59c67dd564fa488f660efc14b74b5c5bcd775bbc3ef0bdf9e94dd9ece",
}

# Every control this suite is required to report. A control that is deleted,
# renamed or silently skipped stops appearing here, and `main` turns that into a
# RED rather than into a smaller green suite. Without this, removing an
# inconvenient control would IMPROVE the summary line.
ROSTER = {
    "M01": "four verbs, and a fifth is refused in the script's own words",
    "M02": "register requires all four state directories and defaults none",
    "M03": "a relative state directory is refused",
    "M04": "a state directory inside the package is refused",
    "M05": "the package is the script's own directory, and no location is baked in",
    "M06": "a script outside a complete package tree is refused",
    "M07": "the plan is W0 §4's service contract, and §4 still says so",
    "M08": "--service, the four directories, --webdir and --ffmpeg always; --nowebclient never",
    "M09": "--webdir and --ffmpeg point inside the package and nowhere else",
    "M10": "every SCM argument that can carry a space is quoted, and a quote is refused",
    "M11": "-Plan reaches no Service Control Manager and changes nothing",
    "M12": "the plan and the verbs read one definition of the SCM calls",
    "M13": "no repair: register refuses a service that already exists",
    "M14": "no rollback: a failed register undoes nothing and says so",
    "M15": "no Add/Remove Programs entry, no installer engine, no machine-wide write",
    "M16": "no second copy of the server, and nothing is fetched",
    "M17": "remove refuses a running service and deletes no file",
    "M18": "without Windows or without administrator the verbs fail closed",
    "M19": "the assembler stages the script at the frozen path and pins its digest",
    "M20": "the packed archive carries the script at that path, with the checkout's bytes",
    "M21": "F18 still REDs any other new .ps1 under ci/windows/w2",
    "M22": "the doc states the frozen path, the non-goals and claims no acceptance",
    "M23": "every accepted W2 file this slice may not change is unmodified",
}


# ===========================================================================
# Reading a file for what it DOES
# ===========================================================================

def strip_commentary(path, text):
    """The executable part of a file, with comments and docstrings blanked out.

    An audit that cannot tell an invocation from a comment explaining why there
    is no invocation has two useless outcomes: it fires on the explanation, or
    it is loosened until it would miss the real thing. This file's whole subject
    is a script whose documentation says at length what it does not do, so the
    first outcome is not hypothetical here. Lines are blanked rather than
    removed, so a finding's line number still points at the real file.
    """
    suffix = os.path.splitext(path)[1].lower()
    if suffix == ".py":
        lines = text.splitlines()
        try:
            tokens = list(tokenize.generate_tokens(io.StringIO(text).readline))
        except (tokenize.TokenError, IndentationError):
            return text
        for token in tokens:
            triple = token.type == tokenize.STRING and \
                token.string.lstrip("rbuRBUf")[:3] in ('"' * 3, "'" * 3)
            if token.type == tokenize.COMMENT or triple:
                for number in range(token.start[0], token.end[0] + 1):
                    if 1 <= number <= len(lines):
                        lines[number - 1] = ""
        return "\n".join(lines)
    if suffix == ".ps1":
        lines = text.splitlines()
        inside = False
        for index, line in enumerate(lines):
            if not inside and "<#" in line:
                inside = True
            if inside:
                if "#>" in line:
                    inside = False
                lines[index] = ""
                continue
            lines[index] = re.sub(r"#.*$", "", line)
        return "\n".join(lines)
    if suffix in (".yml", ".yaml"):
        return "\n".join(re.sub(r"(^|\s)#.*$", r"\1", line) for line in text.splitlines())
    return text


def read_text(path):
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def executable_text(path, text=None):
    return strip_commentary(path, read_text(path) if text is None else text)


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def scan_for(text, pattern):
    """Every (line number, line) a pattern matches in already-stripped text."""
    compiled = re.compile(pattern, re.IGNORECASE)
    return ["%d: %s" % (number, line.strip())
            for number, line in enumerate(text.splitlines(), 1)
            if compiled.search(line)]


# ===========================================================================
# Reporting
# ===========================================================================

class Report:
    def __init__(self):
        self.rows = []

    def record(self, name, status, detail):
        self.rows.append((name, status, detail))
        print("  %-5s %-5s %s" % (name, status, detail), flush=True)

    def names(self):
        return {name for name, _, _ in self.rows}

    def counts(self):
        totals = {"PASS": 0, "RED": 0, "INERT": 0}
        for _, status, _ in self.rows:
            totals[status] = totals.get(status, 0) + 1
        return totals


POWERSHELL = None


def find_powershell():
    for candidate in ("pwsh", "pwsh.exe", "powershell.exe"):
        found = shutil.which(candidate)
        if found:
            return found
    return None


# ===========================================================================
# Driving the REAL script, and mutated copies of it
# ===========================================================================

class Package:
    """A synthetic extracted package with a copy of the script inside it.

    The script resolves the package from its own `$PSScriptRoot`, so a control
    that wants to ask it about a different package does not pass a path -- it
    puts the script somewhere else. That is also what makes "no baked install
    location" observable rather than asserted: the same bytes in two directories
    must plan two different binary paths.
    """

    def __init__(self, root, script_text=None, exe=True, web=True, ffmpeg=True):
        self.root = root
        os.makedirs(root, exist_ok=True)
        if exe:
            with open(os.path.join(root, "tesserafin.exe"), "wb") as handle:
                handle.write(b"MZ\x90\x00")
        if web:
            os.makedirs(os.path.join(root, "web"), exist_ok=True)
            with open(os.path.join(root, "web", "index.html"), "w", encoding="utf-8") as handle:
                handle.write('<script src="main.tesserafin.0123456789ab.bundle.js"></script>')
        if ffmpeg:
            os.makedirs(os.path.join(root, "ffmpeg", "bin"), exist_ok=True)
            with open(os.path.join(root, "ffmpeg", "bin", "ffmpeg.exe"), "wb") as handle:
                handle.write(b"MZ\x90\x00")
        self.script = os.path.join(root, FROZEN_RELATIVE_PATH)
        if script_text is None:
            shutil.copyfile(SCRIPT, self.script)
        else:
            with open(self.script, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(script_text)

    def run(self, *arguments, timeout=180):
        command = [POWERSHELL, "-NoProfile", "-NonInteractive", "-File", self.script]
        command += [str(argument) for argument in arguments]
        completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True, timeout=timeout, cwd=self.root)
        return completed

    def plan(self, verb="register", state=None, extra=()):
        """Run `-Plan` and return (completed, parsed-or-None)."""
        arguments = [verb, "-Plan"]
        if state is None:
            state = self.default_state()
        for name, value in state.items():
            arguments += [name, value]
        arguments += list(extra)
        completed = self.run(*arguments)
        document = None
        if completed.returncode == 0:
            try:
                document = json.loads(completed.stdout)
            except ValueError:
                document = None
        return completed, document

    def default_state(self):
        # Deliberately OUTSIDE the package, and rooted: those are two of the
        # rules under test, and a helper that quietly satisfied them would make
        # M03 and M04 untestable.
        base = os.path.join(os.path.dirname(self.root.rstrip(os.sep)), "state")
        return {
            "-DataDir": os.path.join(base, "data"),
            "-ConfigDir": os.path.join(base, "config"),
            "-CacheDir": os.path.join(base, "cache"),
            "-LogDir": os.path.join(base, "log"),
        }


def mutate(text, replacements):
    """Defeat one check. Returns (text, applied) where `applied` counts each."""
    applied = []
    for old, new in replacements:
        count = text.count(old)
        applied.append(count)
        if count:
            text = text.replace(old, new)
    return text, applied


def refusal(completed, category):
    """The named refusal this script writes, on stderr, with a non-zero exit."""
    return completed.returncode != 0 and ("W2-A5 DENY [%s]" % category) in completed.stderr


def expect_refusal(report, name, completed, category, description):
    if refusal(completed, category):
        return True
    report.record(name, "RED", "%s was not refused with [%s]: exit %d, %s"
                  % (description, category, completed.returncode,
                     (completed.stderr or completed.stdout).strip()[:200]))
    return False


def observed(report, name, work, description, drive, mutation, mutant_ok):
    """One observed refusal, paired with a live INERT-proof.

    `drive(package)` runs the input under test. The real script must refuse it;
    a copy with `mutation` applied must then NOT refuse it in the same way, or
    the refusal is not load-bearing and this reports INERT rather than a
    smaller green suite.
    """
    real = Package(os.path.join(work, name, "real", "tesserafin-server_1.0.0_win-x64"))
    if not drive(real):
        return None
    text, applied = mutate(read_text(SCRIPT), mutation)
    if not all(applied):
        report.record(name, "INERT",
                      "the mutation no longer applies (%s); the rule it defeats has moved"
                      % ", ".join("%dx" % count for count in applied))
        return None
    mutant = Package(os.path.join(work, name, "mutant", "tesserafin-server_1.0.0_win-x64"),
                     script_text=text)
    if not mutant_ok(mutant):
        report.record(name, "INERT",
                      "the mutated copy refused the same input, so the check under test is not "
                      "the one that fired")
        return None
    report.record(name, "PASS", description)
    return True


# ===========================================================================
# The PowerShell AST, clause by clause
# ===========================================================================

AST_QUERY = r'''
param([string] $Path)
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$null)

function Get-EnclosingFunction {
    param($Node)
    $current = $Node.Parent
    while ($null -ne $current) {
        if ($current -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
            return $current.Name
        }
        $current = $current.Parent
    }
    return ''
}

function Get-EnclosingClause {
    # The label of the TOP-LEVEL switch clause a node sits in, or ''. The four
    # verbs are one switch outside every function, so this is what lets a fact
    # say "this sc.exe call belongs to `remove`" without reading clause text.
    param($Node)
    $current = $Node
    while ($null -ne $current) {
        $parent = $current.Parent
        if ($parent -is [System.Management.Automation.Language.SwitchStatementAst]) {
            foreach ($clause in $parent.Clauses) {
                if ($clause.Item2 -eq $current) {
                    return $clause.Item1.Extent.Text.Trim("'", '"')
                }
            }
        }
        $current = $parent
    }
    return ''
}

function Get-RootVariable {
    # The variable an expression REACHES INTO. `$invocations[0].arguments` is a
    # use of `$invocations`, and `$invocation.arguments` is a use of
    # `$invocation`. Without this the audit could only compare strings, and a
    # rebinding would hide behind any index or member access it liked.
    param($Node)
    $current = $Node
    while ($null -ne $current) {
        if ($current -is [System.Management.Automation.Language.VariableExpressionAst]) {
            return $current.VariablePath.UserPath
        }
        elseif ($current -is [System.Management.Automation.Language.MemberExpressionAst]) {
            $current = $current.Expression
        }
        elseif ($current -is [System.Management.Automation.Language.IndexExpressionAst]) {
            $current = $current.Target
        }
        elseif ($current -is [System.Management.Automation.Language.ParenExpressionAst]) {
            $current = $current.Pipeline
        }
        elseif ($current -is [System.Management.Automation.Language.ArrayExpressionAst]) {
            $current = $current.SubExpression
        }
        elseif ($current -is [System.Management.Automation.Language.SubExpressionAst]) {
            $current = $current.SubExpression
        }
        elseif ($current -is [System.Management.Automation.Language.ConvertExpressionAst]) {
            $current = $current.Child
        }
        elseif ($current -is [System.Management.Automation.Language.AttributedExpressionAst]) {
            $current = $current.Child
        }
        elseif ($current -is [System.Management.Automation.Language.CommandExpressionAst]) {
            $current = $current.Expression
        }
        elseif ($current -is [System.Management.Automation.Language.PipelineAst]) {
            if ($current.PipelineElements.Count -ne 1) { return '' }
            $current = $current.PipelineElements[0]
        }
        elseif ($current -is [System.Management.Automation.Language.StatementBlockAst]) {
            if ($current.Statements.Count -ne 1) { return '' }
            $current = $current.Statements[0]
        }
        else { return '' }
    }
    return ''
}

function Get-CommandName {
    param($Command)
    $element = $Command.CommandElements[0]
    if ($element -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
        return $element.Value
    }
    return $element.Extent.Text
}

function Get-NamedArgument {
    # The expression a NAMED parameter carries. A positional argument returns
    # nothing on purpose: `Invoke-Sc $x $y` is not the same claim as
    # `Invoke-Sc -Arguments $y`, and the audit must not read one as the other.
    param($Command, [string] $Name)
    for ($index = 0; $index -lt $Command.CommandElements.Count; $index++) {
        $element = $Command.CommandElements[$index]
        if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and
            $element.ParameterName -ieq $Name) {
            if ($null -ne $element.Argument) { return $element.Argument }
            if ($index + 1 -lt $Command.CommandElements.Count) {
                $next = $Command.CommandElements[$index + 1]
                if ($next -isnot [System.Management.Automation.Language.CommandParameterAst]) {
                    return $next
                }
            }
            return $null
        }
    }
    return $null
}

function Test-InParamBlock {
    # A parameter's own `$Name` node DECLARES a name; it reads nothing, and
    # neither does a variable inside that parameter's attributes. Counting
    # either as a use would make every rule below fire on the declaration it
    # exists to trust.
    param($Node)
    $current = $Node
    while ($null -ne $current) {
        if ($current -is [System.Management.Automation.Language.ParamBlockAst]) { return $true }
        $current = $current.Parent
    }
    return $false
}

function Get-VariableUses {
    # Every variable a function reads or writes ANYWHERE in its body -- not
    # only in an assignment. `$Arguments.SetValue('auto', 5)` is an
    # InvokeMemberExpressionAst and `[array]::Reverse($Arguments)` is an
    # argument to one; neither is an AssignmentStatementAst, and an audit that
    # only walks assignments cannot see either. `splatted` is carried because
    # splatting is the ONE shape in which a value reaches a native command
    # without first being reachable as a value that could be changed.
    param($Function)
    $uses = @()
    foreach ($node in $Function.FindAll({
            param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] },
            $true)) {
        if (Test-InParamBlock -Node $node) { continue }
        # The smallest enclosing statement that says MORE than the variable
        # itself: `if ($Plan)` renders its condition as a one-element pipeline,
        # which is a statement whose whole text is `$Plan`, and a finding that
        # quoted that would name the mechanism without showing it.
        $statement = $node.Parent
        while ($null -ne $statement -and
               ($statement -isnot [System.Management.Automation.Language.StatementAst] -or
                $statement.Extent.Text -eq $node.Extent.Text)) {
            $statement = $statement.Parent
        }
        $text = $node.Extent.Text
        if ($null -ne $statement) { $text = $statement.Extent.Text }
        $uses += [ordered]@{
            name = $node.VariablePath.UserPath
            splatted = [bool] $node.Splatted
            text = $text
        }
    }
    return $uses
}

function Get-CommandNames {
    # Every command a function runs. A rule about which VARIABLES a function may
    # read is blind to `Set-Variable Arguments @(...)`, which names none.
    param($Function)
    return @($Function.FindAll({
        param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) |
        ForEach-Object { Get-CommandName -Command $_ })
}

$switches = $ast.FindAll({
    param($n) $n -is [System.Management.Automation.Language.SwitchStatementAst] }, $true)

$verbClauses = [ordered]@{}
$invocationClauses = [ordered]@{}
foreach ($switch in $switches) {
    $owner = Get-EnclosingFunction -Node $switch
    foreach ($clause in $switch.Clauses) {
        $label = $clause.Item1.Extent.Text.Trim("'", '"')
        $body = $clause.Item2.Extent.Text
        if ($owner -eq '') { $verbClauses[$label] = $body }
        elseif ($owner -eq 'Get-ScInvocations') { $invocationClauses[$label] = $body }
    }
}

$functions = [ordered]@{}
$invokeScFunction = $null
$invocationsFunction = $null
foreach ($function in $ast.FindAll({
        param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    $functions[$function.Name] = $function.Extent.Text
    if ($function.Name -eq 'Invoke-Sc') { $invokeScFunction = $function }
    if ($function.Name -eq 'Get-ScInvocations') { $invocationsFunction = $function }
}

$parameters = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })

# Every command the script runs, reduced to the few facts M12 joins: WHERE it
# sits, WHAT it names, and -- for the two that matter -- which variable the
# argument list it hands on reaches into. Extents are carried too, so a finding
# can quote the offending line rather than assert it.
$scCalls = @()
$invokeScCalls = @()
foreach ($command in $ast.FindAll({
        param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
    $name = Get-CommandName -Command $command
    $bare = [System.IO.Path]::GetFileNameWithoutExtension($name.Trim("'", '"'))
    if ($bare -ieq 'sc') {
        $scCalls += [ordered]@{
            function = Get-EnclosingFunction -Node $command
            clause = Get-EnclosingClause -Node $command
            elements = @($command.CommandElements | ForEach-Object { $_.Extent.Text })
            splatted = @($command.CommandElements | Where-Object {
                $_ -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $_.Splatted } | ForEach-Object { $_.VariablePath.UserPath })
            text = $command.Extent.Text
        }
    }
    if ($name -ieq 'Invoke-Sc') {
        $argument = Get-NamedArgument -Command $command -Name 'Arguments'
        $invokeScCalls += [ordered]@{
            function = Get-EnclosingFunction -Node $command
            clause = Get-EnclosingClause -Node $command
            named = ($null -ne $argument)
            argumentsText = $(if ($null -ne $argument) { $argument.Extent.Text } else { '' })
            argumentsRoot = $(if ($null -ne $argument) { Get-RootVariable -Node $argument }
                              else { '' })
            text = $command.Extent.Text
        }
    }
}

# Every assignment, with the ROOT of its target -- so `$invocations[0].x = ...`
# counts as a second binding of `$invocations` rather than as a different name.
$assignments = @()
foreach ($assignment in $ast.FindAll({
        param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
    $rightCommands = @()
    $action = ''
    foreach ($command in $assignment.Right.FindAll({
            param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
        $commandName = Get-CommandName -Command $command
        $rightCommands += $commandName
        if ($commandName -ieq 'Get-ScInvocations' -and $action -eq '') {
            $value = Get-NamedArgument -Command $command -Name 'Action'
            if ($null -ne $value) { $action = $value.Extent.Text }
        }
    }
    $assignments += [ordered]@{
        function = Get-EnclosingFunction -Node $assignment
        clause = Get-EnclosingClause -Node $assignment
        leftRoot = Get-RootVariable -Node $assignment.Left
        leftText = $assignment.Left.Extent.Text
        operator = $assignment.Operator.ToString()
        rightText = $assignment.Right.Extent.Text
        rightCommands = $rightCommands
        rightHasBinary = @($assignment.Right.FindAll({
            param($n) $n -is [System.Management.Automation.Language.BinaryExpressionAst] },
            $true)).Count -gt 0
        rightVariables = @($assignment.Right.FindAll({
            param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] },
            $true) | ForEach-Object { $_.VariablePath.UserPath })
        action = $action
    }
}

# `foreach ($invocation in $invocations[1..N])` is the second hop between the
# binding and the call, so the audit has to be able to walk it.
$foreaches = @()
foreach ($loop in $ast.FindAll({
        param($n) $n -is [System.Management.Automation.Language.ForEachStatementAst] }, $true)) {
    $foreaches += [ordered]@{
        function = Get-EnclosingFunction -Node $loop
        clause = Get-EnclosingClause -Node $loop
        variable = $loop.Variable.VariablePath.UserPath
        conditionRoot = Get-RootVariable -Node $loop.Condition
        conditionText = $loop.Condition.Extent.Text
    }
}

# The plan document's own fields, so `scInvocations` can be traced back to the
# same function the verbs run rather than assumed to describe it.
$planFields = @()
foreach ($hashtable in $ast.FindAll({
        param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true)) {
    if ((Get-EnclosingFunction -Node $hashtable) -ne 'New-Plan') { continue }
    foreach ($pair in $hashtable.KeyValuePairs) {
        $planFields += [ordered]@{
            key = $pair.Item1.Extent.Text.Trim("'", '"')
            valueText = $pair.Item2.Extent.Text
            valueRoot = Get-RootVariable -Node $pair.Item2
        }
    }
}

# Inside `Invoke-Sc`: every use of the parameter that becomes sc.exe's argv,
# and every command that runs beside it. The splat check above says the CALL
# hands over `$Arguments`; these say nothing reached that variable first.
$invokeScArgumentUses = @()
$invokeScCommands = @()
if ($null -ne $invokeScFunction) {
    $invokeScArgumentUses = @(Get-VariableUses -Function $invokeScFunction |
        Where-Object { $_.name -ieq 'Arguments' })
    $invokeScCommands = @(Get-CommandNames -Function $invokeScFunction)
}

# Inside `Get-ScInvocations`: what it may legitimately see. PowerShell's scoping
# means an unqualified `$Plan` inside this function resolves to the SCRIPT's
# `-Plan` switch, so the one definition can be made to return one argv while
# `-Plan` is printing and another while a verb is running -- without a single
# character changing at either call site.
$invocationParameters = @()
$invocationVariableUses = @()
$invocationAssigned = @()
$invocationCommands = @()
if ($null -ne $invocationsFunction) {
    if ($null -ne $invocationsFunction.Body.ParamBlock) {
        $invocationParameters = @($invocationsFunction.Body.ParamBlock.Parameters |
            ForEach-Object { $_.Name.VariablePath.UserPath })
    }
    $invocationVariableUses = @(Get-VariableUses -Function $invocationsFunction)
    $invocationAssigned = @($invocationsFunction.FindAll({
        param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] },
        $true) | ForEach-Object { Get-RootVariable -Node $_.Left })
    $invocationAssigned += @($invocationsFunction.FindAll({
        param($n) $n -is [System.Management.Automation.Language.ForEachStatementAst] },
        $true) | ForEach-Object { $_.Variable.VariablePath.UserPath })
    $invocationCommands = @(Get-CommandNames -Function $invocationsFunction)
}

[ordered]@{
    verbClauses = $verbClauses
    invocationClauses = $invocationClauses
    functions = $functions
    parameters = $parameters
    scCalls = $scCalls
    invokeScCalls = $invokeScCalls
    assignments = $assignments
    foreaches = $foreaches
    planFields = $planFields
    invokeScArgumentUses = $invokeScArgumentUses
    invokeScCommands = $invokeScCommands
    invocationParameters = $invocationParameters
    invocationVariableUses = $invocationVariableUses
    invocationAssigned = $invocationAssigned
    invocationCommands = $invocationCommands
} | ConvertTo-Json -Depth 8
'''


def ast_query(work, path):
    query = os.path.join(work, "ast-query.ps1")
    if not os.path.isfile(query):
        with open(query, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(AST_QUERY)
    completed = subprocess.run(
        [POWERSHELL, "-NoProfile", "-NonInteractive", "-File", query, path],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=180)
    if completed.returncode != 0:
        raise RuntimeError("the AST query failed: %s" % completed.stderr.strip()[:300])
    return json.loads(completed.stdout)


def clause_code(clause_text):
    """A switch clause's executable text, with its comments blanked."""
    return strip_commentary("x.ps1", clause_text)


def as_list(value):
    """ConvertTo-Json renders a one-element array as a scalar and an empty one as null."""
    if value is None:
        return []
    if isinstance(value, list):
        return value
    return [value]


def _clip(text):
    return " ".join(str(text).split())[:120]


def _unique(findings):
    """Findings in order, each once: one bad binding read by two calls is one fact."""
    seen = set()
    return [f for f in findings if not (f in seen or seen.add(f))]


# ---------------------------------------------------------------------------
# M12: does the SCM get the argument list `-Plan` printed?
#
# W2-A5-V1 answered this with a substring -- `-Arguments $invocation` had to
# appear on every line that called Invoke-Sc -- and the W2-A5-R1 ruling measured
# what that buys: a `register` that rebinds $invocations to a literal still
# writes `-Arguments $invocations[0].arguments`, and an Invoke-Sc that appends
# `obj= LocalSystem` never touches the line at all. Both left 25 PASS while
# `-Plan` printed something else. A rule that reads the shape of a line cannot
# own a claim about the VALUE that line carries.
#
# So the two plants below are not a hardcoded probe string: they are applied to
# the real script's own bytes and audited by the same function that audits the
# real script, which is what makes the INERT-proof a measurement rather than a
# tautology about a literal the control wrote itself.
# ---------------------------------------------------------------------------
V1_PLANTS = (
    ("a register clause that rebinds $invocations to a literal",
     [("            $invocations = @(Get-ScInvocations -Action 'register' "
       "-BinaryPath $binaryPath)\n",
       "            $invocations = @(\n"
       "                [ordered]@{ what = \"sc.exe create $SERVICE_NAME\"; arguments = @(\n"
       "                    'create', $SERVICE_NAME,\n"
       "                    'binPath=', $binaryPath,\n"
       "                    'start=', 'auto',\n"
       "                    'DisplayName=', $SERVICE_DISPLAY_NAME) }\n"
       "            )\n")]),
    ("an Invoke-Sc that appends obj= LocalSystem on the way to sc.exe",
     [("    $output = & sc.exe @Arguments 2>&1 | Out-String\n",
       "    $Arguments = @($Arguments) + @('obj=', 'LocalSystem')\n"
       "    $output = & sc.exe @Arguments 2>&1 | Out-String\n")]),
)


# The W2-A5-R2 ruling measured a second pair, on this same HEAD, that the rules
# above are blind to for the same reason V1's pair was: each reads a SHAPE. The
# splat check asks what the sc.exe call hands over and the rebinding check asks
# what assignments target `$Arguments`, so a mutation that is neither an
# assignment nor a change to the call site passes both; and every rule about the
# one definition asked WHO calls `Get-ScInvocations`, never what that function
# reads, so a body that can see the script's own `-Plan` switch passes all of
# them. Both were measured leaving the suite at 25 PASS / 0 RED / 0 INERT while
# `-Plan register` printed `start= delayed-auto` and the verb path built
# `start= auto`.
R2_PLANTS = (
    ("an Invoke-Sc that rewrites element 5 of $Arguments in place, with no assignment",
     [("    $output = & sc.exe @Arguments 2>&1 | Out-String\n",
       "    $Arguments.SetValue('auto', 5)\n"
       "    $output = & sc.exe @Arguments 2>&1 | Out-String\n")]),
    ("a Get-ScInvocations whose register clause branches on the script's own -Plan switch",
     [("                    'start=', $SERVICE_START_TYPE,\n",
       "                    'start=', $(if ($Plan) { $SERVICE_START_TYPE } else { 'auto' }),\n")]),
)

# Every plant M12 is required to detect on the real script's bytes. A plant that
# can no longer be applied is reported as an unmeasured rule, not as a pass.
M12_PLANTS = V1_PLANTS + R2_PLANTS

# `$true`, `$false`, `$null` and the pipeline's `$_` carry nothing about HOW the
# script was invoked, so reading one inside `Get-ScInvocations` says nothing
# about whether that function is the one definition.
SCOPE_FREE_VARIABLES = ("true", "false", "null", "_")

# Reading any of these inside `Get-ScInvocations` would let it answer `-Plan`
# and a verb differently even with no script variable named at all. They are
# named here so the finding can say WHICH mechanism it refused rather than only
# that a whitelist rejected a name.
DYNAMIC_SCOPE_VARIABLES = {
    "PSCmdlet": "the cmdlet's own invocation state, which names the parameter set",
    "PSBoundParameters": "the parameters the caller actually bound",
    "MyInvocation": "how this script was invoked",
    "args": "the caller's unbound arguments",
    "PSScriptRoot": "the script's location rather than anything it was asked for",
}

# The only commands `Invoke-Sc` and `Get-ScInvocations` may run. Without this,
# a rule about which variables a function reads is blind to `Set-Variable
# Arguments @(...)`, which replaces the splat source and names no variable at
# all, and to `Get-Variable Plan -Scope 1`, which reads the caller's scope
# without writing `$Plan`.
INVOKE_SC_COMMANDS = ("sc", "sc.exe", "out-string", "deny")
GET_SC_INVOCATIONS_COMMANDS = ("deny",)


def _script_constants(assignments):
    """Top-level names whose value cannot depend on how the script was invoked.

    A name qualifies when it is assigned exactly once in the WHOLE script, at
    the top level, by an expression that runs no command and reads only other
    names that already qualify. That is a fixed point rather than a list, so
    `$SERVICE_START_TYPE = 'delayed-auto'` qualifies, `$action = $Verb.Trim()`
    does not (it reads a script parameter), `$packageRoot = Get-PackageRoot`
    does not (it runs a command), and a `$SERVICE_START_TYPE` that a verb clause
    rebinds before calling `Invoke-Sc` stops qualifying for every reader.
    """
    counts = {}
    for row in assignments:
        if row["leftRoot"]:
            counts[row["leftRoot"]] = counts.get(row["leftRoot"], 0) + 1
    top = [row for row in assignments
           if not row["function"] and not row["clause"]
           and row["leftRoot"] and counts.get(row["leftRoot"]) == 1
           and row["operator"] == "Equals"]
    constants = set()
    growing = True
    while growing:
        growing = False
        for row in top:
            name = row["leftRoot"]
            if name in constants or as_list(row["rightCommands"]):
                continue
            reads = [used for used in as_list(row["rightVariables"])
                     if used.lower() not in SCOPE_FREE_VARIABLES]
            if all(used in constants for used in reads):
                constants.add(name)
                growing = True
    return constants


def audit_invoke_sc_arguments(tree):
    """Every finding that says sc.exe can be handed something no plan printed.

    The splat check in `audit_one_definition` reads the CALL: `& sc.exe
    @Arguments`, two elements, that parameter splatted alone. It is silent about
    everything that happens to `$Arguments` before that line, and PowerShell
    gives a plant three shapes there that are not assignments -- an in-place
    method on the array (`SetValue`, `Add`, `Insert`, `Clear`), a static helper
    taking it by reference, and `Set-Variable`, which names no variable at all.

    So this does not enumerate the shapes. It requires the opposite: inside
    `Invoke-Sc`, `$Arguments` may be SPLATTED and may be nothing else, and no
    command may run there but sc.exe, the pipeline it is read through and the
    script's own refusal. An indexed assignment is caught here as well as by the
    rebinding rule, because it is a use that is not a splat.
    """
    findings = []
    uses = as_list(tree["invokeScArgumentUses"])
    if not uses:
        return ["Invoke-Sc never mentions its own $Arguments, so whatever it hands the Service "
                "Control Manager comes from somewhere no plan describes"]
    for use in uses:
        if not use["splatted"]:
            findings.append("Invoke-Sc reaches into its own $Arguments other than by splatting "
                            "them at sc.exe, so the argument list can be changed after the plan "
                            "printed it and before the Service Control Manager sees it: %s"
                            % _clip(use["text"]))
    for name in as_list(tree["invokeScCommands"]):
        bare = os.path.splitext(name.strip("'\"").lower())[0]
        if bare not in INVOKE_SC_COMMANDS:
            findings.append("Invoke-Sc runs '%s', which can replace the value splatted at sc.exe "
                            "without naming $Arguments anywhere" % name)
    return findings


def audit_invocations_scope(tree, assignments):
    """Every finding that says the one definition can tell `-Plan` from a verb.

    `New-Plan` and the four verb clauses call the same function, which is what
    makes the printed argv evidence about the executed one -- but only while
    that function answers the same way to both. PowerShell resolves an
    unqualified `$Plan` inside `Get-ScInvocations` to the SCRIPT's `-Plan`
    switch, so the function can branch on which of its two callers is asking
    with no change at either call site and no assignment anywhere.

    The rule is therefore about what the function may READ, not about which
    names are forbidden: its own parameters, values it binds itself, and script
    constants -- names assigned once, at the top level, from an expression that
    runs nothing and reads only other constants. A script parameter is refused
    even if it were somehow also a constant, and the five dynamic-scope
    automatics are named so the finding can say which mechanism it refused.
    """
    findings = []
    parameters = set(as_list(tree["invocationParameters"]))
    bound_here = set(name for name in as_list(tree["invocationAssigned"]) if name)
    constants = _script_constants(assignments)
    script_parameters = set(as_list(tree["parameters"]))
    for use in as_list(tree["invocationVariableUses"]):
        name = use["name"]
        if name.lower() in SCOPE_FREE_VARIABLES or name in parameters or name in bound_here:
            continue
        if name in DYNAMIC_SCOPE_VARIABLES:
            findings.append("Get-ScInvocations reads $%s -- %s -- so the one definition can "
                            "return one argument list while -Plan is printing and another while "
                            "a verb is running: %s"
                            % (name, DYNAMIC_SCOPE_VARIABLES[name], _clip(use["text"])))
        elif name in script_parameters:
            findings.append("Get-ScInvocations reads the script parameter $%s, so the one "
                            "definition can answer -Plan and a verb differently: %s"
                            % (name, _clip(use["text"])))
        elif name not in constants:
            findings.append("Get-ScInvocations reads $%s, which is neither one of its own "
                            "parameters, nor a value it binds itself, nor a script constant, so "
                            "what it returns is not fixed by the action it was asked for: %s"
                            % (name, _clip(use["text"])))
    for name in as_list(tree["invocationCommands"]):
        if name.strip("'\"").lower() not in GET_SC_INVOCATIONS_COMMANDS:
            findings.append("Get-ScInvocations runs '%s', which can read the caller's scope "
                            "without naming a variable this rule could refuse" % name)
    return findings


def _trace_arguments(verb, call, clause_assignments, clause_foreaches):
    """Follow one Invoke-Sc `-Arguments` expression back to what defines it.

    Returns findings, empty when the expression reaches Get-ScInvocations for
    this verb and nothing else has touched it inside the clause.
    """
    root = call["argumentsRoot"]
    if not root:
        return ["the %s clause hands Invoke-Sc an -Arguments expression that reaches no "
                "variable: %s" % (verb, _clip(call["argumentsText"]))]
    seen = []
    while True:
        if root in seen:
            return ["the %s clause defines $%s in terms of itself" % (verb, root)]
        seen.append(root)
        bindings = [row for row in clause_assignments if row["leftRoot"] == root]
        loops = [row for row in clause_foreaches if row["variable"] == root]
        if len(bindings) + len(loops) != 1:
            return ["$%s reaches Invoke-Sc in the %s clause with %d definitions in that clause, "
                    "so no single one owns what the Service Control Manager is handed"
                    % (root, verb, len(bindings) + len(loops))]
        if loops:
            nxt = loops[0]["conditionRoot"]
            if not nxt:
                return ["the %s clause iterates an expression that reaches no variable: %s"
                        % (verb, _clip(loops[0]["conditionText"]))]
            root = nxt
            continue
        binding = bindings[0]
        commands = as_list(binding["rightCommands"])
        if commands != ["Get-ScInvocations"]:
            return ["the %s clause builds its own sc.exe argument list rather than reading the "
                    "one definition: $%s = %s" % (verb, root, _clip(binding["rightText"]))]
        if binding["rightHasBinary"]:
            return ["the %s clause extends what Get-ScInvocations returned: $%s = %s"
                    % (verb, root, _clip(binding["rightText"]))]
        if binding["operator"] != "Equals":
            return ["the %s clause binds $%s with %s, so the one definition is not the whole "
                    "value" % (verb, root, binding["operator"])]
        if binding["action"].strip("'\"") != verb:
            return ["the %s clause reads Get-ScInvocations -Action %s, so it performs a verb "
                    "other than the one -Plan would describe"
                    % (verb, binding["action"] or "(nothing)")]
        return []


def audit_one_definition(tree):
    """Every finding that says the verbs and `-Plan` are not the same claim.

    This walks the script's dataflow rather than its text. For each verb clause
    it takes the expression that clause passes to `Invoke-Sc` as `-Arguments`,
    reduces it to the variable it reaches into, follows that variable back
    through the clause's `foreach` iterators to the assignment that binds it,
    and requires that assignment to be a call to the one function `New-Plan`
    also reads, for the verb the clause actually is. It then requires
    `Invoke-Sc` to hand sc.exe exactly the parameter it was given -- unmodified
    and splatted alone -- and requires that no other place in the script reaches
    sc.exe at all, except `Get-ServiceRecord`'s read-only query.

    A verb that builds its own list is off that path at the assignment; an
    Invoke-Sc that grows tokens is off it at the splat; and a clause that calls
    sc.exe directly is off it at the call site. None of the three can be
    reached by editing a line that still reads `-Arguments $invocation`.

    Two further questions are asked by `audit_invoke_sc_arguments` and
    `audit_invocations_scope`, because the walk above cannot ask either. It
    reads the SHAPE of the call and of the assignments around it, so it is
    silent about a mutation of `$Arguments` that is neither -- and it reads WHO
    calls `Get-ScInvocations`, never what that function is allowed to see, so it
    is silent about a body that can tell `-Plan` from a verb.
    """
    findings = []
    clauses = tree["verbClauses"]
    if sorted(clauses) != sorted(VERBS):
        findings.append("the verb switch has clauses %s" % sorted(clauses))
    if sorted(tree["invocationClauses"]) != sorted(VERBS):
        findings.append("Get-ScInvocations defines %s" % sorted(tree["invocationClauses"]))
    for required in ("Get-ScInvocations", "Invoke-Sc", "New-Plan", "Get-ServiceRecord"):
        if required not in tree["functions"]:
            findings.append("%s does not exist" % required)
    if findings:
        # The shape every join below assumes is already gone; more findings
        # derived from it would describe the audit, not the script.
        return _unique(findings)

    assignments = as_list(tree["assignments"])
    foreaches = as_list(tree["foreaches"])
    invoke_calls = as_list(tree["invokeScCalls"])

    # 1. Each verb's argument list comes from Get-ScInvocations, for that verb.
    for verb in VERBS:
        calls = [row for row in invoke_calls if not row["function"] and row["clause"] == verb]
        if not calls:
            findings.append("the %s clause calls Invoke-Sc nowhere, so whatever it asks of the "
                            "Service Control Manager does not go through the one definition"
                            % verb)
            continue
        clause_assignments = [row for row in assignments
                              if not row["function"] and row["clause"] == verb]
        clause_foreaches = [row for row in foreaches
                            if not row["function"] and row["clause"] == verb]
        for call in calls:
            if not call["named"]:
                findings.append("the %s clause calls Invoke-Sc without a named -Arguments: %s"
                                % (verb, _clip(call["text"])))
                continue
            findings += _trace_arguments(verb, call, clause_assignments, clause_foreaches)

    # 2. Invoke-Sc passes that list to sc.exe unchanged, and nothing else runs sc.exe.
    sc_calls = as_list(tree["scCalls"])
    if not sc_calls:
        findings.append("nothing in the script reaches sc.exe, so the verbs perform no "
                        "registration at all")
    for call in sc_calls:
        elements = as_list(call["elements"])
        if call["function"] == "Invoke-Sc":
            if len(elements) != 2 or as_list(call["splatted"]) != ["Arguments"]:
                findings.append("Invoke-Sc hands sc.exe %d element(s) rather than its own "
                                "$Arguments splatted alone, so the Service Control Manager sees "
                                "tokens no plan printed: %s" % (len(elements), _clip(call["text"])))
        elif call["function"] == "Get-ServiceRecord":
            if elements != ["sc.exe", "query", SERVICE_NAME_VARIABLE]:
                findings.append("Get-ServiceRecord's sc.exe call is no longer the read-only "
                                "query: %s" % _clip(call["text"]))
        else:
            where = call["function"] or (("the %s clause" % call["clause"]) if call["clause"]
                                         else "the script body")
            findings.append("%s reaches sc.exe outside Invoke-Sc, where no plan describes it: %s"
                            % (where, _clip(call["text"])))
    for row in assignments:
        if row["function"] == "Invoke-Sc" and row["leftRoot"] == "Arguments":
            findings.append("Invoke-Sc rebinds its own $Arguments before sc.exe sees them: "
                            "%s %s %s" % (row["leftText"], row["operator"],
                                          _clip(row["rightText"])))
    # ...and nothing reaches that parameter before the splat by any other shape.
    findings += audit_invoke_sc_arguments(tree)

    # 2b. The one definition answers the action it was asked for and nothing else.
    findings += audit_invocations_scope(tree, assignments)

    # 3. The plan document describes that same function, for the action asked of it.
    fields = [row for row in as_list(tree["planFields"]) if row["key"] == "scInvocations"]
    if len(fields) != 1:
        findings.append("New-Plan states scInvocations %d time(s)" % len(fields))
    else:
        root = fields[0]["valueRoot"]
        bindings = [row for row in assignments
                    if row["function"] == "New-Plan" and row["leftRoot"] == root]
        if not root:
            findings.append("New-Plan's scInvocations reaches no variable: %s"
                            % _clip(fields[0]["valueText"]))
        elif len(bindings) != 1:
            findings.append("$%s has %d definitions in New-Plan, so the plan's scInvocations is "
                            "not one value" % (root, len(bindings)))
        else:
            commands = as_list(bindings[0]["rightCommands"])
            if not commands or commands[0] != "Get-ScInvocations":
                findings.append("New-Plan's scInvocations does not begin at Get-ScInvocations: "
                                "%s" % _clip(bindings[0]["rightText"]))
            elif bindings[0]["action"] != "$Action":
                findings.append("New-Plan reads Get-ScInvocations -Action %s rather than the "
                                "action it was asked to describe"
                                % (bindings[0]["action"] or "(nothing)"))
    return _unique(findings)


# ===========================================================================
# The controls
# ===========================================================================

def run_controls(work, report, only=None):
    def selected(name):
        return only is None or name in only

    code = executable_text(SCRIPT)
    assembler_code = executable_text(ASSEMBLER)
    tree = None
    if any(selected(name) for name in ("M12", "M13", "M14", "M17", "M18")):
        tree = ast_query(work, SCRIPT)

    # --- M01: the verb surface ------------------------------------------------
    if selected("M01"):
        def drive(package):
            # Every real verb must be accepted as a verb; only the fifth is not.
            for verb in VERBS:
                completed, _ = package.plan(verb=verb)
                if completed.returncode != 0 and refusal(completed, "verb"):
                    report.record("M01", "RED", "'%s' is one of the four verbs and was refused"
                                  % verb)
                    return False
            completed, _ = package.plan(verb="install")
            if not expect_refusal(report, "M01", completed, "verb", "the verb 'install'"):
                return False
            if "is not one of the four verbs" not in completed.stderr:
                report.record("M01", "RED", "the refusal does not name the four verbs")
                return False
            return True

        def mutant_ok(package):
            completed, _ = package.plan(verb="install")
            return "is not one of the four verbs" not in completed.stderr

        observed(report, "M01", work,
                 "register, start, stop and remove are accepted; 'install' is refused in the "
                 "script's own words, next to the list, rather than as a parameter-binding error",
                 drive, [("if ($VERBS -notcontains $action) {", "if ($false) {")], mutant_ok)

    # --- M02: the four state directories are required -------------------------
    if selected("M02"):
        def drive(package):
            completed, _ = package.plan(state={})
            if not expect_refusal(report, "M02", completed, "state-directories",
                                  "register with no state directory at all"):
                return False
            for missing in STATE_PARAMETERS:
                state = package.default_state()
                del state[missing]
                completed, _ = package.plan(state=state)
                if not refusal(completed, "state-directories"):
                    report.record("M02", "RED", "register was accepted without %s" % missing)
                    return False
                if missing not in completed.stderr:
                    report.record("M02", "RED", "the refusal does not name %s" % missing)
                    return False
            # And the complete set is accepted, so the rule is not "always no".
            completed, document = package.plan()
            if completed.returncode != 0 or document is None:
                report.record("M02", "RED", "the four directories together were not accepted: %s"
                              % completed.stderr.strip()[:200])
                return False
            return True

        def mutant_ok(package):
            # Message-specific, not exit-specific: with the required-directories
            # check defeated the four values are empty strings, and an empty
            # string is refused a line later as a relative path. That is a
            # DIFFERENT rule firing, and an inert-proof that accepted it would
            # be reporting the wrong check as load-bearing.
            completed, _ = package.plan(state={})
            return "required" not in completed.stderr

        observed(report, "M02", work,
                 "each of -DataDir, -ConfigDir, -CacheDir and -LogDir is required by name and "
                 "none is defaulted; the archive supplies no location for state",
                 drive, [("if ($missing.Count -gt 0) {", "if ($false) {")], mutant_ok)

    # --- M03: a relative state directory --------------------------------------
    if selected("M03"):
        # The script runs with the package as its working directory, so a plain
        # relative path would ALSO resolve inside the package and be refused by
        # the containment rule instead. `../../` resolves outside it, which
        # leaves relativity as the only thing wrong with the input.
        relative = os.path.join("..", "..", "m03-state", "data")

        def drive(package):
            state = package.default_state()
            state["-DataDir"] = relative
            completed, _ = package.plan(state=state)
            if not expect_refusal(report, "M03", completed, "state-directories",
                                  "a relative -DataDir"):
                return False
            if "is relative" not in completed.stderr:
                report.record("M03", "RED", "the refusal does not say the path is relative")
                return False
            return True

        def mutant_ok(package):
            state = package.default_state()
            state["-DataDir"] = relative
            completed, _ = package.plan(state=state)
            return "is relative" not in completed.stderr

        observed(report, "M03", work,
                 "a relative state directory is refused: the SCM starts a service with a working "
                 "directory this script does not choose",
                 drive, [("if (-not [System.IO.Path]::IsPathRooted($value)) {", "if ($false) {")],
                 mutant_ok)

    # --- M04: state inside the package ----------------------------------------
    if selected("M04"):
        def drive(package):
            for leaf in ("data", os.path.join("web", "state")):
                state = package.default_state()
                state["-DataDir"] = os.path.join(package.root, leaf)
                completed, _ = package.plan(state=state)
                if not refusal(completed, "state-directories"):
                    report.record("M04", "RED", "a -DataDir at %s inside the package was accepted"
                                  % leaf)
                    return False
            # A sibling directory whose name merely STARTS with the package path
            # is not inside it, and must still be accepted.
            state = package.default_state()
            state["-DataDir"] = package.root.rstrip(os.sep) + "-state"
            completed, _ = package.plan(state=state)
            if completed.returncode != 0:
                report.record("M04", "RED",
                              "a sibling directory sharing the package's name prefix was refused, "
                              "so the rule is a string prefix rather than containment")
                return False
            return True

        def mutant_ok(package):
            state = package.default_state()
            state["-DataDir"] = os.path.join(package.root, "data")
            completed, _ = package.plan(state=state)
            return completed.returncode == 0

        observed(report, "M04", work,
                 "state inside the package directory is refused, at the top level and nested, "
                 "because the package is replaced wholesale on upgrade; a sibling that only "
                 "shares the name prefix is still accepted",
                 drive, [("if ($inside) {", "if ($false) {")], mutant_ok)

    # --- M05: the package is $PSScriptRoot, and nothing is baked in -----------
    if selected("M05"):
        shallow = Package(os.path.join(work, "M05", "a", "tesserafin-server_1.0.0_win-x64"))
        deep = Package(os.path.join(work, "M05", "b", "one", "two", "three",
                                    "tesserafin-server_1.0.0_win-x64"))
        findings = []
        documents = []
        for package in (shallow, deep):
            completed, document = package.plan()
            if document is None:
                findings.append("no plan from %s: %s" % (package.root,
                                                         completed.stderr.strip()[:160]))
                continue
            documents.append(document)
            if os.path.normcase(document["packageRoot"]) != os.path.normcase(package.root):
                findings.append("packageRoot %s is not the script's own directory"
                                % document["packageRoot"])
            if package.root not in document["binaryPath"]:
                findings.append("the binary path does not name %s" % package.root)
        if len(documents) == 2 and documents[0]["binaryPath"] == documents[1]["binaryPath"]:
            findings.append("two packages at different depths planned the same binary path")
        # And no absolute machine location anywhere in the executable text.
        planted = "$INSTALL_ROOT = 'C:\\Program Files\\Tesserafin\\Server'\n"
        location = r"([A-Za-z]:\\|%ProgramFiles%|%ProgramData%|\$env:ProgramFiles|\$env:ProgramData)"
        if not scan_for(planted, location):
            report.record("M05", "INERT", "the baked-location scanner does not detect a planted one")
        elif findings:
            report.record("M05", "RED", "; ".join(findings[:3]))
        else:
            hits = scan_for(code, location)
            if hits:
                report.record("M05", "RED", "the script names a machine location: %s" % hits[:3])
            else:
                report.record("M05", "PASS",
                              "the same bytes at two depths plan two binary paths, each rooted at "
                              "the script's own directory, and the executable text names no "
                              "install location at all")

    # --- M06: the package must be a package -----------------------------------
    if selected("M06"):
        def drive(package):
            for kwargs, what in (({"exe": False}, "tesserafin.exe"),
                                 ({"web": False}, "web/"),
                                 ({"ffmpeg": False}, "ffmpeg/bin/ffmpeg.exe")):
                partial = Package(os.path.join(work, "M06", what.replace("/", "-"),
                                               "tesserafin-server_1.0.0_win-x64"), **kwargs)
                completed, _ = partial.plan()
                if not refusal(completed, "package"):
                    report.record("M06", "RED", "a package missing %s was accepted" % what)
                    return False
            return True

        def mutant_ok(package):
            partial = Package(os.path.join(work, "M06", "mutant-partial",
                                           "tesserafin-server_1.0.0_win-x64"), exe=False,
                              script_text=read_text(package.script))
            completed, _ = partial.plan()
            return completed.returncode == 0

        observed(report, "M06", work,
                 "a directory missing tesserafin.exe, web/ or ffmpeg/bin/ffmpeg.exe is refused: a "
                 "loose copy of the script is not a package and cannot say where the server is",
                 drive,
                 [("if (-not [System.IO.File]::Exists($paths.serverExe)) {", "if ($false) {"),
                  ("if (-not [System.IO.Directory]::Exists($paths.webDir)) {", "if ($false) {"),
                  ("if (-not [System.IO.File]::Exists($paths.ffmpegExe)) {", "if ($false) {")],
                 mutant_ok)

    # --- M07: the plan IS W0 §4's service contract ----------------------------
    if selected("M07"):
        w0 = read_text(W0_DOC) if os.path.isfile(W0_DOC) else ""
        section = ""
        start = w0.find("### The service contract")
        if start >= 0:
            end = w0.find("\n## ", start)
            section = w0[start:end if end > 0 else len(w0)]
        findings = []
        if not section:
            findings.append("W0 §4's service-contract table could not be read")
        else:
            for expected, what in ((SERVICE_NAME, "the service name"),
                                   (SERVICE_DISPLAY_NAME, "the display name"),
                                   (SERVICE_DESCRIPTION, "the description"),
                                   ("Automatic (Delayed Start)", "the startup mode"),
                                   ("restart after 60 s on first and second failure",
                                    "the recovery policy")):
                if expected not in section:
                    findings.append("W0 §4 no longer states %s (%r)" % (what, expected))
        package = Package(os.path.join(work, "M07", "tesserafin-server_1.0.0_win-x64"))
        completed, document = package.plan()
        if document is None:
            findings.append("no plan: %s" % completed.stderr.strip()[:160])
        else:
            for key, expected in (("serviceName", SERVICE_NAME),
                                  ("displayName", SERVICE_DISPLAY_NAME),
                                  ("description", SERVICE_DESCRIPTION),
                                  ("startType", SERVICE_START_TYPE),
                                  ("failureActions", FAILURE_ACTIONS)):
                if document.get(key) != expected:
                    findings.append("the plan's %s is %r, §4 requires %r"
                                    % (key, document.get(key), expected))
            create = [invocation for invocation in document["scInvocations"]
                      if invocation["arguments"][0] == "create"]
            if len(create) != 1:
                findings.append("register plans %d sc.exe create calls" % len(create))
            else:
                arguments = create[0]["arguments"]
                for token, value in (("start=", SERVICE_START_TYPE),
                                     ("DisplayName=", SERVICE_DISPLAY_NAME)):
                    if token not in arguments or arguments[arguments.index(token) + 1] != value:
                        findings.append("sc.exe create does not pass %s %r" % (token, value))
            if [invocation["arguments"][0] for invocation in document["scInvocations"]] != \
                    ["create", "description", "failure"]:
                findings.append("register does not plan create, description and failure in order")
        if findings:
            report.record("M07", "RED", "; ".join(findings[:3]))
        else:
            report.record("M07", "PASS",
                          "the planned registration is §4's table -- name, display name, "
                          "description, delayed-auto and the 60 s/60 s/none recovery policy -- "
                          "and §4 itself still states each of them")

    # --- M08: the argument list §4 requires -----------------------------------
    if selected("M08"):
        package = Package(os.path.join(work, "M08", "tesserafin-server_1.0.0_win-x64"))
        state = package.default_state()
        completed, document = package.plan(state=state)
        findings = []
        if document is None:
            findings.append("no plan: %s" % completed.stderr.strip()[:160])
        else:
            arguments = document["serviceArguments"]
            if "--service" not in arguments:
                findings.append("--service is not passed")
            if "--nowebclient" in arguments:
                findings.append("--nowebclient is passed, and §4 says it is never used")
            for flag, expected in (("--datadir", state["-DataDir"]),
                                   ("--configdir", state["-ConfigDir"]),
                                   ("--cachedir", state["-CacheDir"]),
                                   ("--logdir", state["-LogDir"]),
                                   ("--webdir", document["webDirectory"]),
                                   ("--ffmpeg", document["ffmpegExecutable"])):
                if flag not in arguments:
                    findings.append("%s is not passed" % flag)
                elif arguments[arguments.index(flag) + 1] != expected:
                    findings.append("%s is not %r" % (flag, expected))
            if arguments.count("--webdir") != 1 or arguments.count("--ffmpeg") != 1:
                findings.append("--webdir or --ffmpeg is passed more than once")
        if findings:
            report.record("M08", "RED", "; ".join(findings[:3]))
        else:
            report.record("M08", "PASS",
                          "--service, the four operator directories, and --webdir and --ffmpeg "
                          "explicitly, exactly as §4 requires so the service can never fall back "
                          "to a PATH encoder or a stale web directory; --nowebclient never")

    # --- M09: --webdir and --ffmpeg are inside the package --------------------
    if selected("M09"):
        package = Package(os.path.join(work, "M09", "tesserafin-server_1.0.0_win-x64"))
        completed, document = package.plan()
        findings = []
        if document is None:
            findings.append("no plan: %s" % completed.stderr.strip()[:160])
        else:
            root = os.path.normcase(os.path.abspath(document["packageRoot"]))
            for key in ("serverExecutable", "webDirectory", "ffmpegExecutable"):
                value = os.path.normcase(os.path.abspath(document[key]))
                if not value.startswith(root + os.sep):
                    findings.append("%s (%s) is outside the package" % (key, document[key]))
            for key, relative in (("webDirectory", "web"),
                                  ("ffmpegExecutable", os.path.join("ffmpeg", "bin", "ffmpeg.exe"))):
                if os.path.normcase(os.path.abspath(document[key])) != \
                        os.path.normcase(os.path.join(root, relative)):
                    findings.append("%s is not the frozen relative path %r" % (key, relative))
        if findings:
            report.record("M09", "RED", "; ".join(findings[:3]))
        else:
            report.record("M09", "PASS",
                          "the server, the Web payload and the encoder are all named inside the "
                          "package, at the relative layout the frozen assembler stages; nothing "
                          "is borrowed from the machine")

    # --- M10: quoting ---------------------------------------------------------
    if selected("M10"):
        def drive(package):
            state = package.default_state()
            spaced = os.path.join(os.path.dirname(package.root.rstrip(os.sep)),
                                  "état — 状態 dir", "data")
            state["-DataDir"] = spaced
            completed, document = package.plan(state=state)
            if document is None:
                report.record("M10", "RED", "a state directory with spaces was refused: %s"
                              % completed.stderr.strip()[:200])
                return False
            binary = document["binaryPath"]
            if ('"' + spaced + '"') not in binary:
                report.record("M10", "RED", "the spaced state directory is not quoted in binPath")
                return False
            if ('"' + document["serverExecutable"] + '"') not in binary:
                report.record("M10", "RED", "the executable is not quoted in binPath")
                return False
            if '"--service"' in binary:
                report.record("M10", "RED", "a switch is quoted as if it were a path")
                return False
            state["-DataDir"] = os.path.join(os.path.dirname(package.root.rstrip(os.sep)),
                                             'quo"te', "data")
            completed, _ = package.plan(state=state)
            return expect_refusal(report, "M10", completed, "service-arguments",
                                  "a state directory containing a double quote")

        def mutant_ok(package):
            state = package.default_state()
            state["-DataDir"] = os.path.join(os.path.dirname(package.root.rstrip(os.sep)),
                                             'quo"te', "data")
            completed, _ = package.plan(state=state)
            return completed.returncode == 0

        observed(report, "M10", work,
                 "spaces, accented Latin, an em dash and CJK survive into binPath quoted, "
                 "switches are not quoted as if they were paths, and a path carrying a double "
                 "quote is refused rather than passed to a command line that cannot express it",
                 drive, [("if ($piece.Contains('\"')) {", "if ($false) {")], mutant_ok)

    # --- M11: -Plan reaches no SCM --------------------------------------------
    if selected("M11"):
        package = Package(os.path.join(work, "M11", "tesserafin-server_1.0.0_win-x64"))
        before = sorted(os.walk(package.root).__next__()[2])
        completed, document = package.plan()
        after = sorted(os.walk(package.root).__next__()[2])
        state_root = os.path.dirname(package.default_state()["-DataDir"])
        findings = []
        if document is None:
            findings.append("no plan: %s" % completed.stderr.strip()[:160])
        if before != after:
            findings.append("the plan wrote into the package: %s"
                            % sorted(set(after) - set(before)))
        if os.path.exists(state_root):
            findings.append("the plan created the state directories at %s" % state_root)
        # The early exit must come BEFORE the first SCM query, or a host with an
        # SCM would be touched by a document that claims to touch nothing.
        plan_exit = code.find("if ($Plan) {")
        first_query = code.find("$existing = Get-ServiceRecord")
        if plan_exit < 0 or first_query < 0:
            findings.append("the plan exit or the first SCM query could not be located")
        elif plan_exit > first_query:
            findings.append("the plan exit comes after the first sc.exe query")
        # Only the MAIN BODY, not the function definitions above it: defining
        # Get-ServiceRecord is not reaching the Service Control Manager, and a
        # scan that could not tell the two apart would have to be loosened until
        # it missed a real call.
        body_start = code.find("$action = $Verb.Trim()")
        if body_start < 0:
            findings.append("the main body could not be located")
        elif plan_exit > body_start and scan_for(code[body_start:plan_exit], r"\bsc\.exe\b"):
            findings.append("sc.exe is reached before the plan exit")
        if findings:
            report.record("M11", "RED", "; ".join(findings[:3]))
        else:
            report.record("M11", "PASS",
                          "-Plan resolved the whole registration and exited before the first "
                          "sc.exe query; the package is byte-for-byte unchanged and no state "
                          "directory was created")

    # --- M12: one definition of the SCM calls ---------------------------------
    if selected("M12"):
        findings = audit_one_definition(tree)
        if findings:
            # Ordered before the INERT-proof deliberately. A planted script is
            # a script whose text has moved, so the proof's own anchors may no
            # longer apply to it; reporting INERT there would turn a detection
            # into a shrug.
            report.record("M12", "RED", "; ".join(findings[:3]))
        else:
            # The INERT-proof: the two rebindings W2-A5-R1 measured passing V1
            # and the two W2-A5-R2 measured passing V2, applied to the REAL
            # script's bytes and audited by the functions above. Each must
            # produce at least one finding on its own, or this rule has stopped
            # owning the property and says so. Deleting any one of the four new
            # or old checks therefore turns M12 INERT rather than leaving the
            # suite green.
            blind = []
            for label, mutation in M12_PLANTS:
                planted_text, applied = mutate(read_text(SCRIPT), mutation)
                if not all(applied):
                    blind.append("%s can no longer be planted (%s), so the rule is unmeasured "
                                 "rather than sound"
                                 % (label, ", ".join("%dx" % n for n in applied)))
                    continue
                directory = os.path.join(work, "M12")
                os.makedirs(directory, exist_ok=True)
                planted_path = os.path.join(directory, "%d.ps1" % (len(blind) + 1))
                with open(planted_path, "w", encoding="utf-8", newline="\n") as handle:
                    handle.write(planted_text)
                if not audit_one_definition(ast_query(work, planted_path)):
                    blind.append("%s is not detected" % label)
            if blind:
                report.record("M12", "INERT", "; ".join(blind))
            else:
                report.record("M12", "PASS",
                              "every -Arguments the four verbs hand Invoke-Sc traces back, "
                              "through the clause's own foreach, to one Get-ScInvocations call "
                              "for that same verb; Invoke-Sc hands sc.exe that parameter "
                              "splatted alone and rebinds it nowhere; nothing else in the "
                              "script reaches sc.exe but the read-only query; and New-Plan's "
                              "scInvocations begins at the same function for the action it was "
                              "asked to describe. Inside Invoke-Sc, $Arguments is splatted and "
                              "never otherwise reached, and no command runs beside sc.exe; "
                              "inside Get-ScInvocations, every variable is one of its own "
                              "parameters or a script constant, so it cannot see whether -Plan "
                              "or a verb is asking -- so the plan is the argv, and all four "
                              "measured plants are detected on the real bytes")

    # --- M13: no repair -------------------------------------------------------
    if selected("M13"):
        register = clause_code(tree["verbClauses"].get("register", ""))
        findings = []
        guard = re.search(r"if \(\$null -ne \$existing\) \{\s*\n\s*Deny 'register'", register)
        if not guard:
            findings.append("register does not refuse an already-registered service first")
        else:
            create = register.find("Invoke-Sc")
            if create >= 0 and guard.start() > create:
                findings.append("the already-registered refusal comes after the first sc.exe call")
        if not re.search(r"already registered", tree["verbClauses"].get("register", "")):
            findings.append("the refusal does not say the service is already registered")
        for pattern, what in ((r"sc\.exe\s+config", "sc.exe config"),
                              (r"'config'", "an sc.exe config argument"),
                              (r"Set-Service", "Set-Service")):
            if scan_for(register, pattern):
                findings.append("register reconfigures an existing service with %s" % what)
        planted = "if ($null -ne $existing) { $null = Invoke-Sc -What 'x' -Arguments @('config') }"
        if not scan_for(planted, r"'config'"):
            report.record("M13", "INERT", "the reconfigure scanner does not detect a planted one")
        elif findings:
            report.record("M13", "RED", "; ".join(findings[:3]))
        else:
            report.record("M13", "PASS",
                          "register refuses an existing registration before it calls sc.exe at "
                          "all, and reconfigures nothing: rewriting a registration in place is "
                          "repair, and §6 gives this script none")

    # --- M14: no rollback -----------------------------------------------------
    if selected("M14"):
        register = clause_code(tree["verbClauses"].get("register", ""))
        findings = []
        for pattern, what in ((r"'delete'", "an sc.exe delete argument"),
                              (r"Remove-Service", "Remove-Service"),
                              (r"Remove-Item", "Remove-Item"),
                              (r"Directory\]::Delete", "a directory delete"),
                              (r"File\]::Delete", "a file delete")):
            if scan_for(register, pattern):
                findings.append("register undoes its own work with %s" % what)
        if "has no rollback" not in tree["verbClauses"].get("register", ""):
            findings.append("the partial-registration refusal does not say there is no rollback")
        if not re.search(r"WAS created", tree["verbClauses"].get("register", "")):
            findings.append("the partial-registration refusal does not say what exists")
        planted = "} catch { $null = Invoke-Sc -What 'x' -Arguments @('delete', $SERVICE_NAME) }"
        if not scan_for(planted, r"'delete'"):
            report.record("M14", "INERT", "the rollback scanner does not detect a planted one")
        elif findings:
            report.record("M14", "RED", "; ".join(findings[:3]))
        else:
            report.record("M14", "PASS",
                          "a register that fails after sc.exe create deletes nothing and names "
                          "what exists; §6 calls rollback a property of the format that no "
                          "script can add, and a half-performed one is worse than none")

    # --- M15: no ARP entry, no installer engine, no machine-wide write --------
    if selected("M15"):
        patterns = {
            "an Add/Remove Programs entry":
                r"(CurrentVersion\\Uninstall|Uninstall\\\{|DisplayVersion|UninstallString)",
            "an installer engine": r"(msiexec|Win32_Product|Start-Msi|\.msi\b)",
            # A machine-wide WRITE, not a mention of one: the plan document
            # names ServicesPipeTimeout in order to record that it is NOT
            # written, and a rule that could not tell the two apart would have
            # to be loosened until it missed the write.
            "a registry write":
                r"(New-ItemProperty|Set-ItemProperty|New-Item\s+-Path\s+'?HKLM|reg(\.exe)?\s+add|"
                r"HKLM:|Registry::)",
        }
        planted = ("New-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion"
                   "\\Uninstall\\Tesserafin' -Name UninstallString -Value 'msiexec /x'\n"
                   "Set-ItemProperty -Path 'Registry::HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet"
                   "\\Control' -Name ServicesPipeTimeout -Value 120000\n")
        blind = [what for what, pattern in patterns.items() if not scan_for(planted, pattern)]
        findings = []
        for what, pattern in patterns.items():
            hits = scan_for(code, pattern)
            if hits:
                findings.append("%s: %s" % (what, hits[:2]))
        if blind:
            report.record("M15", "INERT", "these scanners detect no planted violation: %s"
                          % ", ".join(sorted(blind)))
        elif findings:
            report.record("M15", "RED", "; ".join(findings[:3]))
        else:
            report.record("M15", "PASS",
                          "no Uninstall key, no installer engine and no machine-wide registry "
                          "write anywhere in the executable text; §4's 120 s ServicesPipeTimeout "
                          "is machine-wide and appears only as a recorded deferral to W3's MSI")

    # --- M16: no second copy of the server, and nothing fetched ---------------
    if selected("M16"):
        patterns = {
            "a copy of the tree":
                r"(Copy-Item|robocopy|xcopy|File\]::Copy|Directory\]::Move|Move-Item|"
                r"Expand-Archive|Compress-Archive)",
            "a network fetch":
                r"(Invoke-WebRequest|Invoke-RestMethod|Start-BitsTransfer|curl\b|wget\b|"
                r"WebClient|HttpClient)",
            "arbitrary execution": r"(Invoke-Expression|iex\b|\[scriptblock\]::Create)",
        }
        planted = ("Copy-Item -Path $exe -Destination $env:ProgramFiles\n"
                   "Invoke-WebRequest -Uri https://example.invalid -OutFile x\n"
                   "Invoke-Expression $payload\n")
        blind = [what for what, pattern in patterns.items() if not scan_for(planted, pattern)]
        findings = []
        for what, pattern in patterns.items():
            hits = scan_for(code, pattern)
            if hits:
                findings.append("%s: %s" % (what, hits[:2]))
        if blind:
            report.record("M16", "INERT", "these scanners detect no planted violation: %s"
                          % ", ".join(sorted(blind)))
        elif findings:
            report.record("M16", "RED", "; ".join(findings[:3]))
        else:
            report.record("M16", "PASS",
                          "the service runs the tesserafin.exe already in the directory; nothing "
                          "is copied to a second location, nothing is downloaded and nothing is "
                          "evaluated, so there is no second tree that can drift from this one")

    # --- M17: remove refuses a running service and deletes no file ------------
    if selected("M17"):
        remove = clause_code(tree["verbClauses"].get("remove", ""))
        raw = tree["verbClauses"].get("remove", "")
        findings = []
        if not re.search(r"if \(\$existing\.state -ne 'STOPPED'\) \{\s*\n(\s*#[^\n]*\n)*\s*Deny 'remove'",
                         raw):
            findings.append("remove does not refuse a service that is not STOPPED")
        else:
            guard = raw.find("-ne 'STOPPED'")
            delete = remove.find("Invoke-Sc")
            if delete >= 0 and guard > raw.find("Invoke-Sc"):
                findings.append("the running-service refusal comes after sc.exe delete")
        for pattern, what in ((r"Remove-Item", "Remove-Item"),
                              (r"Directory\]::Delete", "a directory delete"),
                              (r"File\]::Delete", "a file delete"),
                              (r"Directory\]::CreateDirectory", "a directory create")):
            if scan_for(remove, pattern):
                findings.append("remove touches the filesystem with %s" % what)
        planted = "if ($existing.state -ne 'STOPPED') { Remove-Item -Recurse $state.DataDir }"
        if not scan_for(planted, r"Remove-Item"):
            report.record("M17", "INERT", "the deletion scanner does not detect a planted one")
        elif findings:
            report.record("M17", "RED", "; ".join(findings[:3]))
        else:
            report.record("M17", "PASS",
                          "remove refuses a service that is not STOPPED -- §4's point that an "
                          "orphan holding the database is worse than a clean failure -- and "
                          "deletes the registration only: no file, and none of the operator's "
                          "state directories")

    # --- M18: fail closed without Windows and without administrator -----------
    if selected("M18"):
        def drive(package):
            for verb in VERBS:
                completed = package.run(verb, "-DataDir", "/w2a5/data", "-ConfigDir",
                                        "/w2a5/config", "-CacheDir", "/w2a5/cache",
                                        "-LogDir", "/w2a5/log")
                if not refusal(completed, "platform"):
                    report.record("M18", "RED",
                                  "'%s' was not refused on a host with no Service Control "
                                  "Manager: exit %d, %s"
                                  % (verb, completed.returncode,
                                     (completed.stderr or completed.stdout).strip()[:160]))
                    return False
            return True

        def mutant_ok(package):
            completed = package.run("register", "-DataDir", "/w2a5/data", "-ConfigDir",
                                    "/w2a5/config", "-CacheDir", "/w2a5/cache",
                                    "-LogDir", "/w2a5/log")
            return not refusal(completed, "platform")

        # The elevation gate cannot be reached on a host with no SCM, so it is
        # asserted structurally: it must be called for every verb, on the same
        # branch as the platform gate, and never on the -Plan branch.
        gate = re.search(r"if \(-not \$Plan\) \{\s*\n\s*Assert-Windows\s*\n\s*"
                         r"Assert-Administrator\s*\n\s*\}", code)
        if not gate:
            report.record("M18", "RED",
                          "the platform and privilege gates are not both run, in that order, on "
                          "the non-plan branch")
        elif len(re.findall(r"Assert-Administrator", code)) != 2:
            report.record("M18", "RED",
                          "Assert-Administrator is defined or called more than once, so which "
                          "paths are gated is not decidable here")
        elif "IsInRole" not in code:
            report.record("M18", "RED", "the privilege gate does not test an administrator role")
        else:
            observed(report, "M18", work,
                     "every verb refuses on a host with no Service Control Manager, naming the "
                     "platform, and the elevation check runs for every verb on that same branch "
                     "and never for -Plan; nothing is attempted before either",
                     drive, [("if (-not $Plan) {", "if ($false) {")], mutant_ok)

    # --- M19: the assembler stages it and pins its digest ---------------------
    if selected("M19"):
        findings = []
        requirements = (
            (r"\$SERVICE_SCRIPT_NAME = 'tesserafin-server-service\.ps1'",
             "the frozen relative path as a constant"),
            (r"\$serviceScript = \[System\.IO\.Path\]::Combine\(\$repo, 'ci', 'windows', 'w2', "
             r"\$SERVICE_SCRIPT_NAME\)",
             "the script read from the checkout"),
            (r"\[System\.IO\.File\]::Copy\(\$serviceScript, \$stagedServiceScript, \$false\)",
             "the script staged into the package root"),
            (r"\$serviceScriptDigest = Get-Sha256 \$stagedServiceScript",
             "the digest taken from the STAGED copy"),
            (r"serviceScript = \[ordered\]@\{", "a provenance record for it"),
            (r"sha256 = \$serviceScriptDigest", "its digest in the provenance manifest"),
            (r"relativePath = \$SERVICE_SCRIPT_NAME", "its relative path in the manifest"),
        )
        for pattern, what in requirements:
            if not re.search(pattern, assembler_code):
                findings.append("the assembler does not carry %s" % what)
        # It is STAGED, never run: §6 makes it a convenience, not a step.
        for pattern, what in ((r"&\s*\$serviceScript", "runs the service script"),
                              (r"&\s*\$stagedServiceScript", "runs the staged service script"),
                              (r"\bsc\.exe\b", "reaches the Service Control Manager")):
            if scan_for(assembler_code, pattern):
                findings.append("the assembler %s" % what)
        planted = "        serviceScript = [ordered]@{\n            sha256 = $serviceScriptDigest\n"
        if not re.search(r"sha256 = \$serviceScriptDigest", planted):
            report.record("M19", "INERT", "the provenance scanner does not detect a planted row")
        elif findings:
            report.record("M19", "RED", "; ".join(findings[:3]))
        else:
            report.record("M19", "PASS",
                          "the assembler stages ci/windows/w2/%s at the top level of the package "
                          "directory, hashes the STAGED copy, and records that digest and its "
                          "relative path in the provenance manifest; it never runs it"
                          % FROZEN_RELATIVE_PATH)

    # --- M20: the packed archive carries it, at that path ---------------------
    if selected("M20"):
        report_m20 = _pack_control(work, report)
        del report_m20

    # --- M21: F18 still REDs any other new .ps1 -------------------------------
    if selected("M21"):
        findings = []
        module = _load_a1_controls()
        if module is None:
            findings.append("ci/windows/w2/ffmpeg-consume-controls.py could not be read")
        else:
            entries = sorted(os.listdir(HERE))
            if module.w2_directory_findings(entries):
                findings.append("F18 already REDs the accepted directory: %s"
                                % module.w2_directory_findings(entries))
            for planted in ("ffmpeg-consume.ps1", "tesserafin-server-service2.ps1",
                            "consume-web-payload-copy.ps1"):
                if not module.w2_directory_findings(entries + [planted]):
                    findings.append("F18 does not RED a planted %s" % planted)
            if FROZEN_RELATIVE_PATH not in entries:
                findings.append("the service script is not in ci/windows/w2 at all")
        if findings:
            report.record("M21", "RED", "; ".join(findings[:3]))
        else:
            report.record("M21", "PASS",
                          "F18's allowlist now continues on %s by exact name and on nothing "
                          "else; a planted ffmpeg-consume.ps1, a near-miss of this slice's own "
                          "name and a near-miss of the Web consumer's are each still REDed"
                          % FROZEN_RELATIVE_PATH)

    # --- M22: the doc ---------------------------------------------------------
    if selected("M22"):
        findings = []
        if not os.path.isfile(DOC):
            findings.append("docs/distribution/W2-A5-service-script.md does not exist")
        else:
            doc = read_text(DOC)
            for needle, what in (
                    (FROZEN_RELATIVE_PATH, "the frozen relative path"),
                    ("register", "the register verb"), ("start", "the start verb"),
                    ("stop", "the stop verb"), ("remove", "the remove verb"),
                    ("not a second installer", "that it is not a second installer"),
                    ("no repair", "that there is no repair"),
                    ("no rollback", "that there is no rollback"),
                    ("Add/Remove Programs", "that there is no ARP entry"),
                    ("is not hosted evidence", "that an SCM start is not hosted evidence"),
                    (".reefin", "that the .reefin rename is not done"),
                    ("W2 is not accepted", "that W2 is not accepted"),
                    ("W2-A5", "the slice it belongs to"),
                    ("#256", "the tracker")):
                if needle not in doc:
                    findings.append("the doc does not state %s" % what)
            for forbidden in ("W2 is accepted", "W2 accepted", "the .reefin rename is done"):
                if forbidden in doc:
                    findings.append("the doc claims %r" % forbidden)
        if findings:
            report.record("M22", "RED", "; ".join(findings[:3]))
        else:
            report.record("M22", "PASS",
                          "the doc names the frozen path and the four verbs, states each "
                          "non-goal §6 requires, says an SCM start is not hosted evidence, and "
                          "claims neither the .reefin rename nor W2 acceptance")

    # --- M23: the frozen files ------------------------------------------------
    if selected("M23"):
        findings = []
        for relative, pinned in sorted(FROZEN_PINS.items()):
            path = os.path.join(REPO_ROOT, relative)
            if not os.path.isfile(path):
                findings.append("%s is missing" % relative)
                continue
            actual = sha256_file(path)
            if actual != pinned:
                findings.append("%s is %s, the pin is %s" % (relative, actual[:16], pinned[:16]))
        probe = os.path.join(work, "M23-probe")
        shutil.copyfile(os.path.join(REPO_ROOT, "ci/windows/w2/pkg-tree-digest.py"), probe)
        with open(probe, "a", encoding="utf-8") as handle:
            handle.write("\n# one added byte\n")
        if sha256_file(probe) == FROZEN_PINS["ci/windows/w2/pkg-tree-digest.py"]:
            report.record("M23", "INERT", "the content pin does not distinguish a change")
        elif findings:
            report.record("M23", "RED", "; ".join(findings[:3]))
        else:
            report.record("M23", "PASS",
                          "all %d accepted W2 files this slice may not touch are byte-identical "
                          "to their pins" % len(FROZEN_PINS))


def _load_a1_controls():
    """W2-A1's controls, imported so F18's OWN allowlist function is measured.

    A copy of the rule written here would prove that this file agrees with
    itself. Importing is what makes M21 a statement about F18.
    """
    try:
        spec = importlib.util.spec_from_file_location("w2a1_controls", A1_CONTROLS)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module
    except Exception:  # pragma: no cover - reported as a RED by the caller
        return None


def _pack_control(work, report):
    """M20: the REAL packer, over a synthetic stage, and the ZIP is opened."""
    root = os.path.join(work, "M20")
    findings = []
    listings = {}
    for label, with_script in (("with", True), ("without", False)):
        stage = os.path.join(root, label, "stage")
        package_name = "tesserafin-server_1.0.0_win-x64"
        package_root = os.path.join(stage, package_name)
        Package(package_root, script_text=None if with_script else "")
        if not with_script:
            os.remove(os.path.join(package_root, FROZEN_RELATIVE_PATH))
        os.makedirs(os.path.join(package_root, "licenses"), exist_ok=True)
        with open(os.path.join(package_root, "licenses", "provenance.json"), "w",
                  encoding="utf-8") as handle:
            handle.write('{"schemaVersion": 1}\n')
        out = os.path.join(root, label, "out")
        os.makedirs(out, exist_ok=True)
        completed = subprocess.run(
            [POWERSHELL, "-NoProfile", "-NonInteractive", "-File", ASSEMBLER,
             "-StageRoot", stage, "-OutDir", out, "-SourceDateEpoch", "1756900000"],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=600)
        if completed.returncode != 0:
            findings.append("the packer refused the %s-script stage: %s"
                            % (label, (completed.stderr or completed.stdout).strip()[:200]))
            continue
        archives = [name for name in sorted(os.listdir(out)) if name.endswith(".zip")]
        if len(archives) != 1:
            findings.append("the %s-script stage produced %d archives" % (label, len(archives)))
            continue
        with zipfile.ZipFile(os.path.join(out, archives[0])) as archive:
            listings[label] = archive.namelist()
            if with_script:
                member = "%s/%s" % (package_name, FROZEN_RELATIVE_PATH)
                if member not in listings[label]:
                    findings.append("the archive does not carry %s" % member)
                else:
                    if archive.read(member) != open(SCRIPT, "rb").read():
                        findings.append("the archived script is not the checkout's bytes")
                    tops = {name.split("/")[0] for name in listings[label]}
                    if tops != {package_name}:
                        findings.append("the archive has top-level entries %s" % sorted(tops))

    member = "%s/%s" % ("tesserafin-server_1.0.0_win-x64", FROZEN_RELATIVE_PATH)
    if "without" in listings and member in listings["without"]:
        report.record("M20", "INERT",
                      "the listing check reports the script even when it was never staged")
    elif findings:
        report.record("M20", "RED", "; ".join(findings[:3]))
    else:
        report.record("M20", "PASS",
                      "the frozen packer, driven over a synthetic stage, produced one top-level "
                      "directory carrying %s with the checkout's exact bytes; a stage without it "
                      "produced a listing without it" % member)


def _repository_fingerprint():
    fingerprint = {}
    paths = [SCRIPT, ASSEMBLER, A1_CONTROLS, DOC, W0_DOC, os.path.abspath(__file__)]
    paths += [os.path.join(REPO_ROOT, relative) for relative in FROZEN_PINS]
    for path in paths:
        if os.path.isfile(path):
            fingerprint[os.path.relpath(path, REPO_ROOT)] = sha256_file(path)
    return fingerprint


def main(argv):
    global POWERSHELL
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--only", action="append", help="run only the named control(s)")
    args = parser.parse_args(argv)

    POWERSHELL = find_powershell()
    if not POWERSHELL:
        # Not a skip. Every observed refusal in this suite is driven through the
        # real script, so without an interpreter the suite proves nothing and
        # must say so rather than reporting a smaller green run.
        print("W2-A5 hostile controls")
        print("  SETUP RED   no PowerShell on PATH, so the real decision functions cannot be "
              "driven; install pwsh 7 or newer")
        return 1

    work = tempfile.mkdtemp(prefix="w2a5-controls-")
    try:
        before = _repository_fingerprint()
        print("W2-A5 hostile controls")
        report = Report()
        started = time.time()
        run_controls(work, report, set(args.only) if args.only else None)
        after = _repository_fingerprint()

        # A control that is deleted or renamed simply stops running, which would
        # make the summary line SHORTER and still green. The roster is what
        # turns that into a failure.
        if args.only is None:
            missing = sorted(set(ROSTER) - report.names())
            if missing:
                report.record("ROSTER", "RED", "these controls did not report: %s"
                              % ", ".join("%s (%s)" % (name, ROSTER[name]) for name in missing))
            else:
                report.record("ROSTER", "PASS",
                              "all %d rostered controls reported" % len(ROSTER))

        if before != after:
            report.record("RESTORE", "RED", "the controls modified the audited files")
        else:
            report.record("RESTORE", "PASS",
                          "every audited file is byte-identical to before the run")

        totals = report.counts()
        print("")
        print("W2-A5 controls: %d PASS, %d RED, %d INERT in %.1fs"
              % (totals["PASS"], totals["RED"], totals["INERT"], time.time() - started))
        return 0 if (totals["RED"] == 0 and totals["INERT"] == 0) else 1
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
