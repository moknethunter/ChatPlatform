"use strict";

/**
 * ChatPlatform API Client - متوافق مع مشروع GitHub + SnapDeploy + WhatsApp Business API
 * @version 2.1.0
 * @description عميل API لمنصة واتساب مع Retry لـ GET فقط، Idempotency لـ POST، Hooks، وإلغاء آمن.
 * يعمل على same-origin افتراضيا: https://chat55d-6696f.containers.snapdeploy.app
 */
(() => {
    "use strict";

    // =====================================
    // Constants - نفس مسارات مشروعك الحالي، ما غيرتها
    // =====================================
    const API = Object.freeze({
        health: "/health",
        platformInfo: "/api/platform-info",
        conversations: "/api/conversations",
        tenants: "/api/whatsapp/tenants",
        connectors: "/api/connectors",
        sendMessage: "/api/messages/send",
        sendTemplate: "/api/messages/send-template",
        templates: "/api/templates",
        autoReply: "/api/autoreply",
        webhookStatus: "/api/whatsapp/webhook-status"
    });

    const META_ERRORS = Object.freeze({
        131030: "الرقم غير مسجل في واتساب. تأكد من الرقم.",
        131031: "تم حظر إرسال الرسائل لهذا الرقم من قبل واتساب.",
        131047: "تجاوزت حد إرسال الرسائل المسموح. انتظر أو ارفع حد حسابك في Meta.",
        131048: "انتهت نافذة 24 ساعة. يجب إرسال قالب Template معتمد وليس رسالة عادية.",
        132000: "رقم القالب غير موجود أو غير معتمد من Meta.",
        132001: "القالب غير مكتمل أو ناقص المكونات.",
        132002: "لغة القالب غير مدعومة أو غير مطابقة للمعتمد.",
        132005: "عدد المتغيرات في القالب لا يطابق عدد القيم المرسلة.",
        190: "توكن Meta منتهي أو غير صالح. جدد Access Token من لوحة Meta.",
        80007: "تم تجاوز حد الطلبات. حاول بعد قليل.",
        130429: "تجاوزت حد معدل الإرسال في واتساب. حاول لاحقاً."
    });

    const RETRYABLE_STATUS = Object.freeze(new Set([408, 425, 429, 500, 502, 503, 504]));
    const AUTO_RETRY_METHODS = Object.freeze(new Set(["GET", "HEAD"]));

    // =====================================
    // Errors
    // =====================================
    class ApiError extends Error {
        constructor(message, status = 0, details = null, metaCode = null, requestId = null) {
            super(message);
            this.name = "ApiError";
            this.status = status;
            this.details = details;
            this.metaCode = metaCode;
            this.requestId = requestId;
            Object.setPrototypeOf(this, ApiError.prototype);
        }
    }

    // =====================================
    // Pure helpers
    // =====================================
    function getMetaErrorMessage(code, fallback) {
        if (code === null || code === undefined) return fallback;
        return META_ERRORS[code] || fallback;
    }

    function normalizeMetaLanguage(lang) {
        let l = String(lang || "ar").trim().replace(/-/g, "_");
        if (l === "ar") return "ar_EG";
        if (l === "en") return "en_US";
        return l;
    }

    function validateId(id, label = "المعرّف") {
        if (id === undefined || id === null || String(id).trim() === "") {
            throw new ApiError(`${label} مطلوب.`);
        }
        return String(id).trim();
    }

    function validateTenantId(tenantId) {
        return validateId(tenantId, "معرّف المؤسسة Tenant");
    }

    function validatePhone(to) {
        if (!to) throw new ApiError("رقم المستلم مطلوب.");
        let clean = String(to).trim().replace(/[\s\-()]/g, "");
        if (clean.startsWith("+")) clean = clean.slice(1);
        if (clean.startsWith("00")) clean = clean.slice(2);
        if (!/^[1-9]\d{6,14}$/.test(clean)) {
            throw new ApiError("رقم المستلم غير صالح. استخدم الصيغة الدولية بدون + مثال: 967770000000");
        }
        return clean;
    }

    function extractTemplateVariables(body) {
        const matches = [...String(body || "").matchAll(/\{\{(\d+)\}\}/g)];
        return [...new Set(matches.map(m => Number(m[1])))].sort((a, b) => a - b);
    }

    function validateTemplateVariables(body, parameters) {
        const vars = extractTemplateVariables(body);
        if (vars.length === 0) return;
        if (vars[vars.length - 1] > 5) {
            throw new ApiError("Meta لا تسمح بأكثر من 5 متغيرات في Body.");
        }
        if (!Array.isArray(parameters) || parameters.length < vars.length) {
            throw new ApiError(`القالب يحتاج ${vars.length} متغير، وأنت أرسلت ${parameters ? parameters.length : 0}.`);
        }
        for (let i = 0; i < vars.length; i++) {
            if (vars[i] !== i + 1) throw new ApiError("متغيرات القالب يجب أن تكون متسلسلة: {{1}} {{2}} {{3}}");
        }
    }

    function extractMetaCode(data) {
        if (!data || typeof data !== "object") return null;
        const err = data.error;
        if (err && typeof err === "object") {
            if (err.code !== undefined) return err.code;
            if (err.error && err.error.code !== undefined) return err.error.code;
        }
        return data.metaCode ?? data.code ?? null;
    }

    function extractErrorMessage(data) {
        if (!data || typeof data !== "object") return null;
        const err = data.error;
        if (typeof err === "string") return err;
        if (err && typeof err === "object") {
            if (typeof err.message === "string") return err.message;
            if (err.error && typeof err.error.message === "string") return err.error.message;
        }
        if (typeof data.message === "string") return data.message;
        if (typeof data.title === "string") return data.title;
        return null;
    }

    function generateRequestId() {
        if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
            return crypto.randomUUID();
        }
        return `req_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`;
    }

    function sleep(ms, signal) {
        return new Promise((resolve, reject) => {
            const t = setTimeout(resolve, ms);
            if (signal) {
                if (signal.aborted) { clearTimeout(t); reject(new DOMException("Aborted", "AbortError")); return; }
                signal.addEventListener("abort", () => { clearTimeout(t); reject(new DOMException("Aborted", "AbortError")); }, { once: true });
            }
        });
    }

    function backoff(attempt) {
        const base = Math.min(1000 * Math.pow(2, attempt - 1), 8000);
        return base + Math.floor(Math.random() * 300);
    }

    function resolveUrl(baseUrl, path) {
        if (!baseUrl || /^https?:\/\//i.test(path)) return path;
        const b = baseUrl.replace(/\/+$/, "");
        return b + (path.startsWith("/") ? path : "/" + path);
    }

    // تطبيع رد السيرفر: بعض النسخ ترجع { data: [...] } وبعضها ترجع [...] مباشرة
    function unwrapList(data) {
        if (Array.isArray(data)) return data;
        if (data && Array.isArray(data.data)) return data.data;
        if (data && Array.isArray(data.items)) return data.items;
        return data;
    }

    // =====================================
    // Factory - متوافق مع SnapDeploy (same-origin)
    // =====================================
    function createClient(config = {}) {
        const {
            baseUrl = "",
            timeout: defaultTimeout = 30000,
            fetchImpl = (typeof fetch !== "undefined" ? fetch : null),
            onRequest,
            onResponse,
            onError,
            onUnauthorized,
            credentials = "same-origin"
        } = config;

        if (!fetchImpl) throw new Error("fetch غير متوفر. مرر fetchImpl في إعدادات العميل.");

        const mutableHeaders = { ...(config.defaultHeaders || {}) };

        function setHeader(name, value) {
            if (value === null || value === undefined) delete mutableHeaders[name];
            else mutableHeaders[name] = value;
        }

        async function request(path, options = {}) {
            const requestId = options.requestId || generateRequestId();
            const method = (options.method || "GET").toUpperCase();
            const timeoutMs = options.timeout ?? defaultTimeout;
            const maxRetries = options.retries ?? (AUTO_RETRY_METHODS.has(method) ? 2 : 0);
            const retryOn = options.retryOn || RETRYABLE_STATUS;
            const url = resolveUrl(baseUrl, path);
            const useJson = options.body !== undefined;

            let attempt = 0;
            let authRetried = false;
            let lastError = null;

            while (attempt <= maxRetries) {
                const abortCtrl = new AbortController();
                const timer = setTimeout(() => abortCtrl.abort(), timeoutMs);

                if (options.signal) {
                    if (options.signal.aborted) {
                        clearTimeout(timer);
                        throw new ApiError("تم إلغاء الطلب.");
                    }
                    options.signal.addEventListener("abort", () => abortCtrl.abort(), { once: true });
                }

                const headers = {
                    "Accept": "application/json",
                    "X-Request-Id": requestId,
                    "X-ChatPlatform-CSRF": "1",
                    ...mutableHeaders,
                    ...(useJson ? { "Content-Type": "application/json" } : {}),
                    ...options.headers
                };

                const fetchConfig = {
                    method,
                    credentials: options.credentials || credentials,
                    headers,
                    signal: abortCtrl.signal
                };

                if (options.rawBody !== undefined) fetchConfig.body = options.rawBody;
                else if (useJson) fetchConfig.body = JSON.stringify(options.body);

                if (typeof onRequest === "function") {
                    try { await onRequest({ url, method, headers, requestId, body: options.body }); }
                    catch (e) { console.error("onRequest hook:", e); }
                }

                const start = (typeof performance !== "undefined" ? performance.now() : Date.now());
                let response;

                try {
                    response = await fetchImpl(url, fetchConfig);
                } catch (fetchErr) {
                    clearTimeout(timer);
                    if (fetchErr.name === "AbortError") {
                        if (options.signal?.aborted) throw fetchErr;
                        lastError = new ApiError("انتهت مهلة الطلب. تحقق من الاتصال وحالة المنصة.", 0, null, null, requestId);
                    } else {
                        lastError = new ApiError("تعذر الاتصال بالخادم. تحقق من اتصال الإنترنت وحالة المنصة.", 0, null, null, requestId);
                    }
                    if (attempt < maxRetries) {
                        attempt++;
                        try { await sleep(backoff(attempt), options.signal); } catch { throw lastError; }
                        continue;
                    }
                    if (typeof onError === "function") onError(lastError);
                    throw lastError;
                }

                clearTimeout(timer);
                const durationMs = (typeof performance !== "undefined" ? performance.now() : Date.now()) - start;
                const responseRequestId = response.headers.get("x-request-id") || requestId;

                const contentType = response.headers.get("content-type") || "";
                let data = null;
                if (response.status !== 204) {
                    if (contentType.includes("application/json")) {
                        try { data = await response.json(); }
                        catch { throw new ApiError("استجابة JSON غير صالحة من الخادم.", response.status, null, null, responseRequestId); }
                    } else {
                        data = await response.text();
                    }
                }

                if (typeof onResponse === "function") {
                    try { await onResponse({ url, method, status: response.status, durationMs, requestId: responseRequestId, data }); }
                    catch (e) { console.error("onResponse hook:", e); }
                }

                if (response.ok) return data;
                if (response.status === 401 && typeof window !== "undefined" && new URL(url, window.location.href).origin === window.location.origin) {
                    window.location.assign("/login.html");
                }

                const metaCode = extractMetaCode(data);
                const rawMessage = extractErrorMessage(data);
                lastError = new ApiError(
                    getMetaErrorMessage(metaCode, rawMessage || `فشل الطلب: HTTP ${response.status}`),
                    response.status, data, metaCode, responseRequestId
                );

                if (response.status === 401 && typeof onUnauthorized === "function" && !authRetried) {
                    authRetried = true;
                    try {
                        const shouldRetry = await onUnauthorized(lastError, { setHeader });
                        if (shouldRetry) continue;
                    } catch (e) { console.error("onUnauthorized hook:", e); }
                }

                if (retryOn.has(response.status) && attempt < maxRetries) {
                    let delay = backoff(attempt + 1);
                    const retryAfter = response.headers.get("retry-after");
                    if (retryAfter) {
                        const parsed = Number(retryAfter);
                        if (!Number.isNaN(parsed)) delay = Math.min(parsed * 1000, 30000);
                    }
                    attempt++;
                    try { await sleep(delay, options.signal); } catch { throw lastError; }
                    continue;
                }

                if (typeof onError === "function") onError(lastError);
                throw lastError;
            }

            if (lastError) {
                if (typeof onError === "function") onError(lastError);
                throw lastError;
            }
            throw new ApiError("فشل الطلب بعد عدة محاولات.", 0, null, null, null);
        }

        const api = {
            getHealth(opts = {}) { return request(API.health, { signal: opts.signal }); },
            getPlatformInfo(opts = {}) { return request(API.platformInfo, { signal: opts.signal }); },
            getWebhookStatus(opts = {}) { return request(API.webhookStatus, { signal: opts.signal }); },

            getConversations(opts = {}) {
                const q = new URLSearchParams();
                if (opts.page) q.set("page", String(opts.page));
                if (opts.limit) q.set("limit", String(opts.limit));
                for (const key of ["search","status","assigned"]) if (opts[key]) q.set(key, opts[key]);
                const qs = q.toString();
                return request(API.conversations + (qs ? "?" + qs : ""), { signal: opts.signal });
            },
            knowledgeList() { return request("/api/knowledge"); },
            knowledgeGet(id) { return request("/api/knowledge/"+encodeURIComponent(id)); },
            knowledgeSave(id,data) { return request("/api/knowledge"+(id?"/"+encodeURIComponent(id):""),{method:id?"PUT":"POST",body:data}); },
            knowledgeDelete(id,version) { return request("/api/knowledge/"+encodeURIComponent(id)+"?version="+encodeURIComponent(version),{method:"DELETE"}); },
            knowledgeSearch(question) { return request("/api/knowledge/search",{method:"POST",body:{question}}); },
            modelStatus() { return request("/api/knowledge/model"); },
            modelPreview(question) { return request("/api/knowledge/preview",{method:"POST",body:{question},timeout:35000,retries:0}); },
            activeBot() { return request("/api/bots/active"); },
            publishBot(id,version) { return request("/api/bots/"+encodeURIComponent(id)+"/publish",{method:"POST",body:{version}}); },
            stopBot(id,activationId) { return request("/api/bots/"+encodeURIComponent(id)+"/publish/"+encodeURIComponent(activationId),{method:"DELETE"}); },
            botRuns() { return request("/api/bots/runs"); },
            listBots() { return request("/api/bots"); },
            getBot(id) { return request("/api/bots/" + encodeURIComponent(id)); },
            saveBot(id,data) { return request("/api/bots" + (id ? "/" + encodeURIComponent(id) : ""), {method:id?"PUT":"POST",body:data}); },
            simulateBot(flow,input) { return request("/api/bots/simulate",{method:"POST",body:{flow,input}}); },
            getContacts(opts = {}) {
                const q = new URLSearchParams();
                for (const key of ["search","stage","page","limit"]) if (opts[key]) q.set(key, String(opts[key]));
                return request("/api/contacts?" + q, { signal: opts.signal });
            },
            getContact(phone) { return request("/api/contacts/" + encodeURIComponent(phone)); },
            createContact(data) { return request("/api/contacts", { method: "POST", body: data }); },
            updateContact(phone,data) { return request("/api/contacts/" + encodeURIComponent(phone), { method: "PUT", body: data }); },
            updateConversation(id, action, body = {}) {
                if (!["status","read","assign","tags"].includes(action)) throw new ApiError("Invalid action");
                return request(API.conversations + "/" + encodeURIComponent(id) + "/" + action, { method: "PUT", body });
            },
            uploadMedia(conversationId, file) {
                const data = new FormData(); data.append("file", file);
                return request("/api/media/upload/" + encodeURIComponent(conversationId), { method: "POST", rawBody: data, timeout: 45000, retries: 0 });
            },
            sendMedia(conversationId, ticket, caption, idempotencyKey) {
                return request("/api/messages/send-media", { method: "POST", body: { conversationId, ticket, caption }, headers: { "Idempotency-Key": idempotencyKey }, retries: 0 });
            },
            getConversationMessages(id, take = 100, opts = {}) {
                id = validateId(id, "معرّف المحادثة");
                const limit = Math.min(Math.max(Number(take) || 100, 1), 200);
                const q = new URLSearchParams({ take: String(limit) });
                return request(`${API.conversations}/${encodeURIComponent(id)}/messages?${q}`, { signal: opts.signal });
            },

            sendMessage(to, payloadOrText, options = {}) {
                const cleanTo = validatePhone(to);
                let payload;
                if (typeof payloadOrText === "string") {
                    const trimmed = payloadOrText.trim();
                    if (!trimmed) throw new ApiError("نص الرسالة مطلوب.");
                    if (trimmed.length > 4096) throw new ApiError("نص الرسالة تجاوز حد واتساب 4096 حرف.");
                    payload = { type: "text", text: { body: trimmed, preview_url: !!options.previewUrl } };
                } else if (payloadOrText && typeof payloadOrText === "object") {
                    if (!payloadOrText.type) throw new ApiError("نوع الرسالة (type) مطلوب.");
                    payload = payloadOrText;
                } else {
                    throw new ApiError("رقم المستلم ونص الرسالة مطلوبان.");
                }
                // ملاحظة: Idempotency-Key هنا للتوافق، والمنع الحقيقي للتكرار لازم يكون في السيرفر
                return request(API.sendMessage, {
                    method: "POST",
                    signal: options.signal,
                    timeout: options.timeout,
                    retries: options.retries ?? 0,
                    headers: { "Idempotency-Key": options.idempotencyKey || generateRequestId() },
                    body: {
                        messaging_product: "whatsapp",
                        recipient_type: "individual",
                        to: cleanTo,
                        ...payload,
                        fallbackTemplateName: options.fallbackTemplateName || null,
                        fallbackTemplateLanguage: normalizeMetaLanguage(options.fallbackTemplateLanguage || "ar"),
                        tenantId: options.tenantId || null,
                        phoneNumberId: options.phoneNumberId || null,
                        whatsAppTenantId: options.whatsAppTenantId || null
                    }
                });
            },

            sendTemplateMessage(to, templateName, language, parameters, options = {}) {
                if (language && typeof language === "object") { options = language; language = undefined; parameters = undefined; }
                language = language || "ar";
                parameters = parameters || [];
                const cleanTo = validatePhone(to);
                const name = String(templateName || "").trim();
                if (!name) throw new ApiError("اسم القالب مطلوب.");
                const langCode = normalizeMetaLanguage(language);
                const components = [];
                if (options.headerImageUrl) {
                    components.push({ type: "header", parameters: [{ type: "image", image: { link: options.headerImageUrl } }] });
                } else if (options.headerDocumentUrl) {
                    components.push({ type: "header", parameters: [{ type: "document", document: { link: options.headerDocumentUrl } }] });
                } else if (options.headerVideoUrl) {
                    components.push({ type: "header", parameters: [{ type: "video", video: { link: options.headerVideoUrl } }] });
                } else if (options.headerText) {
                    components.push({ type: "header", parameters: [{ type: "text", text: String(options.headerText) }] });
                }
                if (parameters.length > 0) {
                    components.push({ type: "body", parameters: parameters.map(p => ({ type: "text", text: String(p) })) });
                }
                if (Array.isArray(options.buttons)) {
                    options.buttons.forEach((btn, idx) => {
                        components.push({ type: "button", sub_type: btn.sub_type || "quick_reply", index: String(idx), parameters: btn.parameters || [] });
                    });
                }
                return request(API.sendTemplate, {
                    method: "POST",
                    signal: options.signal,
                    timeout: options.timeout,
                    retries: options.retries ?? 0,
                    headers: { "Idempotency-Key": options.idempotencyKey || generateRequestId() },
                    body: {
                        messaging_product: "whatsapp",
                        to: cleanTo,
                        type: "template",
                        phoneNumberId: options.phoneNumberId || null,
                        template: { name, language: { code: langCode }, components },
                        tenantId: options.tenantId || null,
                        whatsAppTenantId: options.whatsAppTenantId || null
                    }
                });
            },

            // --- Tenants / WhatsApp Accounts - بدون this لتتوافق مع whatsapp.html ---
            getWhatsAppDiagnostics(opts = {}) { return request("/api/whatsapp/diagnostics", { signal: opts.signal }); },
            getTeam(opts = {}) { return request("/api/team", { signal: opts.signal }); },
            createAgent(data) { return request("/api/team", { method: "POST", body: data }); },
            setAgentActive(username, isActive) { return request("/api/team/" + encodeURIComponent(username) + "/active", { method: "PUT", body: { isActive } }); },
            resetAgentPassword(username, password) { return request("/api/team/" + encodeURIComponent(username) + "/password", { method: "PUT", body: { password } }); },
            getIdentity(opts = {}) { return request("/api/auth/me", { signal: opts.signal }); },
            logout() { return request("/api/auth/logout", { method: "POST" }); },
            async getTenants(opts = {}) {
                const identity = await request("/api/auth/me", { signal: opts.signal });
                return [{ id: identity.tenantId, tenantId: identity.tenantId, name: identity.tenantId }];
            },
            createTenant(data, opts = {}) {
                if (!data || typeof data !== "object") throw new ApiError("بيانات حساب واتساب غير صالحة.");
                return request(API.tenants, { method: "POST", body: data, signal: opts.signal });
            },
            getWhatsAppAccount(id, opts = {}) {
                id = validateId(id, "معرّف حساب واتساب");
                return request(`${API.tenants}/${encodeURIComponent(id)}`, { signal: opts.signal });
            },
            updateWhatsAppAccount(id, data, opts = {}) {
                id = validateId(id, "معرّف حساب واتساب");
                return request(`${API.tenants}/${encodeURIComponent(id)}`, { method: "PUT", body: data, signal: opts.signal });
            },
            deleteWhatsAppAccount(id, opts = {}) {
                id = validateId(id, "معرّف حساب واتساب");
                return request("/api/whatsapp/accounts/" + encodeURIComponent(id), { method: "DELETE", signal: opts.signal });
            },
            getWhatsAppAccounts(opts = {}) { return request("/api/whatsapp/accounts", { signal: opts.signal }); },
            getWhatsAppTenants(opts = {}) { return api.getWhatsAppAccounts(opts); },
            createWhatsAppAccount(data, opts = {}) { return request("/api/whatsapp/accounts", { method: "POST", body: data, signal: opts.signal }); },
            validateMetaCredentials(data, opts = {}) { return request("/api/whatsapp/validate-credentials", { method: "POST", body: data, signal: opts.signal }); },
            updateWhatsAppToken(id, data, opts = {}) { return request("/api/whatsapp/accounts/" + encodeURIComponent(validateId(id)) + "/token", { method: "PUT", body: data, signal: opts.signal }); },

            getConnectors(opts = {}) { return request(API.connectors, { signal: opts.signal }); },
            createConnector(data, opts = {}) {
                if (!data || typeof data !== "object") throw new ApiError("بيانات التكامل غير صالحة.");
                return request(API.connectors, { method: "POST", body: data, signal: opts.signal });
            },
            deleteConnector(id, opts = {}) {
                id = validateId(id, "معرّف التكامل");
                return request(`${API.connectors}/${encodeURIComponent(id)}`, { method: "DELETE", signal: opts.signal });
            },

            getTemplates(tenantId, whatsAppTenantId = null, opts = {}) {
                // توافق: getTemplates(tenantId, { signal }) أو getTemplates(tenantId, whatsAppTenantId, { signal })
                if (whatsAppTenantId && typeof whatsAppTenantId === "object") { opts = whatsAppTenantId; whatsAppTenantId = null; }
                tenantId = validateTenantId(tenantId);
                const q = new URLSearchParams({ tenantId });
                if (whatsAppTenantId) q.set("whatsAppTenantId", validateId(whatsAppTenantId, "معرّف حساب واتساب"));
                return request(`${API.templates}?${q}`, { signal: opts.signal });
            },
            createTemplate(data, opts = {}) {
                if (!data || typeof data !== "object") throw new ApiError("بيانات القالب غير صالحة.");
                const tenantId = validateTenantId(data.tenantId);
                const whatsAppTenantId = validateId(data.whatsAppTenantId, "معرّف حساب واتساب");
                const name = String(data.name || "").trim().toLowerCase().replace(/\s+/g, "_");
                const body = String(data.body || "").trim();
                const category = String(data.category || "UTILITY").toUpperCase();
                const language = normalizeMetaLanguage(data.language || "ar");
                if (!name || !body) throw new ApiError("اسم القالب ونص القالب مطلوبان.");
                if (!/^[a-z0-9_]+$/.test(name)) throw new ApiError("اسم القالب في Meta يجب أن يكون حروفاً صغيرة وأرقاماً و _ فقط.");
                if (name.length > 512 || body.length > 1024) throw new ApiError("تجاوز اسم القالب أو نصه الحد المسموح في Meta.");
                if (!["UTILITY", "MARKETING", "AUTHENTICATION"].includes(category)) throw new ApiError("تصنيف القالب غير صالح.");
                const exampleParams = (Array.isArray(data.exampleParameters) && data.exampleParameters.length > 0) ? data.exampleParameters : extractTemplateVariables(body).map(() => "مثال");
                validateTemplateVariables(body, exampleParams);
                const components = [];
                if (data.headerText || data.headerImageUrl || data.headerMediaHandle) {
                    const header = { type: "HEADER" };
                    if (data.headerMediaHandle) {
                        header.format = "IMAGE";
                        header.example = { header_handle: [data.headerMediaHandle] };
                    } else if (data.headerImageUrl) {
                        header.format = "IMAGE";
                    } else {
                        header.format = "TEXT";
                        header.text = String(data.headerText).slice(0, 60);
                    }
                    components.push(header);
                }
                const bodyComponent = { type: "BODY", text: body };
                if (exampleParams.length > 0) bodyComponent.example = { body_text: [exampleParams] };
                components.push(bodyComponent);
                if (data.footerText) components.push({ type: "FOOTER", text: String(data.footerText).slice(0, 60) });
                if (Array.isArray(data.buttons) && data.buttons.length > 0) components.push({ type: "BUTTONS", buttons: data.buttons });
                return request(API.templates, {
                    method: "POST",
                    signal: opts.signal,
                    body: { tenantId, whatsAppTenantId, name, category, language, body, components, headerText: data.headerText || null, footerText: data.footerText || null, buttons: data.buttons || [] }
                });
            },
            getTemplate(id, tenantId, opts = {}) {
                id = validateId(id, "معرّف القالب");
                tenantId = validateTenantId(tenantId);
                const q = new URLSearchParams({ tenantId });
                return request(`${API.templates}/${encodeURIComponent(id)}?${q}`, { signal: opts.signal });
            },
            deleteTemplate(id, tenantId, opts = {}) {
                id = validateId(id, "معرّف القالب");
                tenantId = validateTenantId(tenantId);
                const q = new URLSearchParams({ tenantId });
                return request(`${API.templates}/${encodeURIComponent(id)}?${q}`, { method: "DELETE", signal: opts.signal });
            },

            getAutoReplyRules(tenantId = "default", opts = {}) {
                if (tenantId && typeof tenantId === "object") { opts = tenantId; tenantId = "default"; }
                const q = new URLSearchParams({ tenantId: validateTenantId(tenantId) });
                return request(`${API.autoReply}?${q}`, { signal: opts.signal });
            },
            createAutoReplyRule(data, opts = {}) {
                if (!data || typeof data !== "object") throw new ApiError("بيانات قاعدة الرد الآلي غير صالحة.");
                return request(API.autoReply, { method: "POST", body: data, signal: opts.signal });
            },
            updateAutoReplyRule(id, data, opts = {}) {
                id = validateId(id, "معرّف قاعدة الرد الآلي");
                if (!data || typeof data !== "object") throw new ApiError("بيانات قاعدة الرد الآلي غير صالحة.");
                return request(`${API.autoReply}/${encodeURIComponent(id)}`, { method: "PUT", body: data, signal: opts.signal });
            },
            toggleAutoReplyRule(id, tenantId = "default", opts = {}) {
                id = validateId(id, "معرّف قاعدة الرد الآلي");
                const q = new URLSearchParams({ tenantId: validateTenantId(tenantId) });
                return request(`${API.autoReply}/${encodeURIComponent(id)}/toggle?${q}`, { method: "PATCH", signal: opts.signal });
            },
            deleteAutoReplyRule(id, tenantId = "default", opts = {}) {
                id = validateId(id, "معرّف قاعدة الرد الآلي");
                const q = new URLSearchParams({ tenantId: validateTenantId(tenantId) });
                return request(`${API.autoReply}/${encodeURIComponent(id)}?${q}`, { method: "DELETE", signal: opts.signal });
            },

            helpers: Object.freeze({ validatePhone, extractTemplateVariables, getMetaErrorMessage, normalizeMetaLanguage }),
            _helpers: Object.freeze({ validatePhone, extractTemplateVariables, getMetaErrorMessage, normalizeMetaLanguage })
        };

        // كشف المصنع من نفس الكائن للتوافق
        api.createClient = createClient;
        api.setHeader = setHeader;

        return Object.freeze(api);
    }

    const defaultClient = createClient();

    if (typeof window !== "undefined") {
        window.ChatPlatformAPI = defaultClient;
        window.ChatPlatformApiError = ApiError;
        window.ChatPlatformClient = { createClient, ApiError };
        // All pages loading the shared API client obtain the user's identity from the server.
        document.addEventListener("DOMContentLoaded", async () => {
            try {
                const identity = await defaultClient.getIdentity();
                const bar = document.createElement("div");
                bar.setAttribute("dir", "rtl");
                bar.style.cssText = "padding:10px 16px;background:#f0f7f4;color:#173b32;display:flex;align-items:center;gap:12px;flex-wrap:wrap";
                const label = document.createElement("span");
                label.textContent = identity.username + " | " + identity.tenantId + " | " + (identity.role === "Admin" ? "مدير" : "موظف");
                const logout = document.createElement("button");
                logout.textContent = "تسجيل الخروج";
                logout.addEventListener("click", async () => {
                    try { await defaultClient.logout(); location.assign("/login.html"); }
                    catch { label.textContent = "تعذر تسجيل الخروج؛ حاول مجددًا."; }
                });
                bar.append(label);
                if (identity.role === "Admin") {
                    const link = document.createElement("a"); link.href = "/team.html"; link.textContent = "إدارة الفريق"; bar.append(link);
                }
                bar.append(logout); document.body.prepend(bar);
            } catch (err) {
                if (err.status !== 401) console.warn("Unable to verify session");
            }
        }, { once: true });
    }
    if (typeof module !== "undefined" && module.exports) {
        module.exports = { createClient, ApiError, defaultClient };
    }
})();
