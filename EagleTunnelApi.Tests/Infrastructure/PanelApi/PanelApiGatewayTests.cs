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
            new List<int> { 1, 2 })
        {
            CreatedAt = 1_700_000_000_000,
            UpdatedAt = 1_700_000_100_000
        };
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
        Assert.Equal(7, result.Client.Id);
        Assert.Equal(new[] { 1, 2 }, result.InboundIds);
        Assert.Equal(99, result.UsedTraffic);
        Assert.Equal(new[] { "https://link" }, result.ExternalLinks);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), result.CreatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_100_000), result.UpdatedAt);
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
    public async Task GetClientsPage_Success_SendsExpectedQueryAndMapsRows()
    {
        var requests = new List<HttpRequestMessage>();
        var gateway = CreateGateway(_ => Json(
            new PanelApiResponseDto<PanelClientPageResponseDto>(
                true,
                "ok",
                new PanelClientPageResponseDto(
                    [
                        new PanelClientSlimDto(
                            "user@example.com",
                            "sub-1",
                            true,
                            300L * 1024 * 1024 * 1024,
                            123456789,
                            1,
                            2,
                            30,
                            1,
                            0,
                            0,
                            [1, 2],
                            new PanelTrafficSummaryDto(10, 20, true),
                            1_700_000_000_000,
                            1_700_000_100_000)
                    ],
                    2,
                    200,
                    401,
                    398))), requests);

        var page = await gateway.GetClientsPageAsync(
            new PanelClientPageRequest(2, 200, PanelClientSortFields.UpdatedAt, PanelSortOrders.Descend),
            CancellationToken.None);

        var sent = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/admin/panel/api/clients/list/paged", sent.RequestUri!.AbsolutePath);
        Assert.Contains("page=2", sent.RequestUri.Query);
        Assert.Contains("pageSize=200", sent.RequestUri.Query);
        Assert.Contains("sort=updatedAt", sent.RequestUri.Query);
        Assert.Contains("order=descend", sent.RequestUri.Query);

        Assert.Equal(2, page.Page);
        Assert.Equal(200, page.PageSize);
        Assert.Equal(401, page.Total);
        Assert.Equal(398, page.Filtered);

        var row = Assert.Single(page.Items);
        Assert.Equal("user@example.com", row.Email);
        Assert.Equal("sub-1", row.SubId);
        Assert.True(row.Enable);
        Assert.Equal(123456789, row.ExpiryTime);
        Assert.Equal(300L * 1024 * 1024 * 1024, row.TotalGB);
        Assert.Equal(1, row.LimitIp);
        Assert.Equal(2, row.LimitHwid);
        Assert.Equal(30, row.Reset);
        Assert.Equal(1, row.ResetDay);
        Assert.Equal(0, row.ResetMax);
        Assert.Equal(0, row.ResetWeekday);
        Assert.Equal(new[] { 1, 2 }, row.InboundIds);
        Assert.NotNull(row.Traffic);
        Assert.Equal(10, row.Traffic.Up);
        Assert.Equal(20, row.Traffic.Down);
        Assert.True(row.Traffic.Enable);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), row.CreatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_100_000), row.UpdatedAt);
    }

    [Fact]
    public async Task GetClientsPage_OmitsOptionalQueryValues()
    {
        var requests = new List<HttpRequestMessage>();
        var gateway = CreateGateway(_ => Json(
            new PanelApiResponseDto<PanelClientPageResponseDto>(
                true,
                "ok",
                new PanelClientPageResponseDto([], 1, 200, 0, 0))), requests);

        await gateway.GetClientsPageAsync(new PanelClientPageRequest(1, 200), CancellationToken.None);

        var sent = Assert.Single(requests);
        Assert.Equal("?page=1&pageSize=200", sent.RequestUri!.Query);
    }

    [Fact]
    public async Task GetClientsPage_PanelFailure_ThrowsPanelApiException()
    {
        var gateway = CreateGateway(_ => Json(
            new PanelApiResponseDto<PanelClientPageResponseDto>(false, "boom", null)));

        await Assert.ThrowsAsync<PanelApiException>(() =>
            gateway.GetClientsPageAsync(new PanelClientPageRequest(1, 200), CancellationToken.None));
    }

    [Fact]
    public async Task GetClientsPage_NullObject_ThrowsPanelApiException()
    {
        var gateway = CreateGateway(_ => Json(
            new PanelApiResponseDto<PanelClientPageResponseDto>(true, "ok", null)));

        await Assert.ThrowsAsync<PanelApiException>(() =>
            gateway.GetClientsPageAsync(new PanelClientPageRequest(1, 200), CancellationToken.None));
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
