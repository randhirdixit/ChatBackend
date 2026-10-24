# ChatBackend

A production-ready, provider-agnostic AI chat backend built with **.NET 10 / C# 14**.  
Powers conversational AI over **ASP.NET Core Minimal APIs** and **Meta WhatsApp Cloud API (Graph API)**, backed by **Groq Cloud API** and **SQLite** via Entity Framework Core. Interactive API documentation powered by **Scalar**.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                  API Layer  (Minimal APIs)                   │
│          POST /api/chat      POST /webhook/whatsapp          │
└─────────────────┬───────────────────────┬───────────────────┘
                  │                       │
         ┌────────▼───────────────────────▼────────┐
         │           ChatOrchestrator               │
         │  resolve user → persist → call LLM       │
         │  → persist reply → return ChatResponse   │
         └────────┬────────────────────────────────┘
                  │
         ┌────────▼────────────────────┐
         │      ILLMClient             │  ← provider contract
         ├─────────────────────────────┤
         │  GroqAdapter                │  ← openai/gpt-oss-120b / llama-3.x
         └────────┬────────────────────┘
                  │
         ┌────────▼────────────────────┐
         │      Infrastructure         │
         │  EF Core + SQLite           │
         │  Polly (retry + timeout)    │
         │  Meta WhatsApp Cloud API    │
         └─────────────────────────────┘
```

### Project Layout

```
ChatBackend/
├── ChatBackend.slnx
├── .env.sample                         ← copy → .env, fill secrets
│
├── ChatBackend.Core/                   ← Zero dependencies — pure domain
│   ├── Interfaces/
│   │   ├── ILLMClient.cs               ← Provider contract
│   │   └── IChatOrchestrator.cs
│   ├── DTOs/
│   │   ├── ChatRequest.cs
│   │   ├── ChatResponse.cs
│   │   └── LLMMessage.cs
│   └── Models/
│       ├── User.cs
│       ├── Conversation.cs
│       └── Message.cs
│
├── ChatBackend.Infrastructure/         ← EF Core, Groq adapter, Meta Cloud API
│   ├── Data/
│   │   ├── AppDbContext.cs
│   │   ├── DesignTimeDbContextFactory.cs
│   │   └── Migrations/
│   ├── LLM/
│   │   ├── GroqAdapter.cs              ← Primary adapter (Groq Cloud API)
│   │   └── GroqModels.cs               ← Wire format records
│   ├── Meta/
│   │   ├── MetaWhatsAppHandler.cs      ← Webhook verification & Graph API messaging
│   │   ├── MetaModels.cs               ← Meta webhook & send payload DTOs
│   │   ├── WhatsAppMessageChannel.cs   ← In-memory bounded queue channel
│   │   └── WhatsAppMessageWorker.cs    ← Background worker draining webhook queue
│   ├── Orchestrator/
│   │   └── ChatOrchestrator.cs         ← Turn management & context assembly
│   └── Extensions/
│       └── InfrastructureServiceExtensions.cs
│
├── ChatBackend.Api/                    ← Entry point
│   ├── Program.cs                      ← Minimal APIs, RateLimiter, CORS, Migrations
│   ├── Endpoints/
│   │   ├── ChatEndpoints.cs            ← POST /api/chat, GET /api/conversations/{id}
│   │   └── WhatsAppEndpoints.cs        ← GET/POST /webhook/whatsapp
│   ├── Middleware/
│   │   └── GlobalExceptionMiddleware.cs ← RFC 7807 ProblemDetails
│   ├── appsettings.json
│   └── appsettings.Development.json
│
└── ChatBackend.Tests/                  ← xUnit v3 + Moq + FluentAssertions
    ├── ChatOrchestratorTests.cs
    ├── GroqAdapterTests.cs
    ├── MetaWhatsAppHandlerTests.cs
    └── WhatsAppMessageWorkerTests.cs
```

---

## Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| .NET SDK | 10.0+ | https://dotnet.microsoft.com/download |
| dotnet-ef | latest | `dotnet tool install -g dotnet-ef` |
| Groq API key | — | https://console.groq.com |
| Meta for Developers Account | — | https://developers.facebook.com |
| ngrok (optional) | — | https://ngrok.com (for WhatsApp local webhook testing) |

---

## Quick Start

### 1 — Clone & restore

```bash
git clone <your-repo-url>
cd ChatBackend
dotnet restore
```

### 2 — Set secrets

**Option A — .env file (automatically loaded on startup via DotNetEnv)**

```bash
cp .env.sample .env
# edit .env with your real keys
```

> **Note:** `ChatBackend.Api` automatically locates and loads `.env` on startup in Visual Studio, Rider, or `dotnet run` without any manual exports.

**Option B — dotnet user-secrets (cleanest for VS / Rider / CLI)**

```bash
cd ChatBackend.Api
dotnet user-secrets set "LLM:Groq:ApiKey"     "gsk_xxxx"
dotnet user-secrets set "Meta:AccessToken"    "EAAxxxx"
dotnet user-secrets set "Meta:PhoneNumberId"  "123456789012345"
dotnet user-secrets set "Meta:VerifyToken"    "your_secret_verify_token"
```

### 3 — Apply database migration

```bash
cd ChatBackend.Api
dotnet ef database update \
  --project ../ChatBackend.Infrastructure \
  --startup-project .
```

> The application automatically runs pending EF Core migrations on startup, so this step is optional during development.

### 4 — Run

```bash
cd ChatBackend.Api
dotnet run
```

- API Base URL: **http://localhost:5000** (or https://localhost:5001)  
- Interactive API Documentation (Scalar): **http://localhost:5000/scalar/v1** (or navigate to **http://localhost:5000/** in Development)

---

## API Reference

### `POST /api/chat`

Send a chat message and receive an AI response.

**Request body:**

```json
{
  "userMessage":    "What is recursion?",
  "conversationId": null,
  "userId":         "user-123",
  "source":         "api"
}
```

**Response `200 OK`:**

```json
{
  "assistantMessage": "Recursion is a technique where a function calls itself...",
  "conversationId":   "3fa85f64-...",
  "messageId":        "7cb92e11-...",
  "provider":         "Groq",
  "timestamp":        "2026-08-30T12:00:00Z"
}
```

**Continue the conversation** by passing `conversationId` from the previous response:

```json
{
  "userMessage":    "Can you give me a code example?",
  "conversationId": "3fa85f64-..."
}
```

### `POST /api/chat/stream`

Stream chat response token-by-token using **Server-Sent Events (SSE)**.

**Request body:** Same as `/api/chat`.

**Response `200 OK` (`text/event-stream`):**

```text
data: {"token":"Hello","conversationId":"3fa85f64-..."}

data: {"token":" world","conversationId":"3fa85f64-..."}

data: [DONE]
```

### `GET /api/conversations/{conversationId}`

Returns the full message history for the given conversation ID.

### `GET /webhook/whatsapp`

Meta webhook ownership verification handshake: validates `hub.verify_token` against `Meta:VerifyToken` and returns `hub.challenge`.

### `POST /webhook/whatsapp`

Receives inbound WhatsApp events from Meta Cloud API:
1. Validates `X-Hub-Signature-256` HMAC-SHA256 signature if `Meta:AppSecret` is configured.
2. Returns `200 OK` immediately (satisfies Meta's <5s SLA).
3. Spawns an isolated DI scope (`IServiceScopeFactory`).
4. Sends read receipt (double blue ticks) via Meta Graph API.
5. Executes `ChatOrchestrator` to retrieve history, apply sliding context window, query LLM, and persist turns.
6. Sends reply text back to user via Meta Graph API (`https://graph.facebook.com/v19.0/{phone_number_id}/messages`).

### `GET /health`

```json
{ "status": "healthy", "timestamp": "...", "env": "Development" }
```

---

## WhatsApp Setup (Meta Cloud API)

1. Go to [developers.facebook.com](https://developers.facebook.com) → create an app → add **WhatsApp**.
2. Under **WhatsApp > API Setup**:
   - Note your **Phone Number ID** (`Meta:PhoneNumberId`).
   - Generate a temporary access token or configure a System User permanent token (`Meta:AccessToken`).
   - Under **App settings > Basic**, copy your **App Secret** (`Meta:AppSecret`) for webhook HMAC signature verification.
3. Start ngrok to expose your local API port:
   ```bash
   ngrok http 5000
   ```
4. In Meta App Dashboard → **WhatsApp > Configuration > Webhook**:
   - **Callback URL:** `https://<ngrok-id>.ngrok-free.app/webhook/whatsapp`
   - **Verify Token:** The string configured in `Meta:VerifyToken` (e.g. `HareKrishnHareRam`)
   - Click **Verify and Save**.
   - Subscribe to the **`messages`** webhook field.
5. Send a WhatsApp message to your test number. The backend will process your prompt through Groq and reply automatically.

---

## Running Tests

Run the test suite using `dotnet test`:

```bash
dotnet test
```

Or run the test project directly:

```bash
dotnet run --project ChatBackend.Tests
```

Test coverage includes **21 automated unit tests**:

| Test Suite | Focus Area |
|---|---|
| `ChatOrchestratorTests` | Lifecycle, persistence, sliding context window, multi-turn resumption, input validation, LLM error bubbling |
| `MetaWhatsAppHandlerTests` | Handshake verification, HMAC-SHA256 signature security, payload parsing, message processing |
| `GroqAdapterTests` | REST chat completions parsing, HTTP error mapping, SSE token streaming |
| `WhatsAppMessageWorkerTests` | Channel transfer, background queue draining, hosted service lifecycle |

---

## Interactive API Documentation (Scalar)

In `Development` mode, interactive API documentation is available at:

```
http://localhost:5000/scalar/v1
```

Scalar provides an interactive client, live request runner, theme switching, and auto-generated code snippets in C#, Python, JavaScript, and cURL.

---

## Environment Variables Reference

| Variable | Required | Description |
|---|---|---|
| `LLM__Groq__ApiKey` | ✅ | Groq Cloud API key (`gsk_...`) |
| `LLM__Groq__Model` | ❌ | Model name (e.g. `openai/gpt-oss-120b`, `openai/gpt-oss-20b`) |
| `LLM__Groq__BaseUrl` | ❌ | Default: `https://api.groq.com/openai/v1/` |
| `META__ACCESSTOKEN` | ✅ (WhatsApp) | Meta Graph API access token |
| `META__PHONENUMBERID` | ✅ (WhatsApp) | WhatsApp Business Phone Number ID |
| `META__VERIFYTOKEN` | ✅ (WhatsApp) | Secret token for webhook GET verification |
| `META__APPSECRET` | ❌ (Prod) | Meta App Secret for webhook HMAC-SHA256 signature verification |
| `CHAT__MAXHISTORYMESSAGES` | ❌ | Max context messages sliding window (Default: `20`) |
| `CONNECTIONSTRINGS__SQLITE` | ❌ | Default: `Data Source=chat.db` |
| `ASPNETCORE_ENVIRONMENT` | ❌ | `Development` / `Production` |

---

## Rate Limiting

The `/api/*` route group applies a **fixed-window** policy:
- **30 requests / minute** per client IP
- Queue limit of 5 requests (returns 429 when exceeded)
- Configured in `Program.cs` (`AddRateLimiter`)

---

## Tech Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10, C# 14 |
| API | ASP.NET Core Minimal APIs |
| Background Queues | System.Threading.Channels + BackgroundService |
| Time Abstraction | System.TimeProvider |
| ORM | EF Core 10 + SQLite |
| LLM Provider | Groq Cloud API |
| Messaging | Meta WhatsApp Cloud API (Graph API v19.0) |
| Resilience | Microsoft.Extensions.Http.Resilience (Polly 8) |
| Testing | xUnit v3 + Moq + FluentAssertions |
| API Reference UI | Scalar (Scalar.AspNetCore) + Microsoft.AspNetCore.OpenApi |

---

## License & Copyright

Copyright © 2026 Randhir Dixit. All rights reserved.

This repository and its source code are made available publicly for viewing, portfolio review, and evaluation purposes only. No permission is granted for commercial use, copying, modification, or redistribution without prior written permission. See [LICENSE](LICENSE) for details.

