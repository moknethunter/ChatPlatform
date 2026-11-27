// Cryptographic random state and hashing / توليد وبصمة آمنة.
using System.Security.Cryptography;
// Stable UTF-8 bytes for hashing / ترميز البصمة.
using System.Text;
// Use the current platform database / قاعدة المنصة الحالية.
using ChatPlatform.Infrastructure.Data;
// Parameterized SQL and transactions / معاملات SQL آمنة.
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Adapters.WhatsApp;

/// <summary>Shared durable signup correlation. Stores hashes, never OAuth codes or tokens.
/// One attempt per authenticated session. Consumption is one atomic conditional DELETE.</summary>
public sealed class DatabaseSignupAttemptStore(PlatformDbContext db) : IEmbeddedSignupAttemptStore
{
    // Hash correlation values before storing them / تخزين بصمات لا قيم أصلية.
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Create a five-minute tenant/session-bound attempt / إنشاء محاولة محددة الصلاحية.
    public async Task<string?> StartAsync(string tenant, string session, string appId, string configId,
        DateTimeOffset now, CancellationToken ct)
    {
        // Do not create anonymous attempts / رفض الجلسة المجهولة.
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(session)) return null;
        // Avoid storing raw session ID / بصمة الجلسة.
        var sessionHash = Hash(session);
        // Generate 256-bit random correlation secret / قيمة عشوائية.
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // Persist only the state hash / بصمة الحالة.
        var stateHash = Hash(state);
        // Bound validity to five minutes / صلاحية خمس دقائق.
        var expires = now.UtcDateTime.AddMinutes(5);
        // Normalize timestamps to UTC / توحيد الوقت.
        var utc = now.UtcDateTime;
        // SQLite and PostgreSQL both support ON CONFLICT. Provider-specific schema
        // provisioning and concurrency qualification remain separate deployment work.
        // Cleanup and replacement commit together / معاملة واحدة.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Parameterized maintenance/upsert; do not interpolate untrusted SQL / SQL بمعاملات.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM EmbeddedSignupAttempts WHERE ExpiresAtUtc <= {utc}
            """, ct);
        // Parameterized maintenance/upsert; do not interpolate untrusted SQL / SQL بمعاملات.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO EmbeddedSignupAttempts (TenantId, SessionHash, StateHash, AppId, ConfigId, ExpiresAtUtc)
            VALUES ({tenant}, {sessionHash}, {stateHash}, {appId}, {configId}, {expires})
            ON CONFLICT (TenantId, SessionHash) DO UPDATE SET
                StateHash = excluded.StateHash, AppId = excluded.AppId,
                ConfigId = excluded.ConfigId, ExpiresAtUtc = excluded.ExpiresAtUtc
            """, ct);
        // Expose the state only after durable commit / حفظ قبل الإرجاع.
        await tx.CommitAsync(ct);
        // Return raw state once to authenticated caller / إرجاع القيمة للمستدعي.
        return state;
    }

    // Consume once, bound to all ownership dimensions / استهلاك مرة واحدة.
    public async Task<bool> ConsumeAsync(string? state, string tenant, string session, string appId,
        string configId, DateTimeOffset now, CancellationToken ct)
    {
        // Reject malformed or missing correlation values / تحقق المدخلات.
        if (state is not { Length: 64 } || !state.All(char.IsAsciiHexDigit) ||
            string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(session)) return false;
        // Avoid storing raw session ID / بصمة الجلسة.
        var sessionHash = Hash(session);
        // Persist only the state hash / بصمة الحالة.
        var stateHash = Hash(state);
        // Normalize timestamps to UTC / توحيد الوقت.
        var utc = now.UtcDateTime;
        // Atomic conditional delete: one winner, no read-then-delete race / حذف ذري مشروط.
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM EmbeddedSignupAttempts
            WHERE TenantId = {tenant} AND SessionHash = {sessionHash}
                AND StateHash = {stateHash} AND AppId = {appId} AND ConfigId = {configId}
                AND ExpiresAtUtc > {utc}
            """, ct) == 1;
    }
}

// Contract permits other trusted storage implementations / عقد مخزن قابل للاستبدال.
public interface IEmbeddedSignupAttemptStore
{
    // Create or supersede current attempt / إنشاء أو استبدال المحاولة.
    Task<string?> StartAsync(string tenant, string session, string appId, string configId, DateTimeOffset now, CancellationToken ct);
    // True only for valid unconsumed matching state / نجاح لمرة واحدة.
    Task<bool> ConsumeAsync(string? state, string tenant, string session, string appId, string configId, DateTimeOffset now, CancellationToken ct);
}
