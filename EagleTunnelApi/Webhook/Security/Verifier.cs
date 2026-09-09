using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using Microsoft.Extensions.Options;

namespace EagleTunnelApi.Webhook.Security;

public interface IVerifier
{
    Task<WebhookEvent?> VerifySignature(HttpRequest request);
}

public class Verifier(IOptions<TributeOptions> tributeOptions, ILogger<Verifier> logger) : IVerifier
{
    public async Task<WebhookEvent?> VerifySignature(HttpRequest request)
    {
        logger.LogInformation("Verifying Signature: {@Time}", DateTime.UtcNow);

        var apiKey = tributeOptions.Value.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            logger.LogError("Tribute API Key Not Found In Config: {@Time}", DateTime.UtcNow);
            throw new NotFoundException();
        }

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        if (!request.Headers.TryGetValue("trbt-signature", out var signatureHeader))
        {
            logger.LogError("Signature Not Found In Headers: {@Time}", DateTime.UtcNow);
            throw new InvalidSignatureException();
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));

        if (!SignaturesMatch(computedHash, signatureHeader.ToString()))
        {
            logger.LogError("Invalid Signature: {@Time}", DateTime.UtcNow);
            throw new InvalidSignatureException();
        }

        logger.LogInformation("Signature Verified: {@Time}", DateTime.UtcNow);

        WebhookEvent? webhookEvent;
        try
        {
            webhookEvent = JsonSerializer.Deserialize<WebhookEvent>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            logger.LogError("Invalid JSON Payload: {@Time}", DateTime.UtcNow);
            return null;
        }

        logger.LogInformation("Webhook Event: {@WebhookEvent}", webhookEvent);

        return webhookEvent;
    }

    private static bool SignaturesMatch(byte[] computed, string provided)
    {
        try
        {
            var providedBytes = Convert.FromHexString(provided);
            return CryptographicOperations.FixedTimeEquals(computed, providedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}