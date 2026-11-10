"use strict";

(() => {

    const API = Object.freeze({
        health: "/health",
        platformInfo: "/api/platform-info",

        conversations: "/api/conversations",

        tenants: "/api/whatsapp/tenants",

        connectors: "/api/connectors",

        sendMessage: "/api/messages/send",

        templates: "/api/templates",

        autoReply: "/api/autoreply"
    });

    class ApiError extends Error {
        constructor(message, status = 0, details = null) {
            super(message);
            this.name = "ApiError";
            this.status = status;
            this.details = details;
        }
    }

    function validateId(id, label = "المعرّف") {
        if (
            id === undefined ||
            id === null ||
            String(id).trim() === ""
        ) {
            throw new ApiError(`${label} مطلوب.`);
        }

        return String(id).trim();
    }

    function validateTenantId(tenantId) {
        return validateId(tenantId, "معرّف المؤسسة Tenant");
    }

    async function request(path, options = {}) {

        const controller = options.controller;

        const config = {
            method: options.method || "GET",

            credentials: "same-origin",

            headers: {
                "Accept": "application/json",

                ...(options.body !== undefined
                    ? { "Content-Type": "application/json" }
                    : {}),

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

            if (error.name === "AbortError") {
                throw error;
            }

            throw new ApiError(
                "تعذر الاتصال بالخادم. تحقق من اتصال الإنترنت وحالة المنصة."
            );
        }

        const contentType =
            response.headers.get("content-type") || "";

        let data = null;

        if (response.status !== 204) {

            if (contentType.includes("application/json")) {

                try {
                    data = await response.json();

                } catch {
                    throw new ApiError(
                        "استجابة JSON غير صالحة من الخادم.",
                        response.status
                    );
                }

            } else {

                data = await response.text();
            }
        }

        if (!response.ok) {

            const message =
                data && typeof data === "object"
                    ? data.error || data.message || data.title
                    : typeof data === "string" && data.trim()
                        ? data
                        : null;

            throw new ApiError(
                message || `فشل الطلب: HTTP ${response.status}`,
                response.status,
                data
            );
        }

        return data;
    }

    const api = Object.freeze({

        // =====================================
        // HEALTH AND PLATFORM
        // =====================================

        getHealth() {
            return request(API.health);
        },

        getPlatformInfo() {
            return request(API.platformInfo);
        },


        // =====================================
        // CONVERSATIONS
        // =====================================

        getConversations() {
            return request(API.conversations);
        },

        getConversationMessages(id, take = 100) {

            id = validateId(id, "معرّف المحادثة");

            const limit = Math.min(
                Math.max(Number(take) || 100, 1),
                200
            );

            const query = new URLSearchParams({
                take: String(limit)
            });

            return request(
                `${API.conversations}/${encodeURIComponent(id)}/messages?${query}`
            );
        },


        // =====================================
        // SEND WHATSAPP MESSAGE
        // =====================================

        sendMessage(to, text) {

            if (
                !to ||
                typeof text !== "string" ||
                !text.trim()
            ) {
                throw new ApiError(
                    "رقم المستلم ونص الرسالة مطلوبان."
                );
            }

            return request(API.sendMessage, {
                method: "POST",

                body: {
                    to: String(to).trim(),
                    text: text.trim()
                }
            });
        },


        // =====================================
        // WHATSAPP TENANTS
        // =====================================

        getTenants() {
            return request(API.tenants);
        },

        createTenant(data) {

            if (!data || typeof data !== "object") {
                throw new ApiError("بيانات حساب واتساب غير صالحة.");
            }

            return request(API.tenants, {
                method: "POST",
                body: data
            });
        },


        // =====================================
        // CONNECTORS
        // =====================================

        getConnectors() {
            return request(API.connectors);
        },

        createConnector(data) {

            if (!data || typeof data !== "object") {
                throw new ApiError("بيانات التكامل غير صالحة.");
            }

            return request(API.connectors, {
                method: "POST",
                body: data
            });
        },

        deleteConnector(id) {

            id = validateId(id, "معرّف التكامل");

            return request(
                `${API.connectors}/${encodeURIComponent(id)}`,
                {
                    method: "DELETE"
                }
            );
        },


        // =====================================
        // LOCAL WHATSAPP TEMPLATES
        // =====================================

        // عرض قوالب مؤسسة محددة
        // GET /api/templates?tenantId=...&whatsAppTenantId=...

        getTemplates(tenantId, whatsAppTenantId = null) {

            tenantId = validateTenantId(tenantId);

            const query = new URLSearchParams({
                tenantId
            });

            if (whatsAppTenantId) {

                query.set(
                    "whatsAppTenantId",
                    validateId(
                        whatsAppTenantId,
                        "معرّف حساب واتساب"
                    )
                );
            }

            return request(
                `${API.templates}?${query.toString()}`
            );
        },


        // إنشاء قالب محلي مرتبط بمؤسسة وحساب واتساب
        // POST /api/templates

        createTemplate(data) {

            if (!data || typeof data !== "object") {
                throw new ApiError("بيانات القالب غير صالحة.");
            }

            const tenantId = validateTenantId(data.tenantId);

            const whatsAppTenantId = validateId(
                data.whatsAppTenantId,
                "معرّف حساب واتساب"
            );

            const name = String(data.name || "").trim();

            const body = String(data.body || "").trim();

            const category = String(
                data.category || "UTILITY"
            ).toUpperCase();

            const language = String(
                data.language || "ar"
            ).trim();

            if (!name || !body) {
                throw new ApiError(
                    "اسم القالب ونص القالب مطلوبان."
                );
            }

            if (name.length > 100 || body.length > 4000) {
                throw new ApiError(
                    "تجاوز اسم القالب أو نصه الحد المسموح."
                );
            }

            if (
                !["UTILITY", "MARKETING", "AUTHENTICATION"]
                    .includes(category)
            ) {
                throw new ApiError(
                    "تصنيف القالب غير صالح."
                );
            }

            return request(API.templates, {
                method: "POST",

                body: {
                    tenantId,
                    whatsAppTenantId,
                    name,
                    category,
                    language,
                    body
                }
            });
        },


        // عرض قالب واحد
        // GET /api/templates/{id}?tenantId=...

        getTemplate(id, tenantId) {

            id = validateId(id, "معرّف القالب");

            tenantId = validateTenantId(tenantId);

            const query = new URLSearchParams({
                tenantId
            });

            return request(
                `${API.templates}/${encodeURIComponent(id)}?${query.toString()}`
            );
        },


        // تعطيل قالب محلي
        // DELETE /api/templates/{id}?tenantId=...

        deleteTemplate(id, tenantId) {

            id = validateId(id, "معرّف القالب");

            tenantId = validateTenantId(tenantId);

            const query = new URLSearchParams({
                tenantId
            });

            return request(
                `${API.templates}/${encodeURIComponent(id)}?${query.toString()}`,
                {
                    method: "DELETE"
                }
            );
        },


        // =====================================
        // AUTO REPLY
        // =====================================

        getAutoReplyRules() {
            return request(API.autoReply);
        },

        createAutoReplyRule(data) {

            if (!data || typeof data !== "object") {
                throw new ApiError("بيانات قاعدة الرد الآلي غير صالحة.");
            }

            return request(API.autoReply, {
                method: "POST",
                body: data
            });
        }

    });

    // =====================================
    // GLOBAL EXPORT
    // =====================================

    window.ChatPlatformAPI = api;

    window.ChatPlatformApiError = ApiError;

})();
