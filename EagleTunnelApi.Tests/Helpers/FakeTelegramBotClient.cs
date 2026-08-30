using System.Net.Http.Headers;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace EagleTunnelApi.Tests.Helpers;

public sealed class FakeTelegramBotClient : ITelegramBotClient
{
    public bool LocalBotServer { get; } = false;

    public long BotId { get; } = 0;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public global::Telegram.Bot.Exceptions.IExceptionParser ExceptionsParser { get; set; } = default!;

    public List<(string MethodName, string Body)> Requests { get; } = [];

    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest;
    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived;

    public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        using var content = request.ToHttpContent();
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var body = content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

        Requests.Add((request.MethodName, body));

        return Task.FromResult(default(TResponse)!)!;
    }

    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal static class RequestExtensions
{
    public static HttpContent ToContentStream<TResponse>(this IRequest<TResponse> request) =>
        request.ToHttpContent();
}