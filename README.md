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
  referee pays for the first time. Friends can be pre-registered by email and claim their account on `/start`. See
  [Referral System](#referral-system).
- **Free Trial:** Newly registered accounts are enabled for `Telegram:TrialDurationHours` (default `3`) so users can
  connect immediately; first payment ends the trial and preserves any referral attribution.
- **Expiry Reminders:** Daily reminders (3 / 2 / 1 / 0 days out) for cancelled, email-migrated accounts approaching
  expiry; instant manual nudges via `/admin` → `📨 Send Reminder`.
- **Panel Integration:** Fetches, updates, enables/disables clients via the 3X-UI REST API.
- **Panel Mirror & Sync:** Mirrors panel clients in PostgreSQL, syncs changed rows every 10 minutes by default,
  periodically full-scans for deletions, and tracks identity conflicts without auto-merging them. Successful app
  panel writes are mirrored best-effort; the panel remains authoritative.
- **Panel Management:** `/admin` → `🖥 Panel Management` shows sync status, open conflicts with resolve/dismiss actions,
  and the effective default inbounds used for new clients.
- **Telegram Bot:** `/start` guides new users through email registration (with optional referrer), shows subscription
  status, `/help` points at support, `/referrer` lets unpaid users view or fix their referrer, `/admin` exposes a full
  account-management console (lookup, grant, ban/unban, device limit, traffic reset, link, register, change email,
  nudge) with partial-email search and pick lists, for configured admins.
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

### Deployment

See [DEPLOYMENT.md](DEPLOYMENT.md) for the release checklist, current environment variables, and the infrastructure and
settings required by each roadmap slice. R0 and R1 are behavior-preserving; R3b adds PostgreSQL, the panel mirror/sync,
and `Panel Management`; later slices add email and SPA prerequisites as documented there.

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
| `Telegram:TributeSubscriptionUrl` | `Telegram__TributeSubscriptionUrl` | Subscription/checkout link shown in menus and reminders.                                |
| `Telegram:DefaultInboundIds`  | `Telegram__DefaultInboundIds`  | Comma-separated panel inbound ids used as the first-run seed and database-outage fallback. The effective set is stored in Postgres and managed under `Panel Management`; the panel rejects the whole add call if any effective id no longer exists. |
| `Telegram:AdminIds`           | `Telegram__AdminIds`           | Comma-separated Telegram ids allowed to use `/admin` and receive panel sync/conflict alerts. |
| `Telegram:WebhookPath`        | `Telegram__WebhookPath`        | Path of the Telegram webhook endpoint. Set explicitly; the code fallback is `/webhooks/telegram`, but `appsettings.json` ships an empty value. |
| `Telegram:WebhookUrl`         | `Telegram__WebhookUrl`         | Public base URL used to register the webhook (**required in production**).                   |
| `Telegram:WebhookSecretToken` | `Telegram__WebhookSecretToken` | Secret token checked on every Telegram webhook request.                                      |
| `Telegram:BotUsername`        | `Telegram__BotUsername`        | Bot username without `@` (e.g. `EagleTunnelBot`), used to build referral links.              |
| `Telegram:ReferralBonusDays`  | `Telegram__ReferralBonusDays`  | Free days granted to a referrer on the referee's first payment (default `30`, `0` disables). |
| `Telegram:ReminderHourUtc`    | `Telegram__ReminderHourUtc`    | Hour of day in UTC (`0`-`23`) when expiry reminders are sent (default `9`).                  |
| `Telegram:TrialDurationHours` | `Telegram__TrialDurationHours` | Free trial hours for newly registered accounts (default `3`, `0` disables).                  |
| `PanelSync:IntervalMinutes`   | `PanelSync__IntervalMinutes`   | Minutes between cursor sync runs (`1`–`1440`, default `10`).                                  |
| `PanelSync:FullReconcileHours` | `PanelSync__FullReconcileHours` | Hours between full scans for deletions (`1`–`720`, default `24`).                            |
| `PanelSync:FailureAlertThreshold` | `PanelSync__FailureAlertThreshold` | Consecutive sync failures before admin alerts (`2`–`100`, default `3`).                  |
| `PanelSync:ConflictNotificationBatchSize` | `PanelSync__ConflictNotificationBatchSize` | Maximum open conflicts notified per pass (`1`–`200`, default `50`).               |
| `PanelSync:CommentBackfillOnStartup` | `PanelSync__CommentBackfillOnStartup` | Run legacy comment backfill once after the first successful sync (default `true`).   |

### Example `.env` file:

See [.env.example](.env.example) for the full list.

```dotenv
Tribute__ApiKey=your-tribute-secret
Panel__BaseUri=https://panel.example.com/admin
Panel__ApiKey=your-panel-api-token
Telegram__BotToken=123456:ABC-your-bot-token
Telegram__SupportUrl=https://t.me/support
Telegram__TributeSubscriptionUrl=https://tribute.example.com/your-subscription
Telegram__DefaultInboundIds=1,2
Telegram__BotUsername=YourBotName
```

## Project Structure

- **`EagleTunnelApi/`**: The main API service and composition root.
    - `Program.cs`: Composition root; webhook and bot endpoints.
    - `Configuration/`: Strongly-typed options with fail-fast validation on startup.
    - `PanelApi/`: 3X-UI panel REST client, models, and shared client defaults.
    - `Telegram/`: Bot update handlers, menus, sessions, provisioning, referrals, reminders, admin actions.
    - `Webhook/`: Tribute event models, HMAC signature verification, event handlers.
    - `Logging/`: Correlation-id middleware and outgoing-request logging.
- **`EagleTunnelApi.Domain/`**: Pure business rules: subscription status/formatter, referral/trial/credit tag policy, and
  email normalization. No project references.
- **`EagleTunnelApi.Application/`**: Use cases and ports. References Domain only.
- **`EagleTunnelApi.Infrastructure/`**: Adapters for the panel, email, and persistence. References Application and Domain.
  The Telegram adapter stays in the API project for now.
- **`EagleTunnelApi.AppHost/`**: .NET Aspire orchestration project for managing dependencies and local environment.
- **`EagleTunnelApi.ServiceDefaults/`**: Shared configurations for observability, health checks, and service defaults.
- **`EagleTunnelApi.Tests/`**: xUnit unit tests plus NetArchTest project-boundary tests.
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

> **Panel upgrades:** `Telegram:DefaultInboundIds` must contain ids that still exist on the panel. Recent 3X-UI
> versions reject the entire `clients/add` call if any requested inbound id is missing. The service checks this at
> startup and logs a critical message listing the missing and available ids; `/admin` registration also shows the
> panel's error when a create fails.

Panel responses that report `"success": false` (the panel answers HTTP 200 even on failure) are treated as errors so
failed panel operations surface as non-200 responses and trigger a retry rather than being silently acknowledged.

## Referral System

- **Registration:** `/start` asks new users for their email address, then for their referrer's email (skippable
  for organic signups). Deep links (`t.me/<bot>?start=ref_<telegramId>`) pre-fill the referrer and show a Confirm /
  Edit screen — attribution from a link is required and can't be skipped. Mistyped referrers can be fixed with
  `/referrer` until the first payment.
- **Invite links:** Every user gets a personal link behind the `🎁 Invite Friends — Get 1 Month Free` menu button.
  Legacy `tg{id}` accounts are asked for a one-time login-email update when they open that screen.
- **Pre-registration:** Users can register a friend's email from the invite screen, and admins via `/admin` →
  `➕ Register Account`. The friend's free trial starts **immediately** so they get VPN access before ever touching
  Telegram (offline-friend flow: register → share link/QR → friend connects → friend later starts the bot, enters the
  email to link Telegram, and pays). The inviter instantly receives the friend's VPN link (tap-to-copy) plus a
  scannable **QR code** (`🔳 Friend QR Code` re-sends it). If the trial already expired before the friend claims, the
  claim restarts a fresh trial.
- **QR codes:** Active users get `🔳 Show QR Code` on the main menu for their own subscription link; every friend/admin
  invite shares its link as a QR photo for in-person scanning when a link can't be forwarded.
- **Free trial:** New (never-provisioned) accounts are enabled for `Telegram:TrialDurationHours` (default `3`) so users
  can connect immediately; the first payment ends the trial (tag cleared) without touching referral attribution.
- **Bonus:** When a referred friend pays for the first time, the referrer gets
  `Telegram:ReferralBonusDays` (default `30`) added to their VPN expiry. The payout fires on the first paid
  activation however it happens — Tribute `new_subscription` webhook, manual `/admin` grant (e.g. direct payment
  to support), or the settlement sweep for extensions made directly in the 3X-UI dashboard. Direct/dashboard
  activations that still carry the trial tag are detected as paid from the expiry date. `/admin` →
  `🛠 Settle Referrals` runs the sweep on demand (manual grants report the payout in the confirmation). The bonus is
  recorded as `Referral credit:` in the referrer's panel comment and re-applied on every renewal so rebills never
  absorb it. Once the credited days have been used up (the account lapsed), the tag is cleared rather than re-added,
  so a later resubscribe never pays the same bonus twice. Referrers are advised to cancel their Tribute renewal to
  enjoy the free month; support provides a step-by-step cancellation video on request.
- **Eligibility:** Referrals only count for genuinely new subscribers. A referrer can only be attached to accounts that
  never had paid access, each referral pays out exactly once, and self-referrals are blocked.
- **Email lockdown:** Once an account is active, its login email can only be changed by support/admins (`/admin` →
  `✏️ Change Email`). Provisioned legacy `tg{id}` accounts keep their login until support migrates them.
- **Admin search:** Every admin action that takes an email accepts partial matches and offers a pick list when several
  accounts match, so the exact address, case and all, is never required.
- **Expiry reminders:** A daily background job (at `Telegram:ReminderHourUtc`) messages cancelled, email-migrated
  accounts 3, 2, 1, and 0 days before expiry with a resubscribe link; `/admin` → `📨 Send Reminder` fires the same
  reminder instantly for any linked account, and manual grants clear the cancelled/reminder markers just like a
  Tribute renewal. `/admin` lookup shows referral, credit, and cancellation state per account.
- This flow is interim until a credits-based payment platform replaces Tribute; the referral ledger maps 1:1 onto future
  credit balances. See [FEATURES.md](FEATURES.md) for the credits design and other unimplemented work.

## Roadmap

Unimplemented work is tracked in [FEATURES.md](FEATURES.md):

- Clean architecture refactor (Domain/Application/Infrastructure/Api) so new features land behind explicit boundaries.
  R0–R2 and the R3a persistence foundation are implemented; R3b adds the panel mirror/sync/conflicts and
  `Panel Management`; R3c and later slices remain.
- Email-first Vite + React web app with PostgreSQL-backed auth/identity/audit; no Telegram required to log in.
- Comment-state dual-write for new referral/trial/cancel/reminder/credit changes, then the DB-backed read switch and
  comment-write removal.
- Credits ledger and eventual Tribute replacement.
- Pending manual verification and operational checks.

Implemented behavior stays in this README and the test suite; `FEATURES.md` is the single backlog for what is not built
yet.

## Tests

Unit tests live in the `EagleTunnelApi.Tests` project (xUnit). They cover webhook signature verification, subscription
event handling (fetch / update / auto-create / idempotent fallback / error surfacing), referral bonus payout
(first-payment grant, manual-grant grant, settlement sweep, on-demand settlement, trial-aware paid detection,
idempotency, no-chaining, gift/self-referral blocks,
pending referrals, trial referees, trial-tag stripping),
trial registration, friend pre-registration and claim, admin register/change-email, partial-email pick lists, nudge flow,
cancellation tagging, expiry reminders, subscription status derivation, panel mirror backfill/sync/conflicts, and the
Telegram bot flows including the registration wizard, admin commands, and `Panel Management`.

Run them with:

```bash
dotnet test
```

## License

- [Apache 2.0](LICENSE.md)
