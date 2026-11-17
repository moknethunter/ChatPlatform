namespace ChatPlatform.Endpoints;

public static class ApiEndpoints
{
    // ==========================================
    // 1. Core API Routes
    // ==========================================
    public const string PlatformInfo = "/api/platform-info";
    public const string Health = "/health";

    // ==========================================
    // 2. Connectors API Routes
    // ==========================================
    public const string Connectors = "/api/connectors";
    public const string ConnectorsById = "/api/connectors/{id}";

    // ==========================================
    // 3. Events & Inbound API Routes
    // ==========================================
    public const string Events = "/api/events";
    public const string InboundByPlatform = "/api/inbound/{platform}";

    // ==========================================
    // 4. SignalR Hubs
    // ==========================================
    public const string ChatHub = "/hub/chat";

    // ==========================================
    // 5. WhatsApp Routes - متوافق مع Meta
    // ==========================================
    public const string WhatsAppWebhook = "/webhook";
    public const string WhatsAppOnboarding = "/api/whatsapp/exchange-code";
    public const string WhatsAppWebhookStatus = "/api/whatsapp/webhook-status";
    public const string WhatsAppTenants = "/api/whatsapp/tenants";
    public const string WhatsAppSendMessage = "/api/messages/send";
    public const string WhatsAppSendTemplate = "/api/messages/send-template";

    // ==========================================
    // 6. WhatsApp Related Routes
    // ==========================================
    public const string Conversations = "/api/conversations";
    public const string Templates = "/api/templates";
    public const string AutoReply = "/api/autoreply";

    // ==========================================
    // 7. Controllers Routes (للاستخدام في الـ Attributes)
    // ==========================================
    public const string WhatsAppController = "api/whatsapp";
    public const string WhatsAppOnboardingController = "api/whatsapp/onboarding";
}
