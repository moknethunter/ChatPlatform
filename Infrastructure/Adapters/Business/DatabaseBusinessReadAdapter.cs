using System.Text.Json;
using ChatPlatform.Core.Abstractions;
using ChatPlatform.Infrastructure.Data.Connections;

namespace ChatPlatform.Infrastructure.Adapters.Business;

// Reuse existing independent SQL adapters; never open the primary platform database here.
// إعادة استخدام محولات قواعد البيانات الخارجية دون تغيير قاعدة المنصة.
public sealed class DatabaseBusinessReadAdapter(ExternalDatabaseRegistry registry) : IBusinessReadAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Provider => "database";

    public async Task<BusinessReadResult?> ReadAsync(BusinessReadSource source, BusinessReadKind kind,
        string reference, CancellationToken ct)
    {
        // Alias and operation come from server configuration; input remains parameterized.
        // المصدر والاستعلام من إعدادات الخادم، والمدخل مجرد قيمة.
        var raw = await registry.ReadScalarAsync(source.Tenant, source.DatabaseAlias!,
            source.Operations[kind], reference, ct);
        // SQL NULL means no matching record; malformed data fails closed.
        return raw == null ? null : JsonSerializer.Deserialize<BusinessReadResult>(raw, Json);
    }
}
