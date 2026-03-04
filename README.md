# Eagle Tunnel API

A minimal ASP.NET Core service that receives Tribute webhook events and synchronizes subscription status with the Remnawave API.

## Overview
Eagle Tunnel API acts as a bridge between Tribute (payment/subscription provider) and Remnawave. It validates incoming webhooks using HMAC SHA-256 and calls Remnawave to activate or renew user subscriptions based on their Telegram ID.

### Key Features
- **Webhook Validation:** Verifies Tribute signatures (`trbt-signature`) using HMAC SHA-256.
- **Subscription Handling:** Automatically processes `new_subscription` and `renewed_subscription` events.
- **Remnawave Integration:** Updates user status (active/expireAt) via Remnawave's patch APIs.
- **Modern .NET:** Built with .NET 10.0 and Aspire for local orchestration.

## Stack
- **Language:** C# 14.0
- **SDK:** .NET 10.0
- **Framework:** ASP.NET Core (Minimal APIs), .NET Aspire 13.0
- **Package Manager:** NuGet (via `dotnet` CLI)
- **Containerization:** Docker / Docker Compose

## Requirements
- .NET 10 SDK (for local development)
- Docker (for containerization/deployment)
- A Tribute webhook secret (HMAC key)
- A Remnawave instance with API access (Base URL and API Key)

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
| `Remnawave:BaseUri` | `Remnawave__BaseUri` | The base URL of your Remnawave instance. |
| `Remnawave:ApiKey` | `Remnawave__ApiKey` | Bearer token for authenticating with Remnawave. |

### Example `.env` file:
```dotenv
Tribute__ApiKey=your-tribute-secret
Remnawave__BaseUri=https://remnawave.example.com/
Remnawave__ApiKey=your-remnawave-bearer-token
```

## Project Structure
- **`EagleTunnelApi/`**: The main API service.
  - `Webhook/Handlers/`: Business logic for processing different event types.
  - `Webhook/Security/`: Signature verification and security logic.
  - `Webhook/Events/`: Webhook event payload models (e.g., `NewSubscription`).
  - `Webhook/Models/`: Models for interacting with the Remnawave API.
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
  "payload": { ... }
}
```

## Remnawave Integration Details
The service interacts with:
- `GET /api/users/by-telegram-id/{telegramId}`: To fetch user UUID.
- `PATCH /api/users`: To activate (`ACTIVE`) or update `expireAt` for a user.

## Tests
- **TODO:** Implement unit tests for webhook signature verification and handler logic.

## License
- [Apache 2.0](LICENSE.md)
