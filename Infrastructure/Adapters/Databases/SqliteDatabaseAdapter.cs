// Expose provider-neutral connection and transaction types / أنواع عامة.
using System.Data.Common;
// Implement the extension contract / تطبيق عقد المحول.
using ChatPlatform.Core.Abstractions;
// SQLite-specific implementation stays here / تفاصيل SQLite هنا فقط.
using Microsoft.Data.Sqlite;

namespace ChatPlatform.Infrastructure.Adapters.Databases;

// Independent external SQLite adapter; not the platform database / محول خارجي مستقل.
public sealed class SqliteDatabaseAdapter : IDatabaseAdapter
{
    // Match configured provider alias / اسم المحرك في الإعدادات.
    public string Provider => "sqlite";

    // Construct a closed connection; registry owns lifetime / بناء الاتصال دون فتحه.
    public DbConnection CreateReadOnlyConnection(string connectionString) =>
        new SqliteConnection(new SqliteConnectionStringBuilder(connectionString)
        {
            // Prevent writes, avoid persistent pools, bound busy waits / منع الكتابة وتقييد الانتظار.
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5
        }.ConnectionString);

    // Begin a deferred read transaction on an already open SQLite handle / معاملة قراءة مؤجلة.
    public ValueTask<DbTransaction> BeginReadOnlyTransactionAsync(DbConnection connection, CancellationToken ct)
    {
        // Check cancellation before synchronous SQLite work / احترام الإلغاء.
        ct.ThrowIfCancellationRequested();
        // Avoid an immediate write lock on a read-only file / تجنب قفل الكتابة.
        return ValueTask.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: true));
    }
}
