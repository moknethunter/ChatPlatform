using System.Data.Common;

namespace ChatPlatform.Core.Abstractions;

/// <summary>Trusted, server-registered adapter for an external database.
/// It does not replace the platform's primary persistence or manage tenant routing.</summary>
public interface IDatabaseAdapter
{
    string Provider { get; }
    DbConnection CreateReadOnlyConnection(string connectionString);
    ValueTask<DbTransaction> BeginReadOnlyTransactionAsync(DbConnection connection, CancellationToken ct);
}
