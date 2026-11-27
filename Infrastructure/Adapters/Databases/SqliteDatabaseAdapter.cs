using System.Data.Common;
using ChatPlatform.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace ChatPlatform.Infrastructure.Adapters.Databases;

public sealed class SqliteDatabaseAdapter : IDatabaseAdapter
{
    public string Provider => "sqlite";

    public DbConnection CreateReadOnlyConnection(string connectionString) =>
        new SqliteConnection(new SqliteConnectionStringBuilder(connectionString)
        {
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5
        }.ConnectionString);

    public ValueTask<DbTransaction> BeginReadOnlyTransactionAsync(DbConnection connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: true));
    }
}
