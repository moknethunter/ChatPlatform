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

        Execute(connection, transaction, """
            UPDATE WhatsAppTenants
            SET BusinessTokenEncrypted = AccessTokenEncrypted
            WHERE
                (BusinessTokenEncrypted IS NULL
                 OR BusinessTokenEncrypted = '')
                AND AccessTokenEncrypted IS NOT NULL
                AND AccessTokenEncrypted <> '';

            -- Older databases may already have these columns with nullable definitions.
            -- Normalize optional metadata only; never change account or tenant identity.
            UPDATE WhatsAppTenants SET WabaId = '' WHERE WabaId IS NULL;
            UPDATE WhatsAppTenants SET AppId = '' WHERE AppId IS NULL;
            UPDATE WhatsAppTenants SET BusinessTokenEncrypted = '' WHERE BusinessTokenEncrypted IS NULL;

            UPDATE WhatsAppTenants
            SET CreatedAt = CreatedAtUtc
            WHERE CreatedAt IS NULL OR CreatedAt = '';
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
