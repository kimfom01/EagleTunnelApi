using EagleTunnelApi.Telegram;
using EagleTunnelApi.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Xunit;

namespace EagleTunnelApi.Tests.Telegram;

public class TelegramWebhookProcessorTests
{
    [Fact]
    public async Task ProcessAsync_DuplicateUpdateId_HandlesOnlyOnce()
    {
        var handler = new RecordingUpdateHandler();
        var processor = CreateProcessor(handler);

        await processor.ProcessAsync(new Update { Id = 42 }, CancellationToken.None);
        await processor.ProcessAsync(new Update { Id = 42 }, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ProcessAsync_HandlerThrows_DoesNotThrow()
    {
        var handler = new RecordingUpdateHandler { Exception = new InvalidOperationException("boom") };
        var processor = CreateProcessor(handler);

        await processor.ProcessAsync(new Update { Id = 43 }, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ProcessAsync_NonPositiveUpdateId_IsNotDeduplicated()
    {
        var handler = new RecordingUpdateHandler();
        var processor = CreateProcessor(handler);

        await processor.ProcessAsync(new Update { Id = 0 }, CancellationToken.None);
        await processor.ProcessAsync(new Update { Id = 0 }, CancellationToken.None);

        Assert.Equal(2, handler.Calls);
    }

    private static TelegramWebhookProcessor CreateProcessor(IUpdateHandler handler)
    {
        return new TelegramWebhookProcessor(handler, new FakeTelegramBotClient(),
            new TelegramUpdateDeduplicator(), NullLogger<TelegramWebhookProcessor>.Instance);
    }

    private sealed class RecordingUpdateHandler : IUpdateHandler
    {
        public int Calls { get; private set; }

        public Exception? Exception { get; init; }

        public Task HandleUpdateAsync(ITelegramBotClient botClient, Update update,
            CancellationToken cancellationToken)
        {
            Calls++;

            if (Exception is not null) throw Exception;

            return Task.CompletedTask;
        }

        public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception,
            HandleErrorSource source, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
