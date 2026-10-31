"""Shared helpers of the PLAYBACK-CORE-R1 rig. Disposable local server only; tokens never printed."""
import json, os, secrets, subprocess, time, urllib.parse, urllib.request, urllib.error
BASE = os.environ.get("PCR_BASE", "http://127.0.0.1:18196")
PW = "pcr-" + "rig-password"
def hdr(dev, tok=None):
    return f'MediaBrowser Client="pcr", Device="{dev}", DeviceId="{dev}", Version="1"' + (f', Token="{tok}"' if tok else "")
def http(m, p, b=None, tok=None, dev="pcr-admin", raw=False, timeout=60, rng=None):
    url = p if p.startswith("http") else BASE + p
    q = urllib.request.Request(url, data=json.dumps(b).encode() if b is not None else None, method=m)
    q.add_header("Authorization", hdr(dev, tok)); 
    if b is not None: q.add_header("Content-Type", "application/json")
    if rng: q.add_header("Range", rng)
    try:
        with urllib.request.urlopen(q, timeout=timeout) as x:
            d = x.read()
            if raw: return x.status, d, dict(x.headers)
            return x.status, (json.loads(d) if d and "json" in x.headers.get("Content-Type","") else d)
    except urllib.error.HTTPError as e:
        d = e.read()
        if raw: return e.code, d, dict(e.headers)
        try: return e.code, json.loads(d)
        except Exception: return e.code, d[:300]
def wait():
    for _ in range(120):
        try:
            if http("GET", "/System/Info/Public")[0] == 200: return
        except Exception: pass
        time.sleep(1)
    raise SystemExit("server not ready")
def login(u, dev): 
    c, a = http("POST", "/Users/AuthenticateByName", {"Username": u, "Pw": PW}, dev=dev)
    assert c == 200, (c, a)
    return a["AccessToken"], a["User"]["Id"]
def seed(media):
    wait()
    if not http("GET", "/System/Info/Public")[1].get("StartupWizardCompleted"):
        for _ in range(60):
            if http("GET", "/Startup/User")[0] == 200: break
            time.sleep(1)
        http("POST", "/Startup/User", {"Name": "rigadmin", "Password": PW})
        http("POST", "/Startup/Complete")
    t, _ = login("rigadmin", "pcr-admin")
    if not http("GET", "/Library/VirtualFolders", tok=t)[1]:
        q = urllib.parse.urlencode({"name": "Movies", "collectionType": "movies", "paths": media, "refreshLibrary": "true"})
        print("lib", http("POST", f"/Library/VirtualFolders?{q}", {"LibraryOptions": {}}, tok=t)[0])
    names = {u["Name"] for u in http("GET", "/Users", tok=t)[1]}
    for n in ("alice", "bob"):
        if n not in names: http("POST", "/Users/New", {"Name": n, "Password": PW}, tok=t)
    for _ in range(90):
        r = http("GET", "/Items?recursive=true&includeItemTypes=Movie", tok=t)[1]
        if isinstance(r, dict) and r["TotalRecordCount"] >= 5 and all(probed(t, i["Id"]) for i in r["Items"]): break
        time.sleep(2)
    return t
def probed(t, item):
    """A scanned item plans wrongly until its streams are probed: visible is not ready."""
    uid = http("GET","/Users/Me",tok=t)[1]["Id"]
    d = http("GET", f"/Items/{item}?userId={uid}", tok=t)[1]
    return any(s.get("Type")=="Video" and s.get("Codec") for src in d.get("MediaSources") or [] for s in src.get("MediaStreams") or [])
def items(t):
    uid = http("GET","/Users/Me",tok=t)[1]["Id"]
    r = http("GET", f"/Items?recursive=true&includeItemTypes=Movie&userId={uid}", tok=t)[1]
    return {i["Name"]: i["Id"] for i in r["Items"]}
DECLARED = ["mp4","m4v","mov","mkv","webm"]
def caps(containers=DECLARED, vc=("h264","hevc","vp8","vp9"), ac=("aac","mp3","opus","vorbis","flac"), output=True):
    return {"Decode":{"AudioCodecs":[{"Codec":c} for c in ac],"DirectPlayProfiles":[{"AudioCodecs":list(ac),"Containers":list(containers),"VideoCodecs":list(vc),"Type":"Video"}],"SubtitleDelivery":[],"VideoCodecs":[{"Codec":c,"Profiles":[],"VideoRangeTypes":["SDR"]} for c in vc],"SupportsDash":False,"SupportsHls":True},
      "OutputProfiles":[{"AudioCodecs":["aac"],"Container":"ts","VideoCodecs":["h264"],"Protocol":"Hls","Type":"Video"}] if output else []}
def cons(**k):
    d={"PreferredSubtitleLanguages":[],"AllowAudioStreamCopy":True,"AllowDirectPlay":True,"AllowDirectStream":True,"AllowTranscoding":True,"AllowVideoStreamCopy":True,"AlwaysBurnInSubtitleWhenTranscoding":False,"StartTimeTicks":0,"SubtitleMode":"None"}; d.update(k); return d
def plan(item, uid, tok, dev, c=None, k=None):
    return http("POST","/Playback/Sessions",{"Capabilities":c or caps(),"Constraints":k or cons(),"ItemId":item,"PlaySessionId":secrets.token_hex(16),"PlaybackAttemptId":secrets.token_hex(16),"UserId":uid},tok=tok,dev=dev)
def scrub(u):
    return urllib.parse.urlunsplit((lambda s:(s.scheme,s.netloc,s.path,"&".join(x for x in s.query.split("&") if not x.lower().startswith(("apikey=","api_key=","playbackcapability="))),""))(urllib.parse.urlsplit(u)))
def ffprobe(path_or_url):
    o=subprocess.run(["ffprobe","-v","error","-show_entries","format=format_name:stream=codec_type,codec_name","-of","json",path_or_url],capture_output=True,text=True,timeout=120)
    if o.returncode: return {"error":o.stderr.strip()[:200]}
    j=json.loads(o.stdout); return {"format":j["format"]["format_name"],"codecs":sorted(s["codec_name"] for s in j["streams"])}
