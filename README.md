# Eagle Tunnel API

An ASP.NET Core service and Telegram bot that bridges **Tribute** (a Telegram-native payment provider) and a **3X-UI Panel**. It receives signed Tribute webhook events and provisions VPN client accounts on the panel, keyed by each customer's **Telegram ID**.

## Overview
Eagle Tunnel API verifies incoming Tribute webhooks using HMAC SHA-256 and calls the Panel API to activate or renew user subscriptions based on their Telegram ID. Users interact with the built-in Telegram bot to self-register, check their subscription status, and retrieve their VPN link.

### Key Features
- **Webhook Validation:** Verifies Tribute signatures (`trbt-signature`) using HMAC SHA-256 with constant-time comparison.
- **Subscription Handling:** Processes `new_subscription` and `renewed_subscription` events; unknown events are logged for auditing.
- **Auto-Provisioning:** Automatically creates panel clients with a deterministic username (`tg{telegramId}`) when none exists; idempotent under concurrent webhooks.
- **Panel Integration:** Fetches, updates, enables/disables clients via the 3X-UI REST API.
- **Telegram Bot:** `/start` auto-registers users and shows their status, `/help` points at support, `/admin` exposes a full account-management console for configured admins.
- **Modern .NET:** Built with .NET 10.0 and Aspire for local orchestration.

## Stack
- **Language:** C# 14.0
- **SDK:** .NET 10.0
- **Framework:** ASP.NET Core (Minimal APIs), .NET Aspire 13.4
- **Bot framework:** [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) 22.x
- **Containerization:** Docker

## Requirements
- .NET 10 SDK (for local development)
- Docker (for containerization/deployment)
- A Tribute API key (HMAC key for webhook signature verification)
- A 3X-UI Panel instance with API access (Base URL and API Token)
- A Telegram bot token from [@BotFather](https://t.me/BotFather)

## Setup & Run

### Local Development
Clone the repository and run the API project:
```bash
dotnet run --project EagleTunnelApi/EagleTunnelApi.csproj
```

Alternatively, you can run via the **Aspire AppHost** for full orchestration:
```bash
dotnet run --project EagleTunnelApi.AppHost/EagleTunnelApi.AppHost.csproj
```

In non-production environments the bot uses long polling, so no public URL is required locally.

### OpenAPI / Swagger
In Development mode, interactive documentation is available:
- **Swagger UI:** `http://localhost:<port>/swagger`
- **OpenAPI Spec:** `http://localhost:<port>/openapi/v1.json`

Health probes are exposed in all environments at `/health` (readiness) and `/alive` (liveness).

### Using Docker
```bash
docker build -t eagletunnelapi .
docker run --env-file .env -p 8080:8080 eagletunnelapi
```

## Scripts & CLI Commands
The following `dotnet` commands are commonly used:
- `dotnet build`: Compile the solution.
- `dotnet test`: Run the unit tests.
- `dotnet run`: Start the API or AppHost.
- `dotnet publish`: Package the application for deployment.

## Configuration (Environment Variables)
Configuration is handled via standard ASP.NET Core mechanisms (`appsettings.json`, Environment Variables).

| Key | Env Variable | Description |
|-----|--------------|-------------|
| `Tribute:ApiKey` | `Tribute__ApiKey` | HMAC key for verifying `trbt-signature`. |
| `Panel:BaseUri` | `Panel__BaseUri` | The base URL of your 3X-UI Panel instance (e.g. `https://panel.example.com/admin`). |
| `Panel:ApiKey` | `Panel__ApiKey` | API token for authenticating with the Panel. |
| `Telegram:BotToken` | `Telegram__BotToken` | Bot token from @BotFather. |
| `Telegram:SupportUrl` | `Telegram__SupportUrl` | Support contact link shown by `/help`. |
| `Telegram:DefaultInboundIds` | `Telegram__DefaultInboundIds` | Comma-separated inbound ids new clients are attached to. |
| `Telegram:AdminIds` | `Telegram__AdminIds` | Comma-separated Telegram ids allowed to use `/admin`. |
| `Telegram:WebhookPath` | `Telegram__WebhookPath` | Path of the Telegram webhook endpoint (default `/webhook/telegram`). |
| `Telegram:WebhookUrl` | `Telegram__WebhookUrl` | Public base URL used to register the webhook (**required in production**). |
| `Telegram:WebhookSecretToken` | `Telegram__WebhookSecretToken` | Secret token checked on every Telegram webhook request. |

### Example `.env` file:
See [.env.example](.env.example) for the full list.

```dotenv
Tribute__ApiKey=your-tribute-secret
Panel__BaseUri=https://panel.example.com/admin
Panel__ApiKey=your-panel-api-token
Telegram__BotToken=123456:ABC-your-bot-token
Telegram__SupportUrl=https://t.me/support
Telegram__DefaultInboundIds=1,2
```

## Project Structure
- **`EagleTunnelApi/`**: The main API service.
  - `Program.cs`: Composition root; webhook and bot endpoints.
  - `Configuration/`: Strongly-typed options with fail-fast validation on startup.
  - `PanelApi/`: 3X-UI panel REST client, models, and shared client defaults.
  - `Telegram/`: Bot update handlers, menus, sessions, provisioning, admin actions.
  - `Webhook/`: Tribute event models, HMAC signature verification, event handlers.
  - `Logging/`: Correlation-id middleware and outgoing-request logging.
- **`EagleTunnelApi.AppHost/`**: .NET Aspire orchestration project for managing dependencies and local environment.
- **`EagleTunnelApi.ServiceDefaults/`**: Shared configurations for observability, health checks, and service defaults.
- **`EagleTunnelApi.Tests/`**: xUnit unit tests for webhook verification, subscription handling, and the bot handlers.
- **`Dockerfile`**: Container definition for production deployment.

## Webhook Endpoint Details
`POST /webhooks/tribute`

**Headers:**
- `trbt-signature`: lower-case hex HMAC SHA-256 of the raw request body.

**Expected JSON Body:**
```json
{
  "name": "new_subscription",
  "created_at": "2026-01-28T10:15:00Z",
  "sent_at": "2026-01-28T10:15:00Z",
  "payload": { ... }
}
```

Handled events: `new_subscription`, `renewed_subscription`. Any other event name is logged as unhandled and acknowledged so Tribute does not retry it.

## 3X-UI Panel Integration Details
The service interacts with:
- `GET /admin/panel/api/clients/get/tgId/{telegramId}`: To fetch client details by Telegram ID.
- `POST /admin/panel/api/clients/update/{email}`: To update client expiry time, enable status, and inbounds.
- `POST /admin/panel/api/clients/add`: To create a client when no existing client is found for a Telegram ID, so the subscription is linked to an account rather than left orphaned.

All clients are created with a deterministic email (`tg{telegramId}`), a 300 GB quota, `xtls-rprx-vision` flow, device limits (`limitHwid`: 2), monthly traffic reset on day 1, random credentials, and configured inbounds. Clients created via `/start` are registered disabled until the first payment activates them. The same email pattern is used for both `/start` registration and subscription events, ensuring a single client per Telegram ID. Auto-creation is idempotent: if a create collides with a concurrently-created client (duplicate `tg{telegramId}` email), the service re-fetches by Telegram ID and updates the existing client instead of failing.

Panel responses that report `"success": false` (the panel answers HTTP 200 even on failure) are treated as errors so failed panel operations surface as non-200 responses and trigger a retry rather than being silently acknowledged.

## Tests
Unit tests live in the `EagleTunnelApi.Tests` project (xUnit). They cover webhook signature verification, subscription event handling (fetch / update / auto-create / idempotent fallback / error surfacing), subscription status derivation, and the Telegram bot flows including admin commands.

Run them with:
```bash
dotnet test
```

## License
- [Apache 2.0](LICENSE.md)
