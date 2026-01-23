using System.Security.Cryptography;
using System.Text;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;

namespace EagleTunnelApi.Webhook.Security;

public interface IVerifier
{
    Task<WebhookEvent?> VerifySignature(HttpRequest request);
}

public class Verifier : IVerifier
{
    private readonly IConfiguration _config;

    public Verifier(IConfiguration config)
    {
        _config = config;
    }

    public async Task<WebhookEvent?> VerifySignature(HttpRequest request)
    {
        var apiKey = _config.GetValue<string>("Tribute:ApiKey");
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidPayloadException();

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        if (!request.Headers.TryGetValue("trbt-signature", out var signatureHeader))
            throw new InvalidSignatureException();

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

        if (!computedSignature.Equals(signatureHeader.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidSignatureException();

        var webhookEvent = System.Text.Json.JsonSerializer.Deserialize<WebhookEvent>(body);

        return webhookEvent;
    }
}