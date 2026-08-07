using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using EagleTunnelApi.Webhook.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EagleTunnelApi.Tests.Webhook.Security;

public class VerifierTests
{
    private const string ApiKey = "test-secret";

    private static Verifier CreateVerifier(string? apiKey = ApiKey)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tribute:ApiKey"] = apiKey
            })
            .Build();

        return new Verifier(config, NullLogger<Verifier>.Instance);
    }

    private static string ComputeSignature(string body, string key = ApiKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    private static HttpRequest CreateRequest(string body, string? signatureHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        if (signatureHeader is not null)
        {
            context.Request.Headers["trbt-signature"] = signatureHeader;
        }

        return context.Request;
    }

    private const string SampleBody =
        "{\"name\":\"renewed_subscription\",\"created_at\":\"2026-01-28T10:15:00Z\",\"sent_at\":\"2026-01-28T10:15:00Z\",\"payload\":{}}";

    [Fact]
    public async Task VerifySignature_ValidSignature_ReturnsDeserializedWebhookEvent()
    {
        var verifier = CreateVerifier();
        var request = CreateRequest(SampleBody, ComputeSignature(SampleBody));

        var result = await verifier.VerifySignature(request);

        Assert.NotNull(result);
        Assert.Equal("renewed_subscription", result!.Name);
    }

    [Fact]
    public async Task VerifySignature_UppercaseSignatureHeader_StillAccepted()
    {
        var verifier = CreateVerifier();
        var request = CreateRequest(SampleBody, ComputeSignature(SampleBody).ToUpperInvariant());

        var result = await verifier.VerifySignature(request);

        Assert.NotNull(result);
        Assert.Equal("renewed_subscription", result!.Name);
    }

    [Fact]
    public async Task VerifySignature_ValidSignatureButInvalidJson_ReturnsNull()
    {
        var verifier = CreateVerifier();
        var request = CreateRequest("not json at all", ComputeSignature("not json at all"));

        var result = await verifier.VerifySignature(request);

        Assert.Null(result);
    }

    [Fact]
    public async Task VerifySignature_MissingSignatureHeader_ThrowsNotFoundException()
    {
        var verifier = CreateVerifier();
        var request = CreateRequest(SampleBody, signatureHeader: null);

        await Assert.ThrowsAsync<NotFoundException>(() => verifier.VerifySignature(request));
    }

    [Fact]
    public async Task VerifySignature_InvalidSignature_ThrowsInvalidSignatureException()
    {
        var verifier = CreateVerifier();
        var request = CreateRequest(SampleBody, "deadbeef");

        await Assert.ThrowsAsync<InvalidSignatureException>(() => verifier.VerifySignature(request));
    }

    [Fact]
    public async Task VerifySignature_MissingApiKey_ThrowsNotFoundException()
    {
        var verifier = CreateVerifier(apiKey: null);
        var request = CreateRequest(SampleBody, ComputeSignature(SampleBody));

        await Assert.ThrowsAsync<NotFoundException>(() => verifier.VerifySignature(request));
    }

    [Fact]
    public async Task VerifySignature_ValidSignature_ReadsBodyExactlyOnce()
    {
        var verifier = CreateVerifier();
        var request = CreateRequest(SampleBody, ComputeSignature(SampleBody));

        var result = await verifier.VerifySignature(request);

        Assert.NotNull(result);
        var replayed = await JsonSerializer.DeserializeAsync<WebhookEvent>(request.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal("renewed_subscription", replayed!.Name);
    }
}
