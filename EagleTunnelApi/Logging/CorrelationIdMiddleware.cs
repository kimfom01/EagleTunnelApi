namespace EagleTunnelApi.Logging;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context, ILogger<CorrelationIdMiddleware> logger)
    {
        var correlationId = context.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrEmpty(correlationId)) correlationId = Guid.NewGuid().ToString("N");

        context.Response.Headers[HeaderName] = correlationId;

        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "/";

        using (logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId }))
        {
            logger.LogInformation("HTTP {Method} {Path} started", method, path);

            try
            {
                await next(context);
            }
            finally
            {
                logger.LogInformation("HTTP {Method} {Path} completed with status {StatusCode}", method, path,
                    context.Response.StatusCode);
            }
        }
    }
}