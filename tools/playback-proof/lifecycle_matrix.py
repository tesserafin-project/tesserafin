"""#301/#305 lifecycle matrix on a real server with real ffmpeg. PCR_BASE, PCR_STATE env."""
import json, os, re, secrets, signal, subprocess, sys, threading, time, urllib.parse
from pcrlib import *
ST=os.environ["PCR_STATE"]; adm=seed(os.path.join(os.environ["PCR_ROOT"],"media/movies")); ids=items(adm)
T="Transcode Mpeg2 (2023)"; U2="Second Mpeg2 (2023)"
A=dict(zip(("tok","uid"),login("alice","pcr-a1"))); A["dev"]="pcr-a1"
A2=dict(zip(("tok","uid"),login("alice","pcr-a2"))); A2["dev"]="pcr-a2"
B=dict(zip(("tok","uid"),login("bob","pcr-b1"))); B["dev"]="pcr-b1"
def procs(name=None):
    o=subprocess.run(["ps","-eo","pid,args"],capture_output=True,text=True).stdout.splitlines()
    r=[]
    for l in o:
        if "ffmpeg" in l and " -i " in l and os.path.basename(ST)+"/" in l and (name is None or name in l): r.append(int(l.split()[0]))
    return r
def clean():
    for p in procs():
        try: os.kill(p,signal.SIGKILL)
        except OSError: pass
    time.sleep(.5)
def wait_up(name,n=1,t=20):
    e=time.time()+t
    while time.time()<e:
        if len(procs(name))>=n: return True
        time.sleep(.2)
    return False
def wait_gone(pids,t=90):
    t0=time.time()
    while time.time()-t0<t:
        alive=[p for p in pids if os.path.exists(f"/proc/{p}") and "ffmpeg" in open(f"/proc/{p}/cmdline","rb").read().decode(errors="replace")]
        if not alive: return round((time.time()-t0)*1000)
        time.sleep(.05)
    return None
def open_session(o,item):
    c,s=plan(ids[item],o["uid"],o["tok"],o["dev"]); assert c==200,(c,s)
    c,d=http("GET",f"/Playback/Sessions/{s['Id']}/Stream",tok=o["tok"],dev=o["dev"]); assert c==200,d
    url=d["Url"]; psid=urllib.parse.parse_qs(urllib.parse.urlsplit(url).query)["PlaySessionId"][0]
    return dict(sid=s["Id"],url=re.sub(r"[&?](api_?key)=[^&]+","",url,flags=re.I),psid=psid,item=item,owner=o)
def mint(o,sess):
    c,r=http("POST","/Playback/Capabilities",{"PlaySessionId":sess["psid"],"ItemId":ids[sess["item"]],"MediaSourceId":ids[sess["item"]].replace("-",""),"Scopes":["Media"]},tok=o["tok"],dev=o["dev"])
    assert c==200,(c,r); return r["Value"] if "Value" in r else r["Capability"]
def fetch(sess,tok=None,dev="pcr-anon",cap=None):
    u=sess["url"]+(f"&playbackCapability={cap}" if cap else "")
    c,m=http("GET",u,tok=tok,dev=dev); 
    if c!=200: return c
    v=urllib.parse.urljoin(BASE+sess["url"],[l for l in m.decode().splitlines() if l and not l.startswith("#")][0])
    if cap and "playbackCapability" not in v: v+=f"&playbackCapability={cap}"
    c,m=http("GET",v,tok=tok,dev=dev,timeout=45)
    if c!=200: return c
    g=urllib.parse.urljoin(v,[l for l in m.decode().splitlines() if l and not l.startswith("#")][0])
    if cap and "playbackCapability" not in g: g+=f"&playbackCapability={cap}"
    c,_,_h=http("GET",g,tok=tok,dev=dev,timeout=60,raw=True); return c
def active(o,psid): return http("DELETE",f"/Videos/ActiveEncodings?deviceId={o['dev']}&playSessionId={psid}",tok=o["tok"],dev=o["dev"])[0]
def stopped(o,s): return http("POST","/Sessions/Playing/Stopped",{"ItemId":ids[s["item"]],"PlaySessionId":s["psid"],"PositionTicks":100000000,"MediaSourceId":ids[s["item"]].replace("-","")},tok=o["tok"],dev=o["dev"])[0]
def sdelete(o,s): return http("DELETE",f"/Playback/Sessions/{s['sid']}",tok=o["tok"],dev=o["dev"])[0]
R=[]
def row(name,**k): k={"row":name,**k}; R.append(k); print(json.dumps(k),flush=True)
def stop_row(name,start,stopfn,item=T):
    clean(); s=open_session(A,item); fc=start(s); up=wait_up(item); p=procs(item); t0=time.time(); code=stopfn(s); ms=wait_gone(p,t=70) if up else None
    row(name,start=fc,job=up,stop=code,stop_call_to_gone_ms=None if ms is None else round((time.time()-t0)*1000)); clean(); return s
# credentials × stop
for nm,st in (("bearer",lambda s:fetch(s,A["tok"],A["dev"])),("capability",lambda s:fetch(s,cap=mint(A,s))),("coherent",lambda s:fetch(s,A["tok"],A["dev"],cap=mint(A,s)))):
    stop_row(f"{nm}: owner ActiveEncodings",st,lambda s:active(A,s["psid"]))
    stop_row(f"{nm}: owner session DELETE",st,lambda s:sdelete(A,s))
    stop_row(f"{nm}: owner Stopped report",st,lambda s:stopped(A,s))
# #301 contradiction
clean(); s=open_session(A,T); cap=mint(A,s); c=fetch(s,B["tok"],B["dev"],cap); up=wait_up(T,t=12); p=procs(T)
row("contradictory bob token + alice capability",start=c,job=up)
if up:
    b=active(B,s["psid"]); time.sleep(2); a=active(A,s["psid"]); ms=wait_gone(p,t=4); row(" hybrid job: bob then alice stop",bob=b,alice=a,gone_ms_within_4s=ms); clean()
# same user, other device + capability of first
clean(); s=open_session(A,T); cap=mint(A,s); c=fetch(s,A2["tok"],A2["dev"],cap); up=wait_up(T,t=15); p=procs(T); a2=active(A2,s["psid"]); time.sleep(1.5); after_a2=len(procs(T)); a1=active(A,s["psid"]); ms=wait_gone(p,t=6)
row("same user other device + capability: owner is capability device",start=c,job=up,other_device_stop=a2,alive_after_other_device=after_a2,minting_device_stop=a1,gone_ms=ms); clean()
# unauthorized cannot stop
clean(); s=open_session(A,T); fetch(s,A["tok"],A["dev"]); wait_up(T); p=procs(T)
b=active(B,s["psid"]); bd=sdelete(B,s); bs=stopped(B,s); an=http("DELETE",f"/Videos/ActiveEncodings?deviceId=x&playSessionId={s['psid']}")[0]; time.sleep(2)
row("unauthorized stop attempts",bob_active=b,bob_session_delete=bd,bob_stopped=bs,anon=an,job_alive=len(procs(T))==len(p) and bool(p)); active(A,s["psid"]); clean()
# repeats and concurrency
clean(); s=open_session(A,T); fetch(s,A["tok"],A["dev"]); wait_up(T); p=procs(T); codes=[]; th=[threading.Thread(target=lambda:codes.append(sdelete(A,s))) for _ in range(4)]+[threading.Thread(target=lambda:codes.append(active(A,s["psid"]))) for _ in range(4)]
[t.start() for t in th];[t.join() for t in th]; ms=wait_gone(p,t=20); row("8 concurrent stops",codes=sorted(codes),gone_ms=ms,repeat_active=active(A,s["psid"]),repeat_delete=sdelete(A,s)); clean()
# isolation
clean(); s1=open_session(A,T); s2=open_session(A2,U2); fetch(s1,A["tok"],A["dev"]); fetch(s2,A2["tok"],A2["dev"]); wait_up(T); wait_up(U2); p=procs(T); c=sdelete(A,s1); ms=wait_gone(p,t=10); row("two devices same user: stop one",stop=c,gone_ms=ms,other_alive=bool(procs(U2)),other_segment=fetch(s2,A2["tok"],A2["dev"])); sdelete(A2,s2); clean()
s1=open_session(A,T); s2=open_session(B,U2); fetch(s1,A["tok"],A["dev"]); fetch(s2,B["tok"],B["dev"]); wait_up(T); wait_up(U2); p=procs(T); c=sdelete(A,s1); ms=wait_gone(p,t=10); row("two users: stop one",stop=c,gone_ms=ms,other_alive=bool(procs(U2))); sdelete(B,s2); clean()
# same play session id, other user's job survives
s1=open_session(A,T); s2=open_session(B,U2); s2b=dict(s2); s2b["url"]=s2["url"].replace(s2["psid"],s1["psid"]); fetch(s1,A["tok"],A["dev"]); fetch(s2b,B["tok"],B["dev"]); wait_up(T); wait_up(U2); p=procs(T); c=sdelete(A,s1); ms=wait_gone(p,t=10); row("shared play session id: bob job survives alice stop",stop=c,gone_ms=ms,other_alive=bool(procs(U2))); active(B,s1["psid"]); clean()
# old links
clean(); s=open_session(A,T); cap=mint(A,s); fetch(s,cap=cap); wait_up(T); sdelete(A,s); time.sleep(1); row("capability link replay after session DELETE",status=fetch(s,cap=cap),job=wait_up(T,t=4)); clean()
s=open_session(A,T); cap=mint(A,s); fetch(s,cap=cap); wait_up(T); stopped(A,s); time.sleep(1); row("capability link replay after Stopped",status=fetch(s,cap=cap),job=wait_up(T,t=4)); clean()
s=open_session(A,T); fetch(s,A["tok"],A["dev"]); wait_up(T); sdelete(A,s); time.sleep(1); row("owner bearer link replay after DELETE (new play with durable token)",status=fetch(s,A["tok"],A["dev"]),job=wait_up(T,t=8)); active(A,s["psid"]); clean()
# seek, restart, retry
s=open_session(A,T); fetch(s,A["tok"],A["dev"]); wait_up(T); p=procs(T)
c,m=http("GET",s["url"],tok=A["tok"],dev=A["dev"]); v=urllib.parse.urljoin(BASE+s["url"],[l for l in m.decode().splitlines() if l and not l.startswith("#")][0]); c,m=http("GET",v,tok=A["tok"],dev=A["dev"]); segs=[l for l in m.decode().splitlines() if l and not l.startswith("#")]
c,b,_=http("GET",urllib.parse.urljoin(v,segs[100]),tok=A["tok"],dev=A["dev"],raw=True,timeout=60); sp=os.path.join(ST,"seek.ts"); open(sp,"wb").write(b)
st=subprocess.run(["ffprobe","-v","error","-show_entries","format=start_time","-of","csv=p=0",sp],capture_output=True,text=True).stdout.strip(); row("seek: segment 100 of the playlist",status=c,bytes=len(b),start_time_s=st,probe=ffprobe(sp),restarted_with_ss=any(" -ss " in open(f"/proc/{x}/cmdline","rb").read().replace(b"\0",b" ").decode(errors="replace") for x in procs(T))); sdelete(A,s); wait_gone(procs(T),t=10)
n=open_session(A,T); fc=fetch(n,A["tok"],A["dev"]); up=wait_up(T); row("new play after stop",fetch=fc,job=up,no_leftover_from_previous=len(procs(T))==1); sdelete(A,n); clean()
open(os.path.join(os.environ["PCR_ROOT"],"FAILNEXT"),"w").close(); s=open_session(A,T); f1=fetch(s,A["tok"],A["dev"]); time.sleep(1); left=os.path.exists(os.path.join(os.environ["PCR_ROOT"],"FAILNEXT")); s2=open_session(A,T); f2=fetch(s2,A["tok"],A["dev"]); up=wait_up(T); row("encoder failure then retry",first=f1,flag_consumed=not left,retry=f2,job=up); sdelete(A,s2); clean()
# client loss
s=open_session(A,T); fetch(s,A["tok"],A["dev"]); wait_up(T); p=procs(T); ms=wait_gone(p,t=100); row("client lost, no stop, no ping",gone_ms=ms); clean()
# temp files
tr=ST+"/cache/transcodes"; time.sleep(3); s=open_session(A,T); fetch(s,A["tok"],A["dev"]); wait_up(T); time.sleep(3); n1=len(os.listdir(tr)); sdelete(A,s); time.sleep(5); row("transcode files after stop",during=n1,after=len(os.listdir(tr)))
json.dump(R,open(sys.argv[1],"w"),indent=1)
