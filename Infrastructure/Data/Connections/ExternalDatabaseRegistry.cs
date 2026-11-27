// Use shared database abstractions / أنواع قاعدة البيانات العامة.
using System.Data.Common;
// Stable formatting regardless of server locale / تنسيق مستقل عن اللغة.
using System.Globalization;
// Depend on adapter contracts, not providers / الاعتماد على العقد.
using ChatPlatform.Core.Abstractions;

namespace ChatPlatform.Infrastructure.Data.Connections;

// Safe public metadata; no SQL or secrets / معلومات عامة بلا أسرار.
public sealed record DatabaseSourceInfo(string Id, string Provider);

/// <summary>
/// Operator-provisioned, tenant-scoped external data sources, independent of PlatformDbContext.
/// Only named operator-approved scalar reads; never execute SQL supplied by a tenant or a bot.
/// </summary>
public sealed class ExternalDatabaseRegistry
{
    // Keep trusted connection and operation definitions private / تعريف مصدر خاص.
    private sealed record Source(IDatabaseAdapter Adapter, string ConnectionString, IReadOnlyDictionary<string,string> Reads);
    // Composite tenant/source ownership key / مفتاح المؤسسة والمصدر.
    private readonly Dictionary<(string Tenant, string Id), Source> _sources = new();
    // Cap concurrent operations per process, not cluster-wide / حد تزامن لكل عملية.
    private readonly SemaphoreSlim _slots = new(16,16);

    // Reject long and malformed aliases / تحقق المعرف.
    private static bool ValidId(string? id) => id is { Length: >= 1 and <= 64 } &&
        id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    // Load operator-only settings and injected providers once / تحميل الإعدادات الموثوقة.
    public ExternalDatabaseRegistry(IConfiguration configuration, IEnumerable<IDatabaseAdapter> adapters)
    {
        // Duplicate provider names fail instead of routing ambiguously / منع التعارض.
        var providers = adapters.ToDictionary(a => a.Provider, StringComparer.OrdinalIgnoreCase);
        // Read configured tenants, never request-supplied connection strings / مصادر من إعدادات الخادم.
        foreach (var tenant in configuration.GetSection("ExternalDatabases:Tenants").GetChildren())
        // Each tenant can have several independent aliases / عدة مصادر لكل مؤسسة.
        foreach (var entry in tenant.GetChildren())
        {
            // Fail closed on malformed configuration / رفض الإعداد الخاطئ.
            if (!ValidId(entry.Key) || string.IsNullOrWhiteSpace(tenant.Key))
                throw new InvalidOperationException("Invalid external database identifier.");
            // Resolve the configured provider key / تحديد المحرك.
            var provider = entry["Provider"]?.ToLowerInvariant();
            // Reference a named secret rather than returning it / مرجع سر الاتصال.
            var connectionName = entry["ConnectionName"];
            // Look up only validated connection names / قراءة سر معرف صالح.
            var connection = ValidId(connectionName) ? configuration.GetConnectionString(connectionName!) : null;
            // Refuse unknown provider or missing credentials / رفض محرك أو اتصال مجهول.
            if (provider == null || !providers.TryGetValue(provider,out var adapter) || string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("Invalid external database configuration.");
            // Approved operations only; no caller SQL / استعلامات معتمدة.
            var reads = new Dictionary<string,string>(StringComparer.Ordinal);
            // Validate every named operation / تحقق العمليات.
            foreach (var read in entry.GetSection("Reads").GetChildren())
            {
                // Bound the operation identifier and SQL text / حدود اسم العملية والنص.
                if (!ValidId(read.Key) || string.IsNullOrWhiteSpace(read.Value) || read.Value.Length > 4000)
                    throw new InvalidOperationException("Invalid external database operation.");
                // Duplicate operation IDs fail closed / منع تكرار العملية.
                reads.Add(read.Key, read.Value);
            }
            // Store source under its exact tenant and alias / عزل المصدر.
            _sources.Add((tenant.Key,entry.Key),new Source(adapter,connection,reads));
        }
    }

    // Expose metadata for current tenant only / عرض مصادر المؤسسة فقط.
    public IReadOnlyList<DatabaseSourceInfo> List(string tenant) => _sources
        .Where(x => x.Key.Tenant == tenant)
        .OrderBy(x => x.Key.Id,StringComparer.Ordinal)
        .Select(x => new DatabaseSourceInfo(x.Key.Id,x.Value.Adapter.Provider)).ToArray();

    // Check exact ownership before probing / تحقق الملكية.
    public bool Contains(string tenant,string id) => _sources.ContainsKey((tenant,id));

    // Fixed harmless connectivity query / فحص اتصال ثابت.
    public Task<string?> ProbeAsync(string tenant,string id,CancellationToken ct) =>
        ExecuteAsync(Find(tenant,id),"SELECT 1",null,ct);

    // Execute a named scalar operation with bounded input / قراءة قيمة محددة.
    public Task<string?> ReadScalarAsync(string tenant,string id,string operation,string input,CancellationToken ct)
    {
        // Resolve ownership before selecting operation / تحقق المؤسسة أولًا.
        var source = Find(tenant,id);
        // Reject unapproved operations and excessive input / رفض غير المصرح.
        if (!source.Reads.TryGetValue(operation,out var sql) || input.Length > 500)
            throw new KeyNotFoundException("External database operation unavailable.");
        // Run only trusted SQL with bound input / تنفيذ بمعاملات.
        return ExecuteAsync(source,sql,input,ct);
    }

    // No fallback to another tenant / لا رجوع لمؤسسة أخرى.
    private Source Find(string tenant,string id) => _sources.TryGetValue((tenant,id),out var source)
        ? source : throw new KeyNotFoundException("External database unavailable.");

    // Centralized bounded execution and cleanup / تنفيذ محدود الموارد.
    private async Task<string?> ExecuteAsync(Source source,string sql,string? input,CancellationToken ct)
    {
        // Bound simultaneous I/O across sources. A saturated worker returns a retryable error.
        // Fail quickly on saturation instead of unbounded waiting / منع تراكم الطلبات.
        if (!await _slots.WaitAsync(0,ct)) throw new InvalidOperationException("External database capacity busy.");
        // Release concurrency slot even on failure / ضمان تحرير الحصة.
        try
        {
            // Link caller cancellation to local operation deadline / دمج الإلغاء والمهلة.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Bound overall operation time where provider honors cancellation / تحديد المهلة.
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            // Use linked token for I/O / تمرير الإلغاء.
            var token = deadline.Token;
            // Create through chosen adapter; dispose independently / اتصال مستقل.
            await using var connection = source.Adapter.CreateReadOnlyConnection(source.ConnectionString);
            // Open only while operation runs / فتح عند الحاجة.
            await connection.OpenAsync(token);
            // Enforce provider-specific read-only guard / معاملة قراءة.
            await using var transaction = await source.Adapter.BeginReadOnlyTransactionAsync(connection,token);
            // Dispose command before connection / تحرير الأمر.
            await using var command = connection.CreateCommand();
            // Associate with guarded transaction / ربط المعاملة.
            command.Transaction = transaction;
            // SQL originates in approved configuration / نص موثوق.
            command.CommandText = sql;
            // Bound command execution / مهلة الأمر.
            command.CommandTimeout = 5;
            // Probe has no parameter; named reads bind input / ربط مدخل الاستعلام.
            if (input != null)
            {
                // Create provider-compatible parameter / معامل مستقل.
                var parameter = command.CreateParameter();
                // Use the documented @input placeholder / اسم المعامل.
                parameter.ParameterName = "input";
                // Treat input as data, not SQL / قيمة نصية.
                parameter.DbType = System.Data.DbType.String;
                // Bind raw text safely / ربط القيمة.
                parameter.Value = input;
                // Never interpolate input into SQL / منع حقن SQL.
                command.Parameters.Add(parameter);
            }
            // Return first scalar only / قيمة واحدة.
            var value = await command.ExecuteScalarAsync(token);
            // No commit: reads are deliberately not a distributed transaction.
            // Do not commit external operations / تراجع دون كتابة.
            await transaction.RollbackAsync(token);
            // Normalize SQL null / معالجة NULL.
            if (value is null or DBNull) return null;
            // Format using invariant culture / تنسيق ثابت.
            var result = Convert.ToString(value,CultureInfo.InvariantCulture);
            // Reject oversized result / حد حجم النتيجة.
            if (result?.Length > 4096) throw new InvalidOperationException("External result too large.");
            // Return only bounded scalar / إرجاع القيمة المحدودة.
            return result;
        }
        // Release the acquired slot on every path / تحرير الحصة دائمًا.
        finally { _slots.Release(); }
    }
}
