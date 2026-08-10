using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
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
    private readonly TelegramOptions _telegramOptions;
    private readonly string _panelBaseUri;
    private readonly ILogger<TelegramHandlers> _logger;

    public TelegramHandlers(SessionStore sessionStore, IPanelClient panelClient,
        IOptions<TelegramOptions> telegramOptions, IOptions<PanelOptions> panelOptions,
        ILogger<TelegramHandlers> logger)
    {
        _sessionStore = sessionStore;
        _panelClient = panelClient;
        _telegramOptions = telegramOptions.Value;
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
                await ShowStart(botClient, telegramId, cancellationToken);
                return;

            case "/help":
                await botClient.SendMessage(telegramId,
                    $"Please contact {_telegramOptions.SupportUrl} for any support requests",
                    cancellationToken: cancellationToken);
                return;
        }

        await HandleTextMessage(botClient, telegramId, text, cancellationToken);
    }

    private async Task ShowStart(ITelegramBotClient botClient, long telegramId,
        CancellationToken cancellationToken)
    {
        var userDetails = await GetUserDetails(telegramId, cancellationToken);

        if (userDetails is null)
        {
            _logger.LogInformation("New user detected, needs registration. TelegramId: {TelegramId}", telegramId);

            var session = _sessionStore.Get(telegramId);

            if (session.RegistrationStep == RegistrationStep.None)
            {
                await botClient.SendMessage(telegramId,
                    "👤 Welcome to Eagle Tunnel Network! Please complete your registration.\n\nClick below to start:",
                    replyMarkup: MenuService.StartRegistrationMenu(), cancellationToken: cancellationToken);
            }

            return;
        }

        var activeSession = _sessionStore.Get(telegramId);
        activeSession.SubscriptionUrl = userDetails.SubscriptionUrl;
        activeSession.UserStatus = userDetails.Status;

        _logger.LogDebug("User subscription status. TelegramId: {TelegramId}, Status: {Status}",
            telegramId, userDetails.Status);

        var text = BuildStatusText(userDetails);

        _logger.LogInformation("Rendering start menu. TelegramId: {TelegramId}", telegramId);

        await botClient.SendMessage(telegramId, text,
            replyMarkup: MenuService.MainMenu(activeSession.UserStatus, activeSession.SubscriptionUrl,
                _telegramOptions.TributeSubscriptionUrl),
            cancellationToken: cancellationToken);
    }

    private string BuildStatusText(UserDetails userDetails)
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
            text +=
                "\n\n📱 To connect:\n" +
                "1️⃣ Tap 'Copy VPN Link'\n" +
                "2️⃣ Open INCY\n" +
                "3️⃣ Tap on \"Clipboard\"\n" +
                "Select \"Allow Paste\" if prompted.\n" +
                "4️⃣ Select a server location\n" +
                "5️⃣ Tap the big power button to connect\n" +
                "Allow VPN permission if prompted.\n\n" +
                "You're connected 🎉";
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

    private async Task HandleTextMessage(ITelegramBotClient botClient, long telegramId, string messageText,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);
        var input = messageText.Trim();

        switch (session.RegistrationStep)
        {
            case RegistrationStep.FirstName:
                if (input.Length == 0)
                {
                    await botClient.SendMessage(telegramId, "Please enter a valid first name.",
                        cancellationToken: cancellationToken);
                    return;
                }

                session.FirstName = input;
                session.RegistrationStep = RegistrationStep.MiddleName;

                await botClient.SendMessage(telegramId,
                    "Thank you! Now please enter your **middle name** (or type 'skip' to leave it empty):",
                    parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                return;

            case RegistrationStep.MiddleName:
                if (string.Equals(input, "skip", StringComparison.OrdinalIgnoreCase))
                {
                    session.MiddleName = null;
                }
                else
                {
                    session.MiddleName = input;
                }

                session.RegistrationStep = RegistrationStep.LastName;

                await botClient.SendMessage(telegramId, "Almost done! Please enter your **last name**:",
                    parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                return;

            case RegistrationStep.LastName:
                await FinishRegistration(botClient, telegramId, input, cancellationToken);
                return;

            default:
                await botClient.SendMessage(telegramId,
                    $"Please contact {_telegramOptions.SupportUrl} for any support requests",
                    cancellationToken: cancellationToken);
                return;
        }
    }

    private async Task FinishRegistration(ITelegramBotClient botClient, long telegramId, string input,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);

        if (input.Length == 0)
        {
            await botClient.SendMessage(telegramId, "Please enter a valid last name.",
                cancellationToken: cancellationToken);
            return;
        }

        session.LastName = input;
        session.RegistrationStep = RegistrationStep.None;

        var usernameParts = new List<string> { session.FirstName ?? "" };
        if (session.MiddleName is { Length: > 0 })
        {
            usernameParts.Add(session.MiddleName);
        }

        usernameParts.Add(session.LastName);
        var username = string.Concat(usernameParts);

        try
        {
            await RegisterNewUser(telegramId, username, cancellationToken);

            _logger.LogInformation("User registered successfully. TelegramId: {TelegramId}, Username: {Username}",
                telegramId, username);

            await botClient.SendMessage(telegramId,
                $"✅ Registration complete!\n\nWelcome {session.FirstName} {session.LastName}! Your account is ready.",
                cancellationToken: cancellationToken);

            await ShowStart(botClient, telegramId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "User registration failed. TelegramId: {TelegramId}", telegramId);

            await botClient.SendMessage(telegramId,
                "❌ Registration failed. Please try again by using /start command.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task RegisterNewUser(long telegramId, string username, CancellationToken cancellationToken)
    {
        var expiryTimeMs = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds();

        var payload = new CreateClientPayload(
            new CreateClientRequest(
                Email: username,
                Enable: false,
                ExpiryTime: expiryTimeMs,
                TotalGB: TotalGigabytes,
                TgId: telegramId,
                Comment: null,
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
            case MenuService.StartRegistration:
                session.RegistrationStep = RegistrationStep.FirstName;

                await botClient.EditMessageText(telegramId.Value, messageId, "Please enter your **first name**:",
                    parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                break;

            case MenuService.Setup:
                await botClient.EditMessageReplyMarkup(telegramId.Value, messageId, MenuService.SetupMenu(),
                    cancellationToken: cancellationToken);
                break;

            case MenuService.SetupInstall:
                await botClient.EditMessageText(telegramId.Value, messageId,
                    "📲 *Step 1 — Install INCY*\n\n" +
                    "Download and install INCY from the App Store or Play Store.\n\n" +
                    "_If you want to set up the VPN on your PC please go back and contact the support_\n\n" +
                    "After installing, return here and continue.",
                    parseMode: ParseMode.Markdown, replyMarkup: MenuService.SetupMenu(),
                    cancellationToken: cancellationToken);
                break;

            case MenuService.SetupImport:
                var subscriptionUrl = session.SubscriptionUrl ?? "";

                await botClient.EditMessageText(telegramId.Value, messageId,
                    "🔗 *Step 2 — Import Subscription*\n\n" +
                    "1️⃣ Copy the link below\n" +
                    "2️⃣ Open INCY\n" +
                    "3️⃣ Tap on \"Clipboard\"\n" +
                    "Select \"Allow Paste\" if prompted.\n\n" +
                    $"{subscriptionUrl}\n",
                    parseMode: ParseMode.Markdown, replyMarkup: MenuService.SetupMenu(),
                    cancellationToken: cancellationToken);
                break;

            case MenuService.SetupConnect:
                await botClient.EditMessageText(telegramId.Value, messageId,
                    "🚀 *Step 3 — Connect*\n\n" +
                    "1️⃣ Select a server location\n" +
                    "2️⃣ Tap the big power button to connect\n\n" +
                    "Allow VPN permission if prompted.\n\n" +
                    "You're connected 🎉",
                    parseMode: ParseMode.Markdown, replyMarkup: MenuService.SetupMenu(),
                    cancellationToken: cancellationToken);
                break;

            case MenuService.PreSupport:
                await botClient.EditMessageText(telegramId.Value, messageId,
                    "❗If your VPN is not working, please follow these steps before contacting support:\n\n" +
                    "1️⃣ Turn on airplane mode for 10-15 seconds and turn off\n" +
                    "2️⃣ Tap the power button to disconnect if it shows that you are connected\n" +
                    "3️⃣ Tap on the refresh button 🔄 \n" +
                    "4️⃣ Tap the power button to connect\n\n\n" +
                    "If its still not working then reboot your phone and perform the above steps\n\n" +
                    "Have you rebooted your phone?",
                    replyMarkup: MenuService.PreSupportMenu(), cancellationToken: cancellationToken);
                break;

            case MenuService.PreSupportYes:
                await botClient.EditMessageText(telegramId.Value, messageId,
                    "Still not working? Another question?\n\n" +
                    "Write to support and specify your username from the /start menu.",
                    replyMarkup: MenuService.SupportMenu(_telegramOptions.SupportUrl),
                    cancellationToken: cancellationToken);
                break;

            case MenuService.PreSupportNo:
                await botClient.EditMessageText(telegramId.Value, messageId,
                    "🔄 Please restart your phone and check the operation again.\n\n" +
                    "When you're ready, click 🔙 Back.",
                    replyMarkup: MenuService.PreSupportMenu(), cancellationToken: cancellationToken);
                break;

            case MenuService.Back:
                await botClient.EditMessageReplyMarkup(telegramId.Value, messageId,
                    MenuService.MainMenu(session.UserStatus, session.SubscriptionUrl,
                        _telegramOptions.TributeSubscriptionUrl),
                    cancellationToken: cancellationToken);
                break;
        }

        await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
    }
}
