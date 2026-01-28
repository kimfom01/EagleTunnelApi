using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly ILogger<Verifier> _logger;

    public Verifier(IConfiguration config, ILogger<Verifier> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<WebhookEvent?> VerifySignature(HttpRequest request)
    {
        _logger.LogInformation("Verifying Signature: {@Time}", DateTime.UtcNow);

        var apiKey = _config.GetValue<string>("Tribute:ApiKey");
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("Tribute API Key Not Found In Config: {@Time}", DateTime.UtcNow);
            throw new NotFoundException();
        }

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;

        if (!request.Headers.TryGetValue("trbt-signature", out var signatureHeader))
        {
            _logger.LogError("Signature Not Found In Headers: {@Time}", DateTime.UtcNow);
            throw new NotFoundException();
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

        if (!computedSignature.Equals(signatureHeader.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Invalid Signature: {@Time}", DateTime.UtcNow);
            throw new InvalidSignatureException();
        }

        _logger.LogInformation("Signature Verified: {@Time}", DateTime.UtcNow);

        _logger.LogInformation("Request Body: {@Body}", body);

        var webhookEvent = JsonSerializer.Deserialize<WebhookEvent>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return webhookEvent;
    }
}