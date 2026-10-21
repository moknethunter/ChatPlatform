using ChatPlatform.Core.Events;

namespace ChatPlatform.Core.Abstractions;

public interface IIntegrationAdapter
{
    string Name { get; }
    // الدالة التي ستستقبل الحدث وتقوم بمعالجته (إرساله لمنصة أخرى مثلاً)
    Task HandleEventAsync(PlatformEvent evt, CancellationToken ct);
}
