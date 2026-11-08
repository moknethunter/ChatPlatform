<div align="center">

# 🚀 ChatPlatform

### WhatsApp Business Cloud API Server · .NET 8

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://learn.microsoft.com/aspnet/core)
[![SQLite](https://img.shields.io/badge/SQLite-3-003B57?style=for-the-badge&logo=sqlite&logoColor=white)](https://sqlite.org/)
[![EF Core](https://img.shields.io/badge/EF_Core-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://learn.microsoft.com/ef/core/)
[![Docker](https://img.shields.io/badge/Docker-Ready-2496ED?style=for-the-badge&logo=docker&logoColor=white)](https://docker.com/)
[![License](https://img.shields.io/badge/License-Private-red?style=for-the-badge)](#-الترخيص)

**خادم متكامل لاستقبال وإرسال رسائل WhatsApp Business**
معمارية Event-Driven · Outbox Pattern · Webhook Signature Validation

[نظرة عامة](#-نظرة-عامة) · [المعمارية](#-المعمارية) · [قاعدة-البيانات](#-قاعدة-البيانات) · [التشغيل](#-التشغيل-السريع) · [الـAPI](#-نقاط-النهاية)

</div>

---

## 📑 جدول المحتويات

- [🎯 نظرة عامة](#-نظرة-عامة)
- [🏗️ المعمارية](#️-المعمارية)
  - [المعمارية العامة](#المعمارية-العامة)
  - [معمارية الطبقات](#معمارية-الطبقات)
  - [معمارية المكونات](#معمارية-المكونات)
  - [تسلسل الرسائل الواردة](#تسلسل-الرسائل-الواردة)
  - [تسلسل الرسائل الصادرة](#تسلسل-الرسائل-الصادرة)
- [🗄️ قاعدة البيانات](#️-قاعدة-البيانات)
  - [مخطط الكيانات (ERD)](#مخطط-الكيانات-erd)
  - [الجداول بالتفصيل](#الجداول-بالتفصيل)
  - [العلاقات](#العلاقات)
  - [الفهارس](#الفهارس)
- [🔄 دورة حياة الرسائل](#-دورة-حياة-الرسائل)
- [🗂️ بنية المشروع](#️-بنية-المشروع)
- [🚀 التشغيل السريع](#-التشغيل-السريع)
- [🔌 نقاط النهاية (API)](#-نقاط-النهاية-api)
- [🔐 الأمان والامتثال](#-الأمان-والامتثال)
- [☁️ النشر](#️-النشر)
- [🐛 استكشاف الأخطاء](#-استكشاف-الأخطاء)

---

## 🎯 نظرة عامة

**ChatPlatform** هو خادم ويب مبني بـ **ASP.NET Core 8** يوفّر حلقة وصل كاملة بين **WhatsApp Cloud API** (Meta) وأي واجهة أمامية (React, Flutter, Mobile, Web).

| القدرة | التقنية |
|--------|---------|
| 📥 **استقبال** الرسائل الواردة | Webhook موقّع (`X-Hub-Signature-256`) |
| 📤 **إرسال** الرسائل الصادرة | Outbox Pattern + Retry with backoff |
| 🔄 **معالجة غير متزامنة** | `IHostedService` + `System.Threading.Channels` |
| 💾 **التخزين** | EF Core 8 + SQLite (قابل للترقية) |
| 🔐 **الأمان** | Signature Validation + API Key + CORS |
| 📊 **المرونة** | Modular Adapters (`IIntegrationAdapter`) |
| 🌐 **REST API** | JSON + Swagger UI |

---

## 🏗️ المعمارية

### المعمارية العامة

```mermaid
flowchart TB
    subgraph META["☁️ Meta Cloud API"]
        direction LR
        FB[graph.facebook.com]
    end

    subgraph BACKEND["🖥️ ChatPlatform (.NET 8)"]
        direction TB

        subgraph ENTRY["🚪 Entry Layer"]
            WH["POST /webhook<br/>GET /webhook"]
            API["REST API<br/>/api/*"]
        end

        subgraph MIDDLEWARE["🛡️ Middleware"]
            EXC[GlobalException]
            CORS[CORS Policy]
            AUTH[ApiKey]
        end

        subgraph CORE["⚙️ Core"]
            BUS["IEventBus<br/>(In-Memory Channels)"]
            ENGINE["CoreEngine<br/>(IHostedService)"]
        end

        subgraph ADAPTERS["🔌 Adapters"]
            WA[WhatsAppAdapter]
            CN[ConsoleAdapter]
            CH[ChatAdapter]
            UC[UniversalConnector]
        end

        subgraph WORKERS["👷 Workers"]
            INBOX[WhatsAppInboxWorker]
            OUTBOX[WhatsAppOutboxWorker]
        end

        subgraph DATA["💾 Data Layer"]
            DB[("SQLite<br/>PlatformDbContext")]
            EF[EF Core 8]
        end
    end

    subgraph CLIENT["📱 Clients"]
        WEB[Web / Mobile / React]
    end

    WH --> MIDDLEWARE
    API --> MIDDLEWARE
    MIDDLEWARE --> CORE
    MIDDLEWARE --> API
    CORE <--> ADAPTERS
    ADAPTERS --> WA
    WA -->|HTTPS| FB
    FB -->|Webhook| WH
    WORKERS <--> DB
    DB <--> EF
    API <--> DB
    WEB <-->|HTTP| API

    style META fill:#e3f2fd,stroke:#1976d2,stroke-width:2px
    style BACKEND fill:#f3e5f5,stroke:#7b1fa2,stroke-width:2px
    style CLIENT fill:#e8f5e9,stroke:#388e3c,stroke-width:2px
    style DATA fill:#fff3e0,stroke:#f57c00,stroke-width:2px
    style WORKERS fill:#fce4ec,stroke:#c2185b,stroke-width:2px
