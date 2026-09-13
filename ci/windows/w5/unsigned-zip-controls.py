#!/usr/bin/env python3
"""Hostile controls for W5-A1 (#276), the unsigned win-x64 ZIP acceptance.

Two shapes, each observed RED rather than asserted:

  * VERIFIER CONTROLS. `verify-unsigned-zip.py` is loaded from its own source
    and driven against synthetic archives built here: two that differ, a lying
    hash sidecar, a changed Web tree, a wrong FFmpeg runtime archive, a changed
    ffmpeg/ tree, a provenance naming another commit, a non-self-contained or
    non-x64 server. The real verifier must name each fault. Each check is then
    MUTATED out of a copy of the source (for the hash: replaced by trusting the
    sidecar an assemble job could have written) and the same control must go
    RED against the mutant -- a control that cannot tell the two apart is
    reported INERT.

  * WORKFLOW CONTROLS. `.github/workflows/w5-unsigned-zip.yml` is audited as
    text, and every rule is proved load-bearing by mutating a copy of the text:
    write-all / write permissions, pull_request_target and push triggers, a
    verify job that publishes, assembles or reads an assemble job's output, an
    assemble job that needs the other or uploads a hash, and a path filter
    without Tesserafin.Server.Core -- for which a one-line C# edit is planted
    in a real Core file and the filter must, and then must not, queue.

Plus byte pins over the frozen W2 inputs and W0-W4 workflows, and the verifier's
pins against `ci/package/pins.env` and `accepted-runtime.json`.

Nothing here reaches a registry, builds, assembles or writes to the repository.

    python3 ci/windows/w5/unsigned-zip-controls.py
"""

import contextlib
import glob
import hashlib
import io
import json
import os
import re
import struct
import subprocess
import sys
import tempfile
import types
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
VERIFIER = os.path.join(HERE, "verify-unsigned-zip.py")
WORKFLOW = os.path.join(REPO_ROOT, ".github", "workflows", "w5-unsigned-zip.yml")
TREE_DIGEST = os.path.join(REPO_ROOT, "ci", "windows", "w2", "pkg-tree-digest.py")

# Bytes at W5-A0's master f90fd18862a79d4f08cb4a52223bc852a3ffd541. W5-A1 edits
# none of these; a pin is the only thing that can say so about a file it never
# touches.
FROZEN_PINS = {
    "ci/windows/w2/assemble-server-zip.ps1": "e26bf3b826303b04cb18bcfe77e29379cce114cab4b8da25a4eefc961b42f9b5",
    "ci/windows/w2/consume-web-payload.ps1": "db49f21001067a8f55ae71432ff9d47830daa454704a09800bb0e1eadf3b117c",
    "ci/windows/w2/ffmpeg-consume-controls.py": "fca41685c584d52b792fe190579b5bcd79d38b9f44529b33fc30ede63e9120ec",
    "ci/windows/w2/pkg-tree-digest.py": "0c70114c69e85d06bc3d95249cc1a86f917eb2b8deb44718cc05ad6f3afa70b4",
    "ci/windows/w2/relocate-and-start.ps1": "637095a09ae2e845f5359bbe57e960727cb30bf7d198efc731ba07463cae6b94",
    "ci/windows/w2/service-script-controls.py": "d2dc30e83a31bf0a78dda7ebd48c64f93f88701eb98a8bf447a0ddd9f4bd4eba",
    "ci/windows/w2/start-controls.py": "c7e826ffa5ffa7860cda5de3cf360fc2afebb115c04861014e742d37e03c11d3",
    "ci/windows/w2/tesserafin-server-service.ps1": "00bc9bb488907d8ef7a52fd4baa2dc707aa28a24ddb1fbaa8a232a8dc1266d98",
    "ci/windows/w2/two-runner-controls.py": "6668381e736a4c30945950fa9f7f6b26b7947868a464db3921de6be2cfc01f8f",
    "ci/windows/w2/web-payload-controls.py": "60466ae4da90d9ed876e709c29c90fef025dc287ad8ffbaf5d64d1f053b6e9ea",
    "ci/windows/w2/zip-controls.py": "1cdd22612db0ae34b2234c73e57aa6b345fec931266fd94868a0bb37a94353c2",
    ".github/workflows/w0-windows-probe.yml": "e092e3c3a6af555e2b3c2f0675c19adb81b563cf4d763d1d2d441041768136e3",
    ".github/workflows/w1-windows-build-inputs-consume.yml": "301db6478768e1d874e936b2df6d0cbd4234e7f71a99317652e2772d3a6554cd",
    ".github/workflows/w1-windows-build-inputs.yml": "29d6468e3d7033b62ccbfa8b0401132a23ebf54e1f4f7fdcc2e83c37b803c479",
    ".github/workflows/w1-windows-ffmpeg-runtime.yml": "803cc465bb7d38c33a86cec28cc043d2199fbb2851279c01a2f804e820c12b0d",
    ".github/workflows/w1-windows-runtime-publish.yml": "892fdcc7badb429adcd821294421faee9995c4ac8161d2972e9062de7de7b526",
    ".github/workflows/w1-windows-runtime-retention.yml": "4a48aa663c49bb0c978ec85f29ed377f189af09b49a9aefadd8a0e170a6b5dc1",
    ".github/workflows/w2-windows-ffmpeg-consume.yml": "82ce2aa5c2a4b2b832a49cfe2ca8390be4feb663b12bcaf0c7aaea43040df4e6",
    ".github/workflows/w2-windows-relocate-start.yml": "d493f1f25cdc94fecf33649fdbfe6bd401795b6db14c410fe9f1ac123ba07ce9",
    ".github/workflows/w2-windows-server-zip.yml": "337921cc0e473701a29d0ea193e70884ec93c68de1ba1d57bf1d84ec63dc4ac1",
    ".github/workflows/w2-windows-web-payload.yml": "c27fb4f9b768be1401fb143990402b7efd214ca9838e7784236231e13cd878ca",
    ".github/workflows/w2-windows-zip-two-runner.yml": "fbfcbf19931cb5396e391443714b62da77bff43b7998b06c6999cb7c726c368b",
    ".github/workflows/w3-windows-service-host.yml": "60678f742861628d92680cf846452675bd3f3d1b8403f1e6d732c85b7cf716e2",
    ".github/workflows/w4-windows-msi.yml": "deffcba556b413f5823ec8605f3ad6560a703f0b95660763968c124fd7b001c0",
    "ci/windows/runtime-retention/consume.ps1": "f19fefcc48de9ae2175aa49ecff6e732762219a3d76c38067ba4114a1924646d",
    "ci/windows/runtime-retention/accepted-runtime.json": "593c21f59c67dd564fa488f660efc14b74b5c5bcd775bbc3ef0bdf9e94dd9ece",
    "ci/package/pins.env": "d9a47fc89741f7674cfff9258043d419775dd1849eb118906c762d63f4cfd0ba",
    "SharedVersion.cs": "beeb301584718715830f5302bb483df461238388060fd821d7711c785df03fd4",
    "packaging/windows/msi/Tesserafin.wxs": "097bb7c68f62a3b15d64af0c58d947ce28377810752681407afd99f0d4b983dc",
}

# The ruling's minimum path filter, verbatim.
RULING_PATHS = [
    "ci/windows/w5/**", "ci/windows/w2/**",
    "ci/windows/runtime-retention/consume.ps1",
    "ci/windows/runtime-retention/accepted-runtime.json",
    "ci/package/pins.env", ".github/workflows/w5-unsigned-zip.yml",
    "docs/distribution/W5-A1-unsigned-acceptance.md", "SharedVersion.cs",
    "Tesserafin.Server/**", "Tesserafin.Server.Core/**",
    "Tesserafin.Server.Implementations/**", "Tesserafin.Common/**",
]

RESULTS = []


def record(ok, cid, text):
    RESULTS.append(ok)
    print("%s %s %s" % ("PASS" if ok else "FAIL", cid, text))


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


# ===========================================================================
# Verifier: load from source, optionally mutated
# ===========================================================================

def load_verifier(mutation=None):
    source = open(VERIFIER, encoding="utf-8").read()
    if mutation is not None:
        old, new = mutation
        if source.count(old) != 1:
            return None
        source = source.replace(old, new)
    module = types.ModuleType("w5a1_verifier")
    module.__file__ = VERIFIER
    exec(compile(source, VERIFIER, "exec"), module.__dict__)
    return module


def run_verify(module, fx, **overrides):
    args = dict(zip_a=fx["zip_a"], zip_b=fx["zip_b"], runtime_archive=fx["runtime"],
                head=fx["head"], epoch=fx["epoch"], pins=fx["pins"])
    args.update(overrides)
    with contextlib.redirect_stdout(io.StringIO()):
        return module.verify(**args)


# ===========================================================================
# Fixtures
# ===========================================================================

PACKAGE = "tesserafin-server_1.0.0_win-x64"
HEAD = "0123456789abcdef0123456789abcdef01234567"
EPOCH = 1789000000
WEB_EPOCH = 1785852822
FIXED_TIME = (1980, 1, 1, 0, 0, 0)


def pe_image(machine):
    data = bytearray(0x200)
    data[0:2] = b"MZ"
    struct.pack_into("<i", data, 0x3C, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<H", data, 0x84, machine)
    return bytes(data)


def write_zip(path, members):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in sorted(members):
            archive.writestr(zipfile.ZipInfo(name, FIXED_TIME), members[name])


def tree_digest(files, epoch):
    with tempfile.TemporaryDirectory() as root:
        for name, data in files.items():
            target = os.path.join(root, *name.split("/"))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "wb") as handle:
                handle.write(data)
        run = subprocess.run([sys.executable, TREE_DIGEST, root, str(epoch)],
                             stdout=subprocess.PIPE, check=True)
        return run.stdout.decode().strip()


WEB = {"index.html": b"<html>tesserafin</html>\n", "assets/app.js": b"console.log(1);\n"}
RUNTIME = {"ffmpeg.exe": pe_image(0x8664), "LICENSES/LGPL.txt": b"licence\n", "capability.json": b"{}\n"}


def package_members(pins, head=HEAD, web=None, ffmpeg=None, config=None, exe_machine=0x8664,
                    drop=(), extra=None):
    members = {
        "tesserafin.exe": pe_image(exe_machine),
        "tesserafin.runtimeconfig.json": json.dumps(
            config or {"runtimeOptions": {"tfm": "net10.0", "includedFrameworks": [
                {"name": "Microsoft.NETCore.App", "version": "10.0.0"}]}}).encode(),
        "licenses/LICENSE": b"GPL-2.0-or-later\n",
        "licenses/provenance.json": json.dumps({
            "serverCommit": head, "sourceDateEpoch": EPOCH,
            "web": {"payloadSha256": pins["web"], "sourceDateEpoch": WEB_EPOCH},
            "ffmpegRuntime": {"archiveSha256": pins["ffmpeg"]},
        }).encode(),
    }
    for component in ("hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "System.Private.CoreLib.dll"):
        members[component] = b"dll " + component.encode()
    for name, data in (web or WEB).items():
        members["web/" + name] = data
    for name, data in (ffmpeg or RUNTIME).items():
        members["ffmpeg/" + name] = data
    members.update(extra or {})
    return {PACKAGE + "/" + n: d for n, d in members.items() if n not in drop}


def fixture(work, name, a=None, b=None, runtime=None):
    """A pair of archives plus a runtime archive, pinned as a valid fixture's."""
    root = os.path.join(work, name)
    runtime_path = os.path.join(root, "runtime", "tesserafin-ffmpeg-win-x64.zip")
    write_zip(runtime_path, RUNTIME)
    pins = {"web": tree_digest(WEB, WEB_EPOCH),
            "ffmpeg": hashlib.sha256(open(runtime_path, "rb").read()).hexdigest()}
    if runtime is not None:
        write_zip(runtime_path, runtime)
    zip_a = os.path.join(root, "a", PACKAGE + ".zip")
    zip_b = os.path.join(root, "b", PACKAGE + ".zip")
    write_zip(zip_a, (a or package_members)(pins))
    write_zip(zip_b, (b or a or package_members)(pins))
    return {"zip_a": zip_a, "zip_b": zip_b, "runtime": runtime_path, "head": HEAD,
            "epoch": EPOCH, "pins": pins}


def verifier_controls(work):
    real = load_verifier()

    good = fixture(work, "good")
    same = open(good["zip_a"], "rb").read() == open(good["zip_b"], "rb").read()
    findings = run_verify(real, good)
    record(same and findings == [], "V00",
           "a valid synthetic pair PASSes the real verifier (findings: %s)" % findings)

    # (id, description, fixture, expected code, mutation that removes the check)
    def lying_pair():
        fx = fixture(work, "lie", b=lambda p: package_members(p, extra={"licenses/NOTE": b"B\n"}))
        claimed = hashlib.sha256(open(fx["zip_a"], "rb").read()).hexdigest()
        for path in (fx["zip_a"], fx["zip_b"]):
            with open(path + ".sha256", "w") as handle:
                handle.write(claimed + "\n")
        return fx

    cases = [
        ("V01", "assemble A and assemble B differ",
         fixture(work, "differ", b=lambda p: package_members(p, extra={"licenses/NOTE": b"B\n"})),
         "HASH-MISMATCH",
         ("    if digest_a != digest_b:\n        fail(\"HASH-MISMATCH\"",
          "    if False:\n        fail(\"HASH-MISMATCH\"")),
        ("V02", "verify trusts the assemble-job hash instead of recomputing from bytes",
         lying_pair(), "HASH-MISMATCH",
         ("    digest_b = sha256_file(zip_b)\n",
          "    digest_b = open(zip_b + \".sha256\").read().strip()\n")),
        ("V03", "unpacked Web tree digest differs from the pin",
         fixture(work, "web", a=lambda p: package_members(p, web=dict(WEB, **{"index.html": b"<html>x</html>\n"}))),
         "WEB-DIGEST",
         ("                if digest != pins[\"web\"]:", "                if False:")),
        ("V04", "FFmpeg runtime archive differs from the pin",
         fixture(work, "ffarchive", runtime=dict(RUNTIME, **{"extra.dll": b"x"}),
                 a=lambda p: package_members(p, ffmpeg=dict(RUNTIME, **{"extra.dll": b"x"}))),
         "FFMPEG-ARCHIVE",
         ("    if runtime_digest != pins[\"ffmpeg\"]:", "    if False:")),
        ("V05", "ffmpeg/ tree in the ZIP is not the accepted runtime archive's members",
         fixture(work, "fftree", a=lambda p: package_members(p, ffmpeg=dict(RUNTIME, **{"ffmpeg.exe": b"MZ patched"}))),
         "FFMPEG-TREE",
         ("        if runtime_members is not None and packed != runtime_members:", "        if False:")),
        ("V06", "provenance.json serverCommit is not the commit built",
         fixture(work, "commit", a=lambda p: package_members(p, head="f" * 40)),
         "PROVENANCE-COMMIT",
         ("            if provenance.get(\"serverCommit\") != head:", "            if False:")),
        ("V07", "tesserafin.exe is not PE x64",
         fixture(work, "pe", a=lambda p: package_members(p, exe_machine=0x014C)),
         "PE-X64",
         ("        if machine != 0x8664:", "        if False:")),
        ("V08", "runtimeconfig declares a shared framework",
         fixture(work, "framework", a=lambda p: package_members(p, config={"runtimeOptions": {
             "includedFrameworks": [], "framework": {"name": "Microsoft.NETCore.App"}}})),
         "SELF-CONTAINED",
         ("                if shared in options:", "                if False:")),
        ("V09", "coreclr.dll is absent",
         fixture(work, "coreclr", a=lambda p: package_members(p, drop=("coreclr.dll",))),
         "SELF-CONTAINED",
         ("            if PACKAGE + \"/\" + component not in members:", "            if False:")),
    ]
    for cid, text, fx, code, mutation in cases:
        codes = [c for c, _ in run_verify(real, fx)]
        mutant = load_verifier(mutation)
        if mutant is None:
            record(False, cid, "%s: INERT, the mutation no longer applies to the verifier source" % text)
            continue
        mutant_codes = [c for c, _ in run_verify(mutant, fx)]
        red = code in codes
        caught = code not in mutant_codes
        record(red and caught, cid, "%s -> real verifier RED %s %s; mutant without the check %s"
               % (text, code, "observed" if red else "NOT OBSERVED (codes %s)" % codes,
                  "misses it, so the control is load-bearing" if caught else "still reports it: INERT"))

    source = open(VERIFIER, encoding="utf-8").read()
    offenders = [w for w in ("dotnet", "assemble-server-zip", "sha256.txt", "\".sha256", "'.sha256") if w in source]
    flags = re.findall(r'add_argument\("(--[a-z-]+)"', source)
    record(not offenders and flags == ["--zip-a", "--zip-b", "--runtime-archive", "--head", "--epoch"], "V10",
           "the verifier names no publish, assembler or hash file (%s) and its only arguments are %s"
           % (offenders or "none", flags))


# ===========================================================================
# Workflow audit
# ===========================================================================

def code_lines(text):
    return [l for l in text.splitlines() if not l.lstrip().startswith("#")]


def top_block(lines, key):
    out, inside = [], False
    for line in lines:
        if re.match(r"^%s:" % re.escape(key), line):
            inside = True
            rest = line.split(":", 1)[1].strip()
            if rest:
                out.append("  " + rest)
            continue
        if inside and line and not line.startswith(" "):
            break
        if inside and line.strip():
            out.append(line)
    return out


def jobs(lines):
    result, name = {}, None
    for line in top_block(lines, "jobs"):
        match = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if match:
            name = match.group(1)
            result[name] = []
        elif name:
            result[name].append(line)
    return result


def filter_paths(lines):
    on = top_block(lines, "on")
    paths, inside = [], False
    for line in on:
        if re.match(r"^    paths:\s*$", line):
            inside = True
            continue
        if inside:
            match = re.match(r"^      - '([^']+)'\s*$", line)
            if not match:
                break
            paths.append(match.group(1))
    return paths


def glob_to_regex(pattern):
    out, i = "", 0
    while i < len(pattern):
        if pattern.startswith("**", i):
            out += ".*"
            i += 2
        elif pattern[i] == "*":
            out += "[^/]*"
            i += 1
        elif pattern[i] == "?":
            out += "[^/]"
            i += 1
        else:
            out += re.escape(pattern[i])
            i += 1
    return re.compile("^" + out + "$")


def queues(paths, changed):
    return any(glob_to_regex(p).match(f) for p in paths for f in changed)


def project_closure():
    seen, queue = set(), [os.path.join(REPO_ROOT, "Tesserafin.Server", "Tesserafin.Server.csproj")]
    while queue:
        path = os.path.normpath(queue.pop())
        if path in seen:
            continue
        seen.add(path)
        text = open(path, encoding="utf-8-sig").read()
        for ref in re.findall(r'<ProjectReference\s+Include="([^"]+)"', text):
            queue.append(os.path.join(os.path.dirname(path), ref.replace("\\", "/")))
    return sorted(os.path.relpath(p, REPO_ROOT).replace(os.sep, "/") for p in seen)


def planted_core_edit(work):
    """A one-line C# edit in a real Tesserafin.Server.Core file, planted in a copy."""
    real = sorted(glob.glob(os.path.join(REPO_ROOT, "Tesserafin.Server.Core", "**", "*.cs"), recursive=True))[0]
    relative = os.path.relpath(real, REPO_ROOT).replace(os.sep, "/")
    plant = os.path.join(work, "plant", relative)
    os.makedirs(os.path.dirname(plant), exist_ok=True)
    data = open(real, "rb").read()
    with open(plant, "wb") as handle:
        handle.write(data + b"// W5-A1 planted one-line edit\n")
    changed = [relative] if open(plant, "rb").read() != data else []
    return relative, changed


def audit(text, work):
    """Findings for one workflow text."""
    findings = []
    lines = code_lines(text)
    body = "\n".join(lines)

    triggers = [re.match(r"^  ([A-Za-z_]+):", l).group(1) for l in top_block(lines, "on")
                if re.match(r"^  [A-Za-z_]+:", l)]
    if triggers != ["pull_request"]:
        findings.append("TRIGGERS: %s, required exactly pull_request" % triggers)

    perms = [l.strip() for l in top_block(lines, "permissions")]
    if perms != ["contents: read", "actions: read", "pull-requests: none"]:
        findings.append("PERMISSIONS: workflow permissions are %s" % perms)
    if re.search(r"write-all|read-all|:\s*write\b", body):
        findings.append("PERMISSIONS: a write or blanket grant appears")

    all_jobs = jobs(lines)
    for name, job in all_jobs.items():
        job_text = "\n".join(job)
        if "packages: read" in job_text and name not in ("assemble-a", "assemble-b"):
            findings.append("PERMISSIONS: %s holds packages: read" % name)
        if "actions/cache" in job_text:
            findings.append("CACHE: %s uses actions/cache" % name)
        for value in re.findall(r"^\s+cache:(.*)$", job_text, re.M):
            if value.strip() != "false":
                findings.append("CACHE: %s sets cache:%s" % (name, value))
        for step in re.split(r"^      - ", job_text, flags=re.M):
            if "actions/setup-dotnet" in step and not re.search(r"^          cache: false$", step, re.M):
                findings.append("CACHE: a setup-dotnet step in %s does not set cache: false" % name)

    verify = "\n".join(all_jobs.get("verify", []))
    for word in ("dotnet", "setup-dotnet", "assemble-server-zip", "publish"):
        if word in verify:
            findings.append("VERIFY-BUILDS: the verify job mentions %r" % word)
    if re.search(r"needs\.assemble-[ab]\.outputs|sha256\.txt|\.sha256\b", verify):
        findings.append("VERIFY-TRUSTS: the verify job reads an assemble job's hash")
    if "ci/windows/w5/verify-unsigned-zip.py" not in verify or "--zip-a" not in verify or "--zip-b" not in verify:
        findings.append("VERIFY-TRUSTS: the verify job does not run the verifier over both archives")
    if "if: ${{ always() }}" not in verify or not re.search(
            r"needs: \[controls, prepare, assemble-a, assemble-b\]", verify):
        findings.append("VERIFY-SKIPPABLE: the verify job is not always() over all four upstream jobs")

    for name in ("assemble-a", "assemble-b"):
        job = "\n".join(all_jobs.get(name, []))
        if not re.search(r"^    needs: \[prepare\]$", job, re.M):
            findings.append("ALLOCATION: %s does not need exactly [prepare]" % name)
        if "-SourceDateEpoch ${{ needs.prepare.outputs.epoch }}" not in job:
            findings.append("EPOCH: %s is not given prepare's single epoch" % name)
        uploads = re.findall(r"^\s+path: (.+)$", job, re.M)
        if len(uploads) != 1 or not uploads[0].endswith("\\out\\tesserafin-server_1.0.0_win-x64.zip"):
            findings.append("UPLOAD: %s uploads %s, not the ZIP alone" % (name, uploads))
        retention = re.findall(r"retention-days: (\d+)", job)
        if not retention or any(int(r) < 14 for r in retention):
            findings.append("RETENTION: %s retains %s days" % (name, retention))
        if "outputs:" in job:
            findings.append("VERIFY-TRUSTS: %s declares job outputs" % name)

    paths = filter_paths(lines)
    for required in RULING_PATHS:
        if required not in paths:
            findings.append("PATHS: the filter omits %s" % required)
    for project in project_closure():
        if not queues(paths, [project]):
            findings.append("PATHS: a change to %s would not queue this workflow" % project)
    relative, changed = planted_core_edit(work)
    if not changed or not queues(paths, changed):
        findings.append("PATHS: a planted one-line C# edit in %s does not queue this workflow" % relative)
    return findings


def workflow_controls(work):
    text = open(WORKFLOW, encoding="utf-8").read()
    findings = audit(text, work)
    record(not findings, "W00", "the committed workflow passes every audit rule (%s)" % (findings or "no findings"))

    mutations = [
        ("W01", "workflow permissions are write-all",
         "permissions:\n  contents: read\n  actions: read\n  pull-requests: none\n",
         "permissions: write-all\n", "PERMISSIONS"),
        ("W02", "a job holds contents: write",
         "    name: Verify the pair from the archive bytes\n",
         "    name: Verify the pair from the archive bytes\n    permissions:\n      contents: write\n", "PERMISSIONS"),
        ("W03", "the workflow is reachable from pull_request_target",
         "      - 'src/**'\n", "      - 'src/**'\n  pull_request_target:\n", "TRIGGERS"),
        ("W04", "the workflow is reachable from a push (fork or master)",
         "      - 'src/**'\n", "      - 'src/**'\n  push:\n", "TRIGGERS"),
        ("W05", "the verify job calls dotnet publish",
         "      - name: Verify both archives from their own bytes\n",
         "      - run: dotnet publish Tesserafin.Server --runtime win-x64\n"
         "      - name: Verify both archives from their own bytes\n", "VERIFY-BUILDS"),
        ("W06", "the verify job calls the assembler",
         "      - name: Verify both archives from their own bytes\n",
         "      - run: pwsh ./ci/windows/w2/assemble-server-zip.ps1\n"
         "      - name: Verify both archives from their own bytes\n", "VERIFY-BUILDS"),
        ("W07", "the verify job trusts an assemble job's printed hash",
         "            --head '${{ steps.epoch.outputs.head }}' \\\n",
         "            --head '${{ steps.epoch.outputs.head }}' --claimed '${{ needs.assemble-a.outputs.sha256 }}' \\\n",
         "VERIFY-TRUSTS"),
        ("W08", "assemble B waits for assemble A",
         "    name: Assemble the win-x64 ZIP on allocation B\n    needs: [prepare]\n",
         "    name: Assemble the win-x64 ZIP on allocation B\n    needs: [prepare, assemble-a]\n", "ALLOCATION"),
        ("W09", "assemble A uploads a hash file beside the ZIP",
         "          path: ${{ runner.temp }}\\w5a1-a\\out\\tesserafin-server_1.0.0_win-x64.zip\n",
         "          path: ${{ runner.temp }}\\w5a1-a\\out\\sha256.txt\n", "UPLOAD"),
        ("W10", "the verify job can be skipped",
         "    if: ${{ always() }}\n", "", "VERIFY-SKIPPABLE"),
        ("W11", "artifacts retained under 14 days",
         "          retention-days: 14\n", "          retention-days: 7\n", "RETENTION"),
        ("W12", "the path filter omits Tesserafin.Server.Core (planted one-line C# edit must queue)",
         "      - 'Tesserafin.Server.Core/**'\n", "", "PATHS"),
        ("W14", "workflow_dispatch is restored",
         "      - 'src/**'\n", "      - 'src/**'\n  workflow_dispatch:\n", "TRIGGERS"),
        ("W15", "an assemble setup-dotnet step drops cache: false",
         "          dotnet-version: ${{ env.SDK_VERSION }}\n          cache: false\n",
         "          dotnet-version: ${{ env.SDK_VERSION }}\n", "CACHE"),
    ]
    for cid, what, old, new, code in mutations:
        count = text.count(old)
        if count == 0:
            record(False, cid, "%s: INERT, the mutation no longer applies" % what)
            continue
        mutant = text.replace(old, new, 1)
        hits = [f for f in audit(mutant, work) if f.startswith(code)]
        record(bool(hits), cid, "%s -> RED %s" % (what, hits[:2] if hits else "NOT OBSERVED"))

    # The planted edit, stated on its own: queued by the real filter, not by
    # the filter without Core.
    paths = filter_paths(code_lines(text))
    relative, changed = planted_core_edit(work)
    without = [p for p in paths if p != "Tesserafin.Server.Core/**"]
    record(queues(paths, changed) and not queues(without, changed), "W13",
           "planted one-line edit to %s: queues with the committed filter, does not queue without "
           "Tesserafin.Server.Core/**" % relative)


# ===========================================================================
# Pins
# ===========================================================================

def pin_controls():
    drift = []
    for relative, expected in FROZEN_PINS.items():
        path = os.path.join(REPO_ROOT, *relative.split("/"))
        actual = sha256_bytes(open(path, "rb").read()) if os.path.isfile(path) else None
        if actual != expected:
            drift.append("%s is %s" % (relative, actual))
    w2 = sorted("ci/windows/w2/" + n for n in os.listdir(os.path.join(REPO_ROOT, "ci", "windows", "w2")))
    added = [n for n in w2 if n not in FROZEN_PINS]
    record(not drift and not added, "P01",
           "frozen W2 inputs, W0-W4 workflows, SharedVersion.cs and Tesserafin.wxs keep their "
           "W5-A0 master bytes (drift %s, added under w2/ %s)" % (drift or "none", added or "none"))

    verifier = load_verifier()
    pins_env = open(os.path.join(REPO_ROOT, "ci", "package", "pins.env"), encoding="utf-8").read()
    web = re.search(r"^WEB_PAYLOAD_SHA256=([0-9a-f]{64})$", pins_env, re.M).group(1)
    accepted = json.load(open(os.path.join(REPO_ROOT, "ci", "windows", "runtime-retention",
                                           "accepted-runtime.json"), encoding="utf-8"))
    record(verifier.PINS == {"web": web, "ffmpeg": accepted["runtimeSha256"]}
           and web == "4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f"
           and accepted["runtimeSha256"] == "f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e",
           "P02", "the verifier's pins equal pins.env WEB_PAYLOAD_SHA256 and accepted-runtime.json runtimeSha256")


def main():
    with tempfile.TemporaryDirectory(prefix="w5a1-controls-") as work:
        pin_controls()
        verifier_controls(work)
        workflow_controls(work)
    failed = RESULTS.count(False)
    print("W5-A1 controls: %d passed, %d failed" % (RESULTS.count(True), failed))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
