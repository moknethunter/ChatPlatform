"use strict";

(() => {
    const API = Object.freeze({
        health: "/health",
        platformInfo: "/api/platform-info",
        conversations: "/api/conversations",
        tenants: "/api/whatsapp/tenants",
        connectors: "/api/connectors",
        sendMessage: "/api/messages/send"
    });

    class ApiError extends Error {
        constructor(message, status = 0, details = null) {
            super(message);
            this.name = "ApiError";
            this.status = status;
            this.details = details;
        }
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
                    ? data.error || data.message
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

        getHealth() {
            return request(API.health);
        },

        getPlatformInfo() {
            return request(API.platformInfo);
        },

        getConversations() {
            return request(API.conversations);
        },

        getConversationMessages(id, take = 100) {
            if (!id) {
                throw new ApiError("معرّف المحادثة مطلوب.");
            }

            const query = new URLSearchParams({
                take: String(Math.min(Math.max(take, 1), 200))
            });

            return request(
                `${API.conversations}/${encodeURIComponent(id)}/messages?${query}`
            );
        },

        sendMessage(to, text) {
            if (!to || !text || !text.trim()) {
                throw new ApiError(
                    "رقم المستلم ونص الرسالة مطلوبان."
                );
            }

            return request(API.sendMessage, {
                method: "POST",
                body: {
                    to: to.trim(),
                    text: text.trim()
                }
            });
        },

        getTenants() {
            return request(API.tenants);
        },

        createTenant(data) {
            return request(API.tenants, {
                method: "POST",
                body: data
            });
        },

        getConnectors() {
            return request(API.connectors);
        },

        createConnector(data) {
            return request(API.connectors, {
                method: "POST",
                body: data
            });
        },

        deleteConnector(id) {
            if (!id) {
                throw new ApiError("معرّف التكامل مطلوب.");
            }

            return request(
                `${API.connectors}/${encodeURIComponent(id)}`,
                { method: "DELETE" }
            );
        }
    });

    window.ChatPlatformAPI = api;
    window.ChatPlatformApiError = ApiError;

})();
