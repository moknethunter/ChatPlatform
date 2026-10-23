# 🚀 ChatPlatform - Enterprise Integration Hub

<div align="center">
  <b>A highly scalable, event-driven integration platform built with .NET 8.</b><br>
  <i>Seamlessly connecting Chat Apps, WhatsApp, Telegram, and External Webhooks with Enterprise-Grade Resilience.</i>
</div>

---

## 📖 Overview
**ChatPlatform** is a modular, event-driven integration hub designed to solve the chaos of data fragmentation across different messaging platforms. Instead of writing custom, hard-coded logic for every new integration, this platform acts as a centralized middleware. It receives events (from a chat app, webhooks, or external APIs) and routes them dynamically to registered adapters (WhatsApp, Telegram, CRM, etc.) without ever modifying the core engine.

## ✨ Key Engineering Features
- **Event-Driven Architecture:** Uses `System.Threading.Channels` for high-throughput, in-memory event dispatching.
- **Producer-Consumer Pattern:** Employs a bounded queue and multiple background workers to process events in parallel without overwhelming the server (Backpressure).
- **Fault Tolerance & Resilience:** Implements Exponential Backoff with **Jitter** and **Dead Letter Queues (DLQ)**. Failed events are retried smartly and captured for auditing if they fail permanently.
- **Modular Monolith (Clean Architecture):** Strict separation of concerns between the Core domain, Infrastructure, and API layers.
- **Zero Data Loss:** Implements **Graceful Shutdown** (Drain Timeout) to ensure all queued events are processed before the server stops.
- **Polly Integration:** Protects outbound HTTP requests against transient network failures (Circuit Breaker, Retries).
- **Real-time Communication:** Uses **SignalR** for instant bi-directional messaging with the frontend.
- **Observability:** Built-in `System.Diagnostics.Metrics` (Telemetry) for tracking event throughput, failures, and adapter latency.

## 🏗️ System Architecture

```text
[ Inbound Webhooks / APIs ]      [ Web Chat UI ]
            |                          |
            v                          v
+-----------------------------------------------------+
|                   ChatPlatform API                  |
|  (Endpoints, SignalR Hubs, Global Exception Handling)|
+-----------------------------------------------------+
            |                          ^
            v                          |
+-----------------------------------------------------+
|                 IEventBus (In-Memory)               |
|           (Publish / Subscribe Fan-out)             |
+-----------------------------------------------------+
            |
            v
+-----------------------------------------------------+
|               CoreEngine (Background)               |
|  - Bounded Queue (Backpressure)                     |
|  - Worker Pool (Parallel Processing)                |
|  - Retry with Jitter & DLQ (Resilience)             |
+-----------------------------------------------------+
            |
     +------+------+------------------+
     |             |                  |
     v             v                  v
[ChatAdapter] [UniversalConnector] [ConsoleAdapter]
 (SignalR)       (WhatsApp/Telegram)     (Logging)
