# Features & Roadmap

Single source of truth for work that is **not implemented yet**. Implemented behavior is documented in
[README.md](README.md) and verified by the test suite. Historical build plans (`REFERRAL_PLAN.md` and
`TESTING_PLAN.md`) were consolidated into this document and removed to avoid duplication; git history still contains
them if the old detail is ever needed.

> **Status:** planning only. Nothing in this document has been built unless explicitly marked otherwise.
>
> **Last updated:** 2026-10-25

## Status legend

| Status | Meaning |
|---|---|
| **Design only** | Decisions and schema sketched; no code. |
| **Planned** | Agreed direction; implementation can be broken into tasks. |
| **Needs decision** | Blocked on a product, security, or provider choice. |
| **Deferred** | Deliberately out of scope until a later phase. |
| **Operational** | A runbook/process item, not application code. |

## Suggested order

1. Clean architecture skeleton (Section 2, R0–R2).
2. Web auth/identity/audit foundation (Section 1, Phase 1 / R3).
3. Web auth endpoints + read-only dashboard (R6).
4. Extract the remaining bot/admin flows (R4–R5) in parallel where safe.
5. Credits ledger (Section 3, R7).

---

## 1. Email-first web app

**Status:** Design only

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
- **Auth:** email confirmation code (not a Telegram login link, not a password).
- **Database:** PostgreSQL. Postgres owns app auth/identity/audit; the 3X-UI panel remains authoritative for
  subscription state.
- **Migrations:** EF Core migrations run at API startup under a Postgres advisory lock.
- **Credits:** design the ledger now; do not implement it in the first slice.
- **No web admin console:** admin actions stay in Telegram for now.

### 1.3 Who can log in

- Any panel client whose email matches the submitted address, including:
  - normal accounts linked to a Telegram ID;
  - unclaimed friend/admin-registered accounts with `TgId = 0`;
  - disabled, expired, or limited accounts (they still need to see status and renew).
- A panel client must exist. The web app is **not** a self-signup product in v1.
- If no panel client exists, the API returns the same `202 Accepted` response as a successful request but sends no
  email. This avoids account enumeration.
- If the panel is unreachable, return `503 Service Unavailable` rather than pretending no account exists.

### 1.4 Identity model

The panel email is the login identifier; the Telegram ID is an optional link.

Proposed `app_users`:

| Column | Notes |
|---|---|
| `id` | UUID primary key. |
| `email_normalized` | Lowercase/trimmed email, unique, not null. |
| `telegram_id` | Nullable, unique when present. Null for `TgId = 0` accounts. |
| `created_at`, `updated_at` | `timestamptz`. |
| `last_login_at` | Nullable. |

Sync rules:

1. On a login request, look up the panel client by normalized email.
2. If found:
   - if the panel row has a Telegram ID and an `app_users` row already exists for that Telegram ID, update its email
     to the panel email;
   - otherwise find by email and update/set the Telegram ID;
   - otherwise create the row.
3. If the panel row is `TgId = 0`, create/find the app user with `telegram_id = NULL`.
4. If the panel row is deleted later, the app user/session may remain, but dashboard calls return a clear
   "account not found / contact support" state.
5. If the panel email changes via the bot or admin, update the matching `app_users` row in the same operation.
6. If an app user later claims an unclaimed panel row in Telegram, link `app_users.telegram_id` by email.
7. Duplicate/conflicting email or Telegram IDs must be logged and resolved manually; do not silently merge accounts.

**Prerequisite:** the panel client must distinguish "email not found" from "panel unavailable". Today
`GetClientByEmailAsync` throws `PanelApiException` for both. Add a `TryGetClientByEmailAsync` (or equivalent) that
returns `null` only for a definitive not-found response and throws for transport/server failures.

### 1.5 Email-code login flow

**Request code** — `POST /api/auth/email/request-code`

1. Validate and normalize the email.
2. Apply rate limits (per email and per IP).
3. Look up the panel client. If no account, return `202` and do nothing.
4. Upsert `app_users`.
5. Invalidate any previous unconsumed code for that email.
6. Generate a 6-digit code. Store only `HMAC-SHA256(server_pepper, code + ":" + email_normalized)`.
7. Store an `email_login_codes` row with a 10-minute expiry and `attempts = 0`.
8. Send the email.
9. Return `202 Accepted` with a generic message.

**Verify code** — `POST /api/auth/email/verify-code`

1. Normalize the email and load the active code.
2. Reject if expired, consumed, or attempts exhausted.
3. Increment attempts on failure.
4. On success, atomically mark the code consumed, create `app_users` if needed, create a `web_sessions` row, and set
   the session cookie.
5. Audit the event.
6. Return the current user summary.

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

Decisions to make:

- Provider: Resend, Postmark, Amazon SES, Mailgun, SMTP, or self-hosted.
- Sending domain and from/reply-to addresses.
- SPF, DKIM, and DMARC setup.
- Bounce/complaint handling and monitoring.

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

- Development/tests should use `Email:Provider=log` (code appears in logs) or a local Mailpit container.
- A transactional outbox (`email_outbox`) is recommended for production so a provider outage does not lose codes.
  For v1, a direct send with retry and a clear `503` on failure may be acceptable.
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

**Email verification at registration** — known gap from the old testing plan (#12). Today someone can register a
Telegram account with another person's email. That becomes a security problem once email is a login factor: the
email owner could receive a web code and access the other person's subscription. Recommended fix: send a
verification code during bot registration (or at least before enabling web login for that account). This needs a
product decision because it adds friction to Telegram onboarding.

### 1.8 Planned web API

All routes are under `/api`, use cookie auth, JSON, and Problem Details. None are implemented.

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/auth/email/request-code` | Send a login code. |
| `POST` | `/api/auth/email/verify-code` | Verify code and create a session. |
| `POST` | `/api/auth/logout` | Revoke the current session. |
| `GET` | `/api/me` | Current app user and Telegram-link state. |
| `GET` | `/api/subscription` | Panel-backed status, expiry, traffic, devices, subscription URL. |
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
- Show a friendly degraded state if the panel is unavailable; do not log the user out.
- Never expose another user's data. All panel lookups are scoped by the authenticated app user's email/Telegram ID.
- QR generation can stay client-side to avoid a server endpoint.

Build/serving:

- Vite build output copied to `wwwroot`.
- `UseStaticFiles()` + SPA fallback to `index.html`, excluding `/api`, `/webhooks`, `/health`, `/alive`, `/openapi`,
  and `/swagger`.
- In development, run Vite separately with a proxy to the API.
- Dockerfile gains a Node build stage; `.dockerignore` excludes `node_modules`.

### 1.10 Proposed database schema

All timestamps are UTC (`timestamptz`). UUID primary keys. Exact column names may change during implementation.

```text
app_users
  id                uuid primary key
  email_normalized  text not null unique
  telegram_id       bigint null
  created_at        timestamptz not null
  updated_at        timestamptz not null
  last_login_at     timestamptz null

  unique index on (telegram_id) where telegram_id is not null

email_login_codes
  id                uuid primary key
  app_user_id       uuid not null references app_users(id) on delete cascade
  email_normalized  text not null
  code_hash         bytea not null
  created_at        timestamptz not null
  expires_at        timestamptz not null
  consumed_at       timestamptz null
  attempts          int not null default 0
  request_ip        inet null

  index on (email_normalized, expires_at)

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

email_outbox (optional, recommended)
  id                uuid primary key
  to_email          text not null
  subject           text not null
  body_html         text not null
  body_text         text not null
  created_at        timestamptz not null
  sent_at           timestamptz null
  attempts          int not null default 0
  next_attempt_at   timestamptz null
  last_error        text null
```

Audit event examples: `web.auth.code_requested`, `web.auth.code_sent`, `web.auth.code_verify_failed`,
`web.auth.login`, `web.auth.logout`, `web.account.email_change_requested`, `web.account.email_changed`,
`web.session.revoked`, `panel.lookup_failed`, `panel.account_not_found`, `admin.email_changed`,
`referral.credit_granted`.

Retention decisions to make: audit events (proposed 1 year), session rows (purge after expiry + 30 days), login
codes (purge after expiry + 1 day), IP/user-agent storage (privacy tradeoff).

### 1.11 Security checklist

- Codes hashed with a server-side pepper; never stored or logged in plaintext.
- Short code TTL, resend cooldown, max attempts, rate limits per email and IP.
- Generic responses to avoid email enumeration.
- Session tokens hashed at rest; opaque cookies; revocation supported.
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
| 0 | Finalize decisions, contract, schema, email provider | Design only |
| 1 | Postgres + EF Core, entities, migrations, token/session/email services, tests. **No HTTP endpoints.** | Planned |
| 2 | Auth endpoints + SPA shell + login page. | Planned |
| 3 | Read-only dashboard (subscription, traffic, expiry, devices, link/QR). | Planned |
| 4 | Referrals, account page, email change, support. | Planned |
| 5 | Email verification at registration, web sign-up, non-Telegram payments, credits integration. | Deferred |

Phase 1 is the first web-app code slice once approved, after the R0–R2 architecture skeleton in Section 2 is in place. It should
be independently testable without exposing any web routes.

### 1.14 Open decisions

1. Email provider, sending domain, and from/reply-to addresses.
2. Code length/TTL/resend policy and session lifetime.
3. Whether bot self-service email changes require a code to the new address; what admins can override.
4. Whether to verify emails at bot registration (strongly recommended before email login is widely used).
5. Whether to allow web self-signup (currently no) and how to handle payment/anti-abuse.
6. **Web payments:** Tribute checkout is Telegram-native. A user who cannot access Telegram can view their
   subscription but cannot renew it. Removing that dependency needs a non-Telegram payment provider; this is the
   biggest open product question.
7. Support channel for users who cannot access Telegram (email address, contact form, or admin inbox).
8. Whether to store raw IP/user-agent values and for how long.
9. Whether the web app shows referral/credit data in v1 or only the subscription dashboard.
10. OpenAPI/Postman contract ownership and CI validation.

---

## 2. Clean architecture refactor

**Status:** Planned. This is the delivery vehicle for the web app, not a separate rewrite.

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
| `IPanelGateway` | Panel reads/writes, with application models and explicit not-found vs unavailable errors. |
| `ITelegramNotifier` | Send a message/notification without exposing Telegram.Bot types to Application. |
| `IEmailSender` | Send login/verification email. |
| `IAppDbContext` | Persistence for app users, sessions, codes, audit, and later ledger. |
| `ICodeHasher`, `ITokenGenerator`, `IClock` | Testable crypto/time seams. |
| `IAuditWriter` | Append audit events. |
| `IUserDirectory` | Future read model; not needed until panel mirroring is justified. |

Rules:

- Application must not reference `Telegram.Bot`, EF Core, Npgsql, or ASP.NET Core.
- Do not expose `PanelClient`/panel DTOs to Application or Domain. Map them in the Infrastructure adapter.
- Use `IAppDbContext` for new persistence. Add repositories only for aggregate roots with real domain behavior; avoid a
  generic repository.
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
- **EF Core:** `AppDbContext`, entity configurations, and migrations in Infrastructure. Application sees `IAppDbContext`,
  not `DbContext`.

### 2.8 Strangler migration phases

Each phase is a separately verifiable, commit-sized slice. No behavior change unless the phase explicitly adds a feature.

| Phase | Scope | Exit criteria |
|---|---|---|
| R0 | Create Domain/Application/Infrastructure projects, add project references, add architecture tests. Move no production code. | Solution builds; all existing tests pass; architecture tests fail on an intentional violation. |
| R1 | Move pure Domain types and their tests (`SubscriptionStatus`, formatter, referral parsing/policies, email normalization). | Existing behavior unchanged; tests moved/updated; no panel DTOs in Domain. |
| R2 | Introduce `IPanelGateway` and application models; move `PanelApiClient` + DTOs to Infrastructure; map at the boundary. | No panel DTOs outside Infrastructure; not-found vs unavailable preserved; tests pass. |
| R3 | Add EF Core + Postgres + auth/identity/audit foundation (web app Phase 1). New use cases and ports; no HTTP endpoints. | Migrations run against a test Postgres; code/session/audit tests pass; no endpoints exposed. |
| R4 | Extract registration, claim, activation, and referral-settlement use cases; shrink `TelegramHandlers`. | Bot flows still pass existing tests; handler contains parsing/rendering only; no business rules. |
| R5 | Extract admin, reminder, trial, cancel, and device-limit use cases; split `AdminPanelService`; background jobs call use cases. | Admin/reminder behavior unchanged; `AdminPanelService` is gone or a thin facade; tests pass. |
| R6 | Add web auth endpoints + SPA shell + read-only dashboard. | Login, session, dashboard, and logout work end to end; no Telegram requirement. |
| R7 | Credits ledger (Section 3). | Ledger design implemented behind use cases; panel projection reconciled. |

Sequencing:

1. R0 → R1 → R2 are prerequisites for clean web/auth code.
2. R3 is the first web-app code slice.
3. R4/R5 can be interleaved after R2; they reduce risk before the web surface grows.
4. R6 should follow R3, with R4/R5 continuing in parallel where safe.
5. R7 comes after auth/identity is stable.

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
- **Persistence abstraction:** `IAppDbContext` first; repositories only where aggregate behavior justifies them.
- **Auth persistence models:** keep them in Infrastructure with Application DTOs unless real domain behavior emerges.

---

## 3. Credits platform and ledger

**Status:** Design only. Do not implement until the web auth foundation is stable.

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

1. Scan panel clients for `Referral credit: Nd`.
2. Insert one `migration` entry per user with an idempotency key.
3. Keep the comment tag until all readers use the ledger.
4. Dual-write new bonuses to the ledger and the comment during the transition.
5. Switch reads to the ledger, then stop writing/clearing the comment tag.
6. Record a reconciliation report and keep the migration idempotent.

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

### 3.9 Open decisions

- Day credits only first, or money + days from the start?
- How are unused credits expired or refunded?
- Multi-currency? Taxes?
- Does a referral bonus become spendable wallet balance or only subscription days?
- What is the source of truth when the panel and ledger disagree?

---

## 4. Identity, panel directory, and sync

**Status:** Deferred, except the login-request lookup.

- The 3X-UI panel remains authoritative for panel clients and subscription state.
- Do **not** mirror the full panel userbase into Postgres yet. A copy will drift and adds reconciliation work.
- Web login and dashboard use direct panel lookups by email/Telegram ID.
- If search, analytics, or admin tooling later needs a local directory, introduce an `IUserDirectory` seam backed by a
  read-through cache with `last_synced_at`. The panel stays authoritative.
- Handle panel client deletion/recreation: sessions may remain, but dashboard calls should show a clear state.
- Handle `TgId = 0` → linked account transitions by matching email.
- Do not silently merge two app users with the same email or Telegram ID.

---

## 5. Telegram / bot backlog

**Status:** Mixed (see each item)

| Item | Status | Notes |
|---|---|---|
| Self-service email change in the bot | Planned | New requirement. Verify the new address; admin override with audit. |
| Email verification during bot registration | Needs decision | Closes known gap #12 and makes email login safe. Adds onboarding friction. |
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

---

## 7. Explicit non-goals

These are deliberately out of scope for the web app's first release:

- Password-based login, social login, or OAuth.
- Web self-signup (until payment and anti-abuse are designed).
- Web admin/console.
- Native mobile apps.
- Multi-language UI.
- Full panel userbase mirroring.
- Real-time analytics.
- Credits wallet implementation (design only for now).

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
