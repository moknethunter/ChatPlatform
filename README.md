# WhatsApp Business Console

> منصة متكاملة لإدارة محادثات WhatsApp Business عبر Meta Cloud API،
> مبنية بمعمارية Event-Driven مع دعم SignalR، Outbox Pattern،
> ونافذة الـ 24 ساعة المطلوبة من Meta.

---

## 📌 نظرة عامة

هذا المشروع يتكوّن من مستودعين متكاملين يعملان معاً:

| المكوّن | التقنية | الدور | المستودع |
|---------|---------|-------|----------|
| **ChatPlatform** | .NET 8 / ASP.NET Core | الخادم الرئيسي — يستقبل Webhooks، يدير المحادثات، يتصل بـ Meta | [`moknethunter/ChatPlatform`](https://github.com/moknethunter/ChatPlatform) |
| **WhatsApp Inbox UI** | Next.js 15 / React 19 | واجهة الويب — عرض المحادثات، إدارة الأرقام، الإعدادات | [`moknethunter/whatsapp-cloud-inbox`](https://github.com/moknethunter/whatsapp-cloud-inbox) |
| **WhatsApp SDK** (مرجعي) | .NET Library | مكتبة fork من `gabrieldwight/Whatsapp-Business-Cloud-Api-Net` | [`moknethunter/Whatsapp-Business-Cloud-Api-Net`](https://github.com/moknethunter/Whatsapp-Business-Cloud-Api-Net) |

---

## 🏗️ معمارية النظام
