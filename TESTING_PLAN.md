# Referral System — Phone Testing Plan

How to verify the whole feature from Telegram on your phone, plus the gaming/vulnerability checks.
Unit suite is green (133 tests); this plan covers what only a live run can prove.

## 0. Prerequisites (do once)

- [ ] Deploy this branch somewhere you can reach (staging ideal; prod works if you're careful).
- [ ] Set the new env vars, or the app **won't start** (fail-fast validation):
  - `Telegram__BotUsername=YourBotName` (no `@`)
  - `Telegram__ReferralBonusDays=30`
  - `Telegram__ReminderHourUtc=9`
- [ ] You need **two Telegram accounts** (your phone + a second account — e.g. desktop client, work phone, or a willing friend). Call them **A (referrer)** and **B (referee)** below.
- [ ] Have the 3x-ui panel UI open in a browser (to inspect account `comment` fields directly).
- [ ] Note: sending `/start` with a legacy `tg{id}` account triggers the migration prompt — that's expected (Track C).

---

## Track A — Fresh registration (account B, never seen before)

1. [ ] B sends `/start` → bot asks for **email address** (no account created yet — verify in panel: no new row).
2. [ ] B replies `not-an-email` → bot rejects, asks again.
3. [ ] B replies `b@example.com` → bot asks for referrer email **with a Skip button**.
4. [ ] B taps **Skip** → account created (`b@example.com`, disabled), main menu renders.
5. [ ] Panel check: comment contains `Telegram ID:` and **no** `Referred by`.

## Track B — Referral link end-to-end (accounts A + B)

1. [ ] A (registered, migrated) opens `🎁 Invite Friends — Get 1 Month Free` → screen shows how-it-works + `t.me/<bot>?start=ref_<A-id>` + copy button.
2. [ ] B (fresh) opens A's link → `/start` → email prompt.
3. [ ] B enters `b@example.com` → bot shows `You were referred by <A-email>. Please confirm:` with Confirm / Edit / Skip.
4. [ ] B taps **Edit** → bot asks to type the email manually.
5. [ ] B types A's email → account created. Panel check on B: comment has `Referrer tgId: <A-id>` **and** `Referred by: <A-email>`.
6. [ ] Redo with a third account C to test **Confirm** path (one tap, same tags expected).

## Track C — Migration (your own legacy account)

1. [ ] Send `/start` → migration prompt asking for email (NOT instant menu).
2. [ ] Enter your email → if your account ever had paid access, migration completes **with no referrer step**; panel email is now your address.
3. [ ] Panel check: old `tg{id}` row either renamed in place or replaced (old row disabled).

## Track D — Bonus payout (costs one real Tribute payment)

1. [ ] Note A's panel `expiryTime` (via `/admin` lookup or panel UI).
2. [ ] B (referred, never paid) buys the smallest subscription via Tribute.
3. [ ] Expect within a minute: A's expiry **+30 days exactly**; A's comment gains `Referral credit: 30d`; B's comment gains `Referral bonus paid`; A gets a DM (`+30 days`, cancel tip, support pointer).
4. [ ] B renews next cycle → **no second bonus** (A's expiry advances only by the paid period; credit persists and is re-applied, visible in comment).

## Track E — Cancel + reminders

1. [ ] A cancels Tribute → next `cancelled_subscription` tags A's comment with `Cancelled <date>` (check via `/admin` lookup — it now shows referral state, credit, and cancel flag).
2. [ ] **Fast check (no waiting):** as admin, `/admin` → `📨 Send Reminder` → enter A's email → confirm → A gets the reminder DM immediately, and the comment gains a `Reminder Nd sent for <date>` marker.
3. [ ] Reminder DMs arrive 3 / 2 / 1 / 0 days before A's expiry, each with the resubscribe link (the daily job; the nudge in step 2 proves the content + delivery path end to end).
4. [ ] A resubscribes → `Cancelled` tag and reminder markers clear on next activation.

## Track F — `/referrer` corrections

1. [ ] Fresh account D registers (skip referrer), **before paying** sends `/referrer`, enters A's email → panel comment updated with attribution.
2. [ ] D pays → A gets the bonus (proves late correction works pre-payment).
3. [ ] After D's payment, D sends `/referrer` again → still editable only if never provisioned… D is now provisioned → bot refuses. (Use account E to test the refusal without paying: E must first get any paid access — or trust the unit test.)

---

## Gaming / vulnerability checks

| # | Attack | Expected | How to check on phone |
|---|--------|----------|----------------------|
| 1 | B opens **own** invite link | Link ignored, manual referrer prompt | `/start ref_<own-id>` → no pre-filled confirm screen |
| 2 | B types **own email** as referrer | Rejected (`can't refer yourself`), wizard stays open | Type it at the referrer step |
| 3 | Active user A sends `/referrer` to gift friend B free VPN | Refused (`new subscribers only`) | `/referrer` on any paid account |
| 4 | Active user migrates and names a referrer | Referrer step skipped entirely for provisioned accounts | Track C step 2 |
| 5 | Cancel → resubscribe to farm bonuses | No payout without a *never-provisioned* referee carrying an unpaid tag; each payout costs a real payment | Unit-covered; live re-test optional (costs 2 payments) |
| 6 | Enter a stranger's email as referrer | Saved as `Referred by (pending)`; **no bonus** until that email registers AND the referee pays | Track B with an unregistered address, check panel tag, confirm no DM/payout |
| 7 | Guess `ref_<random-id>` links | Attribution without payment is worthless; payout still requires the referee to pay | Try `ref_1` — worst case a pending tag |
| 8 | Webhook spoofing (fake Tribute events) | Rejected — HMAC-SHA256 `trbt-signature` verified before any processing | Code-level (existing `Verifier`, unit-tested) |
| 9 | Non-admin uses `/admin` | Denied | Send `/admin` from B |
| 10 | Reminder spam | One DM per threshold per expiry cycle (markers in comment); only cancelled + migrated accounts | Inspect comment markers after Track E |
| 11 | Double `/start` race | Second create collides → re-fetch → single account (existing idempotency) | Double-tap `/start` quickly, confirm one panel row |
| 12 | Register with **someone else's email** | ⚠️ **Known gap — no email verification.** Possible: account mislabeled, true owner later blocked by "already registered". Impact is confusion-level (no money moves without Tribute payment), but flag any occurrence. Fix options if it matters: emailed code verification, or restrict to "one email per tgId, first come with support override". | Try registering account C with A's email → observe it succeeds; decide if you want verification added |

## Sign-off checklist

- [ ] A–C pass on phone, panel comments look right
- [ ] D payout is exactly +30d, once, with DM
- [ ] E tagging works (full DM wait optional)
- [ ] F corrections + refusals behave
- [ ] Gaming #1–4, #6, #9 verified live; #5, #8, #10–11 accepted via unit tests + code review
- [ ] Decision recorded on #12 (email verification: yes / later / never)
