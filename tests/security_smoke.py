"""Isolated HTTP regression suite. Synthetic identities and messages only."""
import base64, hashlib, hmac, json, os, shlex, sqlite3, struct, sys, time
import urllib.request, urllib.error
PASSWORD = "ci-only-password-not-for-production"
USERS = [("ci-legacy", "tenant-alhawbani-001", "Admin"), ("ci-a", "ci-tenant-a", "Admin"),
         ("ci-b", "ci-tenant-b", "Admin"), ("ci-agent", "ci-tenant-a", "Agent")]
if "--environment" in sys.argv:
    for i, (name, tenant, role) in enumerate(USERS):
        salt = os.urandom(16)
        # ASP.NET Identity V3: marker, PRF HMACSHA512, iterations, salt length, salt, subkey
        digest = base64.b64encode(b"\x01" + struct.pack(">III", 2, 100000, len(salt)) + salt +
            hashlib.pbkdf2_hmac("sha512", PASSWORD.encode(), salt, 100000, 32)).decode()
        for key, value in (("Username", name), ("TenantId", tenant), ("Role", role), ("PasswordHash", digest)):
            print("export Security__Accounts__" + str(i) + "__" + key + "=" + shlex.quote(value))
    sys.exit(0)

BASE = "http://127.0.0.1:5080"
DB = os.path.join(os.environ["CHATPLATFORM_DATA_DIR"], "platform.db")
def call(path, method="GET", body=None, cookie=None, expected=200, csrf=True, extra=None):
    headers = {}
    if cookie: headers["Cookie"] = cookie
    if csrf: headers["X-ChatPlatform-CSRF"] = "1"
    if body is not None: headers["Content-Type"] = "application/json"
    if extra: headers.update(extra)
    data = json.dumps(body).encode() if body is not None else None
    try:
        response = urllib.request.urlopen(urllib.request.Request(BASE+path, data=data, headers=headers, method=method), timeout=15)
    except urllib.error.HTTPError as exc:
        response = exc
    with response:
        assert response.status == expected, (path, response.status, expected)
        raw = response.read()
        return (json.loads(raw) if raw and "application/json" in response.headers.get("Content-Type","") else raw.decode(), response.headers)
def login(name):
    _, headers = call("/api/auth/login", "POST", {"username":name,"password":PASSWORD})
    value = headers["Set-Cookie"]
    assert all(x in value.lower() for x in ("secure", "httponly", "samesite=strict"))
    return value.split(";",1)[0]

with sqlite3.connect(DB) as db:
    db.executemany("INSERT INTO Tenants (Id, Name, IsActive, CreatedAtUtc) VALUES (?, ?, 1, '2026-01-01')",
       [("ci-tenant-a","CI A"),("ci-tenant-b","CI B")])
    # Synthetic account has no token. No real outbound request is possible.
    db.execute("INSERT INTO WhatsAppTenants (Id,TenantId,PhoneNumberId,IsActive,CreatedAtUtc,WabaId,AppId,BusinessTokenEncrypted,CreatedAt) VALUES ('ci-account-a','ci-tenant-a','999999999999',1,'2026-01-01','','','','2026-01-01')")
call("/api/conversations",expected=401)
call("/api/live/inbox",expected=401)
call("/api/bots",expected=401)
call("/api/media/00000000-0000-0000-0000-000000000001",expected=401)
call("/api/whatsapp/accounts",expected=401)
call("/api/auth/login","POST",{"username":"ci-a","password":"wrong"},expected=401)
call("/api/auth/login","POST",{"username":"ci-a","password":PASSWORD},expected=403,csrf=False)
legacy, a, b, agent = (login(name) for name,_,_ in USERS)
accounts,_=call("/api/whatsapp/accounts",cookie=legacy)
assert accounts[0]["phoneNumberId"]=="999999999998" and accounts[0]["hasToken"]
with sqlite3.connect(DB) as db:
    row=db.execute("SELECT BusinessTokenEncrypted,TenantId,AppId FROM WhatsAppTenants WHERE Id='ci-legacy-account'").fetchone()
    assert row[0] and row[0]!="ci-only-bootstrap-token" and row[1:]==("tenant-alhawbani-001","")
assert call("/api/auth/me",cookie=a)[0]["tenantId"]=="ci-tenant-a"
assert not call("/api/whatsapp/accounts",cookie=b)[0]
call("/api/live/inbox",cookie=a,expected=403,extra={"Origin":"https://untrusted.example"})
call("/api/bots",cookie=agent,expected=403)
flow={"start":"s","nodes":[{"id":"s","type":"start","next":"c","x":0,"y":0},{"id":"c","type":"contains","text":"hello","next":"r","otherwise":"e","x":180,"y":0},{"id":"r","type":"reply","text":"CI reply","next":"e","x":360,"y":0},{"id":"e","type":"end","x":540,"y":0}]}
assert call("/api/bots/simulate","POST",{"flow":flow,"input":"HELLO"},cookie=a)[0]["replies"]==["CI reply"]
assert call("/api/bots/simulate","POST",{"flow":flow,"input":"no match"},cookie=a)[0]["replies"]==[]
cyclic=json.loads(json.dumps(flow));cyclic["nodes"][2]["next"]="c"
call("/api/bots/simulate","POST",{"flow":cyclic,"input":"hello"},cookie=a,expected=400)
draft,_=call("/api/bots","POST",{"name":"CI draft","flow":flow,"version":0,"tenantId":"ci-tenant-b"},cookie=a,expected=201)
botpath="/api/bots/"+draft["id"]
assert not call("/api/bots",cookie=b)[0]
call(botpath,cookie=b,expected=404)
call(botpath,"PUT",{"name":"Foreign","flow":flow,"version":1},cookie=b,expected=404)
assert call(botpath,"PUT",{"name":"Updated","flow":flow,"version":1},cookie=a)[0]["version"]==2
call(botpath,"PUT",{"name":"Stale","flow":flow,"version":1},cookie=a,expected=409)
assert call(botpath,cookie=a)[0]["name"]=="Updated"
def next_event(response):
    while True:
        line=response.readline().decode().strip()
        if not line and response.closed: raise AssertionError("stream ended")
        if line.startswith("event:"):
            event=line.split(":",1)[1].strip()
            value=response.readline().decode().strip().split(":",1)[1].strip()
            return event,value
def open_live(cookie):
    return urllib.request.urlopen(urllib.request.Request(BASE+"/api/live/inbox",headers={"Cookie":cookie}),timeout=10)
with open_live(a) as ra, open_live(b) as rb:
    ea,va=next_event(ra); eb,vb=next_event(rb)
    assert ea==eb=="inbox"
    with sqlite3.connect(DB) as db:
        db.execute("INSERT INTO CrmContacts(TenantId,Phone,CreatedAtUtc,UpdatedAtUtc) VALUES ('ci-tenant-a','15559990000','2026-01-01','2026-01-01')")
    assert next_event(ra)==("inbox",str(int(va)+1))
    with sqlite3.connect(DB) as db:
        assert db.execute("SELECT Revision FROM InboxRevisions WHERE TenantId='ci-tenant-b'").fetchone() is None or int(vb)==db.execute("SELECT Revision FROM InboxRevisions WHERE TenantId='ci-tenant-b'").fetchone()[0]
        db.execute("DELETE FROM CrmContacts WHERE TenantId='ci-tenant-a' AND Phone='15559990000'")
    print("PASS: draft simulation, tenant isolation, version conflicts and SSE tenant revision")
call("/api/conversations?tenantId=ci-tenant-b",cookie=a,expected=403)
call("/api/events","POST",{},cookie=a,expected=403)
call("/api/messages/send","POST",{"to":"15550000001","text":"CI","phoneNumberId":"999999999998"},cookie=a,expected=404)
call("/api/messages/send","POST",{"to":"15550000001","text":{"body":"CI"},"phoneNumberId":"999999999999"},cookie=b,expected=404)
call("/api/messages/send","POST",{"to":"15550000001","text":"CI","phoneNumberId":"999999999999"},cookie=a,expected=409)
call("/api/messages/send","POST",{"to":"15550000001","text":"CI","tenantId":"ci-tenant-b"},cookie=a,expected=403)
call("/api/messages/send","POST",{"to":"15550000001","text":"CI"},expected=401)
send_template={"to":"15550000001","phoneNumberId":"999999999999","template":{"name":"ci_example","language":{"code":"ar"}}}
call("/api/messages/send-template","POST",send_template,cookie=b,expected=404)
call("/api/messages/send-template","POST",send_template,cookie=a,expected=409)
call("/api/messages/send-template","POST",send_template,expected=401)
call("/api/whatsapp/accounts","POST",{"tenantId":"ci-tenant-b","phoneNumberId":"999999999997","wabaId":"1","accessToken":"fake"},cookie=a,expected=403)
call("/api/whatsapp/accounts","POST",{"tenantId":"ci-tenant-a","phoneNumberId":"999999999998","wabaId":"1","accessToken":"fake"},cookie=a,expected=409)
call("/api/whatsapp/accounts","POST",{"tenantId":"ci-tenant-a","phoneNumberId":"999999999997","wabaId":"1","accessToken":"fake"},cookie=agent,expected=403)
call("/api/whatsapp/accounts/ci-account-a/token","PUT",{"accessToken":"fake"},cookie=b,expected=404)
call("/api/whatsapp/accounts/ci-account-a","DELETE",cookie=b,expected=404)
call("/api/whatsapp/accounts/ci-account-a","DELETE",cookie=agent,expected=403)
call("/api/whatsapp/validate-credentials","POST",{"phoneNumberId":"https://invalid.example","wabaId":"1","accessToken":"fake"},cookie=a)
call("/hub/chat/negotiate?negotiateVersion=1","POST",{},cookie=a,expected=403)
call("/dev/seed/list",expected=404)
call("/dev/seed/init-tenants","POST",expected=404)
assert call("/webhook?hub.mode=subscribe&hub.verify_token=ci-only-test-token&hub.challenge=12345")[0]=="12345"
call("/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=12345",expected=401)
call("/webhook","POST",{},expected=401)
body={"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"999999999999"},"messages":[{"id":"wamid.ci.inbound.1","from":"15550000001","type":"text","text":{"body":"CI inbound message"}}]}}]}]}
sig="sha256="+hmac.new(b"ci-only-test-secret",json.dumps(body).encode(),hashlib.sha256).hexdigest()
for _ in range(2): call("/webhook","POST",body,extra={"X-Hub-Signature-256":sig})
for _ in range(30):
    conversations,_=call("/api/conversations",cookie=a)
    match=next((c for c in conversations if c["contactPhone"]=="15550000001" and c.get("lastMessage")),None)
    if match: break
    time.sleep(1)
else: raise AssertionError("Inbound message not persisted")
assert match["lastMessage"]["text"]=="CI inbound message"
path="/api/conversations/"+match["id"]+"/messages"
messages,_=call(path,cookie=a)
assert sum(m.get("wamid")=="wamid.ci.inbound.1" for m in messages)==1
call(path,cookie=b,expected=404)
# Synthetic inbound image; no network calls because account has no token.
image_body={"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"999999999999"},"messages":[{"id":"wamid.ci.image.1","from":"15550000001","type":"image","image":{"id":"123456789","mime_type":"image/png"}}]}}]}]}
image_sig="sha256="+hmac.new(b"ci-only-test-secret",json.dumps(image_body).encode(),hashlib.sha256).hexdigest()
call("/webhook","POST",image_body,extra={"X-Hub-Signature-256":image_sig})
for _ in range(30):
    image=next((m for m in call(path,cookie=a)[0] if m.get("wamid")=="wamid.ci.image.1"),None)
    if image: break
    time.sleep(1)
else: raise AssertionError("Inbound image not persisted")
call("/api/media/"+image["id"],cookie=b,expected=404)
call("/api/media/"+image["id"],cookie=a,expected=409)
call("/api/messages/send-media","POST",{"conversationId":match["id"],"ticket":"forged"},cookie=a,expected=400)
call("/api/messages/send-media","POST",{"conversationId":match["id"],"ticket":"forged"},expected=401)
assert not call("/api/conversations",cookie=b)[0]
rule={"name":"CI rule","matchType":"any","replyText":"CI only","isActive":False,"tenantId":"ci-tenant-b"}
call("/api/autoreply","POST",rule,cookie=agent,expected=403)
call("/api/autoreply","POST",rule,cookie=a,expected=403,csrf=False)
created,_=call("/api/autoreply","POST",rule,cookie=a)
assert created["tenantId"]=="ci-tenant-a"
assert not call("/api/autoreply",cookie=b)[0]
call("/api/autoreply/"+created["id"],"DELETE",cookie=b,expected=404)
call("/api/autoreply/"+created["id"],"DELETE",cookie=a)
call("/api/templates?tenantId=ci-tenant-a",cookie=b,expected=403)
template={"tenantId":"ci-tenant-a","whatsAppTenantId":"ci-account-a","name":"ci_local","category":"UTILITY","language":"ar","body":"CI only"}
call("/api/templates","POST",template,cookie=agent,expected=403)
call("/api/templates","POST",template,cookie=a,expected=403,extra={"Origin":"https://untrusted.example"})
created_template,_=call("/api/templates","POST",template,cookie=a,expected=201)
template_path="/api/templates/"+created_template["template"]["id"]
assert call(template_path+"?tenantId=ci-tenant-a",cookie=a)[0]["tenantId"]=="ci-tenant-a"
call(template_path+"?tenantId=ci-tenant-b",cookie=b,expected=404)
call(template_path+"?tenantId=ci-tenant-b","DELETE",cookie=b,expected=404)
call(template_path+"?tenantId=ci-tenant-a","DELETE",cookie=agent,expected=403)
call(template_path+"?tenantId=ci-tenant-a","DELETE",cookie=a)
# Team administration: configured admins stay operator-managed; tenant admins can provision agents only.
agent_body={"username":"ci-managed","displayName":"CI Managed","password":PASSWORD,"role":"Admin","tenantId":"ci-tenant-b"}
call("/api/team",cookie=agent,expected=403)
call("/api/team","POST",agent_body,cookie=agent,expected=403)
call("/api/team","POST",dict(agent_body,username="ci-a"),cookie=a,expected=409)
managed,_=call("/api/team","POST",agent_body,cookie=a,expected=201)
assert managed["role"]=="Agent" and "passwordHash" not in managed
assert [x["username"] for x in call("/api/team",cookie=a)[0]]==["ci-managed"]
assert not call("/api/team",cookie=b)[0]
call("/api/team/ci-managed/password","PUT",{"password":"different-ci-password"},cookie=b,expected=404)
call("/api/team/ci-managed/active","PUT",{"isActive":False},cookie=b,expected=404)
managed_cookie=login("ci-managed")
identity,_=call("/api/auth/me",cookie=managed_cookie)
assert identity["tenantId"]=="ci-tenant-a" and identity["role"]=="Agent"
call("/api/team",cookie=managed_cookie,expected=403)
# CRM profiles are automatically created from incoming conversations, and isolated by tenant.
contacts,_=call("/api/contacts",cookie=a)
assert contacts["total"]==1 and contacts["items"][0]["phone"]=="15550000001"
assert call("/api/contacts",cookie=b)[0]["total"]==0
call("/api/contacts/15550000001",cookie=b,expected=404)
contact={"phone":"15550000001","displayName":"CI Lead","email":"ci@example.invalid","notes":"private CI note","tags":"vip","stage":"lead"}
call("/api/contacts/15550000001","PUT",contact,cookie=b,expected=404)
call("/api/contacts/15550000001","PUT",contact,cookie=managed_cookie)
assert call("/api/contacts?search=CI&stage=lead",cookie=a)[0]["items"][0]["displayName"]=="CI Lead"
call("/api/contacts","POST",contact,cookie=a,expected=409)
other,_=call("/api/contacts","POST",dict(contact,displayName="Other tenant"),cookie=b,expected=201)
assert other["tenantId"]=="ci-tenant-b"
assert call("/api/contacts/15550000001",cookie=a)[0]["displayName"]=="CI Lead"
cid=match["id"]
root="/api/conversations/"+cid
call(root+"/assign","PUT",{"username":"ci-managed"},cookie=managed_cookie,expected=403)
call(root+"/assign","PUT",{"username":"ci-managed"},cookie=b,expected=404)
call(root+"/assign","PUT",{"username":"ci-b"},cookie=a,expected=400)
call(root+"/assign","PUT",{"username":"ci-managed"},cookie=a,expected=204)
assert call("/api/conversations?assigned=me",cookie=managed_cookie)[0][0]["id"]==cid
assert not call("/api/conversations?assigned=unassigned",cookie=a)[0]
call(root+"/status","PUT",{"status":"closed"},cookie=b,expected=404)
call(root+"/tags","PUT",{"tags":"priority"},cookie=b,expected=404)
call(root+"/read","PUT",{},cookie=b,expected=404)
call(root+"/status","PUT",{"status":"closed"},cookie=managed_cookie,expected=204)
call(root+"/read","PUT",{},cookie=managed_cookie,expected=204)
call(root+"/tags","PUT",{"tags":"priority"},cookie=managed_cookie,expected=204)
closed,_=call("/api/conversations?status=closed&search=CI",cookie=a)
assert len(closed)==1 and closed[0]["unreadCount"]==0 and closed[0]["tags"]=="priority"
assert not call("/api/conversations?status=open",cookie=a)[0]
call(root+"/status","PUT",{"status":"open"},cookie=a,expected=204)
print("PASS: CRM import, same-phone tenant isolation, assignment, filters, status and unread updates")
call("/api/team/ci-managed/password","PUT",{"password":"different-ci-password"},cookie=a,expected=204)
call("/api/auth/me",cookie=managed_cookie,expected=401)
call("/api/auth/login","POST",{"username":"ci-managed","password":PASSWORD},expected=401)
_, managed_headers=call("/api/auth/login","POST",{"username":"ci-managed","password":"different-ci-password"})
managed_cookie=managed_headers["Set-Cookie"].split(";",1)[0]
call("/api/team/ci-managed/active","PUT",{"isActive":False},cookie=a,expected=204)
call("/api/auth/me",cookie=managed_cookie,expected=401)
call("/api/team/ci-managed/active","PUT",{"isActive":True},cookie=a,expected=204)
call("/api/auth/me",cookie=managed_cookie,expected=401)
with sqlite3.connect(DB) as db:
    row=db.execute("SELECT PasswordHash,TenantId FROM ManagedAgents WHERE Username='ci-managed'").fetchone()
    assert row[0]!="different-ci-password" and row[1]=="ci-tenant-a"
    db.execute("UPDATE Tenants SET IsActive=0 WHERE Id='ci-tenant-b'")
call("/api/auth/me",cookie=b,expected=401)
call("/api/auth/logout","POST",cookie=a,expected=204)
call("/api/auth/me",cookie=a,expected=401)
call("/api/auth/me",expected=401)
print("PASS: authenticated reads, cookie flags, role checks, CSRF, tenant isolation, signed inbound and deduplication")
