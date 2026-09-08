# Referral System — Full Plan (approved, build when ready)

## 0. Goal
Email-based referral system for the EagleTunnel Telegram bot + 3X-UI panel + Tribute payments:
- Registration asks for the user's **email** (replaces name-based auto-register).
- A referrers step asks for the **referrer's email**.
- Every user gets a **referral link** (`t.me/<bot>?start=ref_<telegramId>`); the bot resolves the ID to the referrer's email and displays it for confirmation, otherwise asks. Manual referrer entry is always by **email**.
- Referrer earns **1 free month (+30 days panel time)** on the referee's **first paid activation**, enjoyed by **cancelling Tribute** for that month.
- Targeted expiry reminders bring cancelled users back. All **interim** until a credits-based payment platform replaces Tribute.

## 1. Locked decisions
| # | Decision | Choice |
|---|----------|--------|
| 1 | Referral key | Referrer **email** for identity/storage; link carries referrer **Telegram user id**, resolved to email live |
| 2 | Registration identity | Ask user's **real email** (replaces first/last-name sanitizing) |
| 3 | Storage | **Panel `comment` only** — no new DB |
| 4 | Existing `tg{telegramId}` accounts | **Keep + migrate on next `/start`** |
| 5 | Bonus amount | **1 month = 30 days** (`ReferralBonusDays = 30`) |
| 6 | Bonus timing | **First paid activation** (`new_subscription` only, never `renewed_subscription`, never registration) |
| 7 | Bonus redemption | **Cancel-based**: referrer cancels Tribute so they aren't billed while covered (recommended, not enforced) |
| 8 | Deep-link payload | **`ref_<telegramId>`** (numeric Telegram user id with `ref_` prefix; short, no encoding, no PII in link; bare numeric accepted as fallback) |
| 9 | Menu label (UX pick) | Button `🎁 Invite Friends — Get 1 Month Free` → screen "Invite Friends, Get 1 Month Free" + how-it-works + tap-to-copy link + Back |
| 10 | Typo handling | **Confirm + editable**: YES / Edit / Skip buttons at entry + `/referrer` fix until first payment, locked after |
| 11 | Unmigrated referrer | **Pending + auto-claim**: store `Referred by (pending):`, resolve at payment/migration |
| 12 | Anti-gaming | **Block self-referral** by normalized email equality AND `tgId` equality |
| 13 | Reminders | **3 / 2 / 1 / 0 days** before expiry, **cancelled users only**, **email-migrated only** |
| 14 | Cancel video guide | **Support-sent only** (human canned reply; bot only points users to support), **TEMPORARY** until credits platform |
| 15 | Future | Credits-ready seams now; credits system later (pay → virtual balance → spend/renew) |
| 16 | New-subscribers-only | Referrer attachable **only to never-provisioned accounts** (initial registration + migration of never-activated); bonus fires only if referee never had paid access — existing users can never gift via referral |

## 2. Current state (verified in code)
- `Telegram/TelegramHandlers.cs:62-98` — `HandleMessage` matches exact `"/start"` only; `"/start <payload>"` falls through. Must parse prefix + payload.
- `TelegramHandlers.cs:100-152 ShowStart`, `236-282 RegisterNewUser` — unknown `tgId` auto-registers with fake email `tg{telegramId}`, `enable:false`, comment from `BuildPanelComment` (`215-234`: names + @username + tgId). No email prompt, no referral.
- `PanelApi/IPanelClient.cs` — `GetClientByTelegramIdAsync`, `GetClientByEmailAsync` (referrer validation), `AddClientAsync`, `UpdateClientAsync` (keyed by email in URL), `BulkDisable*`. No referral field; only free-form field is `comment` (`PanelApi/Models/PanelClient.cs`).
- `Telegram/SessionStore.cs:20-31` — no registration state; needs `RegistrationStep` + pending fields.
- `Telegram/MenuService.cs:34-59` — no referral button.
- `Configuration/TelegramOptions.cs` — no `BotUsername` / bonus / video config yet.
- `Telegram/AdminPanelService.cs:46-69 GrantAsync` — grant-by-days; reuse for +30d.
- `Webhook/Handlers/TributeEventsHandler.cs` — handles `new/renewed_subscription`; `cancelledSubscription` exists in Tribute docs but falls into `UnhandledEvent` in `Program.cs`.
- `Telegram/SubscriptionProvisioner.cs:23-29 ActivateAsync`, `101-112 UpdateClientAsync` — **sets** expiry = Tribute `expires_at` on every activation (would wipe a bonus; must become credit-aware, §5).
- `SubscriptionProvisioner.cs:46-61 CreateClient` fallback creates legacy `tg{id}` accounts — keep; they self-heal via migration.
- Support flow: `SupportMenu` deep-links out to `SupportUrl` (external human chat); bot never sees those messages.
- Tests: `EagleTunnelApi.Tests/Telegram/TelegramHandlersTests.cs` (auto-register test expects immediate create — must be rewritten to wizard).

## 3. Registration flow (new users)
1. User opens `https://t.me/<bot>?start=ref_<referrerTelegramId>` → Telegram delivers `/start ref_<id>`.
2. `HandleMessage` parses `/start` prefix, strips `ref_`, parses numeric id → `pendingReferrerTgId` (unparsable → treat as absent, ask manually; equals own `tgId` → "that's your own link", ask manually). Resolve via `GetClientByTelegramIdAsync` → display referrer's **email** for confirmation. No panel fetch needed to *build* the link (own `tgId` is known from chat); resolution happens on the referee's side.
3. `ShowStart`: `GetClientByTelegramIdAsync` → null = new user → `session.RegistrationStep = AwaitingEmail`, store `DeepLinkReferrerTgId` + resolved email, prompt for email.
4. Email step: normalize (trim + lowercase), validate (`MailAddress` + length ≤ 64); `GetClientByEmailAsync` hit → "already registered" → re-prompt/link flow.
5. Referrer step (`AwaitingReferrer`): deep-link present → "You were referred by `<resolved-email>`: ✅ Confirm / ✏️ Edit / ⏭️ Skip"; absent → "Enter referrer's email or /skip". Manual entry is always **email** (referee won't know the tgId).
6. Referrer validation: normalize; **reject self** (email == own email OR referrer `tgId` == own `tgId`, log attempt — link path makes this a trivial numeric compare). Store **both** `Referrer tgId: <id>` (stable attribution, survives referrer email changes, works pre-migration) and `Referred by: <email>` in comment. Manual-email miss → offer **pending save** vs retry vs skip.
7. `RegisterNewUser(tgId, realEmail, referrerOrPending?, from)`: `CreateClient(email: realEmail, enable:false, …)`, comment = `BuildPanelComment(…)` + ` · Referred by: <email>` (or ` · Referred by (pending): <email>`), then `BulkDisable` as today. **No grant yet.**

## 4. Migration (legacy `tg*` users)
- `ShowStart` detects `client.Email` matching `^tg\d+$` → migration wizard.
- **Never-provisioned accounts** (disabled + ~100-year placeholder expiry, i.e. never paid): run the full email → referrer wizard — they are effectively still new subscribers.
- **Ever-provisioned accounts** (enabled now, or real expiry ever set): email step only, **referrer step skipped entirely**. They can never gain a referrer later, so they can never mint a gifted bonus via cancel → resubscribe.
- Provisioned check (shared helper `HasEverBeenProvisioned(client)`): `client.Enable || client.ExpiryTime <= UtcNow.AddYears(50).ToUnixTimeMilliseconds()`.
- Email rename spike first: verify whether panel `update/{oldEmail}` accepts an email change; Plan A update-in-place, Plan B fallback `AddClient(newEmail, same tgId)` + `BulkDisable([oldEmail])`.
- On migration to `X`: scan `GetAllClientsAsync` comments for `Referred by (pending): X` → flip to confirmed (referee unpaid) or grant immediately (referee already paid). Lazy-check on referrer's next `/start` too ("🎉 N pending referrals claimed!").
- Legacy users see "Register your email first" CTA in Invite screen instead of a link.

## 5. Bonus: +30d at first paid activation, cancel to enjoy
1. Referee's first `new_subscription` → fetch referee **before** the provisioner runs, parse `Referred by:` (re-resolve `(pending)` live — referrer may have migrated since).
2. Guards (all required): referrer exists, not self, referee comment lacks `Referral bonus paid` (idempotent; retries/renewals never re-grant), **and referee `!HasEverBeenProvisioned`** (never-provisioned check — blocks gifted bonuses via cancel → resubscribe on accounts that somehow carry a tag).
3. `GrantAsync(referrerEmail, 30)` + tag referrer comment `Referral credit: 30d` (sums across referrals — this ledger maps 1:1 into future credit balances) + referee ` · Referral bonus paid`.
4. `/referrer` edits (and the wizard referrer step) are gated on the same `!HasEverBeenProvisioned` check — once provisioned, the bot refuses ("referrals only count for new subscribers — contact support") and directs to support.
3. `GrantAsync(referrerEmail, 30)` + tag referrer comment `Referral credit: 30d` (sums across referrals — this ledger maps 1:1 into future credit balances) + referee ` · Referral bonus paid`.
4. DM referrer: "+30 days active until `<date>`. **Cancel your Tribute renewal** so you aren't billed while covered — message support for a step-by-step video guide — we'll remind you 3-2-1-0 days before it ends" + resubscribe link + support button. (The bot never sends the video itself; see §7.)
5. **Renewal-safe math (required):** provisioner becomes credit-aware — every `new/renewed` computes `expiry = tributeExpiresAt + creditDays` (parsed from `Referral credit:` tag) instead of blind set, so the bonus is never absorbed by the next rebill. Keeping Tribute running without cancelling is harmless (expiry just sits ahead).
6. Cancellation is a **user action** (`Tribute__ApiKey` is HMAC-verify only; no Tribute cancel API). Open item: confirm exact Tribute cancel taps for the message/video script.

## 6. Cancellations + reminders (win-back loop)
- New `CancelledSubscription` webhook model + handler in `Program.cs`: tag `Cancelled <date>`; clear it (plus reminder markers) on any later `new/renewed`.
- New `ExpiryReminderService : BackgroundService` (daily, e.g. 09:00 UTC, all envs): `GetAllClientsAsync` → skip legacy `^tg\d+$` → skip untagged (active payers never nagged) → `daysUntilExpiry ∈ {3,2,1,0}` + marker `Reminder Nd sent <date>` absent → `SendMessage(tgId, …resubscribe: <TributeSubscriptionUrl>… + "need help cancelling? message support for the video guide" support button)` → write marker via `UpdateClientAsync`.
- First-deploy safety: log-and-acknowledge raw `cancelledSubscription` shape once (only `telegram_user_id` needed).

## 7. Cancel video guide (TEMPORARY, support-owned)
- The video is **sent by support humans, never by the bot** — no `sendVideo` code, no video config. Support keeps it as a canned/quick reply (Saved Messages) and forwards it whenever a user asks how to cancel.
- The bot's only job is to **point at support**: the bonus-earned DM, Invite screen, and reminders carry a "📹 Cancel guide? Message support" button (existing `SupportMenu` deep-link pattern) plus one-line cancel steps in text.
- Guide spec for the recording: MP4, <2 min, 720p, small file for fast forwarding.
- **Sunset:** the runbook entry retires with the Tribute off-boarding (cancel-DM copy + cancel webhook + reminder service go together when credits ship).

## 8. Code changes (build order)
1. `Configuration/TelegramOptions.cs` + `OptionsValidators.cs` + `.env.example` + `README.md`: `BotUsername` (required), `ReferralBonusDays = 30` (0 = kill-switch), `ReminderHourUtc`. (No video config — video is support-owned.)
2. `Telegram/SessionStore.cs`: `enum RegistrationStep { None, AwaitingEmail, AwaitingReferrer }` + `RegistrationStep`, `PendingEmail?`, `PendingReferrerEmail?` on `UserSession`.
3. New `Telegram/ReferralService.cs`: `NormalizeEmail/IsValidEmail`, `BuildRefPayload(tgId)` / `TryParseRefPayload` (`ref_<id>`, bare-numeric fallback), `BuildReferralLink(botUsername, tgId)`, `ResolveReferrerDisplay` (tgId → email via panel), comment tags + parsers (`Referred by:`, `Referrer tgId:`, `(pending)`, `Referral bonus paid`, `Referral credit:`, `Cancelled`, `Reminder Nd sent`).
4. `Telegram/TelegramHandlers.cs`: `/start <payload>` parse; wizard routing before admin check; `/skip`, `/referrer` (locked post-payment); `RegisterNewUser(tgId, email, referrer?, from)`; `BuildPanelComment(…, referrer?)`; bonus-earned DM + video; Invite screen wiring; migration gate + pending-claim scan.
5. `Telegram/MenuService.cs`: `Referral = "referral"`; invite row `🎁 Invite Friends — Get 1 Month Free`; `ReferralMenu(link)` (copy-link + message-support-for-cancel-guide buttons), confirm/edit/skip keyboards.
6. `Webhook/Events/WebhookEvent.cs` + `Program.cs` + `Webhook/Handlers/TributeEventsHandler.cs`: `CancelledSubscription` model/route/handler; first-payment grant path; credit-aware expiry in `SubscriptionProvisioner`.
7. New `Telegram/ExpiryReminderService.cs`: daily scan + sends + markers.
8. `EagleTunnelApi.Tests/`: rewrite auto-register test to wizard; add deep-link prefill, email/self/unknown validation, skip, migration (provisioned skips referrer; unprovisioned gets wizard), pending-claim, grant-exactly-once (renewed/retry safe), **no-grant for ever-provisioned referee (gift-block), `/referrer` refused once provisioned, resubscribe-without-tag grants nothing**, bonus-survives-renewal, cancel→3/2/1/0 once each, active/legacy silent, bonus DM points to support (no video assertions).

## 9. Edge cases
- Payload >64 chars/undecodable → ignore, ask manually. Emails always lowercased; panel lookups use normalized form.
- Concurrent double `/start` → keep existing create-then-refetch fallback.
- `/start` resets wizard state (`SessionStore.Reset` already called).
- A↔B loop referrals allowed in v1 (each leg needs a real paid activation); revisit if abused.
- Privacy accepted: email visible inside referral links.

## 10. Open items for build
1. Exact Tribute cancel taps/URL for message + video script.
2. The cancel-guide MP4 file — for the support runbook only (the bot needs nothing).
3. Confirm support inbox is human-operated (for canned-reply runbook step).
4. Referral reminder copy tone (same voice as status texts?).

## 11. Credits-platform sunset (do NOT build now)
- Seams: all bonus math behind `ReferralService`/provisioner credit application; call sites keep shape so "expiry +30d" becomes "balance +30d" later.
- Ledger tags read as credit history and migrate into a balance table.
- Reminder trigger ("service ends in N days + no active billing") is the same trigger credits need ("balance covers until X — top up?").
- Retire as one unit: Tribute cancel flow + video + cancel webhook + reminder copy.
