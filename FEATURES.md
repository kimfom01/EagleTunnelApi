# Features & Roadmap

Single source of truth for work that is **not implemented yet**. Implemented behavior is documented in
[README.md](README.md) and verified by the test suite. Historical build plans (`REFERRAL_PLAN.md` and
`TESTING_PLAN.md`) were consolidated into this document and removed to avoid duplication; git history still contains
them if the old detail is ever needed.

> **Status:** planning document. R0–R3a are implemented; later slices remain planned unless explicitly marked otherwise.
>
> **Deployment:** per-slice infrastructure, environment variables, and release checks live in
> [DEPLOYMENT.md](DEPLOYMENT.md).
>
> **Last updated:** 2026-10-26

## Status legend

| Status | Meaning |
|---|---|
| **Design only** | Decisions and schema sketched; no code. |
| **Planned** | Agreed direction; implementation can be broken into tasks. |
| **Needs decision** | Blocked on a product, security, or provider choice. |
| **Deferred** | Deliberately out of scope until a later phase. |
| **Operational** | A runbook/process item, not application code. |

## Suggested order

The database and panel directory are the foundation for everything else. Build them before the web surface so login,
dashboard, admin tooling, and the bot all read the same state.

1. Clean architecture skeleton and panel gateway (Section 2, R0–R2).
2. EF Core/PostgreSQL foundation, schema, migrations, and Testcontainers harness (R3a).
3. Comment backfill + panel directory, 10-minute sync, conflict alerts, and the admin sync/default-inbound menu
   (R3b, Section 4).
4. Email outbox + email-code auth/identity foundation (R3c, Section 1).
5. Web auth endpoints + DB-backed dashboard + manual refresh (R6).
6. Continue extracting bot/admin use cases (R4–R5) in parallel from R2 onward.
7. Credits ledger (Section 3, R7), then web payments/Tribute replacement.

Steps 3 and 4 can overlap. Step 6 is deliberately continuous, not a big-bang rewrite.

---

## 1. Email-first web app

**Status:** Design only for endpoints and auth use cases. The R3a auth/session/login-code schema is implemented.

### 1.1 Goal

Reduce and eventually remove dependence on Telegram. A user who cannot access Telegram must still be able to:

- log in with their email;
- view subscription status, expiry, traffic, and devices;
- copy their VPN subscription link and show its QR code;
- see referral/credit information;
- get support when their email is wrong or their account needs help.

The web app is **email-first**: it must not require Telegram for login. It should also work for pre-registered
friend accounts that have never linked Telegram (`TgId = 0`).

### 1.2 Locked decisions

- **Frontend:** Vite + React + TypeScript SPA, built into `EagleTunnelApi/wwwroot`, served by the ASP.NET Core app.
- **Auth:** email confirmation code (not a Telegram login link, not a password). Login and email changes require a code.
  Telegram registration does **not** block on email verification (accepted risk; see §1.7).
- **Database:** PostgreSQL. Postgres owns web identity, auth, sessions, audit, referrals, credits, trial/cancellation
  state, reminders, and app settings. The 3X-UI panel remains authoritative for enforced subscription fields
  (`enable`, expiry, traffic quota, device limits, inbounds, `subId`).
- **Panel directory:** Postgres keeps a local mirror of panel clients and inbounds, reconciled every 10 minutes and on
  demand. Dashboard reads use Postgres, not a live panel call.
- **Email provider:** Resend, behind a provider adapter and a transactional outbox.
- **Migrations:** EF Core migrations run at API startup under a Postgres advisory lock.
- **Credits:** design the ledger now; do not implement it in the first slice.
- **Admin v1:** Telegram admin gets only default-inbound configuration plus sync status/conflicts. Full panel client
  management stays out of v1.
- **No web admin console:** admin actions stay in Telegram for now.

### 1.3 Who can log in

- Any panel client whose email matches the submitted address, including:
  - normal accounts linked to a Telegram ID;
  - unclaimed friend/admin-registered accounts with `TgId = 0`;
  - disabled, expired, or limited accounts (they still need to see status and renew).
- A panel client must exist. The web app is **not** a self-signup product in v1.
- The login request reads the Postgres `panel_clients` mirror. If the row is not found and the mirror is fresh, return the
  same `202 Accepted` response as a successful request but send no email. This avoids account enumeration.
- If the mirror has never completed a sync, or the requested row is stale, do a scoped panel lookup for that email before
  deciding. A definitive not-found returns `202`; a transport/server failure returns `503 Service Unavailable`.
- If the panel is unreachable but a fresh mirror row exists, login still works. This is the main reason for the mirror.

### 1.4 Identity model

There are two related records:

- `panel_clients` — the Postgres mirror of a 3X-UI client. It is the local read model for subscription/panel state.
- `app_users` — the web/Telegram identity. It links to a `panel_clients` row and owns login, sessions, and app state.

Proposed `app_users`:

| Column | Notes |
|---|---|
| `id` | UUID primary key. |
| `email_normalized` | Lowercase/trimmed email, unique, not null. |
| `telegram_id` | Nullable, unique when present. Null for `TgId = 0` accounts. |
| `panel_client_id` | Bigint, unique, not null; references `panel_clients.panel_client_id`. |
| `email_verified_at` | Nullable. Set when an email code is successfully verified. |
| `created_at`, `updated_at` | `timestamptz`. |
| `last_login_at` | Nullable. |

Identity and sync rules:

1. Panel existence and panel-held fields are owned by the panel and mirrored into `panel_clients`. Every mirrored panel
   client gets an `app_users` row with `email_verified_at = NULL` when no identity conflict prevents it; this is an
   identity/state owner, not a verified web login. Sessions are created only after a code is verified.
2. Match an app user to a panel client by `panel_client_id` first, then by normalized email. Use Telegram ID only when the
   match is unambiguous. Ambiguity is a conflict, not a merge.
3. `TgId = 0` mirrors as `telegram_id = NULL`.
4. App-initiated email changes write the panel first, then update `panel_clients`/`app_users`. Direct panel edits are
   treated as panel-originated changes and reconciled by sync.
5. If a panel email changes to an address already used by another app user or panel client, keep the last-known-good
   identity, persist a `panel_sync_conflicts` row, and alert admins. Never silently merge.
6. If a panel client is deleted, mark the mirror deleted and keep the app user/session rows for audit. The dashboard shows
   a clear "account no longer exists / contact support" state; sessions are not silently transferred.
7. A direct panel email change clears `email_verified_at` until the new address completes a login code.
8. Telegram registration does not require email verification. The first web login proves control of the email. This is an
   accepted risk documented in §1.7, not a reason to auto-merge accounts.

**Prerequisite:** the panel adapter must distinguish "email not found" from "panel unavailable". Today
`GetClientByEmailAsync` throws `PanelApiException` for both. Add a `TryGetClientByEmailAsync` (or equivalent) that
returns `null` only for a definitive not-found response and throws for transport/server failures. The sync uses the same
distinction.

### 1.5 Email-code login flow

**Request code** — `POST /api/auth/email/request-code`

1. Validate and normalize the email.
2. Apply rate limits (per email and per IP).
3. Load the `panel_clients` mirror by normalized email. If it is missing and the mirror is stale, do a scoped panel lookup.
   If there is still no account, return `202` and do nothing.
4. Invalidate any previous unconsumed code for that panel client.
5. Generate a 6-digit code. Store only `HMAC-SHA256(server_pepper, code + ":" + email_normalized)`.
6. Insert an `email_login_codes` row with a 10-minute expiry and `attempts = 0`.
7. Enqueue the email in `email_outbox` in the same transaction. The transaction commits the code and the outbox row
   together; a background processor sends it.
8. Return `202 Accepted` with a generic message, whether or not the account exists.

**Verify code** — `POST /api/auth/email/verify-code`

1. Normalize the email and load the active code.
2. Reject if expired, consumed, or attempts exhausted.
3. Increment attempts on failure.
4. On success, atomically mark the code consumed with a conditional update, create/find the `app_users` row linked to the
   panel client, set `email_verified_at`, create a `web_sessions` row, and audit the event in one transaction.
5. Set the session cookie and return the current user summary.

**Session**

- Opaque random token (32 bytes), stored only as a hash.
- Cookie: `HttpOnly`, `Secure` in production, `SameSite=Lax`, `Path=/`, `__Host-` prefix in production.
- Sliding expiry (proposed 30 days) with a maximum absolute lifetime (proposed 90 days).
- Logout revokes the session; "log out everywhere" is a later addition.
- Email changes should revoke other sessions.

**Logout** — `POST /api/auth/logout`

- Revoke the current session, clear the cookie, audit.

### 1.6 Email delivery

This is a new operational dependency. There is no email provider configured today.

Decisions:

- **Provider:** Resend, behind an `IEmailSender` adapter. Keep the adapter thin so another provider can be added later.
- **Sending domain and from/reply-to addresses:** still to be chosen.
- **SPF, DKIM, and DMARC:** required before production sends.
- **Bounce/complaint handling:** start with Resend webhooks/log review; add an admin alert for repeated failures.

Suggested configuration keys (names are provisional):

| Key | Purpose |
|---|---|
| `Email:Provider` | `resend`, `postmark`, `smtp`, or `log`. |
| `Email:ApiKey` | Provider API key. |
| `Email:FromAddress` | Verified sender address. |
| `Email:FromName` | Display name. |
| `Email:ReplyTo` | Optional reply-to. |
| `Email:BaseUrl` | Public web app base URL for links. |
| `Auth:CodePepper` | Server-side pepper for code hashes. |
| `Auth:CodeTtlMinutes` | Code lifetime. |
| `Auth:CodeMaxAttempts` | Failed verification limit. |
| `Auth:ResendCooldownSeconds` | Minimum time between sends. |

- Development/tests should use `Email:Provider=log` (code appears in logs only in development) or a local Mailpit
  container.
- **Transactional outbox is required.** Enqueue the email in the same EF Core transaction as the code/session change, then
  let a background processor send it. This prevents a provider outage from losing a login code.
- Outbox processing: claim rows with `FOR UPDATE SKIP LOCKED`, commit the claim, send outside the transaction, then mark
  sent. Retry with exponential backoff, cap attempts, and move exhausted rows to a dead-letter state.
- Protect the message payload at rest with ASP.NET Core Data Protection. Do not store a plaintext login code in the
  outbox. Store a template key plus protected template data, and render at send time.
- Persist the Data Protection key ring outside the container (mounted volume or shared store) so restarts/deploys can
  still read pending outbox payloads.
- Never log codes, tokens, or full email bodies in production logs.

### 1.7 Email changes and recovery

If the user cannot receive the login code, the email is wrong, or the mailbox is inaccessible:

1. **Telegram self-service** (new feature): a bot command/menu action to change the login email.
   - Check the new email is not already used by another panel client.
   - Send a verification code to the new address.
   - Apply the change only after the code is confirmed.
   - Rate-limit and audit the change.
   - If the user cannot receive email at all, an admin override is required.
2. **Admin support**: existing `/admin` → `✏️ Change Email` flow, extended to update `app_users` and audit the change.
3. **Web self-service** (after login): request a change, verify the new address, update panel + app user, revoke other
   sessions.

**Email verification at registration — decided:** Telegram registration does **not** require email verification. This
keeps bot onboarding fast. Email ownership is proven by the login code and by the new-address code during email changes.

This is an accepted risk: someone can register a Telegram account with an email they do not control. The email owner could
then log in with a code and see that subscription, but cannot silently take over the Telegram identity. The mirror's
duplicate/conflict detection and admin alerts are the safety net. Revisit this decision if abuse appears; adding a
registration-time verification step would close it at the cost of extra onboarding friction.

### 1.8 Planned web API

All routes are under `/api`, use cookie auth, JSON, and Problem Details. None are implemented.

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/auth/email/request-code` | Send a login code. |
| `POST` | `/api/auth/email/verify-code` | Verify code and create a session. |
| `POST` | `/api/auth/logout` | Revoke the current session. |
| `GET` | `/api/me` | Current app user and Telegram-link state. |
| `GET` | `/api/subscription` | DB-mirrored status, expiry, traffic, devices, subscription URL, `last_synced_at`, stale flag. |
| `POST` | `/api/subscription/refresh` | Scoped panel refresh for the current user; rate-limited; returns the updated DB row. |
| `GET` | `/api/referrals` | Referral link, credit balance/history, pending attribution. |
| `POST` | `/api/account/email/request-change` | Send a code to the new email. |
| `POST` | `/api/account/email/verify-change` | Confirm and apply the email change. |

OpenAPI should be the source of truth for the contract once implementation starts. A Postman collection can carry
runnable auth-flow examples if the team wants one.

### 1.9 Frontend

Proposed routes:

- `/login` — email entry, code entry, resend countdown.
- `/dashboard` — status, expiry, traffic bar, device limit, subscription URL, copy button, QR code.
- `/referrals` — personal referral link, credit balance/history, how it works.
- `/account` — email, Telegram link state, email-change flow, support.
- `/connect` — how-to-connect guide.

UX requirements:

- Mobile-first; most users arrive from a phone.
- Accessible labels, focus states, keyboard support.
- No Telegram dependency for any of the above.
- Dashboard reads come from Postgres. Show `last_synced_at` and a stale badge when the mirror is older than the expected
  interval.
- A **Refresh** button calls the scoped panel refresh and updates the mirror; it must not block the initial page render.
- Show a friendly degraded state if the panel refresh fails; do not log the user out or hide the last-known state.
- Never expose another user's data. All refresh/lookup operations are scoped by the authenticated app user's panel client.
- QR generation can stay client-side to avoid a server endpoint.

Build/serving:

- Vite build output copied to `wwwroot`.
- `UseStaticFiles()` + SPA fallback to `index.html`, excluding `/api`, `/webhooks`, `/health`, `/alive`, `/openapi`,
  and `/swagger`.
- In development, run Vite separately with a proxy to the API.
- Dockerfile gains a Node build stage in R6, when the SPA is added; `.dockerignore` excludes `node_modules`. R0–R3 do not
  change the runtime image.

### 1.10 Proposed web/auth schema (EF Core/PostgreSQL)

All timestamps are UTC (`timestamptz`). UUID primary keys. Exact column names may change during implementation. Panel
mirror tables are in §4.2; app-state migration tables are in §4.6.

```text
app_users
  id                uuid primary key
  email_normalized  text not null unique
  telegram_id       bigint null
  panel_client_id   bigint not null unique
  email_verified_at timestamptz null
  created_at        timestamptz not null
  updated_at        timestamptz not null
  last_login_at     timestamptz null

  unique index on (telegram_id) where telegram_id is not null
  foreign key (panel_client_id) references panel_clients(panel_client_id)

email_login_codes
  id                uuid primary key
  panel_client_id   bigint not null references panel_clients(panel_client_id) on delete cascade
  app_user_id       uuid null references app_users(id) on delete cascade
  email_normalized  text not null
  code_hash         bytea not null
  created_at        timestamptz not null
  expires_at        timestamptz not null
  consumed_at       timestamptz null
  attempts          int not null default 0
  request_ip        inet null

  index on (email_normalized, expires_at)
  index on (panel_client_id, expires_at)

web_sessions
  id                uuid primary key
  app_user_id       uuid not null references app_users(id) on delete cascade
  token_hash        bytea not null unique
  created_at        timestamptz not null
  last_seen_at      timestamptz not null
  expires_at        timestamptz not null
  revoked_at        timestamptz null
  ip                inet null
  user_agent        text null

  index on (app_user_id, expires_at)

audit_events
  id                bigint generated always as identity primary key
  occurred_at       timestamptz not null
  actor_user_id     uuid null references app_users(id) on delete set null
  actor_type        text not null          -- web | telegram | admin | system | tribute
  event_type        text not null
  email_normalized  text null
  telegram_id       bigint null
  correlation_id    text null
  ip                inet null
  user_agent        text null
  metadata          jsonb null

  index on (occurred_at)
  index on (actor_user_id, occurred_at)
  index on (event_type, occurred_at)

email_outbox
  id                  uuid primary key
  template_key        text not null          -- login_code | email_change_code
  to_email            text not null
  payload_protected   bytea not null         -- Data Protection, never plaintext code
  status              text not null          -- pending | processing | sent | dead
  attempts            int not null default 0
  next_attempt_at     timestamptz not null
  locked_until        timestamptz null
  last_error          text null
  dedupe_key          text null
  created_at          timestamptz not null
  sent_at             timestamptz null

  index on (status, next_attempt_at)
  index on (created_at)
```

Outbox rows are claimed with `SELECT ... FOR UPDATE SKIP LOCKED`. A row is marked `processing` with a short lease,
committed, then sent. On success it becomes `sent`; failure increments `attempts` and sets `next_attempt_at`. After the
maximum attempts it becomes `dead` and raises an operational alert. A cleanup job removes sent/dead rows after the
retention window, but audit events remain.

Audit event examples: `web.auth.code_requested`, `web.auth.code_sent`, `web.auth.code_verify_failed`,
`web.auth.login`, `web.auth.logout`, `web.account.email_change_requested`, `web.account.email_changed`,
`web.session.revoked`, `panel.lookup_failed`, `panel.account_not_found`, `admin.email_changed`,
`referral.credit_granted`.

Retention decisions to make: audit events (proposed 1 year), session rows (purge after expiry + 30 days), login
codes (purge after expiry + 1 day), IP/user-agent storage (privacy tradeoff).

### 1.11 Security checklist

- Codes hashed with a server-side pepper; never stored or logged in plaintext.
- Outbox payloads that contain codes are protected with ASP.NET Core Data Protection.
- Short code TTL, resend cooldown, max attempts, rate limits per email and IP.
- Generic responses to avoid email enumeration.
- Session tokens hashed at rest; opaque cookies; revocation supported.
- Panel refresh is rate-limited per user and globally so the web app cannot amplify load on 3X-UI.
- CSRF protection: `SameSite=Lax` plus an `Origin`/`Sec-Fetch-Site` check on mutating requests.
- No CORS needed because the SPA is same-origin.
- Email-change flows re-verify the new address; admin overrides are audited.
- Rate-limit panel lookups to protect the 3X-UI API.
- Do not put subscription URLs or tokens in logs.
- Consider hashing/truncating IPs if full retention is not justified.

### 1.12 Hosting and migrations

- Local development: Aspire AppHost adds a PostgreSQL resource; the API references it.
- Production: Coolify-managed PostgreSQL (or external), with `ConnectionStrings__Postgres` in secrets.
- Startup migrations:
  - acquire a Postgres advisory lock;
  - run `Database.MigrateAsync()` with a timeout;
  - release the lock;
  - fail startup on migration failure.
- One instance is sufficient for the lock; multiple replicas also work because only one acquires the lock.
- Backups: enable Coolify scheduled backups and test a restore.
- Health checks: `/health` should include database readiness; `/alive` stays process-only.
- Observability: EF Core/Npgsql traces, audit events, and a metric/alerts for email-provider failures.

### 1.13 Implementation phases

| Phase | Scope | Status |
|---|---|---|
| 0 | Finalize schema/contract details, sending domain, from/reply-to, and operational limits. | Design only |
| 1 | R3a: EF Core/Postgres, entities, migrations, Testcontainers, auth/session/outbox/audit schema, persistence primitives. **No HTTP endpoints.** | Implemented |
| 2 | R3b: comment backfill + panel mirror/sync/conflicts + admin sync/default-inbound menu. | Planned |
| 3 | R3c: email outbox processor + email-code auth/identity use cases. **Still no endpoints.** | Planned |
| 4 | R6: auth endpoints + SPA shell + login page. | Planned |
| 5 | DB-backed dashboard (subscription, traffic, expiry, devices, link/QR, refresh). | Planned |
| 6 | Referrals, account page, email change, support. | Planned |
| 7 | Email verification at registration, web sign-up, non-Telegram payments, credits integration. | Deferred |

Phase 1 is the first web-app code slice once approved, after R0–R2. Each phase must be independently testable before the
next one starts; do not expose routes before the use cases behind them are covered.

### 1.14 Open decisions

1. Sending domain, from/reply-to addresses, and bounce/complaint alerting.
2. Code length/TTL/resend policy and session lifetime.
3. Admin override policy when a user cannot receive email at any address.
4. Whether to allow web self-signup (currently no) and how to handle payment/anti-abuse.
5. **Web payments:** Tribute checkout is Telegram-native. A user who cannot access Telegram can view their
   subscription but cannot renew it. Removing that dependency needs a non-Telegram payment provider; this is the
   biggest open product question.
6. Support channel for users who cannot access Telegram (email address, contact form, or admin inbox).
7. Whether to store raw IP/user-agent values and for how long.
8. Whether the web app shows referral/credit data in v1 or only the subscription dashboard.
9. OpenAPI/Postman contract ownership and CI validation.
10. Exact sync interval, staleness threshold, and manual-refresh rate limit once real panel latency is measured.

---

## 2. Clean architecture refactor

**Status:** R0–R2 are implemented, along with the R3a persistence foundation. R3b–R7 remain planned. This is the
delivery vehicle for the web app, not a separate rewrite.

### 2.1 Why now

The project is outgrowing a single deployable project:

- `TelegramHandlers.cs` is ~2,800 lines and mixes parsing, conversation state, business rules, and rendering.
- `AdminPanelService.cs` is ~550 lines and mixes admin orchestration, panel calls, referral settlement, and Telegram messaging.
- Panel API DTOs (`PanelClient`, `PanelClientResponse`, etc.) leak into business logic.
- `Program.cs` contains composition, webhook routing, and request handling in one place.
- The web app adds Postgres, email, sessions, audit, and new HTTP endpoints. Without explicit boundaries, those concerns will
  spread into the bot code and the panel adapter.

The goal is **not** a big-bang rewrite. Behavior stays the same while code moves behind explicit boundaries, one
independently verifiable slice at a time.

### 2.2 Target layout

Keep the existing deployable project name `EagleTunnelApi` as the **Api** project to avoid churn in the Dockerfile,
AppHost, namespaces, and deployment configuration. Add three projects around it:

```text
EagleTunnelApi.Domain/          pure business rules; no project references
EagleTunnelApi.Application/     use cases and ports; references Domain only
EagleTunnelApi.Infrastructure/  adapters (panel, Telegram, email, EF Core); references Application + Domain
EagleTunnelApi/                 Api: HTTP endpoints, webhooks, middleware, composition root
EagleTunnelApi.Tests/           unit, integration, API, and architecture tests
EagleTunnelApi.AppHost/         Aspire orchestration
EagleTunnelApi.ServiceDefaults/ shared telemetry/health defaults
```

An optional later rename to `EagleTunnelApi.Api` is cosmetic and should be done separately, if at all.

### 2.3 Dependency rules

| Project | May reference | Must not reference |
|---|---|---|
| `EagleTunnelApi.Domain` | Nothing | Application, Infrastructure, Api, EF Core, Npgsql, Telegram.Bot, ASP.NET Core |
| `EagleTunnelApi.Application` | Domain; DI abstractions only for registration | Infrastructure, Api, EF Core, Npgsql, Telegram.Bot, ASP.NET Core |
| `EagleTunnelApi.Infrastructure` | Application, Domain | Api |
| `EagleTunnelApi` (Api) | Application, Infrastructure, Domain | Nothing is forbidden, but it must stay thin |
| `EagleTunnelApi.Tests` | All projects | — |

The compiler enforces the project graph. Architecture tests enforce the package/type rules that project references cannot
express (for example, no `Telegram.Bot` types in Application).

### 2.4 What moves where

| Current code | Target | Notes |
|---|---|---|
| `Configuration/*Options.cs` | Api/Infrastructure | Adapter configuration stays in the outer layer. Business rules encoded in validators move to Domain/Application. |
| `PanelApi/PanelApiClient.cs`, `PanelApi/Models/*` | Infrastructure/Panel | Keep the HTTP client and panel JSON DTOs here. |
| `PanelApi/IPanelClient.cs` | Application port + Infrastructure adapter | Replace with an application-facing `IPanelGateway` (name provisional). Map panel DTOs to application models in the adapter. |
| `Telegram/SubscriptionStatus.cs`, `SubscriptionFormatter` | Domain/Subscriptions | Pure status/formatting rules. |
| `Telegram/ReferralService.cs` | Domain + Infrastructure | Pure tag parsing, email normalization, credit math, and paid/trial policy move to Domain. Panel-comment projections stay in Infrastructure or a mapping adapter. |
| `Telegram/SubscriptionProvisioner.cs` | Application use case/service | Orchestrates activation; depends on `IPanelGateway`, not Telegram.Bot. |
| `Telegram/AdminPanelService.cs` | Application use cases + adapters | Split by use case: grant, ban, unban, device limit, reset traffic, link Telegram, change email, settle referrals. |
| `Telegram/ReferralSettlementService.cs`, `ReferralSettlementRunner.cs` | Application + background job | Background job triggers a use case; no Telegram rendering. |
| `Telegram/ExpiryReminderService.cs` | Application + background job | Use case computes who to notify; `ITelegramNotifier` sends. |
| `Telegram/TelegramHandlers.cs` | Api/Telegram adapter | Parse update → resolve session → call use case → render response. No business rules. |
| `Telegram/MenuService.cs`, `SessionStore.cs`, polling, dedupe, webhook processor | Api/Telegram adapter | Presentation/conversation concerns, not Domain. |
| `Webhook/Handlers/*` | Application commands/use cases | Map verified events to commands. |
| `Webhook/Security/*`, `Webhook/Events/*` | Infrastructure/Api | Signature verification and transport DTOs are adapter concerns. |
| `Logging/*` | Api | Middleware/handlers stay in the outer layer. |
| `Program.cs` | Api composition root | Register `AddDomain`/`AddApplication`/`AddInfrastructure`/`AddApi`; endpoints call use cases. |

### 2.5 Use cases are the unit of work

Application handlers should be small, explicit classes with one job. Candidate use cases:

- **Identity/auth:** `RegisterTelegramUser`, `ClaimInvitedAccount`, `LinkTelegram`, `ChangeEmail`,
  `RequestEmailLoginCode`, `VerifyEmailLoginCode`, `Logout`, `RevokeSession`.
- **Subscription:** `ActivateSubscription`, `RenewSubscription`, `CancelSubscription`, `GrantDays`,
  `SetDeviceLimit`, `ResetTraffic`, `BanAccount`, `UnbanAccount`.
- **Referral:** `CreateInvite`, `SetReferrer`, `SettleReferralBonus`, `SweepPendingReferrals`.
- **Queries:** `GetDashboard`, `GetSubscription`, `GetReferralSummary`, `FindAccountsForAdmin`.
- **Background:** `SendExpiryReminders`, `SettlePendingReferrals`, `PreflightPanelInboundConfiguration`.

Ports (interfaces) live in Application and are implemented in Infrastructure:

| Port | Responsibility |
|---|---|
| `IPanelGateway` | Panel reads/writes, paged/full client reads, inbounds, with application models and explicit not-found vs unavailable errors. |
| `IPanelDirectory` | Query the Postgres panel mirror; separate from live panel I/O. |
| `IPanelSync` | Run a scoped or full sync, read sync state, and record conflicts. |
| `ITelegramNotifier` | Send a message/notification without exposing Telegram.Bot types to Application. |
| `IEmailSender` | Provider adapter (Resend in production; log/Mailpit in tests). |
| `IEmailOutbox` | Enqueue and claim transactional email messages. |
| `IUnitOfWork` | Commit a use-case transaction; implemented by EF Core in Infrastructure. |
| `IUserRepository`, `IPanelClientRepository`, `ISessionRepository`, `ILoginCodeRepository`, `IAuditRepository` | Focused persistence/query ports. Keep `DbContext` and EF Core types out of Application. |
| `ICodeHasher`, `ITokenGenerator`, `IClock` | Testable crypto/time seams. |
| `IAuditWriter` | Append audit events. |

Rules:

- Application must not reference `Telegram.Bot`, EF Core, Npgsql, or ASP.NET Core.
- Do not expose `PanelClient`/panel DTOs to Application or Domain. Map them in the Infrastructure adapter.
- Use focused persistence/query ports, not `DbContext`/`DbSet`, in Application. Keep `AppDbContext` in Infrastructure.
  Avoid a generic repository; each port should expose the queries/operations its use cases actually need.
- Use `IUnitOfWork` only where one transaction spans more than one repository (for example code + outbox, or session +
  audit). Single-aggregate operations can commit through their repository.
- Start with explicit handler classes and DI; do not add MediatR/Mediator in the first refactor. Add a lightweight
  dispatcher only if cross-cutting pipeline behavior (validation, logging, transactions) proves it is worth it.

### 2.6 Domain model

Keep the Domain small and pure:

- `SubscriptionStatus`, `SubscriptionPeriod`, `SubscriptionFormatter`.
- `ReferralAttribution`, `ReferralPolicy`, `CreditDays`.
- `EmailAddress` (normalization/validation).
- `Result`/`Error` or a small domain exception type.

Domain rules include self-referral rejection, first-paid-only bonuses, no chaining, trial vs paid classification,
credit lapse/consumption, reminder thresholds, and email normalization. Date/time and panel-specific JSON stay outside
the Domain.

Not every table needs a rich Domain entity. Auth/session/login-code/audit rows can be Infrastructure persistence models
with Application DTOs until real behavior emerges. Avoid anemic domain entities created only to satisfy a diagram.

### 2.7 Adapter rules

- **Panel:** preserve the distinction between “not found” and “panel unavailable”. This is required for email login.
- **Telegram:** the adapter owns Telegram.Bot, menus, callback data, and conversation state. It calls use cases and renders
  their results. Background use cases call `ITelegramNotifier`; they do not build `InlineKeyboardMarkup`.
- **Tribute:** verify the signature in Infrastructure/Api, map the verified event to an Application command, and keep
  idempotency at the use-case boundary.
- **Email:** provider adapter in Infrastructure; log/Mailpit mode for development and tests.
- **EF Core:** one focused `AppDbContext`, `IEntityTypeConfiguration<T>` per aggregate, and migrations in Infrastructure.
  Application sees focused ports, not `DbContext`. Use `IDbContextFactory<AppDbContext>` in background services. The
  provider-specific rules and test strategy are in §4.8.

### 2.8 Strangler migration phases

Each phase is a separately verifiable, commit-sized slice. No behavior change unless the phase explicitly adds a feature.

| Phase | Scope | Exit criteria |
|---|---|---|
| R0 ✅ | Create Domain/Application/Infrastructure projects, add project references, add architecture tests. Move no production code. | Solution builds; all existing tests pass; architecture tests fail on an intentional violation. |
| R1 ✅ | Move pure Domain types and their tests (`SubscriptionStatus`, formatter, referral parsing/policies, email normalization). | Existing behavior unchanged; tests moved/updated; no panel DTOs in Domain. |
| R2 ✅ | Introduce `IPanelGateway` and application models; move `PanelApiClient` + DTOs to Infrastructure; map at the boundary. | No panel DTOs outside Infrastructure; not-found vs unavailable preserved; tests pass. |
| R3a ✅ | EF Core/Postgres foundation: `AppDbContext`, configurations, migrations, startup migration runner, Testcontainers harness, auth/session/outbox/audit/panel-mirror schema. No HTTP endpoints. | Migrations apply to a clean Postgres; schema tests cover unique/partial indexes and `xmin`; no endpoints exposed. |
| R3b | Comment backfill + panel directory/sync/conflicts + admin sync/default-inbound menu. | Initial backfill is idempotent; sync is locked and observable; conflicts are persisted and alerted; default inbounds come from Postgres. |
| R3c | Email outbox processor + email-code auth/identity use cases. No HTTP endpoints. | Code + outbox commit atomically; claim/retry/dead-letter tests pass; auth use cases covered against Postgres. |
| R4 | Extract registration, claim, activation, and referral-settlement use cases; shrink `TelegramHandlers`. | Bot flows still pass existing tests; handler contains parsing/rendering only; no business rules. |
| R5 | Extract admin, reminder, trial, cancel, and device-limit use cases; split `AdminPanelService`; background jobs call use cases. | Admin/reminder behavior unchanged; `AdminPanelService` is gone or a thin facade; tests pass. |
| R6 | Add web auth endpoints + SPA shell + DB-backed dashboard + manual refresh. | Login, session, dashboard, refresh, and logout work end to end; no Telegram requirement. |
| R7 | Credits ledger (Section 3). | Ledger design implemented behind use cases; panel projection reconciled. |

R0–R3a are implemented: the Domain/Application/Infrastructure projects, NetArchTest boundary tests, the pure domain types
(subscription status/formatter, referral/trial/credit tag policy, email normalization), the panel gateway, and the EF
Core/PostgreSQL foundation with its initial migration and startup runner are in the solution. R3b–R7 remain unbuilt.

Sequencing:

1. R0 → R1 → R2 are prerequisites for clean web/auth code.
2. R3a is the first persistence slice; do not build web/auth features on ad-hoc SQL.
3. R3b comes next because login, dashboard, conflicts, and admin tooling all depend on the panel mirror.
4. R3c may be developed in parallel with R3b after R3a, but it must not be enabled in production until the mirror sync is
   healthy.
5. R4/R5 can be interleaved from R2 onward; they reduce risk before the web surface grows.
6. R6 follows R3b + R3c, with R4/R5 continuing in parallel where safe.
7. R7 comes after auth/identity and the panel mirror are stable.

**Comment-write stop condition:** do not remove comment writes until R3b backfill, dual-write reconciliation, and the
DB-backed read switch have all passed. See §4.6.

Use the strangler pattern: a new use case may call the existing service initially, then absorb the logic, then delete the
old path once tests cover it. Do not delete a working flow before its replacement is verified.

### 2.9 Architecture tests

Add automated checks, for example with NetArchTest or direct assembly-reference assertions:

- Domain references no other production project.
- Application references only Domain (plus DI abstractions).
- Application contains no `Microsoft.EntityFrameworkCore`, `Npgsql`, `Telegram.Bot`, or `Microsoft.AspNetCore` types.
- Infrastructure does not reference Api.
- Panel DTO types are used only in Infrastructure.
- Api endpoints do not contain business-rule types beyond request/response mapping (best-effort; review-enforced).

Run architecture tests in CI with the normal build/test job.

### 2.10 Definition of done

- Dependency rules are enforced by tests.
- No EF Core, Npgsql, Telegram.Bot, or ASP.NET Core in Domain/Application.
- Panel DTOs are confined to Infrastructure.
- `TelegramHandlers` is a thin adapter; business flows live in Application use cases.
- `AdminPanelService` is split into use cases or reduced to a thin adapter.
- API endpoints map HTTP to use cases and do not contain business logic.
- The existing test suite stays green; new use cases have focused tests.
- `dotnet build`, `dotnet format --verify-no-changes`, and `dotnet test` pass.

### 2.11 Risks and mitigations

| Risk | Mitigation |
|---|---|
| Big-bang rewrite breaks bot flows | Strangler phases; keep behavior tests; delete old code only after replacement is covered. |
| Mapping overhead between panel DTOs and application models | Keep models small and focused; map only what each use case needs. Phase the move. |
| Namespace churn and merge conflicts | Schedule R0–R2 when no large feature branches are open; keep mechanical moves separate from behavior changes. |
| Telegram UI coupling | Adapter owns rendering; use cases return view models/results. |
| Panel API quirks (not-found vs failure) | Preserve error semantics in `IPanelGateway`; add adapter tests. |
| Test rewrites slow the refactor | Move tests with their types; keep one test project with folders initially. |

### 2.12 Decisions and open questions

- **Project name:** keep `EagleTunnelApi` as Api for now; rename later only if it adds clarity. Recommended: keep.
- **Mediator:** no MediatR/Mediator in the first refactor. Recommended: explicit handlers; revisit if pipeline behavior grows.
- **Test layout:** one `EagleTunnelApi.Tests` project with `Domain/`, `Application/`, `Infrastructure/`, `Api/`, and
  `Architecture/` folders. Split into multiple test projects only if size becomes painful.
- **Telegram adapter location:** keep it in the Api project under `Telegram/` (outer layer) for now. A dedicated adapter
  project is optional later.
- **Panel abstraction depth:** target application models; phase the mapping so R2 does not become a rewrite.
- **Persistence abstraction:** focused repository/query ports plus `IUnitOfWork`; never expose `DbContext`/`DbSet` to
  Application.
- **Auth persistence models:** keep EF entities in Infrastructure with Application DTOs unless real domain behavior
  emerges.
- **Panel mirror:** a first-class Postgres read model, not a cache. Dashboard and admin reads use it; the panel remains the
  enforcement source for panel fields.

---

## 3. Credits platform and ledger

**Status:** Storage schema implemented in R3a. Ledger behavior, wallet, spending, and payments remain design-only until
the web auth foundation is stable and R7 begins.

### 3.1 Goal

Replace the interim Tribute + panel-comment model with:

> pay → virtual balance → spend/renew

The referral bonus currently stored as `Referral credit: 30d` becomes a ledger entry. The ledger should support
referral bonuses, purchases, renewals, adjustments, and migration from existing comments.

### 3.2 Current interim state

- Referral attribution, bonus credit, trial, cancellation, and reminder markers live in the panel client `comment`
  field.
- `SubscriptionProvisioner` applies credit days to the panel expiry on activation.
- A lapsed `Referral credit` is treated as consumed and cleared.
- This is documented in [README.md](README.md#referral-system).
- The migration to Postgres-owned app state is defined in §4.6. The ledger is the final owner of credit entries; the
  interim comment tags become a read-only compatibility projection during the transition.

### 3.3 Proposed ledger

```text
credit_entries
  id                uuid primary key
  user_id           uuid not null references app_users(id)
  unit              text not null          -- 'days' | 'currency'
  amount            numeric(18,4) not null -- positive credit, negative spend
  currency          char(3) null           -- required when unit = 'currency'
  reason            text not null          -- referral_bonus | purchase | renewal | adjustment | expiry | migration
  reference_type    text null              -- tribute_event | referee | admin | migration
  reference_id      text null
  idempotency_key   text not null unique
  occurred_at       timestamptz not null
  expires_at        timestamptz null
  metadata          jsonb null

  index on (user_id, unit, occurred_at)

credit_applications
  id                    uuid primary key
  user_id               uuid not null references app_users(id)
  entry_id              uuid not null references credit_entries(id) unique
  applied_at            timestamptz not null
  panel_expiry_before   timestamptz not null
  panel_expiry_after    timestamptz not null
```

A `credit_balances` view computes the remaining balance by user/unit/currency. Day credits are consumed through
`credit_applications`; monetary credits are debited by negative `credit_entries`.

Idempotency examples:

- `referral:{referee_user_id}:first_paid`
- `tribute:{event_id}`
- `migration:{email_normalized}:{days}`
- `admin:{admin_id}:{correlation_id}`

### 3.4 Panel interaction

The panel remains the enforcement point for access. The ledger is the entitlement record.

- On a payment/renewal, compute the new panel expiry from the paid period plus active credit days, then record the
  application.
- On expiry, mark day credits consumed; do not re-apply them on a later resubscribe.
- The existing `Referral credit` comment tag can remain as a read-only projection during the transition.
- Reminder logic should eventually use `access_until` + remaining balance instead of parsing comments.

### 3.5 Migration from comments

1. Scan panel clients for every app-state tag: `Referred by:`, `Referred by (pending):`, `Referrer tgId:`,
   `Referral bonus paid`, `Referral credit: Nd`, `Trial until`, `Cancelled`, and `Reminder Nd sent for`.
2. Insert idempotent migration records into the owning Postgres tables (referral attribution, trial, cancellation,
   reminder delivery, credit entry). Use an idempotency key that includes the normalized email and tag value.
3. Keep the comment tags until all readers use Postgres; do not delete or rewrite them during backfill.
4. Dual-write new app-state changes to Postgres and the legacy comment during the transition.
5. Switch reads to Postgres, reconcile the report, then stop writing/clearing comment tags.
6. Keep the migration idempotent and re-runnable; a second run must not duplicate bonuses or reminders.

### 3.6 Referral payout integration

- On first paid activation, insert a `referral_bonus` entry for the referrer with the unique idempotency key.
- Record an audit event and keep the existing bonus DM.
- Do not chain bonuses: paying a referrer must not settle their referrer.
- The ledger replaces the "has bonus been paid" comment marker once trusted.

### 3.7 Payment provider replacement

Tribute retirement is part of this phase, not a prerequisite for the ledger. Open work:

- Choose a non-Telegram checkout provider for web payments.
- Define webhook verification, idempotency, refunds, chargebacks, and reconciliation.
- Decide whether prices are in currency, days, or both.
- Decide how taxes/receipts are handled.
- Define what happens to active Tribute subscriptions during cutover.
- Retire the Tribute cancel flow, cancel webhook, cancel video, and reminder copy as one cutover unit.

### 3.8 Phases

| Phase | Scope |
|---|---|
| 1 | Finalize units, reasons, idempotency, and panel-projection rules. |
| 2 | Add ledger tables + service + tests; no reads/writes from production paths. |
| 3 | Backfill from comments; dual-write new referral bonuses; reconcile. |
| 4 | Switch reads and bonus checks to the ledger. |
| 5 | Introduce wallet/purchase flows and retire Tribute/comment tags. |

Because comment removal needs a DB destination for `Referral credit`, the table creation and migration-entry backfill
from Phases 2–3 may land in R3a/R3b. That is storage only; wallet, spending, purchase, and payment behavior stays in R7.

### 3.9 Open decisions

- Day credits only first, or money + days from the start?
- How are unused credits expired or refunded?
- Multi-currency? Taxes?
- Does a referral bonus become spendable wallet balance or only subscription days?
- When an admin edits panel expiry directly, should the ledger record an adjustment automatically or only flag the
  discrepancy for review? The panel stays authoritative for enforcement; the ledger stays authoritative for entitlement.

---

## 4. Identity, panel directory, sync, and conflicts

**Status:** The R3a persistence schema is implemented. Backfill, panel sync, conflicts, and admin flows are planned in
R3b; login and dashboard use cases follow. This is the foundation for login, dashboard, admin tooling, and comment
removal.

### 4.1 Authority split

| Concern | Authority | Notes |
|---|---|---|
| Panel client existence, panel client ID, panel email/tgId | 3X-UI panel | Postgres mirrors; identity conflicts are reported, never auto-merged. |
| Enforced subscription fields: `enable`, expiry, traffic quota, device limits, inbounds, `subId`, flow, reset counters | 3X-UI panel | Postgres mirrors for reads. App-initiated changes write the panel first, then update the mirror. |
| Web login email, email verification, sessions, audit | Postgres | Panel email is kept in sync; direct panel edits are reconciled. |
| Referral attribution, credits, trial, cancellation, reminders, app settings | Postgres | Panel comments are legacy input only until the migration in §4.6 is complete. |
| Sync state, conflicts, sync history | Postgres | Admin-visible; no panel equivalent. |

Rules:

- The panel is the enforcement point. If Postgres and the panel disagree about access, the panel wins.
- The dashboard reads Postgres. It never calls the panel on page load.
- A manual refresh and the 10-minute sync are the only normal paths that read the panel.
- Every admin/panel mutation is audited and confirmed before execution.
- Identity collisions are quarantined in `panel_sync_conflicts`; do not overwrite the last-known-good identity.

### 4.2 Panel mirror schema

```text
panel_clients
  id                    uuid primary key
  panel_client_id       bigint not null unique       -- 3X-UI client id
  email_normalized      text not null unique
  telegram_id           bigint null
  enable                bool not null
  expiry_time           timestamptz null
  total_gb              bigint not null
  limit_ip              int not null
  limit_hwid            int not null
  traffic_reset         text null
  traffic_reset_day     int null
  reset                 int not null
  sub_id                text null
  flow                  text null
  security              text null
  traffic_up            bigint not null
  traffic_down          bigint not null
  traffic_enabled       bool null
  legacy_comment        text null                   -- migration/audit only; never app state
  panel_created_at      timestamptz null
  panel_updated_at      timestamptz null
  last_synced_at        timestamptz not null
  is_deleted            bool not null default false
  deleted_at            timestamptz null

  unique index on (telegram_id) where telegram_id is not null
  index on (panel_updated_at)
  index on (is_deleted, last_synced_at)

panel_inbounds
  inbound_id              int primary key
  remark                  text null
  tag                     text null
  protocol                text null
  port                    int null
  is_default_for_new_clients bool not null default false
  sort_order              int not null default 0
  last_synced_at          timestamptz not null

panel_client_inbounds
  panel_client_id       uuid not null references panel_clients(id) on delete cascade
  inbound_id            int not null references panel_inbounds(inbound_id)
  primary key (panel_client_id, inbound_id)

panel_sync_state
  id                    text primary key                 -- 'default'
  cursor_panel_updated_at timestamptz null
  last_run_mode         text null                       -- cursor | full
  last_started_at       timestamptz null
  last_completed_at     timestamptz null
  last_success_at       timestamptz null
  last_error            text null
  last_seen_count       int not null default 0
  last_changed_count    int not null default 0
  lock_owner            text null
  lock_expires_at       timestamptz null

panel_sync_conflicts
  id                    uuid primary key
  dedupe_key            text not null unique
  type                  text not null
  email_normalized      text null
  telegram_id           bigint null
  panel_client_id       bigint null
  other_panel_client_id bigint null
  app_user_id           uuid null references app_users(id) on delete set null
  details               jsonb null
  status                text not null                   -- open | resolved | dismissed
  first_seen_at         timestamptz not null
  last_seen_at          timestamptz not null
  notified_at           timestamptz null
  resolved_at           timestamptz null
  resolved_by           text null

  index on (status, last_seen_at)
```

`legacy_comment` exists only so the migration can be audited. After cutover, no application logic reads or writes it as
state. The panel rows are keyed by `panel_client_id`; the normalized email is also unique in the mirror. A mirror upsert
is transactional: upsert `panel_clients`, ensure its `app_users` row exists, replace `panel_client_inbounds`, then commit.

### 4.3 Sync modes

The 3X-UI paged endpoint is the primary read path:

```text
GET /panel/api/clients/list/paged?page=1&pageSize=200&sort=...&order=...
```

Confirmed response details:

- `pageSize` is capped at 200.
- Rows include `email`, `subId`, `enable`, `totalGB`, `expiryTime`, `limitIp`, `reset`, `inboundIds`, `traffic`,
  `createdAt`, and `updatedAt`.
- Rows omit `uuid`, `password`, `auth`, `flow`, `security`, `reverse`, and `tgId`. They also do not carry `comment`,
  `limitHwid`, or the full reset configuration, so a changed row needs a full-detail fetch.
- Documented sort keys are `enable`, `email`, `inboundIds`, `traffic`, `remaining`, and `expiryTime`. `updatedAt` is
  present in the response but is **not** documented as a sort key.

Two sync modes:

1. **Cursor mode (preferred):** if the panel accepts `sort=updatedAt&order=asc`, page forward from the stored cursor with
   a small overlap (for example 5 minutes) to tolerate equal timestamps and clock skew. Stop when the page is beyond the
   cursor. Update the cursor only after a successful transaction.
2. **Full-scan fallback:** if `updatedAt` sorting is rejected, page all clients using a documented sort (for example
   `email`), compare each row's `updatedAt` with the cursor, and fetch full details only for changed rows. This reduces
   detail calls but not list calls. A full scan must complete successfully before the cursor advances.

For every changed row, call `GET /panel/api/clients/get/{email}` and map the full client into the mirror. The paged row is
a change detector, not the complete record. Full-detail fetches are also used for initial backfill and manual refresh.

**Deletions:** an incremental scan cannot see a client that disappeared. Run a full reconciliation daily (and once at
startup when the mirror is empty or very stale) and mark panel client IDs/emails that are no longer present as deleted.
If the client has an `app_user` or active session, record a `panel_client_missing` conflict and notify admins.

**Run control:**

- A background hosted service runs every 10 minutes (configurable).
- Acquire a Postgres advisory lock or a `panel_sync_state` lease before running. A second run skips and records why.
- Do not advance the cursor or mark success on a partial/failed page.
- Distinguish definitive not-found from panel unavailable. Only not-found is a deletion signal.
- Record mode, counts, duration, and last error for the admin status screen.
- Alert admins after repeated consecutive failures; do not alert on a single transient timeout.

**Open verification:** confirm whether the deployed panel accepts `sort=updatedAt`. The adapter should detect a rejected
sort and fall back, with a metric/log so the mode is visible. Do not assume the documented list is exhaustive without a
test against the target panel.

### 4.4 Dashboard reads and manual refresh

- `GET /api/subscription` reads `panel_clients` + `panel_client_inbounds`; it returns `last_synced_at` and a `stale` flag.
- Stale threshold is configurable; a reasonable starting point is 15 minutes (one sync interval plus slack).
- `POST /api/subscription/refresh`:
  1. Rate-limit per user and globally.
  2. Fetch `GET /panel/api/clients/get/{email}`.
  3. Upsert the mirror and inbounds.
  4. Return the updated row; if the panel is unavailable, return the last-known DB row with a warning instead of logging
     the user out.
- Admin "run sync now" uses the same sync service, not a separate implementation.
- A login request may perform a scoped lookup when the mirror is stale or the email is missing; see §1.3.

### 4.5 Conflict handling

A conflict is any state that cannot be applied without choosing between two identities. Examples:

- two panel clients resolve to the same normalized email;
- two panel clients share a Telegram ID;
- a panel email/Telegram ID changes to a value already owned by another `app_user`;
- a panel client disappears while an app user/session exists;
- an app user points at a panel client that no longer exists;
- `TgId = 0` and a linked Telegram ID both claim the same email;
- panel client ID changed after delete/recreate and the email matches a different app user.

Handling:

1. Check unique constraints and identity mappings before applying an upsert.
2. If the change is ambiguous, keep the last-known-good identity, store a conflict row, and mark the mirror as needing
   attention. Do not merge, delete, or relink automatically.
3. Send one Telegram alert per new/changed conflict to all configured `Telegram:AdminIds`, deduplicated by `dedupe_key`.
   Include the conflict ID/type, email, panel IDs, and a pointer to `/admin` → `Panel Management` → `Conflicts`. Never
   include tokens or subscription URLs.
4. The admin conflict list shows open/resolved/dismissed conflicts. Resolve/dismiss records the decision and who made it;
   the actual fix is still a deliberate panel/DB change (no hidden merge button in v1).
5. Re-run the scoped refresh after a fix; if the collision is gone, mark the conflict resolved.
6. If the same `dedupe_key` reappears later, reopen the conflict and re-alert rather than creating a duplicate row.
7. Keep conflict history for audit.

`dedupe_key` examples: `email:{normalized}`, `tg:{id}`, `panel:{panelClientId}:missing`, `app:{appUserId}:orphan`.

### 4.6 Removing panel comments

Comment tags currently in use:

| Comment tag | Postgres owner |
|---|---|
| `Referred by: <email>` / `Referred by (pending): <email>` | `referral_attributions` |
| `Referrer tgId: <id>` | `referral_attributions.referrer_telegram_id` |
| `Referral bonus paid` | `referral_attributions.paid_at` / status |
| `Referral credit: Nd` | `credit_entries` (`migration` reason) |
| `Trial until <date>` | `trial_periods` |
| `Cancelled <date>` | `cancellation_state` |
| `Reminder Nd sent for <date>` | `reminder_deliveries` |

Proposed app-state tables (exact columns may be refined with the credits design):

```text
referral_attributions
  id                    uuid primary key
  referrer_user_id      uuid null references app_users(id)
  referee_user_id       uuid null references app_users(id)
  referrer_email        text null
  referrer_telegram_id  bigint null
  status                text not null          -- pending | paid | void
  source                text not null          -- telegram | admin | migration
  created_at            timestamptz not null
  paid_at               timestamptz null
  idempotency_key       text not null unique

trial_periods
  id                    uuid primary key
  user_id               uuid not null references app_users(id) on delete cascade
  starts_at             timestamptz not null
  ends_at               timestamptz not null
  source                text not null
  created_at            timestamptz not null

cancellation_state
  user_id               uuid primary key references app_users(id) on delete cascade
  cancelled_at          timestamptz not null
  expires_on            date null
  cleared_at            timestamptz null

reminder_deliveries
  id                    uuid primary key
  user_id               uuid not null references app_users(id) on delete cascade
  expiry_date           date not null
  days_before           int not null
  sent_at               timestamptz not null

  unique index on (user_id, expiry_date, days_before)
```

`credit_entries` / `credit_applications` are defined in §3. The tables can be created in R3a because comment backfill
needs a destination; spending, wallet, and payment behavior stay deferred to R7.

Migration sequence:

1. **R3a:** create the app-state tables and the panel mirror.
2. **R3b backfill:** read every panel client (full detail), parse tags with the existing pure parsers, and insert
   idempotent rows. Record a reconciliation report with counts and parse failures.
3. **Dual-write:** new referral/trial/cancel/reminder/credit changes write Postgres and the legacy comment.
4. **Read switch:** switch all reads to Postgres. Run a reconciliation job comparing DB state to parsed comments and
   investigate every difference.
5. **Stop writes:** stop adding/clearing comment tags. Keep `legacy_comment` as a snapshot for audit, not state.
6. **Delete parsers:** remove comment-state parsing from production paths in R5 after the read switch is trusted.

Do not skip the reconciliation step. A missed `Referral credit` or `Trial until` tag changes entitlement behavior.

### 4.7 Admin Panel Management menu (v1)

Add `/admin` → `🖥 Panel Management` with exactly these v1 actions:

- **📊 Sync Status:** last success, mode (cursor/full), seen/changed counts, last error, and **Run sync now**.
- **⚠️ Conflicts:** open conflicts with email/Telegram ID/panel IDs and **Resolve** / **Dismiss** actions. No automatic
  merge.
- **🌐 Default Inbounds:** show the current default inbound IDs, list panel inbound options, and let the admin toggle the
  set used for new clients. Persist to `panel_inbounds.is_default_for_new_clients` and audit the change.

Rules:

- Seed the default set from `Telegram__DefaultInboundIds` on first startup. Config remains the fallback until the DB
  setting exists.
- Reject an empty default set; a new client must always get at least one inbound.
- Startup preflight validates the effective set against the panel inbound options.
- Only configured admin Telegram IDs can use the menu; every change is audited.
- This menu reads the mirror for status and only calls the panel for **Run sync now** and inbound options.
- **Out of scope for v1:** client create/edit/delete, bulk enable/disable, traffic reset, device-limit edits, and client
  search. These remain backlog until the mirror and conflict flow are trusted.

### 4.8 EF Core conventions and testing

- One focused `AppDbContext` in Infrastructure. Split only if it becomes unwieldy; do not create a context per table.
- Use `IEntityTypeConfiguration<T>` per aggregate; keep mapping out of `OnModelCreating` beyond `ApplyConfigurationsFromAssembly`.
- Use snake_case column names consistently (explicit `HasColumnName` or one naming-convention package, not both).
- Map `DateTimeOffset`/UTC `DateTime` to `timestamptz`; map JSON payloads and audit metadata to `jsonb`; store enums as
  readable strings unless a PostgreSQL enum gives a clear benefit.
- Use `UseXminAsConcurrencyToken()` on mutable aggregates. Handle `DbUpdateConcurrencyException` explicitly: panel fields
  are retried from the panel; app-owned fields are preserved and reported as a conflict.
- Use partial unique indexes for nullable identity keys (`telegram_id is not null`, active sessions, open conflicts,
  pending outbox rows) and check constraints where they express a real invariant.
- Migrations live in Infrastructure. A design-time factory supports `dotnet ef migrations add`. Startup applies migrations
  under a Postgres advisory lock; fail startup if migration fails. Never use `EnsureCreated` for production.
- Background services use `IDbContextFactory<AppDbContext>` and a fresh scope per run/message. Web requests use a scoped
  context. Do not capture a context in a singleton.
- Outbox claiming uses parameterized raw SQL with `FOR UPDATE SKIP LOCKED`; keep the transaction short and send outside it.
- Reads use `AsNoTracking`, projections, and pagination. Add compiled queries only for measured hot paths. Avoid N+1 and
  unbounded `Include` graphs; use `AsSplitQuery` where it actually helps.
- Unique constraints are the last line of defense; do application-level conflict checks for useful messages.
- Indices to add: `app_users(email_normalized)` unique, partial unique `app_users(telegram_id)`,
  `panel_clients(email_normalized)` unique, partial unique `panel_clients(telegram_id)`, `panel_clients(panel_updated_at)`,
  `email_outbox(status, next_attempt_at)`, `panel_sync_conflicts(status, last_seen_at)`, `audit_events(occurred_at)`,
  `audit_events(actor_user_id, occurred_at)`, and the reminder/session/code indexes above.
- Test with **Testcontainers PostgreSQL**, not EF InMemory or SQLite. The schema depends on partial indexes, `jsonb`,
  `xmin`, and `FOR UPDATE SKIP LOCKED`; the other providers hide those behaviors.
- Run migrations once per test collection, reset data between tests with Respawn, and keep domain/unit tests provider-free.
- Add migration smoke tests against an empty database and concurrency/outbox tests that prove single-claim behavior.

### 4.9 Limitations and open questions

- `sort=updatedAt` is unverified. The fallback full scan is correct but costs one list read per 200 clients every 10
  minutes. Measure the real client count before choosing page size/interval.
- Paged rows omit `tgId` and full settings, so changed rows require `/clients/get/{email}`. Do not build dashboard state
  from paged rows alone.
- Incremental scans cannot detect deletions; daily full reconciliation is required.
- The local client currently calls `/admin/panel/api/...` while the public API docs show `/panel/api/...`. Confirm how the
  configured base URI maps and keep path construction inside the adapter.
- Direct panel edits can race with app writes. The panel wins for panel fields; identity changes can still produce a
  conflict that needs an admin.
- If 3X-UI exposes a reliable change feed/webhook, replace polling later. Do not assume one exists.
- Admin v1 deliberately excludes client CRUD. Revisit after the mirror, conflict flow, and audit are trusted.

---

## 5. Telegram / bot backlog

**Status:** Mixed (see each item)

| Item | Status | Notes |
|---|---|---|
| Self-service email change in the bot | Planned | Verify the new address; admin override with audit. |
| Email verification during bot registration | Decided: not required | Accepted risk documented in §1.7. Revisit if abuse appears. |
| Telegram `Panel Management` menu | Planned | v1: default inbounds, sync status, conflicts. No client CRUD. |
| Durable bot sessions | Deferred | `SessionStore` is in-memory and resets on deploy. A Postgres JSONB session table would fix it but is not required for the web app. |
| Tribute cancel video / exact cancel taps | Operational | Support-owned video; bot only points to support. Confirm the current Tribute flow before publishing. |
| Referral loop monitoring | Deferred | A↔B loops are allowed in v1 because each leg needs a real paid activation; revisit if abuse appears. |
| Reminder copy tone | Needs decision | Align with the existing status/menu voice. |
| Web admin console | Deferred | Admin remains in Telegram. Revisit only if support load justifies it. |

---

## 6. Operational and verification backlog

**Status:** Operational

These are unfinished verification tasks from the removed `TESTING_PLAN.md`. They are not feature work. Keep them
here until they are closed.

### 6.1 Referral/manual checks

- [ ] Deep-link referral: a stale `/skip` after the email step is rejected; attribution is kept.
- [ ] Bonus payout: referrer expiry moves by exactly +30 days; comment tags and DM are correct.
- [ ] Renewal does not pay a second bonus; credit is re-applied and visible.
- [ ] Expired resubscribe uses Tribute expiry + grace + credit, not a rebase onto today.
- [ ] Manual `/admin` grant pays the referrer and reports it.
- [ ] Dashboard-direct extension + `🛠 Settle Referrals` pays the referrer; a genuine trial is left alone.
- [ ] Unclaimed pre-registration (`TgId = 0`) pays the referrer after an admin grant.
- [ ] Consumed bonus is not paid again on a later resubscribe.
- [ ] Cancel webhook tags the account; manual reminder works; 3/2/1/0-day reminders fire once each; resubscribe
      clears the markers and keeps credit.
- [ ] Late `/referrer` correction before payment works; refusal after paid access works.
- [ ] Free trial starts immediately, clears on payment, and expires cleanly.
- [ ] Friend invite payout after claim; legacy claim retires the old `tg{id}` row as a tombstone.
- [ ] Provisioned legacy account goes straight to the menu (no migration prompt).

### 6.2 Gaming/security checks

- [ ] Own invite link is ignored; own email as referrer is rejected.
- [ ] Active user cannot use `/referrer` to gift a friend; provisioned migration skips the referrer step.
- [ ] Cancel → resubscribe cannot farm bonuses.
- [ ] Stranger email is stored as pending and pays nothing until that person registers and pays.
- [ ] Guessed `ref_<id>` links cannot mint a payout without a real payment.
- [ ] Non-admin cannot use `/admin`.
- [ ] Reminder spam is marker-gated.
- [ ] Double `/start` creates one panel row.
- [ ] Someone else's email cannot be registered without verification (currently a known gap; see §1.7).
- [ ] Trial farming with new emails is blocked by one account per Telegram ID.
- [ ] Bonus chains do not pay up the graph.
- [ ] Paid account claiming a friend invite voids attribution.

Some of these are already unit-covered; the list remains for live/staging verification. Unit coverage is recorded in
the test suite and the git history of the old testing plan.

### 6.3 Production checks

- [ ] Coolify has `Telegram__DefaultInboundIds=2,6` and the startup preflight logs a pass.
- [ ] PostgreSQL backups are enabled and a restore has been tested.
- [ ] Email domain has SPF, DKIM, and DMARC; a test code reaches Gmail, Outlook, and iCloud.
- [ ] `/health` reports database readiness; `/alive` stays process-only.
- [ ] SPA fallback does not swallow `/api`, `/webhooks`, `/health`, `/alive`, `/openapi`, or `/swagger`.
- [ ] Cookies are `Secure`, `HttpOnly`, and correctly scoped in production.
- [ ] Rate limits are active on code requests and verification.
- [ ] Audit retention and IP/user-agent storage are documented and approved.
- [ ] Support inbox is confirmed human-operated; the canned-reply/cancel-guide runbook is current.

### 6.4 Panel sync and comment migration checks

- [ ] Deployed panel accepts or rejects `sort=updatedAt`; the sync logs which mode it uses.
- [ ] A full scan completes under the configured timeout and advances the cursor only on success.
- [ ] Two concurrent sync triggers do not overlap; the loser records a skipped run.
- [ ] A panel client edited directly in 3X-UI appears in Postgres within one sync interval with the correct fields.
- [ ] A panel client deleted directly in 3X-UI is detected by full reconciliation and does not leave a login-able ghost.
- [ ] A duplicate email/Telegram ID creates one conflict, sends one admin alert, and does not merge either account.
- [ ] `/admin` → `Panel Management` shows sync status, conflicts, and default inbounds; changes are audited.
- [ ] Default inbounds are seeded from `Telegram__DefaultInboundIds`; the config fallback works when the DB setting is absent.
- [ ] Changing default inbounds affects only newly created clients, not existing clients.
- [ ] Manual refresh rate limit works and a panel outage leaves the last-known dashboard state usable.
- [ ] Comment backfill reconciliation report has zero unexplained differences before comment writes stop.
- [ ] A second backfill run produces no duplicate referral, trial, cancellation, reminder, or credit rows.
- [ ] Resend accepts a login code and a test message reaches Gmail, Outlook, and iCloud.
- [ ] Outbox retry/dead-letter behavior is verified with the provider stub forced to fail.

---

## 7. Explicit non-goals

These are deliberately out of scope for the web app's first release:

- Password-based login, social login, or OAuth.
- Web self-signup (until payment and anti-abuse are designed).
- Web admin/console.
- Native mobile apps.
- Multi-language UI.
- Automatic merging of duplicate/conflicting identities.
- Real-time panel push/webhooks (polling is the v1 mechanism unless 3X-UI provides a reliable feed).
- Panel client create/edit/delete/bulk/traffic-reset UI in Telegram v1.
- Real-time analytics.
- Credits wallet implementation (design only for now; backfill tables are the exception in §4.6).

---

## 8. Decision log

| Date | Decision |
|---|---|
| 2026-10-25 | Web app is email-first and must work without Telegram, including `TgId = 0` accounts. |
| 2026-10-25 | Frontend is Vite + React + TypeScript in `wwwroot`, served by the API. |
| 2026-10-25 | Postgres owns app auth/identity/audit; the panel remains authoritative for subscriptions. |
| 2026-10-25 | EF Core migrations run at API startup under a Postgres advisory lock. |
| 2026-10-25 | Credits ledger is designed now and implemented later. |
| 2026-10-25 | Refactor to clean architecture (Domain/Application/Infrastructure/Api) with dependencies pointing inward; keep `EagleTunnelApi` as the Api project and migrate in strangler phases. |
| 2026-10-25 | No MediatR/Mediator in the first refactor; use explicit use-case handlers, then revisit if cross-cutting pipeline behavior justifies a dispatcher. |
| 2026-10-25 | `REFERRAL_PLAN.md` and `TESTING_PLAN.md` are consolidated here and removed to avoid duplication. |
| 2026-10-26 | Authority split: the panel enforces subscription fields; Postgres owns web identity, auth, audit, referral, credits, trial/cancellation, reminders, and settings. |
| 2026-10-26 | Postgres maintains a panel client/inbound mirror, reconciled every 10 minutes plus manual refresh; dashboard reads use the mirror. |
| 2026-10-26 | Panel comments are migrated with backfill → dual-write → read switch → stop writes; no big-bang comment removal. |
| 2026-10-26 | Duplicate/conflicting email or Telegram IDs are persisted, reported to admins, and never auto-merged. |
| 2026-10-26 | Email provider is Resend; login codes use a required transactional outbox with protected payloads. |
| 2026-10-26 | Telegram admin panel-management v1 is limited to default inbounds, sync status, and conflicts. |
| 2026-10-26 | Telegram registration does not require email verification; login and email changes do. Accepted risk. |
| 2026-10-26 | EF Core: focused `AppDbContext` in Infrastructure, configurations, startup migrations, `xmin` concurrency, partial indexes, outbox `SKIP LOCKED`, Testcontainers PostgreSQL. |
| 2026-10-26 | Delivery order: architecture (R0–R2) → EF Core/schema (R3a) → comment backfill + panel sync/conflicts (R3b) → email/auth (R3c) → web app (R6) → credits (R7), with bot extraction in parallel. |
