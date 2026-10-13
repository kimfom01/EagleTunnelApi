# Referral System — Phone Testing Plan

How to verify the whole feature from Telegram on your phone, plus the gaming/vulnerability checks.
Unit suite is green (179 tests); this plan covers what only a live run can prove.

## 0. Prerequisites (do once)

- [x] Deploy this branch somewhere you can reach (staging ideal; prod works if you're careful).
- [x] Set the new env vars, or the app **won't start** (fail-fast validation):
  - `Telegram__BotUsername=YourBotName` (no `@`)
  - `Telegram__ReferralBonusDays=30`
  - `Telegram__ReminderHourUtc=9`
- [x] You need **two Telegram accounts** (your phone + a second account — e.g. desktop client, work phone, or a willing friend). Call them **A (referrer)** and **B (referee)** below.
- [x] Have the 3x-ui panel UI open in a browser (to inspect account `comment` fields directly).
- [x] Note: sending `/start` with a legacy `tg{id}` account triggers the migration prompt — that's expected (Track C).

---

## Track A — Fresh registration (account B, never seen before)

1. [x] B sends `/start` → bot asks for **email address** (no account created yet — verify in panel: no new row).
2. [x] B replies `not-an-email` → bot rejects, asks again.
3. [x] B replies `b@example.com` → bot asks for referrer email **with a Skip button**.
4. [x] B taps **Skip** → account created (`b@example.com`, disabled), main menu renders.
5. [x] Panel check: comment contains `Telegram ID:` and **no** `Referred by`.

## Track B — Referral link end-to-end (accounts A + B)

1. [x] A (registered, migrated) opens `🎁 Invite Friends — Get 1 Month Free` → screen shows how-it-works + `t.me/<bot>?start=ref_<A-id>` + copy button.
2. [x] B (fresh) opens A's link → `/start` → email prompt.
3. [x] B enters `b@example.com` → bot shows `You were referred by <A-email>. Please confirm:` with Confirm / Edit only (no Skip — deep-link attribution can't be stripped).
4. [x] B taps **Edit** → bot asks to type the email manually (required — no Skip offered on this path).
5. [x] B types A's email → account created. Panel check on B: comment has `Referrer tgId: <A-id>` **and** `Referred by: <A-email>`.
6. [x] Redo with a third account C to test **Confirm** path (one tap, same tags expected).
7. [ ] B opens A's link, enters email, then sends `/skip` or taps a stale Skip → rejected (`Confirm or Edit`), attribution kept.

## Track C — Migration (your own legacy account)

1. [x] Never-paid legacy account: `/start` → migration prompt asking for email (+ referrer step) → trial account created.
2. [x] Already-paid legacy account: `/start` → straight to menu, no email prompt (login kept; changes via support/admin only).
3. [x] Paid legacy account opens `🎁 Invite Friends` → one-time email prompt → enter email → invite link appears; reopening the screen shows the link directly (update happens exactly once).
4. [x] Same gate for **invalid (non-legacy) emails**: any account whose login email is malformed opens `🎁 Invite Friends` (or `➕ Register a Friend`) → one-time email prompt first, no link until the email is fixed.

## Track D — Bonus payout (costs one real Tribute payment)

1. [ ] Note A's panel `expiryTime` (via `/admin` lookup or panel UI).
2. [ ] B (referred, never paid) buys the smallest subscription via Tribute.
3. [ ] Expect within a minute: A's expiry **+30 days exactly**; A's comment gains `Referral credit: 30d`; B's comment gains `Referral bonus paid`; A gets a DM (`+30 days`, cancel tip, support pointer).
4. [ ] B renews next cycle → **no second bonus** (A's expiry advances only by the paid period; credit persists and is re-applied, visible in comment).
5. [ ] **Expired resubscribe (no rebase):** let a subscription lapse (e.g. expired 7 days), then resubscribe for 30 days → new expiry is Tribute `expires_at` + 1h grace + credit, **not** rebased onto today. Verify via `/admin` lookup or panel UI.
6. [ ] **Manual-payment payout (free, no Tribute cost):** referred trial account F (never paid) → as admin, `/admin` → `➕ Grant / Extend` → grant F 30 days → A (referrer) gets **+30 days**, `Referral credit: 30d`, A gets the bonus DM; F's comment gains `Referral bonus paid` and loses the trial tag. Repeat the grant → **no second payout**.
7. [ ] **Dashboard-direct payout (slow):** extend a referred, never-paid account directly in the 3X-UI panel UI → after the next daily settlement run (`Telegram:ReminderHourUtc`), A gets the bonus as in step 6.

## Track E — Cancel + reminders

1. [ ] A cancels Tribute → next `cancelled_subscription` tags A's comment with `Cancelled <date>` (check via `/admin` lookup — it now shows referral state, credit, and cancel flag).
2. [ ] **Fast check (no waiting):** as admin, `/admin` → `📨 Send Reminder` → enter A's email → confirm → A gets the reminder DM immediately, and the comment gains a `Reminder Nd sent for <date>` marker.
3. [ ] Reminder DMs arrive 3 / 2 / 1 / 0 days before A's expiry, each with the resubscribe link (the daily job; the nudge in step 2 proves the content + delivery path end to end).
4. [ ] A resubscribes → `Cancelled` tag and reminder markers clear on next activation (verify via `/admin` lookup: no `Cancelled`/`Reminder … sent` in comment, `Referral credit` still present); expiry is Tribute `expires_at` + 1h grace + credit.

## Track F — `/referrer` corrections

1. [x] Fresh account D registers (skip referrer), **before paying** sends `/referrer`, enters A's email → panel comment updated with attribution.
2. [ ] D pays → A gets the bonus (proves late correction works pre-payment).
3. [ ] After D's payment, D sends `/referrer` again → still editable only if never provisioned… D is now provisioned → bot refuses. (Use account E to test the refusal without paying: E must first get any paid access — or trust the unit test.)

## Track G — Free trial (fresh account F)

1. [ ] F registers via `/start` → account is **enabled** immediately with expiry ~2h out; comment has `Trial until …`; menu shows Active + trial note; F can connect.
2. [ ] F pays within the trial → provisioner clears the `Trial until` tag, expiry becomes the paid date + any credit.
3. [x] Let a second trial lapse unpaid → status flips to Expired, no auto-disable needed (expiry enforces it).

## Track H — Friend pre-registration + claim (A invites G)

1. [x] A opens invite screen → `➕ Register a Friend` → enters `g@example.com` → bot confirms invite created; panel shows disabled `TgId=0` row with `Invited by <A-email>` + referrer tags and **no trial** (no `Trial until`, far-future expiry).
2. [x] G starts the bot, enters `g@example.com` → account auto-linked (panel `TgId` = G's id), trial starts now (`enabled`, expiry ~2h, `Trial until …`), referrer kept, straight to menu with no referrer re-prompt.
3. [ ] G pays → A gets the bonus.
4. [x] A tries inviting an already-registered email → refused; invites own email → refused.
5. [ ] Claim with a pre-existing legacy row: G started the bot before being invited (owns a dead `tg{id}` row) → after claiming, panel shows the linked account working and the old row retired as a disabled `tg{id}.merged-…` tombstone.

## Track I — Admin register + change email (as admin)

1. [x] `/admin` → `➕ Register Account` → new email → referrer email (or `/skip`) → unclaimed `TgId=0` row created; friend claims it per Track H.
2. [x] `/admin` → `✏️ Change Email` → current email → new email → confirm → panel email renamed; user keeps service uninterrupted. Try a taken new email → refused.
3. [ ] Provisioned legacy account: `/start` goes straight to menu (no migration prompt, no email change possible from the bot).

## Track J — Partial admin search (as admin)

1. [x] With `johnappleseed@icloud.com` + `johnny@example.com` present: `/admin` → lookup → type `john` → pick list with both → tap second → correct card.
2. [x] Type a full exact email → resolves directly, no pick list.
3. [x] Type `zzz-no-match` → `No client found`.
4. [x] Repeat via `🚫 Ban` (or grant): partial → pick → confirm screen shows the picked email → execute works.
5. [x] Tap a stale pick button after starting another action → `selection expired` message.

## Track K — How to Connect guide (active account)

1. [x] Active account taps `🚀 How to Connect` → guide shows the 5 steps: install INCY → Copy VPN Link → open INCY → Paste (Allow Paste on iPhone) → big button to connect.
2. [x] `📋 Copy VPN Link` button copies the subscription URL; Back returns to the main menu.

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
| 13 | Farm free trials with new emails, same Telegram | Blocked — one account per Telegram ID; re-registration isn't offered to existing accounts | Try `/start` again on a trial account → straight to menu, no second trial |
| 14 | Claim someone else's invite (know their email) | Claim links YOUR Telegram to THEIR email — self-harm only (you lose your own account linkage); no bonus without a real new payer | Unit-covered; live test optional |
| 15 | Trial user never pays, refers friends | Fine — bonus fires only when the REFEREE pays; trial itself grants nothing to anyone | Unit-covered (trial referee still bonus-eligible on payment) |
| 16 | Bonus chains up the graph (A←B←C, C pays) | Only B is paid; A's tag on B never triggers from a bonus grant | Unit-covered (referrer payout uses the chain-free grant core) |
| 17 | Already-paid account claims a friend invite carrying a referrer | Attribution voided on claim (new-subscribers-only); later sweep pays nothing | Unit-covered; check panel comment has no `Referred by` after claiming with a paid account |

## Sign-off checklist

- [x] A–C pass on phone, panel comments look right (incl. invalid-email invite gate)
- [ ] D payout is exactly +30d, once, with DM; expired resubscribe uses Tribute expiry + grace (no rebase); manual-grant and dashboard payouts work
- [ ] E tagging works (full DM wait optional); resubscribe clears Cancelled/markers, keeps credit
- [ ] F corrections + refusals behave
- [ ] G trial connects immediately, survives payment, lapses cleanly
- [ ] H invite (no trial at creation) → claim (trial starts) → payout chain works
- [ ] I admin register/change-email + provisioned lockdown behave
- [x] J partial search + picks work across actions
- [x] K connect guide shows the 5 INCY steps
- [ ] Gaming #1–4, #6, #9, #13 verified live; #5, #8, #10–11, #14–17 accepted via unit tests + code review
- [x] Decision recorded on #12 (email verification: later)
