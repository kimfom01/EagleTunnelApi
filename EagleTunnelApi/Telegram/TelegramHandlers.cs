using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Webhook.Exceptions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace EagleTunnelApi.Telegram;

public sealed class TelegramHandlers : IUpdateHandler
{
    private const int AdminListPageSize = 20;
    private readonly IAdminPanelService _adminPanelService;
    private readonly ILogger<TelegramHandlers> _logger;
    private readonly string _panelBaseUri;
    private readonly IPanelClient _panelClient;

    private readonly SessionStore _sessionStore;
    private readonly TelegramOptions _telegramOptions;

    public TelegramHandlers(SessionStore sessionStore, IPanelClient panelClient,
        IAdminPanelService adminPanelService, IOptions<TelegramOptions> telegramOptions,
        IOptions<PanelOptions> panelOptions, ILogger<TelegramHandlers> logger)
    {
        _sessionStore = sessionStore;
        _panelClient = panelClient;
        _adminPanelService = adminPanelService;
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
            await HandleCallback(botClient, callbackQuery, cancellationToken);
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
        var trimmed = text.Trim();

        if (IsStartCommand(trimmed))
        {
            _logger.LogInformation("User started bot. TelegramId: {TelegramId}, Username: {Username}",
                telegramId, message.Chat.Username);
            _sessionStore.Reset(telegramId);
            await ShowStart(botClient, telegramId, message.From, ExtractStartPayload(trimmed), cancellationToken);
            return;
        }

        var session = _sessionStore.Get(telegramId);

        if (session.FriendInviteActive)
        {
            await HandleFriendInviteInput(botClient, telegramId, trimmed, cancellationToken);
            return;
        }

        if (session.RegistrationStep != RegistrationStep.None)
        {
            await HandleRegistrationInput(botClient, telegramId, message.From, trimmed, cancellationToken);
            return;
        }

        switch (trimmed)
        {
            case "/help":
                await botClient.SendMessage(telegramId,
                    $"Please contact {_telegramOptions.SupportUrl} for any support requests",
                    cancellationToken: cancellationToken);
                return;

            case "/admin":
                await ShowAdminMenu(botClient, telegramId, cancellationToken);
                return;

            case "/referrer":
                await ShowReferrerEditor(botClient, telegramId, cancellationToken);
                return;
        }

        if (session.AdminAction != AdminAction.None && IsAdmin(telegramId))
        {
            await HandleAdminTextInput(botClient, telegramId, text, cancellationToken);
            return;
        }

        await botClient.SendMessage(telegramId,
            $"Please contact {_telegramOptions.SupportUrl} for any support requests",
            cancellationToken: cancellationToken);
    }

    private static bool IsStartCommand(string text)
    {
        return text == "/start" || text.StartsWith("/start ", StringComparison.Ordinal) ||
               text.StartsWith("/start@", StringComparison.Ordinal);
    }

    private static string? ExtractStartPayload(string text)
    {
        var rest = text["/start".Length..];

        if (rest.StartsWith('@'))
        {
            var space = rest.IndexOf(' ');
            if (space < 0) return null;

            rest = rest[space..];
        }

        rest = rest.Trim();
        return rest.Length == 0 ? null : rest;
    }

    private async Task ShowStart(ITelegramBotClient botClient, long telegramId, User? from,
        string? startPayload, CancellationToken cancellationToken)
    {
        var userDetails = await GetUserDetails(telegramId, cancellationToken);

        if (userDetails is null)
        {
            await StartEmailRegistration(botClient, telegramId, startPayload, true,
                false, cancellationToken);
            return;
        }

        if (ReferralService.IsLegacyEmail(userDetails.Username))
        {
            if (userDetails.HasEverBeenProvisioned())
            {
                _logger.LogInformation(
                    "Provisioned legacy account keeps its login, no migration. TelegramId: {TelegramId}",
                    telegramId);

                await RenderMainMenu(botClient, telegramId, userDetails, cancellationToken);
                return;
            }

            _logger.LogInformation(
                "Legacy account detected, starting email migration. TelegramId: {TelegramId}", telegramId);

            await StartEmailRegistration(botClient, telegramId, null,
                true, true, cancellationToken);
            return;
        }

        await RenderMainMenu(botClient, telegramId, userDetails, cancellationToken);
    }

    private async Task StartEmailRegistration(ITelegramBotClient botClient, long telegramId,
        string? startPayload, bool collectReferrer, bool isMigration, CancellationToken cancellationToken,
        string? introOverride = null)
    {
        var session = _sessionStore.Get(telegramId);
        session.RegistrationStep = RegistrationStep.AwaitingEmail;
        session.CollectReferrer = collectReferrer;
        session.PendingEmail = null;
        session.PendingReferrerTgId = null;
        session.PendingReferrerEmail = null;

        if (collectReferrer && ReferralService.TryParseRefPayload(startPayload, out var referrerTgId))
        {
            if (referrerTgId == telegramId)
                _logger.LogInformation("Ignoring self referral link. TelegramId: {TelegramId}", telegramId);
            else
                try
                {
                    var referrer = await GetUserDetails(referrerTgId, cancellationToken);
                    if (referrer is not null)
                    {
                        session.PendingReferrerTgId = referrerTgId;
                        session.PendingReferrerEmail = referrer.Username;
                    }
                }
                catch (PanelApiException ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve referrer. ReferrerTgId: {ReferrerTgId}",
                        referrerTgId);
                }
        }

        var intro = introOverride
            ?? (isMigration
                ? "👋 Welcome back! We've upgraded accounts to use email addresses.\n\n"
                : "👋 Welcome to Eagle Tunnel Network!\n\n");

        if (introOverride is null && _telegramOptions.TrialDurationHours > 0)
            intro += $"🎁 New accounts start with a {_telegramOptions.TrialDurationHours}-hour free trial.\n\n";

        await botClient.SendMessage(telegramId,
            intro + "To create your account, please reply with your **email address**:",
            ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task RenderMainMenu(ITelegramBotClient botClient, long telegramId, UserDetails userDetails,
        CancellationToken cancellationToken)
    {
        var activeSession = _sessionStore.Get(telegramId);
        activeSession.SubscriptionUrl = userDetails.SubscriptionUrl;
        activeSession.UserStatus = userDetails.Status;

        _logger.LogDebug("User subscription status. TelegramId: {TelegramId}, Status: {Status}",
            telegramId, userDetails.Status);

        var text = BuildStatusText(userDetails);

        if (ReferralService.HasTrialTag(userDetails.Comment))
            text += "\n\n🎁 You're on a free trial — subscribe before it ends to stay connected.";

        activeSession.MainMenuText = text;

        _logger.LogInformation("Rendering start menu. TelegramId: {TelegramId}", telegramId);

        await botClient.SendMessage(telegramId, text,
            replyMarkup: MenuService.MainMenu(activeSession.UserStatus, activeSession.SubscriptionUrl,
                _telegramOptions.TributeSubscriptionUrl, IsAdmin(telegramId)),
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
            $"📱 Devices Allowed: {userDetails.HwidDeviceLimit}\n" +
            $"⏳ Expires: {userDetails.ExpireAt:yyyy-MM-dd HH:mm} UTC\n" +
            $"♻️ Traffic Reset: {userDetails.TrafficReset} — Day {userDetails.TrafficResetDay}\n\n" +
            $"VPN and unrestricted access — one subscription";

        if (userDetails.Status == SubscriptionStatus.Active)
            text += "\n\n📲 Tap 'Copy VPN Link' to grab your link, or 'How to Connect' for a quick setup guide.";

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

    private async Task HandleRegistrationInput(ITelegramBotClient botClient, long telegramId, User? from,
        string text, CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);

        if (text.Equals("/skip", StringComparison.OrdinalIgnoreCase))
        {
            if (session.RegistrationStep == RegistrationStep.AwaitingReferrer)
            {
                session.PendingReferrerTgId = null;
                session.PendingReferrerEmail = null;
                await CompleteRegistrationAsync(botClient, telegramId, from, cancellationToken);
                return;
            }

            await botClient.SendMessage(telegramId,
                "⚠️ Your email is required — please reply with your **email address**:",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);
            return;
        }

        if (session.RegistrationStep == RegistrationStep.AwaitingEmail)
        {
            await HandleEmailInput(botClient, telegramId, from, text, cancellationToken);
            return;
        }

        if (session.RegistrationStep == RegistrationStep.AwaitingReferrer)
            await HandleManualReferrerInput(botClient, telegramId, from, text, cancellationToken);
    }

    private async Task HandleEmailInput(ITelegramBotClient botClient, long telegramId, User? from,
        string text, CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);
        var email = ReferralService.NormalizeEmail(text);

        if (!ReferralService.IsValidEmail(email))
        {
            await botClient.SendMessage(telegramId,
                "❌ That doesn't look like a valid email address. Please try again:",
                cancellationToken: cancellationToken);
            return;
        }

        PanelClientResponse? existing;
        try
        {
            existing = await _panelClient.GetClientByEmailAsync(email!, cancellationToken);
        }
        catch (PanelApiException ex)
        {
            // The panel reports unknown emails as a failure, so a lookup error most likely
            // means the address is free. Proceed: the create/update step below is authoritative
            // and fails safely if the email is truly taken or the panel is down.
            _logger.LogWarning(ex, "Email lookup failed, assuming available. Email: {Email}", email);
            existing = null;
        }

        if (existing?.Client is not null && existing.Client.TgId != telegramId)
        {
            if (existing.Client.TgId <= 0)
            {
                await ClaimInvitedAccountAsync(botClient, telegramId, from, existing.Client,
                    cancellationToken);
                return;
            }

            await botClient.SendMessage(telegramId,
                "❌ That email is already registered. Please enter a different email address:",
                cancellationToken: cancellationToken);
            return;
        }

        if (existing?.Client is not null)
        {
            _logger.LogInformation("Email already linked to requester, finishing. TelegramId: {TelegramId}",
                telegramId);
            session.RegistrationStep = RegistrationStep.None;
            await ShowStart(botClient, telegramId, null, null, cancellationToken);
            return;
        }

        session.PendingEmail = email;

        if (!session.CollectReferrer)
        {
            await CompleteRegistrationAsync(botClient, telegramId, null, cancellationToken);
            return;
        }

        session.RegistrationStep = RegistrationStep.AwaitingReferrer;
        await PromptReferrerAsync(botClient, telegramId, cancellationToken);
    }

    private async Task PromptReferrerAsync(ITelegramBotClient botClient, long telegramId,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);

        if (session.PendingReferrerEmail is not null)
        {
            await botClient.SendMessage(telegramId,
                $"🎁 You were referred by `{session.PendingReferrerEmail}`.\n\nPlease confirm:",
                ParseMode.Markdown,
                replyMarkup: MenuService.RefConfirmMenu(),
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendMessage(telegramId,
            "🎁 If someone referred you, reply with their **email address** — or tap Skip if nobody did.",
            ParseMode.Markdown,
            replyMarkup: MenuService.ReferralInputMenu(),
            cancellationToken: cancellationToken);
    }

    private async Task HandleManualReferrerInput(ITelegramBotClient botClient, long telegramId, User? from,
        string text, CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);
        var email = ReferralService.NormalizeEmail(text);

        if (!ReferralService.IsValidEmail(email))
        {
            await botClient.SendMessage(telegramId,
                "❌ That doesn't look like a valid email address. Please try again, or tap Skip.",
                replyMarkup: MenuService.ReferralInputMenu(),
                cancellationToken: cancellationToken);
            return;
        }

        if (session.PendingEmail is not null && email == session.PendingEmail)
        {
            await botClient.SendMessage(telegramId,
                "🙂 You can't refer yourself. Please enter someone else's email, or tap Skip.",
                replyMarkup: MenuService.ReferralInputMenu(),
                cancellationToken: cancellationToken);
            return;
        }

        PanelClient? referrer = null;
        try
        {
            referrer = (await _panelClient.GetClientByEmailAsync(email!, cancellationToken))?.Client;
        }
        catch (PanelApiException ex)
        {
            _logger.LogWarning(ex, "Referrer lookup failed, saving as pending. Email: {Email}", email);
        }

        if (referrer is not null)
        {
            if (referrer.TgId == telegramId)
            {
                await botClient.SendMessage(telegramId,
                    "🙂 You can't refer yourself. Please enter someone else's email, or tap Skip.",
                    replyMarkup: MenuService.ReferralInputMenu(),
                    cancellationToken: cancellationToken);
                return;
            }

            session.PendingReferrerTgId = referrer.TgId > 0 ? referrer.TgId : null;
            session.PendingReferrerEmail = referrer.Email;
        }
        else
        {
            session.PendingReferrerTgId = null;
            session.PendingReferrerEmail = email;
        }

        await CompleteRegistrationAsync(botClient, telegramId, from, cancellationToken);
    }

    private async Task ShowReferrerEditor(ITelegramBotClient botClient, long telegramId,
        CancellationToken cancellationToken)
    {
        PanelClientResponse? response;
        try
        {
            response = await _panelClient.GetClientByTelegramIdAsync(telegramId, cancellationToken);
        }
        catch (PanelApiException ex)
        {
            _logger.LogWarning(ex, "Referrer editor lookup failed. TelegramId: {TelegramId}", telegramId);
            await botClient.SendMessage(telegramId,
                "❌ We couldn't load your account right now. Please try again shortly.",
                cancellationToken: cancellationToken);
            return;
        }

        if (response?.Client is null)
        {
            await botClient.SendMessage(telegramId,
                "You don't have an account yet — please use /start to register first.",
                cancellationToken: cancellationToken);
            return;
        }

        var client = response.Client;

        if (ReferralService.HasEverBeenProvisioned(client))
        {
            await botClient.SendMessage(telegramId,
                "🔒 Referrals only count for new subscribers, so your referrer can't be changed anymore.\n\n" +
                $"If you believe this is a mistake, please contact {_telegramOptions.SupportUrl}.",
                cancellationToken: cancellationToken);
            return;
        }

        if (ReferralService.HasBonusPaidMarker(client.Comment))
        {
            await botClient.SendMessage(telegramId,
                "🔒 Your referral was already counted, so it can't be changed anymore.\n\n" +
                $"If you believe this is a mistake, please contact {_telegramOptions.SupportUrl}.",
                cancellationToken: cancellationToken);
            return;
        }

        var session = _sessionStore.Get(telegramId);
        session.RegistrationStep = RegistrationStep.AwaitingReferrer;
        session.CollectReferrer = true;
        session.PendingEmail = client.Email;
        session.PendingReferrerTgId = null;
        session.PendingReferrerEmail = null;

        await botClient.SendMessage(telegramId,
            "🎁 Reply with your referrer's **email address** — or tap Skip to remove it.",
            ParseMode.Markdown,
            replyMarkup: MenuService.ReferralInputMenu(),
            cancellationToken: cancellationToken);
    }

    private async Task CompleteRegistrationAsync(ITelegramBotClient botClient, long telegramId, User? from,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);

        if (string.IsNullOrEmpty(session.PendingEmail))
        {
            session.RegistrationStep = RegistrationStep.AwaitingEmail;
            await botClient.SendMessage(telegramId,
                "Please reply with your **email address**:",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);
            return;
        }

        var email = session.PendingEmail;
        var referrerTgId = session.PendingReferrerTgId;
        var referrerEmail = session.PendingReferrerEmail;

        PanelClientResponse? existing;
        try
        {
            existing = await _panelClient.GetClientByTelegramIdAsync(telegramId, cancellationToken);
        }
        catch (PanelApiException ex)
        {
            _logger.LogError(ex, "Registration failed loading account. TelegramId: {TelegramId}", telegramId);
            await botClient.SendMessage(telegramId,
                "❌ Something went wrong creating your account. Please try /start again.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            if (existing?.Client is null)
                await CreateRegisteredClientAsync(telegramId, from, email, referrerTgId, referrerEmail,
                    cancellationToken);
            else if (existing.Client.Email.Equals(email, StringComparison.OrdinalIgnoreCase))
                await UpdateReferralAttributionAsync(existing.Client, referrerTgId, referrerEmail,
                    cancellationToken);
            else
                await MigrateClientEmailAsync(existing.Client, email, referrerTgId, referrerEmail,
                    cancellationToken);

            _logger.LogInformation("Registration complete. TelegramId: {TelegramId}, Email: {Email}",
                telegramId, email);
        }
        catch (PanelApiException ex)
        {
            _logger.LogError(ex, "Registration failed. TelegramId: {TelegramId}", telegramId);
            await botClient.SendMessage(telegramId,
                "❌ Something went wrong creating your account. Please try /start again.",
                cancellationToken: cancellationToken);
            return;
        }

        session.RegistrationStep = RegistrationStep.None;
        session.CollectReferrer = false;
        session.PendingEmail = null;
        session.PendingReferrerTgId = null;
        session.PendingReferrerEmail = null;

        if (session.ReturnToReferral)
        {
            session.ReturnToReferral = false;
            await SendReferralLinkAsync(botClient, telegramId, cancellationToken);
            return;
        }

        var userDetails = await GetUserDetails(telegramId, cancellationToken);
        if (userDetails is null)
        {
            await botClient.SendMessage(telegramId,
                "❌ We couldn't find your account yet. Please use /start again shortly.",
                cancellationToken: cancellationToken);
            return;
        }

        await RenderMainMenu(botClient, telegramId, userDetails, cancellationToken);
    }

    private string BuildReferralComment(string baseComment, long? referrerTgId, string? referrerEmail)
    {
        var comment = baseComment;

        if (referrerTgId is long tgId) comment = ReferralService.AppendTag(comment, $"Referrer tgId: {tgId}");

        if (referrerEmail is not null)
            comment = ReferralService.WithReferredBy(comment, referrerEmail, referrerTgId is null);

        return comment;
    }

    private async Task CreateRegisteredClientAsync(long telegramId, User? from, string email,
        long? referrerTgId, string? referrerEmail, CancellationToken cancellationToken)
    {
        var (enable, expiryTimeMs, trialEnds) = NewAccountState();
        var comment = BuildReferralComment(BuildPanelComment(telegramId, from), referrerTgId, referrerEmail);

        if (trialEnds.HasValue) comment = ReferralService.WithTrialTag(comment, trialEnds.Value);

        var payload = new CreateClientPayload(
            PanelClientDefaults.CreateClient(email, enable, expiryTimeMs, telegramId, comment),
            _telegramOptions.DefaultInboundIds.ToList()
        );

        try
        {
            await _panelClient.AddClientAsync(payload, cancellationToken);
        }
        catch (PanelApiException)
        {
            _logger.LogWarning(
                "Creating client failed for TelegramId: {TelegramId}. Checking whether it was created concurrently.",
                telegramId);

            var existing = await _panelClient.GetClientByTelegramIdAsync(telegramId, cancellationToken);

            if (existing is null) throw;
        }

        if (!enable) await _panelClient.BulkDisableClientsAsync([email], cancellationToken);
    }

    private (bool Enable, long ExpiryTimeMs, DateTimeOffset? TrialEnds) NewAccountState()
    {
        if (_telegramOptions.TrialDurationHours <= 0)
            return (false, DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds(), null);

        var ends = DateTimeOffset.UtcNow.AddHours(_telegramOptions.TrialDurationHours);
        return (true, ends.ToUnixTimeMilliseconds(), ends);
    }

    private async Task UpdateReferralAttributionAsync(PanelClient client, long? referrerTgId,
        string? referrerEmail, CancellationToken cancellationToken)
    {
        var cleared = ReferralService.ClearReferralAttribution(client.Comment ?? "");
        var comment = BuildReferralComment(cleared, referrerTgId, referrerEmail);

        await _panelClient.UpdateClientAsync(client.ToUpdateRequest() with { Comment = comment },
            cancellationToken);
    }

    private async Task MigrateClientEmailAsync(PanelClient client, string email, long? referrerTgId,
        string? referrerEmail, CancellationToken cancellationToken)
    {
        if (ReferralService.HasEverBeenProvisioned(client))
        {
            await _panelClient.UpdateClientAsync(client.Email,
                client.ToUpdateRequest() with { Email = email }, cancellationToken);

            _logger.LogInformation("Renamed provisioned client email, state preserved. OldEmail: {Old}, NewEmail: {New}",
                client.Email, email);
            return;
        }

        var comment = BuildReferralComment(
            ReferralService.ClearReferralAttribution(client.Comment ?? ""), referrerTgId, referrerEmail);
        var (enable, expiryTimeMs, trialEnds) = NewAccountState();

        if (trialEnds.HasValue) comment = ReferralService.WithTrialTag(comment, trialEnds.Value);

        var migrated = client.ToUpdateRequest() with
        {
            Email = email,
            Comment = comment,
            Enable = enable,
            ExpiryTime = expiryTimeMs
        };

        try
        {
            await _panelClient.UpdateClientAsync(client.Email, migrated, cancellationToken);

            _logger.LogInformation("Migrated client email in place. OldEmail: {Old}, NewEmail: {New}",
                client.Email, email);
            return;
        }
        catch (PanelApiException ex)
        {
            _logger.LogWarning(ex,
                "In-place email migration failed, recreating client. OldEmail: {Old}, NewEmail: {New}",
                client.Email, email);
        }

        var recreated = migrated;
        var payload = new CreateClientPayload(
            new CreateClientRequest(
                recreated.Email, recreated.Enable, recreated.ExpiryTime, recreated.TotalGB, recreated.TgId,
                recreated.Comment, recreated.LimitHwid, recreated.TrafficReset, recreated.TrafficResetDay,
                recreated.SubId ?? RandomString.LowerAndNum(PanelClientDefaults.CredentialsLength),
                RandomString.LowerAndNum(PanelClientDefaults.CredentialsLength),
                RandomString.LowerAndNum(PanelClientDefaults.CredentialsLength),
                recreated.Flow ?? PanelClientDefaults.VisionFlow),
            _telegramOptions.DefaultInboundIds.ToList()
        );

        await _panelClient.AddClientAsync(payload, cancellationToken);
        await _panelClient.BulkDisableClientsAsync([client.Email], cancellationToken);
    }

    private static string BuildPanelComment(long telegramId, User? from)
    {
        var fullName = string.Join(" ", new[] { from?.FirstName, from?.LastName }
            .Where(name => !string.IsNullOrWhiteSpace(name)));

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(fullName)) parts.Add(fullName);

        if (!string.IsNullOrWhiteSpace(from?.Username)) parts.Add($"@{from.Username}");

        parts.Add($"Telegram ID: {telegramId}");

        return string.Join(" · ", parts);
    }

    private async Task HandleCallback(ITelegramBotClient botClient, CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        var telegramId = callbackQuery.Message?.Chat.Id;

        if (telegramId is null) return;

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
                    "📲 *How to Connect*\n\n" +
                    "1️⃣ Download and install INCY from the App Store or Play Store.\n\n" +
                    "2️⃣ Import your subscription link.\n\n" +
                    "3️⃣ Connect to a server location.\n\n" +
                    "You're connected 🎉",
                    ParseMode.Markdown, MenuService.ConnectMenu(session.SubscriptionUrl ?? ""),
                    cancellationToken: cancellationToken);
                break;

            case MenuService.Support:
                {
                    var supportUserDetails = await GetUserDetails(telegramId.Value, cancellationToken);
                    var username = supportUserDetails?.Username ?? "";
                    var prefillText = $"My username: {username}\nMy Telegram ID: {telegramId.Value}";
                    await botClient.EditMessageText(telegramId.Value, messageId,
                        "❗If your VPN is not working, please contact support:",
                        replyMarkup: MenuService.SupportMenu(_telegramOptions.SupportUrl, prefillText),
                        cancellationToken: cancellationToken);
                    break;
                }

            case MenuService.Back:
                await EditMainMenu(botClient, telegramId.Value, messageId, cancellationToken);
                break;

            case MenuService.Referral:
                await ShowReferralScreen(botClient, telegramId.Value, messageId, cancellationToken);
                break;

            case MenuService.RefConfirm:
                await ConfirmDeepLinkReferrer(botClient, telegramId.Value, callbackQuery.From,
                    cancellationToken);
                break;

            case MenuService.RefEdit:
                await EditDeepLinkReferrer(botClient, telegramId.Value, cancellationToken);
                break;

            case MenuService.RefSkip:
                await SkipDeepLinkReferrer(botClient, telegramId.Value, callbackQuery.From,
                    cancellationToken);
                break;

            case MenuService.FriendRegister:
                await StartFriendInvite(botClient, telegramId.Value, cancellationToken);
                break;
        }

        await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
    }

    private bool IsAdmin(long telegramId)
    {
        return _telegramOptions.AdminIds.Contains(telegramId);
    }

    private async Task ShowReferralScreen(ITelegramBotClient botClient, long telegramId, int messageId,
        CancellationToken cancellationToken)
    {
        var userDetails = await GetUserDetails(telegramId, cancellationToken);

        if (userDetails is not null && ReferralService.IsLegacyEmail(userDetails.Username))
        {
            _logger.LogInformation(
                "Legacy account opened referrals, requiring one-time email update. TelegramId: {TelegramId}",
                telegramId);

            await StartEmailRegistration(botClient, telegramId, null,
                !userDetails.HasEverBeenProvisioned(), true, cancellationToken,
                "🎁 To share your invite link, set your login email address below.\n" +
                "This is a one-time update — afterwards the link is yours to share.\n\n");

            _sessionStore.Get(telegramId).ReturnToReferral = true;
            return;
        }

        var link = ReferralService.BuildReferralLink(_telegramOptions.BotUsername, telegramId);

        await botClient.EditMessageText(telegramId, messageId, ReferralScreenText(),
            ParseMode.Markdown,
            MenuService.ReferralMenu(link, _telegramOptions.SupportUrl),
            cancellationToken: cancellationToken);
    }

    private async Task SendReferralLinkAsync(ITelegramBotClient botClient, long telegramId,
        CancellationToken cancellationToken)
    {
        var link = ReferralService.BuildReferralLink(_telegramOptions.BotUsername, telegramId);

        await botClient.SendMessage(telegramId, ReferralScreenText(),
            ParseMode.Markdown,
            replyMarkup: MenuService.ReferralMenu(link, _telegramOptions.SupportUrl),
            cancellationToken: cancellationToken);
    }

    private static string ReferralScreenText()
    {
        return "🎁 *Invite Friends — Get 1 Month Free*\n\n" +
            "1️⃣ Share your invite link below.\n" +
            "2️⃣ Your friend registers and subscribes.\n" +
            "3️⃣ You get **+30 days** of VPN time.\n\n" +
            "Tip: once your bonus lands, cancel your Tribute renewal so you aren't billed while " +
            "covered — message support if you'd like the step-by-step video guide.";
    }

    private async Task ConfirmDeepLinkReferrer(ITelegramBotClient botClient, long telegramId, User? from,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);

        if (session.RegistrationStep != RegistrationStep.AwaitingReferrer ||
            session.PendingReferrerEmail is null)
            return;

        await CompleteRegistrationAsync(botClient, telegramId, from, cancellationToken);
    }

    private async Task EditDeepLinkReferrer(ITelegramBotClient botClient, long telegramId,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);

        if (session.RegistrationStep != RegistrationStep.AwaitingReferrer) return;

        session.PendingReferrerTgId = null;
        session.PendingReferrerEmail = null;

        await botClient.SendMessage(telegramId,
            "Please reply with your referrer's **email address**:",
            ParseMode.Markdown,
            replyMarkup: MenuService.ReferralInputMenu(),
            cancellationToken: cancellationToken);
    }

    private async Task SkipDeepLinkReferrer(ITelegramBotClient botClient, long telegramId, User? from,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);

        if (session.RegistrationStep != RegistrationStep.AwaitingReferrer) return;

        session.PendingReferrerTgId = null;
        session.PendingReferrerEmail = null;
        await CompleteRegistrationAsync(botClient, telegramId, from, cancellationToken);
    }

    private async Task StartFriendInvite(ITelegramBotClient botClient, long telegramId,
        CancellationToken cancellationToken)
    {
        PanelClientResponse? own;
        try
        {
            own = await _panelClient.GetClientByTelegramIdAsync(telegramId, cancellationToken);
        }
        catch (PanelApiException ex)
        {
            _logger.LogWarning(ex, "Friend invite lookup failed. TelegramId: {TelegramId}", telegramId);
            own = null;
        }

        if (own?.Client is null)
        {
            await botClient.SendMessage(telegramId,
                "You don't have an account yet — please use /start to register first.",
                cancellationToken: cancellationToken);
            return;
        }

        _sessionStore.Get(telegramId).FriendInviteActive = true;

        await botClient.SendMessage(telegramId,
            "📝 Reply with your friend's **email address** and we'll create their account " +
            "with you as referrer. They'll link it when they start the bot.\n\n(/start to cancel)",
            ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task HandleFriendInviteInput(ITelegramBotClient botClient, long telegramId, string text,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);
        var email = ReferralService.NormalizeEmail(text);

        if (!ReferralService.IsValidEmail(email))
        {
            await botClient.SendMessage(telegramId,
                "❌ That doesn't look like a valid email address. Please try again (/start to cancel):",
                cancellationToken: cancellationToken);
            return;
        }

        PanelClientResponse? own = null;
        PanelClientResponse? existing = null;
        try
        {
            own = await _panelClient.GetClientByTelegramIdAsync(telegramId, cancellationToken);
            existing = await _panelClient.GetClientByEmailAsync(email!, cancellationToken);
        }
        catch (PanelApiException ex)
        {
            _logger.LogWarning(ex, "Friend invite lookup failed. Email: {Email}", email);
        }

        if (own?.Client is null)
        {
            session.FriendInviteActive = false;
            await botClient.SendMessage(telegramId,
                "You don't have an account yet — please use /start to register first.",
                cancellationToken: cancellationToken);
            return;
        }

        if (email!.Equals(own.Client.Email, StringComparison.OrdinalIgnoreCase))
        {
            await botClient.SendMessage(telegramId,
                "🙂 That's your own email — enter your friend's email instead (/start to cancel).",
                cancellationToken: cancellationToken);
            return;
        }

        if (existing?.Client is not null)
        {
            session.FriendInviteActive = false;

            if (existing.Client.TgId <= 0)
            {
                await botClient.SendMessage(telegramId,
                    $"ℹ️ `{email}` was already invited and is still unclaimed — " +
                    "they'll link it when they start the bot.",
                    ParseMode.Markdown,
                    cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(telegramId,
                $"❌ `{email}` is already registered.",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);
            return;
        }

        var inviterEmail = own.Client.Email;
        var (enable, expiryTimeMs, trialEnds) = NewAccountState();
        var comment =
            $"Invited by {inviterEmail} · Referrer tgId: {telegramId} · Referred by: {inviterEmail}";

        if (trialEnds.HasValue) comment = ReferralService.WithTrialTag(comment, trialEnds.Value);

        try
        {
            await _panelClient.AddClientAsync(new CreateClientPayload(
                PanelClientDefaults.CreateClient(email, enable, expiryTimeMs, 0, comment),
                _telegramOptions.DefaultInboundIds.ToList()), cancellationToken);

            if (!enable) await _panelClient.BulkDisableClientsAsync([email], cancellationToken);
        }
        catch (PanelApiException ex)
        {
            _logger.LogError(ex, "Friend invite creation failed. Email: {Email}", email);
            await botClient.SendMessage(telegramId,
                "❌ Something went wrong creating the invite. Please try again.",
                cancellationToken: cancellationToken);
            return;
        }

        session.FriendInviteActive = false;
        _logger.LogInformation("Friend invite created. Email: {Email}, Inviter: {Inviter}", email, inviterEmail);

        await botClient.SendMessage(telegramId,
            $"✅ Invite created for `{email}` — they'll be linked as your referral " +
            "when they start the bot and enter this email.",
            ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task ClaimInvitedAccountAsync(ITelegramBotClient botClient, long telegramId, User? from,
        PanelClient invited, CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);
        var comment = ReferralService.AppendTag(invited.Comment, $"Telegram ID: {telegramId}");

        if (!string.IsNullOrWhiteSpace(from?.Username))
            comment = ReferralService.AppendTag(comment, $"@{from.Username}");

        try
        {
            await _panelClient.UpdateClientAsync(invited.Email,
                invited.ToUpdateRequest() with { TgId = telegramId, Comment = comment }, cancellationToken);
        }
        catch (PanelApiException ex)
        {
            _logger.LogError(ex, "Invite claim failed. Email: {Email}", invited.Email);
            await botClient.SendMessage(telegramId,
                "❌ Something went wrong linking your account. Please try /start again.",
                cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("Invite claimed. Email: {Email}, TelegramId: {TelegramId}",
            invited.Email, telegramId);

        await RetireOwnLegacyDuplicateAsync(telegramId, invited.Email, cancellationToken);

        session.PendingEmail = invited.Email;

        var hasReferrer = ReferralService.TryParseReferredBy(invited.Comment, out _, out _);

        if (!hasReferrer && !ReferralService.HasEverBeenProvisioned(invited))
        {
            session.RegistrationStep = RegistrationStep.AwaitingReferrer;
            session.CollectReferrer = true;
            session.PendingReferrerTgId = null;
            session.PendingReferrerEmail = null;
            await PromptReferrerAsync(botClient, telegramId, cancellationToken);
            return;
        }

        session.RegistrationStep = RegistrationStep.None;
        session.CollectReferrer = false;

        var userDetails = await GetUserDetails(telegramId, cancellationToken);

        if (userDetails is null)
        {
            await botClient.SendMessage(telegramId,
                "❌ We couldn't find your account yet. Please use /start again shortly.",
                cancellationToken: cancellationToken);
            return;
        }

        await RenderMainMenu(botClient, telegramId, userDetails, cancellationToken);
    }

    private async Task RetireOwnLegacyDuplicateAsync(long telegramId, string claimedEmail,
        CancellationToken cancellationToken)
    {
        PanelClient? legacy;
        try
        {
            legacy = (await _panelClient.GetClientByEmailAsync($"tg{telegramId}", cancellationToken))?.Client;
        }
        catch (Exception ex)
        {
            // Best effort only: unknown email surfaces as a failure, transport errors must not break the claim.
            _logger.LogWarning(ex, "Legacy duplicate check failed while claiming. TelegramId: {TelegramId}",
                telegramId);
            return;
        }

        if (legacy is null
            || legacy.TgId != telegramId
            || !ReferralService.IsLegacyEmail(legacy.Email)
            || legacy.Email.Equals(claimedEmail, StringComparison.OrdinalIgnoreCase)
            || ReferralService.HasEverBeenProvisioned(legacy))
            return;

        var tombstone = $"{legacy.Email}.merged-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        try
        {
            await _panelClient.UpdateClientAsync(legacy.Email,
                legacy.ToUpdateRequest() with { Email = tombstone, Enable = false }, cancellationToken);
            await _panelClient.BulkDisableClientsAsync([tombstone], cancellationToken);

            _logger.LogInformation("Retired legacy duplicate on claim. Old: {Old}, Tombstone: {New}",
                legacy.Email, tombstone);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retire legacy duplicate. Email: {Email}", legacy.Email);
        }
    }

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
            ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
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
            ParseMode.Markdown, MenuService.AdminMenu(),
            cancellationToken: cancellationToken);
    }

    private async Task HandleAdminCallback(ITelegramBotClient botClient, long telegramId, int messageId,
        string data, CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);

        if (data.StartsWith(MenuService.AdminPick + ":", StringComparison.Ordinal))
        {
            await PickAdminCandidate(botClient, telegramId, data, cancellationToken);
            return;
        }

        session.AdminCandidates = null;
        session.AdminCandidatesAction = AdminAction.None;

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
                    "🔍 Enter the account's **email or Telegram ID**:", ParseMode.Markdown,
                    MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminGrant:
                session.AdminAction = AdminAction.GrantTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "➕ Enter the account's **email or Telegram ID** to grant or extend:", ParseMode.Markdown,
                    MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminBan:
                session.AdminAction = AdminAction.BanTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "🚫 Enter the account's **email or Telegram ID** to ban:", ParseMode.Markdown,
                    MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminUnban:
                session.AdminAction = AdminAction.UnbanTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "✅ Enter the account's **email or Telegram ID** to unban:", ParseMode.Markdown,
                    MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminLimit:
                session.AdminAction = AdminAction.LimitTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "📱 Enter the account's **email or Telegram ID** to change device limit:", ParseMode.Markdown,
                    MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminReset:
                session.AdminAction = AdminAction.ResetTrafficTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "♻️ Enter the account's **email or Telegram ID** to reset traffic:", ParseMode.Markdown,
                    MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminLink:
                session.AdminAction = AdminAction.LinkTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "🔗 Enter the existing account's **email or Telegram ID**:", ParseMode.Markdown,
                    MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminNudge:
                session.AdminAction = AdminAction.NudgeTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "📨 Enter the account's **email or Telegram ID** to nudge:", ParseMode.Markdown,
                    MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminRegister:
                session.AdminAction = AdminAction.RegisterTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "➕ Enter the **new user's email** to register:", ParseMode.Markdown,
                    MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case MenuService.AdminChangeEmail:
                session.AdminAction = AdminAction.ChangeEmailTarget;
                await botClient.EditMessageText(telegramId, messageId,
                    "✏️ Enter the account's **current email or Telegram ID**:", ParseMode.Markdown,
                    MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
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
            await ExecuteAdminLink(botClient, telegramId, messageId, data, cancellationToken);

        if (data.StartsWith(MenuService.AdminConfirmNudge + ":", StringComparison.Ordinal))
            await ExecuteAdminNudge(botClient, telegramId, messageId, data, cancellationToken);

        if (data.StartsWith(MenuService.AdminConfirmChangeEmail + ":", StringComparison.Ordinal))
            await ExecuteAdminChangeEmail(botClient, telegramId, messageId, data, cancellationToken);
    }

    private async Task HandleAdminTextInput(ITelegramBotClient botClient, long telegramId, string text,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(telegramId);
        var input = text.Trim();

        switch (session.AdminAction)
        {
            case AdminAction.Lookup:
            case AdminAction.GrantTarget:
            case AdminAction.BanTarget:
            case AdminAction.UnbanTarget:
            case AdminAction.ResetTrafficTarget:
            case AdminAction.NudgeTarget:
            case AdminAction.LimitTarget:
            case AdminAction.LinkTarget:
            case AdminAction.ChangeEmailTarget:
                var resolvedTarget = await TryResolveAdminTargetAsync(botClient, telegramId, input,
                    cancellationToken);
                if (resolvedTarget is null) return;

                await ContinueAdminTargetAsync(botClient, telegramId, resolvedTarget, cancellationToken);
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
                    $"➡️ Grant **{days}** days to `{grantTarget}`?", ParseMode.Markdown,
                    replyMarkup: MenuService.AdminConfirmMenu(MenuService.AdminConfirmGrant, $"{grantTarget}:{days}"),
                    cancellationToken: cancellationToken);
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
                    $"➡️ Set device limit to **{limit}** for `{limitTarget}`?", ParseMode.Markdown,
                    replyMarkup: MenuService.AdminConfirmMenu(MenuService.AdminConfirmLimit, $"{limitTarget}:{limit}"),
                    cancellationToken: cancellationToken);
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
                    $"➡️ Link `{linkTarget}` to TG `{tgId}`?", ParseMode.Markdown,
                    replyMarkup: MenuService.AdminConfirmMenu(MenuService.AdminConfirmLink, $"{linkTarget}:{tgId}"),
                    cancellationToken: cancellationToken);
                return;

            case AdminAction.RegisterTarget:
                var newEmail = ReferralService.NormalizeEmail(input);

                if (!ReferralService.IsValidEmail(newEmail))
                {
                    await botClient.SendMessage(telegramId,
                        "❌ That doesn't look like a valid email address. Please try again.",
                        cancellationToken: cancellationToken);
                    return;
                }

                PanelClient? clash = await ResolveAdminTarget(newEmail!, cancellationToken);

                if (clash is not null)
                {
                    await botClient.SendMessage(telegramId,
                        $"❌ `{newEmail}` is already registered.",
                        ParseMode.Markdown, cancellationToken: cancellationToken);
                    return;
                }

                session.AdminTargetEmail = newEmail;
                session.AdminAction = AdminAction.RegisterReferrer;
                await botClient.SendMessage(telegramId,
                    $"➕ Register `{newEmail}`. Reply with the referrer's **email address**, or /skip for none:",
                    ParseMode.Markdown, cancellationToken: cancellationToken);
                return;

            case AdminAction.RegisterReferrer:
                var pendingEmail = session.AdminTargetEmail;

                if (pendingEmail is null)
                {
                    await ShowAdminMenu(botClient, telegramId, cancellationToken);
                    return;
                }

                if (input.Equals("/skip", StringComparison.OrdinalIgnoreCase))
                {
                    session.AdminAction = AdminAction.None;
                    session.AdminTargetEmail = null;
                    await CreateAdminRegisteredClientAsync(botClient, telegramId, pendingEmail, null, null,
                        cancellationToken);
                    return;
                }

                var candidate = ReferralService.NormalizeEmail(input);

                if (!ReferralService.IsValidEmail(candidate))
                {
                    await botClient.SendMessage(telegramId,
                        "❌ That doesn't look like a valid email address. Please try again, or /skip.",
                        cancellationToken: cancellationToken);
                    return;
                }

                PanelClient? directHit = null;
                var isTgIdInput = long.TryParse(candidate, out var refTgId) && refTgId > 0;

                if (isTgIdInput)
                {
                    try
                    {
                        directHit = (await _panelClient.GetClientByTelegramIdAsync(refTgId,
                            cancellationToken))?.Client;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Admin referrer lookup failed. Input: {Input}", candidate);
                        await botClient.SendMessage(telegramId,
                            "❌ Couldn't look that up right now. Please try again, or /skip.",
                            cancellationToken: cancellationToken);
                        return;
                    }

                    if (directHit is null)
                    {
                        await botClient.SendMessage(telegramId, $"❌ No client found for `{candidate}`.",
                            ParseMode.Markdown, cancellationToken: cancellationToken);
                        return;
                    }

                    await ContinueAdminTargetAsync(botClient, telegramId, directHit, cancellationToken);
                    return;
                }

                try
                {
                    directHit = (await _panelClient.GetClientByEmailAsync(candidate!,
                        cancellationToken))?.Client;
                }
                catch (PanelApiException)
                {
                    directHit = null;
                }

                if (directHit is not null)
                {
                    await ContinueAdminTargetAsync(botClient, telegramId, directHit, cancellationToken);
                    return;
                }

                var refMatches = await SearchClientEmailsAsync(botClient, telegramId, candidate!,
                    cancellationToken);
                if (refMatches is null) return;

                refMatches.RemoveAll(e => e.Equals(candidate, StringComparison.OrdinalIgnoreCase));

                if (refMatches.Count == 0)
                {
                    session.AdminAction = AdminAction.None;
                    session.AdminTargetEmail = null;
                    await botClient.SendMessage(telegramId,
                        $"ℹ️ `{candidate}` isn't registered — saving as a pending referrer. " +
                        "It counts if they register before the new user subscribes.",
                        ParseMode.Markdown, cancellationToken: cancellationToken);
                    await CreateAdminRegisteredClientAsync(botClient, telegramId, pendingEmail, null,
                        candidate, cancellationToken);
                    return;
                }

                if (refMatches.Count == 1)
                {
                    PanelClient? single = null;
                    try
                    {
                        single = (await _panelClient.GetClientByEmailAsync(refMatches[0],
                            cancellationToken))?.Client;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Admin referrer lookup failed. Email: {Email}", refMatches[0]);
                    }

                    if (single is null)
                    {
                        await botClient.SendMessage(telegramId,
                            "❌ Couldn't load that account right now. Please try again, or /skip.",
                            cancellationToken: cancellationToken);
                        return;
                    }

                    await ContinueAdminTargetAsync(botClient, telegramId, single, cancellationToken);
                    return;
                }

                await SendPickListAsync(botClient, telegramId, refMatches, cancellationToken);
                return;

            case AdminAction.ChangeEmailValue:
                var changeTarget = session.AdminTargetEmail;
                if (changeTarget is null)
                {
                    await ShowAdminMenu(botClient, telegramId, cancellationToken);
                    return;
                }

                var newAddress = ReferralService.NormalizeEmail(input);

                if (!ReferralService.IsValidEmail(newAddress))
                {
                    await botClient.SendMessage(telegramId,
                        "❌ That doesn't look like a valid email address. Please try again.",
                        cancellationToken: cancellationToken);
                    return;
                }

                session.AdminAction = AdminAction.None;
                session.AdminTargetEmail = null;
                await botClient.SendMessage(telegramId,
                    $"➡️ Change `{changeTarget}` to `{newAddress}`?", ParseMode.Markdown,
                    replyMarkup: MenuService.AdminConfirmMenu(MenuService.AdminConfirmChangeEmail,
                        $"{changeTarget}:{newAddress}"),
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

    private async Task<List<string>?> SearchClientEmailsAsync(ITelegramBotClient botClient, long adminId,
        string term, CancellationToken cancellationToken)
    {
        IReadOnlyList<PanelClientSummary> all;
        try
        {
            all = await _panelClient.GetAllClientsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin client search failed. Term: {Term}", term);
            await botClient.SendMessage(adminId,
                "❌ Couldn't search right now. Please try again shortly.",
                cancellationToken: cancellationToken);
            return null;
        }

        var lower = term.ToLowerInvariant();
        return all.Select(c => c.Email)
            .Where(e => e.ToLowerInvariant().Contains(lower))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task SendPickListAsync(ITelegramBotClient botClient, long adminId, List<string> matches,
        CancellationToken cancellationToken)
    {
        const int maxPicks = 10;
        var session = _sessionStore.Get(adminId);
        var shown = matches.Take(maxPicks).ToList();
        session.AdminCandidates = shown;
        session.AdminCandidatesAction = session.AdminAction;

        var rows = shown.Select((email, i) => new[]
        {
            InlineKeyboardButton.WithCallbackData(
                $"{i + 1}. {(email.Length > 32 ? email[..32] + "…" : email)}",
                $"{MenuService.AdminPick}:{i}")
        });

        var note = matches.Count > shown.Count
            ? $"\n\nShowing {shown.Count} of {matches.Count} — refine your search."
            : "";

        await botClient.SendMessage(adminId,
            $"🔍 Multiple matches:{note}\nPick one:",
            replyMarkup: new InlineKeyboardMarkup(rows),
            cancellationToken: cancellationToken);
    }

    private async Task<PanelClient?> TryResolveAdminTargetAsync(ITelegramBotClient botClient, long adminId,
        string input, CancellationToken cancellationToken)
    {
        if (long.TryParse(input, out var tgId) && tgId > 0)
        {
            PanelClient? byId = null;
            try
            {
                byId = (await _panelClient.GetClientByTelegramIdAsync(tgId, cancellationToken))?.Client;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Admin target lookup failed. Target: {Target}", input);
                await botClient.SendMessage(adminId,
                    "❌ Couldn't load that account right now. Please try again shortly.",
                    cancellationToken: cancellationToken);
                return null;
            }

            if (byId is null)
            {
                await botClient.SendMessage(adminId, $"❌ No client found for `{input}`.",
                    ParseMode.Markdown, cancellationToken: cancellationToken);
            }

            return byId;
        }

        try
        {
            var exact = (await _panelClient.GetClientByEmailAsync(input, cancellationToken))?.Client;
            if (exact is not null) return exact;
        }
        catch (PanelApiException ex)
        {
            _logger.LogWarning(ex, "Admin exact email lookup missed, falling back to search. Input: {Input}",
                input);
        }

        var matches = await SearchClientEmailsAsync(botClient, adminId, input, cancellationToken);
        if (matches is null) return null;

        matches.RemoveAll(e => e.Equals(input, StringComparison.OrdinalIgnoreCase));

        if (matches.Count == 0)
        {
            await botClient.SendMessage(adminId, $"❌ No client found for `{input}`.",
                ParseMode.Markdown, cancellationToken: cancellationToken);
            return null;
        }

        if (matches.Count == 1)
        {
            try
            {
                return (await _panelClient.GetClientByEmailAsync(matches[0], cancellationToken))?.Client;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Admin target lookup failed. Target: {Target}", matches[0]);
                await botClient.SendMessage(adminId,
                    "❌ Couldn't load that account right now. Please try again shortly.",
                    cancellationToken: cancellationToken);
                return null;
            }
        }

        await SendPickListAsync(botClient, adminId, matches, cancellationToken);
        return null;
    }

    private async Task ContinueAdminTargetAsync(ITelegramBotClient botClient, long adminId, PanelClient target,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(adminId);

        switch (session.AdminAction)
        {
            case AdminAction.Lookup:
                session.AdminAction = AdminAction.None;
                await ShowAdminLookupResult(botClient, adminId, target, cancellationToken);
                return;

            case AdminAction.GrantTarget:
                session.AdminTargetEmail = target.Email;
                session.AdminAction = AdminAction.GrantDays;
                await botClient.SendMessage(adminId,
                    $"➕ How many days to grant to **{target.Email}**?", ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case AdminAction.BanTarget:
            case AdminAction.UnbanTarget:
            case AdminAction.ResetTrafficTarget:
            case AdminAction.NudgeTarget:
                var action = session.AdminAction switch
                {
                    AdminAction.BanTarget => MenuService.AdminConfirmBan,
                    AdminAction.UnbanTarget => MenuService.AdminConfirmUnban,
                    AdminAction.NudgeTarget => MenuService.AdminConfirmNudge,
                    _ => MenuService.AdminConfirmReset
                };
                var emoji = session.AdminAction switch
                {
                    AdminAction.BanTarget => "🚫 Ban",
                    AdminAction.UnbanTarget => "✅ Unban",
                    AdminAction.NudgeTarget => "📨 Send reminder to",
                    _ => "♻️ Reset traffic"
                };

                session.AdminAction = AdminAction.None;
                await botClient.SendMessage(adminId,
                    $"{emoji} `{target.Email}`?", ParseMode.Markdown,
                    replyMarkup: MenuService.AdminConfirmMenu(action, target.Email),
                    cancellationToken: cancellationToken);
                return;

            case AdminAction.LimitTarget:
                session.AdminTargetEmail = target.Email;
                session.AdminAction = AdminAction.LimitValue;
                await botClient.SendMessage(adminId,
                    $"📱 How many connected devices for **{target.Email}**?", ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case AdminAction.LinkTarget:
                session.AdminTargetEmail = target.Email;
                session.AdminAction = AdminAction.LinkTelegramId;
                await botClient.SendMessage(adminId,
                    $"🔗 Enter the **Telegram ID** to link to `{target.Email}`:", ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case AdminAction.ChangeEmailTarget:
                session.AdminTargetEmail = target.Email;
                session.AdminAction = AdminAction.ChangeEmailValue;
                await botClient.SendMessage(adminId,
                    $"✏️ Enter the **new email** for `{target.Email}`:", ParseMode.Markdown,
                    replyMarkup: MenuService.AdminInputMenu(), cancellationToken: cancellationToken);
                return;

            case AdminAction.RegisterReferrer:
                var pendingEmail = session.AdminTargetEmail;
                if (pendingEmail is null)
                {
                    await ShowAdminMenu(botClient, adminId, cancellationToken);
                    return;
                }

                session.AdminAction = AdminAction.None;
                session.AdminTargetEmail = null;
                await CreateAdminRegisteredClientAsync(botClient, adminId, pendingEmail,
                    target.TgId > 0 ? target.TgId : null, target.Email, cancellationToken);
                return;

            default:
                await ShowAdminMenu(botClient, adminId, cancellationToken);
                return;
        }
    }

    private async Task PickAdminCandidate(ITelegramBotClient botClient, long adminId, string data,
        CancellationToken cancellationToken)
    {
        var session = _sessionStore.Get(adminId);

        if (!int.TryParse(data[(MenuService.AdminPick.Length + 1)..], out var index) ||
            session.AdminCandidates is null ||
            index < 0 || index >= session.AdminCandidates.Count ||
            session.AdminCandidatesAction != session.AdminAction ||
            session.AdminAction == AdminAction.None)
        {
            session.AdminCandidates = null;
            await botClient.SendMessage(adminId,
                "⚠️ That selection expired — please start again from the admin menu.",
                replyMarkup: MenuService.AdminMenu(),
                cancellationToken: cancellationToken);
            return;
        }

        var email = session.AdminCandidates[index];
        session.AdminCandidates = null;

        PanelClient? target = null;
        try
        {
            target = (await _panelClient.GetClientByEmailAsync(email, cancellationToken))?.Client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin pick lookup failed. Email: {Email}", email);
        }

        if (target is null)
        {
            await botClient.SendMessage(adminId, $"❌ No client found for `{email}`.",
                ParseMode.Markdown, cancellationToken: cancellationToken);
            return;
        }

        await ContinueAdminTargetAsync(botClient, adminId, target, cancellationToken);
    }

    private async Task ShowAdminLookupResult(ITelegramBotClient botClient, long adminId, PanelClient client,
        CancellationToken cancellationToken)
    {
        var expiry = client.ExpiryTime > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(client.ExpiryTime).ToUniversalTime().ToString("yyyy-MM-dd HH:mm")
              + " UTC"
            : "unlimited";

        var status = client.Enable
            ? client.ExpiryTime > 0 && client.ExpiryTime < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                ? "🧟 Expired"
                : "🟢 Active"
            : "🔴 Disabled";

        var referralState = BuildReferralStateLines(client.Comment);

        await botClient.SendMessage(adminId,
            $"*User lookup*\n\n" +
            $"👤 Email: `{client.Email}`\n" +
            $"🆔 Telegram ID: {(client.TgId > 0 ? client.TgId.ToString() : "—")}\n" +
            $"📌 Status: {status}\n" +
            $"⏳ Expires: {expiry}\n" +
            $"📱 Devices: {client.LimitHwid}\n" +
            $"💾 Quota: {SubscriptionFormatter.FormatGigabytes(client.TotalGB)} GB\n" +
            referralState +
            $"\nAccount managed by panel 🔑 {client.Uuid}",
            parseMode: ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
            cancellationToken: cancellationToken);
    }

    private static string BuildReferralStateLines(string? comment)
    {
        var lines = new List<string>();

        if (ReferralService.TryParseReferredBy(comment, out var referrerEmail, out var pending))
        {
            lines.Add(pending
                ? $"⏳ Referred by (pending): `{referrerEmail}`"
                : $"🎁 Referred by: `{referrerEmail}`");
        }

        if (ReferralService.TryParseReferrerTgId(comment, out var referrerTgId))
        {
            lines.Add($"🔗 Referrer TG ID: `{referrerTgId}`");
        }

        var creditDays = ReferralService.GetCreditDays(comment);
        if (creditDays > 0)
        {
            lines.Add($"💰 Referral credit: {creditDays}d");
        }

        if (ReferralService.HasBonusPaidMarker(comment))
        {
            lines.Add("✅ Referral bonus paid");
        }

        if (comment?.Contains("Referral bonus failed", StringComparison.OrdinalIgnoreCase) is true)
        {
            lines.Add("⚠️ Referral bonus failed");
        }

        if (ReferralService.IsCancelled(comment))
        {
            lines.Add("🚫 Cancelled (Tribute)");
        }

        return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
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
                ParseMode.Markdown,
                MenuService.AdminListMenu(page, totalPages),
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
        if (separator < 0) return (raw, "");

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
                ParseMode.Markdown, MenuService.AdminMenu(),
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
                $"🚫 Banned `{target}`.", ParseMode.Markdown,
                MenuService.AdminMenu(), cancellationToken: cancellationToken);
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
                $"✅ Unbanned `{target}`.", ParseMode.Markdown,
                MenuService.AdminMenu(), cancellationToken: cancellationToken);
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
                ParseMode.Markdown, MenuService.AdminMenu(),
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
                $"♻️ Traffic reset for `{target}`.", ParseMode.Markdown,
                MenuService.AdminMenu(), cancellationToken: cancellationToken);
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
                $"🔗 Linked `{target}` to TG `{tgId}`.", ParseMode.Markdown,
                MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Link failed. Target: {Target}", target);
            await botClient.EditMessageText(telegramId, messageId, "❌ Link failed. Please try again.",
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
    }

    private async Task ExecuteAdminNudge(ITelegramBotClient botClient, long telegramId, int messageId,
        string data, CancellationToken cancellationToken)
    {
        var (target, _) = ParseAdminConfirmTarget(data, MenuService.AdminConfirmNudge);

        PanelClient? client;
        try
        {
            client = await ResolveAdminTarget(target, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nudge target resolution failed. Target: {Target}", target);
            client = null;
        }

        if (client is null)
        {
            await botClient.EditMessageText(telegramId, messageId, $"❌ No client found for `{target}`.",
                ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
                cancellationToken: cancellationToken);
            return;
        }

        if (client.TgId <= 0)
        {
            await botClient.EditMessageText(telegramId, messageId,
                $"❌ `{target}` has no Telegram account linked, nothing to nudge.",
                ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
                cancellationToken: cancellationToken);
            return;
        }

        if (client.ExpiryTime <= 0)
        {
            await botClient.EditMessageText(telegramId, messageId,
                $"❌ `{target}` has no expiry date, nothing to nudge.",
                ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
                cancellationToken: cancellationToken);
            return;
        }

        var expiryDate =
            DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(client.ExpiryTime).UtcDateTime);
        var daysLeft = expiryDate.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
        var text = ExpiryReminderService.BuildReminderText(daysLeft, expiryDate,
            _telegramOptions.TributeSubscriptionUrl, _telegramOptions.SupportUrl);

        try
        {
            await botClient.SendMessage(client.TgId, text, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nudge send failed. Target: {Target}", target);
            await botClient.EditMessageText(telegramId, messageId,
                "❌ Couldn't message them (they may have blocked the bot).",
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
            return;
        }

        if (daysLeft is >= 0 and <= 3 &&
            !ReferralService.HasReminderMarker(client.Comment, daysLeft, expiryDate))
            try
            {
                await _panelClient.UpdateClientAsync(client.ToUpdateRequest() with
                {
                    Comment = ReferralService.AppendTag(client.Comment,
                        ReferralService.BuildReminderMarker(daysLeft, expiryDate))
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nudge marker update failed. Target: {Target}", target);
            }

        _logger.LogInformation("Nudge sent. Target: {Target}, DaysLeft: {DaysLeft}", target, daysLeft);
        await botClient.EditMessageText(telegramId, messageId,
            $"📨 Reminder sent to `{target}`.", ParseMode.Markdown,
            replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
    }

    private async Task CreateAdminRegisteredClientAsync(ITelegramBotClient botClient, long adminId,
        string email, long? referrerTgId, string? referrerEmail, CancellationToken cancellationToken)
    {
        var (enable, expiryTimeMs, trialEnds) = NewAccountState();
        var comment = "Registered by admin";

        if (referrerTgId is long tgId) comment = ReferralService.AppendTag(comment, $"Referrer tgId: {tgId}");

        if (referrerEmail is not null)
            comment = ReferralService.WithReferredBy(comment, referrerEmail, pending: referrerTgId is null);

        if (trialEnds.HasValue) comment = ReferralService.WithTrialTag(comment, trialEnds.Value);

        try
        {
            await _panelClient.AddClientAsync(new CreateClientPayload(
                PanelClientDefaults.CreateClient(email, enable, expiryTimeMs, 0, comment),
                _telegramOptions.DefaultInboundIds.ToList()), cancellationToken);

            if (!enable) await _panelClient.BulkDisableClientsAsync([email], cancellationToken);

            _logger.LogInformation("Admin registered account. Email: {Email}", email);

            await botClient.SendMessage(adminId,
                $"✅ Account created for `{email}` — they'll link it when they start the bot.",
                ParseMode.Markdown, replyMarkup: MenuService.AdminMenu(),
                cancellationToken: cancellationToken);
        }
        catch (PanelApiException ex)
        {
            _logger.LogError(ex, "Admin registration failed. Email: {Email}", email);
            await botClient.SendMessage(adminId,
                "❌ Registration failed. The email may have been taken just now — please try again.",
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
    }

    private async Task ExecuteAdminChangeEmail(ITelegramBotClient botClient, long telegramId, int messageId,
        string data, CancellationToken cancellationToken)
    {
        var (target, payload) = ParseAdminConfirmTarget(data, MenuService.AdminConfirmChangeEmail);

        try
        {
            await _adminPanelService.ChangeEmailAsync(target, payload, cancellationToken);

            await botClient.EditMessageText(telegramId, messageId,
                $"✏️ Changed `{target}` to `{payload}`.", ParseMode.Markdown,
                replyMarkup: MenuService.AdminMenu(), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change email failed. Target: {Target}", target);
            await botClient.EditMessageText(telegramId, messageId, "❌ Change email failed. Please try again.",
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
                    _telegramOptions.TributeSubscriptionUrl, IsAdmin(telegramId)),
                cancellationToken: cancellationToken);
            return;
        }

        if (!string.IsNullOrEmpty(session.MainMenuText))
        {
            _logger.LogWarning("Panel fetch failed on Back; restoring cached main menu. TelegramId: {TelegramId}",
                telegramId);

            await botClient.EditMessageText(telegramId, messageId, session.MainMenuText,
                replyMarkup: MenuService.MainMenu(session.UserStatus, session.SubscriptionUrl,
                    _telegramOptions.TributeSubscriptionUrl, IsAdmin(telegramId)),
                cancellationToken: cancellationToken);
            return;
        }

        await botClient.EditMessageText(telegramId, messageId,
            "❌ Account not found. Please use /start to re-register.",
            cancellationToken: cancellationToken);
    }
}