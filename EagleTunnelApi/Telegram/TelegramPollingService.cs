using Telegram.Bot;
using Telegram.Bot.Polling;

namespace EagleTunnelApi.Telegram;

public sealed class TelegramPollingService(ITelegramBotClient botClient, IUpdateHandler updateHandler,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            DropPendingUpdates = true
        };

        botClient.StartReceiving(updateHandler, receiverOptions, stoppingToken);

        logger.LogInformation("Telegram bot polling started");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}