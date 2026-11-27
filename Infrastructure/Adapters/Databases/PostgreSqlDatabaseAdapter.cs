using System.Data.Common;
using ChatPlatform.Core.Abstractions;
using Npgsql;

namespace ChatPlatform.Infrastructure.Adapters.Databases;

public sealed class PostgreSqlDatabaseAdapter : IDatabaseAdapter
{
    public string Provider => "postgresql";

    public DbConnection CreateReadOnlyConnection(string connectionString) =>
        new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
        {
            SslMode = SslMode.VerifyFull, Pooling = false, Timeout = 5, CommandTimeout = 5
        }.ConnectionString);

    public async ValueTask<DbTransaction> BeginReadOnlyTransactionAsync(DbConnection connection, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var guard = connection.CreateCommand();
            guard.Transaction = transaction;
            guard.CommandText = "SET TRANSACTION READ ONLY";
            guard.CommandTimeout = 5;
            await guard.ExecuteNonQueryAsync(ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
