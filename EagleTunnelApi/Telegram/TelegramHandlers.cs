using System.Text;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.TributeShop;
using EagleTunnelApi.Webhook.Exceptions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace EagleTunnelApi.Telegram;

public sealed class TelegramHandlers : IUpdateHandler
{
    private const long TotalGigabytes = 300L * 1024 * 1024 * 1024;
    private const string VisionFlow = "xtls-rprx-vision";

    private const int AdminListPageSize = 20;

    private readonly SessionStore _sessionStore;
    private readonly IPanelClient _panelClient;
    private readonly IAdminPanelService _adminPanelService;
    private readonly ITributeShopClient _tributeShopClient;
    private readonly TelegramOptions _telegramOptions;
    private readonly TributeOptions _tributeOptions;
    private readonly string _panelBaseUri;
    private readonly ILogger<TelegramHandlers> _logger;

    public TelegramHandlers(SessionStore sessionStore, IPanelClient panelClient,
        IAdminPanelService adminPanelService, ITributeShopClient tributeShopClient,
        IOptions<TelegramOptions> telegramOptions, IOptions<TributeOptions> tributeOptions,
        IOptions<PanelOptions> panelOptions, ILogger<TelegramHandlers> logger)
    {
        _sessionStore = sessionStore;
        _panelClient = panelClient;
        _adminPanelService = adminPanelService;
        _tributeShopClient = tributeShopClient;
        _telegramOptions = telegramOptions.Value;
        _tributeOptions = tributeOptions.Value;
        _panelBaseUri = panelOptions.Value.BaseUri;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Incoming update. UpdateId: {UpdateId}, From: {From}, Type: {Type}",
            update.Id, update.Message?.From?.Id ?? update.CallbackQuery?.From.Id, update.Type);

        if (update.Message?.Text is { } text)
        {
            await HandleMessage(botClient, update.Message, text, cancellationToken);
            return;
        }

        if (update.CallbackQuery is { } callbackQuery)
        {
            await HandleCallback(botClient, callbackQuery, cancellationToken);
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception,
        HandleErrorSource source, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled bot error. Source: {Source}", source);
        return Task.CompletedTask;
    }

    private async Task HandleMessage(ITelegramBotClient botClient, Message message, string text,
        CancellationToken cancellationToken)
    {
        var telegramId = message.Chat.Id;

        switch (text)
        {
            case "/start":
                _logger.LogInformation("User started bot. TelegramId: {TelegramId}, Username: {Username}",
                    telegramId, message.Chat.Username);
                _sessionStore.Reset(telegramId);
                await ShowStart(botClient, telegramId, message.From, cancellationToken);
                return;

            case "/help":
                await botClient.SendMessage(telegramId,
                    $"Please contact {_telegramOptions.SupportUrl} for any support requests",
                    cancellationToken: cancellationToken);
                return;

            case "/admin":
                await ShowAdminMenu(botClient, telegramId, cancellationToken);
                return;
        }

        var session = _sessionStore.Get(telegramId);

        if (session.AdminAction != AdminAction.None && IsAdmin(telegramId))
        {
            await HandleAdminTextInput(botClient, telegramId, text, cancellationToken);
            return;
        }

        await botClient.SendMessage(telegramId,
            $"Please contact {_telegramOptions.SupportUrl} for any support requests",
            cancellationToken: cancellationToken);
    }

    private async Task ShowStart(ITelegramBotClient botClient, long telegramId, User? from,
        CancellationToken cancellationToken)
    {
        var userDetails = await GetUserDetails(telegramId, cancellationToken);

        if (userDetails is null)
        {
            _logger.LogInformation("New user detected, auto-registering. TelegramId: {TelegramId}", telegramId);

            try
            {
                await RegisterNewUser(telegramId, from, cancellationToken);

                _logger.LogInformation("Auto-registration complete. TelegramId: {TelegramId}", telegramId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-registration failed. TelegramId: {TelegramId}", telegramId);

                await botClient.SendMessage(telegramId,
                    "❌ Something went wrong creating your account. Please try /start again.",
                    cancellationToken: cancellationToken);
                return;
            }

            userDetails = await GetUserDetails(telegramId, cancellationToken);
        }

        if (userDetails is null)
        {
            await botClient.SendMessage(telegramId,
                "❌ We couldn't find your account yet. Please use /start again shortly.",
                cancellationToken: cancellationToken);
            return;
        }

        var activeSession = _sessionStore.Get(telegramId);
        activeSession.SubscriptionUrl = userDetails.SubscriptionUrl;
        activeSession.UserStatus = userDetails.Status;

        _logger.LogDebug("User subscription status. TelegramId: {TelegramId}, Status: {Status}",
            telegramId, userDetails.Status);

        var text = BuildStatusText(userDetails);
        activeSession.MainMenuText = text;

        _logger.LogInformation("Rendering start menu. TelegramId: {TelegramId}", telegramId);

        await botClient.SendMessage(telegramId, text,
            replyMarkup: MenuService.MainMenu(activeSession.UserStatus, activeSession.SubscriptionUrl,
                IsAdmin(telegramId)),
            cancellationToken: cancellationToken);
    }

    private static string BuildStatusText(UserDetails userDetails)
    {
        var usedBandwidth = SubscriptionFormatter.FormatGigabytes(userDetails.UsedTrafficBytes) + "/" +
                            SubscriptionFormatter.FormatGigabytes(userDetails.TrafficLimitBytes);

        var text =
            $"Welcome to Eagle Tunnel Network\n\n" +
            $"👤 Username: {userDetails.Username}\n" +
            $"🆔 ID: {userDetails.TelegramId}\n\n" +
            $"{SubscriptionFormatter.GetStatusText(userDetails.Status)}\n" +
            $"📶 Bandwidth: {usedBandwidth} GB\n" +
            $"📱 No. of Devices Allowed: {userDetails.HwidDeviceLimit}\n\n\n\n" +
            $"VPN and unrestricted access — one subscription";

        if (userDetails.Status == SubscriptionStatus.Active)
        {
            text += "\n\n📲 Tap 'Copy VPN Link' to grab your link, or 'How to Connect' for a quick setup guide.";
        }

        return text;
    }

    private async Task<UserDetails?> GetUserDetails(long telegramId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _panelClient.GetClientByTelegramIdAsync(telegramId, cancellationToken);
            return UserDetails.From(response, _panelBaseUri);
        }
        catch (PanelApiException ex)
        {
            _logger.LogError(ex, "Failed to fetch user details. TelegramId: {TelegramId}", telegramId);
            return null;
        }
    }

    private static string SanitizePanelUsername(params string?[] parts)
    {
        var builder = new StringBuilder();

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            foreach (var rune in part.EnumerateRunes())
            {
                if (Rune.IsLetterOrDigit(rune))
                {
                    builder.Append(rune);
                }
            }
        }

        return builder.ToString();
    }

    private static string BuildPanelComment(long telegramId, User? from)
    {
        var fullName = string.Join(" ", new[] { from?.FirstName, from?.LastName }
            .Where(name => !string.IsNullOrWhiteSpace(name)));

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            parts.Add(fullName);
        }

        if (!string.IsNullOrWhiteSpace(from?.Username))
        {
            parts.Add($"@{from.Username}");
        }

        parts.Add($"Telegram ID: {telegramId}");

        return string.Join(" · ", parts);
    }

    private async Task RegisterNewUser(long telegramId, User? from, CancellationToken cancellationToken)
    {
        var username = SanitizePanelUsername(from?.FirstName, from?.LastName);
        if (username.Length == 0)
        {
            username = SanitizePanelUsername(from?.Username);
        }

        if (username.Length == 0)
        {
            username = $"user{telegramId}";
        }

        if (username.Length > 32)
        {
            username = username[..32];
        }

        var expiryTimeMs = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds();

        var payload = new CreateClientPayload(
            new CreateClientRequest(
                Email: username,
                Enable: false,
                ExpiryTime: expiryTimeMs,
                TotalGB: TotalGigabytes,
                TgId: telegramId,
                Comment: BuildPanelComment(telegramId, from),
                LimitIp: 0,
                SubId: RandomString.LowerAndNum(16),
                Password: RandomString.LowerAndNum(16),
                Auth: RandomString.LowerAndNum(16),
                Flow: VisionFlow
            ),
            _telegramOptions.DefaultInboundIds.ToList()
        );

        await _panelClient.AddClientAsync(payload, cancellationToken);
        await _panelClient.BulkDisableClientsAsync(new[] { username }, cancellationToken);
    }

    private async Task HandleCallback(ITelegramBotClient botClient, CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        var telegramId = callbackQuery.Message?.Chat.Id;

        if (telegramId is null)
        {
            return;
        }

        var messageId = callbackQuery.Message!.MessageId;
        var data = callbackQuery.Data ?? "";
        var session = _sessionStore.Get(telegramId.Value);

        if (IsAdmin(telegramId.Value) &&
            (data == MenuService.Admin || data.StartsWith(MenuService.Admin, StringComparison.Ordinal)))
        {
            await HandleAdminCallback(botClient, telegramId.Value, messageId, data, cancellationToken);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
            return;
        }

        switch (data)
        {
            case MenuService.Connect:
                await botClient.EditMessageText(telegramId.Value, messageId,
                    "🚀 *How to Connect*\n\n" +
                    "1️⃣ Install **INCY** from the App Store or Play Store\n" +
                    "2️⃣ Tap **📋 Copy VPN Link** below\n" +
                    "3️⃣ Open INCY and tap **\"Paste\"**\n" +
                    "   Select \"Allow Paste\" if prompted.\n" +
                    "4️⃣ Select a server location\n" +
                    "5️⃣ Tap the big power button to connect\n\n" +
                    "Allow VPN permission if prompted. You're connected 🎉",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.ConnectMenu(session.SubscriptionUrl ?? ""),
                    cancellationToken: cancellationToken);
                break;

            case MenuService.Support:
                var supportUserDetails = await GetUserDetails(telegramId.Value, cancellationToken);
                var supportUsername = supportUserDetails?.Username ??
                                      (callbackQuery.From.Username is { Length: > 0 }
                                          ? $"@{callbackQuery.From.Username}"
                                          : $"user{telegramId.Value}");

                var prefillText =
                    $"Hi! I need help with Eagle Tunnel VPN.\n\n" +
                    $"My username: {supportUsername}\n" +
                    $"My Telegram ID: {telegramId.Value}";

                await botClient.EditMessageText(telegramId.Value, messageId,
                    "Need help? Tap below — your username and Telegram ID are already pre-filled.",
                    replyMarkup: MenuService.SupportMenu(_telegramOptions.SupportUrl, prefillText),
                    cancellationToken: cancellationToken);
                break;

            case MenuService.Subscribe:
                await botClient.EditMessageText(telegramId.Value, messageId,
                    "💳 *Choose a subscription plan*\n\n" +
                    "🔄 Recurring plans charge every period — you can cancel anytime later.\n" +
                    "⚡ The one-time plan is a single payment and your card is *not* saved.",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.SubscriptionMenu(),
                    cancellationToken: cancellationToken);
                break;

            case MenuService.Back:
                await EditMainMenu(botClient, telegramId.Value, messageId, cancellationToken);
                break;

            default:
                if (data.StartsWith(SubscriptionPlans.PlanPrefix, StringComparison.Ordinal))
                {
                    await HandlePlanPurchase(botClient, callbackQuery, telegramId.Value, messageId, data,
                        cancellationToken);
                }

                break;
        }

        await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
    }

    private async Task HandlePlanPurchase(ITelegramBotClient botClient, CallbackQuery callbackQuery, long telegramId,
        int messageId, string data, CancellationToken cancellationToken)
    {
        var planKey = data[SubscriptionPlans.PlanPrefix.Length..];
        var plan = SubscriptionPlans.ByPeriod(planKey);

        if (plan is null)
        {
            _logger.LogWarning("Unknown plan selected. TelegramId: {TelegramId}, Data: {Data}", telegramId, data);

            await botClient.EditMessageText(telegramId, messageId, "❌ Unknown plan. Please try again.",
                replyMarkup: MenuService.SubscriptionMenu(), cancellationToken: cancellationToken);
            return;
        }

        var request = new CreateShopOrderRequest(
            ShopId: _tributeOptions.ShopId,
            Amount: plan.AmountKopecks,
            Currency: "rub",
            Title: $"Eagle Tunnel — {plan.Title}",
            Description: $"Eagle Tunnel Network VPN · {plan.Title}",
            SuccessUrl: string.IsNullOrWhiteSpace(_tributeOptions.SuccessUrl) ? null : _tributeOptions.SuccessUrl,
            FailUrl: string.IsNullOrWhiteSpace(_tributeOptions.FailUrl) ? null : _tributeOptions.FailUrl,
            Comment: telegramId.ToString(),
            CustomerId: telegramId.ToString(),
            Period: plan.TributePeriod
        );

        ShopOrderResponse? order;
        try
        {
            order = await _tributeShopClient.CreateOrderAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create shop order. TelegramId: {TelegramId}, Plan: {Plan}",
                telegramId, planKey);

            await botClient.EditMessageText(telegramId, messageId,
                "❌ Failed to create a payment link. Please try again or contact support.",
                replyMarkup: MenuService.SubscriptionMenu(), cancellationToken: cancellationToken);
            return;
        }

        if (string.IsNullOrEmpty(order?.PaymentUrl) && string.IsNullOrEmpty(order?.WebappPaymentUrl))
        {
            _logger.LogError("Shop order created without a payment URL. TelegramId: {TelegramId}, Uuid: {Uuid}",
                telegramId, order?.Uuid);

            await botClient.EditMessageText(telegramId, messageId,
                "❌ Payment link is unavailable. Please try again or contact support.",
                replyMarkup: MenuService.SubscriptionMenu(), cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("Shop order created for purchase. TelegramId: {TelegramId}, Uuid: {Uuid}, Plan: {Plan}",
            telegramId, order!.Uuid, planKey);

        await botClient.EditMessageText(telegramId, messageId,
            $"{plan.Title} — {plan.PriceRubles} ₽\n\n" +
            "✅ Your payment link is ready! Complete the payment to activate your VPN.",
            replyMarkup: MenuService.PaymentMenu(order?.PaymentUrl, order?.WebappPaymentUrl),
            cancellationToken: cancellationToken);
    }

    private bool IsAdmin(long telegramId) => _telegramOptions.AdminIds.Contains(telegramId);

    private async Task ShowAdminMenu(ITelegramBotClient botClient, long telegramId,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin(telegramId))
        {
            await botClient.SendMessage(telegramId, "⛔ You are not authorized to manage the panel.",
                cancellationToken: cancellationToken);
            return;
        }

        var session = _sessionStore.Get(telegramId);
        session.AdminAction = AdminAction.None;
        session.AdminTargetEmail = null;

        await botClient.SendMessage(telegramId,
            "🛠 *Admin Panel*\n\nPick an action to manage your panel.",
            parseMode: ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
            cancellationToken: cancellationToken);
    }

    private async Task EditAdminMenu(ITelegramBotClient botClient, long telegramId, int messageId,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);
        session.AdminAction = AdminAction.None;
        session.AdminTargetEmail = null;

        await botClient.EditMessageText(telegramId, messageId,
            "🛠 *Admin Panel*\n\nPick an action to manage your panel.",
            parseMode: ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
            cancellationToken: cancellationToken);
    }

    private async Task HandleAdminCallback(ITelegramBotClient botClient, long telegramId, int messageId,
        string data, CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);

        switch (data)
        {
            case MenuService.Admin:
                await EditAdminMenu(botClient, telegramId, messageId, cancellationToken);
                return;

            case MenuService.AdminExit:
                await EditMainMenu(botClient, telegramId, messageId, cancellationToken);
                return;

            case MenuService.AdminCancel:
                await EditAdminMenu(botClient, telegramId, messageId, cancellationToken);
                return;

            case MenuService.AdminLookup:
                session.AdminAction = AdminAction.Lookup;
                await botClient.EditMessageText(telegramId, messageId,
                    "🔍 Enter the account's **email or Telegram ID**:", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminGrant:
                session.AdminAction = AdminAction.GrantTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "➕ Enter the account's **email or Telegram ID** to grant or extend:", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminBan:
                session.AdminAction = AdminAction.BanTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "🚫 Enter the account's **email or Telegram ID** to ban:", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminUnban:
                session.AdminAction = AdminAction.UnbanTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "✅ Enter the account's **email or Telegram ID** to unban:", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminLimit:
                session.AdminAction = AdminAction.LimitTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "📱 Enter the account's **email or Telegram ID** to change device limit:", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminReset:
                session.AdminAction = AdminAction.ResetTrafficTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "♻️ Enter the account's **email or Telegram ID** to reset traffic:", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminLink:
                session.AdminAction = AdminAction.LinkTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "🔗 Enter the existing account's **email or Telegram ID**:", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminList:
                await ShowAdminList(botClient, telegramId, messageId, 0, cancellationToken);
                return;
        }

        if (data.StartsWith(MenuService.AdminListPrev + ":", StringComparison.Ordinal))
        {
            var page = int.Parse(data[(MenuService.AdminListPrev.Length + 1)..]);
            await ShowAdminList(botClient, telegramId, messageId, page, cancellationToken);
            return;
        }

        if (data.StartsWith(MenuService.AdminListNext + ":", StringComparison.Ordinal))
        {
            var page = int.Parse(data[(MenuService.AdminListNext.Length + 1)..]);
            await ShowAdminList(botClient, telegramId, messageId, page, cancellationToken);
            return;
        }

        if (data.StartsWith(MenuService.AdminConfirmGrant + ":", StringComparison.Ordinal))
        {
            await ExecuteAdminGrant(botClient, telegramId, messageId, data, cancellationToken);
            return;
        }

        if (data.StartsWith(MenuService.AdminConfirmBan + ":", StringComparison.Ordinal))
        {
            await ExecuteAdminBan(botClient, telegramId, messageId, data, cancellationToken);
            return;
        }

        if (data.StartsWith(MenuService.AdminConfirmUnban + ":", StringComparison.Ordinal))
        {
            await ExecuteAdminUnban(botClient, telegramId, messageId, data, cancellationToken);
            return;
        }

        if (data.StartsWith(MenuService.AdminConfirmLimit + ":", StringComparison.Ordinal))
        {
            await ExecuteAdminLimit(botClient, telegramId, messageId, data, cancellationToken);
            return;
        }

        if (data.StartsWith(MenuService.AdminConfirmReset + ":", StringComparison.Ordinal))
        {
            await ExecuteAdminReset(botClient, telegramId, messageId, data, cancellationToken);
            return;
        }

        if (data.StartsWith(MenuService.AdminConfirmLink + ":", StringComparison.Ordinal))
        {
            await ExecuteAdminLink(botClient, telegramId, messageId, data, cancellationToken);
            return;
        }
    }

    private async Task HandleAdminTextInput(ITelegramBotClient botClient, long telegramId, string text,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);
        var input = text.Trim();

        switch (session.AdminAction)
        {
            case AdminAction.Lookup:
                var lookupClient = await ResolveAdminTarget(input, cancellationToken);

                session.AdminAction = AdminAction.None;

                if (lookupClient is null)
                {
                    await botClient.SendMessage(telegramId, $"❌ No client found for `{input}`.",
                        parseMode: ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
                        cancellationToken: cancellationToken);
                    return;
                }

                await ShowAdminLookupResult(botClient, telegramId, lookupClient, cancellationToken);
                return;

            case AdminAction.GrantTarget:
                var grantClient = await ResolveAdminTarget(input, cancellationToken);
                if (grantClient is null)
                {
                    await botClient.SendMessage(telegramId, $"❌ No client found for `{input}`.",
                        parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    return;
                }

                session.AdminTargetEmail = grantClient.Email;
                session.AdminAction = AdminAction.GrantDays;
                await botClient.SendMessage(telegramId,
                    $"➕ How many days to grant to **{grantClient.Email}**?", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case AdminAction.GrantDays:
                if (!int.TryParse(input, out var days) || days <= 0)
                {
                    await botClient.SendMessage(telegramId, "❌ Invalid number of days. Please try again.",
                        cancellationToken: cancellationToken);
                    return;
                }

                var grantTarget = session.AdminTargetEmail;
                if (grantTarget is null)
                {
                    await ShowAdminMenu(botClient, telegramId, cancellationToken);
                    return;
                }

                session.AdminAction = AdminAction.None;
                session.AdminTargetEmail = null;

                await botClient.SendMessage(telegramId,
                    $"➡️ Grant **{days}** days to `{grantTarget}`?", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminConfirmMenu(MenuService.AdminConfirmGrant, $"{grantTarget}:{days}"),
                    cancellationToken: cancellationToken);
                return;

            case AdminAction.BanTarget:
            case AdminAction.UnbanTarget:
            case AdminAction.ResetTrafficTarget:
                var targetClient = await ResolveAdminTarget(input, cancellationToken);
                if (targetClient is null)
                {
                    await botClient.SendMessage(telegramId, $"❌ No client found for `{input}`.",
                        parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    return;
                }

                var action = session.AdminAction switch
                {
                    AdminAction.BanTarget => MenuService.AdminConfirmBan,
                    AdminAction.UnbanTarget => MenuService.AdminConfirmUnban,
                    _ => MenuService.AdminConfirmReset
                };
                var emoji = session.AdminAction switch
                {
                    AdminAction.BanTarget => "🚫 Ban",
                    AdminAction.UnbanTarget => "✅ Unban",
                    _ => "♻️ Reset traffic"
                };

                session.AdminAction = AdminAction.None;
                await botClient.SendMessage(telegramId,
                    $"{emoji} `{targetClient.Email}`?", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminConfirmMenu(action, targetClient.Email),
                    cancellationToken: cancellationToken);
                return;

            case AdminAction.LimitTarget:
                var limitClient = await ResolveAdminTarget(input, cancellationToken);
                if (limitClient is null)
                {
                    await botClient.SendMessage(telegramId, $"❌ No client found for `{input}`.",
                        parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    return;
                }

                session.AdminTargetEmail = limitClient.Email;
                session.AdminAction = AdminAction.LimitValue;
                await botClient.SendMessage(telegramId,
                    $"📱 How many connected devices for **{limitClient.Email}**?", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case AdminAction.LimitValue:
                if (!int.TryParse(input, out var limit) || limit < 0)
                {
                    await botClient.SendMessage(telegramId, "❌ Invalid device limit. Please try again.",
                        cancellationToken: cancellationToken);
                    return;
                }

                var limitTarget = session.AdminTargetEmail;
                if (limitTarget is null)
                {
                    await ShowAdminMenu(botClient, telegramId, cancellationToken);
                    return;
                }

                session.AdminAction = AdminAction.None;
                session.AdminTargetEmail = null;
                await botClient.SendMessage(telegramId,
                    $"➡️ Set device limit to **{limit}** for `{limitTarget}`?", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminConfirmMenu(MenuService.AdminConfirmLimit, $"{limitTarget}:{limit}"),
                    cancellationToken: cancellationToken);
                return;

            case AdminAction.LinkTarget:
                var linkClient = await ResolveAdminTarget(input, cancellationToken);
                if (linkClient is null)
                {
                    await botClient.SendMessage(telegramId, $"❌ No client found for `{input}`.",
                        parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    return;
                }

                session.AdminTargetEmail = linkClient.Email;
                session.AdminAction = AdminAction.LinkTelegramId;
                await botClient.SendMessage(telegramId,
                    $"🔗 Enter the **Telegram ID** to link to `{linkClient.Email}`:", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case AdminAction.LinkTelegramId:
                if (!long.TryParse(input, out var tgId) || tgId <= 0)
                {
                    await botClient.SendMessage(telegramId, "❌ Invalid Telegram ID. Please try again.",
                        cancellationToken: cancellationToken);
                    return;
                }

                var linkTarget = session.AdminTargetEmail;
                if (linkTarget is null)
                {
                    await ShowAdminMenu(botClient, telegramId, cancellationToken);
                    return;
                }

                session.AdminAction = AdminAction.None;
                session.AdminTargetEmail = null;
                await botClient.SendMessage(telegramId,
                    $"➡️ Link `{linkTarget}` to TG `{tgId}`?", parseMode: ParseMode.Markdown,
                    replyMarkup: MenuService.AdminConfirmMenu(MenuService.AdminConfirmLink, $"{linkTarget}:{tgId}"),
                    cancellationToken: cancellationToken);
                return;
        }

        await ShowAdminMenu(botClient, telegramId, cancellationToken);
    }

    private async Task<PanelClient?> ResolveAdminTarget(string input, CancellationToken cancellationToken)
    {
        try
        {
            return await _adminPanelService.GetByTargetAsync(input, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin target resolution failed. Target: {Target}", input);
            return null;
        }
    }

    private async Task ShowAdminLookupResult(ITelegramBotClient botClient, long adminId, PanelClient client,
        CancellationToken cancellationToken)
    {
        var expiry = client.ExpiryTime > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(client.ExpiryTime).ToUniversalTime().ToString("yyyy-MM-dd HH:mm")
                + " UTC"
            : "unlimited";

        var status = client.Enable
            ? (client.ExpiryTime > 0 && client.ExpiryTime < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                ? "🧟 Expired"
                : "🟢 Active")
            : "🔴 Disabled";

        await botClient.SendMessage(adminId,
            $"*User lookup*\n\n" +
            $"👤 Email: `{client.Email}`\n" +
            $"🆔 Telegram ID: {(client.TgId > 0 ? client.TgId.ToString() : "—")}\n" +
            $"📌 Status: {status}\n" +
            $"⏳ Expires: {expiry}\n" +
            $"📱 Devices: {client.LimitIp}\n" +
            $"💾 Quota: {SubscriptionFormatter.FormatGigabytes(client.TotalGB)} GB\n\n" +
            $"Account managed by panel 🔑 {client.Uuid}",
            parseMode: ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
            cancellationToken: cancellationToken);
    }

    private async Task ShowAdminList(ITelegramBotClient botClient, long telegramId, int messageId, int page,
        CancellationToken cancellationToken)
    {
        try
        {
            var clients = await _adminPanelService.ListClientsAsync(cancellationToken);

            if (clients.Count == 0)
            {
                await botClient.EditMessageText(telegramId, messageId, "📜 No clients found.",
                    replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
                return;
            }

            var totalPages = (int)Math.Ceiling(clients.Count / (double)AdminListPageSize);
            page = Math.Clamp(page, 0, totalPages - 1);

            var rows = clients
                .Skip(page * AdminListPageSize)
                .Take(AdminListPageSize)
                .Select(c =>
                {
                    var status = c.Enable ? "🟢" : "🔴";
                    var discriminator = c.SubId is { Length: > 0 } ? $" (sub: {c.SubId})" : "";
                    return $"{status} `{c.Email}`{discriminator} tg:{c.Id}";
                });

            await botClient.EditMessageText(telegramId, messageId,
                $"📜 *Clients* — {clients.Count} total\n\n" + string.Join("\n", rows),
                parseMode: ParseMode.Markdown,
                replyMarkup: MenuService.AdminListMenu(page, totalPages),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list clients. TelegramId: {TelegramId}", telegramId);
            await botClient.EditMessageText(telegramId, messageId, "❌ Failed to fetch clients.",
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
    }

    private (string Target, string Payload) ParseAdminConfirmTarget(string data, string prefix)
    {
        var raw = data[(prefix.Length + 1)..];
        var separator = raw.IndexOf(':');
        if (separator < 0)
        {
            return (raw, "");
        }

        return (raw[..separator], raw[(separator + 1)..]);
    }

    private async Task ExecuteAdminGrant(ITelegramBotClient botClient, long telegramId, int messageId,
        string data, CancellationToken cancellationToken)
    {
        var (target, payload) = ParseAdminConfirmTarget(data, MenuService.AdminConfirmGrant);

        try
        {
            var days = int.Parse(payload);
            await _adminPanelService.GrantAsync(target, days, cancellationToken);

            await botClient.EditMessageText(telegramId, messageId,
                $"✅ Granted **{days}** days to `{target}`.",
                parseMode: ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Grant failed. Target: {Target}", target);
            await botClient.EditMessageText(telegramId, messageId, "❌ Grant failed. Please try again.",
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
    }

    private async Task ExecuteAdminBan(ITelegramBotClient botClient, long telegramId, int messageId,
        string data, CancellationToken cancellationToken)
    {
        var (target, _) = ParseAdminConfirmTarget(data, MenuService.AdminConfirmBan);

        try
        {
            await _adminPanelService.BanAsync(target, cancellationToken);

            await botClient.EditMessageText(telegramId, messageId,
                $"🚫 Banned `{target}`.", parseMode: ParseMode.Markdown,
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ban failed. Target: {Target}", target);
            await botClient.EditMessageText(telegramId, messageId, "❌ Ban failed. Please try again.",
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
    }

    private async Task ExecuteAdminUnban(ITelegramBotClient botClient, long telegramId, int messageId,
        string data, CancellationToken cancellationToken)
    {
        var (target, _) = ParseAdminConfirmTarget(data, MenuService.AdminConfirmUnban);

        try
        {
            await _adminPanelService.UnbanAsync(target, cancellationToken);

            await botClient.EditMessageText(telegramId, messageId,
                $"✅ Unbanned `{target}`.", parseMode: ParseMode.Markdown,
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unban failed. Target: {Target}", target);
            await botClient.EditMessageText(telegramId, messageId, "❌ Unban failed. Please try again.",
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
    }

    private async Task ExecuteAdminLimit(ITelegramBotClient botClient, long telegramId, int messageId,
        string data, CancellationToken cancellationToken)
    {
        var (target, payload) = ParseAdminConfirmTarget(data, MenuService.AdminConfirmLimit);

        try
        {
            var limit = int.Parse(payload);
            await _adminPanelService.SetDeviceLimitAsync(target, limit, cancellationToken);

            await botClient.EditMessageText(telegramId, messageId,
                $"📱 Device limit set to **{limit}** for `{target}`.",
                parseMode: ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device limit change failed. Target: {Target}", target);
            await botClient.EditMessageText(telegramId, messageId, "❌ Device limit change failed. Please try again.",
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
    }

    private async Task ExecuteAdminReset(ITelegramBotClient botClient, long telegramId, int messageId,
        string data, CancellationToken cancellationToken)
    {
        var (target, _) = ParseAdminConfirmTarget(data, MenuService.AdminConfirmReset);

        try
        {
            await _adminPanelService.ResetTrafficAsync(target, cancellationToken);

            await botClient.EditMessageText(telegramId, messageId,
                $"♻️ Traffic reset for `{target}`.", parseMode: ParseMode.Markdown,
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Traffic reset failed. Target: {Target}", target);
            await botClient.EditMessageText(telegramId, messageId, "❌ Traffic reset failed. Please try again.",
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
    }

    private async Task ExecuteAdminLink(ITelegramBotClient botClient, long telegramId, int messageId,
        string data, CancellationToken cancellationToken)
    {
        var (target, payload) = ParseAdminConfirmTarget(data, MenuService.AdminConfirmLink);

        try
        {
            var tgId = long.Parse(payload);
            await _adminPanelService.LinkToTelegramAsync(target, tgId, cancellationToken);

            await botClient.EditMessageText(telegramId, messageId,
                $"🔗 Linked `{target}` to TG `{tgId}`.", parseMode: ParseMode.Markdown,
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Link failed. Target: {Target}", target);
            await botClient.EditMessageText(telegramId, messageId, "❌ Link failed. Please try again.",
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
    }

    private async Task EditMainMenu(ITelegramBotClient botClient, long telegramId, int messageId,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);

        var userDetails = await GetUserDetails(telegramId, cancellationToken);

        if (userDetails is not null)
        {
            session.SubscriptionUrl = userDetails.SubscriptionUrl;
            session.UserStatus = userDetails.Status;

            var freshText = BuildStatusText(userDetails);
            session.MainMenuText = freshText;

            await botClient.EditMessageText(telegramId, messageId, freshText,
                replyMarkup: MenuService.MainMenu(userDetails.Status, userDetails.SubscriptionUrl,
                    IsAdmin(telegramId)),
                cancellationToken: cancellationToken);
            return;
        }

        if (!string.IsNullOrEmpty(session.MainMenuText))
        {
            _logger.LogWarning("Panel fetch failed on Back; restoring cached main menu. TelegramId: {TelegramId}",
                telegramId);

            await botClient.EditMessageText(telegramId, messageId, session.MainMenuText,
                replyMarkup: MenuService.MainMenu(session.UserStatus, session.SubscriptionUrl,
                    IsAdmin(telegramId)),
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.EditMessageText(telegramId, messageId,
            "❌ Account not found. Please use /start to re-register.",
            cancellationToken: cancellationToken);
    }
}