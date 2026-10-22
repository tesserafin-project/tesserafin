#!/usr/bin/env python3
"""Read-only capture and offline adoption planning. There is deliberately no apply command."""
import argparse
from collections import Counter, defaultdict
from datetime import datetime
import ipaddress
import json
import os
from pathlib import Path
import re
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener

MAX_BYTES = 64 * 1024 * 1024
MAX_ROWS = 100_000
VERSIONS = {"jellyfin": ("Jellyfin Server", "10.10.7"), "tesserafin": ("Tesserafin Server", "1.0.0")}
STATE_FIELDS = ("IsFavorite", "Played", "PlayCount", "PlaybackPositionTicks")
ITEM_FIELDS = ("Id", "Name", "Type", "Path", "ProviderIds", "RunTimeTicks")
LEAF_TYPES = {"Movie", "Episode", "Audio"}


class InvalidInput(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise InvalidInput(message)


def unique_object(pairs):
    out = {}
    for key, value in pairs:
        require(key not in out, "duplicate JSON key")
        out[key] = value
    return out


def decode(raw):
    require(len(raw) <= MAX_BYTES, "JSON size limit exceeded")
    return json.loads(raw, object_pairs_hook=unique_object,
                      parse_constant=lambda _: require(False, "non-finite JSON number"))


def read_json(path):
    with open(path, "rb") as stream:
        return decode(stream.read(MAX_BYTES + 1))


def write_private(path, value):
    """Publish a complete owner-only file without ever replacing an existing file."""
    destination = Path(path)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=destination.parent,
                                         prefix=".adoption-", delete=False) as stream:
            temporary = stream.name
            os.chmod(temporary, 0o600)
            json.dump(value, stream, ensure_ascii=False, allow_nan=False, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
            require(os.fstat(stream.fileno()).st_size <= MAX_BYTES, "output size limit exceeded")
        os.link(temporary, destination)  # atomic, exclusive: no overwrite race
    finally:
        if temporary is not None:
            os.unlink(temporary)


def identity(value):
    return isinstance(value, str) and 0 < len(value) <= 512 and not any(ord(c) < 32 for c in value)


def index(rows, label):
    require(isinstance(rows, list) and len(rows) <= MAX_ROWS, label + " must be a bounded list")
    out = {}
    for row in rows:
        require(isinstance(row, dict) and identity(row.get("Id")), label + " has invalid identity")
        require(row["Id"] not in out, label + " has duplicate identity")
        out[row["Id"]] = row
    return out


def validate_snapshot(value, product):
    require(isinstance(value, dict) and type(value.get("schemaVersion")) is int and value.get("schemaVersion") == 1, "unknown snapshot schema")
    require(value.get("product") == product, "wrong snapshot product")
    require(value.get("version") == VERSIONS[product][1], "source/target version is outside the selected adapter")
    require(identity(value.get("serverId")), "snapshot server identity is missing")
    users = index(value.get("users"), "users")
    items = index(value.get("items"), "items")
    require(len(users) <= 64, "user count limit exceeded")
    require(all(identity(user.get("Name")) for user in users.values()), "user names are missing or invalid")
    for item in items.values():
        require(identity(item.get("Type")), "item type is missing")
        for key, limit in (("Name", 4096), ("Path", 8192)):
            require(item.get(key) is None or (isinstance(item[key], str) and len(item[key]) <= limit), "invalid item text field")
        ids = item.get("ProviderIds", {})
        require(isinstance(ids, dict) and all(identity(k) and identity(v) for k, v in ids.items()), "invalid provider identity")
        require(len({key.lower() for key in ids}) == len(ids), "ambiguous provider namespace")
        runtime = item.get("RunTimeTicks")
        require(runtime is None or (type(runtime) is int and 0 <= runtime < 2**63), "invalid runtime")
    require(isinstance(value.get("libraries"), list) and len(value["libraries"]) <= 512, "libraries are missing or too numerous")
    for library in value["libraries"]:
        require(isinstance(library, dict) and identity(library.get("Name")), "invalid library identity")
        locations = library.get("Locations")
        require(isinstance(locations, list) and len(locations) <= 1024 and
                all(isinstance(path, str) and 0 < len(path) <= 8192 for path in locations), "invalid library locations")
    by_user = value.get("userItems")
    require(isinstance(by_user, dict) and set(by_user) == set(users), "per-user coverage is incomplete or unknown")
    count = 0
    for user, rows in by_user.items():
        require(all(row_id in items for row_id in index(rows, "user items")), "user item has unknown identity")
        count += len(rows)
    require(count <= MAX_ROWS, "per-user record limit exceeded")
    return value


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise InvalidInput("API redirects are refused")


class Reader:
    def __init__(self, base_url, token, allow_http=False):
        parsed = urlsplit(base_url)
        require(parsed.scheme in {"http", "https"} and parsed.hostname is not None,
                "an explicit HTTP(S) server is required")
        require(not parsed.username and not parsed.password and not parsed.query and not parsed.fragment,
                "credentials, query and fragment do not belong in the server URL")
        try:
            local = ipaddress.ip_address(parsed.hostname).is_loopback
        except ValueError:
            local = parsed.hostname.lower() == "localhost"
        require(parsed.scheme == "https" or local or allow_http, "LAN HTTP requires --allow-http")
        require(isinstance(token, str) and 0 < len(token) <= 8192 and token.strip() and not any(c in token for c in "\r\n\""),
                "credential file is empty or malformed")
        self.base_url = base_url.rstrip("/")
        self.token = token
        # Tesserafin disables the legacy X-Emby-Token header; both products accept this form.
        self.authorization = f'MediaBrowser Token="{token}"'
        # Never forward a credential through an environment-configured proxy or redirect.
        self.opener = build_opener(ProxyHandler({}), NoRedirect())
        self.remaining = MAX_ROWS
        self.bytes_remaining = MAX_BYTES
        self.requests_remaining = 1024
        self.deadline = time.monotonic() + 180

    def get(self, path, **query):
        require(path in {"/System/Info/Public", "/Users", "/Library/VirtualFolders", "/Items"},
                "operation is outside the read-only capture surface")
        remaining = self.deadline - time.monotonic()
        require(remaining > 0 and self.requests_remaining > 0, "capture time/request budget exceeded")
        self.requests_remaining -= 1
        url = self.base_url + path + ("?" + urlencode(query) if query else "")
        request = Request(url, method="GET", headers={"Authorization": self.authorization, "Accept": "application/json"})
        try:
            with self.opener.open(request, timeout=min(15, remaining)) as response:
                chunks = []
                read = getattr(response, "read1", response.read)
                while True:
                    require(time.monotonic() < self.deadline, "capture time budget exceeded")
                    chunk = read(min(65536, self.bytes_remaining + 1))
                    if not chunk:
                        break
                    self.bytes_remaining -= len(chunk)
                    require(self.bytes_remaining >= 0, "capture byte budget exceeded")
                    chunks.append(chunk)
                return decode(b"".join(chunks))
        except (HTTPError, URLError, TimeoutError) as error:
            # Do not print exception URLs, server error bodies or credentials.
            raise InvalidInput("API read failed; check the selected server, permissions and connectivity") from error

    def items(self, user_id):
        rows = []
        seen = set()
        total = None
        for _ in range(4096):
            page = self.get("/Items", userId=user_id, recursive="true", fields="Path,ProviderIds",
                            enableUserData="true", enableImages="false", enableTotalRecordCount="true",
                            sortBy="SortName", sortOrder="Ascending", startIndex=len(rows), limit=200)
            require(isinstance(page, dict), "invalid item page")
            current_total = page.get("TotalRecordCount")
            require(type(current_total) is int and 0 <= current_total <= MAX_ROWS, "invalid item total")
            if total is None:
                total = current_total
            require(total == current_total, "library changed during capture; retry while it is stable")
            batch = page.get("Items")
            require(isinstance(batch, list) and len(batch) <= 200, "invalid item batch")
            require(bool(batch) or len(rows) == total, "incomplete item pagination")
            for row in batch:
                require(isinstance(row, dict) and identity(row.get("Id")) and row["Id"] not in seen,
                        "repeated/invalid item across pages")
                seen.add(row["Id"])
                rows.append(row)
                self.remaining -= 1
                require(self.remaining >= 0, "capture record budget exceeded")
            require(len(rows) <= total, "inconsistent item pagination")
            if len(rows) == total:
                return rows
        raise InvalidInput("capture page budget exceeded")


def capture(reader, product):
    info = reader.get("/System/Info/Public")
    expected_name, expected_version = VERSIONS[product]
    require(isinstance(info, dict) and info.get("ProductName") == expected_name, "wrong server product")
    require(info.get("Version") == expected_version, "server version is outside the selected adapter")
    users = index(reader.get("/Users"), "users")
    require(0 < len(users) <= 64, "capture requires a bounded administrator user inventory")
    libraries = reader.get("/Library/VirtualFolders")  # elevated read; ordinary users must be refused
    require(isinstance(libraries, list), "invalid library inventory")
    items = {}
    user_items = {}
    for user_id in sorted(users):
        records = []
        for row in reader.items(user_id):
            item = {key: row[key] for key in ITEM_FIELDS if key in row}
            if row["Id"] in items:
                previous = items[row["Id"]]
                require(all(previous.get(k) == item.get(k) for k in ("Type", "Path", "ProviderIds")),
                        "item identity changed during capture")
            else:
                items[row["Id"]] = item
            state = row.get("UserData")
            require(state is None or isinstance(state, dict), "invalid per-user state")
            records.append({"Id": row["Id"], "UserData": {
                key: state[key] for key in (*STATE_FIELDS, "LastPlayedDate") if state and key in state
            }})
        user_items[user_id] = records
    after = reader.get("/System/Info/Public")
    require(isinstance(after, dict), "invalid final server identity")
    require(all(after.get(k) == info.get(k) for k in ("Id", "ProductName", "Version")), "server changed during capture")
    return validate_snapshot({
        "schemaVersion": 1, "product": product, "version": info["Version"], "serverId": info.get("Id"),
        "users": [{"Id": user_id, "Name": users[user_id].get("Name")} for user_id in sorted(users)],
        "libraries": [{key: row[key] for key in ("Name", "Locations", "CollectionType") if key in row} for row in libraries],
        "items": list(items.values()), "userItems": user_items
    }, product)


def canonical_path(value):
    if not isinstance(value, str) or not value or any(ord(c) < 32 for c in value):
        return None
    windows = bool(re.match(r"^[A-Za-z]:[\\/]", value)) or value.startswith("\\\\")
    path = value.replace("\\", "/") if windows else value
    if not (path.startswith("/") or re.match(r"^[A-Za-z]:/", path)):
        return None
    if any(part in {".", ".."} for part in path.split("/")):
        return None
    return path.rstrip("/") or "/"


def validate_mapping(value, source_users, target_users):
    require(isinstance(value, dict) and set(value) <= {"users", "paths"}, "invalid mapping document")
    users = value.get("users", {})
    require(isinstance(users, dict) and all(s in source_users and t in target_users for s, t in users.items()),
            "user mapping names unknown identities")
    require(len(set(users.values())) == len(users), "merging two users is not a supported mapping")
    paths = value.get("paths", [])
    require(isinstance(paths, list), "invalid path mappings")
    normalized = []
    for pair in paths:
        require(isinstance(pair, dict) and set(pair) == {"source", "target"}, "invalid path mapping")
        old, new = canonical_path(pair["source"]), canonical_path(pair["target"])
        require(old is not None and new is not None, "path mappings must be absolute and contain no traversal")
        normalized.append((old, new))
    require(len({old for old, _ in normalized}) == len(normalized), "duplicate path mapping prefix")
    return users, sorted(normalized, key=lambda pair: -len(pair[0]))


def remap_path(value, mappings):
    path = canonical_path(value)
    if path is None:
        return None
    for old, new in mappings:
        if path == old or path.startswith(old.rstrip("/") + "/"):
            return new.rstrip("/") + path[len(old.rstrip("/")):]
    return path


def state_valid(state):
    if not isinstance(state, dict) or not all(k in state for k in STATE_FIELDS):
        return False
    if not all(type(state[k]) is bool for k in ("IsFavorite", "Played")):
        return False
    for key, maximum in (("PlayCount", 2**31 - 1), ("PlaybackPositionTicks", 2**63 - 1)):
        if type(state[key]) is not int or not 0 <= state[key] <= maximum:
            return False
    date = state.get("LastPlayedDate")
    if date is not None:
        try:
            if not isinstance(date, str) or datetime.fromisoformat(date.replace("Z", "+00:00")).tzinfo is None:
                return False
        except ValueError:
            return False
    return True


def provider_keys(item):
    ids = item.get("ProviderIds")
    if not isinstance(ids, dict):
        return set()
    return {(item.get("Type"), key.lower(), value) for key, value in ids.items()
            if isinstance(key, str) and isinstance(value, str) and value}


def plan(source, target, mapping):
    validate_snapshot(source, "jellyfin")
    validate_snapshot(target, "tesserafin")
    source_users = index(source["users"], "users")
    target_users = index(target["users"], "users")
    user_map, path_map = validate_mapping(mapping, source_users, target_users)
    source_items = index(source["items"], "items")
    source_paths = Counter((item.get("Type"), remap_path(item.get("Path"), path_map))
                           for item in source_items.values() if item.get("Type") in LEAF_TYPES)
    target_items = index(target["items"], "items")
    by_path, by_provider = defaultdict(list), defaultdict(set)
    for item in target_items.values():
        if item.get("Type") not in LEAF_TYPES:
            continue
        path = canonical_path(item.get("Path"))
        if path is not None:
            by_path[(item["Type"], path)].append(item["Id"])
        for key in provider_keys(item):
            by_provider[key].add(item["Id"])
    target_states = {user: {row["Id"]: row.get("UserData") for row in rows} for user, rows in target["userItems"].items()}
    records = []
    for source_user in sorted(source_users):
        for row in sorted(source["userItems"][source_user], key=lambda r: r["Id"]):
            item = source_items[row["Id"]]
            state = row.get("UserData")
            record = {"sourceUserId": source_user, "sourceItemId": row["Id"]}
            records.append(record)
            if source_user not in user_map:
                record["status"] = "user-unmapped"
                continue
            target_user = user_map[source_user]
            record["targetUserId"] = target_user
            if not state_valid(state):
                record["status"] = "source-state-unknown"
                continue
            if not any(state.get(k) for k in (*STATE_FIELDS, "LastPlayedDate")):
                record["status"] = "no-source-state"
                continue
            path = remap_path(item.get("Path"), path_map)
            if item.get("Type") not in LEAF_TYPES or path is None:
                record["status"] = "unsupported-item"
                continue
            if source_paths[(item["Type"], path)] > 1:
                record["status"] = "ambiguous-source-path"
                continue
            matches = by_path[(item["Type"], path)]
            if len(matches) != 1:
                candidates = set().union(*(by_provider[k] for k in provider_keys(item)))
                record["status"] = "ambiguous-path" if len(matches) > 1 else "provider-review" if candidates else "item-unmatched"
                if candidates:
                    record["candidateItemIds"] = sorted(candidates)
                continue
            target_id = matches[0]
            record["targetItemId"] = target_id
            # Metadata conflicts never override exact path identity silently.
            src_ids, dst_ids = dict((k[1], k[2]) for k in provider_keys(item)), dict((k[1], k[2]) for k in provider_keys(target_items[target_id]))
            if any(key in dst_ids and dst_ids[key] != value for key, value in src_ids.items()):
                record["status"] = "provider-conflict"
                continue
            ticks = state["PlaybackPositionTicks"]
            source_runtime = item.get("RunTimeTicks")
            target_runtime = target_items[target_id].get("RunTimeTicks")
            if ticks and (type(target_runtime) is not int or target_runtime <= 0):
                record["status"] = "resume-runtime-unknown"
                continue
            if ticks and (ticks >= target_runtime or source_runtime != target_runtime):
                record["status"] = "resume-runtime-conflict"
                continue
            existing = target_states[target_user].get(target_id)
            if not state_valid(existing):
                record["status"] = "target-state-unknown"
                continue
            fields = (*STATE_FIELDS, "LastPlayedDate")
            if all(state.get(k) == existing.get(k) for k in fields):
                record["status"] = "unchanged"
            elif any(existing.get(k) and state.get(k) != existing.get(k) for k in fields):
                record["status"] = "target-state-conflict"
            else:
                record["status"] = "state-candidate"
                record["sourceState"] = {key: state[key] for key in fields if key in state}
    return {
        "schemaVersion": 1, "mode": "simulation-only", "writesToServers": False,
        "source": {"product": source["product"], "version": source["version"]},
        "target": {"product": target["product"], "version": target["version"]},
        "counts": dict(sorted(Counter(row["status"] for row in records).items())),
        "users": [{"sourceUserId": key, "sourceName": value.get("Name"), "targetUserId": user_map.get(key),
                   "passwordTransfer": "excluded", "newAccountRequiresPasswordReset": True, "policyTransfer": "not-planned"} for key, value in sorted(source_users.items())],
        "libraries": [{key: row[key] for key in ("Name", "Locations", "CollectionType") if key in row}
                      for row in source["libraries"]], "records": records,
        "limitations": ["No users, permissions, passwords, media or state are written.",
                        "Only Movie/Episode/Audio filesystem paths are selected; provider matches require review.",
                        "API capture is not transactional or whole-server disaster recovery.",
                        "Candidate state is not proof of an applied import; there is no apply command."]
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    export = commands.add_parser("capture", help="GET-only snapshot of one selected server")
    export.add_argument("--product", choices=VERSIONS, required=True)
    export.add_argument("--url", required=True)
    export.add_argument("--token-file", type=Path, required=True)
    export.add_argument("--allow-http", action="store_true")
    export.add_argument("--output", type=Path, required=True)
    simulate = commands.add_parser("plan", help="offline simulation; no server connection")
    simulate.add_argument("--source", type=Path, required=True)
    simulate.add_argument("--target", type=Path, required=True)
    simulate.add_argument("--mapping", type=Path, required=True)
    simulate.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "capture":
            token = args.token_file.read_text(encoding="utf-8").strip()
            value = capture(Reader(args.url, token, args.allow_http), args.product)
        else:
            value = plan(read_json(args.source), read_json(args.target), read_json(args.mapping))
        write_private(args.output, value)
    except InvalidInput as error:
        # These messages are controlled strings; they interpolate no input data.
        parser.exit(2, f"adoption: refused ({error})\n")
    except (OSError, ValueError, TypeError, KeyError, RecursionError):
        # Report neither response bodies nor input data: snapshots contain private
        # paths/names/history, and HTTP exceptions can carry credential-bearing URLs.
        parser.exit(2, "adoption: refused; check version, input shape, mappings, permissions and output path\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
