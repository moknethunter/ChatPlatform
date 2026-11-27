using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ChatPlatform.Infrastructure.Data.Connections;

public sealed record DatabaseSourceInfo(string Id, string Provider);

/// <summary>
/// Operator-provisioned, tenant-scoped external data sources, independent of PlatformDbContext.
/// Only named operator-approved scalar reads; never execute SQL supplied by a tenant or a bot.
/// </summary>
public sealed class ExternalDatabaseRegistry
{
    private sealed record Source(string Provider, string ConnectionString, IReadOnlyDictionary<string,string> Reads);
    private readonly Dictionary<(string Tenant, string Id), Source> _sources = new();
    private readonly SemaphoreSlim _slots = new(16,16);

    private static bool ValidId(string? id) => id is { Length: >= 1 and <= 64 } &&
        id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    public ExternalDatabaseRegistry(IConfiguration configuration)
    {
        foreach (var tenant in configuration.GetSection("ExternalDatabases:Tenants").GetChildren())
        foreach (var entry in tenant.GetChildren())
        {
            if (!ValidId(entry.Key) || string.IsNullOrWhiteSpace(tenant.Key))
                throw new InvalidOperationException("Invalid external database identifier.");
            var provider = entry["Provider"]?.ToLowerInvariant();
            var connectionName = entry["ConnectionName"];
            var connection = ValidId(connectionName) ? configuration.GetConnectionString(connectionName!) : null;
            if (provider is not ("sqlite" or "postgresql") || string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("Invalid external database configuration.");
            var reads = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var read in entry.GetSection("Reads").GetChildren())
            {
                if (!ValidId(read.Key) || string.IsNullOrWhiteSpace(read.Value) || read.Value.Length > 4000)
                    throw new InvalidOperationException("Invalid external database operation.");
                reads.Add(read.Key, read.Value);
            }
            _sources.Add((tenant.Key,entry.Key),new Source(provider,connection,reads));
        }
    }

    public IReadOnlyList<DatabaseSourceInfo> List(string tenant) => _sources
        .Where(x => x.Key.Tenant == tenant)
        .OrderBy(x => x.Key.Id,StringComparer.Ordinal)
        .Select(x => new DatabaseSourceInfo(x.Key.Id,x.Value.Provider)).ToArray();

    public bool Contains(string tenant,string id) => _sources.ContainsKey((tenant,id));

    public Task<string?> ProbeAsync(string tenant,string id,CancellationToken ct) =>
        ExecuteAsync(Find(tenant,id),"SELECT 1",null,ct);

    public Task<string?> ReadScalarAsync(string tenant,string id,string operation,string input,CancellationToken ct)
    {
        var source = Find(tenant,id);
        if (!source.Reads.TryGetValue(operation,out var sql) || input.Length > 500)
            throw new KeyNotFoundException("External database operation unavailable.");
        return ExecuteAsync(source,sql,input,ct);
    }

    private Source Find(string tenant,string id) => _sources.TryGetValue((tenant,id),out var source)
        ? source : throw new KeyNotFoundException("External database unavailable.");

    private static DbConnection CreateConnection(Source source)
    {
        if (source.Provider == "sqlite")
        {
            var builder = new SqliteConnectionStringBuilder(source.ConnectionString)
            { Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 };
            return new SqliteConnection(builder.ConnectionString);
        }
        var pg = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            SslMode = SslMode.VerifyFull, Pooling = true, MinPoolSize = 0,
            MaxPoolSize = 4, Timeout = 5, CommandTimeout = 5
        };
        return new NpgsqlConnection(pg.ConnectionString);
    }

    private async Task<string?> ExecuteAsync(Source source,string sql,string? input,CancellationToken ct)
    {
        // Bound simultaneous I/O across sources. A saturated worker returns a retryable error.
        if (!await _slots.WaitAsync(0,ct)) throw new InvalidOperationException("External database capacity busy.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var token = deadline.Token;
            await using var connection = CreateConnection(source);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            if (source.Provider == "postgresql")
            {
                await using var guard = connection.CreateCommand();
                guard.Transaction = transaction;
                guard.CommandText = "SET TRANSACTION READ ONLY";
                guard.CommandTimeout = 5;
                await guard.ExecuteNonQueryAsync(token);
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = 5;
            if (input != null)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "input";
                parameter.DbType = System.Data.DbType.String;
                parameter.Value = input;
                command.Parameters.Add(parameter);
            }
            var value = await command.ExecuteScalarAsync(token);
            // No commit: reads are deliberately not a distributed transaction.
            await transaction.RollbackAsync(token);
            if (value is null or DBNull) return null;
            var result = Convert.ToString(value,CultureInfo.InvariantCulture);
            if (result?.Length > 4096) throw new InvalidOperationException("External result too large.");
            return result;
        }
        finally { _slots.Release(); }
    }
}
