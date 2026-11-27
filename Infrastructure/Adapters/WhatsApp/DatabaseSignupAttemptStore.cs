using System.Security.Cryptography;
using System.Text;
using ChatPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Shared durable signup correlation. Stores hashes, never OAuth codes or tokens.
/// One attempt per authenticated session. Consumption is one atomic conditional DELETE.</summary>
public sealed class DatabaseSignupAttemptStore(PlatformDbContext db) : IEmbeddedSignupAttemptStore
{
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<string?> StartAsync(string tenant, string session, string appId, string configId,
        DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(session)) return null;
        var sessionHash = Hash(session);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var stateHash = Hash(state);
        var expires = now.UtcDateTime.AddMinutes(5);
        var utc = now.UtcDateTime;
        // SQLite and PostgreSQL both support ON CONFLICT. Provider-specific schema
        // provisioning and concurrency qualification remain separate deployment work.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM EmbeddedSignupAttempts WHERE ExpiresAtUtc <= {utc}
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO EmbeddedSignupAttempts (TenantId, SessionHash, StateHash, AppId, ConfigId, ExpiresAtUtc)
            VALUES ({tenant}, {sessionHash}, {stateHash}, {appId}, {configId}, {expires})
            ON CONFLICT (TenantId, SessionHash) DO UPDATE SET
                StateHash = excluded.StateHash, AppId = excluded.AppId,
                ConfigId = excluded.ConfigId, ExpiresAtUtc = excluded.ExpiresAtUtc
            """, ct);
        await tx.CommitAsync(ct);
        return state;
    }

    public async Task<bool> ConsumeAsync(string? state, string tenant, string session, string appId,
        string configId, DateTimeOffset now, CancellationToken ct)
    {
        if (state is not { Length: 64 } || !state.All(char.IsAsciiHexDigit) ||
            string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(session)) return false;
        var sessionHash = Hash(session);
        var stateHash = Hash(state);
        var utc = now.UtcDateTime;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM EmbeddedSignupAttempts
            WHERE TenantId = {tenant} AND SessionHash = {sessionHash}
                AND StateHash = {stateHash} AND AppId = {appId} AND ConfigId = {configId}
                AND ExpiresAtUtc > {utc}
            """, ct) == 1;
    }
}

public interface IEmbeddedSignupAttemptStore
{
    Task<string?> StartAsync(string tenant, string session, string appId, string configId, DateTimeOffset now, CancellationToken ct);
    Task<bool> ConsumeAsync(string? state, string tenant, string session, string appId, string configId, DateTimeOffset now, CancellationToken ct);
}
