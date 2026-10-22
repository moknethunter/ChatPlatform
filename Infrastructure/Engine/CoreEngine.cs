using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;

using ChatPlatform.Core.Abstractions;
using ChatPlatform.Core.Events;
using ChatPlatform.Infrastructure.Configuration;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatPlatform.Infrastructure.Engine;

public sealed class CoreEngine : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly IReadOnlyList<IIntegrationAdapter> _adapters;
    private readonly ILogger<CoreEngine> _log;
    private readonly EngineOptions _options;
    private readonly Channel<PlatformEvent> _channel;
    private readonly CancellationTokenSource _processingCts = new();

    // استخدام static لمنع تكرار تعريف العدادات في الذاكرة
    private static readonly Meter Meter = new("ChatPlatform.Engine", "3.0.0");
    private static readonly Counter<long> EventsReceived = Meter.CreateCounter<long>("engine.events.received");
    private static readonly Counter<long> EventsCompleted = Meter.CreateCounter<long>("engine.events.completed");
    private static readonly Counter<long> EventsFailed = Meter.CreateCounter<long>("engine.events.failed");
    private static readonly Counter<long> AdapterFailures = Meter.CreateCounter<long>("engine.adapter.failures");
    private static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>("engine.events.dead_lettered");
    
    private static readonly Histogram<double> EventDuration = Meter.CreateHistogram<double>("engine.event.duration", unit: "ms");
    private static readonly Histogram<double> AdapterDuration = Meter.CreateHistogram<double>("engine.adapter.duration", unit: "ms");

    public CoreEngine(
        IEventBus bus,
        IEnumerable<IIntegrationAdapter> adapters,
        IOptions<EngineOptions> options,
        ILogger<CoreEngine> log)
    {
        _bus = bus;
        _adapters = adapters.ToArray();
        _log = log;
        _options = options.Value;

        ValidateOptions(_options);

        _channel = Channel.CreateBounded<PlatformEvent>(
            new BoundedChannelOptions(_options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = _options.WorkerCount == 1,
                AllowSynchronousContinuations = false
            });
    }

    private static void ValidateOptions(EngineOptions options)
    {
        if (options.WorkerCount < 1) throw new ArgumentOutOfRangeException(nameof(options.WorkerCount));
        if (options.QueueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(options.QueueCapacity));
        if (options.MaxRetryAttempts < 0) throw new ArgumentOutOfRangeException(nameof(options.MaxRetryAttempts));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("🚀 Core Engine 3.0 starting. Adapters={AdapterCount}, Workers={WorkerCount}", _adapters.Count, _options.WorkerCount);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _processingCts.Token);
        var processingToken = linkedCts.Token;

        var workers = Enumerable.Range(0, _options.WorkerCount)
            .Select(id => RunWorkerAsync(id, processingToken))
            .ToArray();

        try
        {
            await ProduceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { _log.LogCritical(ex, "Event producer failed."); }
        finally { _channel.Writer.TryComplete(); }

        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.DrainTimeoutSeconds));
        try
        {
            await Task.WhenAll(workers).WaitAsync(drainCts.Token);
        }
        catch (OperationCanceledException) when (drainCts.IsCancellationRequested)
        {
            _log.LogWarning("Drain timeout reached. Cancelling remaining processing.");
            _processingCts.Cancel();
        }

        _log.LogInformation("Core Engine stopped.");
    }

    private async Task ProduceAsync(CancellationToken ct)
    {
        await foreach (var evt in _bus.SubscribeAsync(ct).WithCancellation(ct))
        {
            await _channel.Writer.WriteAsync(evt, ct);
            if (_options.EnableMetrics) EventsReceived.Add(1, new KeyValuePair<string, object?>("event_type", evt.Type));
        }
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            {
                await ProcessEventAsync(workerId, evt, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _log.LogCritical(ex, "Worker {WorkerId} terminated unexpectedly.", workerId); throw; }
    }

    private async Task ProcessEventAsync(int workerId, PlatformEvent evt, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var tasks = _adapters.Select(adapter => HandleAdapterWithRetryAsync(adapter, evt, workerId, ct));
            var results = await Task.WhenAll(tasks);

            if (results.All(success => success))
            {
                if (_options.EnableMetrics) EventsCompleted.Add(1);
            }
            else
            {
                if (_options.EnableMetrics) EventsFailed.Add(1);
                _log.LogWarning("Event {EventId} completed with one or more adapter failures.", evt.Id);
                
                // 💡 الميزة الجديدة: إذا فشل الحدث نهائياً، ننشر حدثاً جديداً للـ Dead Letter Queue
                // لكي يستطيع مدير النظام (أو لوحة التحكم) رؤيته وإعادة معالجته.
                await PublishDeadLetterAsync(evt, ct);
            }
        }
        finally
        {
            stopwatch.Stop();
            if (_options.EnableMetrics) EventDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<bool> HandleAdapterWithRetryAsync(IIntegrationAdapter adapter, PlatformEvent evt, int workerId, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await adapter.HandleEventAsync(evt, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_options.EnableMetrics) AdapterFailures.Add(1, new KeyValuePair<string, object?>("adapter", adapter.Name));

                var isLastAttempt = attempt == _options.MaxRetryAttempts;

                // 💡 الميزة الجديدة: عدم إعادة المحاولة في حالات البيانات الخاطئة (Permanent Errors)
                if (ex is ArgumentException || ex is FormatException || ex is JsonException)
                {
                    _log.LogError(ex, "Adapter {AdapterName} failed with a permanent error (Bad Data). No retry will be attempted.", adapter.Name);
                    return false;
                }

                if (isLastAttempt)
                {
                    _log.LogError(ex, "Adapter {AdapterName} failed permanently for event {EventId} after {Attempts} attempts.", adapter.Name, evt.Id, attempt + 1);
                    return false;
                }

                var delay = CalculateRetryDelayWithJitter(attempt);
                _log.LogWarning("Adapter {AdapterName} failed for event {EventId}. Retry {RetryNumber} after {DelayMs} ms.", adapter.Name, evt.Id, attempt + 1, delay.TotalMilliseconds);
                
                await Task.Delay(delay, ct);
            }
            finally
            {
                stopwatch.Stop();
                if (_options.EnableMetrics) AdapterDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("adapter", adapter.Name));
                if (stopwatch.ElapsedMilliseconds > _options.SlowAdapterThresholdMilliseconds)
                    _log.LogWarning("Slow adapter {AdapterName}: {ElapsedMs} ms for event {EventId}.", adapter.Name, stopwatch.ElapsedMilliseconds, evt.Id);
            }
        }
        return false;
    }

    private TimeSpan CalculateRetryDelayWithJitter(int attempt)
    {
        // 💡 الميزة الجديدة: إضافة Jitter (عشوائية) لمنع "القطيع الهادر"
        var exponentialDelay = _options.RetryBaseDelayMilliseconds * Math.Pow(2, Math.Min(attempt, 10));
        var cappedDelay = Math.Min(exponentialDelay, _options.MaxRetryDelayMilliseconds);
        
        // إضافة تأخير عشوائي بين 0 و 200 مللي ثانية
        var jitter = Random.Shared.Next(0, 200); 

        return TimeSpan.FromMilliseconds(cappedDelay + jitter);
    }

    private async Task PublishDeadLetterAsync(PlatformEvent originalEvent, CancellationToken ct)
    {
        try
        {
            var dlqPayload = JsonSerializer.SerializeToElement(new
            {
                OriginalEventId = originalEvent.Id,
                OriginalType = originalEvent.Type,
                OriginalPayload = originalEvent.Payload,
                FailedAt = DateTimeOffset.UtcNow
            });

            var dlqEvent = new PlatformEvent
            {
                Type = "engine.event.dead_letter",
                Source = "CoreEngine",
                Payload = dlqPayload
            };

            // نعيد نشر الحدث في النواة ليلتقطه أي نظام مراقبة
            await _bus.PublishAsync(dlqEvent, ct);
            if (_options.EnableMetrics) DeadLetters.Add(1);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to publish dead letter for event {EventId}", originalEvent.Id);
        }
    }

    public override void Dispose()
    {
        _processingCts.Dispose();
        base.Dispose();
    }
}
