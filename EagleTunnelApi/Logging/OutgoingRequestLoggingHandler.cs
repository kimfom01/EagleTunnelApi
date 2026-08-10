using System.Diagnostics;
using EagleTunnelApi.Configuration;
using Microsoft.Extensions.Options;

namespace EagleTunnelApi.Logging;

public sealed class OutgoingRequestLoggingHandler(IOptions<TelegramOptions> telegramOptions,
    ILogger<OutgoingRequestLoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = telegramOptions.Value.BotToken;
        var requestId = Guid.NewGuid().ToString("N");

        using (logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = requestId }))
        {
            var redactedUri = RedactUri(request.RequestUri, token);

            logger.LogInformation("Outgoing {Method} {Uri} started", request.Method, redactedUri);

            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.LogError(ex, "Outgoing {Method} {Uri} failed after {ElapsedMs}ms", request.Method,
                    redactedUri, stopwatch.ElapsedMilliseconds);
                throw;
            }

            stopwatch.Stop();
            logger.LogInformation("Outgoing {Method} {Uri} completed with status {StatusCode} in {ElapsedMs}ms",
                request.Method, redactedUri, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);

            return response;
        }
    }

    private static string RedactUri(Uri? uri, string? botToken)
    {
        var value = uri?.ToString() ?? "(null)";

        if (!string.IsNullOrEmpty(botToken))
        {
            value = value.Replace($"/bot{botToken}/", "/bot{REDACTED}/", StringComparison.Ordinal);
        }

        return value;
    }
}