using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Webhook.Exceptions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace EagleTunnelApi.Telegram;

public interface IAdminPanelService
{
    Task<PanelClient?> GetByTargetAsync(string target, CancellationToken cancellationToken);

    Task<IReadOnlyList<PanelClientSummary>> ListClientsAsync(CancellationToken cancellationToken);

    Task GrantAsync(string email, int days, CancellationToken cancellationToken);

    Task BanAsync(string email, CancellationToken cancellationToken);

    Task UnbanAsync(string email, CancellationToken cancellationToken);

    Task SetDeviceLimitAsync(string email, int limit, CancellationToken cancellationToken);

    Task ResetTrafficAsync(string email, CancellationToken cancellationToken);

    Task LinkToTelegramAsync(string email, long telegramId, CancellationToken cancellationToken);

    Task ChangeEmailAsync(string oldEmail, string newEmail, CancellationToken cancellationToken);

    /// <summary>
    /// Pays the referral bonus if <paramref name="refereeBefore"/> shows a genuinely
    /// new subscriber (never provisioned) carrying unpaid referral attribution.
    /// Safe to call after any first paid activation: webhook, manual grant, or sweep.
    /// Returns true when a bonus was granted.
    /// </summary>
    Task<bool> TrySettleReferralBonusAsync(long refereeTgId, PanelClient? refereeBefore,
        CancellationToken cancellationToken);

    /// <summary>
    /// Settles the referral bonus for an admin-created account that was granted
    /// paid days immediately (TgId is still 0/unclaimed). Only pays when the
    /// new account is already provisioned (granted, non-trial) with referral
    /// attribution. Returns true when a bonus was granted.
    /// </summary>
    Task<bool> TrySettleReferralBonusForEmailAsync(string refereeEmail, CancellationToken cancellationToken);

    /// <summary>
    /// Settles bonuses for attributed accounts that were activated outside the
    /// webhook flow (e.g. extended directly in the 3X-UI dashboard).
    /// Returns the number of bonuses granted.
    /// </summary>
    Task<int> SettlePendingReferralBonusesAsync(CancellationToken cancellationToken);
}

public class AdminPanelService(
    ILogger<AdminPanelService> logger,
    IPanelClient panelClient,
    ITelegramBotClient botClient,
    IOptions<TelegramOptions> telegramOptions) : IAdminPanelService
{
    public async Task<PanelClient?> GetByTargetAsync(string target, CancellationToken cancellationToken)
    {
        if (long.TryParse(target, out var tgId) && tgId > 0)
        {
            var response = await panelClient.GetClientByTelegramIdAsync(tgId, cancellationToken);
            return response?.Client;
        }

        var responseByEmail = await panelClient.GetClientByEmailAsync(target, cancellationToken);
        return responseByEmail?.Client;
    }

    public async Task<IReadOnlyList<PanelClientSummary>> ListClientsAsync(CancellationToken cancellationToken)
    {
        var clients = await panelClient.GetAllClientsAsync(cancellationToken);
        return clients.OrderBy(c => c.Email).ToList();
    }

    public async Task GrantAsync(string email, int days, CancellationToken cancellationToken)
    {
        var before = await RequireClient(email, cancellationToken);

        await GrantCoreAsync(before, days, cancellationToken);

        // A manual grant is a paid activation like any other: a referred first-time
        // payer earns their referrer the bonus even without a Tribute webhook.
        await TrySettleReferralBonusAsync(before.TgId, before, cancellationToken);
    }

    /// <summary>
    /// Extends access without settling referrals. Used for the referrer payout
    /// itself so bonuses never chain up the referral graph.
    /// </summary>
    private async Task GrantCoreAsync(PanelClient client, int days, CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var currentExpiry = client.ExpiryTime > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(client.ExpiryTime).ToUniversalTime()
            : DateTimeOffset.MinValue;

        var baseDate = client.Enable
            ? currentExpiry > nowUtc ? currentExpiry : nowUtc
            : nowUtc;

        var newExpiryMs = baseDate.AddDays(days).ToUnixTimeMilliseconds();

        var comment = ReferralService.HasTrialTag(client.Comment)
            ? ReferralService.ClearTrialTag(client.Comment)
            : client.Comment;

        await panelClient.UpdateClientAsync(client.ToUpdateRequest() with
        {
            Enable = true,
            ExpiryTime = newExpiryMs,
            Comment = comment
        }, cancellationToken);

        logger.LogInformation("Granted {Days} days to client. Email: {Email}, NewExpiry: {NewExpiry}",
            days, client.Email, newExpiryMs);
    }

    public async Task BanAsync(string email, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        await panelClient.BulkDisableClientsAsync([client.Email], cancellationToken);

        logger.LogInformation("Banned client. Email: {Email}", client.Email);
    }

    public async Task UnbanAsync(string email, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        await panelClient.BulkEnableClientsAsync([client.Email], cancellationToken);

        logger.LogInformation("Unbanned client. Email: {Email}", client.Email);
    }

    public async Task SetDeviceLimitAsync(string email, int limit, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        await panelClient.UpdateClientAsync(client.ToUpdateRequest() with { LimitHwid = limit }, cancellationToken);

        logger.LogInformation("Set device limit to {Limit} for client. Email: {Email}", limit, client.Email);
    }

    public async Task ResetTrafficAsync(string email, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        await panelClient.ResetClientTrafficAsync(client.Email, cancellationToken);

        logger.LogInformation("Reset traffic for client. Email: {Email}", client.Email);
    }

    public async Task LinkToTelegramAsync(string email, long telegramId, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        await panelClient.UpdateClientAsync(client.ToUpdateRequest() with { TgId = telegramId }, cancellationToken);

        logger.LogInformation("Linked client to telegram. Email: {Email}, TelegramId: {TelegramId}", client.Email,
            telegramId);
    }

    public async Task ChangeEmailAsync(string oldEmail, string newEmail, CancellationToken cancellationToken)
    {
        var client = await RequireClient(oldEmail, cancellationToken);

        PanelClientResponse? clash = null;
        try
        {
            clash = await panelClient.GetClientByEmailAsync(newEmail, cancellationToken);
        }
        catch (PanelApiException ex)
        {
            logger.LogWarning(ex, "Email clash check failed, assuming available. Email: {Email}", newEmail);
        }

        if (clash?.Client is not null)
            throw new PanelApiException($"Email {newEmail} is already registered.");

        await panelClient.UpdateClientAsync(oldEmail,
            client.ToUpdateRequest() with { Email = newEmail }, cancellationToken);

        logger.LogInformation("Changed client email. OldEmail: {Old}, NewEmail: {New}", oldEmail, newEmail);
    }

    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;

    public Task<bool> TrySettleReferralBonusAsync(long refereeTgId, PanelClient? refereeBefore,
        CancellationToken cancellationToken)
    {
        // Called with the genuine pre-activation snapshot: only a never-provisioned
        // (new or trial) subscriber can trigger a first-payment payout.
        return SettleCoreAsync(refereeTgId, refereeBefore, requireFirstActivation: true, cancellationToken);
    }

    public async Task<bool> TrySettleReferralBonusForEmailAsync(string refereeEmail,
        CancellationToken cancellationToken)
    {
        var bonusDays = _telegramOptions.ReferralBonusDays;
        if (bonusDays <= 0 || string.IsNullOrWhiteSpace(refereeEmail)) return false;

        PanelClient? referee;
        try
        {
            referee = (await panelClient.GetClientByEmailAsync(refereeEmail, cancellationToken))?.Client;
        }
        catch (PanelApiException ex)
        {
            logger.LogWarning(ex, "Referral settlement lookup failed. Email: {Email}", refereeEmail);
            return false;
        }

        if (referee is null) return false;
        if (ReferralService.HasBonusPaidMarker(referee.Comment)) return false;

        // Immediate admin grants create provisioned (enabled, non-trial) accounts.
        // Trial/disabled creates stay pending until the referee links and subscribes.
        if (!ReferralService.HasEverBeenProvisioned(referee)) return false;

        if (!ReferralService.TryParseReferredBy(referee.Comment, out var referrerEmail, out _))
            return false;

        PanelClient? referrer = null;
        try
        {
            if (ReferralService.TryParseReferrerTgId(referee.Comment, out var referrerTgId))
                referrer = (await panelClient.GetClientByTelegramIdAsync(referrerTgId, cancellationToken))?.Client;

            referrer ??= (await panelClient.GetClientByEmailAsync(referrerEmail, cancellationToken))?.Client;
        }
        catch (PanelApiException ex)
        {
            logger.LogWarning(ex, "Referrer resolution failed, staying pending. Email: {Email}", referrerEmail);
            return false;
        }

        if (referrer is null)
        {
            logger.LogInformation("Referrer not registered yet, staying pending. Email: {Email}", referrerEmail);
            return false;
        }

        if (referrer.Email.Equals(referee.Email, StringComparison.OrdinalIgnoreCase) ||
            (referee.TgId > 0 && referrer.TgId == referee.TgId))
        {
            logger.LogWarning("Ignoring self-referral at payout. Referee: {Email}", referee.Email);
            return false;
        }

        try
        {
            await GrantCoreAsync(referrer, bonusDays, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Referral grant failed. Referrer: {Email}", referrer.Email);
            await TagBonusFailedByEmailAsync(referee.Email, cancellationToken);
            return false;
        }

        logger.LogInformation("Referral bonus granted. Referrer: {Email}, Days: {Days}, Referee: {Referee}",
            referrer.Email, bonusDays, referee.Email);

        await MarkBonusPaidByEmailAsync(referee.Email, cancellationToken);
        await AddReferrerCreditAsync(referrer.Email, bonusDays, cancellationToken);
        await NotifyReferrerAsync(referrer.TgId, bonusDays, cancellationToken);

        return true;
    }

    public async Task<int> SettlePendingReferralBonusesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PanelClientSummary> summaries;
        try
        {
            summaries = await panelClient.GetAllClientsAsync(cancellationToken);
        }
        catch (PanelApiException ex)
        {
            logger.LogError(ex, "Failed to list clients for referral settlement.");
            return 0;
        }

        var settled = 0;

        foreach (var summary in summaries)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (ReferralService.IsLegacyEmail(summary.Email)) continue;

            PanelClient? client;
            try
            {
                client = (await panelClient.GetClientByEmailAsync(summary.Email, cancellationToken))?.Client;
            }
            catch (PanelApiException ex)
            {
                logger.LogWarning(ex, "Referral settlement lookup failed. Email: {Email}", summary.Email);
                continue;
            }

            if (client is null || client.TgId <= 0) continue;

            if (!ReferralService.TryParseReferredBy(client.Comment, out _, out _)) continue;

            if (ReferralService.HasBonusPaidMarker(client.Comment)) continue;

            // Only accounts that converted to paid access without a payout qualify.
            // Never-provisioned accounts are settled by their activation path instead.
            if (!ReferralService.HasEverBeenProvisioned(client)) continue;

            try
            {
                // The pre-activation snapshot is unavailable here; the filters above
                // (attributed + paid + unpaid) stand in for the first-activation check.
                if (await SettleCoreAsync(client.TgId, client, requireFirstActivation: false, cancellationToken))
                    settled++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Referral settlement failed. Email: {Email}", client.Email);
            }
        }

        return settled;
    }

    private async Task<bool> SettleCoreAsync(long refereeTgId, PanelClient? refereeBefore,
        bool requireFirstActivation, CancellationToken cancellationToken)
    {
        var bonusDays = _telegramOptions.ReferralBonusDays;

        if (bonusDays <= 0 || refereeTgId <= 0 || refereeBefore is null) return false;

        if (ReferralService.HasBonusPaidMarker(refereeBefore.Comment)) return false;

        // Webhook and manual-grant callers hold the pre-activation snapshot, so a
        // genuinely new subscriber is required. The sweep pre-filters instead
        // (attributed + converted to paid + still unpaid) and skips this check.
        if (requireFirstActivation && ReferralService.HasEverBeenProvisioned(refereeBefore)) return false;

        if (!ReferralService.TryParseReferredBy(refereeBefore.Comment, out var referrerEmail, out _))
            return false;

        PanelClient? referrer = null;
        try
        {
            if (ReferralService.TryParseReferrerTgId(refereeBefore.Comment, out var referrerTgId))
                referrer = (await panelClient.GetClientByTelegramIdAsync(referrerTgId, cancellationToken))?.Client;

            referrer ??= (await panelClient.GetClientByEmailAsync(referrerEmail, cancellationToken))?.Client;
        }
        catch (PanelApiException ex)
        {
            logger.LogWarning(ex, "Referrer resolution failed, staying pending. Email: {Email}", referrerEmail);
            return false;
        }

        if (referrer is null)
        {
            logger.LogInformation("Referrer not registered yet, staying pending. Email: {Email}", referrerEmail);
            return false;
        }

        if (referrer.TgId == refereeTgId ||
            referrer.Email.Equals(refereeBefore.Email, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Ignoring self-referral at payout. Referee: {Email}", refereeBefore.Email);
            return false;
        }

        try
        {
            await GrantCoreAsync(referrer, bonusDays, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Referral grant failed. Referrer: {Email}", referrer.Email);
            await TagBonusFailedAsync(refereeTgId, cancellationToken);
            return false;
        }

        logger.LogInformation("Referral bonus granted. Referrer: {Email}, Days: {Days}, Referee: {Referee}",
            referrer.Email, bonusDays, refereeBefore.Email);

        await MarkBonusPaidAsync(refereeTgId, cancellationToken);
        await AddReferrerCreditAsync(referrer.Email, bonusDays, cancellationToken);
        await NotifyReferrerAsync(referrer.TgId, bonusDays, cancellationToken);

        return true;
    }

    private async Task TagBonusFailedAsync(long refereeTgId, CancellationToken cancellationToken)
    {
        try
        {
            var fresh = (await panelClient.GetClientByTelegramIdAsync(refereeTgId, cancellationToken))?.Client;

            if (fresh is not null && !ReferralService.HasBonusPaidMarker(fresh.Comment))
                await panelClient.UpdateClientAsync(fresh.ToUpdateRequest() with
                {
                    Comment = ReferralService.AppendTag(fresh.Comment, "Referral bonus failed")
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to tag bonus failure. TelegramUserId: {TelegramUserId}", refereeTgId);
        }
    }

    private async Task MarkBonusPaidAsync(long refereeTgId, CancellationToken cancellationToken)
    {
        try
        {
            var fresh = (await panelClient.GetClientByTelegramIdAsync(refereeTgId, cancellationToken))?.Client;

            if (fresh is not null && !ReferralService.HasBonusPaidMarker(fresh.Comment))
                await panelClient.UpdateClientAsync(fresh.ToUpdateRequest() with
                {
                    Comment = ReferralService.AppendTag(fresh.Comment, ReferralService.BonusPaidMarker)
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark bonus paid. TelegramUserId: {TelegramUserId}", refereeTgId);
        }
    }

    private async Task TagBonusFailedByEmailAsync(string refereeEmail, CancellationToken cancellationToken)
    {
        try
        {
            var fresh = (await panelClient.GetClientByEmailAsync(refereeEmail, cancellationToken))?.Client;

            if (fresh is not null && !ReferralService.HasBonusPaidMarker(fresh.Comment))
                await panelClient.UpdateClientAsync(fresh.ToUpdateRequest() with
                {
                    Comment = ReferralService.AppendTag(fresh.Comment, "Referral bonus failed")
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to tag bonus failure. Email: {Email}", refereeEmail);
        }
    }

    private async Task MarkBonusPaidByEmailAsync(string refereeEmail, CancellationToken cancellationToken)
    {
        try
        {
            var fresh = (await panelClient.GetClientByEmailAsync(refereeEmail, cancellationToken))?.Client;

            if (fresh is not null && !ReferralService.HasBonusPaidMarker(fresh.Comment))
                await panelClient.UpdateClientAsync(fresh.ToUpdateRequest() with
                {
                    Comment = ReferralService.AppendTag(fresh.Comment, ReferralService.BonusPaidMarker)
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark bonus paid. Email: {Email}", refereeEmail);
        }
    }

    private async Task AddReferrerCreditAsync(string referrerEmail, int bonusDays,
        CancellationToken cancellationToken)
    {
        try
        {
            var fresh = (await panelClient.GetClientByEmailAsync(referrerEmail, cancellationToken))?.Client;

            if (fresh is not null)
                await panelClient.UpdateClientAsync(fresh.ToUpdateRequest() with
                {
                    Comment = ReferralService.AddCreditDays(fresh.Comment, bonusDays)
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record referrer credit. Referrer: {Email}", referrerEmail);
        }
    }

    private async Task NotifyReferrerAsync(long referrerTgId, int bonusDays, CancellationToken cancellationToken)
    {
        if (referrerTgId <= 0) return;

        try
        {
            await botClient.SendMessage(referrerTgId,
                $"🎉 Good news! Someone subscribed with your invite link — **+{bonusDays} days** of VPN time added.\n\n" +
                "Tip: cancel your Tribute renewal so you aren't billed while covered — " +
                $"message {_telegramOptions.SupportUrl} if you'd like the step-by-step video guide.",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify referrer. TelegramUserId: {TelegramUserId}", referrerTgId);
        }
    }

    private async Task<PanelClient> RequireClient(string email, CancellationToken cancellationToken)
    {
        var response = await panelClient.GetClientByEmailAsync(email, cancellationToken);

        if (response?.Client is null) throw new PanelApiException($"No client found for email {email}.");

        return response.Client;
    }
}