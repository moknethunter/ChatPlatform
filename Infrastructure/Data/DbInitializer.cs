using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ChatPlatform.Infrastructure.Data;

public static class DbInitializer
{
    private const string DefaultDatabaseFile = "platform.db";
    private const string ContainerDataDirectory = "/data";

    private const string DefaultTenantId =
        "00000000-0000-0000-0000-000000000001";

    private const string AlhawbaniTenantId =
        "tenant-alhawbani-001";

    public static void EnsureCreated(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException(
                "Database connection string is missing.",
                nameof(connectionString));

        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dbPath = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dbPath))
            throw new InvalidOperationException(
                "SQLite Data Source is not configured.");

        if (dbPath.Equals(":memory:",
            StringComparison.OrdinalIgnoreCase))
        {
            InitializeDatabase(connectionString, dbPath);
            return;
        }

        var configuredDirectory =
            Environment.GetEnvironmentVariable(
                "CHATPLATFORM_DATA_DIR");

        var dataDirectory =
            string.IsNullOrWhiteSpace(configuredDirectory)
                ? ContainerDataDirectory
                : configuredDirectory;

        var normalizedPath = dbPath.Replace('\\', '/');

        // تحويل مسارات Docker القديمة إلى مجلد البيانات الدائم.
        if (normalizedPath.Equals("/app",
            StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Equals("/app/",
            StringComparison.OrdinalIgnoreCase))
        {
            dbPath = Path.Combine(
                dataDirectory,
                DefaultDatabaseFile);
        }
        else if (normalizedPath.StartsWith(
            "/app/",
            StringComparison.OrdinalIgnoreCase))
        {
            dbPath = Path.Combine(
                dataDirectory,
                Path.GetFileName(dbPath));
        }
        else if (!Path.IsPathRooted(dbPath))
        {
            // المسارات النسبية تحفظ داخل مجلد البيانات.
            dbPath = Path.Combine(
                dataDirectory,
                dbPath);
        }

        dbPath = Path.GetFullPath(dbPath);

        var directory = Path.GetDirectoryName(dbPath);

        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(
                "Could not determine database directory.");

        if (!TryEnsureDirectory(directory))
        {
            // لا نستخدم /tmp تلقائياً في الإنتاج.
            // لأن ذلك يؤدي إلى فقدان البيانات بعد إعادة التشغيل.
            var allowEphemeral =
                string.Equals(
                    Environment.GetEnvironmentVariable(
                        "CHATPLATFORM_ALLOW_EPHEMERAL_DB"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);

            if (!allowEphemeral)
            {
                throw new IOException(
                    $"Cannot write to SQLite directory: {directory}. " +
                    "Configure CHATPLATFORM_DATA_DIR to a writable " +
                    "persistent volume.");
            }

            var fallbackDirectory = Path.Combine(
                Path.GetTempPath(),
                "chatplatform-data");

            Console.WriteLine(
                $">>> WARNING: Using EPHEMERAL database: {fallbackDirectory}");

            if (!TryEnsureDirectory(fallbackDirectory))
                throw new IOException(
                    "Cannot create fallback SQLite directory.");

            dbPath = Path.Combine(
                fallbackDirectory,
                Path.GetFileName(dbPath));
        }

        builder.DataSource = dbPath;

        InitializeDatabase(builder.ToString(), dbPath);
    }

    private static bool TryEnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var testFile = Path.Combine(
                directory,
                ".chatplatform-write-test-" +
                Guid.NewGuid().ToString("N"));

            File.WriteAllText(testFile, "test");
            File.Delete(testFile);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $">>> SQLite directory unavailable: {directory}");

            Console.WriteLine(
                $">>> Reason: {ex.Message}");

            return false;
        }
    }

    private static void InitializeDatabase(
        string connectionString,
        string dbPath)
    {
        using var connection =
            new SqliteConnection(connectionString);

        connection.Open();

        using var transaction =
            connection.BeginTransaction();

        try
        {
            Execute(connection, transaction,
                "PRAGMA foreign_keys = ON;");

            Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS Tenants (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    IsActive INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Connectors (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    Type TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    ConfigJson TEXT NOT NULL,
                    IsActive INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

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

                CREATE TABLE IF NOT EXISTS DataProtectionKeys (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FriendlyName TEXT NULL,
                    Xml TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS WhatsAppWebhookInbox (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    EventKey TEXT NOT NULL,
                    PhoneNumberId TEXT NOT NULL,
                    PayloadJson TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    Attempts INTEGER NOT NULL,
                    MaxAttempts INTEGER NOT NULL,
                    ReceivedAtUtc TEXT NOT NULL,
                    NextAttemptAtUtc TEXT NOT NULL,
                    LeaseUntilUtc TEXT NULL,
                    LeaseOwner TEXT NULL,
                    ProcessedAtUtc TEXT NULL,
                    LastError TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS WhatsAppStatusInbox (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    EventKey TEXT NOT NULL,
                    PhoneNumberId TEXT NOT NULL,
                    ContactPhone TEXT NOT NULL,
                    Wamid TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    StatusTimestamp INTEGER NOT NULL,
                    PayloadJson TEXT NOT NULL,
                    Processed INTEGER NOT NULL,
                    ReceivedAtUtc TEXT NOT NULL,
                    ProcessedAtUtc TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_WhatsAppStatusInbox_Pending ON WhatsAppStatusInbox(Processed, ReceivedAtUtc);

                CREATE TABLE IF NOT EXISTS WhatsAppConversations (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    PhoneNumberId TEXT NOT NULL,
                    ContactPhone TEXT NOT NULL,
                    ContactName TEXT NULL,
                    LastInboundAtUtc TEXT NULL,
                    LastOutboundAtUtc TEXT NULL,
                    WindowExpiresAtUtc TEXT NULL,
                    Status TEXT NOT NULL,
                    AssignedToUserId TEXT NULL,
                    UnreadCount INTEGER NOT NULL,
                    Tags TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS CrmContacts (
                    TenantId TEXT NOT NULL,
                    Phone TEXT NOT NULL,
                    DisplayName TEXT NOT NULL DEFAULT '',
                    Email TEXT NOT NULL DEFAULT '',
                    Notes TEXT NOT NULL DEFAULT '',
                    Tags TEXT NOT NULL DEFAULT '',
                    Stage TEXT NOT NULL DEFAULT 'new',
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    PRIMARY KEY (TenantId, Phone)
                );
                CREATE INDEX IF NOT EXISTS IX_CrmContacts_Tenant_Stage ON CrmContacts(TenantId, Stage);
                CREATE INDEX IF NOT EXISTS IX_WhatsAppConversations_Tenant_Updated ON WhatsAppConversations(TenantId, UpdatedAtUtc);
                -- Import existing conversations without overwriting a CRM profile.
                INSERT OR IGNORE INTO CrmContacts (TenantId, Phone, DisplayName, CreatedAtUtc, UpdatedAtUtc)
                SELECT TenantId, ContactPhone, COALESCE(ContactName,''), CreatedAtUtc, UpdatedAtUtc
                FROM WhatsAppConversations ORDER BY UpdatedAtUtc DESC;
                CREATE TRIGGER IF NOT EXISTS CRM_Conversation_Insert
                AFTER INSERT ON WhatsAppConversations
                BEGIN
                    INSERT OR IGNORE INTO CrmContacts (TenantId, Phone, DisplayName, CreatedAtUtc, UpdatedAtUtc)
                    VALUES (NEW.TenantId, NEW.ContactPhone, COALESCE(NEW.ContactName,''), NEW.CreatedAtUtc, NEW.UpdatedAtUtc);
                END;

                CREATE TABLE IF NOT EXISTS WhatsAppMessages (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    ConversationId TEXT NOT NULL,
                    Wamid TEXT NULL,
                    Direction TEXT NOT NULL,
                    MessageType TEXT NOT NULL,
                    ContentJson TEXT NOT NULL,
                    TextPreview TEXT NULL,
                    MediaMimeType TEXT NULL,
                    MediaUrl TEXT NULL,
                    Status TEXT NOT NULL,
                    TimestampUtc TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY (ConversationId)
                        REFERENCES WhatsAppConversations(Id)
                        ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS InboxRevisions (
                    TenantId TEXT NOT NULL PRIMARY KEY,
                    Revision INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS BotDrafts (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    DefinitionJson TEXT NOT NULL,
                    Version INTEGER NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_BotDrafts_TenantId ON BotDrafts(TenantId);
                CREATE TABLE IF NOT EXISTS MarketingConsents (
                    TenantId TEXT NOT NULL, PhoneNumberId TEXT NOT NULL, Phone TEXT NOT NULL,
                    Granted INTEGER NOT NULL, Evidence TEXT NOT NULL, UpdatedBy TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                    PRIMARY KEY(TenantId,PhoneNumberId,Phone)
                );
                CREATE TABLE IF NOT EXISTS ConsentEvents (
                    Id TEXT PRIMARY KEY NOT NULL, TenantId TEXT NOT NULL, PhoneNumberId TEXT NOT NULL, Phone TEXT NOT NULL,
                    Granted INTEGER NOT NULL, Evidence TEXT NOT NULL, UpdatedBy TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ConsentEvents_Tenant_Phone ON ConsentEvents(TenantId,PhoneNumberId,Phone);
                CREATE TABLE IF NOT EXISTS CustomerLists (
                    Id TEXT PRIMARY KEY NOT NULL, TenantId TEXT NOT NULL, Name TEXT NOT NULL, PhonesJson TEXT NOT NULL, Version INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_CustomerLists_TenantId ON CustomerLists(TenantId);
                CREATE TABLE IF NOT EXISTS MarketingCampaigns (
                    Id TEXT PRIMARY KEY NOT NULL, TenantId TEXT NOT NULL, Name TEXT NOT NULL, ListId TEXT NOT NULL,
                    TemplateId TEXT NOT NULL, PhoneNumberId TEXT NOT NULL, Status TEXT NOT NULL,
                    ScheduledAtUtc TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, CreatedBy TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_MarketingCampaigns_TenantId ON MarketingCampaigns(TenantId);
                CREATE TABLE IF NOT EXISTS CampaignRecipients (
                    TenantId TEXT NOT NULL, CampaignId TEXT NOT NULL, Phone TEXT NOT NULL, OutboxId TEXT NOT NULL,
                    PRIMARY KEY(TenantId,CampaignId,Phone)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_CampaignRecipients_OutboxId ON CampaignRecipients(OutboxId);
                CREATE TABLE IF NOT EXISTS KnowledgeArticles (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    Body TEXT NOT NULL,
                    IsPublished INTEGER NOT NULL,
                    Version INTEGER NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_KnowledgeArticles_TenantId ON KnowledgeArticles(TenantId);
                CREATE TABLE IF NOT EXISTS ConversationAutomation (
                    ConversationId TEXT NOT NULL,
                    TenantId TEXT NOT NULL,
                    Mode TEXT NOT NULL,
                    ActivationId TEXT NULL,
                    NextNode TEXT NULL,
                    VariablesJson TEXT NOT NULL DEFAULT '{}',
                    LastInputAtUtc TEXT NULL,
                    ExpiresAtUtc TEXT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    UpdatedBy TEXT NOT NULL,
                    PRIMARY KEY (TenantId, ConversationId)
                );
                CREATE TRIGGER IF NOT EXISTS Rev_ConversationAutomation_Insert AFTER INSERT ON ConversationAutomation
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (NEW.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TRIGGER IF NOT EXISTS Rev_ConversationAutomation_Update AFTER UPDATE ON ConversationAutomation
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (NEW.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TABLE IF NOT EXISTS BotReleases (
                    TenantId TEXT NOT NULL PRIMARY KEY,
                    DraftId TEXT NOT NULL,
                    ActivationId TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Version INTEGER NOT NULL,
                    DefinitionJson TEXT NOT NULL,
                    PublishedAtUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS AutomationRuns (
                    InboundMessageId TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    ConversationId TEXT NOT NULL,
                    RecipientPhone TEXT NOT NULL,
                    BotId TEXT NULL,
                    BotVersion INTEGER NULL,
                    RuleId TEXT NULL,
                    Outcome TEXT NOT NULL,
                    OutboundMessageId TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_AutomationRuns_Tenant_Phone_Created ON AutomationRuns(TenantId, RecipientPhone, CreatedAtUtc);
                CREATE INDEX IF NOT EXISTS IX_AutomationRuns_Tenant_Created ON AutomationRuns(TenantId, CreatedAtUtc);
                CREATE TRIGGER IF NOT EXISTS Rev_WhatsAppConversations_INSERT AFTER INSERT ON WhatsAppConversations
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (NEW.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TRIGGER IF NOT EXISTS Rev_WhatsAppConversations_UPDATE AFTER UPDATE ON WhatsAppConversations
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (NEW.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TRIGGER IF NOT EXISTS Rev_WhatsAppConversations_DELETE AFTER DELETE ON WhatsAppConversations
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (OLD.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TRIGGER IF NOT EXISTS Rev_WhatsAppMessages_INSERT AFTER INSERT ON WhatsAppMessages
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (NEW.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TRIGGER IF NOT EXISTS Rev_WhatsAppMessages_UPDATE AFTER UPDATE ON WhatsAppMessages
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (NEW.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TRIGGER IF NOT EXISTS Rev_WhatsAppMessages_DELETE AFTER DELETE ON WhatsAppMessages
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (OLD.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TRIGGER IF NOT EXISTS Rev_CrmContacts_INSERT AFTER INSERT ON CrmContacts
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (NEW.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TRIGGER IF NOT EXISTS Rev_CrmContacts_UPDATE AFTER UPDATE ON CrmContacts
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (NEW.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;
                CREATE TRIGGER IF NOT EXISTS Rev_CrmContacts_DELETE AFTER DELETE ON CrmContacts
                BEGIN
                    INSERT INTO InboxRevisions(TenantId, Revision) VALUES (OLD.TenantId, 1)
                    ON CONFLICT(TenantId) DO UPDATE SET Revision = Revision + 1;
                END;

                CREATE TABLE IF NOT EXISTS WhatsAppOutbox (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    CorrelationId TEXT NOT NULL,
                    IdempotencyKey TEXT NOT NULL,
                    MessageId TEXT NULL,
                    PhoneNumberId TEXT NOT NULL,
                    RecipientPhone TEXT NOT NULL,
                    MessageType TEXT NOT NULL,
                    PayloadJson TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    Attempts INTEGER NOT NULL,
                    MaxAttempts INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    NextAttemptAtUtc TEXT NOT NULL,
                    ScheduledForUtc TEXT NULL,
                    LeaseUntilUtc TEXT NULL,
                    LeaseOwner TEXT NULL,
                    SentAtUtc TEXT NULL,
                    MetaMessageId TEXT NULL,
                    LastError TEXT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

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

                CREATE TABLE IF NOT EXISTS ManagedAgents (
                    Username TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY (TenantId) REFERENCES Tenants(Id)
                );
                CREATE INDEX IF NOT EXISTS IX_ManagedAgents_TenantId ON ManagedAgents(TenantId);

                CREATE TABLE IF NOT EXISTS ManagedTenantAdmins (
                    Username TEXT NOT NULL PRIMARY KEY, TenantId TEXT NOT NULL, PasswordHash TEXT NOT NULL,
                    IsActive INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY(TenantId) REFERENCES Tenants(Id)
                );
                CREATE INDEX IF NOT EXISTS IX_ManagedTenantAdmins_TenantId ON ManagedTenantAdmins(TenantId);
                CREATE INDEX IF NOT EXISTS IX_Tenants_Name_Id ON Tenants(Name,Id);
                CREATE TABLE IF NOT EXISTS ServicePlans (
                    Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, MonthlyMessages INTEGER NOT NULL,
                    Agents INTEGER NOT NULL, Accounts INTEGER NOT NULL, Bots INTEGER NOT NULL, Articles INTEGER NOT NULL,
                    Campaigns INTEGER NOT NULL, OwnModel INTEGER NOT NULL, MonthlyPriceMinor INTEGER NOT NULL,
                    Currency TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS TenantSubscriptions (
                    TenantId TEXT NOT NULL PRIMARY KEY, PlanId TEXT NOT NULL, Enabled INTEGER NOT NULL,
                    EndsAtUtc TEXT NOT NULL, Version INTEGER NOT NULL, UpdatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY(TenantId) REFERENCES Tenants(Id), FOREIGN KEY(PlanId) REFERENCES ServicePlans(Id)
                );
                CREATE TABLE IF NOT EXISTS PlatformAdminAudit (
                    Id TEXT NOT NULL PRIMARY KEY, Actor TEXT NOT NULL, Action TEXT NOT NULL,
                    Target TEXT NOT NULL, Detail TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_PlatformAdminAudit_CreatedAtUtc ON PlatformAdminAudit(CreatedAtUtc);
                CREATE INDEX IF NOT EXISTS IX_WhatsAppOutbox_Tenant_Quota ON WhatsAppOutbox(TenantId, CreatedAtUtc, ScheduledForUtc);
                CREATE TABLE IF NOT EXISTS EmbeddedSignupAttempts (
                    TenantId TEXT NOT NULL,
                    SessionHash TEXT NOT NULL,
                    StateHash TEXT NOT NULL,
                    AppId TEXT NOT NULL,
                    ConfigId TEXT NOT NULL,
                    ExpiresAtUtc TEXT NOT NULL,
                    PRIMARY KEY (TenantId, SessionHash)
                );
                CREATE INDEX IF NOT EXISTS IX_EmbeddedSignupAttempts_ExpiresAtUtc ON EmbeddedSignupAttempts(ExpiresAtUtc);

                CREATE TABLE IF NOT EXISTS PlatformSessions (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Username TEXT NOT NULL,
                    TenantId TEXT NOT NULL,
                    ExpiresAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_PlatformSessions_ExpiresAtUtc ON PlatformSessions(ExpiresAtUtc);

                CREATE TABLE IF NOT EXISTS Users (
                    Id TEXT NOT NULL PRIMARY KEY,
                    TenantId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001',
                    FullName TEXT NOT NULL,
                    Phone TEXT NOT NULL,
                    Email TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    Role TEXT NOT NULL DEFAULT 'agent',
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    CreatedAtUtc TEXT NOT NULL,
                    LastLoginAtUtc TEXT NULL,
                    FOREIGN KEY (TenantId)
                        REFERENCES Tenants(Id)
                );
                """);

            // ترحيل قواعد البيانات القديمة.
            ApplyWhatsAppTenantCompatibilityMigration(
                connection,
                transaction);

            EnsureColumnExists(connection, transaction, "ConversationAutomation", "VariablesJson", "TEXT NOT NULL DEFAULT '{}'");

            // Preserve existing templates while adding fields required by the EF model.
            EnsureColumnExists(connection, transaction, "WhatsAppTemplates", "MetaTemplateId", "TEXT NULL");
            EnsureColumnExists(connection, transaction, "WhatsAppTemplates", "UpdatedAtUtc",
                "TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'");
            Execute(connection, transaction, """
                UPDATE WhatsAppTemplates SET UpdatedAtUtc = CreatedAtUtc
                WHERE UpdatedAtUtc = '0001-01-01 00:00:00';
                """);

            // Keep existing auto-reply rules while adding fields used by EF Core.
            EnsureColumnExists(connection, transaction, "AutoReplyRules", "CooldownSeconds",
                "INTEGER NOT NULL DEFAULT 60");
            EnsureColumnExists(connection, transaction, "AutoReplyRules", "UpdatedAtUtc",
                "TEXT NOT NULL DEFAULT '2026-01-01 00:00:00'");
            Execute(connection, transaction, """
                UPDATE AutoReplyRules SET UpdatedAtUtc = CreatedAtUtc
                WHERE UpdatedAtUtc = '2026-01-01 00:00:00';
                """);

            // إنشاء المؤسسات الافتراضية.
            EnsureDefaultTenant(connection, transaction);

            // إنشاء مؤسسة الحوباني.
            EnsureAlhawbaniTenant(connection, transaction);

            // ربط حساب واتساب عند توفر إعداداته.
            EnsureAlhawbaniWhatsAppTenant(
                connection,
                transaction);

            // إنشاء الفهارس بعد التأكد من الأعمدة.
            Execute(connection, transaction, """
                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_WhatsAppTenants_PhoneNumberId
                    ON WhatsAppTenants(PhoneNumberId);

                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_WhatsAppWebhookInbox_EventKey
                    ON WhatsAppWebhookInbox(EventKey);

                CREATE INDEX IF NOT EXISTS
                    IX_WhatsAppWebhookInbox_Status_NextAttemptAtUtc
                    ON WhatsAppWebhookInbox(Status, NextAttemptAtUtc);
                CREATE INDEX IF NOT EXISTS IX_WhatsAppWebhookInbox_Lease
                    ON WhatsAppWebhookInbox(Status, LeaseUntilUtc);

                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_WhatsAppStatusInbox_EventKey
                    ON WhatsAppStatusInbox(EventKey);

                CREATE INDEX IF NOT EXISTS
                    IX_WhatsAppStatusInbox_Wamid_Processed
                    ON WhatsAppStatusInbox(Wamid, Processed);

                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_WhatsAppConversations_Unique
                    ON WhatsAppConversations(
                        TenantId,
                        PhoneNumberId,
                        ContactPhone);

                CREATE INDEX IF NOT EXISTS
                    IX_WhatsAppConversations_TenantId_Status
                    ON WhatsAppConversations(TenantId, Status);

                CREATE INDEX IF NOT EXISTS
                    IX_WhatsAppConversations_WindowExpiresAtUtc
                    ON WhatsAppConversations(WindowExpiresAtUtc);

                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_WhatsAppMessages_Wamid
                    ON WhatsAppMessages(Wamid);

                CREATE INDEX IF NOT EXISTS
                    IX_WhatsAppMessages_TenantId_ConversationId_TimestampUtc
                    ON WhatsAppMessages(
                        TenantId,
                        ConversationId,
                        TimestampUtc);

                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_WhatsAppOutbox_TenantId_IdempotencyKey
                    ON WhatsAppOutbox(TenantId, IdempotencyKey);

                CREATE INDEX IF NOT EXISTS
                    IX_WhatsAppOutbox_Status_NextAttemptAtUtc
                    ON WhatsAppOutbox(Status, NextAttemptAtUtc);
                CREATE INDEX IF NOT EXISTS IX_WhatsAppOutbox_Lease
                    ON WhatsAppOutbox(Status, LeaseUntilUtc);

                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_WhatsAppOutbox_MetaMessageId
                    ON WhatsAppOutbox(MetaMessageId);

                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_WhatsAppTemplates_Unique
                    ON WhatsAppTemplates(
                        TenantId,
                        WhatsAppTenantId,
                        Name);

                CREATE INDEX IF NOT EXISTS
                    IX_WhatsAppTemplates_Tenant
                    ON WhatsAppTemplates(
                        TenantId,
                        WhatsAppTenantId,
                        IsActive);

                CREATE INDEX IF NOT EXISTS
                    IX_AutoReplyRules_Tenant
                    ON AutoReplyRules(
                        TenantId,
                        IsActive,
                        Priority);

                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_Users_Tenant_Phone
                    ON Users(TenantId, Phone);

                CREATE UNIQUE INDEX IF NOT EXISTS
                    IX_Users_Tenant_Email
                    ON Users(TenantId, Email);
                """);

            transaction.Commit();

            Console.WriteLine(
                $">>> SQLite initialized successfully: {dbPath}");
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = sql;

        command.ExecuteNonQuery();
    }

    private static void EnsureDefaultTenant(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT OR IGNORE INTO Tenants
                (Id, Name, IsActive, CreatedAtUtc)
            VALUES
                ($id, $name, 1, $created);
            """;

        command.Parameters.AddWithValue(
            "$id", DefaultTenantId);

        command.Parameters.AddWithValue(
            "$name", "Default Tenant");

        command.Parameters.AddWithValue(
            "$created", DateTime.UtcNow.ToString("O"));

        command.ExecuteNonQuery();
    }

    private static void EnsureAlhawbaniTenant(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT OR IGNORE INTO Tenants
                (Id, Name, IsActive, CreatedAtUtc)
            VALUES
                ($id, $name, 1, $created);
            """;

        command.Parameters.AddWithValue(
            "$id", AlhawbaniTenantId);

        command.Parameters.AddWithValue(
            "$name", "مؤسسة الحوباني");

        command.Parameters.AddWithValue(
            "$created", DateTime.UtcNow.ToString("O"));

        command.ExecuteNonQuery();

        Console.WriteLine(
            ">>> Alhawbani tenant ensured: " +
            AlhawbaniTenantId);
    }

    private static void EnsureAlhawbaniWhatsAppTenant(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var phoneNumberId =
            Environment.GetEnvironmentVariable(
                "WHATSAPP_PHONE_NUMBER_ID");

        if (string.IsNullOrWhiteSpace(phoneNumberId))
            phoneNumberId = Environment.GetEnvironmentVariable("WhatsApp__PhoneNumberId");

        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            Console.WriteLine(
                ">>> WhatsApp linking skipped: " +
                "WHATSAPP_PHONE_NUMBER_ID is not configured.");

            return;
        }

        phoneNumberId = phoneNumberId.Trim();

        var tenantId =
            Environment.GetEnvironmentVariable(
                "WHATSAPP_TENANT_ID");

        if (string.IsNullOrWhiteSpace(tenantId))
            tenantId = AlhawbaniTenantId;

        // لا نسمح بربط حساب WhatsApp بمؤسسة غير موجودة.
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;

            check.CommandText = """
                SELECT COUNT(*)
                FROM Tenants
                WHERE Id = $id AND IsActive = 1;
                """;

            check.Parameters.AddWithValue("$id", tenantId);

            if (Convert.ToInt32(check.ExecuteScalar()) == 0)
            {
                throw new InvalidOperationException(
                    "WhatsApp tenant does not exist or is inactive: " +
                    tenantId);
            }
        }

        var existingTenantId = GetExistingWhatsAppTenant(
            connection,
            transaction,
            phoneNumberId);

        if (existingTenantId != null)
        {
            if (!string.Equals(
                existingTenantId,
                tenantId,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "PhoneNumberId is already linked to another tenant. " +
                    "Automatic reassignment was blocked.");
            }

            Console.WriteLine(
                ">>> WhatsApp PhoneNumberId already linked: " +
                phoneNumberId);

            return;
        }

        var wabaId =
            Environment.GetEnvironmentVariable(
                "WHATSAPP_WABA_ID") ?? "";

        var appId =
            Environment.GetEnvironmentVariable(
                "WHATSAPP_APP_ID") ?? "";

        var businessName =
            Environment.GetEnvironmentVariable(
                "WHATSAPP_BUSINESS_NAME") ??
            "مؤسسة الحوباني";

        var displayPhone =
            Environment.GetEnvironmentVariable(
                "WHATSAPP_DISPLAY_PHONE_NUMBER");

        var verifyToken =
            Environment.GetEnvironmentVariable(
                "WHATSAPP_VERIFY_TOKEN");

        // يجب أن تكون القيمة مشفرة مسبقاً باستخدام
        // آلية الحماية المعتمدة في التطبيق.
        // لا نقرأ Access Token الخام هنا.
        var encryptedToken =
            Environment.GetEnvironmentVariable(
                "WHATSAPP_BUSINESS_TOKEN_ENCRYPTED") ?? "";

        using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO WhatsAppTenants
            (
                Id,
                TenantId,
                PhoneNumberId,
                DisplayPhoneNumber,
                BusinessName,
                AccessTokenEncrypted,
                VerifyToken,
                IsActive,
                CreatedAtUtc,
                WabaId,
                BusinessTokenEncrypted,
                AppId,
                CreatedAt
            )
            VALUES
            (
                $id,
                $tenantId,
                $phoneNumberId,
                $displayPhone,
                $businessName,
                $encryptedToken,
                $verifyToken,
                1,
                $created,
                $wabaId,
                $encryptedToken,
                $appId,
                $created
            );
            """;

        var created = DateTime.UtcNow.ToString("O");

        command.Parameters.AddWithValue(
            "$id", Guid.NewGuid().ToString());

        command.Parameters.AddWithValue(
            "$tenantId", tenantId);

        command.Parameters.AddWithValue(
            "$phoneNumberId", phoneNumberId);

        command.Parameters.AddWithValue(
            "$displayPhone",
            (object?)displayPhone ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$businessName", businessName);

        command.Parameters.AddWithValue(
            "$encryptedToken", encryptedToken);

        command.Parameters.AddWithValue(
            "$verifyToken",
            (object?)verifyToken ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$created", created);

        command.Parameters.AddWithValue(
            "$wabaId", wabaId);

        command.Parameters.AddWithValue(
            "$appId", appId);

        command.ExecuteNonQuery();

        Console.WriteLine(
            ">>> WhatsApp tenant linked successfully.");

        Console.WriteLine(
            ">>> TenantId: " + tenantId);

        Console.WriteLine(
            ">>> PhoneNumberId: " + phoneNumberId);
    }

    private static string? GetExistingWhatsAppTenant(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string phoneNumberId)
    {
        using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT TenantId
            FROM WhatsAppTenants
            WHERE PhoneNumberId = $phoneNumberId
            LIMIT 1;
            """;

        command.Parameters.AddWithValue(
            "$phoneNumberId", phoneNumberId);

        var result = command.ExecuteScalar();

        return result == null || result == DBNull.Value
            ? null
            : Convert.ToString(result);
    }

    private static void ApplyWhatsAppTenantCompatibilityMigration(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        // Capture legacy shape before adding defaults: a database created by an older
        // EF model may contain only CreatedAt, which holds the original timestamp.
        var hadCreatedAtUtc = ColumnExists(connection, transaction, "WhatsAppTenants", "CreatedAtUtc");
        var hadCreatedAt = ColumnExists(connection, transaction, "WhatsAppTenants", "CreatedAt");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "TenantId",
            "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "PhoneNumberId",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "DisplayPhoneNumber",
            "TEXT NULL");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "BusinessName",
            "TEXT NULL");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "AccessTokenEncrypted",
            "TEXT NULL");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "VerifyToken",
            "TEXT NULL");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "IsActive",
            "INTEGER NOT NULL DEFAULT 1");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "CreatedAtUtc",
            "TEXT NOT NULL DEFAULT '2026-01-01 00:00:00'");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "WabaId",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "BusinessTokenEncrypted",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "AppId",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumnExists(
            connection, transaction,
            "WhatsAppTenants", "CreatedAt",
            "TEXT NOT NULL DEFAULT '2026-01-01 00:00:00'");

        if (!hadCreatedAtUtc && hadCreatedAt)
        {
            Execute(connection, transaction, """
                UPDATE WhatsAppTenants SET CreatedAtUtc = CreatedAt
                WHERE CreatedAt IS NOT NULL AND julianday(CreatedAt) IS NOT NULL;
                """);
        }

        if (!hadCreatedAt && hadCreatedAtUtc)
        {
            Execute(connection, transaction, """
                UPDATE WhatsAppTenants SET CreatedAt = CreatedAtUtc
                WHERE CreatedAtUtc IS NOT NULL AND julianday(CreatedAtUtc) IS NOT NULL;
                """);
        }

        Execute(connection, transaction, """
            UPDATE WhatsAppTenants
            SET BusinessTokenEncrypted = AccessTokenEncrypted
            WHERE
                IsActive = 1
                AND (BusinessTokenEncrypted IS NULL
                 OR BusinessTokenEncrypted = '')
                AND AccessTokenEncrypted IS NOT NULL
                AND AccessTokenEncrypted <> '';

            -- Older databases may already have these columns with nullable definitions.
            -- Normalize optional metadata only; never change account or tenant identity.
            UPDATE WhatsAppTenants SET WabaId = '' WHERE WabaId IS NULL;
            UPDATE WhatsAppTenants SET AppId = '' WHERE AppId IS NULL;
            UPDATE WhatsAppTenants SET BusinessTokenEncrypted = '' WHERE BusinessTokenEncrypted IS NULL;

            -- Canonical UTC timestamp must be present for EF materialization.
            -- Prefer an existing valid legacy date, never replace a valid canonical date.
            UPDATE WhatsAppTenants
            SET CreatedAtUtc = CASE WHEN julianday(CreatedAt) IS NOT NULL
                THEN CreatedAt ELSE '2026-01-01 00:00:00' END
            WHERE CreatedAtUtc IS NULL OR julianday(CreatedAtUtc) IS NULL;

            UPDATE WhatsAppTenants
            SET CreatedAt = CreatedAtUtc
            WHERE CreatedAt IS NULL OR julianday(CreatedAt) IS NULL;
            """);

        Console.WriteLine(
            ">>> WhatsAppTenants compatibility migration completed.");
    }

    private static void EnsureColumnExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        if (ColumnExists(
            connection,
            transaction,
            tableName,
            columnName))
            return;

        // أسماء الجداول والأعمدة هنا ثوابت داخلية وليست مدخلات مستخدم.
        using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            $"ALTER TABLE \"{tableName}\" " +
            $"ADD COLUMN \"{columnName}\" {columnDefinition};";

        command.ExecuteNonQuery();

        Console.WriteLine(
            $">>> Added column {tableName}.{columnName}");
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName)
    {
        using var command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info($tableName)
            WHERE name = $columnName;
            """;

        command.Parameters.AddWithValue(
            "$tableName", tableName);

        command.Parameters.AddWithValue(
            "$columnName", columnName);

        return Convert.ToInt32(
            command.ExecuteScalar()) > 0;
    }
}
