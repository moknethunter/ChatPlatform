# ChatPlatform configuration

The application targets .NET 8 and does not commit credentials. Configure secrets through the hosting provider's secret/environment-variable facility.

## Required for Meta webhook verification

- `WhatsApp__VerifyToken`: high-entropy private value configured identically in Meta's webhook setup and the server. It is used only for the GET challenge handshake.
- `META_APP_SECRET`: Meta App Secret used to validate `X-Hub-Signature-256` on the exact incoming request bytes. A missing secret causes the webhook to fail closed (503); invalid signatures receive 401.

## Optional Meta Cloud API setup

- `Meta__AppId` and `META_APP_SECRET` are required for OAuth code exchange in the existing onboarding endpoint.
- `WhatsApp__GraphApiVersion`, `WhatsApp__PhoneNumberId`, and `WhatsApp__AccessToken` configure the legacy single-number adapter. Prefer per-account encrypted credentials for multi-account operation; do not put live tokens in checked-in JSON.
- Meta Embedded Signup, business verification, permissions, app review, and subscribed webhook fields must be configured in Meta Business/Developer settings. A personal WhatsApp number is not automatically eligible for Cloud API onboarding.

## Management API and browser origins

- `CHATPLATFORM_API_KEY` (or `Security__ApiKey`) is required to use the existing connector/admin endpoints. Requests are rejected when it is unset.
- `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1`, ... may be configured with exact trusted origins. No wildcard credentialed CORS policy is used.

## Local development

Use .NET 8 SDK and local user-secrets/environment variables. The webhook needs public HTTPS reachability for Meta delivery. Do not send real WhatsApp messages in automated tests. SQLite is the default development database; production persistence requires a durable volume or a configured production database and a durable event store before relying on webhook delivery guarantees.
