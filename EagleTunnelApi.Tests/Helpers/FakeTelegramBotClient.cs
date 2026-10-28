using System.Net.Http.Headers;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace EagleTunnelApi.Tests.Helpers;

public sealed class FakeTelegramBotClient : ITelegramBotClient
{
    private readonly object _sync = new();

    private readonly List<(Func<string, string, bool> Predicate, TaskCompletionSource<bool> Completion)>
        _waiters = [];

    public List<(string MethodName, string Body)> Requests { get; } = [];

    /// <summary>
    /// Optional failure hook for tests that need a request to fail after it has
    /// been recorded. Return null to let the request succeed.
    /// </summary>
    public Func<string, string, Exception?>? FailureFactory { get; set; }

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

    /// <summary>
    /// Completes when a request whose method/body matches <paramref name="predicate"/>
    /// has been sent, including requests sent by detached background work.
    /// </summary>
    public Task WaitForAsync(Func<string, string, bool> predicate)
    {
        lock (_sync)
        {
            if (Requests.Any(request => predicate(request.MethodName, request.Body))) return Task.CompletedTask;

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _waiters.Add((predicate, completion));

            return completion.Task;
        }
    }

    public Task WaitForBodyAsync(string fragment)
    {
        return WaitForAsync((_, body) => body.Contains(fragment, StringComparison.Ordinal));
    }

    public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        using var content = request.ToHttpContent() ??
            throw new InvalidOperationException($"Request produced no HTTP content: {request.MethodName}");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var body = content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

        List<TaskCompletionSource<bool>> completed;

        lock (_sync)
        {
            Requests.Add((request.MethodName, body));

            completed = [];

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Predicate(request.MethodName, body)) continue;

                completed.Add(_waiters[i].Completion);
                _waiters.RemoveAt(i);
            }
        }

        foreach (var completion in completed) completion.TrySetResult(true);

        if (FailureFactory?.Invoke(request.MethodName, body) is { } failure) throw failure;

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
