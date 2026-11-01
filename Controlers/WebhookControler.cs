using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using Microsoft.AspNetCore.Mvc;

namespace ChatPlatform.Api.Controllers;

// هذا الملف تم إيقافه لأن Webhook يُدار الآن عبر MapWhatsAppWebhook في Infrastructure/Adapters/WhatsApp/WhatsAppExtensions.cs
// وذلك لتجنب تعارض المسارات مع /webhook وتحديث المنصة بشكل احترافي وآمن.
public class WebhookController : ControllerBase
{
    public IActionResult Placeholder() => Ok();
}
