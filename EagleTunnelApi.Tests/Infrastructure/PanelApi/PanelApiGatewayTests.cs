using System.Text.Json;
using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Infrastructure.PanelApi;
using EagleTunnelApi.Infrastructure.PanelApi.Models;
using EagleTunnelApi.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EagleTunnelApi.Tests.Infrastructure.PanelApi;

public class PanelApiGatewayTests
{
    private const string BaseUri = "https://panel.test";

    private static PanelApiGateway CreateGateway(Func<HttpRequestMessage, HttpResponseMessage> responder,
        List<HttpRequestMessage>? requests = null)
    {
        return new PanelApiGateway(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                requests?.Add(request);
                return responder(request);
            }))
            {
                BaseAddress = new Uri(BaseUri)
            },
            NullLogger<PanelApiGateway>.Instance);
    }

    private static HttpResponseMessage Json<T>(T body)
    {
        return StubHttpMessageHandler.Json(JsonSerializer.Serialize(body));
    }

    private static PanelClientDto SampleClient()
    {
        return new PanelClientDto(
            "uuid-1",
            "user@example.com",
            true,
            123456789,
            42,
            300L * 1024 * 1024 * 1024,
            "comment",
            1,
            2,
            "monthly",
            1,
            30,
            "auto",
            "sub-1",
            "xtls-rprx-vision",
            7,
            new List<int> { 1, 2 });
    }

    [Fact]
    public async Task GetClientByEmail_Success_MapsApplicationModel()
    {
        var gateway = CreateGateway(_ => Json(
            new PanelApiResponseDto<PanelClientResponseDto>(
                true,
                "ok",
                new PanelClientResponseDto(SampleClient(), new List<string> { "https://link" },
                    new List<int> { 1, 2 }, 99))));

        var result = await gateway.GetClientByEmailAsync("user@example.com", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("uuid-1", result.Client.Uuid);
        Assert.Equal("user@example.com", result.Client.Email);
        Assert.Equal(42, result.Client.TgId);
        Assert.Equal("sub-1", result.Client.SubId);
        Assert.Equal(new[] { 1, 2 }, result.InboundIds);
        Assert.Equal(99, result.UsedTraffic);
        Assert.Equal(new[] { "https://link" }, result.ExternalLinks);
    }

    [Fact]
    public async Task GetClientByEmail_DefinitiveNotFound_ReturnsNull()
    {
        var gateway = CreateGateway(_ => Json(
            new PanelApiResponseDto<PanelClientResponseDto>(false, "client not found", null)));

        var result = await gateway.GetClientByEmailAsync("missing@example.com", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetClientByEmail_PanelFailure_ThrowsPanelApiException()
    {
        var gateway = CreateGateway(_ => Json(
            new PanelApiResponseDto<PanelClientResponseDto>(false, "boom", null)));

        await Assert.ThrowsAsync<PanelApiException>(() =>
            gateway.GetClientByEmailAsync("user@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task GetClientByEmail_TransportFailure_ThrowsPanelApiException()
    {
        var gateway = CreateGateway(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<PanelApiException>(() =>
            gateway.GetClientByEmailAsync("user@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task GetClientByTelegramId_EmptyList_ReturnsNull()
    {
        var gateway = CreateGateway(_ => Json(
            new PanelApiResponseDto<List<PanelClientResponseDto>>(true, "ok", [])));

        var result = await gateway.GetClientByTelegramIdAsync(42, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AddClient_MapsApplicationRequestToPanelPayload()
    {
        var requests = new List<HttpRequestMessage>();
        var gateway = CreateGateway(_ => Json(new PanelApiResponseDto<object>(true, "ok", null)), requests);

        var request = new PanelClientCreate(
            "user@example.com",
            true,
            123456789,
            300L * 1024 * 1024 * 1024,
            42,
            "comment",
            2,
            "monthly",
            1,
            "sub-1",
            "password-1",
            "auth-1",
            "xtls-rprx-vision",
            [1, 2]);

        await gateway.AddClientAsync(request, CancellationToken.None);

        var sent = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/admin/panel/api/clients/add", sent.RequestUri!.AbsolutePath);

        var body = JsonDocument.Parse(await sent.Content!.ReadAsStringAsync()).RootElement;
        Assert.Equal("user@example.com", body.GetProperty("client").GetProperty("email").GetString());
        Assert.Equal(123456789, body.GetProperty("client").GetProperty("expiryTime").GetInt64());
        Assert.Equal("sub-1", body.GetProperty("client").GetProperty("subId").GetString());
        Assert.Equal("password-1", body.GetProperty("client").GetProperty("password").GetString());
        Assert.Equal("auth-1", body.GetProperty("client").GetProperty("auth").GetString());
        Assert.Equal(new[] { 1, 2 }, body.GetProperty("inboundIds").EnumerateArray()
            .Select(value => value.GetInt32()).ToArray());
    }

    [Fact]
    public async Task UpdateClient_KeyEmailAndPayload_MapsAllFields()
    {
        var requests = new List<HttpRequestMessage>();
        var gateway = CreateGateway(_ => Json(new PanelApiResponseDto<object>(true, "ok", null)), requests);

        var request = new PanelClientUpdate(
            "new@example.com",
            false,
            123456789,
            300L * 1024 * 1024 * 1024,
            42,
            "comment",
            1,
            2,
            "monthly",
            1,
            30,
            "auto",
            "sub-1",
            "xtls-rprx-vision",
            [1, 2]);

        await gateway.UpdateClientAsync("old@example.com", request, CancellationToken.None);

        var sent = Assert.Single(requests);
        Assert.Equal("/admin/panel/api/clients/update/old@example.com", sent.RequestUri!.AbsolutePath);

        var body = JsonDocument.Parse(await sent.Content!.ReadAsStringAsync()).RootElement;
        Assert.Equal("new@example.com", body.GetProperty("email").GetString());
        Assert.False(body.GetProperty("enable").GetBoolean());
        Assert.Equal(123456789, body.GetProperty("expiryTime").GetInt64());
        Assert.Equal(42, body.GetProperty("tgId").GetInt64());
        Assert.Equal(new[] { 1, 2 }, body.GetProperty("inboundIds").EnumerateArray()
            .Select(value => value.GetInt32()).ToArray());
    }
}
