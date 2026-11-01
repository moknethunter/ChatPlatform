using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using Microsoft.AspNetCore.Mvc;

namespace ChatPlatform.Infrastructure.Adapters;

// Compatibility shim: legacy WhatsApp adapter kept intentionally empty to avoid duplicate registration and conflicting webhook routes.
public static class LegacyWhatsAppCompatibility
{
}
