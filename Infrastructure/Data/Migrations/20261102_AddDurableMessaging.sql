-- One-time SQLite upgrade for an existing ChatPlatform database.
-- Back up chatplatform.db before running this script.
-- The legacy WhatsAppTenants table must already exist and this script must only be run once.
PRAGMA foreign_keys = ON;
BEGIN IMMEDIATE;

CREATE TABLE IF NOT EXISTS "Tenants" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Tenants" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "IsActive" INTEGER NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL
);

INSERT OR IGNORE INTO "Tenants" ("Id", "Name", "IsActive", "CreatedAtUtc")
VALUES ('00000000-0000-0000-0000-000000000001', 'Default Tenant', 1, '2026-01-01 00:00:00');

ALTER TABLE "WhatsAppTenants" ADD COLUMN "TenantId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001';
CREATE INDEX IF NOT EXISTS "IX_WhatsAppTenants_PhoneNumberId" ON "WhatsAppTenants" ("PhoneNumberId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_WhatsAppTenants_PhoneNumberId_unique" ON "WhatsAppTenants" ("PhoneNumberId");

CREATE TABLE IF NOT EXISTS "WhatsAppWebhookInbox" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_WhatsAppWebhookInbox" PRIMARY KEY,
    "TenantId" TEXT NOT NULL, "EventKey" TEXT NOT NULL, "PhoneNumberId" TEXT NOT NULL,
    "PayloadJson" TEXT NOT NULL, "Status" TEXT NOT NULL, "Attempts" INTEGER NOT NULL,
    "MaxAttempts" INTEGER NOT NULL, "ReceivedAtUtc" TEXT NOT NULL, "NextAttemptAtUtc" TEXT NOT NULL,
    "LeaseUntilUtc" TEXT NULL, "LeaseOwner" TEXT NULL, "ProcessedAtUtc" TEXT NULL, "LastError" TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_WhatsAppWebhookInbox_EventKey" ON "WhatsAppWebhookInbox" ("EventKey");
CREATE INDEX IF NOT EXISTS "IX_WhatsAppWebhookInbox_Status_NextAttemptAtUtc" ON "WhatsAppWebhookInbox" ("Status", "NextAttemptAtUtc");

CREATE TABLE IF NOT EXISTS "WhatsAppStatusInbox" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_WhatsAppStatusInbox" PRIMARY KEY,
    "TenantId" TEXT NOT NULL, "EventKey" TEXT NOT NULL, "PhoneNumberId" TEXT NOT NULL,
    "ContactPhone" TEXT NOT NULL, "Wamid" TEXT NOT NULL, "Status" TEXT NOT NULL,
    "StatusTimestamp" INTEGER NOT NULL, "PayloadJson" TEXT NOT NULL, "Processed" INTEGER NOT NULL,
    "ReceivedAtUtc" TEXT NOT NULL, "ProcessedAtUtc" TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_WhatsAppStatusInbox_EventKey" ON "WhatsAppStatusInbox" ("EventKey");
CREATE INDEX IF NOT EXISTS "IX_WhatsAppStatusInbox_Wamid_Processed" ON "WhatsAppStatusInbox" ("Wamid", "Processed");

CREATE TABLE IF NOT EXISTS "WhatsAppConversations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_WhatsAppConversations" PRIMARY KEY,
    "TenantId" TEXT NOT NULL, "PhoneNumberId" TEXT NOT NULL, "ContactPhone" TEXT NOT NULL,
    "ContactName" TEXT NULL, "LastInboundAtUtc" TEXT NULL, "LastOutboundAtUtc" TEXT NULL,
    "WindowExpiresAtUtc" TEXT NULL, "Status" TEXT NOT NULL, "AssignedToUserId" TEXT NULL,
    "UnreadCount" INTEGER NOT NULL, "Tags" TEXT NULL, "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_WhatsAppConversations_TenantId_PhoneNumberId_ContactPhone" ON "WhatsAppConversations" ("TenantId", "PhoneNumberId", "ContactPhone");
CREATE INDEX IF NOT EXISTS "IX_WhatsAppConversations_TenantId_Status" ON "WhatsAppConversations" ("TenantId", "Status");
CREATE INDEX IF NOT EXISTS "IX_WhatsAppConversations_WindowExpiresAtUtc" ON "WhatsAppConversations" ("WindowExpiresAtUtc");

CREATE TABLE IF NOT EXISTS "WhatsAppMessages" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_WhatsAppMessages" PRIMARY KEY,
    "TenantId" TEXT NOT NULL, "ConversationId" TEXT NOT NULL, "Wamid" TEXT NULL,
    "Direction" TEXT NOT NULL, "MessageType" TEXT NOT NULL, "ContentJson" TEXT NOT NULL,
    "TextPreview" TEXT NULL, "MediaMimeType" TEXT NULL, "MediaUrl" TEXT NULL,
    "Status" TEXT NOT NULL, "TimestampUtc" TEXT NOT NULL, "CreatedAtUtc" TEXT NOT NULL,
    CONSTRAINT "FK_WhatsAppMessages_WhatsAppConversations_ConversationId"
        FOREIGN KEY ("ConversationId") REFERENCES "WhatsAppConversations" ("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_WhatsAppMessages_Wamid" ON "WhatsAppMessages" ("Wamid");
CREATE INDEX IF NOT EXISTS "IX_WhatsAppMessages_TenantId_ConversationId_TimestampUtc" ON "WhatsAppMessages" ("TenantId", "ConversationId", "TimestampUtc");

CREATE TABLE IF NOT EXISTS "WhatsAppOutbox" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_WhatsAppOutbox" PRIMARY KEY,
    "TenantId" TEXT NOT NULL, "CorrelationId" TEXT NOT NULL, "IdempotencyKey" TEXT NOT NULL,
    "MessageId" TEXT NULL, "PhoneNumberId" TEXT NOT NULL, "RecipientPhone" TEXT NOT NULL,
    "MessageType" TEXT NOT NULL, "PayloadJson" TEXT NOT NULL, "Status" TEXT NOT NULL,
    "Attempts" INTEGER NOT NULL, "MaxAttempts" INTEGER NOT NULL, "CreatedAtUtc" TEXT NOT NULL,
    "NextAttemptAtUtc" TEXT NOT NULL, "ScheduledForUtc" TEXT NULL, "LeaseUntilUtc" TEXT NULL,
    "LeaseOwner" TEXT NULL, "SentAtUtc" TEXT NULL, "MetaMessageId" TEXT NULL,
    "LastError" TEXT NULL, "UpdatedAtUtc" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_WhatsAppOutbox_TenantId_IdempotencyKey" ON "WhatsAppOutbox" ("TenantId", "IdempotencyKey");
CREATE INDEX IF NOT EXISTS "IX_WhatsAppOutbox_Status_NextAttemptAtUtc" ON "WhatsAppOutbox" ("Status", "NextAttemptAtUtc");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_WhatsAppOutbox_MetaMessageId" ON "WhatsAppOutbox" ("MetaMessageId");

COMMIT;
