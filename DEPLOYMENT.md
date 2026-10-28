# Deployment Guide

This is the operational companion to [FEATURES.md](FEATURES.md). It explains what must exist before each roadmap slice can
be deployed, which environment variables are active today, and which settings are still planned.

**Current code:** R0–R3b panel mirror/sync/admin scope — clean architecture skeleton, the panel gateway, the EF
Core/PostgreSQL foundation, panel mirror backfill and sync, conflict handling, and Telegram `Panel Management`.
Comment-state dual-write and the comment read switch remain pending.
**Last updated:** 2026-10-28

> **Planned settings are not read by the current code.** Keys marked *planned* below are proposals from
> [FEATURES.md](FEATURES.md). Do not set them expecting an effect until the matching slice is implemented and this guide
> is updated.

## 1. Current deployability (R0–R3b)

R3a adds the PostgreSQL persistence foundation: `AppDbContext`, the schema entities/configurations, the initial migration,
a startup migration runner, the Testcontainers harness, and minimal `IUnitOfWork`/`IAuditWriter` primitives. R3b adds
the panel mirror, first-run full scan, comment-state backfill, recurring cursor sync with full reconciliation, persisted
conflicts and Telegram alerts, effective default inbounds, the Telegram `Panel Management` menu, and best-effort mirror
updates after successful panel writes. R3b adds no HTTP feature endpoints, email sending, auth/credit use cases, or
comment-state read switch.

- New required setting: `ConnectionStrings__Postgres`.
- New optional settings: the `PanelSync:*` cadence, alerting, and comment-backfill settings in §4. All have defaults.
- New infrastructure: a PostgreSQL database with persistent storage and backups.
- Startup applies migrations under a PostgreSQL advisory lock. If the database is unreachable or a migration fails,
  startup fails.
- `GET /health` includes a PostgreSQL readiness check; `GET /alive` remains process liveness only.
- The schema includes auth, session, outbox, audit, panel-mirror, referral, trial, cancellation, reminder, and credit
  tables. The email outbox processor and web/auth use cases are not implemented.
- The panel sync worker runs immediately at startup and then every `PanelSync:IntervalMinutes` (default 10). The first
  run against an empty mirror is a full scan; later runs use the panel `updatedAt` cursor with a 5-minute overlap and
  fall back to a full scan if the panel rejects that ordering. A 30-minute PostgreSQL lease prevents overlapping runs.
- A full scan also runs every `PanelSync:FullReconcileHours` (default 24) and after a restart when no trusted full run
  is known. Full scans detect panel-side deletions and mark the mirror row deleted.
- Legacy comment backfill runs once per process after the first successful sync when
  `PanelSync:CommentBackfillOnStartup` is true (the default). A failed backfill is retried on the next sync cycle.
- Conflicts are persisted and never auto-merged. Open conflicts are notified to `Telegram__AdminIds` in batches, and
  `/admin` → `Panel Management` can resolve or dismiss them.
- The effective default inbound set is stored in Postgres and managed under `/admin` → `Panel Management`. The
  configured `Telegram__DefaultInboundIds` is only the first-run seed and database-outage fallback. Startup preflight
  logs a critical warning if the effective set references ids the panel no longer offers, but does not abort startup.
- Successful app-initiated panel writes are followed by a full panel fetch and mirror upsert. The panel write remains
  authoritative; a mirror failure is logged and swallowed, and the next sync reconciles it.
- Comment-state dual-write for new referral/trial/cancel/reminder/credit changes, the DB-backed comment read switch, and
  comment-write removal are not implemented. Existing comment behavior remains authoritative.
- Existing Telegram, Tribute, and 3X-UI panel behavior is unchanged on success paths.
- The panel adapter still returns `null` only for a definitive not-found response and throws for transport/server
  failures.
- `dotnet test` now starts a PostgreSQL Testcontainers container and requires a working Docker daemon.
- The Dockerfile copies the project files before restore, so the image builds correctly.

Recommended release position: deploy the image built from the current commit only after the checklist in §3 passes.

## 2. What is not in production yet

The current image still has no:

- comment-state dual-write for new referral/trial/cancel/reminder/credit changes, a DB-backed comment read switch, or
  comment-write removal (the legacy comments remain authoritative for app-owned state);
- email provider or outbox processor (the outbox table exists, but nothing claims or sends rows yet);
- web login endpoints or SPA;
- credits ledger behavior or payment flow (the ledger tables exist, but there are no reads or writes yet).

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

`dotnet test` starts a PostgreSQL Testcontainers container, so a working Docker daemon is required. GitHub-hosted
`ubuntu-latest` runners provide one.

Deploy the image with the production environment values from §4, then verify:

1. `GET /alive` returns success. This is process liveness only and does not check PostgreSQL.
2. `GET /health` returns success. This now includes PostgreSQL readiness.
3. Startup logs show `Applying database migrations.` followed by `Database migrations applied.`; a migration failure
   exits the process before it listens for traffic.
4. The bot answers `/start` and the main menu renders.
5. A Tribute test event reaches `/webhooks/tribute` and is accepted.
6. Startup logs show the Telegram webhook registered to the expected public URL.
7. Startup logs show the panel sync worker completing a sync (full on the first run), and `/admin` → `Panel Management`
   → `Sync Status` shows a last success with `cursor` or `full` mode.
8. If comment backfill is enabled, logs show `Panel comment backfill complete` after the first successful sync.
9. `/admin` → `Panel Management` → `Default Inbounds` lists panel inbounds and saves the expected set; the startup
   preflight logs that the effective ids are available.

Rollback before R3a is a normal image redeploy: there is no schema or persistent state added by those slices. After R3a,
the schema is persistent; follow §7 before rolling back across a migration.

## 4. Current configuration

All settings use ASP.NET Core configuration names. Environment variables replace `:` with `__`.

| Setting | Environment variable | Required | Purpose |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `ASPNETCORE_ENVIRONMENT` | Yes in production | Selects Production behavior: registers the Telegram webhook, disables local long polling, and enables production cookie behavior in R6. |
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` | Yes | PostgreSQL connection string. Startup applies migrations; the app fails to start if the setting is missing or the database is unreachable. |
| `Tribute:ApiKey` | `Tribute__ApiKey` | Yes | HMAC key for verifying `trbt-signature` on Tribute webhooks. |
| `Panel:BaseUri` | `Panel__BaseUri` | Yes | 3X-UI panel base URL, including the admin path (e.g. `https://panel.example.com/admin`). |
| `Panel:ApiKey` | `Panel__ApiKey` | Yes | Panel API token. |
| `Telegram:BotToken` | `Telegram__BotToken` | Yes | Bot token from @BotFather. |
| `Telegram:SupportUrl` | `Telegram__SupportUrl` | Yes | Support link shown to users. |
| `Telegram:TributeSubscriptionUrl` | `Telegram__TributeSubscriptionUrl` | Yes | Subscription/checkout link shown in menus and reminders. |
| `Telegram:DefaultInboundIds` | `Telegram__DefaultInboundIds` | Yes (seed/fallback) | Comma-separated inbound ids seeded into the effective default set on first use. The database-backed set is managed under `/admin` → `Panel Management`; every effective id must exist on the panel. |
| `Telegram:AdminIds` | `Telegram__AdminIds` | Recommended | Comma-separated Telegram ids allowed to use `/admin` and receive sync/conflict alerts. |
| `Telegram:WebhookPath` | `Telegram__WebhookPath` | Recommended | Webhook route. Set it explicitly; the code fallback is `/webhooks/telegram`, but `appsettings.json` ships an empty value. |
| `Telegram:WebhookUrl` | `Telegram__WebhookUrl` | Yes in production | Public base URL used to register the Telegram webhook. |
| `Telegram:WebhookSecretToken` | `Telegram__WebhookSecretToken` | Recommended | Secret verified on every Telegram webhook request. |
| `Telegram:BotUsername` | `Telegram__BotUsername` | Yes | Bare bot username (no `@`) used for referral links. |
| `Telegram:ReferralBonusDays` | `Telegram__ReferralBonusDays` | No | Referrer bonus days on first payment (default `30`, `0` disables). |
| `Telegram:ReminderHourUtc` | `Telegram__ReminderHourUtc` | No | UTC hour for expiry reminders (default `9`). |
| `Telegram:TrialDurationHours` | `Telegram__TrialDurationHours` | No | Trial hours for new accounts (default `3`, `0` disables). |
| `PanelSync:IntervalMinutes` | `PanelSync__IntervalMinutes` | No | Minutes between cursor sync runs (`1`–`1440`, default `10`). |
| `PanelSync:FullReconcileHours` | `PanelSync__FullReconcileHours` | No | Hours between full scans used to detect deletions (`1`–`720`, default `24`). |
| `PanelSync:FailureAlertThreshold` | `PanelSync__FailureAlertThreshold` | No | Consecutive sync failures before admins are alerted (`2`–`100`, default `3`). |
| `PanelSync:ConflictNotificationBatchSize` | `PanelSync__ConflictNotificationBatchSize` | No | Maximum open conflicts notified in one pass (`1`–`200`, default `50`). |
| `PanelSync:CommentBackfillOnStartup` | `PanelSync__CommentBackfillOnStartup` | No | Run the legacy comment backfill once per process after the first successful sync (default `true`). Disable after the comment read switch. |

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
  in-progress bot conversations. The `web_sessions` table is not used yet.
- `ConnectionStrings__Postgres` is required. The API applies migrations at startup; do not point it at a database you are
  not prepared to migrate.
- The panel sync worker starts with the app and immediately runs a sync. The first-ever run is a full scan; a restart
  without a trusted full run also starts with a full scan. A PostgreSQL lease prevents overlapping runs; a second host
  records a skipped run rather than applying the same scan.
- Mirror updates after successful panel writes are best-effort. The panel remains authoritative, so a mirror or database
  outage must not be treated as a failed panel operation; the next sync reconciles the mirror.
- Comment-state dual-write and the comment read switch are not implemented. Do not remove legacy comment writes.
- Use `/alive` for process liveness and `/health` for readiness. Only `/health` checks PostgreSQL.

## 5. Slice-by-slice deployment requirements

Each slice is independently testable, but not every slice is a zero-prerequisite deployment. Use this table before
promoting a build.

| Slice | Deployable without new infrastructure? | What to add / verify |
|---|---|---|
| **R0 skeleton** | Yes | None. Pure project wiring and architecture tests. |
| **R1 domain move** | Yes | None. Behavior-preserving. |
| **R2 panel gateway** | Yes | No new settings. Test panel reads/writes and the not-found vs unavailable distinction against staging or a real panel before production. |
| **R3a EF Core/PostgreSQL** ✅ | No | PostgreSQL instance, `ConnectionStrings__Postgres`, backups, migration rehearsal, Data Protection key-ring persistence before R3c. Starting the app applies migrations under an advisory lock and `/health` checks PostgreSQL. |
| **R3b panel mirror/sync** | No | Panel mirror/sync, conflicts, default inbounds, `Panel Management`, and post-write mirroring are implemented. Comment-state dual-write and the read switch remain pending. Add a working panel mirror, first-full-scan verification, `PanelSync__*` settings, admin alert recipients, and `Panel Management` review. |
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

### R3a — PostgreSQL foundation ✅

Implemented in code: the `AppDbContext` and entity configurations, the initial migration, the startup migration runner,
the Testcontainers/Respawn harness, `IUnitOfWork`, `IAuditWriter`, and the PostgreSQL readiness check. The initial
migration creates 16 domain tables plus `__EFMigrationsHistory`; there are still no HTTP feature endpoints.

Add/verify in the environment:

- a PostgreSQL database and backups (Coolify-managed or external);
- `ConnectionStrings__Postgres` (environment variable form of `ConnectionStrings:Postgres`);
- a migration rehearsal on a restored copy of production data;
- monitoring for startup/migration failures and `/health` readiness.

Migrations run at startup under a Postgres advisory lock. If a migration fails, startup fails; do not bypass it. Plan a
maintenance window for the first migration if the database is new to the deployment.

To rehearse the migration without starting the API, point the design-time factory at the target database and run:

```bash
ConnectionStrings__Postgres='Host=...;Database=...;Username=...;Password=...' \
  dotnet ef database update \
  --project EagleTunnelApi.Infrastructure \
  --startup-project EagleTunnelApi.Infrastructure
```

The design-time factory also falls back to a local PostgreSQL connection when `ConnectionStrings__Postgres` is not set;
only use that fallback for local development.

### R3b — panel directory, sync, conflicts, and Panel Management

Implemented in code:

- a first-run full scan that populates the panel mirror, followed by cursor sync every `PanelSync:IntervalMinutes`;
- a full reconciliation every `PanelSync:FullReconcileHours` and after a restart with no trusted full run, including
  deletion marking and orphan app-user reconciliation;
- a 30-minute PostgreSQL lease so concurrent sync triggers cannot apply the same run;
- persisted identity conflicts, Telegram conflict/failure alerts, and resolve/dismiss actions under `/admin`;
- legacy comment backfill once per process after the first successful sync;
- effective default inbounds stored in Postgres, seeded from `Telegram__DefaultInboundIds`, with startup preflight;
- best-effort mirror updates after successful app-initiated panel writes.

Add/verify in the environment:

- a completed first full scan and a healthy `/admin` → `Panel Management` → `Sync Status`;
- the `PanelSync__*` settings from §4, especially the interval and full-reconcile cadence;
- `Telegram__AdminIds` for conflict and repeated-failure alerts;
- the effective default-inbound set under `/admin` → `Panel Management` → `Default Inbounds`;
- an idempotency check on a second comment-backfill run;
- mirroring after a test panel email/comment update.

There is no separate staleness-threshold setting; freshness is derived from the last successful sync and cursor. A
future dashboard/manual-refresh slice will define the staleness threshold and refresh rate limit.

Not yet implemented: comment-state dual-write for new referral/trial/cancel/reminder/credit changes, the DB-backed
comment read switch, and comment-write removal. Do not stop comment writes until backfill, dual-write reconciliation,
and the read switch have all passed.

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

`ConnectionStrings__Postgres` is active from R3a and the `PanelSync__*` settings are active from R3b. The following
settings are documented for planning only and are **not read by the current application**:

- `Email__*` and `Auth__*` — R3c.
- staleness threshold and manual-refresh rate limits — R6; values not finalized.
- Data Protection key-ring path/store — R3c; hosting choice not finalized.
- payment-provider settings — R7; provider not selected.

Setting any of these before its slice has no effect.

## 7. Database rollout and rollback (R3a onward)

- Migrations are applied at startup on a dedicated connection under a PostgreSQL advisory lock. A failed migration logs
  `Database migration failed.` and aborts startup.
- Take a backup before the first production migration and test the restore.
- Multiple replicas are safe for the lock, but only one applies a given migration. The others wait, then observe the
  updated `__EFMigrationsHistory`.
- The API does not start listening until migrations succeed. `/alive` can only pass after that point; `/health` also
  requires PostgreSQL connectivity.
- Application rollback does not roll back the schema. Keep migrations forward-compatible for one release, or restore the
  database backup if a rollback crosses a breaking migration. Do not run old and new code against incompatible schemas.
- Integration tests create an isolated database per test and use Respawn to reset data between tests. Never point the
  test harness at a shared or production database.
- Do not use `EnsureCreated` in production.

## 8. Troubleshooting

| Symptom | Check |
|---|---|
| Container exits during startup | Required option validation: `Tribute__ApiKey`, `Panel__BaseUri`, `Panel__ApiKey`, `Telegram__BotToken`, `Telegram__SupportUrl`, `Telegram__TributeSubscriptionUrl`, `Telegram__DefaultInboundIds`, `Telegram__BotUsername`, and `Telegram__WebhookUrl` in Production. Also check `ConnectionStrings__Postgres`, database reachability, migration logs, and advisory-lock waits. |
| `/alive` fails | Process/container crash or port mismatch. The image listens on `8080`. |
| `/health` fails after R3a | PostgreSQL connectivity, migration state, or the connection string. `/alive` can still pass while `/health` fails. |
| Panel sync does not run | `PanelSync__IntervalMinutes`, panel credentials, `/admin` → `Panel Management` → `Sync Status`, and startup logs for a held/skipped lease or a full-scan error. |
| No conflict or failure alerts | `Telegram__AdminIds`, the bot's ability to message those ids, and `PanelSync__FailureAlertThreshold` (the failure alert fires when the count reaches the threshold). |
| New client creation fails panel validation | Compare the effective default inbounds under `/admin` → `Panel Management` with the startup preflight's available/missing id list. |
| Tests fail before running | Docker is not available to Testcontainers, the `postgres:17-alpine` image cannot be pulled, or the machine is out of disk/memory. |
| Bot does not receive updates | `Telegram__WebhookUrl` reachability, `Telegram__WebhookPath`, and `Telegram__WebhookSecretToken`; inspect the startup webhook log. |
| Tribute webhook returns 401 | `Tribute__ApiKey` does not match the provider's signing key. |
| Panel operations fail | `Panel__BaseUri` path, `Panel__ApiKey`, inbound ids, and panel availability. |
| Docker restore fails | Confirm the Dockerfile still copies every referenced `*.csproj` before `dotnet restore`. |
