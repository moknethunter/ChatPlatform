using Microsoft.Data.Sqlite;

namespace ChatPlatform.Infrastructure.Data;

public static class DbInitializer
{
    public static void EnsureCreated(string connectionString)
    {
        // طلع مسار الملف من ConnectionString
        var dbPath = connectionString.Replace("Data Source=", "").Trim();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
PRAGMA foreign_keys = ON;

-- 1. Tenants - من PlatformDbContext
CREATE TABLE IF NOT EXISTS Tenants (
    Id TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    IsActive INTEGER NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);
INSERT OR IGNORE INTO Tenants (Id, Name, IsActive, CreatedAtUtc)
VALUES ('00000000-0000-0000-0000-000000000001', 'Default Tenant', 1, '2026-01-01 00:00:00');

-- 2. Connectors - كان ناقص
CREATE TABLE IF NOT EXISTS Connectors (
    Id TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL,
    Type TEXT NOT NULL,
    Name TEXT NOT NULL,
    ConfigJson TEXT NOT NULL,
    IsActive INTEGER NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);

-- 3. WhatsAppTenants
CREATE TABLE IF NOT EXISTS WhatsAppTenants (
    Id TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001',
    PhoneNumberId TEXT NOT NULL,
    DisplayPhoneNumber TEXT NULL,
    BusinessName TEXT NULL,
    AccessTokenEncrypted TEXT NULL,
    VerifyToken TEXT NULL,
    IsActive INTEGER NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_WhatsAppTenants_PhoneNumberId ON WhatsAppTenants (PhoneNumberId);

-- 4. DataProtectionKeys
CREATE TABLE IF NOT EXISTS DataProtectionKeys (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    FriendlyName TEXT NULL,
    Xml TEXT NULL
);

-- 5. WhatsAppWebhookInbox - من ملف SQL حقك
CREATE TABLE IF NOT EXISTS WhatsAppWebhookInbox (
    Id TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL, EventKey TEXT NOT NULL, PhoneNumberId TEXT NOT NULL,
    PayloadJson TEXT NOT NULL, Status TEXT NOT NULL, Attempts INTEGER NOT NULL,
    MaxAttempts INTEGER NOT NULL, ReceivedAtUtc TEXT NOT NULL, NextAttemptAtUtc TEXT NOT NULL,
    LeaseUntilUtc TEXT NULL, LeaseOwner TEXT NULL, ProcessedAtUtc TEXT NULL, LastError TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_WhatsAppWebhookInbox_EventKey ON WhatsAppWebhookInbox (EventKey);

-- 6. WhatsAppStatusInbox
CREATE TABLE IF NOT EXISTS WhatsAppStatusInbox (
    Id TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL, EventKey TEXT NOT NULL, PhoneNumberId TEXT NOT NULL,
    ContactPhone TEXT NOT NULL, Wamid TEXT NOT NULL, Status TEXT NOT NULL,
    StatusTimestamp INTEGER NOT NULL, PayloadJson TEXT NOT NULL, Processed INTEGER NOT NULL,
    ReceivedAtUtc TEXT NOT NULL, ProcessedAtUtc TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_WhatsAppStatusInbox_EventKey ON WhatsAppStatusInbox (EventKey);

-- 7. WhatsAppConversations
CREATE TABLE IF NOT EXISTS WhatsAppConversations (
    Id TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL, PhoneNumberId TEXT NOT NULL, ContactPhone TEXT NOT NULL,
    ContactName TEXT NULL, LastInboundAtUtc TEXT NULL, LastOutboundAtUtc TEXT NULL,
    WindowExpiresAtUtc TEXT NULL, Status TEXT NOT NULL, AssignedToUserId TEXT NULL,
    UnreadCount INTEGER NOT NULL, Tags TEXT NULL, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_WhatsAppConversations_Unique ON WhatsAppConversations (TenantId, PhoneNumberId, ContactPhone);

-- 8. WhatsAppMessages
CREATE TABLE IF NOT EXISTS WhatsAppMessages (
    Id TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL, ConversationId TEXT NOT NULL, Wamid TEXT NULL,
    Direction TEXT NOT NULL, MessageType TEXT NOT NULL, ContentJson TEXT NOT NULL,
    TextPreview TEXT NULL, MediaMimeType TEXT NULL, MediaUrl TEXT NULL,
    Status TEXT NOT NULL, TimestampUtc TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL,
    FOREIGN KEY (ConversationId) REFERENCES WhatsAppConversations (Id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_WhatsAppMessages_Wamid ON WhatsAppMessages (Wamid);

-- 9. WhatsAppOutbox
CREATE TABLE IF NOT EXISTS WhatsAppOutbox (
    Id TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL, CorrelationId TEXT NOT NULL, IdempotencyKey TEXT NOT NULL,
    MessageId TEXT NULL, PhoneNumberId TEXT NOT NULL, RecipientPhone TEXT NOT NULL,
    MessageType TEXT NOT NULL, PayloadJson TEXT NOT NULL, Status TEXT NOT NULL,
    Attempts INTEGER NOT NULL, MaxAttempts INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL,
    NextAttemptAtUtc TEXT NOT NULL, ScheduledForUtc TEXT NULL, LeaseUntilUtc TEXT NULL,
    LeaseOwner TEXT NULL, SentAtUtc TEXT NULL, MetaMessageId TEXT NULL,
    LastError TEXT NULL, UpdatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_WhatsAppOutbox_TenantId_IdempotencyKey ON WhatsAppOutbox (TenantId, IdempotencyKey);

-- 10. WhatsAppTemplates - كان ناقص
CREATE TABLE IF NOT EXISTS WhatsAppTemplates (
    Id TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL,
    WhatsAppTenantId TEXT NOT NULL,
    Name TEXT NOT NULL,
    Category TEXT NOT NULL,
    Language TEXT NOT NULL,
    Body TEXT NOT NULL,
    Status TEXT NOT NULL,
    IsActive INTEGER NOT NULL DEFAULT 1,
    CreatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_WhatsAppTemplates_Unique ON WhatsAppTemplates (TenantId, WhatsAppTenantId, Name);

-- 11. AutoReplyRules - كان ناقص
CREATE TABLE IF NOT EXISTS AutoReplyRules (
    Id TEXT NOT NULL PRIMARY KEY,
    TenantId TEXT NOT NULL,
    Name TEXT NOT NULL,
    MatchType TEXT NOT NULL,
    KeywordPattern TEXT NULL,
    ReplyText TEXT NOT NULL,
    Priority INTEGER NOT NULL DEFAULT 0,
    IsActive INTEGER NOT NULL DEFAULT 1,
    CreatedAtUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_AutoReplyRules_Tenant ON AutoReplyRules (TenantId, IsActive, Priority);
";
        cmd.ExecuteNonQuery();
        Console.WriteLine($">>> DB Initialized at {dbPath}");
    }
}
