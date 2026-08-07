using System.Net;
using System.Text;
using System.Text.Json;

namespace EagleTunnelApi.Tests.Helpers;

public sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(responder(request));
    }

    public static HttpResponseMessage Json(HttpStatusCode statusCode, object body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage Json(HttpStatusCode statusCode, string rawJson)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage Json(string rawJson) => Json(HttpStatusCode.OK, rawJson);
}
