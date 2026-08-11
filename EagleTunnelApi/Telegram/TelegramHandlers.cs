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

    private readonly SessionStore _sessionStore;
    private readonly IPanelClient _panelClient;
    private readonly ITributeShopClient _tributeShopClient;
    private readonly TelegramOptions _telegramOptions;
    private readonly TributeOptions _tributeOptions;
    private readonly string _panelBaseUri;
    private readonly ILogger<TelegramHandlers> _logger;

    public TelegramHandlers(SessionStore sessionStore, IPanelClient panelClient,
        ITributeShopClient tributeShopClient, IOptions<TelegramOptions> telegramOptions,
        IOptions<TributeOptions> tributeOptions, IOptions<PanelOptions> panelOptions,
        ILogger<TelegramHandlers> logger)
    {
        _sessionStore = sessionStore;
        _panelClient = panelClient;
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
            replyMarkup: MenuService.MainMenu(activeSession.UserStatus, activeSession.SubscriptionUrl),
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
                replyMarkup: MenuService.MainMenu(userDetails.Status, userDetails.SubscriptionUrl),
                cancellationToken: cancellationToken);
            return;
        }

        if (!string.IsNullOrEmpty(session.MainMenuText))
        {
            _logger.LogWarning("Panel fetch failed on Back; restoring cached main menu. TelegramId: {TelegramId}",
                telegramId);

            await botClient.EditMessageText(telegramId, messageId, session.MainMenuText,
                replyMarkup: MenuService.MainMenu(session.UserStatus, session.SubscriptionUrl),
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.EditMessageText(telegramId, messageId,
            "❌ Account not found. Please use /start to re-register.",
            cancellationToken: cancellationToken);
    }
}