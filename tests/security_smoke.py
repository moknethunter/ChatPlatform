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
call("/api/conversations?tenantId=ci-tenant-b",cookie=a,expected=403)
call("/api/events","POST",{},cookie=a,expected=403)
call("/api/messages/send","POST",{},cookie=a,expected=403)
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
with sqlite3.connect(DB) as db:
    db.execute("UPDATE Tenants SET IsActive=0 WHERE Id='ci-tenant-b'")
call("/api/auth/me",cookie=b,expected=401)
call("/api/auth/logout","POST",cookie=a,expected=204)
call("/api/auth/me",expected=401)
print("PASS: authenticated reads, cookie flags, role checks, CSRF, tenant isolation, signed inbound and deduplication")
