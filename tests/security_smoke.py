"""Isolated HTTP regression suite. Synthetic identities and messages only."""
import base64, hashlib, hmac, json, os, shlex, sqlite3, struct, sys, time
import urllib.request, urllib.error
PASSWORD = "ci-only-password-not-for-production"
USERS = [("ci-legacy", "tenant-alhawbani-001", "Admin"), ("ci-a", "ci-tenant-a", "Admin"),
         ("ci-b", "ci-tenant-b", "Admin"), ("ci-agent", "ci-tenant-a", "Agent"), ("ci-owner","__platform_owner__","SuperAdmin")]
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
call("/api/whatsapp/accounts/ci-account-a/readiness","POST",expected=401)
call("/api/auth/login","POST",{"username":"ci-a","password":"wrong"},expected=401)
call("/api/auth/login","POST",{"username":"ci-a","password":PASSWORD},expected=403,csrf=False)
legacy, a, b, agent, owner = (login(name) for name,_,_ in USERS)
call("/api/whatsapp/accounts/ci-account-a/readiness","POST",cookie=agent,expected=403)
call("/api/whatsapp/accounts/ci-account-a/readiness","POST",cookie=b,expected=404)
call("/api/whatsapp/accounts/ci-account-a/readiness","POST",cookie=a,expected=409)
call("/api/whatsapp/embedded-signup/start","POST",expected=401)
call("/api/whatsapp/embedded-signup/start","POST",cookie=agent,expected=403)
call("/api/whatsapp/embedded-signup/start","POST",cookie=a,expected=409)
call("/api/whatsapp/embedded-signup/complete","POST",{"state":"fake","code":"fake","wabaId":"789","phoneNumberId":"123456"},cookie=a,expected=409)

accounts,_=call("/api/whatsapp/accounts",cookie=legacy)
assert accounts[0]["phoneNumberId"]=="999999999998" and accounts[0]["hasToken"]
with sqlite3.connect(DB) as db:
    row=db.execute("SELECT BusinessTokenEncrypted,TenantId,AppId FROM WhatsAppTenants WHERE Id='ci-legacy-account'").fetchone()
    assert row[0] and row[0]!="ci-only-bootstrap-token" and row[1:]==("tenant-alhawbani-001","")
assert call("/api/auth/me",cookie=a)[0]["tenantId"]=="ci-tenant-a"
assert call("/api/auth/me",cookie=owner)[0]["role"]=="SuperAdmin"
call("/api/platform-admin/plans",expected=401)
call("/api/platform-admin/plans",cookie=a,expected=403)
call("/api/platform-admin/plans",cookie=agent,expected=403)
for path in ["/api/conversations","/api/dashboard","/api/team","/api/subscription","/api/knowledge","/api/reports","/api/whatsapp/accounts","/api/live/inbox"]:
    call(path,cookie=owner,expected=403)
plan,_=call("/api/platform-admin/plans","POST",{"name":"CI limited","monthlyMessages":1,"agents":0,"accounts":1,"bots":0,"articles":0,"campaigns":False,"ownModel":False,"monthlyPriceMinor":0,"currency":"USD"},cookie=owner,expected=201)
assert call("/api/subscription",cookie=a)[0]["managed"] is False
call("/api/platform-admin/tenants","POST",{"name":"CI new","username":"ci-new-admin","password":PASSWORD,"planId":plan["id"],"endsAt":"2099-01-01T00:00:00Z"},cookie=owner,expected=400)
from datetime import datetime,timezone,timedelta
expires=(datetime.now(timezone.utc)+timedelta(days=30)).isoformat()
new_tenant,_=call("/api/platform-admin/tenants","POST",{"name":"CI new","username":"ci-new-admin","password":PASSWORD,"planId":plan["id"],"endsAt":expires},cookie=owner,expected=201)
assert "passwordHash" not in new_tenant and "password" not in new_tenant
call("/api/platform-admin/tenants/"+new_tenant["id"]+"/active","PUT",{"isActive":False},cookie=a,expected=403)
assert any(t["id"]==new_tenant["id"] for t in call("/api/platform-admin/tenants",cookie=owner)[0])
browse="/api/platform-admin/tenants/browse"
call(browse,expected=401)
call(browse,cookie=a,expected=403)
call(browse+"?limit=101",cookie=owner,expected=400)
call(browse+"?by=invalid",cookie=owner,expected=400)
first_page=call(browse+"?limit=1",cookie=owner)[0]
assert len(first_page["items"])==1 and first_page["nextCursor"]==first_page["items"][0]["id"]
second_page=call(browse+"?limit=1&after="+first_page["nextCursor"],cookie=owner)[0]
assert len(second_page["items"])==1 and second_page["items"][0]["id"]>first_page["items"][0]["id"]
for query in ["by=id&search="+new_tenant["id"],"by=username&search=ci-new-admin","by=name&search=CI%20new"]:
    found=call(browse+"?"+query,cookie=owner)[0]
    assert [t["id"] for t in found["items"]]==[new_tenant["id"]] and found["nextCursor"] is None
    assert "passwordHash" not in json.dumps(found)
assert call(browse+"?by=id&search=missing",cookie=owner)[0]["items"]==[]
assert any(x["action"]=="tenant_created" for x in call("/api/platform-admin/audit",cookie=owner)[0])
print("PASS: isolated platform owner, immutable plan and tenant provisioning")
call("/api/dashboard",expected=401)
assert call("/api/dashboard",cookie=a)[0]["metrics"]["contacts"]==0
assert call("/api/dashboard",cookie=b)[0]["metrics"]["openConversations"]==0
call("/api/knowledge",expected=401)
call("/api/knowledge",cookie=agent,expected=403)
article={"title":"CI warranty","body":"Warranty period is 12 months","isPublished":True,"version":0,"tenantId":"ci-tenant-b"}
kb,_=call("/api/knowledge","POST",article,cookie=a,expected=201)
kp="/api/knowledge/"+kb["id"]
assert kb["tenantId"]=="ci-tenant-a"
assert not call("/api/knowledge",cookie=b)[0]
call(kp,cookie=b,expected=404)
call(kp,"PUT",dict(article,version=1),cookie=b,expected=404)
call(kp+"?version=1","DELETE",cookie=b,expected=404)
assert call("/api/knowledge/search","POST",{"question":"warranty"},cookie=a)[0][0]["id"]==kb["id"]
assert not call("/api/knowledge/search","POST",{"question":"warranty"},cookie=b)[0]
call(kp,"PUT",dict(article,version=1,isPublished=False),cookie=a)
assert not call("/api/knowledge/search","POST",{"question":"warranty"},cookie=a)[0]
call(kp,"PUT",dict(article,version=1),cookie=a,expected=409)
assert call("/api/knowledge/model",cookie=a)[0]["configured"] is False
call("/api/knowledge/preview","POST",{"question":"warranty"},cookie=a,expected=409)
call(kp+"?version=2","DELETE",cookie=a,expected=204)
print("PASS: isolated knowledge articles, published-only search, stale version and disabled own-model preview")
assert not call("/api/whatsapp/accounts",cookie=b)[0]
# Both supported account-list APIs use the authenticated tenant; legacy writes stay denied.
assert call("/api/whatsapp/tenants",expected=401)[0] is not None
own_tenants,_=call("/api/whatsapp/tenants",cookie=a)
assert len(own_tenants)==1 and own_tenants[0]["id"]=="ci-account-a" and own_tenants[0]["tenantId"]=="ci-tenant-a"
assert not call("/api/whatsapp/tenants",cookie=b)[0]
verify_path = "/api/whatsapp/accounts/ci-account-a/verify"
call(verify_path,"POST",expected=401)
call(verify_path,"POST",cookie=agent,expected=403)
call(verify_path,"POST",cookie=b,expected=404)
call(verify_path,"POST",cookie=a,expected=403,csrf=False)
call(verify_path,"POST",cookie=a,expected=409)
call("/api/whatsapp/tenants","POST",{"tenantId":"ci-tenant-a","phoneNumberId":"1234","accessToken":"fake"},cookie=a,expected=403)
call("/api/whatsapp/tenant-options",cookie=a,expected=403)
call("/api/whatsapp/messages",cookie=a,expected=403)
call("/api/whatsapp/send","POST",{"tenantId":"ci-tenant-a","to":"1234","text":"ci"},cookie=a,expected=403)
print("PASS: account/tenant lists scoped to session; obsolete unscoped APIs denied")

call("/api/live/inbox",cookie=a,expected=403,extra={"Origin":"https://untrusted.example"})
call("/api/bots",cookie=agent,expected=403)
flow={"start":"s","nodes":[{"id":"s","type":"start","next":"c","x":0,"y":0},{"id":"c","type":"contains","text":"hello","next":"r","otherwise":"e","x":180,"y":0},{"id":"r","type":"reply","text":"CI reply","next":"e","x":360,"y":0},{"id":"e","type":"end","x":540,"y":0}]}
assert call("/api/bots/simulate","POST",{"flow":flow,"input":"HELLO"},cookie=a)[0]["replies"]==["CI reply"]
assert call("/api/bots/simulate","POST",{"flow":flow,"input":"no match"},cookie=a)[0]["replies"]==[]
exact=json.loads(json.dumps(flow));exact["nodes"][1]["matchMode"]="equals"
assert call("/api/bots/simulate","POST",{"flow":exact,"input":" HELLO "},cookie=a)[0]["path"]==["s","c","r","e"]
assert call("/api/bots/simulate","POST",{"flow":exact,"input":"hello there"},cookie=a)[0]["path"]==["s","c","e"]
prefix=json.loads(json.dumps(flow));prefix["nodes"][1]["matchMode"]="starts_with"
assert call("/api/bots/simulate","POST",{"flow":prefix,"input":" HELLO there"},cookie=a)[0]["replies"]==["CI reply"]
assert call("/api/bots/simulate","POST",{"flow":prefix,"input":"say hello"},cookie=a)[0]["replies"]==[]
badmode=json.loads(json.dumps(flow));badmode["nodes"][1]["matchMode"]="regex"
call("/api/bots/simulate","POST",{"flow":badmode,"input":"hello"},cookie=a,expected=400)
badmode=json.loads(json.dumps(flow));badmode["nodes"][2]["matchMode"]="equals"
call("/api/bots/simulate","POST",{"flow":badmode,"input":"hello"},cookie=a,expected=400)
cyclic=json.loads(json.dumps(flow));cyclic["nodes"][2]["next"]="c"
call("/api/bots/simulate","POST",{"flow":cyclic,"input":"hello"},cookie=a,expected=400)
unreachable=json.loads(json.dumps(flow));unreachable["nodes"].append({"id":"unused","type":"end","x":0,"y":0})
call("/api/bots/simulate","POST",{"flow":unreachable,"input":"hello"},cookie=a,expected=400)
invalid=json.loads(json.dumps(flow));invalid["nodes"][1]["otherwise"]="missing"
call("/api/bots/simulate","POST",{"flow":invalid,"input":"hello"},cookie=a,expected=400)
memory_flow={"start":"s","nodes":[{"id":"s","type":"start","next":"w1","x":0,"y":0},{"id":"w1","type":"wait","text":"Name?","next":"capture","x":0,"y":0},{"id":"capture","type":"capture","variable":"name","next":"w2","x":0,"y":0},{"id":"w2","type":"wait","text":"Next?","next":"r","x":0,"y":0},{"id":"r","type":"reply","text":"Hello {{name}}","next":"e","x":0,"y":0},{"id":"e","type":"end","x":0,"y":0}]}
memory_result=call("/api/bots/simulate","POST",{"flow":memory_flow,"inputs":["hi","Alice","yes"]},cookie=a)[0]
assert memory_result["turns"][2]["replies"]==["Hello Alice"]
assert call("/api/bots/simulate","POST",{"flow":memory_flow,"inputs":["hi","Bob","yes"]},cookie=b)[0]["turns"][2]["replies"]==["Hello Bob"]
bad_memory=json.loads(json.dumps(memory_flow));bad_memory["nodes"][2]["variable"]="bad name"
call("/api/bots/simulate","POST",{"flow":bad_memory,"input":"hi"},cookie=a,expected=400)
call("/api/bots/simulate","POST",{"flow":flow,"input":"hello"},cookie=agent,expected=403)
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
        raw=response.readline()
        if not raw: raise AssertionError("stream ended")
        line=raw.decode().strip()
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
# Configuration diagnostics must reflect the same canonical secrets used by the webhook.
call("/api/whatsapp/webhook-status",expected=401)
for cookie in (a,b):
    status,_=call("/api/whatsapp/webhook-status",cookie=cookie)
    assert status["verifyTokenConfigured"] and status["appSecretConfigured"], status
    assert call("/api/whatsapp/diagnostics",cookie=cookie)[0]["webhookSignatureEnabled"] is True
# A signed malformed root is a client error, not a transient database outage.
def signed_webhook(payload,expected=200):
    signature="sha256="+hmac.new(b"ci-only-test-secret",json.dumps(payload).encode(),hashlib.sha256).hexdigest()
    return call("/webhook","POST",payload,extra={"X-Hub-Signature-256":signature},expected=expected)[0]
signed_webhook([],expected=400)
assert signed_webhook({"entry":[None,{"changes":[None,{"value":{"metadata":{"phone_number_id":"000000000001"},"messages":[{"id":"wamid.ci.unknown","from":"15550000001","type":"text","text":{"body":"unknown account"}}]}}]}]})["saved"]==0
assert call("/webhook?hub.mode=subscribe&hub.verify_token=ci-only-test-token&hub.challenge=12345")[0]=="12345"
call("/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=12345",expected=401)
call("/webhook","POST",{},expected=401)
body={"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"999999999999"},"messages":[{"id":"wamid.ci.inbound.1","from":"15550000001","type":"text","text":{"body":"CI inbound message"}}]}}]}]}
sig="sha256="+hmac.new(b"ci-only-test-secret",json.dumps(body).encode(),hashlib.sha256).hexdigest()
call("/webhook","POST",body,extra={"X-Hub-Signature-256":"sha256="+"0"*64},expected=401)
call("/webhook","POST",body,extra={"X-Hub-Signature-256":"sha256=not-hex"},expected=401)
for _ in range(2): call("/webhook","POST",body,extra={"X-Hub-Signature-256":sig})
for _ in range(30):
    conversations,_=call("/api/conversations",cookie=a)
    match=next((c for c in conversations if c["contactPhone"]=="15550000001" and c.get("lastMessage")),None)
    if match: break
    time.sleep(1)
else: raise AssertionError("Inbound message not persisted")
assert match["lastMessage"]["text"]=="CI inbound message"
summary,_=call("/api/dashboard",cookie=a)
assert summary["metrics"]["contacts"]==1 and summary["metrics"]["openConversations"]==1
assert summary["recent"][0]["contactPhone"]=="15550000001"
assert call("/api/dashboard",cookie=b)[0]["recent"]==[]
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
assert call("/api/contacts?search=ci@example.invalid",cookie=a)[0]["total"]==1
activity_path="/api/contacts/15550000001/activity"
call(activity_path,expected=401)
call("/api/contacts/15550000002/activity",cookie=a,expected=404)
activity,_=call(activity_path,cookie=a)
assert activity["conversationCount"]==1 and activity["openConversationCount"]==1
assert activity["conversations"][0]["id"]==match["id"]
assert any(m["textPreview"]=="CI inbound message" for m in activity["recentMessages"])
assert all("contentJson" not in m and "tenantId" not in m for m in activity["recentMessages"])
assert any(x["phoneNumberId"]=="999999999999" and x["granted"] is None for x in activity["consent"])
assert call(activity_path,cookie=managed_cookie)[0]["consent"] is None
other_activity,_=call(activity_path,cookie=b)
assert other_activity["conversationCount"]==0 and not other_activity["recentMessages"] and not other_activity["conversations"]
assert not any(x["phoneNumberId"]=="999999999999" for x in other_activity["consent"])
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
assert any(x["id"]==cid for x in call("/api/conversations?unread=true&phoneNumberId=999999999999",cookie=a)[0])
assert not call("/api/conversations?phoneNumberId=999999999999&unread=true",cookie=b)[0]
assert not call("/api/conversations?phoneNumberId=123456789",cookie=a)[0]
call("/api/conversations?phoneNumberId=bad",cookie=a,expected=400)
call(root+"/read","PUT",{},cookie=managed_cookie,expected=204)
assert not call("/api/conversations?unread=true",cookie=a)[0]
call(root+"/tags","PUT",{"tags":"priority"},cookie=managed_cookie,expected=204)
closed,_=call("/api/conversations?status=closed&search=CI",cookie=a)
assert len(closed)==1 and closed[0]["unreadCount"]==0 and closed[0]["tags"]=="priority"
assert not call("/api/conversations?status=open",cookie=a)[0]
call(root+"/status","PUT",{"status":"open"},cookie=a,expected=204)
print("PASS: CRM import, same-phone tenant isolation, assignment, filters, status and unread updates")
# Real webhook path with intentionally invalid encrypted token: queues are observable but no Meta call can succeed.
# A separate in-process mocked Graph test verifies actual delivery.
with sqlite3.connect(DB) as db:
    db.execute("UPDATE WhatsAppTenants SET BusinessTokenEncrypted='invalid-ci-ciphertext' WHERE Id='ci-account-a'")
fallback,_=call("/api/autoreply","POST",{"name":"Fallback","matchType":"any","replyText":"Legacy fallback","cooldownSeconds":0,"isActive":True},cookie=a)
call(botpath+"/publish","POST",{"version":2},cookie=b,expected=404)
call(botpath+"/publish","POST",{"version":2},cookie=agent,expected=403)
call(botpath+"/publish","POST",{"version":1},cookie=a,expected=409)
pub,_=call(botpath+"/publish","POST",{"version":2},cookie=a)
assert call("/api/bots/active",cookie=b)[0]["active"] is None
changed_flow=json.loads(json.dumps(flow));changed_flow["nodes"][2]["text"]="Unpublished edit"
call(botpath,"PUT",{"name":"Updated draft","flow":changed_flow,"version":2},cookie=a)
assert call("/api/bots/active",cookie=a)[0]["active"]["version"]==2
def automate(wamid,text,timestamp=None):
    bdy={"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"999999999999"},"messages":[{"id":wamid,"from":"15550000001","type":"text","text":{"body":text}}]}}]}]}
    if timestamp is not None: bdy["entry"][0]["changes"][0]["value"]["messages"][0]["timestamp"]=str(timestamp)
    signature="sha256="+hmac.new(b"ci-only-test-secret",json.dumps(bdy).encode(),hashlib.sha256).hexdigest()
    for _ in range(2): call("/webhook","POST",bdy,extra={"X-Hub-Signature-256":signature})
    for _ in range(30):
        with sqlite3.connect(DB) as db:
            result=db.execute("SELECT r.Outcome,r.BotVersion,r.RuleId,m.TextPreview FROM AutomationRuns r JOIN WhatsAppMessages i ON i.Id=r.InboundMessageId LEFT JOIN WhatsAppMessages m ON m.Id=r.OutboundMessageId WHERE i.Wamid=?",(wamid,)).fetchall()
            if result:
                assert len(result)==1
                return result[0]
        time.sleep(1)
    raise AssertionError("No automation execution receipt")
assert automate("wamid.ci.bot.1","hello")==("queued",2,None,"CI reply")
assert automate("wamid.ci.bot.2","unmatched")==("no_match",2,None,None)
assert automate("wamid.ci.bot.old","hello",int(time.time())-90000)==("account_or_window_unavailable",2,None,None)
with sqlite3.connect(DB) as db:
    assert db.execute("SELECT COUNT(*) FROM WhatsAppOutbox WHERE TenantId='ci-tenant-a' AND IdempotencyKey LIKE 'automation:%'").fetchone()[0]==1
    assert db.execute("SELECT COUNT(*) FROM WhatsAppMessages WHERE TenantId='ci-tenant-a' AND Direction='Outbound'").fetchone()[0]==1
stop_path=botpath+"/publish/"+pub["activationId"]
call(stop_path,"DELETE",cookie=b,expected=404)
call(stop_path,"DELETE",cookie=a,expected=204)
assert call("/api/bots/active",cookie=a)[0]["active"] is None
outcome,ver,rule_id,reply=automate("wamid.ci.legacy.1","anything")
assert outcome=="queued" and ver is None and rule_id and reply=="Legacy fallback"
assert not call("/api/bots/runs",cookie=b)[0]
runs,_=call("/api/bots/runs",cookie=a)
assert any(r["botVersion"]==2 and r["outcome"]=="queued" for r in runs)
# Multi-turn state survives separate signed webhook deliveries and takes priority over legacy rules.
multi={"start":"s","nodes":[{"id":"s","type":"start","next":"w","x":0,"y":0},
 {"id":"w","type":"wait","text":"Choose support or price","next":"capture","x":180,"y":0},
 {"id":"capture","type":"capture","variable":"choice","next":"c","x":270,"y":0},
 {"id":"c","type":"contains","text":"support","variable":"choice","next":"h","otherwise":"r","x":360,"y":0},
 {"id":"h","type":"handoff","x":540,"y":0},{"id":"r","type":"wait","text":"Confirm price?","next":"final","x":540,"y":180},{"id":"final","type":"reply","text":"Price is 10 · {{choice}}","next":"e","x":720,"y":180},
 {"id":"e","type":"end","x":720,"y":180}]}
multi_sim,_=call("/api/bots/simulate","POST",{"flow":multi,"inputs":["hi","support"]},cookie=a)
assert multi_sim["turns"][0]["nextNode"]=="capture" and multi_sim["turns"][1]["handoff"]
multi_sim,_=call("/api/bots/simulate","POST",{"flow":multi,"inputs":["hi","price","yes"]},cookie=a)
assert multi_sim["turns"][1]["replies"]==["Confirm price?"] and multi_sim["turns"][2]["replies"]==["Price is 10 · price"]
created_multi,_=call("/api/bots","POST",{"name":"Multi","flow":multi,"version":0},cookie=a,expected=201)
mp="/api/bots/"+created_multi["id"]
published,_=call(mp+"/publish","POST",{"version":1},cookie=a)
with sqlite3.connect(DB) as db:
    db.execute("UPDATE AutomationRuns SET CreatedAtUtc='2020-01-01' WHERE TenantId='ci-tenant-a'")
assert automate("wamid.ci.multi.1","hi")==("queued",1,None,"Choose support or price")
assert call("/api/conversations",cookie=a)[0][0]["waitingForInput"]
assert automate("wamid.ci.multi.2","support")==("human_handoff",1,None,None)
assert call("/api/conversations?automation=human",cookie=a)[0][0]["id"]==cid
assert call("/api/conversations?automation=human",cookie=b)[0]==[]
assert automate("wamid.ci.multi.3","hi")==("human_mode",1,None,None)
call(root+"/automation","PUT",{"mode":"bot"},cookie=b,expected=404)
call(root+"/automation","PUT",{"mode":"bot"},cookie=managed_cookie,expected=204)
with sqlite3.connect(DB) as db:
    db.execute("UPDATE AutomationRuns SET CreatedAtUtc='2020-01-01' WHERE TenantId='ci-tenant-a'")
assert automate("wamid.ci.multi.4","hi")==("queued",1,None,"Choose support or price")
assert automate("wamid.ci.multi.5","price")==("queued",1,None,"Confirm price?")
with sqlite3.connect(DB) as db:
    saved=db.execute("SELECT VariablesJson FROM ConversationAutomation WHERE TenantId=? AND lower(ConversationId)=?",("ci-tenant-a",cid.lower())).fetchone()
    assert json.loads(saved[0])=={"choice":"price"}
assert automate("wamid.ci.multi.confirm","yes")==("queued",1,None,"Price is 10 · price")
assert not call("/api/conversations",cookie=a)[0][0]["waitingForInput"]
with sqlite3.connect(DB) as db:
    assert all(x[0]=="{}" for x in db.execute("SELECT VariablesJson FROM ConversationAutomation WHERE TenantId='ci-tenant-a'"))
# Simulate a scheduled, not-yet-claimed automation reply. The invalid test token
# and future scheduled date ensure the worker never sends the synthetic message.
import uuid
with sqlite3.connect(DB) as db:
    conversation_id=db.execute("SELECT Id FROM WhatsAppConversations WHERE lower(Id)=?",(cid.lower(),)).fetchone()[0]
    pending_message=str(uuid.uuid4()).upper()
    pending_outbox=str(uuid.uuid4()).upper()
    db.execute("""INSERT INTO WhatsAppMessages(Id,TenantId,ConversationId,Direction,MessageType,ContentJson,
        TextPreview,Status,TimestampUtc,CreatedAtUtc)
        VALUES(?,?,?,'Outbound','text','{}','scheduled CI reply','Queued','2026-01-01','2026-01-01')""",
        (pending_message,"ci-tenant-a",conversation_id))
    db.execute("""INSERT INTO WhatsAppOutbox(Id,TenantId,CorrelationId,IdempotencyKey,MessageId,PhoneNumberId,
        RecipientPhone,MessageType,PayloadJson,Status,Attempts,MaxAttempts,CreatedAtUtc,NextAttemptAtUtc,
        ScheduledForUtc,UpdatedAtUtc)
        VALUES(?,?,?,?,?,'999999999999','15550000001','text','{}','Pending',0,10,'2026-01-01','2099-01-01',
        '2099-01-01','2026-01-01')""",
        (pending_outbox,"ci-tenant-a",pending_outbox,"automation:ci-scheduled",pending_message))
call(root+"/automation","PUT",{"mode":"human"},cookie=managed_cookie,expected=204)
with sqlite3.connect(DB) as db:
    assert db.execute("SELECT Status,LastError FROM WhatsAppOutbox WHERE Id=?",(pending_outbox,)).fetchone()==("Cancelled","human_handoff")
    assert db.execute("SELECT Status FROM WhatsAppMessages WHERE Id=?",(pending_message,)).fetchone()==("Cancelled",)
print("PASS: human takeover cancels pending automated outbox and message atomically")
call(mp+"/publish/"+published["activationId"],"DELETE",cookie=a,expected=204)
assert automate("wamid.ci.multi.6","hi")==("human_mode",None,None,None)
call(root+"/automation","PUT",{"mode":"bot"},cookie=a,expected=204)
assert automate("wamid.ci.multi.7","hi")[3]=="Legacy fallback"
print("PASS: multi-turn continuation, tenant-safe human takeover, pause and resume")
# Campaign scheduling is enabled only in this disposable test process; scheduled date is future.
call("/api/database-connections",expected=401)
call("/api/database-connections",cookie=agent,expected=403)
assert call("/api/database-connections",cookie=a)[0]==[]
call("/api/database-connections/unknown/probe","POST",{},cookie=a,expected=404)
call("/api/marketing/lists",expected=401)
call("/api/marketing/lists",cookie=agent,expected=403)
consent={"phoneNumberId":"999999999999","phone":"15550000001","granted":True,"evidence":"CI-only synthetic consent receipt"}
call("/api/marketing/consent","PUT",dict(consent,phoneNumberId="999999999999"),cookie=b,expected=404)
call("/api/marketing/consent","PUT",dict(consent,evidence=""),cookie=a,expected=400)
segment="/api/marketing/segments/preview?phoneNumberId=999999999999&stage=lead&tag=vip"
call(segment,expected=401)
call(segment,cookie=agent,expected=403)
call(segment,cookie=b,expected=404)
call("/api/marketing/segments/preview?phoneNumberId=999999999999&stage=invalid",cookie=a,expected=400)
before,_=call(segment,cookie=a)
assert before["matched"]==1 and before["eligible"]==0 and before["excluded"]==1 and before["items"]==[]
call("/api/marketing/consent","PUT",consent,cookie=a,expected=204)
preview,_=call(segment,cookie=a)
assert preview["matched"]==1 and preview["eligible"]==1 and preview["excluded"]==0 and not preview["truncated"]
assert [x["phone"] for x in preview["items"]]==["15550000001"]
assert call("/api/marketing/lists",cookie=a)[0]==[]
assert call("/api/marketing/campaigns",cookie=a)[0]==[]
assert next(x["granted"] for x in call(activity_path,cookie=a)[0]["consent"] if x["phoneNumberId"]=="999999999999") is True
lst,_=call("/api/marketing/lists","POST",{"name":"CI list","phones":["15550000001","15550000001"],"version":0},cookie=a)
assert len(lst["phones"])==1 and call("/api/marketing/lists",cookie=b)[0]==[]
call("/api/marketing/lists/"+lst["id"],"PUT",{"name":"Foreign","phones":["15550000001"],"version":1},cookie=b,expected=404)
with sqlite3.connect(DB) as db:
    db.execute("UPDATE WhatsAppTenants SET WabaId='999999999997' WHERE Id='ci-account-a'")
    db.execute("""INSERT INTO WhatsAppTemplates(Id,TenantId,WhatsAppTenantId,Name,Category,Language,Body,Status,IsActive,CreatedAtUtc,UpdatedAtUtc)
        VALUES('ci-marketing','ci-tenant-a','ci-account-a','ci_marketing','MARKETING','en','CI offer','LOCAL',1,'2026-01-01','2026-01-01')""")
from datetime import datetime,timezone,timedelta
schedule={"id":str(uuid.uuid4()),"name":"CI campaign","listId":lst["id"],"templateId":"ci-marketing","scheduledAt":(datetime.now(timezone.utc)+timedelta(hours=1)).isoformat()}
call("/api/marketing/campaigns","POST",schedule,cookie=b,expected=404)
result,_=call("/api/marketing/campaigns","POST",schedule,cookie=a,expected=202)
assert result["eligible"]==1
call("/api/marketing/campaigns","POST",schedule,cookie=a)
assert call("/api/marketing/campaigns",cookie=a)[0][0]["counts"]=={"Pending":1}
assert not call("/api/marketing/campaigns",cookie=b)[0]
call("/api/marketing/campaigns/"+schedule["id"]+"/cancel","POST",{},cookie=b,expected=404)
# Signed inbound STOP, not a fabricated marketing opt-in.
stop_body={"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":"999999999999"},"messages":[{"id":"wamid.ci.stop","from":"15550000001","type":"text","text":{"body":"STOP"}}]}}]}]}
sig="sha256="+hmac.new(b"ci-only-test-secret",json.dumps(stop_body).encode(),hashlib.sha256).hexdigest()
call("/webhook","POST",stop_body,extra={"X-Hub-Signature-256":sig})
for _ in range(30):
    campaigns,_=call("/api/marketing/campaigns",cookie=a)
    if campaigns[0]["counts"]=={"Cancelled":1}:break
    time.sleep(1)
else:raise AssertionError("Opt-out did not cancel scheduled campaign")
assert not call("/api/marketing/consents?phoneNumberId=999999999999",cookie=a)[0][0]["granted"]
assert call(segment,cookie=a)[0]["eligible"]==0
assert next(x["granted"] for x in call(activity_path,cookie=a)[0]["consent"] if x["phoneNumberId"]=="999999999999") is False
with sqlite3.connect(DB) as db:
    assert db.execute("SELECT COUNT(*) FROM ConsentEvents WHERE TenantId='ci-tenant-a'").fetchone()[0]==2
call("/api/marketing/campaigns","POST",dict(schedule,id=str(uuid.uuid4())),cookie=a,expected=409)
print("PASS: campaign scheduling, deduplication, tenant isolation and signed opt-out cancellation")
call("/api/reports",expected=401)
call("/api/reports",cookie=agent,expected=403)
ra,_=call("/api/reports?days=7",cookie=a)
rb,_=call("/api/reports?days=7",cookie=b)
assert len(ra["daily"])==7 and sum(d["inbound"] for d in ra["daily"])>0
assert sum(d["outbound"] for d in rb["daily"])==0 and rb["consent"]==[]
assert any(x["granted"]==False and x["count"]==1 for x in ra["consent"])
print("PASS: tenant-scoped date-bounded reports and consent aggregates")
# Signed status notices apply only to outbound message/account/tenant/recipient matches.
with sqlite3.connect(DB) as db:
    receipt_message=str(uuid.uuid4()).upper()
    db.execute("""INSERT INTO WhatsAppMessages(Id,TenantId,ConversationId,Wamid,Direction,MessageType,ContentJson,TextPreview,Status,TimestampUtc,CreatedAtUtc)
        VALUES(?,?,?,'wamid.ci.receipt','Outbound','text','{}','CI receipt','Sent',?,?)""",
        (receipt_message,"ci-tenant-a",conversation_id,datetime.now(timezone.utc).isoformat(),datetime.now(timezone.utc).isoformat()))
def receipt(status,phone_id="999999999999",recipient="15550000001"):
    body={"entry":[{"changes":[{"value":{"metadata":{"phone_number_id":phone_id},"statuses":[{"id":"wamid.ci.receipt","recipient_id":recipient,"status":status,"timestamp":str(int(time.time()))}]}}]}]}
    signed="sha256="+hmac.new(b"ci-only-test-secret",json.dumps(body).encode(),hashlib.sha256).hexdigest()
    call("/webhook","POST",body,extra={"X-Hub-Signature-256":signed})
def wait_receipt(expected):
    for _ in range(20):
        with sqlite3.connect(DB) as db:
            if db.execute("SELECT Status FROM WhatsAppMessages WHERE Id=?",(receipt_message,)).fetchone()[0]==expected:return
        time.sleep(.5)
    raise AssertionError("Missing delivery receipt status "+expected)
receipt("delivered")
wait_receipt("Delivered")
receipt("read")
wait_receipt("Read")
receipt("failed")
for _ in range(20):
    with sqlite3.connect(DB) as db:
        if db.execute("SELECT COUNT(*) FROM WhatsAppStatusInbox WHERE Wamid='wamid.ci.receipt' AND Status='failed' AND Processed=1").fetchone()[0]:break
    time.sleep(.5)
else:raise AssertionError("Late failed receipt not processed")
wait_receipt("Read")
receipt("delivered",recipient="15559999999")
assert any(x["status"]=="Read" for x in call("/api/reports?days=7",cookie=a)[0]["delivery"])
print("PASS: signed delivered/read receipts, late failure cannot downgrade read")
call("/api/autoreply/"+fallback["id"],"DELETE",cookie=a)
with sqlite3.connect(DB) as db:
    db.execute("UPDATE WhatsAppTenants SET BusinessTokenEncrypted='' WHERE Id='ci-account-a'")
print("PASS: published snapshot, exclusive bot priority, duplicate webhook, stop and durable legacy fallback")
# Enforce a lower plan after previous regression tests, preserving their fixture.
subscription_path="/api/platform-admin/tenants/ci-tenant-a/subscription"
call(subscription_path,"PUT",{"planId":plan["id"],"enabled":True,"endsAt":expires,"version":0},cookie=owner)
call(subscription_path,"PUT",{"planId":plan["id"],"enabled":True,"endsAt":expires,"version":0},cookie=owner,expected=409)
sub,_=call("/api/subscription",cookie=a)
assert sub["managed"] and sub["active"] and sub["usage"]["messages"]>=1 and sub["plan"]["monthlyMessages"]==1
call("/api/team","POST",{"username":"ci-limit","displayName":"Quota","password":PASSWORD},cookie=a,expected=409)
call("/api/bots","POST",{"name":"Quota","flow":flow,"version":0},cookie=a,expected=409)
call("/api/knowledge","POST",article,cookie=a,expected=409)
with sqlite3.connect(DB) as db:
    db.execute("UPDATE WhatsAppTenants SET BusinessTokenEncrypted='invalid-ci-ciphertext' WHERE Id='ci-account-a'")
call("/api/messages/send-template","POST",send_template,cookie=a,expected=409)
call(subscription_path,"PUT",{"planId":plan["id"],"enabled":False,"endsAt":expires,"version":1},cookie=owner)
assert not call("/api/subscription",cookie=a)[0]["active"]
call("/api/messages/send-template","POST",send_template,cookie=a,expected=409)
call("/api/knowledge/preview","POST",{"question":"CI test"},cookie=a,expected=409)
with sqlite3.connect(DB) as db:
    db.execute("UPDATE WhatsAppTenants SET BusinessTokenEncrypted='' WHERE Id='ci-account-a'")
# Restore a larger plan for subsequent account lifecycle tests.
larger,_=call("/api/platform-admin/plans","POST",{"name":"CI larger","monthlyMessages":1000,"agents":5,"accounts":5,"bots":20,"articles":50,"campaigns":True,"ownModel":True,"monthlyPriceMinor":0,"currency":"USD"},cookie=owner,expected=201)
call(subscription_path,"PUT",{"planId":larger["id"],"enabled":True,"endsAt":expires,"version":2},cookie=owner)
print("PASS: plan quotas, stale subscription revision and disabled sending")
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
new_cookie=login("ci-new-admin")
assert call("/api/auth/me",cookie=new_cookie)[0]["tenantId"]==new_tenant["id"]
assert call("/api/subscription",cookie=new_cookie)[0]["plan"]["id"]==plan["id"]
# Synthetic invalid token prevents network access while exercising one reserved slot.
with sqlite3.connect(DB) as db:
    db.execute("""INSERT INTO WhatsAppTenants(Id,TenantId,PhoneNumberId,IsActive,CreatedAtUtc,WabaId,AppId,BusinessTokenEncrypted,CreatedAt)
       VALUES('ci-new-account',?,'999999999996',1,'2026-01-01','999999999995','','invalid-ci-ciphertext','2026-01-01')""",(new_tenant["id"],))
new_send={"to":"15550000009","phoneNumberId":"999999999996","template":{"name":"ci_example","language":{"code":"ar"}}}
first,_=call("/api/messages/send-template","POST",new_send,cookie=new_cookie,expected=202,extra={"Idempotency-Key":"ci-quota-first"})
repeat,_=call("/api/messages/send-template","POST",new_send,cookie=new_cookie,expected=202,extra={"Idempotency-Key":"ci-quota-first"})
assert first["id"]==repeat["id"]
assert call("/api/subscription",cookie=new_cookie)[0]["usage"]["messages"]==1
call("/api/messages/send-template","POST",new_send,cookie=new_cookie,expected=409,extra={"Idempotency-Key":"ci-quota-second"})
with sqlite3.connect(DB) as db:
    # A second synthetic future row represents a reservation made before a plan reduction.
    scheduled_id=str(uuid.uuid4()).upper()
    db.execute("""INSERT INTO WhatsAppOutbox(Id,TenantId,CorrelationId,IdempotencyKey,PhoneNumberId,RecipientPhone,MessageType,PayloadJson,Status,Attempts,MaxAttempts,CreatedAtUtc,NextAttemptAtUtc,ScheduledForUtc,UpdatedAtUtc)
       VALUES(?,?,?,'ci-future-reservation','999999999996','15550000009','template','{}','Pending',0,10,'2026-01-01','2099-01-01','2099-01-01','2026-01-01')""",(scheduled_id,new_tenant["id"],scheduled_id))
print("PASS: one outgoing reservation, same-key replay free, distinct second send blocked")
call("/api/platform-admin/tenants/"+new_tenant["id"]+"/active","PUT",{"isActive":False},cookie=owner,expected=204)
with sqlite3.connect(DB) as db:
    assert db.execute("SELECT Status,LastError FROM WhatsAppOutbox WHERE Id=?",(scheduled_id,)).fetchone()==("Cancelled","tenant_suspended")
call("/api/auth/me",cookie=new_cookie,expected=401)
call("/api/platform-admin/tenants/"+new_tenant["id"]+"/active","PUT",{"isActive":True},cookie=owner,expected=204)
call("/api/auth/me",cookie=new_cookie,expected=401)
print("PASS: provisioned administrator scope and tenant suspension revokes sessions")
with open_live(a) as stream:
    assert next_event(stream)[0]=="inbox"
    call("/api/auth/logout","POST",cookie=a,expected=204)
    assert next_event(stream)==("session-expired","1")
call("/api/live/inbox",cookie=a,expected=401)
call("/api/auth/me",cookie=a,expected=401)
print("PASS: active SSE stream stops after server-side session revocation")
call("/api/auth/me",expected=401)
print("PASS: authenticated reads, cookie flags, role checks, CSRF, tenant isolation, signed inbound and deduplication")
