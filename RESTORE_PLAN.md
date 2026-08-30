# Plan: Restore Normal/Old Subscription Flow

## Goal
Restore the subscription flow that existed before commit `40c09bd` (Tribute Shop migration).
Do NOT restore the shop-based purchasing flow (TributeShopClient, ShopOrderModels, TributeShopEventsHandler, etc.).
Keep the admin panel (added in `de7a92b`).

## Changes Required

### 1. Configuration Files

#### `EagleTunnelApi/Configuration/TelegramOptions.cs`
- Add back `TributeSubscriptionUrl` property

#### `EagleTunnelApi/Configuration/OptionsValidators.cs`
- Add back `TributeSubscriptionUrl` validation in `TelegramOptionsValidator`
- Revert `TributeOptionsValidator` to original (just check `ApiKey`)

#### `EagleTunnelApi/Configuration/TributeOptions.cs`
- Revert to original: only `ApiKey` property (remove `BaseUri`, `ShopId`, `SuccessUrl`, `FailUrl`)

### 2. MenuService.cs

- Restore `MainMenu(SubscriptionStatus? status, string? subscriptionUrl, string tributeSubscriptionUrl)` signature
- Restore Subscribe button as `WithUrl("💳 Subscribe", tributeSubscriptionUrl)` when inactive
- Restore "Manage Subscription" as `WithUrl("⚙️ Manage Subscription", tributeSubscriptionUrl)` when active
- Restore `Setup` callback constant (was renamed to `Connect` in `40c09bd`)
- Restore `PreSupport` callback constant (was renamed to `Support` in `40c09bd`)
- Restore `SetupMenu()` method
- Restore `PreSupportMenu()` method
- Restore `SupportMenu(string supportUrl)` (old signature, not `SupportMenu(string supportUrl, string prefillText)`)
- Keep `Connect`, `Back`, `Admin`, admin menu methods (admin panel from `de7a92b`)
- Remove `SubscriptionMenu()`, `PaymentMenu()` (shop-based, already removed, don't restore)

### 3. TelegramHandlers.cs

- Remove `ITributeShopClient _tributeShopClient` field
- Remove `TributeOptions _tributeOptions` field
- Remove `TributeOptions` and `ITributeShopClient` from constructor
- Remove `using EagleTunnelApi.TributeShop`
- Restore `HandleCallback` with `MenuService.Setup`, `MenuService.SetupInstall`, `MenuService.SetupImport`, `MenuService.SetupConnect`, `MenuService.PreSupport`, `MenuService.PreSupportYes`, `MenuService.PreSupportNo` cases
- Restore `EditMessageReplyMarkup` for `Back` callback (not `EditMessageText`)
- Restore `ShowStart` to pass `_telegramOptions.TributeSubscriptionUrl` to `MenuService.MainMenu()`
- Restore `EditMainMenu` to use `EditMessageReplyMarkup` with `MainMenu(..., tributeSubscriptionUrl)`
- Remove `HandlePlanPurchase()` method (already removed)
- Keep admin panel handlers
- Keep `IsAdmin()` method
- Keep `PanelClientDefaults.CreateClient()` usage (improvement from `40c09bd`)

### 4. Program.cs

- Remove `TributeOptions` options binding if `TributeOptions` is reverted (but `Verifier.cs` still uses `IOptions<TributeOptions>` so keep it)
- The webhook routing is already correct (no shop webhook handlers)
- No DI changes needed (shop DI was already removed)

### 5. Tests

#### `EagleTunnelApi.Tests/Telegram/TelegramHandlersTests.cs`
- Restore `TributeSubscriptionUrl = "https://tribute.test"` in `CreateHandler`
- Remove `shopResponder` parameter from `CreateHandler`
- Remove `adminIds` parameter from `CreateHandler`
- Remove `TributeShop` using
- Remove `ClientByEmailJson` helper
- Restore `CreateHandler` to simple form (pre-`40c09bd` style)
- Restore test assertions: `setup` instead of `Support`, etc.
- Update test for `Start_ExistingActiveUser_RendersStatusAndMainMenu` to assert `setup` instead of `Support`
- Update test for `Start_ExistingDisabledUser_ShowsSubscribeWithoutSetup` to assert no `setup`
- Update `Back_RestoresMainMenuText_NotJustKeyboard` to use `Setup` instead of `Connect`
- Update `Back_FromAnySubMenu_RestoresMainMenuText` theory data
- Restore admin tests to use separate `CreateHandler` with admin IDs or remove them (they were added in `de7a92b`)

Wait - admin tests are important and should be kept. But the `CreateHandler` was refactored to support admin. I need to either:
a) Keep separate `CreateHandler` overloads for admin tests, or
b) Add back admin parameters to `CreateHandler` while restoring the old subscription params

Actually, looking more carefully at the current test, `CreateHandler` has `adminIds` and the admin panel code is already in `TelegramHandlers.cs`. So I need to keep the admin support in tests while restoring the old subscription flow.

The cleanest approach: keep `CreateHandler` with `adminIds` parameter but remove `shopResponder`, and restore `TributeSubscriptionUrl`.

#### `EagleTunnelApi.Tests/Telegram/SubscriptionStatusTests.cs`
- Change `using EagleTunnelApi.PanelApi;` to `using EagleTunnelApi.PanelApi.Models;`

### 6. Files NOT to change
- `EagleTunnelApi/Telegram/SubscriptionProvisioner.cs` - keep as-is (improved version)
- `EagleTunnelApi/Telegram/PanelClientDefaults.cs` - keep as-is
- `EagleTunnelApi/PanelApi/` - keep as-is
- `EagleTunnelApi/Telegram/AdminPanelService.cs` - keep as-is
- `EagleTunnelApi/Telegram/SessionStore.cs` - keep as-is (has `AdminAction`, `AdminTargetEmail`)
- `EagleTunnelApi/Webhook/Handlers/TributeEventsHandler.cs` - keep as-is
- `EagleTunnelApi/Webhook/Events/WebhookEvent.cs` - keep as-is
- All test files except `TelegramHandlersTests.cs` and `SubscriptionStatusTests.cs`
- `EagleTunnelApi/Telegram/UserDetails.cs` - keep as-is
