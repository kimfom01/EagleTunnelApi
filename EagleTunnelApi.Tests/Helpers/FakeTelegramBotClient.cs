using System.Net.Http.Headers;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace EagleTunnelApi.Tests.Helpers;

public sealed class FakeTelegramBotClient : ITelegramBotClient
{
    public List<(string MethodName, string Body)> Requests { get; } = [];
    public bool LocalBotServer { get; } = false;

    public long BotId { get; } = 0;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public IExceptionParser ExceptionsParser { get; set; } = default!;

    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest
    {
        add { }
        remove { }
    }

    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived
    {
        add { }
        remove { }
    }

    public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        using var content = request.ToHttpContent() ??
            throw new InvalidOperationException($"Request produced no HTTP content: {request.MethodName}");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var body = content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

        Requests.Add((request.MethodName, body));

        TResponse result = default!;
        return Task.FromResult(result);
    }

    public Task<bool> TestApi(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}