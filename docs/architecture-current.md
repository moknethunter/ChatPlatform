# ChatPlatform architecture map (repository inspection)

## Current solution shape

This repository is a single ASP.NET Core .NET 8 Web SDK project (`ChatPlatform.csproj`), not a multi-project solution. `Program.cs` is the composition root and contains Minimal API routes; `Controlers/` contains MVC controllers (the directory name is retained for compatibility).

## Layers and runtime registrations

- **Core:** `Core/Abstractions/IEventsBus.cs`, `IIntegrationAdapter.cs`, and `Core/Events/PlatformEvent.cs` define the event bus, adapter contract, and event envelope. `IEventsBus.cs` retains its existing filename although its declared type is `IEventBus`.
- **Infrastructure:** `Infrastructure/Data/PlatformDbContext.cs` (EF Core SQLite and Data Protection keys), `Infrastructure/Messaging/InMemoryEventBus.cs`, `Infrastructure/Engine/CoreEngine.cs`, configuration/options, and integration adapters.
- **Presentation:** `Program.cs` Minimal APIs, `Controlers/WhatsAppOnboardingController.cs`, `Hubs/ChatHub.cs`, and static files under `wwwroot/`.
- **Application layer:** no separate Application project/layer exists in the inspected tree.
- **DI:** `PlatformDbContext` uses SQLite; `IEventBus` maps to singleton `InMemoryEventBus`; `CoreEngine` is hosted; `ConsoleAdapter`, `ChatAdapter`, `UniversalConnectorAdapter`, and the database-backed `Infrastructure.Adapters.WhatsAppAdapter` are registered as `IIntegrationAdapter`. SignalR and named HTTP clients are registered in `Program.cs`.

## Event path

Minimal API inbound/event endpoints publish `PlatformEvent` to `IEventBus`; its in-memory subscriber channels fan out to `CoreEngine`. The bounded worker queue dispatches each event to every registered adapter with retries and publishes a dead-letter event on terminal adapter failure. Chat adapter uses SignalR. WhatsApp webhook route is defined by `WhatsAppExtensions.MapWhatsAppWebhook`; the original composition root must map that extension for the route to be active. This architecture is process-local: in-memory events and duplicate suppression are not durable across restarts.

## Persistence and data

EF Core currently exposes `Connectors`, `WhatsAppTenants`, and `DataProtectionKeys`; the inspected repository tree contains no EF migration files. `ConnectorEntity` stores connector configuration including an auth token. `WhatsAppTenantEntity` stores WABA, phone-number, app identifiers, active state, and an encrypted business token. No conversation/message/template entities were present in the inspected tree.

## HTTP endpoints, integrations, and UI

- Controllers: `WhatsAppOnboardingController` has code exchange and phone registration actions. `WebhookControler` is a placeholder and is not the active webhook implementation.
- Minimal API routes in `Program.cs`: platform-info, connectors, generic inbound, event publish, health, and SignalR at `/hub/chat`.
- Adapters: console, chat/SignalR, universal connector, generic webhook, two WhatsApp adapter implementations in different namespaces. The event-bus registration selects the database-backed adapter in `Infrastructure/Adapters/WhatsAppAdapter.cs`; the nested WhatsApp adapter is registered only by its extension method.
- Frontend: existing static HTML in `wwwroot`, including the index and legal pages; no separate React/Next project was present.
- Hosting: Dockerfile uses .NET 8 multi-stage publish; Render blueprint is present. GitHub has CI, ZIP publishing, and container publishing workflows.
- Authentication/authorization: the existing API key middleware only gates `/api/admin` and `/api/connectors`; there is no configured identity provider, user/role model, or tenant authorization policy in the inspected source. SignalR group joining accepts a caller-provided group name.

## Boundaries

This map records the checked-in source structure, not an assertion that the larger requested multi-tenant inbox, durable outbox, authentication/RBAC, and analytics features already exist. Meta account permissions, app review, production tokens, webhook URL configuration, and durable production storage remain external requirements.
