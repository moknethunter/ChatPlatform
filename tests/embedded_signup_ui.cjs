const assert = require("node:assert/strict");
const { parseEvent, createFlow } = require("../wwwroot/embedded-signup.js");
const event = (origin = "https://www.facebook.com", data = { type: "WA_EMBEDDED_SIGNUP", event: "FINISH", data: { waba_id: "789", phone_number_id: "123" } }) => ({ origin, data: JSON.stringify(data) });
for (const origin of ["http://www.facebook.com", "https://notfacebook.com", "https://www.facebook.com.evil.invalid", "https://facebook.com:444", "null"]) assert.equal(parseEvent(event(origin)), null);
assert.equal(parseEvent({ origin: "https://www.facebook.com", data: "{" }), null);
assert.equal(parseEvent(event("https://www.facebook.com", { type: "WA_EMBEDDED_SIGNUP", event: "FINISH", data: { waba_id: "789", phone_number_id: "../bad" } })), null);
assert.deepEqual(parseEvent(event()), { kind: "finish", wabaId: "789", phoneNumberId: "123" });
const code = { authResponse: { code: "synthetic-code" } };
for (const reverse of [false, true]) {
    const calls = [];
    const flow = createFlow("state", payload => calls.push(payload), () => assert.fail("unexpected cancel"));
    flow.message(event("https://notfacebook.com"));
    if (reverse) { flow.callback(code); flow.message(event()); }
    else { flow.message(event()); flow.callback(code); }
    flow.callback(code); flow.message(event());
    assert.deepEqual(calls, [{ state: "state", code: "synthetic-code", wabaId: "789", phoneNumberId: "123" }]);
}
let failures = 0;
const cancelled = createFlow("state", () => assert.fail("unexpected completion"), () => failures++);
cancelled.message(event("https://www.facebook.com", { type: "WA_EMBEDDED_SIGNUP", event: "CANCEL" }));
cancelled.callback(code); cancelled.message(event());
assert.equal(failures, 1);
const stale = createFlow("state", () => assert.fail("cancelled flow completed"), () => assert.fail("unexpected failure"));
stale.cancel(); stale.message(event()); stale.callback(code);
const invalid = createFlow("state", () => assert.fail("invalid code accepted"), () => failures++);
invalid.callback({ authResponse: { code: "bad code" } });
invalid.message(event());
assert.equal(failures, 2);
console.log("PASS: signup UI strict origins, event schema, both arrival orders, cancellation and one-shot completion");
