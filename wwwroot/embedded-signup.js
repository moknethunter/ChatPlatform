/* Isolated candidate Meta SDK flow. Never persist or log authorization codes. */
(function (root) {
    "use strict";
    const origins = new Set(["https://www.facebook.com", "https://web.facebook.com", "https://facebook.com", "https://m.facebook.com"]);
    const metaId = value => typeof value === "string" && /^[0-9]{1,32}$/.test(value);
    function parseEvent(event) {
        if (!origins.has(event.origin) || typeof event.data !== "string" || event.data.length > 16384) return null;
        let value;
        try { value = JSON.parse(event.data); } catch { return null; }
        if (!value || value.type !== "WA_EMBEDDED_SIGNUP") return null;
        if (value.event === "CANCEL" || value.event === "ERROR") return { kind: "cancel" };
        if (value.event !== "FINISH") return null;
        if (!metaId(value.data?.waba_id) || !metaId(value.data?.phone_number_id)) return null;
        return { kind: "finish", wabaId: value.data.waba_id, phoneNumberId: value.data.phone_number_id };
    }

    // SDK callback and postMessage may arrive in either order.
    function createFlow(state, complete, fail) {
        let code = null, account = null, done = false;
        function clear() { done = true; code = null; account = null; }
        function finish() {
            if (done || !code || !account) return;
            const payload = { state, code, wabaId: account.wabaId, phoneNumberId: account.phoneNumberId };
            clear();
            complete(payload);
        }
        return {
            message(event) {
                if (done) return;
                const parsed = parseEvent(event);
                if (!parsed) return;
                if (parsed.kind === "cancel") { clear(); fail(); return; }
                if (account && (account.wabaId !== parsed.wabaId || account.phoneNumberId !== parsed.phoneNumberId)) {
                    clear(); fail(); return;
                }
                account = parsed;
                finish();
            },
            callback(response) {
                if (done) return;
                const value = response?.authResponse?.code;
                if (typeof value !== "string" || !value || value.length > 4096 || /\s/.test(value)) {
                    clear(); fail(); return;
                }
                code = value;
                finish();
            },
            cancel() { clear(); }
        };
    }

    function loadSdk() {
        return new Promise((resolve, reject) => {
            if (root.FB?.init && root.FB?.login) { resolve(root.FB); return; }
            const script = document.createElement("script");
            script.src = "https://connect.facebook.net/en_US/sdk.js";
            script.async = true;
            script.crossOrigin = "anonymous";
            const timeout = setTimeout(() => { script.remove(); reject(new Error("SDK timeout")); }, 15000);
            script.onload = () => {
                clearTimeout(timeout);
                if (root.FB?.init && root.FB?.login) resolve(root.FB);
                else reject(new Error("SDK unavailable"));
            };
            script.onerror = () => { clearTimeout(timeout); script.remove(); reject(new Error("SDK unavailable")); };
            document.head.appendChild(script);
        });
    }

    function attach({ api, prepare, launch, cancel, status, refresh }) {
        let prepared = null, active = null, timer = null, listener = null, busy = false;
        const display = (message, type = "info") => { status.textContent = message; status.className = "message show " + type; };
        const reset = () => {
            if (listener) root.removeEventListener("message", listener);
            if (timer) clearTimeout(timer);
            if (active) active.cancel();
            listener = timer = active = prepared = null;
            busy = false;
            prepare.disabled = false;
            launch.disabled = true;
            cancel.disabled = true;
        };
        reset();
        cancel.addEventListener("click", () => { reset(); display("أُلغيت محاولة الربط. ابدأ محاولة جديدة."); });
        prepare.addEventListener("click", async () => {
            if (busy) return;
            busy = true;
            prepare.disabled = true;
            display("جارٍ التحقق من إتاحة الربط…");
            try {
                const config = await api.startEmbeddedSignup();
                if (!metaId(config?.appId) || !metaId(config?.configId) ||
                    !/^[0-9A-F]{64}$/.test(config?.state || "") ||
                    !/^v[0-9]+\.[0-9]+$/.test(config?.graphApiVersion || "")) throw new Error("Invalid setup");
                const expires = Date.now() + Math.min(300, Number(config.expiresInSeconds) || 0) * 1000;
                const fb = await loadSdk();
                fb.init({ appId: config.appId, version: config.graphApiVersion, cookie: false, xfbml: false });
                if (expires <= Date.now()) throw new Error("Expired setup");
                prepared = { fb, config, expires };
                launch.disabled = false;
                cancel.disabled = false;
                timer = setTimeout(() => { reset(); display("انتهت مهلة الربط؛ ابدأ محاولة جديدة.", "error"); }, expires - Date.now());
                display("الربط التجريبي جاهز. افتح نافذة Meta لإكمال التفويض.");
            } catch {
                reset();
                display("الربط التلقائي غير متاح أو تعذر تحميل Meta. يمكنك استخدام الربط اليدوي.", "error");
            }
        });
        launch.addEventListener("click", () => {
            if (!prepared || active || Date.now() >= prepared.expires) return;
            const { fb, config } = prepared;
            launch.disabled = true;
            const fail = () => { reset(); display("لم يكتمل تفويض Meta. ابدأ محاولة جديدة.", "error"); };
            active = createFlow(config.state, async payload => {
                // No resubmission after an ambiguous response. Refresh accounts before retrying.
                if (listener) root.removeEventListener("message", listener);
                listener = null;
                if (timer) clearTimeout(timer);
                timer = null;
                cancel.disabled = true;
                display("جارٍ التحقق من الحساب وحفظه…");
                try {
                    await api.completeEmbeddedSignup(payload);
                    reset();
                    display("تم حفظ الحساب بعد التحقق. اختبر إرسال واستقبال الرسائل بصورة مستقلة.", "success");
                    await refresh();
                } catch {
                    reset();
                    display("تعذر تأكيد الربط. حدّث الحسابات قبل بدء محاولة أخرى.", "error");
                    await refresh().catch(() => {});
                }
            }, fail);
            const flow = active;
            listener = event => flow.message(event);
            root.addEventListener("message", listener);
            try {
                // Direct user gesture; avoid awaiting a request before opening the popup.
                fb.login(response => flow.callback(response), {
                    config_id: config.configId, response_type: "code",
                    override_default_response_type: true, extras: { setup: {} }
                });
            } catch { fail(); }
        });
    }

    const exported = { parseEvent, createFlow, attach };
    if (typeof module === "object" && module.exports) module.exports = exported;
    else root.ChatPlatformEmbeddedSignup = exported;
})(typeof window !== "undefined" ? window : globalThis);
