// Common connection and transaction types / أنواع عامة.
using System.Data.Common;
// Implement the common adapter contract / عقد المحول.
using ChatPlatform.Core.Abstractions;
// Keep PostgreSQL-specific behavior in this adapter / تفاصيل المحرك هنا.
using Npgsql;

namespace ChatPlatform.Infrastructure.Adapters.Databases;

// Independent external PostgreSQL adapter / محول PostgreSQL خارجي.
public sealed class PostgreSqlDatabaseAdapter : IDatabaseAdapter
{
    // Configuration key for DI lookup / معرف المحرك.
    public string Provider => "postgresql";

    // Build a closed connection with explicit security overrides / إعداد اتصال آمن.
    public DbConnection CreateReadOnlyConnection(string connectionString) =>
        new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Require verified server identity and bounded waits; avoid idle pools / تحقق TLS وحدود انتظار.
            SslMode = SslMode.VerifyFull, Pooling = false, Timeout = 5, CommandTimeout = 5
        }.ConnectionString);

    // Apply read-only semantics before returning the transaction / فرض القراءة فقط.
    public async ValueTask<DbTransaction> BeginReadOnlyTransactionAsync(DbConnection connection, CancellationToken ct)
    {
        // Start transaction on the supplied open connection / بدء المعاملة.
        var transaction = await connection.BeginTransactionAsync(ct);
        // Ensure failed initialization releases the transaction / تنظيف المعاملة عند الفشل.
        try
        {
            // Own and dispose the initialization command / تحرير أمر التهيئة.
            await using var guard = connection.CreateCommand();
            // Apply guard to this exact transaction / ربط الأمر بالمعاملة.
            guard.Transaction = transaction;
            // Disallow writes through this transaction / منع الكتابة.
            guard.CommandText = "SET TRANSACTION READ ONLY";
            // Bound command execution time / تحديد المهلة.
            guard.CommandTimeout = 5;
            // Apply the restriction before any read / تطبيق القيد قبل الاستعلام.
            await guard.ExecuteNonQueryAsync(ct);
            // Transfer disposal responsibility to registry / تسليم المعاملة للمستدعي.
            return transaction;
        }
        // Initialization failed; preserve the original error / معالجة فشل التهيئة.
        catch
        {
            // Release the transaction and connection resources / تحرير المعاملة.
            await transaction.DisposeAsync();
            // Propagate failure; do not return an unrestricted transaction / رفض اتصال غير مقيد.
            throw;
        }
    }
}
