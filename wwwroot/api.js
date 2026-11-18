"use strict";

(() => {

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
        80007: "تم تجاوز حد الطلبات. حاول بعد قليل."
    });

    function getMetaErrorMessage(code, fallback) {
        return META_ERRORS[code] || fallback;
    }

    // =====================================
    // META v26.0: تحويل اللغة للصيغة الكاملة
    // v26.0 يرفض ar لوحدها، لازم ar_EG
    // =====================================
    function normalizeMetaLanguage(lang) {
        const l = String(lang || "ar").trim();
        if (l === "ar") return "ar_EG";
        if (l === "en") return "en_US";
        if (l === "ar_SA" || l === "ar_EG" || l === "ar_AE" || l === "ar_QA" || l === "ar_KW" || l === "en_US" || l === "en_GB") return l;
        // إذا كانت اللغة مختصرة بدون _ ، نرجعها كما هي والسيرفر يتصرف
        return l;
    }

    class ApiError extends Error {
        constructor(message, status = 0, details = null, metaCode = null) {
            super(message);
            this.name = "ApiError";
            this.status = status;
            this.details = details;
            this.metaCode = metaCode;
        }
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
        if (!/^\d{7,15}$/.test(clean)) {
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
        if (!Array.isArray(parameters) || parameters.length < vars.length) {
            throw new ApiError(`القالب يحتاج ${vars.length} متغير، وأنت أرسلت ${parameters ? parameters.length : 0}.`);
        }
        for (let i = 0; i < vars.length; i++) {
            if (vars[i] !== i + 1) throw new ApiError("متغيرات القالب يجب أن تكون متسلسلة: {{1}} {{2}} {{3}}");
        }
    }

    async function request(path, options = {}) {
        const controller = options.controller;
        const config = {
            method: options.method || "GET",
            credentials: "same-origin",
            headers: {
                "Accept": "application/json",
                ...(options.body !== undefined ? { "Content-Type": "application/json" } : {}),
                ...options.headers
            },
            signal: options.signal || controller?.signal
        };

        if (options.body !== undefined) {
            config.body = JSON.stringify(options.body);
        }

        let response;
        try {
            response = await fetch(path, config);
        } catch (error) {
            if (error.name === "AbortError") throw error;
            throw new ApiError("تعذر الاتصال بالخادم. تحقق من اتصال الإنترنت وحالة المنصة.");
        }

        const contentType = response.headers.get("content-type") || "";
        let data = null;

        if (response.status !== 204) {
            if (contentType.includes("application/json")) {
                try { data = await response.json(); }
                catch { throw new ApiError("استجابة JSON غير صالحة من الخادم.", response.status); }
            } else {
                data = await response.text();
            }
        }

        if (!response.ok) {
            const metaError = data?.error?.error || data?.error || data?.metaError || null;
            const metaCode = metaError?.code || data?.metaCode || data?.code || null;
            const rawMessage = metaError?.message || (data && typeof data === "object" ? data.error || data.message || data.title : null);
            const message = getMetaErrorMessage(metaCode, rawMessage || `فشل الطلب: HTTP ${response.status}`);
            throw new ApiError(message, response.status, data, metaCode);
        }

        return data;
    }

    const api = Object.freeze({

        getHealth() { return request(API.health); },
        getPlatformInfo() { return request(API.platformInfo); },
        getWebhookStatus() { return request(API.webhookStatus); },

        getConversations() { return request(API.conversations); },

        getConversationMessages(id, take = 100) {
            id = validateId(id, "معرّف المحادثة");
            const limit = Math.min(Math.max(Number(take) || 100, 1), 200);
            const query = new URLSearchParams({ take: String(limit) });
            return request(`${API.conversations}/${encodeURIComponent(id)}/messages?${query}`);
        },

        sendMessage(to, text, options = {}) {
            const cleanTo = validatePhone(to);
            if (!text || typeof text !== "string" || !text.trim()) {
                throw new ApiError("رقم المستلم ونص الرسالة مطلوبان.");
            }
            if (text.trim().length > 4096) {
                throw new ApiError("نص الرسالة تجاوز حد واتساب 4096 حرف.");
            }
            return request(API.sendMessage, {
                method: "POST",
                body: {
                    messaging_product: "whatsapp",
                    recipient_type: "individual",
                    to: cleanTo,
                    type: "text",
                    text: { body: text.trim(), preview_url: !!options.previewUrl },
                    fallbackTemplateName: options.fallbackTemplateName || null,
                    fallbackTemplateLanguage: normalizeMetaLanguage(options.fallbackTemplateLanguage || "ar"),
                    tenantId: options.tenantId || null,
                    whatsAppTenantId: options.whatsAppTenantId || null
                }
            });
        },

        sendTemplateMessage(to, templateName, language = "ar", parameters = [], options = {}) {
            const cleanTo = validatePhone(to);
            const name = String(templateName || "").trim();
            if (!name) throw new ApiError("اسم القالب مطلوب.");
            const langCode = normalizeMetaLanguage(language);
            const components = [{
                type: "body",
                parameters: parameters.map(p => ({ type: "text", text: String(p) }))
            }];
            if (options.headerText) {
                components.unshift({ type: "header", parameters: [{ type: "text", text: String(options.headerText) }] });
            }
            if (options.headerImageUrl) {
                components.unshift({ type: "header", parameters: [{ type: "image", image: { link: options.headerImageUrl } }] });
            }
            if (options.buttons && Array.isArray(options.buttons) && options.buttons.length > 0) {
                options.buttons.forEach((btn, idx) => {
                    components.push({ type: "button", sub_type: btn.sub_type || "quick_reply", index: String(idx), parameters: btn.parameters || [] });
                });
            }
            return request(API.sendTemplate, {
                method: "POST",
                body: {
                    messaging_product: "whatsapp",
                    to: cleanTo,
                    type: "template",
                    template: { name, language: { code: langCode }, components },
                    tenantId: options.tenantId || null,
                    whatsAppTenantId: options.whatsAppTenantId || null
                }
            });
        },

        getTenants() { return request(API.tenants); },
        createTenant(data) {
            if (!data || typeof data !== "object") throw new ApiError("بيانات حساب واتساب غير صالحة.");
            return request(API.tenants, { method: "POST", body: data });
        },

        getConnectors() { return request(API.connectors); },
        createConnector(data) {
            if (!data || typeof data !== "object") throw new ApiError("بيانات التكامل غير صالحة.");
            return request(API.connectors, { method: "POST", body: data });
        },
        deleteConnector(id) {
            id = validateId(id, "معرّف التكامل");
            return request(`${API.connectors}/${encodeURIComponent(id)}`, { method: "DELETE" });
        },

        getTemplates(tenantId, whatsAppTenantId = null) {
            tenantId = validateTenantId(tenantId);
            const query = new URLSearchParams({ tenantId });
            if (whatsAppTenantId) query.set("whatsAppTenantId", validateId(whatsAppTenantId, "معرّف حساب واتساب"));
            return request(`${API.templates}?${query.toString()}`);
        },

        createTemplate(data) {
            if (!data || typeof data !== "object") throw new ApiError("بيانات القالب غير صالحة.");
            const tenantId = validateTenantId(data.tenantId);
            const whatsAppTenantId = validateId(data.whatsAppTenantId, "معرّف حساب واتساب");
            const name = String(data.name || "").trim().toLowerCase().replace(/\s+/g, "_");
            const body = String(data.body || "").trim();
            const category = String(data.category || "UTILITY").toUpperCase();
            const language = normalizeMetaLanguage(data.language || "ar");
            if (!name || !body) throw new ApiError("اسم القالب ونص القالب مطلوبان.");
            if (!/^[a-z0-9_]+$/.test(name)) {
                throw new ApiError("اسم القالب في Meta يجب أن يكون حروف صغيرة وأرقام و _ فقط، بدون مسافات أو حروف عربية.");
            }
            if (name.length > 512 || body.length > 1024) {
                throw new ApiError("تجاوز اسم القالب أو نصه الحد المسموح في Meta: الاسم 512، النص 1024 حرف للـ Body.");
            }
            if (!["UTILITY", "MARKETING", "AUTHENTICATION"].includes(category)) {
                throw new ApiError("تصنيف القالب غير صالح.");
            }
            const parameters = Array.isArray(data.exampleParameters) ? data.exampleParameters : [];
            validateTemplateVariables(body, parameters.length > 0 ? parameters : extractTemplateVariables(body).map(() => "مثال"));
            const components = [];
            if (data.headerText || data.headerImageUrl) {
                components.push({
                    type: "HEADER",
                    format: data.headerImageUrl ? "IMAGE" : "TEXT",
                    text: data.headerText || undefined,
                    example: data.headerImageUrl ? { header_handle: [data.headerImageUrl] } : undefined
                });
            }
            components.push({
                type: "BODY",
                text: body,
                example: { body_text: [parameters.length > 0 ? parameters : extractTemplateVariables(body).map(() => "مثال")] }
            });
            if (data.footerText) components.push({ type: "FOOTER", text: String(data.footerText).slice(0, 60) });
            if (data.buttons && Array.isArray(data.buttons)) components.push({ type: "BUTTONS", buttons: data.buttons });
            return request(API.templates, {
                method: "POST",
                body: { tenantId, whatsAppTenantId, name, category, language, body, components, headerText: data.headerText || null, footerText: data.footerText || null, buttons: data.buttons || [] }
            });
        },

        getTemplate(id, tenantId) {
            id = validateId(id, "معرّف القالب");
            tenantId = validateTenantId(tenantId);
            const query = new URLSearchParams({ tenantId });
            return request(`${API.templates}/${encodeURIComponent(id)}?${query.toString()}`);
        },

        deleteTemplate(id, tenantId) {
            id = validateId(id, "معرّف القالب");
            tenantId = validateTenantId(tenantId);
            const query = new URLSearchParams({ tenantId });
            return request(`${API.templates}/${encodeURIComponent(id)}?${query.toString()}`, { method: "DELETE" });
        },

        getAutoReplyRules(tenantId = "default") {
            const query = new URLSearchParams({
                tenantId: validateTenantId(tenantId)
            });

            return request(
                `${API.autoReply}?${query.toString()}`
            );
        },

        createAutoReplyRule(data) {
            if (!data || typeof data !== "object") {
                throw new ApiError(
                    "بيانات قاعدة الرد الآلي غير صالحة."
                );
            }

            return request(API.autoReply, {
                method: "POST",
                body: data
            });
        },

        updateAutoReplyRule(id, data) {
            id = validateId(
                id,
                "معرّف قاعدة الرد الآلي"
            );

            if (!data || typeof data !== "object") {
                throw new ApiError(
                    "بيانات قاعدة الرد الآلي غير صالحة."
                );
            }

            return request(
                `${API.autoReply}/${encodeURIComponent(id)}`,
                {
                    method: "PUT",
                    body: data
                }
            );
        },

        toggleAutoReplyRule(
            id,
            tenantId = "default"
        ) {
            id = validateId(
                id,
                "معرّف قاعدة الرد الآلي"
            );

            const query = new URLSearchParams({
                tenantId: validateTenantId(tenantId)
            });

            return request(
                `${API.autoReply}/${encodeURIComponent(id)}/toggle?${query.toString()}`,
                {
                    method: "PATCH"
                }
            );
        },

        deleteAutoReplyRule(
            id,
            tenantId = "default"
        ) {
            id = validateId(
                id,
                "معرّف قاعدة الرد الآلي"
            );

            const query = new URLSearchParams({
                tenantId: validateTenantId(tenantId)
            });

            return request(
                `${API.autoReply}/${encodeURIComponent(id)}?${query.toString()}`,
                {
                    method: "DELETE"
                }
            );
        },

        _helpers: Object.freeze({
            validatePhone,
            extractTemplateVariables,
            getMetaErrorMessage,
            normalizeMetaLanguage
        })
    });

    window.ChatPlatformAPI = api;
    window.ChatPlatformApiError = ApiError;

})();
