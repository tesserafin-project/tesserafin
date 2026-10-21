#!/usr/bin/env python3
"""Negative controls for the recovery oracle, not a server restore simulation."""
import copy
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("recovery", ROOT / "docker/recovery-state.py")
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)

FIXTURE = {"admin_id": "admin", "member_id": "member", "item_id": "movie",
           "pack_id": "pack", "admin_token": "admin-secret", "member_token": "member-secret"}
STATE = {
    "users": [
        {"Id": "admin", "HasPassword": True, "Policy": {"IsAdministrator": True},
         "UserData": {"Played": True, "PlayCount": 1, "IsFavorite": False}},
        {"Id": "member", "HasPassword": True,
         "Policy": {"IsAdministrator": False, "EnableAudioPlaybackTranscoding": False},
         "Configuration": {"AudioLanguagePreference": "fra", "EnableNextEpisodeAutoPlay": False},
         "UserData": {"Played": False, "PlayCount": 2, "IsFavorite": True,
                      "PlaybackPositionTicks": recovery.POSITION,
                      "LastPlayedDate": "2026-01-02T03:04:05.0000000Z"}}],
    "libraries": [{"Name": "Movies", "ItemId": "library", "Locations": ["/media"]}],
    "pack": {"Id": "pack", "Name": "Recovery fixture", "VisibleItemCount": 1},
    "pack_item_ids": ["movie"], "server_name": recovery.SERVER_NAME}


class RecoveryOracleTests(unittest.TestCase):
    def test_seeded_nonempty_state_is_accepted(self):
        recovery.validate(STATE, FIXTURE)

    def test_each_loss_or_identity_mixup_is_rejected_before_snapshot_comparison(self):
        mutations = [
            lambda s: s["users"].reverse(),
            lambda s: s["users"][1].update(Id="admin"),
            lambda s: s["users"][1].update(HasPassword=False),
            lambda s: s["users"][1]["Policy"].update(IsAdministrator=True),
            lambda s: s["users"][1]["Policy"].update(EnableAudioPlaybackTranscoding=True),
            lambda s: s["users"][1]["Configuration"].update(AudioLanguagePreference="eng"),
            lambda s: s["users"][1]["Configuration"].update(EnableNextEpisodeAutoPlay=True),
            lambda s: s["users"][0]["UserData"].update(Played=False),
            lambda s: s["users"][0]["UserData"].update(PlayCount=0),
            lambda s: s["users"][1]["UserData"].update(PlaybackPositionTicks=0),
            lambda s: s["users"][1]["UserData"].update(IsFavorite=False),
            lambda s: s["users"][1]["UserData"].update(LastPlayedDate="2026-02-01T00:00:00Z"),
            lambda s: s["users"][1].update(UserData=copy.deepcopy(s["users"][0]["UserData"])),
            lambda s: s.update(libraries=[]),
            lambda s: s["libraries"][0].update(Locations=["/wrong-media"]),
            lambda s: s["pack"].update(Id="other-pack"),
            lambda s: s["pack"].update(VisibleItemCount=0),
            lambda s: s.update(pack_item_ids=[]),
            lambda s: s.update(server_name="default")]
        for index, mutate in enumerate(mutations):
            with self.subTest(index=index):
                state = copy.deepcopy(STATE)
                mutate(state)
                with self.assertRaises(ValueError):
                    recovery.validate(state, FIXTURE)

    def test_capture_reads_each_users_item_with_that_users_original_token(self):
        class Api:
            def __init__(self):
                self.calls = []

            def call(self, method, path, token="", body=None, device="tf-a2-rt"):
                self.calls.append((method, path, token, body, device))
                if path == "/Users/Me":
                    return {"Id": "member" if token == "member-secret" else "admin"}
                if path.startswith("/Users/") and path != "/Users/AuthenticateByName":
                    data = copy.deepcopy(STATE["users"][path.endswith("member")])
                    data.setdefault("Configuration", {})
                    data["Name"] = data["Id"]
                    return data
                if path.startswith("/Items?"):
                    role = "member" if "userId=member" in path else "admin"
                    if token != role + "-secret":
                        raise AssertionError("item state fetched with the wrong user's token")
                    return {"Items": [{"Id": "movie", "UserData": copy.deepcopy(
                        STATE["users"][role == "member"]["UserData"])}]}
                if path == "/Users/AuthenticateByName":
                    return {"User": {"Id": "member"}, "AccessToken": "fresh-unused"}
                if path == "/Library/VirtualFolders":
                    return copy.deepcopy(STATE["libraries"])
                if path == "/ContentPacks/pack":
                    return copy.deepcopy(STATE["pack"])
                if path == "/ContentPacks/pack/Items":
                    return {"Items": [{"Id": "movie"}]}
                if path == "/System/Configuration":
                    return {"ServerName": recovery.SERVER_NAME}
                raise AssertionError(path)

        api = Api()
        state = recovery.capture(api, FIXTURE)
        self.assertEqual(state["users"][1]["UserData"]["PlaybackPositionTicks"], recovery.POSITION)
        self.assertNotIn("admin-secret", str(state))
        self.assertNotIn("member-secret", str(state))
        logins = [call for call in api.calls if call[1] == "/Users/AuthenticateByName"]
        self.assertEqual(logins[0][3], {"Username": recovery.MEMBER_NAME, "Pw": recovery.MEMBER_PASSWORD})

    def test_loopback_api_refuses_invalid_ports(self):
        for port in ("remote.example", 0, 65536):
            with self.subTest(port=port):
                with self.assertRaises(ValueError):
                    recovery.Api(port)


if __name__ == "__main__":
    unittest.main()
