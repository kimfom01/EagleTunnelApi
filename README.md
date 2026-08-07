# Eagle Tunnel API

A minimal ASP.NET Core service that receives Tribute webhook events and synchronizes subscription status with a 3X-UI Panel API.

## Overview
Eagle Tunnel API acts as a bridge between Tribute (payment/subscription provider) and a 3X-UI Panel. It validates incoming webhooks using HMAC SHA-256 and calls the Panel API to activate or renew user subscriptions based on their Telegram ID.

### Key Features
- **Webhook Validation:** Verifies Tribute signatures (`trbt-signature`) using HMAC SHA-256.
- **Subscription Handling:** Automatically processes `new_subscription` and `renewed_subscription` events.
- **Panel Integration:** Updates client expiry via the Panel's REST API.
- **Modern .NET:** Built with .NET 10.0 and Aspire for local orchestration.

## Stack
- **Language:** C# 14.0
- **SDK:** .NET 10.0
- **Framework:** ASP.NET Core (Minimal APIs), .NET Aspire 13.4
- **Package Manager:** NuGet (via `dotnet` CLI)
- **Containerization:** Docker / Docker Compose

## Requirements
- .NET 10 SDK (for local development)
- Docker (for containerization/deployment)
- A Tribute webhook secret (HMAC key)
- A 3X-UI Panel instance with API access (Base URL and API Token)

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

### OpenAPI / Swagger
In Development mode, interactive documentation is available:
- **Swagger UI:** `http://localhost:<port>/swagger`
- **OpenAPI Spec:** `http://localhost:<port>/openapi/v1.json`

### Using Docker
#### Build & Run manually:
```bash
docker build -t eagletunnelapi .
docker run --env-file .env -p 8080:8080 eagletunnelapi
```

#### Using Docker Compose:
```bash
docker compose up --build
```

## Scripts & CLI Commands
The following `dotnet` commands are commonly used:
- `dotnet build`: Compile the solution.
- `dotnet run`: Start the API or AppHost.
- `dotnet publish`: Package the application for deployment.
- `dotnet restore`: Restore NuGet packages.

## Configuration (Environment Variables)
Configuration is handled via standard ASP.NET Core mechanisms (`appsettings.json`, Environment Variables).

| Key | Env Variable | Description |
|-----|--------------|-------------|
| `Tribute:ApiKey` | `Tribute__ApiKey` | HMAC key for verifying `trbt-signature`. |
| `Panel:BaseUri` | `Panel__BaseUri` | The base URL of your 3X-UI Panel instance (e.g. `https://panel.example.com/admin`). |
| `Panel:ApiKey` | `Panel__ApiKey` | API token for authenticating with the Panel. |

### Example `.env` file:
```dotenv
Tribute__ApiKey=your-tribute-secret
Panel__BaseUri=https://panel.example.com/admin
Panel__ApiKey=your-panel-api-token
```

## Project Structure
- **`EagleTunnelApi/`**: The main API service.
  - `Webhook/Handlers/`: Business logic for processing different event types.
  - `Webhook/Security/`: Signature verification and security logic.
  - `Webhook/Events/`: Webhook event payload models (e.g., `NewSubscription`).
  - `Webhook/Models/`: Models for interacting with the 3X-UI Panel API.
- **`EagleTunnelApi.AppHost/`**: .NET Aspire orchestration project for managing dependencies and local environment.
- **`EagleTunnelApi.ServiceDefaults/`**: Shared configurations for observability, health checks, and service defaults.
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

## 3X-UI Panel Integration Details
The service interacts with:
- `GET /admin/panel/api/clients/get/tgId/{telegramId}`: To fetch client details by Telegram ID.
- `POST /admin/panel/api/clients/update/{email}`: To update client expiry time and enable status.
- `POST /admin/panel/api/clients/add`: To create a client when no existing client is found for a Telegram ID, so the subscription is linked to an account rather than left orphaned.
- `GET /admin/panel/api/inbounds/list`: To gather all inbound IDs when creating a client (new clients are attached to every inbound).

When a `new_subscription` or `renewed_subscription` event references a Telegram ID with no matching panel client, the service auto-creates one with a deterministic username (`tg{telegramId}`), the subscription's expiry time, and the Telegram ID attached so future renewals resolve correctly.

Panel responses that report `"success": false` (the panel answers HTTP 200 even on failure) are treated as errors so failed panel operations surface as non-200 responses and trigger a retry rather than being silently acknowledged. Auto-creation is idempotent: if a create collides with a concurrently-created client (duplicate `tg{telegramId}` email), the service re-fetches by Telegram ID and updates the existing client instead of failing.

## Tests
- **TODO:** Implement unit tests for webhook signature verification and handler logic.

## License
- [Apache 2.0](LICENSE.md)
