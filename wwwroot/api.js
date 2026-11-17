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

    // =====================================
    // META WHATSAPP ERROR MAP - ترجمة أخطاء ميتا
    // =====================================
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

    // =====================================
    // META: التحقق من رقم الهاتف بصيغة E.164
    // Meta ترفض الأرقام التي فيها + أو مسافات أو 00
    // الصحيح: 9677xxxxxxxx بدون +
    // =====================================
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

    // =====================================
    // META: التحقق من متغيرات القالب {{1}} {{2}}
    // =====================================
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
        // يجب أن تكون المتغيرات متسلسلة 1,2,3
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
            // محاولة قراءة خطأ Meta من داخل السيرفر
            // الشكل المتوقع من Meta: { error: { code, message } }
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

        // CONVERSATIONS
        getConversations() { return request(API.conversations); },

        getConversationMessages(id, take = 100) {
            id = validateId(id, "معرّف المحادثة");
            const limit = Math.min(Math.max(Number(take) || 100, 1), 200);
            const query = new URLSearchParams({ take: String(limit) });
            return request(`${API.conversations}/${encodeURIComponent(id)}/messages?${query}`);
        },

        // =====================================
        // SEND WHATSAPP MESSAGE - متوافق مع Meta
        // =====================================
        // يرسل: messaging_product + recipient_type + type
        // ملاحظة: فحص نافذة 24 ساعة يتم في السيرفر، هنا نرسل isTemplateFallback للسماح بالتبديل
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
                    // إذا انتهت نافذة 24 ساعة، السيرفر يحولها لقالب تلقائياً إذا أرسلت هذا
                    fallbackTemplateName: options.fallbackTemplateName || null,
                    fallbackTemplateLanguage: options.fallbackTemplateLanguage || "ar",
                    tenantId: options.tenantId || null,
                    whatsAppTenantId: options.whatsAppTenantId || null
                }
            });
        },

        // =====================================
        // SEND TEMPLATE MESSAGE - إرسال قالب معتمد من Meta
        // هذا هو الإرسال الصحيح بعد انتهاء 24 ساعة
        // =====================================
        sendTemplateMessage(to, templateName, language = "ar", parameters = [], options = {}) {
            const cleanTo = validatePhone(to);
            const name = String(templateName || "").trim();
            if (!name) throw new ApiError("اسم القالب مطلوب.");

            // لغة Meta تكون ar أو ar_EG أو en_US - نسمح بـ ar ونحولها لـ ar_EG في السيرفر إذا لزم
            const langCode = String(language || "ar").trim();

            // تحويل المتغيرات لصيغة Meta: [{type:"text", text:"..."}]
            const components = [{
                type: "body",
                parameters: parameters.map(p => ({ type: "text", text: String(p) }))
            }];

            // دعم Header إذا كان فيه صورة أو نص
            if (options.headerText) {
                components.unshift({ type: "header", parameters: [{ type: "text", text: String(options.headerText) }] });
            }
            if (options.headerImageUrl) {
                components.unshift({ type: "header", parameters: [{ type: "image", image: { link: options.headerImageUrl } }] });
            }

            // دعم أزرار CTA / Quick Reply
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
                    template: {
                        name,
                        language: { code: langCode },
                        components
                    },
                    tenantId: options.tenantId || null,
                    whatsAppTenantId: options.whatsAppTenantId || null
                }
            });
        },

        // TENANTS
        getTenants() { return request(API.tenants); },
        createTenant(data) {
            if (!data || typeof data !== "object") throw new ApiError("بيانات حساب واتساب غير صالحة.");
            return request(API.tenants, { method: "POST", body: data });
        },

        // CONNECTORS
        getConnectors() { return request(API.connectors); },
        createConnector(data) {
            if (!data || typeof data !== "object") throw new ApiError("بيانات التكامل غير صالحة.");
            return request(API.connectors, { method: "POST", body: data });
        },
        deleteConnector(id) {
            id = validateId(id, "معرّف التكامل");
            return request(`${API.connectors}/${encodeURIComponent(id)}`, { method: "DELETE" });
        },

        // =====================================
        // TEMPLATES - مطابق لمواصفات Meta
        // =====================================
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
            // Meta: ar يجب أن يكون ar_EG أو ar_SA أو ar ... نرسل ar و السيرفر يحوله
            const language = String(data.language || "ar").trim();

            if (!name || !body) throw new ApiError("اسم القالب ونص القالب مطلوبان.");

            // اسم قالب Meta: حروف صغيرة وأرقام و _ فقط، بدون مسافات
            if (!/^[a-z0-9_]+$/.test(name)) {
                throw new ApiError("اسم القالب في Meta يجب أن يكون حروف صغيرة وأرقام و _ فقط، بدون مسافات أو حروف عربية.");
            }
            if (name.length > 512 || body.length > 1024) {
                throw new ApiError("تجاوز اسم القالب أو نصه الحد المسموح في Meta: الاسم 512، النص 1024 حرف للـ Body.");
            }
            if (!["UTILITY", "MARKETING", "AUTHENTICATION"].includes(category)) {
                throw new ApiError("تصنيف القالب غير صالح.");
            }

            // تحقق من المتغيرات
            const parameters = Array.isArray(data.exampleParameters) ? data.exampleParameters : [];
            validateTemplateVariables(body, parameters.length > 0 ? parameters : extractTemplateVariables(body).map(() => "مثال"));

            // بناء مكونات Meta الحقيقية
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

        // AUTO REPLY
        getAutoReplyRules() { return request(API.autoReply); },
        createAutoReplyRule(data) {
            if (!data || typeof data !== "object") throw new ApiError("بيانات قاعدة الرد الآلي غير صالحة.");
            return request(API.autoReply, { method: "POST", body: data });
        },

        // أدوات مساعدة للمطور
        _helpers: Object.freeze({
            validatePhone,
            extractTemplateVariables,
            getMetaErrorMessage
        })
    });

    window.ChatPlatformAPI = api;
    window.ChatPlatformApiError = ApiError;

})();
