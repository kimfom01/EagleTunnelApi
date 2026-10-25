using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

namespace EagleTunnelApi.Telegram;

/// <summary>
/// Dispatches webhook updates without letting a slow or failing handler turn
/// into Telegram's redelivery loop: duplicates are dropped, and handler errors
/// are logged and acknowledged instead of returning a non-2xx response.
/// Admin actions are retryable by the user; the daily settlement sweep is the
/// backstop for referral payouts.
/// </summary>
public sealed class TelegramWebhookProcessor(
    IUpdateHandler updateHandler,
    ITelegramBotClient botClient,
    TelegramUpdateDeduplicator deduplicator,
    ILogger<TelegramWebhookProcessor> logger)
{
    public async Task ProcessAsync(Update update, CancellationToken cancellationToken)
    {
        if (!deduplicator.TryRegister(update.Id))
        {
            logger.LogWarning("Ignoring duplicate Telegram update. UpdateId: {UpdateId}", update.Id);
            return;
        }

        try
        {
            await updateHandler.HandleUpdateAsync(botClient, update, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Telegram update handling cancelled. UpdateId: {UpdateId}", update.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telegram update handling failed. UpdateId: {UpdateId}, Type: {Type}",
                update.Id, update.Type);
        }
    }
}
