// Use the common event envelope / غلاف الأحداث المشترك.
using ChatPlatform.Core.Events;

namespace ChatPlatform.Core.Abstractions;

// Independent event handler contract / عقد المحول المستقل.
public interface IIntegrationAdapter
{
    // Stable diagnostic label; never include credentials / اسم للتشخيص دون أسرار.
    string Name { get; }
    // Handle supported events only; respect cancellation and replay / معالجة الأحداث المدعومة مع مراعاة التكرار.
    Task HandleEventAsync(PlatformEvent evt, CancellationToken ct);
}
