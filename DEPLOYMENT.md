# Deployment Guide

This is the operational companion to [FEATURES.md](FEATURES.md). It explains what must exist before each roadmap slice can
be deployed, which environment variables are active today, and which settings are still planned.

**Current code:** R0–R2 — clean architecture skeleton, the pure Domain move, and the panel gateway. All are behavior-preserving.
**Last updated:** 2026-10-26

> **Planned settings are not read by the current code.** Keys marked *planned* below are proposals from
> [FEATURES.md](FEATURES.md). Do not set them expecting an effect until the matching slice is implemented and this guide
> is updated.

## 1. Current deployability (R0–R2)

R0–R2 do not change routes, configuration, database schema, or external dependencies. R0/R1 move pure logic into
`EagleTunnelApi.Domain`; R2 moves the panel adapter and wire DTOs into Infrastructure and exposes `IPanelGateway` to the
application.

- No new environment variables.
- No database or migrations.
- No new external service.
- No new container or volume.
- Existing Telegram, Tribute, and 3X-UI panel behavior is unchanged on success paths.
- The panel adapter now returns `null` only for a definitive not-found response and throws for transport/server failures.
- An unavailable panel now aborts an admin email change instead of assuming the new address is free.
- The Dockerfile copies the project files before restore, so the image builds correctly.

Recommended release position: deploy the image built from the current commit only after the checklist in §3 passes.

## 2. What is not in production yet

The current image still has no:

- PostgreSQL connection or schema;
- panel mirror/sync worker;
- email provider or outbox;
- web login endpoints or SPA;
- credits ledger.

Those arrive in later slices and bring the prerequisites listed in §5. Do not pre-create their infrastructure unless you
are preparing for that slice.

## 3. Release checklist

Run from the repository root:

```bash
dotnet restore
dotnet build EagleTunnelApi.slnx
dotnet test EagleTunnelApi.slnx
dotnet format EagleTunnelApi.slnx --verify-no-changes --no-restore
docker build -t eagletunnelapi:<commit-sha> .
```

Deploy the image with the production environment values from §4, then verify:

1. `GET /alive` returns success. This is process liveness only.
2. `GET /health` returns success. Database readiness will be added in R3a.
3. The bot answers `/start` and the main menu renders.
4. A Tribute test event reaches `/webhooks/tribute` and is accepted.
5. Startup logs show the Telegram webhook registered to the expected public URL.

Rollback before R3a is a normal image redeploy: there is no schema or persistent state added by these slices.

## 4. Current configuration

All settings use ASP.NET Core configuration names. Environment variables replace `:` with `__`.

| Setting | Environment variable | Required | Purpose |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `ASPNETCORE_ENVIRONMENT` | Yes in production | Selects Production behavior: registers the Telegram webhook, disables local long polling, and enables production cookie behavior in R6. |
| `Tribute:ApiKey` | `Tribute__ApiKey` | Yes | HMAC key for verifying `trbt-signature` on Tribute webhooks. |
| `Panel:BaseUri` | `Panel__BaseUri` | Yes | 3X-UI panel base URL, including the admin path (e.g. `https://panel.example.com/admin`). |
| `Panel:ApiKey` | `Panel__ApiKey` | Yes | Panel API token. |
| `Telegram:BotToken` | `Telegram__BotToken` | Yes | Bot token from @BotFather. |
| `Telegram:SupportUrl` | `Telegram__SupportUrl` | Yes | Support link shown to users. |
| `Telegram:TributeSubscriptionUrl` | `Telegram__TributeSubscriptionUrl` | Yes | Subscription/checkout link shown in menus and reminders. |
| `Telegram:DefaultInboundIds` | `Telegram__DefaultInboundIds` | Yes | Comma-separated inbound ids used for new clients. Every id must exist on the panel. |
| `Telegram:AdminIds` | `Telegram__AdminIds` | Recommended | Comma-separated Telegram ids allowed to use `/admin` and receive admin alerts. |
| `Telegram:WebhookPath` | `Telegram__WebhookPath` | Recommended | Webhook route. Set it explicitly; the code fallback is `/webhooks/telegram`, but `appsettings.json` ships an empty value. |
| `Telegram:WebhookUrl` | `Telegram__WebhookUrl` | Yes in production | Public base URL used to register the Telegram webhook. |
| `Telegram:WebhookSecretToken` | `Telegram__WebhookSecretToken` | Recommended | Secret verified on every Telegram webhook request. |
| `Telegram:BotUsername` | `Telegram__BotUsername` | Yes | Bare bot username (no `@`) used for referral links. |
| `Telegram:ReferralBonusDays` | `Telegram__ReferralBonusDays` | No | Referrer bonus days on first payment (default `30`, `0` disables). |
| `Telegram:ReminderHourUtc` | `Telegram__ReminderHourUtc` | No | UTC hour for expiry reminders (default `9`). |
| `Telegram:TrialDurationHours` | `Telegram__TrialDurationHours` | No | Trial hours for new accounts (default `3`, `0` disables). |

See [.env.example](.env.example) for a copyable file.

Startup uses fail-fast option validation. A missing required value stops the app with a validation error instead of
running in a half-configured state.

### Production notes

- Set `ASPNETCORE_ENVIRONMENT=Production`. Without it the app can run in a development-like mode: the Telegram webhook
  is not registered and the bot uses long polling instead.
- The container listens on port `8080`. Override `ASPNETCORE_URLS` only if the platform requires a different port.
- TLS is terminated by the proxy. Keep `UseHttpsRedirection` out of the HTTP pipeline; the container health check uses
  plain HTTP on port `8080`.
- The app registers the Telegram webhook at startup in Production. `Telegram__WebhookUrl` must be reachable from
  Telegram.
- Conversation/session state is in memory. Run a single instance unless that limitation is addressed; a restart drops
  in-progress bot conversations.
- Do not add a Postgres connection string yet. Nothing reads it before R3a.

## 5. Slice-by-slice deployment requirements

Each slice is independently testable, but not every slice is a zero-prerequisite deployment. Use this table before
promoting a build.

| Slice | Deployable without new infrastructure? | What to add / verify |
|---|---|---|
| **R0 skeleton** | Yes | None. Pure project wiring and architecture tests. |
| **R1 domain move** | Yes | None. Behavior-preserving. |
| **R2 panel gateway** | Yes | No new settings. Test panel reads/writes and the not-found vs unavailable distinction against staging or a real panel before production. |
| **R3a EF Core/PostgreSQL** | No | PostgreSQL instance, `ConnectionStrings__Postgres`, backups, migration rehearsal, Data Protection key-ring persistence before R3c. Starting the app applies migrations under an advisory lock. |
| **R3b panel mirror/sync** | No | A working panel mirror, initial backfill verification, sync interval/staleness settings, admin alert recipients, and `Panel Management` review. Comment dual-write begins here. |
| **R3c email outbox/auth use cases** | No | Email provider account, verified sending domain, SPF/DKIM/DMARC, email/auth secrets, persistent Data Protection key ring, outbox monitoring. No HTTP endpoints yet. |
| **R4 bot use-case extraction** | Yes | Existing bot tests; no new settings. Staging bot smoke test. |
| **R5 admin/reminder use-case extraction** | Yes | Existing admin/reminder tests; no new settings. Verify `/admin` flows and reminder timing in staging. |
| **R6 web auth + SPA** | Only with prerequisites | Everything from R3a–R3c, plus the Vite build output in `wwwroot`, cookie/HTTPS behavior at the proxy, and login/dashboard end-to-end tests without Telegram. |
| **R7 credits ledger** | No | Schema migrations, ledger reconciliation, payment-provider decisions, and support procedures. |

### R2 — panel gateway ✅

Implemented. No new environment variables. The risk is behavioral: panel DTOs now live behind `IPanelGateway` and
request/response mapping happens at the adapter boundary. Deploy only after staging exercises:

- client lookup by email and Telegram id;
- not-found vs panel-unavailable handling;
- client create/update;
- inbound option loading.

### R3a — PostgreSQL foundation

Add:

- a PostgreSQL database and backups (Coolify-managed or external);
- `ConnectionStrings__Postgres` (environment variable form of `ConnectionStrings:Postgres`);
- a migration rehearsal on a restored copy of production data;
- monitoring for migration failures and database readiness.

Migrations run at startup under a Postgres advisory lock. If a migration fails, startup fails; do not bypass it. Plan a
maintenance window for the first migration if the database is new to the deployment.

### R3b — panel directory and sync

Add/verify:

- a completed initial backfill with an idempotency check;
- the 10-minute sync worker and its lock/lease behavior;
- stale threshold and sync interval settings (names to be finalized in the implementation);
- admin conflict alerts and `/admin` → `Panel Management` access;
- the default-inbound setting, seeded from `Telegram__DefaultInboundIds`;
- comment dual-write and a reconciliation report before any read switch.

Do not stop comment writes until backfill, dual-write reconciliation, and the DB-backed read switch have all passed.

### R3c — email outbox and auth use cases

Planned settings from [FEATURES.md](FEATURES.md) §1.6. Names are provisional until implemented:

| Planned setting | Environment variable | Purpose |
|---|---|---|
| `Email:Provider` | `Email__Provider` | `resend` in production; `log` or Mailpit in development/tests. |
| `Email:ApiKey` | `Email__ApiKey` | Provider API key. |
| `Email:FromAddress` | `Email__FromAddress` | Verified sender address. |
| `Email:FromName` | `Email__FromName` | Sender display name. |
| `Email:ReplyTo` | `Email__ReplyTo` | Optional reply-to address. |
| `Email:BaseUrl` | `Email__BaseUrl` | Public web app base URL used in email links. |
| `Auth:CodePepper` | `Auth__CodePepper` | Server-side pepper for login-code hashes. |
| `Auth:CodeTtlMinutes` | `Auth__CodeTtlMinutes` | Login-code lifetime. |
| `Auth:CodeMaxAttempts` | `Auth__CodeMaxAttempts` | Failed verification limit. |
| `Auth:ResendCooldownSeconds` | `Auth__ResendCooldownSeconds` | Minimum delay between code sends. |

Also required at R3c:

- SPF, DKIM, and DMARC configured for the sending domain before production sends;
- a persistent Data Protection key ring (mounted volume or shared store) so pending outbox payloads survive restarts;
- outbox dead-letter/failure monitoring and an alert path.

R3c adds no public HTTP endpoints. Do not enable it in production until the panel mirror from R3b is healthy.

### R4/R5 — use-case extraction

No new infrastructure or settings. These are behavior-preserving extractions of bot/admin flows. Treat them as normal
application releases: run the existing test suite and smoke-test `/start`, registration, invite/claim, activation,
`/admin` grants, email changes, and reminders in staging.

### R6 — web auth and SPA

Add/verify:

- all R3a–R3c prerequisites;
- the Vite build copied into `EagleTunnelApi/wwwroot` and the Dockerfile's Node build stage;
- HTTPS at the proxy and production cookie settings (`HttpOnly`, `Secure`, `SameSite=Lax`, `__Host-` prefix);
- `Email:BaseUrl` pointing at the public web app;
- end-to-end login by email code, dashboard, refresh, logout, and email change without Telegram;
- rate limits for login and panel refresh;
- CSRF protection (`SameSite=Lax` plus `Origin`/`Sec-Fetch-Site` checks).

There is no CORS configuration because the SPA is same-origin.

### R7 — credits ledger

Add/verify:

- the ledger schema and migrations;
- reconciliation against panel state and legacy comment tags;
- payment-provider and refund/support procedures;
- credit balance/history views and audit reporting.

R7 is not a drop-in deployment and depends on product decisions still listed in [FEATURES.md](FEATURES.md).

## 6. Planned settings not active yet

The following settings are documented for planning only. They are **not read by the current application**:

- `ConnectionStrings__Postgres` — R3a.
- `Email__*` and `Auth__*` — R3c.
- sync interval, staleness threshold, and manual-refresh limits — R3b/R6; names not finalized.
- Data Protection key-ring path/store — R3c; hosting choice not finalized.
- payment-provider settings — R7; provider not selected.

Setting any of these before its slice has no effect.

## 7. Database rollout and rollback (R3a onward)

- Migrations are applied at startup under an advisory lock. A failed migration aborts startup.
- Take a backup before the first production migration and test the restore.
- Multiple replicas are safe for the lock, but only one applies a given migration.
- Application rollback does not roll back the schema. Keep migrations forward-compatible for one release, or restore the
  database backup if a rollback crosses a breaking migration.
- Do not use `EnsureCreated` in production.

## 8. Troubleshooting

| Symptom | Check |
|---|---|
| Container exits during startup | Required option validation: `Tribute__ApiKey`, `Panel__BaseUri`, `Panel__ApiKey`, `Telegram__BotToken`, `Telegram__SupportUrl`, `Telegram__TributeSubscriptionUrl`, `Telegram__DefaultInboundIds`, `Telegram__BotUsername`, and `Telegram__WebhookUrl` in Production. |
| `/alive` fails | Process/container crash or port mismatch. The image listens on `8080`. |
| `/health` fails after R3a | Database connectivity, migration failure, or connection string. |
| Bot does not receive updates | `Telegram__WebhookUrl` reachability, `Telegram__WebhookPath`, and `Telegram__WebhookSecretToken`; inspect the startup webhook log. |
| Tribute webhook returns 401 | `Tribute__ApiKey` does not match the provider's signing key. |
| Panel operations fail | `Panel__BaseUri` path, `Panel__ApiKey`, inbound ids, and panel availability. |
| Docker restore fails | Confirm the Dockerfile still copies every referenced `*.csproj` before `dotnet restore`. |
