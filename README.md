# Eagle Tunnel API

An ASP.NET Core service and Telegram bot that bridges **Tribute** (a Telegram-native payment provider) and a **3X-UI
Panel**. It receives signed Tribute webhook events and provisions VPN client accounts on the panel, keyed by each
customer's **Telegram ID** with **email-based** accounts.

## Overview

Eagle Tunnel API verifies incoming Tribute webhooks using HMAC SHA-256 and calls the Panel API to activate or renew user
subscriptions based on their Telegram ID. Users interact with the built-in Telegram bot to self-register, check their
subscription status, and retrieve their VPN link.

### Key Features

- **Webhook Validation:** Verifies Tribute signatures (`trbt-signature`) using HMAC SHA-256 with constant-time
  comparison.
- **Subscription Handling:** Processes `new_subscription`, `renewed_subscription`, and `cancelled_subscription` events;
  unknown events are logged for auditing.
- **Auto-Provisioning:** Automatically creates panel clients with the user's registered email when none exists;
  idempotent under concurrent webhooks.
- **Referral Program:** Email-based invite links (`t.me/<bot>?start=ref_<telegramId>`); referrers earn bonus days when a
  referee pays for the first time. See [Referral System](#referral-system).
- **Expiry Reminders:** Daily reminders (3 / 2 / 1 / 0 days out) for cancelled, email-migrated accounts approaching
  expiry; instant manual nudges via `/admin` → `📨 Send Reminder`.
- **Panel Integration:** Fetches, updates, enables/disables clients via the 3X-UI REST API.
- **Telegram Bot:** `/start` guides new users through email registration (with optional referrer), shows subscription
  status, `/help` points at support, `/referrer` lets unpaid users view or fix their referrer, `/admin` exposes a full
  account-management console for configured admins.
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

| Key                           | Env Variable                   | Description                                                                                  |
|-------------------------------|--------------------------------|----------------------------------------------------------------------------------------------|
| `Tribute:ApiKey`              | `Tribute__ApiKey`              | HMAC key for verifying `trbt-signature`.                                                     |
| `Panel:BaseUri`               | `Panel__BaseUri`               | The base URL of your 3X-UI Panel instance (e.g. `https://panel.example.com/admin`).          |
| `Panel:ApiKey`                | `Panel__ApiKey`                | API token for authenticating with the Panel.                                                 |
| `Telegram:BotToken`           | `Telegram__BotToken`           | Bot token from @BotFather.                                                                   |
| `Telegram:SupportUrl`         | `Telegram__SupportUrl`         | Support contact link shown by `/help`.                                                       |
| `Telegram:DefaultInboundIds`  | `Telegram__DefaultInboundIds`  | Comma-separated inbound ids new clients are attached to.                                     |
| `Telegram:AdminIds`           | `Telegram__AdminIds`           | Comma-separated Telegram ids allowed to use `/admin`.                                        |
| `Telegram:WebhookPath`        | `Telegram__WebhookPath`        | Path of the Telegram webhook endpoint (default `/webhooks/telegram`).                        |
| `Telegram:WebhookUrl`         | `Telegram__WebhookUrl`         | Public base URL used to register the webhook (**required in production**).                   |
| `Telegram:WebhookSecretToken` | `Telegram__WebhookSecretToken` | Secret token checked on every Telegram webhook request.                                      |
| `Telegram:BotUsername`        | `Telegram__BotUsername`        | Bot username without `@` (e.g. `EagleTunnelBot`), used to build referral links.              |
| `Telegram:ReferralBonusDays`  | `Telegram__ReferralBonusDays`  | Free days granted to a referrer on the referee's first payment (default `30`, `0` disables). |
| `Telegram:ReminderHourUtc`    | `Telegram__ReminderHourUtc`    | Hour of day in UTC (`0`-`23`) when expiry reminders are sent (default `9`).                  |

### Example `.env` file:

See [.env.example](.env.example) for the full list.

```dotenv
Tribute__ApiKey=your-tribute-secret
Panel__BaseUri=https://panel.example.com/admin
Panel__ApiKey=your-panel-api-token
Telegram__BotToken=123456:ABC-your-bot-token
Telegram__SupportUrl=https://t.me/support
Telegram__DefaultInboundIds=1,2
Telegram__BotUsername=YourBotName
```

## Project Structure

- **`EagleTunnelApi/`**: The main API service.
    - `Program.cs`: Composition root; webhook and bot endpoints.
    - `Configuration/`: Strongly-typed options with fail-fast validation on startup.
    - `PanelApi/`: 3X-UI panel REST client, models, and shared client defaults.
    - `Telegram/`: Bot update handlers, menus, sessions, provisioning, referrals, reminders, admin actions.
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

Handled events: `new_subscription`, `renewed_subscription`, `cancelled_subscription` (tags the client comment so expiry
reminders target cancelled users only). Any other event name is logged as unhandled and acknowledged so Tribute does not
retry it.

## 3X-UI Panel Integration Details

The service interacts with:

- `GET /admin/panel/api/clients/get/tgId/{telegramId}`: To fetch client details by Telegram ID.
- `POST /admin/panel/api/clients/update/{email}`: To update client expiry time, enable status, and inbounds.
- `POST /admin/panel/api/clients/add`: To create a client when no existing client is found for a Telegram ID, so the
  subscription is linked to an account rather than left orphaned.

All clients are created with the user's registered **email address**, a 300 GB quota, `xtls-rprx-vision` flow, device
limits (`limitHwid`: 2), monthly traffic reset on day 1, random credentials, and configured inbounds. Clients created
via `/start` are registered disabled until the first payment activates them. Legacy `tg{telegramId}` accounts (from
before email registration) are migrated to real emails on next `/start`. Auto-creation is idempotent: if a create
collides with a concurrently-created client, the service re-fetches by Telegram ID and updates the existing client
instead of failing. Referral attribution, bonus credit, cancellation flags, and reminder markers are stored in the
client's free-form `comment` field (see [Referral System](#referral-system)).

Panel responses that report `"success": false` (the panel answers HTTP 200 even on failure) are treated as errors so
failed panel operations surface as non-200 responses and trigger a retry rather than being silently acknowledged.

## Referral System

- **Registration:** `/start` asks new users for their email address, then for their referrer's email (skippable). Deep
  links (`t.me/<bot>?start=ref_<telegramId>`) pre-fill the referrer and show a Confirm / Edit / Skip screen. Mistyped
  referrers can be fixed with `/referrer` until the first payment.
- **Invite links:** Every user gets a personal link behind the `🎁 Invite Friends — Get 1 Month Free` menu button.
- **Bonus:** When a referred friend pays for the first time (`new_subscription`), the referrer gets
  `Telegram:ReferralBonusDays` (default `30`) added to their VPN expiry. The bonus is recorded as `Referral credit:` in
  the referrer's panel comment and re-applied on every renewal so rebills never absorb it. Referrers are advised to
  cancel their Tribute renewal to enjoy the free month; support provides a step-by-step cancellation video on request.
- **Eligibility:** Referrals only count for genuinely new subscribers. A referrer can only be attached to accounts that
  never had paid access, each referral pays out exactly once, and self-referrals are blocked.
- **Expiry reminders:** A daily background job (at `Telegram:ReminderHourUtc`) messages cancelled, email-migrated
  accounts 3, 2, 1, and 0 days before expiry with a resubscribe link; `/admin` → `📨 Send Reminder` fires the same
  reminder instantly for any linked account. `/admin` lookup shows referral, credit, and cancellation state per account.
- This flow is interim until a credits-based payment platform replaces Tribute; the referral ledger maps 1:1 onto future
  credit balances. Full detail lives in [REFERRAL_PLAN.md](REFERRAL_PLAN.md).

## Tests

Unit tests live in the `EagleTunnelApi.Tests` project (xUnit). They cover webhook signature verification, subscription
event handling (fetch / update / auto-create / idempotent fallback / error surfacing), referral bonus payout
(first-payment grant, idempotency, gift/self-referral blocks, pending referrals), cancellation tagging, expiry
reminders, subscription status derivation, and the Telegram bot flows including the registration wizard and admin
commands.

Run them with:

```bash
dotnet test
```

## License

- [Apache 2.0](LICENSE.md)
