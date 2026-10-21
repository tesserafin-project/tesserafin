#!/usr/bin/env python3
"""Extended API-state oracle for the isolated container recovery rehearsal.

Fixture secrets use stdin/environment and private files, never command arguments.
This tool is only for a disposable local test server, not production migrations.
"""
import json
import os
import sys
from urllib.request import Request, urlopen

MEMBER_NAME = "a2member"
MEMBER_PASSWORD = "a2-member-recovery-pass"
POSITION = 100000000
SERVER_NAME = "A2 recovery proof"


class Api:
    def __init__(self, port):
        port = int(port)
        if not 1024 <= port <= 65535:
            raise ValueError("expected a local test port between 1024 and 65535")
        self.base = f"http://127.0.0.1:{port}"

    def call(self, method, path, token="", body=None, device="recovery-admin"):
        header = f'MediaBrowser Client="tf-a2", Device="recovery", DeviceId="{device}", Version="1.0"'
        if token:
            header += f', Token="{token}"'
        data = json.dumps(body).encode() if body is not None else None
        request = Request(self.base + path, data=data, method=method,
                          headers={"Authorization": header, "Content-Type": "application/json"})
        with urlopen(request, timeout=20) as response:
            raw = response.read()
        return json.loads(raw) if raw else None


def require(condition, message):
    if not condition:
        raise ValueError(message)


def seed(api, admin_id, item_id, token):
    member = api.call("POST", "/Users/New", token,
                      {"Name": MEMBER_NAME, "Password": MEMBER_PASSWORD})
    member_id = member["Id"]
    configuration = member["Configuration"]
    configuration.update(AudioLanguagePreference="fra", EnableNextEpisodeAutoPlay=False)
    api.call("POST", f"/Users/Configuration?userId={member_id}", token, configuration)
    policy = member["Policy"]
    policy.update(IsAdministrator=False, EnableAudioPlaybackTranscoding=False)
    api.call("POST", f"/Users/{member_id}/Policy", token, policy)
    auth = api.call("POST", "/Users/AuthenticateByName", body={
        "Username": MEMBER_NAME, "Pw": MEMBER_PASSWORD}, device="recovery-member")
    member_token = auth["AccessToken"]
    api.call("POST", f"/UserItems/{item_id}/UserData", member_token, {
        "PlaybackPositionTicks": POSITION, "Played": False, "PlayCount": 2,
        "IsFavorite": True, "LastPlayedDate": "2026-01-02T03:04:05Z"},
        device="recovery-member")
    pack = api.call("POST", "/ContentPacks", token,
                    {"Name": "Recovery fixture", "Description": "A2 preserved membership"})
    api.call("POST", f'/ContentPacks/{pack["Id"]}/Items/{item_id}', token)
    config = api.call("GET", "/System/Configuration", token)
    config["ServerName"] = SERVER_NAME
    api.call("POST", "/System/Configuration", token, config)
    return {"admin_id": admin_id, "item_id": item_id, "admin_token": token,
            "member_id": member_id, "member_token": member_token, "pack_id": pack["Id"]}


def capture(api, fixture):
    """Read saved state using the original tokens; separately prove password login."""
    admin_token = fixture["admin_token"]
    users = []
    for role in ("admin", "member"):
        token = fixture[role + "_token"]
        # This deliberately uses the pre-backup token after recovery, proving its
        # persisted authentication rather than silently replacing it with a login.
        current = api.call("GET", "/Users/Me", token, device="recovery-" + role)
        require(current["Id"] == fixture[role + "_id"], f"{role} token changed identity")
        user = api.call("GET", f'/Users/{current["Id"]}', admin_token)
        items = api.call("GET", f'/Items?userId={current["Id"]}&ids={fixture["item_id"]}',
                         token, device="recovery-" + role)["Items"]
        require(len(items) == 1 and items[0]["Id"] == fixture["item_id"],
                f"{role} item missing or changed identity")
        users.append({"role": role, "Id": user["Id"], "Name": user["Name"],
                      "HasPassword": user["HasPassword"], "Policy": user["Policy"],
                      "Configuration": user["Configuration"],
                      "UserData": {key: items[0]["UserData"].get(key) for key in
                                   ("Played", "PlayCount", "LastPlayedDate",
                                    "PlaybackPositionTicks", "IsFavorite")}})
    fresh = api.call("POST", "/Users/AuthenticateByName", body={
        "Username": MEMBER_NAME, "Pw": MEMBER_PASSWORD}, device="recovery-password-check")
    require(fresh["User"]["Id"] == fixture["member_id"], "member password changed identity")
    libraries = api.call("GET", "/Library/VirtualFolders", admin_token)
    libraries = sorted(({key: library.get(key) for key in
                         ("Name", "ItemId", "Locations", "CollectionType", "LibraryOptions")}
                        for library in libraries), key=lambda value: value["ItemId"])
    pack = api.call("GET", f'/ContentPacks/{fixture["pack_id"]}', admin_token)
    members = api.call("GET", f'/ContentPacks/{fixture["pack_id"]}/Items', admin_token)
    state = {"users": users, "libraries": libraries, "pack": pack,
             "pack_item_ids": sorted(item["Id"] for item in members["Items"]),
             "server_name": api.call("GET", "/System/Configuration", admin_token)["ServerName"]}
    validate(state, fixture)
    return state


def validate(state, fixture):
    """Require nonempty, distinct seeded observations before comparing snapshots."""
    admin, member = state["users"]
    require(admin["Id"] == fixture["admin_id"] and member["Id"] == fixture["member_id"]
            and admin["Id"] != member["Id"], "user identity mismatch")
    require(admin["HasPassword"] and member["HasPassword"], "password fixture missing")
    require(admin["Policy"]["IsAdministrator"] is True
            and member["Policy"]["IsAdministrator"] is False
            and member["Policy"]["EnableAudioPlaybackTranscoding"] is False,
            "policy fixture missing")
    require(member["Configuration"]["AudioLanguagePreference"] == "fra"
            and member["Configuration"]["EnableNextEpisodeAutoPlay"] is False,
            "user configuration fixture missing")
    require(admin["UserData"]["Played"] is True and admin["UserData"]["PlayCount"] == 1
            and admin["UserData"]["IsFavorite"] is False, "admin watched fixture missing")
    require(member["UserData"]["Played"] is False and member["UserData"]["PlayCount"] == 2
            and member["UserData"]["PlaybackPositionTicks"] == POSITION
            and member["UserData"]["IsFavorite"] is True
            and member["UserData"]["LastPlayedDate"].startswith("2026-01-02T03:04:05"),
            "member favourite/saved-position fixture missing")
    require(any(library["Name"] == "Movies" and library["ItemId"]
                and library["Locations"] == ["/media"] for library in state["libraries"]),
            "library identity/paths fixture missing")
    require(state["pack"]["Id"] == fixture["pack_id"]
            and state["pack"]["Name"] == "Recovery fixture"
            and state["pack"]["VisibleItemCount"] == 1
            and state["pack_item_ids"] == [fixture["item_id"]], "content pack fixture missing")
    require(state["server_name"] == SERVER_NAME, "server setting fixture missing")


def main():
    mode, port = sys.argv[1:3]
    api = Api(port)
    if mode == "seed":
        result = seed(api, sys.argv[3], sys.argv[4], os.environ["TF_RECOVERY_TOKEN"])
    elif mode == "capture":
        result = capture(api, json.load(sys.stdin))
    else:
        raise ValueError("expected seed or capture")
    print(json.dumps(result, sort_keys=True, indent=2))


if __name__ == "__main__":
    main()
