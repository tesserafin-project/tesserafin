"""#374 decision/descriptor/bytes matrix. usage: PCR_BASE=.. PCR_STATE=<statedir> matrix374.py <out.json>"""
import sys, os, json, re, time, glob, tempfile, urllib.parse
from pcrlib import *
ST=os.environ["PCR_STATE"]; adm=seed(os.path.join(os.environ["PCR_ROOT"],"media/movies"))
http("POST","/Library/Refresh",tok=adm)
for _ in range(40):
    ids=items(adm)
    if "Remux Mkv (2023)" in ids: break
    time.sleep(2)
time.sleep(3); t,u=login("alice","pcr-m"); D="pcr-m"; tmp=tempfile.mkdtemp(dir=ST)
NARROW=["mp4","webm"]
S=[("direct-play","Direct Mp4 (2023)",caps(),cons()),
   ("remux-mkv","Remux Mkv (2023)",caps(NARROW),cons()),
   ("remux-mkv-no-transcode","Remux Mkv (2023)",caps(NARROW),cons(AllowTranscoding=False)),
   ("remux-mkv-no-output","Remux Mkv (2023)",caps(NARROW,output=False),cons()),
   ("remux-mkv-nothing-producible","Remux Mkv (2023)",caps(["webm"],output=False),cons()),
   ("avi-issue-374","Remux Avi (2023)",caps(),cons()),
   ("avi-no-transcode","Remux Avi (2023)",caps(),cons(AllowTranscoding=False)),
   ("transcode-codec","Transcode Mpeg2 (2023)",caps(),cons()),
   ("transcode-forbidden","Transcode Mpeg2 (2023)",caps(),cons(AllowTranscoding=False))]
rows_master=[]
def seg(url):
    c,m=http("GET",url,dev=D); assert c==200,(c,m)
    rows_master.append(next((l for l in m.decode().splitlines() if l.startswith("#EXT-X-STREAM-INF")),None))
    v=[l for l in m.decode().splitlines() if l and not l.startswith("#")][0]; v=urllib.parse.urljoin(url,v)
    for _ in range(90):
        c,m=http("GET",v,dev=D,timeout=120)
        if c==200: break
        time.sleep(1)
    s=[l for l in m.decode().splitlines() if l and not l.startswith("#")][0]; s=urllib.parse.urljoin(v,s)
    c,b,h=http("GET",s,raw=True,dev=D,timeout=120); return c,b
rows=[]
for name,item,c_,k in S:
    before=set(glob.glob(ST+"/log/FFmpeg*")); c,s=plan(ids[item],u,t,D,c=c_,k=k); r={"name":name,"create":c}
    if c==200:
        r.update({x:s[x] for x in("Method","Reasons","Transforms")})
        c,d=http("GET",f"/Playback/Sessions/{s['Id']}/Stream",tok=t,dev=D); url=urllib.parse.urljoin(BASE,d["Url"])
        r.update(stream=c,Protocol=d["Protocol"],Container=d["Container"],Static="Static=true" in d["Url"],ServedBy=d["ServedBy"],Fallback=d["FallbackReason"])
        p=os.path.join(tmp,name+".bin")
        if d["Protocol"]=="Hls":
            cc,b=seg(url)
            r["master"]=rows_master[-1]
        else:
            cc,b,_=http("GET",url,raw=True,rng="bytes=0-4194303",dev=D)
        open(p,"wb").write(b); r["bytes"]={"http":cc,"len":len(b),**ffprobe(p)}
        dim=subprocess.run(["ffprobe","-v","error","-select_streams","v","-show_entries","stream=width,height","-of","csv=p=0",p],capture_output=True,text=True).stdout.strip(); r["bytes"]["size"]=dim
        time.sleep(1)
        new=sorted(set(glob.glob(ST+"/log/FFmpeg*"))-before)
        if new:
            a=open(new[-1],errors="replace").read(); r["ffmpeg"]=dict(re.findall(r"-codec:([av]):0 (\S+)",a))
        http("DELETE",f"/Playback/Sessions/{s['Id']}",tok=t,dev=D)
    else: r["body"]=s if isinstance(s,dict) else str(s)
    rows.append(r); print(json.dumps(r,ensure_ascii=False))
json.dump(rows,open(sys.argv[1],"w"),indent=1)
