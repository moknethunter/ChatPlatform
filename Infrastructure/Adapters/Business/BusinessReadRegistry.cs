using ChatPlatform.Core.Abstractions;
using ChatPlatform.Infrastructure.Data.Connections;

namespace ChatPlatform.Infrastructure.Adapters.Business;

public sealed record BusinessSourceInfo(string Id, string Provider, string[] Operations);

// Tenant-indexed routing and a bounded executor / توجيه معزول وحدود الموارد.
public sealed class BusinessReadRegistry
{
    private readonly Dictionary<string, Dictionary<string,BusinessReadSource>> _tenants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IBusinessReadAdapter> _adapters;
    private readonly SemaphoreSlim _slots = new(16,16);

    public static bool ValidReference(string? value) => value is {Length: >=1 and <=100} &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    public static bool ValidSourceAlias(string? value) => ValidAlias(value);
    private static bool ValidAlias(string? value) => value is {Length: >=1 and <=64} &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool TryKind(string? value, out BusinessReadKind kind)
    {
        kind = value switch {
            "customer-balance" => BusinessReadKind.CustomerBalance,
            "invoice" => BusinessReadKind.Invoice,
            "payment-status" => BusinessReadKind.PaymentStatus,
            _ => (BusinessReadKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    private static string OperationName(BusinessReadKind kind) => kind switch {
        BusinessReadKind.CustomerBalance => "customer-balance",
        BusinessReadKind.Invoice => "invoice",
        BusinessReadKind.PaymentStatus => "payment-status",
        _ => throw new ArgumentException("Unknown operation.")
    };

    public BusinessReadRegistry(IConfiguration config, IEnumerable<IBusinessReadAdapter> adapters,
        ExternalDatabaseRegistry databases)
    {
        _adapters = adapters.ToDictionary(x=>x.Provider,StringComparer.Ordinal);
        // No sources by default; the operator provisions explicit tenant aliases.
        foreach (var tenant in config.GetSection("BusinessIntegrations:Tenants").GetChildren())
        {
            var sources = new Dictionary<string,BusinessReadSource>(StringComparer.Ordinal);
            foreach (var entry in tenant.GetChildren())
            {
                var provider = entry["Provider"];
                var endpoint = entry["Endpoint"];
                var token = entry["Token"];
                var database = entry["DatabaseAlias"];
                if (!ValidAlias(entry.Key) || provider == null || !_adapters.ContainsKey(provider))
                    throw new InvalidOperationException("Invalid business integration.");
                if (provider == "https-gateway" &&
                    (!HttpsBusinessReadAdapter.IsAllowedEndpoint(endpoint) || string.IsNullOrWhiteSpace(token) ||
                     token.Length > 4096 || token.Any(c=>char.IsWhiteSpace(c) || char.IsControl(c))))
                    throw new InvalidOperationException("Invalid gateway configuration.");
                if (provider == "database" && (!ValidAlias(database) || !databases.Contains(tenant.Key,database!)))
                    throw new InvalidOperationException("Unknown tenant database.");
                // Each operation is separately enabled and mapped by the operator.
                var operations = new Dictionary<BusinessReadKind,string>();
                foreach (var item in entry.GetSection("Operations").GetChildren())
                {
                    if (!TryKind(item.Key,out var kind) || !ValidAlias(item.Value))
                        throw new InvalidOperationException("Invalid business operation.");
                    operations.Add(kind,item.Value!);
                }
                if (operations.Count == 0) throw new InvalidOperationException("No approved operation.");
                sources.Add(entry.Key,new(tenant.Key,entry.Key,provider,endpoint,token,database,operations));
            }
            _tenants.Add(tenant.Key,sources);
        }
    }

    public IReadOnlyList<BusinessSourceInfo> List(string tenant) =>
        _tenants.TryGetValue(tenant,out var sources)
            ? sources.Values.OrderBy(x=>x.Id,StringComparer.Ordinal)
                .Select(x=>new BusinessSourceInfo(x.Id,x.Provider,x.Operations.Keys.Select(OperationName).ToArray())).ToArray()
            : Array.Empty<BusinessSourceInfo>();

    // Revalidate configured operation on publish and when staff opens the request.
    public bool Allows(string tenant,string id,string operation) => TryKind(operation,out var kind) &&
        _tenants.TryGetValue(tenant,out var sources) && sources.TryGetValue(id,out var source) && source.Operations.ContainsKey(kind);

    public async Task<BusinessReadResult?> ReadAsync(string tenant,string id,BusinessReadKind kind,string reference,CancellationToken ct)
    {
        // Exact ownership and operation checks precede any external I/O.
        if (!_tenants.TryGetValue(tenant,out var sources) || !sources.TryGetValue(id,out var source) ||
            !source.Operations.ContainsKey(kind)) throw new KeyNotFoundException("Integration unavailable.");
        if (!ValidReference(reference)) throw new ArgumentException("Invalid reference.");
        if (!await _slots.WaitAsync(0,ct)) throw new InvalidOperationException("Integration busy.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await _adapters[source.Provider].ReadAsync(source,kind,reference,deadline.Token);
            if (result == null) return null;
            // Strict response contract: match reference, safe currency/status, no floating-point money.
            // لا يعتمد الرصيد أو حالة الدفع على مدخلات العميل أو مجرد نجاح HTTP.
            if (result.Reference != reference || result.Currency is not {Length:3} ||
                !result.Currency.All(c=>c is >= 'A' and <= 'Z') ||
                result.AmountMinor is < -1000000000000000L or > 1000000000000000L ||
                (kind != BusinessReadKind.CustomerBalance && result.AmountMinor < 0) ||
                !ValidStatus(kind,result.Status))
                throw new InvalidOperationException("Invalid business response.");
            return result;
        }
        finally { _slots.Release(); }
    }

    private static bool ValidStatus(BusinessReadKind kind,string? status) => kind switch {
        BusinessReadKind.CustomerBalance => status == "available",
        BusinessReadKind.Invoice => status is "open" or "paid" or "void" or "overdue" or "partially_paid",
        BusinessReadKind.PaymentStatus => status is "pending" or "succeeded" or "failed" or "cancelled" or "refunded",
        _ => false
    };
}
