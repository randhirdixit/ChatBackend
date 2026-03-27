# ChatBackend

A production-ready, provider-agnostic AI chat backend built with **.NET 8 / C# 12**.  
Switch between **Groq (Llama 3)** and **Azure OpenAI** via a single config value — no code changes.  
Conversations are persisted in **SQLite** via EF Core. **WhatsApp** messaging is handled through **Twilio**.

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
         │      ILLMClient             │  ← single interface
         ├─────────────────────────────┤
         │  GroqAdapter                │  ← llama3-8b-8192
         │  AzureOpenAIAdapter (stub)  │
         └────────┬────────────────────┘
                  │   LLMProviderFactory picks adapter from config
         ┌────────▼────────────────────┐
         │      Infrastructure         │
         │  EF Core + SQLite           │
         │  Polly (retry + timeout)    │
         │  Twilio SDK                 │
         └─────────────────────────────┘
```

### Project Layout

```
ChatBackend/
├── ChatBackend.sln
├── .env.sample                         ← copy → .env, fill secrets
│
├── ChatBackend.Core/                   ← Zero dependencies — pure domain
│   ├── Interfaces/
│   │   ├── ILLMClient.cs               ← Provider contract
│   │   └── IChatOrchestrator.cs
│   ├── DTOs/
│   │   ├── ChatRequest.cs
│   │   ├── ChatResponse.cs
│   │   ├── LLMMessage.cs
│   │   └── WhatsAppInboundMessage.cs
│   └── Models/
│       ├── User.cs
│       ├── Conversation.cs
│       └── Message.cs
│
├── ChatBackend.Infrastructure/         ← EF Core, adapters, Twilio
│   ├── Data/
│   │   ├── AppDbContext.cs
│   │   ├── DesignTimeDbContextFactory.cs
│   │   └── Migrations/
│   ├── LLM/
│   │   ├── GroqAdapter.cs              ← Primary adapter (Llama 3)
│   │   ├── AzureOpenAIAdapter.cs       ← Stub — ready to implement
│   │   ├── LLMProviderFactory.cs       ← Config-driven selection
│   │   └── GroqModels.cs               ← Wire format records
│   ├── Orchestrator/
│   │   └── ChatOrchestrator.cs
│   ├── Twilio/
│   │   └── TwilioWhatsAppHandler.cs
│   └── Extensions/
│       └── InfrastructureServiceExtensions.cs
│
├── ChatBackend.Api/                    ← Entry point
│   ├── Program.cs
│   ├── Endpoints/
│   │   ├── ChatEndpoints.cs
│   │   └── WhatsAppEndpoints.cs
│   ├── Middleware/
│   │   └── GlobalExceptionMiddleware.cs
│   ├── appsettings.json
│   └── appsettings.Development.json
│
└── ChatBackend.Tests/                  ← xUnit + Moq + FluentAssertions
    ├── LLMProviderFactoryTests.cs
    └── ChatOrchestratorTests.cs
```

---

## Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| .NET SDK | 8.0+ | https://dotnet.microsoft.com/download |
| dotnet-ef  | latest | `dotnet tool install -g dotnet-ef` |
| Groq API key | — | https://console.groq.com |
| Twilio account | — | https://console.twilio.com |
| ngrok (optional) | — | https://ngrok.com (for WhatsApp local testing) |

---

## Quick Start

### 1 — Clone & restore

```bash
git clone <your-repo-url>
cd ChatBackend
dotnet restore
```

### 2 — Set secrets

**Option A — .env file (recommended for local dev)**

```bash
cp .env.sample .env
# edit .env with your real keys
```

Then load it before running (Linux/macOS):

```bash
export $(grep -v '^#' .env | xargs)
```

Or on Windows PowerShell:

```powershell
Get-Content .env | Where-Object { $_ -notmatch '^#' -and $_ -ne '' } |
  ForEach-Object { $k,$v = $_ -split '=',2; [Environment]::SetEnvironmentVariable($k,$v) }
```

**Option B — dotnet user-secrets (cleanest for VS/Rider)**

```bash
cd ChatBackend.Api
dotnet user-secrets set "LLM:Groq:ApiKey"    "gsk_xxxx"
dotnet user-secrets set "Twilio:AccountSid"  "ACxxxx"
dotnet user-secrets set "Twilio:AuthToken"   "your_token"
```

### 3 — Apply database migration

```bash
cd ChatBackend.Api
dotnet ef database update \
  --project ../ChatBackend.Infrastructure \
  --startup-project .
```

> The app also auto-migrates on startup, so this step is optional for dev.

### 4 — Run

```bash
cd ChatBackend.Api
dotnet run
```

The API starts at **https://localhost:5001** (or http://localhost:5000).  
Swagger UI: **https://localhost:5001/swagger**

---

## API Reference

### `POST /api/chat`

Send a message and receive an AI response.

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
  "timestamp":        "2024-06-01T12:00:00Z"
}
```

**Continue the conversation** by passing `conversationId` from the previous response:

```json
{
  "userMessage":    "Can you give me a Python example?",
  "conversationId": "3fa85f64-..."
}
```

### `GET /api/conversations/{conversationId}`

Returns the full message history for a conversation.

### `GET /webhook/whatsapp`

Twilio reachability check — responds `200 OK`.

### `POST /webhook/whatsapp`

Receives inbound WhatsApp messages from Twilio (form-encoded).  
Validates Twilio signature, runs through `ChatOrchestrator`, sends reply via Twilio API.

### `GET /health`

```json
{ "status": "healthy", "timestamp": "...", "env": "Development" }
```

---

## WhatsApp Setup (Twilio)

1. Create a Twilio account → enable the **WhatsApp Sandbox** (or buy a number).
2. Start ngrok to expose your local server:
   ```bash
   ngrok http 5001
   ```
3. In the Twilio Console → **Messaging → Try it out → WhatsApp**, set:
   - **When a message comes in:** `https://<ngrok-id>.ngrok.io/webhook/whatsapp`
   - Method: **HTTP POST**
4. Send a WhatsApp message to your Twilio sandbox number.  
   The backend will reply via the Groq LLM.

> **Signature validation** is disabled in Development (`Twilio:SkipSignatureValidation: true`).  
> Set it to `false` in Production — the handler validates `X-Twilio-Signature` automatically.

---

## Switching LLM Providers

Change one environment variable — no code changes required:

```bash
# Use Groq (default)
export LLM__Provider=Groq

# Use Azure OpenAI
export LLM__Provider=AzureOpenAI
export LLM__AzureOpenAI__ApiKey=your_azure_key
export LLM__AzureOpenAI__Endpoint=https://your-resource.openai.azure.com/
export LLM__AzureOpenAI__DeploymentName=gpt-4o
```

> The `AzureOpenAIAdapter` is currently a stub. Implement `CompleteAsync` / `StreamAsync`  
> using the `Azure.AI.OpenAI` NuGet package or the raw REST API.

---

## Running Tests

```bash
dotnet test --logger "console;verbosity=detailed"
```

Test coverage includes:

| Test class | What it covers |
|---|---|
| `LLMProviderFactoryTests` | Groq / Azure / unknown / missing config selection |
| `ChatOrchestratorTests` | Happy path, DB persistence, multi-turn, anonymous user, LLM error, system prompt injection |

---

## Adding a New LLM Provider

1. Create `MyProvider/MyProviderAdapter.cs` implementing `ILLMClient`.
2. Register its `HttpClient` in `InfrastructureServiceExtensions.cs`.
3. Add `services.AddTransient<MyProviderAdapter>()`.
4. Add a case in `LLMProviderFactory.Create()`:
   ```csharp
   "myprovider" => _services.GetRequiredService<MyProviderAdapter>(),
   ```
5. Set `LLM__Provider=MyProvider` in config.

That's it — no changes anywhere else.

---

## EF Core Migrations (cheatsheet)

```bash
# Add a new migration (run from repo root)
dotnet ef migrations add <MigrationName> \
  --project ChatBackend.Infrastructure \
  --startup-project ChatBackend.Api

# Apply to database
dotnet ef database update \
  --project ChatBackend.Infrastructure \
  --startup-project ChatBackend.Api

# Rollback to previous migration
dotnet ef database update <PreviousMigrationName> \
  --project ChatBackend.Infrastructure \
  --startup-project ChatBackend.Api
```

---

## Rate Limiting

The `/api/*` route group applies a **fixed-window** policy:

- **30 requests / minute** per client IP
- Queue of 5 additional requests (then 429)
- Configurable in `Program.cs` → `AddRateLimiter`

---

## Security Checklist

- [x] API keys loaded from env vars — never hardcoded
- [x] Secrets never logged (logger uses structured placeholders, not string interpolation for sensitive fields)
- [x] Twilio signature validation (disable only in dev via `SkipSignatureValidation`)
- [x] Stack traces never sent to clients (`GlobalExceptionMiddleware`)
- [x] Rate limiting on all `/api/*` routes
- [ ] JWT / API-key authentication — add `app.UseAuthentication()` + `RequireAuthorization()`
- [ ] HTTPS enforcement — `UseHttpsRedirection()` already in pipeline; add cert in prod
- [ ] CORS — tighten `AllowAnyOrigin()` to specific origins in production

---

## Environment Variables Reference

| Variable | Required | Description |
|---|---|---|
| `LLM__Provider` | ✅ | `Groq` or `AzureOpenAI` |
| `LLM__Groq__ApiKey` | ✅ (Groq) | Groq Cloud API key |
| `LLM__Groq__Model` | ❌ | Default: `llama3-8b-8192` |
| `LLM__AzureOpenAI__ApiKey` | ✅ (Azure) | Azure OpenAI key |
| `LLM__AzureOpenAI__Endpoint` | ✅ (Azure) | Azure resource URL |
| `LLM__AzureOpenAI__DeploymentName` | ✅ (Azure) | Model deployment name |
| `TWILIO__ACCOUNTSID` | ✅ (WA) | Twilio Account SID |
| `TWILIO__AUTHTOKEN` | ✅ (WA) | Twilio Auth Token |
| `TWILIO__WHATSAPPNUMBER` | ✅ (WA) | `whatsapp:+1xxxxxxxxxx` |
| `CONNECTIONSTRINGS__SQLITE` | ❌ | Default: `Data Source=chat.db` |
| `ASPNETCORE_ENVIRONMENT` | ❌ | `Development` / `Production` |

---

## Tech Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 8, C# 12 |
| API | ASP.NET Core Minimal APIs |
| ORM | EF Core 8 + SQLite |
| LLM (primary) | Groq Cloud — Llama 3 8B |
| LLM (alternate) | Azure OpenAI (stub) |
| Messaging | Twilio SDK (WhatsApp) |
| Resilience | Microsoft.Extensions.Http.Resilience (Polly 8) |
| Testing | xUnit + Moq + FluentAssertions |
| Docs | Swagger / OpenAPI |
