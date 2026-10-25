#!/usr/bin/env python3
"""Allowlisted provenance/results for an isolated Docker or native recovery rehearsal.

Never copies raw server output, authentication responses, fixture snapshots or
archive files into the retained evidence directory.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys


STAGES = {"0": "prepare", "1": "first-boot", "2": "populate", "2a": "extended-seed",
          "3": "backup", "3a": "artifact-permissions", "3b": "archive-validation",
          "3c": "restart-failure", "3d": "off-host-verification", "3e": "state-loss", "4": "restore", "5": "restored-state",
          "6": "bind-mounts", "7": "helper-pin"}
SEED_MARKER = "  PASS  distinct users, saved position, favourite, policies, configuration, pack and tokens seeded"
RESTORE_MARKER = "  PASS  extended state restored identically by user/item/pack identity"
COMPLETE_MARKER = "ROUNDTRIP: all gates + safety assertions passed"
SOURCE_MARKER = "ROUNDTRIP-SOURCE: snapshot handed off"
ABSENT_MARKER = "  PASS  recovery host holds no source volumes"
OFFHOST_MARKER = "  PASS  off-host snapshot matches its retained checksum"
# ci/native-recovery-deb.sh: purged package and deleted state, then the same oracle.
LOSS_MARKER = "  PASS  package purged and all persistent state removed before restore"
FILES_MARKER = "  PASS  restored files identical in content, owner, group and mode"
NATIVE_MARKER = "NATIVE-ROUNDTRIP: deb cold snapshot restored and verified"
NATIVE_RPM_MARKER = "NATIVE-ROUNDTRIP: rpm cold snapshot restored and verified"
# Its disk-loss split: the source host hands the snapshot off, a fresh host restores it.
NATIVE_SOURCE_MARKER = "NATIVE-SOURCE: snapshot handed off"
NATIVE_ABSENT_MARKER = "  PASS  recovery host never held a Tesserafin installation"
NATIVE_RECOVER_MARKER = "NATIVE-RECOVER: deb snapshot restored on a fresh host and verified"
# Markers each docker/state-roundtrip.sh phase must print before it can be accepted.
REQUIRED = {"all": (SEED_MARKER, RESTORE_MARKER, COMPLETE_MARKER),
            "source": (SEED_MARKER, SOURCE_MARKER),
            "recover": (ABSENT_MARKER, OFFHOST_MARKER, RESTORE_MARKER, COMPLETE_MARKER),
            "native-deb": (SEED_MARKER, LOSS_MARKER, FILES_MARKER, RESTORE_MARKER, NATIVE_MARKER),
            "native-rpm": (SEED_MARKER, LOSS_MARKER, FILES_MARKER, RESTORE_MARKER, NATIVE_RPM_MARKER),
            "native-deb-source": (SEED_MARKER, NATIVE_SOURCE_MARKER),
            "native-deb-recover": (NATIVE_ABSENT_MARKER, OFFHOST_MARKER, FILES_MARKER, RESTORE_MARKER,
                                   NATIVE_RECOVER_MARKER)}
# Allowlisted diagnostic lines emitted by docker/state-roundtrip.sh. Only fixed labels,
# keys and narrowly typed values are retained; any other DIAG line is dropped whole.
DIAG_LABELS = {"first-boot-ready", "first-boot-never-ready", "restored-never-ready", "restored-ready",
               "restored-capture-failed", "restored-users-me-window", "restored-after-window"}
HTTP_CODE = r"[0-9]{3}"
DIAG_VALUES = {
    "restart_count": r"[0-9]{1,4}",
    # Docker container states, then systemd ActiveState words.
    "status": r"created|restarting|running|removing|paused|exited|dead|missing"
              r"|active|inactive|activating|deactivating|failed|reloading",
    "oom_killed": r"true|false",
    "exit_code": r"[0-9]{1,3}",
    "interval_s": r"[0-9]{1,2}",
    "health_http": HTTP_CODE,
    "startup_config_http": HTTP_CODE,
    "users_me_http": HTTP_CODE + r"(?:," + HTTP_CODE + r"){0,29}",
}


def diagnostics(lines):
    result = []
    for line in lines:
        parts = line.split(" ")
        if len(parts) < 3 or parts[0] != "DIAG" or parts[1] not in DIAG_LABELS:
            continue
        fields = {}
        for part in parts[2:]:
            key, sep, value = part.partition("=")
            if not sep or key not in DIAG_VALUES or key in fields \
                    or not re.fullmatch(DIAG_VALUES[key], value):
                fields = None
                break
            fields[key] = value
        if fields:
            result.append({"label": parts[1], **fields})
    return result


def summarize(raw, exit_code, phase="all"):
    lines = raw.splitlines()
    stages = [STAGES[match[1]] for line in lines
              if (match := re.match(r"^== ([0-7](?:[a-e])?)\. ", line))
              and match[1] in STAGES]
    fail_count = sum(line.startswith("  FAIL  ") for line in lines)
    complete = exit_code == 0 and fail_count == 0 and all(
        marker in lines for marker in REQUIRED[phase])
    return {"schema": 1, "phase": phase, "exit_code": exit_code, "accepted": complete,
            "passed_assertions": sum(line.startswith("  PASS  ") for line in lines),
            "failed_assertions": fail_count, "stages": stages,
            "extended_seed_verified": SEED_MARKER in lines,
            "extended_restore_verified": RESTORE_MARKER in lines,
            # Only error classes/status codes and our own source line numbers,
            # never arbitrary exception messages, URLs or response bodies.
            "http_error_codes": sorted(set(re.findall(r"HTTPError: HTTP Error ([0-9]{3}):", raw))),
            "curl_error_codes": sorted(set(re.findall(r"curl: \(([0-9]{1,3})\)", raw))),
            "oracle_error_lines": sorted(set(int(line) for line in re.findall(
                r'File "[^"\n]*/docker/recovery-state\.py", line ([0-9]+)', raw))),
            "diagnostics": diagnostics(lines),
            "raw_log_sha256": hashlib.sha256(raw.encode()).hexdigest()}


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="mode", required=True)
    init = sub.add_parser("init")
    init.add_argument("--expected-source", required=True)
    init.add_argument("--out", required=True, type=Path)
    init.add_argument("--scope", default="same-image independent-volume recovery; no forward migration")
    image = sub.add_parser("image")
    image.add_argument("--image-id", required=True)
    image.add_argument("--expected-source", required=True)
    image.add_argument("--out", required=True, type=Path)
    package = sub.add_parser("package")
    package.add_argument("--deb", required=True, type=Path)
    package.add_argument("--expected-sha256", required=True)
    package.add_argument("--out", required=True, type=Path)
    result = sub.add_parser("result")
    result.add_argument("--raw", required=True, type=Path)
    result.add_argument("--exit-code", required=True, type=int)
    result.add_argument("--out", required=True, type=Path)
    result.add_argument("--phase", choices=sorted(REQUIRED), default="all")
    args = parser.parse_args()
    args.out.parent.mkdir(parents=True, exist_ok=True)
    if args.mode == "init":
        source = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
        if source != args.expected_source:
            raise ValueError("checkout is not the requested source commit")
        data = {"schema": 1, "source_commit": source,
                "source_tree": subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], text=True).strip(),
                "dockerfile_sha256": hashlib.sha256(Path("Dockerfile").read_bytes()).hexdigest(),
                "run_id": os.environ.get("GITHUB_RUN_ID"), "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
                "publication": False, "scope": args.scope}
    elif args.mode == "image":
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", args.image_id):
            raise ValueError("expected an immutable local Docker image ID")
        metadata = json.loads(subprocess.check_output(["docker", "image", "inspect", args.image_id], text=True))[0]
        labels = metadata["Config"].get("Labels") or {}
        if (metadata["Id"] != args.image_id or labels.get("org.opencontainers.image.revision") != args.expected_source
                or metadata["Os"] != "linux" or metadata["Architecture"] != "amd64"
                or metadata["Config"]["User"] != "10000:10000"):
            raise ValueError("selected image provenance/platform/identity mismatch")
        data = {"schema": 1, "image_id": metadata["Id"], "source_commit": args.expected_source,
                "os": metadata["Os"], "architecture": metadata["Architecture"],
                "user": metadata["Config"]["User"],
                "paired_web_revision": labels.get("org.tesserafin.web.revision"),
                "paired_web_image": labels.get("org.tesserafin.web.assets.image")}
    elif args.mode == "package":
        digest = hashlib.sha256(args.deb.read_bytes()).hexdigest()
        if digest != args.expected_sha256:
            raise ValueError("package does not match the pinned SHA-256")
        fields = {key: subprocess.check_output(["dpkg-deb", "-f", str(args.deb), key], text=True).strip()
                  for key in ("Package", "Version", "Architecture")}
        description = subprocess.check_output(["dpkg-deb", "-f", str(args.deb), "Description"], text=True)
        commits = dict(re.findall(r"^ (Server commit|Bundled Tesserafin Web commit): ([0-9a-f]{40})$",
                                  description, re.M))
        data = {"schema": 1, "file": args.deb.name, "sha256": digest, **fields,
                "server_commit": commits.get("Server commit"),
                "web_commit": commits.get("Bundled Tesserafin Web commit")}
    else:
        data = summarize(args.raw.read_text(errors="replace"), args.exit_code, args.phase)
    args.out.write_text(json.dumps(data, sort_keys=True, indent=2) + "\n")
    print(json.dumps(data, sort_keys=True))
    if args.mode == "result" and not data["accepted"]:
        sys.exit(1)


if __name__ == "__main__":
    main()
