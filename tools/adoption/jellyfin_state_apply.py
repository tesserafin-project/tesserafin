#!/usr/bin/env python3
"""Experimental, quiescent-target application of reviewed existing-account state only."""
import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request
import uuid

import jellyfin_dry_run as dry

FIELDS = dry.STATE_FIELDS
GUARDS = ("LastPlayedDate", "Likes", "Rating", "Key")
MAX_OPERATIONS = 128


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                                     ensure_ascii=False, allow_nan=False).encode()).hexdigest()


def guid(value):
    dry.require(isinstance(value, str), "invalid target identity")
    try:
        uuid.UUID(value)
    except ValueError as error:
        raise dry.InvalidInput("invalid target identity") from error
    return value


def state(value):
    dry.require(dry.state_valid(value), "unknown item state")
    return {key: value[key] for key in FIELDS}


def metadata(item):
    return {key: item.get(key) for key in ("Id", "Type", "Path", "ProviderIds", "RunTimeTicks")}


def prepare(source, target, mapping):
    simulation = dry.plan(source, target, mapping)
    items = dry.index(target["items"], "items")
    users = dry.index(target["users"], "users")
    states = {user: dry.index(rows, "states") for user, rows in target["userItems"].items()}
    operations = []
    omitted = dict(simulation["counts"])
    for row in simulation["records"]:
        if row["status"] != "state-candidate":
            continue
        user, item = guid(row["targetUserId"]), guid(row["targetItemId"])
        before = states[user][item]["UserData"]
        # This first apply slice never merges into a nonempty target. Dates cannot
        # be cleared by the current nullable update DTO, so they are not imported.
        if any(before.get(key) for key in (*FIELDS, "LastPlayedDate")):
            omitted["nonempty-target-not-supported"] = omitted.get("nonempty-target-not-supported", 0) + 1
            continue
        desired = state(row["sourceState"])
        if desired == state(before):
            omitted["date-only-not-supported"] = omitted.get("date-only-not-supported", 0) + 1
            continue
        operations.append({"sourceUserId": row["sourceUserId"], "sourceItemId": row["sourceItemId"],
                           "userId": user, "userName": users[user]["Name"],
                           "item": metadata(items[item]), "before": state(before), "after": desired})
    dry.require(0 < len(operations) <= MAX_OPERATIONS, "expected 1 to 128 eligible operations; use a bounded snapshot")
    return {"schemaVersion": 1, "mode": "quiescent-state-apply-v1",
            "sourceSnapshotSha256": digest(source), "targetSnapshotSha256": digest(target),
            "mappingSha256": digest(mapping), "targetServerId": target["serverId"],
            "targetProduct": "tesserafin", "targetVersion": dry.VERSIONS["tesserafin"][1],
            "operations": operations, "simulationCounts": omitted,
            "excluded": ["LastPlayedDate", "accounts", "passwords", "permissions", "media files"],
            "requiresQuiescentTarget": True}


def validate_plan(plan):
    dry.require(type(plan.get("schemaVersion")) is int and plan["schemaVersion"] == 1
                and plan.get("mode") == "quiescent-state-apply-v1"
                and plan.get("targetProduct") == "tesserafin"
                and plan.get("targetVersion") == dry.VERSIONS["tesserafin"][1]
                and dry.identity(plan.get("targetServerId"))
                and plan.get("requiresQuiescentTarget") is True, "unknown apply plan")
    for key in ("sourceSnapshotSha256", "targetSnapshotSha256", "mappingSha256"):
        dry.require(isinstance(plan.get(key), str) and re.fullmatch(r"[0-9a-f]{64}", plan[key]), "invalid plan provenance")
    operations = plan.get("operations")
    dry.require(isinstance(operations, list) and 0 < len(operations) <= MAX_OPERATIONS, "invalid operation count")
    seen = set()
    for op in operations:
        dry.require(isinstance(op, dict), "invalid operation")
        guid(op["userId"])
        guid(op["item"]["Id"])
        key = (op["userId"], op["item"]["Id"])
        dry.require(key not in seen, "duplicate target operation")
        seen.add(key)
        dry.require(dry.identity(op["userName"]) and op["item"].get("Type") in dry.LEAF_TYPES
                    and dry.canonical_path(op["item"].get("Path")) is not None, "invalid target identity")
        dry.require(set(op["before"]) == set(FIELDS) and set(op["after"]) == set(FIELDS), "unknown mutation fields")
        dry.require(not any(state(op["before"]).values()) and state(op["after"]) != state(op["before"]), "nonempty target or empty mutation")
        ticks = op["after"]["PlaybackPositionTicks"]
        if ticks:
            dry.require(type(op["item"].get("RunTimeTicks")) is int
                        and 0 < ticks < op["item"]["RunTimeTicks"], "invalid resume runtime")
    return plan


class Api(dry.Reader):
    def request(self, method, path, body=None, **query):
        userdata = re.fullmatch(r"/UserItems/([0-9a-fA-F-]+)/UserData", path)
        dry.require((method == "GET" and path in {"/System/Info/Public", "/Users", "/Items"})
                    or (userdata and method == "POST" and set(query) == {"userId"}), "operation outside apply surface")
        if userdata:
            guid(userdata[1])
            guid(query["userId"])
            dry.require(set(body) == set(FIELDS), "unknown mutation fields")
            state(body)
        remaining = self.deadline - time.monotonic()
        dry.require(remaining > 0 and self.requests_remaining > 0, "apply request/time budget exceeded")
        self.requests_remaining -= 1
        url = self.base_url + path + ("?" + urlencode(query) if query else "")
        request = Request(url, data=json.dumps(body).encode() if body is not None else None,
                          method=method, headers={"Authorization": self.authorization, "Accept": "application/json",
                                                  "Content-Type": "application/json"})
        try:
            with self.opener.open(request, timeout=min(15, remaining)) as response:
                chunks = []
                read = getattr(response, "read1", response.read)
                while True:
                    dry.require(time.monotonic() < self.deadline, "apply time budget exceeded")
                    chunk = read(min(65536, self.bytes_remaining + 1))
                    if not chunk:
                        break
                    self.bytes_remaining -= len(chunk)
                    dry.require(self.bytes_remaining >= 0, "apply byte budget exceeded")
                    chunks.append(chunk)
                raw = b"".join(chunks)
                return dry.decode(raw) if raw else None
        except (HTTPError, URLError, TimeoutError) as error:
            raise dry.InvalidInput("API operation failed; inspect private journal and reconcile before retry") from error

    def get(self, path, **query):
        return self.request("GET", path, **query)

    def write(self, op, value):
        return self.request("POST", f'/UserItems/{op["item"]["Id"]}/UserData', value, userId=op["userId"])


def observe(api, op):
    page = api.get("/Items", ids=op["item"]["Id"], userId=op["userId"],
                   fields="Path,ProviderIds", enableUserData="true", enableImages="false")
    dry.require(isinstance(page, dict) and isinstance(page.get("Items"), list) and len(page["Items"]) == 1,
                "target item is absent or inaccessible")
    item = page["Items"][0]
    dry.require(metadata(item) == op["item"], "target media identity changed")
    data = item.get("UserData")
    return {"state": state(data), "guards": {key: data.get(key) for key in GUARDS}}


def verify_target(api, plan):
    info = api.get("/System/Info/Public")
    dry.require(isinstance(info, dict) and info.get("ProductName") == dry.VERSIONS["tesserafin"][0]
                and info.get("Version") == plan["targetVersion"] and info.get("Id") == plan["targetServerId"],
                "target server identity/product/version changed")
    users = dry.index(api.get("/Users"), "users")
    inventories = {}
    for op in plan["operations"]:
        dry.require(users.get(op["userId"], {}).get("Name") == op["userName"], "target account identity changed")
        if op["userId"] not in inventories:
            inventories[op["userId"]] = api.items(op["userId"])
        matches = [item for item in inventories[op["userId"]]
                   if item.get("Type") == op["item"]["Type"]
                   and dry.canonical_path(item.get("Path")) == dry.canonical_path(op["item"]["Path"])]
        dry.require(len(matches) == 1 and metadata(matches[0]) == op["item"],
                    "live target media identity is absent, ambiguous or changed")


def sync_dir(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


class Journal:
    def __init__(self, path, plan, approved):
        self.path = Path(path)
        self.plan = plan
        self.approved = approved

    def read(self, name):
        path = self.path / name
        try:
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
        except FileNotFoundError:
            return None
        with os.fdopen(fd, "rb") as stream:
            mode = os.fstat(stream.fileno())
            dry.require(stat.S_ISREG(mode.st_mode) and mode.st_uid == os.getuid()
                        and not mode.st_mode & 0o077, "journal file is not private")
            return dry.decode(stream.read(dry.MAX_BYTES + 1))

    def put(self, name, value):
        dry.write_private(self.path / name, value)
        sync_dir(self.path)

    @contextmanager
    def locked(self):
        try:
            self.path.mkdir(mode=0o700)
        except FileExistsError:
            pass
        mode = self.path.lstat()
        dry.require(stat.S_ISDIR(mode.st_mode) and mode.st_uid == os.getuid()
                    and not mode.st_mode & 0o077, "journal directory is not private")
        # Persist the directory's linkage before any server write. Fsync of an
        # intent inside it does not make a newly created parent entry durable.
        # Repeat on reopen as a prior attempt may have failed this very fsync.
        sync_dir(self.path.parent)
        lock = self.path / ".lock"
        lock.mkdir(mode=0o700)  # exclusive; leftover lock after crash requires operator review
        try:
            manifest = self.read("manifest.json")
            expected = {"schemaVersion": 1, "planSha256": self.approved, "plan": self.plan}
            if manifest is None:
                dry.require(set(self.path.iterdir()) == {lock}, "refusing unknown journal contents")
                self.put("manifest.json", expected)
            else:
                dry.require(manifest == expected, "journal belongs to another reviewed plan")
            allowed = {"manifest.json", ".lock", "rollback-started.json"}
            allowed.update(f"{i:06d}.{kind}.json" for i in range(len(self.plan["operations"]))
                           for kind in ("intent", "applied", "rollback-intent", "rolled-back"))
            dry.require(all(p.name in allowed for p in self.path.iterdir()), "unknown journal entries")
            yield
        finally:
            lock.rmdir()


def execute(api, plan, approved, journal_path, *, rollback=False, quiescent=False):
    dry.require(os.name == "posix" and all(hasattr(os, name) for name in
                ("getuid", "O_DIRECTORY", "O_NOFOLLOW", "fsync")), "POSIX journal host required")
    validate_plan(plan)
    dry.require(quiescent, "pause all target writers and acknowledge quiescent-target mode")
    dry.require(approved == digest(plan), "reviewed plan hash does not match")
    journal = Journal(journal_path, plan, approved)
    with journal.locked():
        verify_target(api, plan)
        if rollback:
            if journal.read("rollback-started.json") is None:
                journal.put("rollback-started.json", {"planSha256": approved})
        else:
            dry.require(journal.read("rollback-started.json") is None, "rollback has begun; apply cannot resume")
        operations = list(enumerate(plan["operations"]))
        if rollback:
            operations.reverse()
        for i, op in operations:
            prefix = f"{i:06d}"
            intent = journal.read(prefix + ".intent.json")
            current = observe(api, op)
            if intent is None:
                if rollback:
                    continue  # no write intent: this journal owns no change here
                dry.require(current["state"] == op["before"] and current["guards"]["LastPlayedDate"] is None,
                            "nonempty or changed target state; recapture and review")
                intent = {"before": current, "after": {"state": op["after"], "guards": current["guards"]}}
                journal.put(prefix + ".intent.json", intent)  # fsync before the network write
            dry.require(intent["before"]["state"] == op["before"] and intent["after"]["state"] == op["after"]
                        and intent["before"]["guards"] == intent["after"]["guards"], "journal intent mismatch")
            before, after = intent["before"], intent["after"]
            if not rollback and journal.read(prefix + ".applied.json") is not None:
                dry.require(current == after, "previously applied target changed; refusing reapply")
                continue
            done_name = prefix + (".rolled-back.json" if rollback else ".applied.json")
            if journal.read(prefix + ".rolled-back.json") is not None:
                dry.require(rollback and current == before, "rolled-back target changed")
                continue
            desired, expected = (before, after) if rollback else (after, before)
            if current == desired:
                # Reconcile a response lost after commit without incrementing counts.
                if journal.read(done_name) is None:
                    journal.put(done_name, {"verified": True})
                continue
            dry.require(current == expected, "intervening target edit; refusing overwrite or rollback")
            if rollback and journal.read(prefix + ".rollback-intent.json") is None:
                journal.put(prefix + ".rollback-intent.json", {"verifiedExpectedState": after})
            api.write(op, desired["state"])
            dry.require(observe(api, op) == desired, "write verification failed; retain journal and reconcile")
            if journal.read(done_name) is None:
                journal.put(done_name, {"verified": True})
    return {"operations": len(plan["operations"]), "mode": "rollback" if rollback else "apply", "verified": True}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare_cli = commands.add_parser("prepare", help="offline explicit apply-plan proposal")
    for name in ("source", "target", "mapping", "output"):
        prepare_cli.add_argument("--" + name, type=Path, required=True)
    for mode in ("apply", "rollback"):
        command = commands.add_parser(mode)
        command.add_argument("--plan", type=Path, required=True)
        command.add_argument("--approve-sha256", required=True)
        command.add_argument("--journal", type=Path, required=True)
        command.add_argument("--url", required=True)
        command.add_argument("--token-file", type=Path, required=True)
        command.add_argument("--allow-http", action="store_true")
        command.add_argument("--quiescent-target", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.command == "prepare":
            plan = prepare(dry.read_json(args.source), dry.read_json(args.target), dry.read_json(args.mapping))
            dry.write_private(args.output, plan)
            print("review-plan-sha256=" + digest(plan))
        else:
            plan = dry.read_json(args.plan)
            validate_plan(plan)
            dry.require(args.approve_sha256 == digest(plan), "reviewed plan hash does not match")
            dry.require(args.quiescent_target, "quiescent-target acknowledgement required")
            api = Api(args.url, args.token_file.read_text(encoding="utf-8").strip(), args.allow_http)
            result = execute(api, plan, args.approve_sha256, args.journal,
                             rollback=args.command == "rollback", quiescent=args.quiescent_target)
            print(json.dumps(result, sort_keys=True))
    except (dry.InvalidInput, OSError, ValueError, TypeError, KeyError, RecursionError):
        parser.exit(2, "adoption apply: refused; preserve private journal, check plan/server identity and quiescent target\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
