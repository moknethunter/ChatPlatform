// Provider-neutral ADO.NET types / أنواع اتصال مشتركة بين المحركات.
using System.Data.Common;

namespace ChatPlatform.Core.Abstractions;

/// <summary>Trusted, server-registered adapter for an external database.
/// It does not replace the platform's primary persistence or manage tenant routing.</summary>
public interface IDatabaseAdapter
{
    // Stable registration key; must be unique / معرف محرك فريد.
    string Provider { get; }
    // Return a closed, read-only configured connection; caller owns disposal / اتصال قراءة مغلق.
    DbConnection CreateReadOnlyConnection(string connectionString);
    // Establish read-only transaction after open; propagate cancellation / معاملة قراءة بعد فتح الاتصال.
    ValueTask<DbTransaction> BeginReadOnlyTransactionAsync(DbConnection connection, CancellationToken ct);
}
