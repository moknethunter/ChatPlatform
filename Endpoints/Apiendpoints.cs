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
    // 5. WhatsApp Routes
    // ==========================================
    public const string WhatsAppWebhook = "/webhook";
    public const string WhatsAppOnboarding = "/api/whatsapp/exchange-code";

    // ==========================================
    // 6. Controllers Routes (للاستخدام في الـ Attributes)
    // ==========================================
    public const string WhatsAppController = "api/whatsapp";
    public const string WhatsAppOnboardingController = "api/whatsapp/onboarding";
}
