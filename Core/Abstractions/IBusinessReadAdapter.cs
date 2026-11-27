using System.Text.Json.Serialization;

namespace ChatPlatform.Core.Abstractions;

// Stable business operations; no arbitrary SQL or payment instructions / عمليات محددة للقراءة.
public enum BusinessReadKind { CustomerBalance, Invoice, PaymentStatus }

// Amounts are integer minor units; currency is explicit / المبلغ بوحدات العملة الصغرى.
public sealed record BusinessReadResult([property: JsonRequired] string Reference, [property: JsonRequired] long AmountMinor,
    [property: JsonRequired] string Currency, [property: JsonRequired] string Status,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? InvoiceReference=null);

// The configuration is internal and is never serialized to browser clients / إعدادات داخلية سرية.
public sealed record BusinessReadSource(string Tenant, string Id, string Provider, string? Endpoint,
    string? Token, string? DatabaseAlias, IReadOnlyDictionary<BusinessReadKind,string> Operations);

// Adapters translate a named read into provider-specific I/O / عقد مستقل لمحولات الأنظمة.
public interface IBusinessReadAdapter
{
    string Provider { get; }
    Task<BusinessReadResult?> ReadAsync(BusinessReadSource source, BusinessReadKind kind,
        string reference, CancellationToken ct);
}
