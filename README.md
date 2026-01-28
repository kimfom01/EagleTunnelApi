# Eagle Tunnel API

Minimal ASP.NET API that receives Tribute webhook events and synchronizes
subscription status to Remnawave.

## What it does
- Verifies Tribute webhook signatures with HMAC SHA-256.
- Handles `new_subscription`, `cancelled_subscription`, and `renewed_subscription`.
- Calls Remnawave APIs to activate or cancel user access based on Telegram ID.

## Requirements
- .NET 10 SDK (for local development)
- Tribute webhook secret (HMAC key)
- Remnawave API base URL + API key

## Configuration
Configuration uses standard ASP.NET settings (env vars, `appsettings.json`, etc.).

Required settings:
- `Tribute:ApiKey` - webhook HMAC key used to verify `trbt-signature`.
- `Remnawave:BaseUri` - base URL for Remnawave, e.g. `https://remnawave.example/`.
- `Remnawave:ApiKey` - bearer token for Remnawave API requests.

Environment variable equivalents:
- `Tribute__ApiKey`
- `Remnawave__BaseUri`
- `Remnawave__ApiKey`

Example `.env` for Docker:
```dotenv
Tribute__ApiKey=your-tribute-secret
Remnawave__BaseUri=https://remnawave.example/
Remnawave__ApiKey=your-remnawave-api-key
```

## Running locally
```bash
dotnet run --project EagleTunnelApi/EagleTunnelApi.csproj
```

In Development, OpenAPI is available at:
- `GET /openapi/v1.json`
- `GET /swagger` (Swagger UI)

## Running with Docker
Build and run:
```bash
docker build -t eagletunnelapi .
docker run --env-file .env -p 8080:8080 eagletunnelapi
```

Or with Compose:
```bash
docker compose up --build
```

## Webhook endpoint
`POST /webhooks/tribute`

Headers:
- `trbt-signature` - lower-case hex HMAC SHA-256 of the raw request body.

Body:
- JSON event with fields `name`, `created_at`, `sent_at`, and `payload`.
- `name` must be one of:
  - `new_subscription`
  - `cancelled_subscription`
  - `renewed_subscription`

## Remnawave API calls
The service uses the following Remnawave routes:
- `GET /api/users/by-telegram-id/{telegramId}`
- `PATCH /api/users` with either:
  - Activate: `{ uuid, status: "ACTIVE", expireAt }`
  - Cancel: `{ uuid, expireAt }`
