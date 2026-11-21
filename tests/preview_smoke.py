"""Disposable Preview environment, no actual credentials or external traffic."""
import sys, os, secrets, hashlib, base64, struct, subprocess, tempfile, time, urllib.request, urllib.error, json
password=secrets.token_urlsafe(24)
salt=os.urandom(16)
hash_value=base64.b64encode(b"\x01"+struct.pack(">III",2,100000,len(salt))+salt+hashlib.pbkdf2_hmac("sha512",password.encode(),salt,100000,32)).decode()
env=dict(os.environ,ASPNETCORE_ENVIRONMENT="Preview",ASPNETCORE_URLS="http://127.0.0.1:5081",Preview__SmokePasswordHash=hash_value)
env.pop("FLY_APP_NAME",None)
base="http://127.0.0.1:5081"
cookie=None
def call(path,method="GET",body=None,expected=200):
    h={"X-ChatPlatform-CSRF":"1"}
    if cookie:h["Cookie"]=cookie
    if body is not None:h["Content-Type"]="application/json"
    try:r=urllib.request.urlopen(urllib.request.Request(base+path,method=method,headers=h,data=json.dumps(body).encode() if body is not None else None),timeout=10)
    except urllib.error.HTTPError as ex:r=ex
    with r:
        data=r.read()
        assert r.status==expected,(path,r.status,expected,data[:300])
        return (json.loads(data) if data and "application/json" in r.headers.get("Content-Type","") else data,r.headers)
subprocess.run(["dotnet","build","ChatPlatform.csproj","-c","Release"],check=True)
with tempfile.TemporaryFile() as log:
    p=subprocess.Popen(["dotnet","bin/Release/net8.0/ChatPlatform.dll"],env=env,stdout=log,stderr=subprocess.STDOUT)
    try:
        for _ in range(60):
            if p.poll() is not None:raise RuntimeError("Preview process exited")
            try:call("/health");break
            except (urllib.error.URLError,AssertionError,TimeoutError):time.sleep(1)
        else:raise RuntimeError("Preview did not become healthy")
        _,headers=call("/api/auth/login","POST",{"username":"preview-admin","password":password})
        cookie=headers["Set-Cookie"].split(";",1)[0]
        assert call("/api/auth/me")[0]["tenantId"]=="preview-tenant"
        assert call("/api/preview")[0]["enabled"]
        rows=call("/api/conversations")[0]
        assert len(rows)==5
        c=rows[0]
        call("/api/preview/inbound","POST",{"conversationId":c["id"],"text":"Preview inbound smoke"},202)
        for _ in range(20):
            current=call("/api/conversations")[0]
            if any(r.get("lastMessage",{}).get("text")=="Preview inbound smoke" for r in current):break
            time.sleep(1)
        else:raise RuntimeError("Simulated incoming not processed")
        sent,_=call("/api/messages/send-text","POST",{"to":c["contactPhone"],"phoneNumberId":"999999999991","text":"Preview outgoing smoke"},202)
        import sqlite3
        for _ in range(30):
            with sqlite3.connect("/tmp/chatplatform-preview/platform.db") as db:
                row=db.execute("SELECT Status,Wamid FROM WhatsAppMessages WHERE lower(Id)=?",(sent["id"].lower(),)).fetchone()
                if row and row[0]=="Sent" and row[1].startswith("preview."):break
            time.sleep(1)
        else:raise RuntimeError("Simulated outgoing not processed")
        call("/webhook","POST",{},404)
        assert not call("/api/knowledge/model")[0]["configured"]
        assert not call("/api/marketing/options")[0]["enabled"]
        call("/api/platform-admin/tenants",expected=403)
        print("PASS: real preview startup, five synthetic conversations, login, simulated inbound/outbound, disabled external providers")
    finally:
        p.terminate()
        try:p.wait(timeout=10)
        except subprocess.TimeoutExpired:p.kill();p.wait()
        log.seek(0)
        if sys.exc_info()[0] is not None:print(log.read().decode(errors="replace")[-12000:])
