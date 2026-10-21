#!/usr/bin/env python3
"""Allowlisted provenance/results for an isolated Docker recovery rehearsal.

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
          "3c": "restart-failure", "4": "restore", "5": "restored-state",
          "6": "bind-mounts", "7": "helper-pin"}
SEED_MARKER = "  PASS  distinct users, saved position, favourite, policies, configuration, pack and tokens seeded"
RESTORE_MARKER = "  PASS  extended state restored identically by user/item/pack identity"
COMPLETE_MARKER = "ROUNDTRIP: all gates + safety assertions passed"


def summarize(raw, exit_code):
    lines = raw.splitlines()
    stages = [STAGES[match[1]] for line in lines
              if (match := re.match(r"^== ([0-7](?:[abc])?)\. ", line))
              and match[1] in STAGES]
    fail_count = sum(line.startswith("  FAIL  ") for line in lines)
    complete = exit_code == 0 and fail_count == 0 and all(
        marker in lines for marker in (SEED_MARKER, RESTORE_MARKER, COMPLETE_MARKER))
    return {"schema": 1, "exit_code": exit_code, "accepted": complete,
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
            "raw_log_sha256": hashlib.sha256(raw.encode()).hexdigest()}


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="mode", required=True)
    init = sub.add_parser("init")
    init.add_argument("--expected-source", required=True)
    init.add_argument("--out", required=True, type=Path)
    image = sub.add_parser("image")
    image.add_argument("--image-id", required=True)
    image.add_argument("--expected-source", required=True)
    image.add_argument("--out", required=True, type=Path)
    result = sub.add_parser("result")
    result.add_argument("--raw", required=True, type=Path)
    result.add_argument("--exit-code", required=True, type=int)
    result.add_argument("--out", required=True, type=Path)
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
                "publication": False, "scope": "same-image independent-volume recovery; no forward migration"}
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
    else:
        data = summarize(args.raw.read_text(errors="replace"), args.exit_code)
    args.out.write_text(json.dumps(data, sort_keys=True, indent=2) + "\n")
    print(json.dumps(data, sort_keys=True))
    if args.mode == "result" and not data["accepted"]:
        sys.exit(1)


if __name__ == "__main__":
    main()
