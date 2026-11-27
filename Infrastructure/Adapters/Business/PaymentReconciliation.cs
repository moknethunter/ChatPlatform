using ChatPlatform.Core.Abstractions;

namespace ChatPlatform.Infrastructure.Adapters.Business;

// A read-only comparison, never a settlement or an accounting write.
// مطابقة للقراءة فقط، لا تسوية مالية ولا قيد محاسبي.
public sealed record ReconciliationPair(string Id,string InvoiceSource,string PaymentSource);
public sealed record ReconciliationResult(string Outcome,BusinessReadResult? Invoice,BusinessReadResult? Payment);

public sealed class PaymentReconciliation
{
    private readonly BusinessReadRegistry _registry;
    private readonly Dictionary<string,Dictionary<string,ReconciliationPair>> _pairs=new(StringComparer.Ordinal);

    public PaymentReconciliation(IConfiguration config,BusinessReadRegistry registry)
    {
        _registry=registry;
        // Each pair is explicitly provisioned by the operator for the same merchant.
        foreach(var tenant in config.GetSection("PaymentReconciliation:Tenants").GetChildren())
        {
            var pairs=new Dictionary<string,ReconciliationPair>(StringComparer.Ordinal);
            foreach(var entry in tenant.GetChildren())
            {
                var invoice=entry["InvoiceSource"];
                var payment=entry["PaymentSource"];
                if(!BusinessReadRegistry.ValidSourceAlias(entry.Key) ||
                    !BusinessReadRegistry.ValidSourceAlias(invoice) || !BusinessReadRegistry.ValidSourceAlias(payment) ||
                    !registry.Allows(tenant.Key,invoice!,"invoice") || !registry.Allows(tenant.Key,payment!,"payment-status"))
                    throw new InvalidOperationException("Invalid reconciliation pair.");
                pairs.Add(entry.Key,new(entry.Key,invoice!,payment!));
            }
            _pairs.Add(tenant.Key,pairs);
        }
    }

    public IReadOnlyList<ReconciliationPair> List(string tenant) =>
        _pairs.TryGetValue(tenant,out var pairs)?pairs.Values.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray():Array.Empty<ReconciliationPair>();

    public async Task<ReconciliationResult> CompareAsync(string tenant,string pairId,string invoiceRef,string paymentRef,CancellationToken ct)
    {
        // Exact tenant ownership before any external calls / تحقق العزل أولًا.
        if(!_pairs.TryGetValue(tenant,out var pairs)||!pairs.TryGetValue(pairId,out var pair))
            throw new KeyNotFoundException("Reconciliation pair unavailable.");
        if(!BusinessReadRegistry.ValidReference(invoiceRef)||!BusinessReadRegistry.ValidReference(paymentRef))
            throw new ArgumentException("Invalid reference.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var invoiceTask=_registry.ReadAsync(tenant,pair.InvoiceSource,BusinessReadKind.Invoice,invoiceRef,deadline.Token);
        var paymentTask=_registry.ReadAsync(tenant,pair.PaymentSource,BusinessReadKind.PaymentStatus,paymentRef,deadline.Token);
        await Task.WhenAll(invoiceTask,paymentTask);
        return Compare(await invoiceTask,await paymentTask);
    }

    // Require provider-attested invoice link, not merely equal money.
    // لا يكفي تطابق المبلغ؛ يجب أن يحدد مصدر الدفع الفاتورة المرتبطة.
    public static ReconciliationResult Compare(BusinessReadResult? invoice,BusinessReadResult? payment)
    {
        var outcome=invoice==null||payment==null?"record_unavailable":
            payment.InvoiceReference!=invoice.Reference?"invoice_link_unverified":
            payment.Currency!=invoice.Currency?"currency_mismatch":
            payment.Status!="succeeded"?"payment_not_succeeded":
            invoice.Status is "void" or "partially_paid"?"invoice_requires_review":
            invoice.AmountMinor<=0||payment.AmountMinor!=invoice.AmountMinor?"amount_mismatch":
            "matched_for_review";
        // Even a match is only a point-in-time observation. No write, delivery or ledger update.
        return new(outcome,invoice,payment);
    }
}
