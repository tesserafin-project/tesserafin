#!/usr/bin/env python3
"""Disposable real-image adoption rehearsal; never point this at a household server."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
from urllib.parse import urlencode
from urllib.request import Request, build_opener, ProxyHandler
import uuid

import jellyfin_dry_run as dry
import jellyfin_state_apply as app


def docker(*arguments):
    return subprocess.check_output(["docker", *arguments], stderr=subprocess.DEVNULL,
                                   text=True, timeout=600).strip()


def host_port(name):
    # The host port is ephemeral, and docker restart allocates a new one.
    return json.loads(docker("inspect", name))[0]["NetworkSettings"]["Ports"]["8096/tcp"][0]["HostPort"]


class FixtureApi:
    def __init__(self, port):
        self.url = f"http://127.0.0.1:{port}"
        self.token = ""
        self.opener = build_opener(ProxyHandler({}), dry.NoRedirect())

    def call(self, method, path, body=None):
        header = 'MediaBrowser Client="adoption-rehearsal", Device="fixture", DeviceId="adoption-fixture", Version="1.0"'
        if self.token:
            header += f', Token="{self.token}"'
        request = Request(self.url + path, method=method,
                          data=json.dumps(body).encode() if body is not None else None,
                          headers={"Authorization": header, "Content-Type": "application/json"})
        with self.opener.open(request, timeout=20) as response:
            raw = response.read(dry.MAX_BYTES + 1)
        return dry.decode(raw) if raw else None

    def wait_ready(self):
        stable = 0
        for _ in range(150):
            try:
                self.call("GET", "/System/Info/Public")
                stable += 1
                if stable >= 5:
                    return
            except Exception:
                stable = 0
            time.sleep(2)
        raise ValueError("fixture startup timeout")

    def setup(self):
        self.call("GET", "/Startup/User")
        self.call("POST", "/Startup/User", {"Name": "Adoption admin", "Password": "adoption-fixture-pass"})
        self.call("POST", "/Startup/Complete")
        auth = self.call("POST", "/Users/AuthenticateByName",
                         {"Username": "Adoption admin", "Pw": "adoption-fixture-pass"})
        self.token = auth["AccessToken"]
        member = self.call("POST", "/Users/New", {"Name": "Adoption member", "Password": "adoption-member-pass"})
        query = urlencode({"name": "Movies", "collectionType": "movies", "paths": "/media", "refreshLibrary": "true"})
        self.call("POST", "/Library/VirtualFolders?" + query, {"LibraryOptions": {
            "PathInfos": [{"Path": "/media"}], "EnableRealtimeMonitor": False,
            "EnableInternetProviders": False, "MetadataSavers": [],
            "TypeOptions": [{"Type": kind, "MetadataFetchers": [], "ImageFetchers": []}
                            for kind in ("Movie", "Folder")]}})
        admin = auth["User"]["Id"]
        for _ in range(90):
            page = self.call("GET", "/Items?" + urlencode({"userId": admin, "recursive": "true",
                "includeItemTypes": "Movie", "fields": "Path,ProviderIds", "enableUserData": "true"}))
            movies = page["Items"]
            if len(movies) == 1 and movies[0].get("RunTimeTicks", 0) > 100_000_000:
                return admin, member["Id"], movies[0]["Id"]
            time.sleep(2)
        raise ValueError("fixture media scan timeout")


def userdata(snapshot, user, item):
    return next(row["UserData"] for row in snapshot["userItems"][user] if row["Id"] == item)


def run(args):
    dry.require(re.fullmatch(r"[0-9a-f]{40}", args.expected_source) is not None, "exact source commit required")
    dry.require(re.fullmatch(r"sha256:[0-9a-f]{64}", args.target_image) is not None, "immutable target image required")
    target_meta = json.loads(docker("image", "inspect", args.target_image))[0]
    dry.require(target_meta["Config"]["Labels"]["org.opencontainers.image.revision"] == args.expected_source,
                "target source/image mismatch")
    docker("pull", "jellyfin/jellyfin:10.10.7")
    source_meta = json.loads(docker("image", "inspect", "jellyfin/jellyfin:10.10.7"))[0]
    source_digest = next(value for value in source_meta["RepoDigests"]
                         if value.startswith("jellyfin/jellyfin@sha256:"))
    stage = "prepare"
    names, volumes = [], []
    try:
        with tempfile.TemporaryDirectory(prefix="adoption-rehearsal-") as temporary:
            private = Path(temporary)
            private.chmod(0o700)
            media = private / "media"
            movie_dir = media / "Fixture Movie (2020)"
            movie_dir.mkdir(parents=True)
            media.chmod(0o755)
            movie_dir.chmod(0o755)
            movie = movie_dir / "Fixture Movie (2020).mp4"
            docker("run", "--rm", "--user", f"{os.getuid()}:{os.getgid()}",
                   "--entrypoint", "/usr/lib/jellyfin-ffmpeg/ffmpeg", "-v", f"{media}:/m", args.target_image,
                   "-y", "-f", "lavfi", "-i", "testsrc=duration=30:size=160x120:rate=2", "-pix_fmt", "yuv420p",
                   "/m/Fixture Movie (2020)/Fixture Movie (2020).mp4")
            movie.chmod(0o644)
            apis = []
            fixture_ids = []
            for role, image in (("source", source_meta["Id"]), ("target", args.target_image)):
                stage = role + "-setup"
                name = "adoption-" + uuid.uuid4().hex[:12]
                names.append(name)
                role_volumes = [name + "-" + part for part in ("config", "cache", "data")]
                volumes.extend(role_volumes)
                for volume in role_volumes:
                    docker("volume", "create", volume)
                mounts = [argument for volume, path in zip(role_volumes, ("/config", "/cache", "/data"))
                          for argument in ("-v", volume + ":" + path)]
                if role == "target":
                    docker("run", "--rm", "--user", "0:0", "--entrypoint", "/bin/sh", *mounts,
                           image, "-c", "chown -R 10000:10000 /config /cache /data")
                docker("run", "-d", "--name", name, "-p", "127.0.0.1::8096", *mounts,
                       "-v", f"{media}:/media:ro", image)
                api = FixtureApi(host_port(name))
                api.wait_ready()
                fixture_ids.append(api.setup())
                apis.append(api)
            source, target = apis
            source_admin, source_member, source_item = fixture_ids[0]
            target_admin, target_member, target_item = fixture_ids[1]
            stage = "source-seed"
            desired = [dict(IsFavorite=True, Played=True, PlayCount=3, PlaybackPositionTicks=0),
                       dict(IsFavorite=False, Played=False, PlayCount=1, PlaybackPositionTicks=100_000_000)]
            for user, value in zip((source_admin, source_member), desired):
                source.call("POST", f"/UserItems/{source_item}/UserData?" + urlencode({"userId": user}),
                            dict(value, LastPlayedDate="2026-01-02T03:04:05Z"))
            source_snapshot = dry.capture(dry.Reader(source.url, source.token), "jellyfin")
            target_snapshot = dry.capture(dry.Reader(target.url, target.token), "tesserafin")
            mapping = {"users": {source_admin: target_admin, source_member: target_member}, "paths": []}
            plan = app.prepare(source_snapshot, target_snapshot, mapping)
            dry.require(len(plan["operations"]) == 2, "fixture must yield two distinct operations")
            before = {user: userdata(target_snapshot, user, target_item) for user in (target_admin, target_member)}
            journal = private / "journal"
            stage = "apply"
            for _ in range(2):
                app.execute(app.Api(target.url, target.token), plan, app.digest(plan), journal, quiescent=True)
            after = dry.capture(dry.Reader(target.url, target.token), "tesserafin")
            for user, expected in zip((target_admin, target_member), desired):
                data = userdata(after, user, target_item)
                dry.require(app.state(data) == expected and data.get("LastPlayedDate") == before[user].get("LastPlayedDate"),
                            "applied state/user separation/date preservation mismatch")
            stage = "restart"
            docker("restart", names[1])
            target.url = FixtureApi(host_port(names[1])).url
            target.wait_ready()
            for user, expected in zip((target_admin, target_member), desired):
                observed = app.observe(app.Api(target.url, target.token),
                                       next(op for op in plan["operations"] if op["userId"] == user))
                dry.require(observed["state"] == expected, "state lost after restart")
            stage = "rollback"
            for _ in range(2):
                app.execute(app.Api(target.url, target.token), plan, app.digest(plan), journal,
                            rollback=True, quiescent=True)
            restored = dry.capture(dry.Reader(target.url, target.token), "tesserafin")
            for user in (target_admin, target_member):
                dry.require(userdata(restored, user, target_item) == before[user], "rollback state mismatch")
            stage = "source-unchanged"
            dry.require(dry.capture(dry.Reader(source.url, source.token), "jellyfin") == source_snapshot,
                        "source observation changed")
            return {"schema": 1, "accepted": True, "candidate_source": args.expected_source,
                    "target_image_id": args.target_image, "source_image_id": source_meta["Id"],
                    "source_repo_digest": source_digest, "source_version": "10.10.7", "operations": 2,
                    "apply_restart_rollback": True, "distinct_users": True, "source_unchanged": True,
                    "last_played_date_transferred": False,
                    "scope": "existing-account four-field state; no real player resume or account migration"}
    except Exception as error:
        # Do not expose tokens, auth responses, snapshots, media paths or raw API errors.
        status = getattr(error, "code", None)
        detail = f" (HTTP {status})" if type(status) is int else ""
        raise RuntimeError("artifact adoption rehearsal failed at " + stage + detail) from None
    finally:
        for name in names:
            try:
                docker("rm", "-f", name)
            except Exception:
                pass
        for volume in volumes:
            try:
                docker("volume", "rm", volume)
            except Exception:
                pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target-image", required=True)
    parser.add_argument("--expected-source", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        evidence = run(args)
    except Exception as error:
        # All fixture failures are converted to a bounded stage by run().
        parser.exit(1, str(error) if isinstance(error, RuntimeError) else "artifact adoption rehearsal refused\n")
    dry.write_private(args.output, evidence)
    print(json.dumps(evidence, sort_keys=True))


if __name__ == "__main__":
    main()
