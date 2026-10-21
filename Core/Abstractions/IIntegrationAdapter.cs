using ChatPlatform.Core.Events;

namespace ChatPlatform.Core.Abstractions;

public interface IIntegrationAdapter
{
    string Name { get; }
    Task HandleEventAsync(PlatformEvent evt, CancellationToken ct);
}
