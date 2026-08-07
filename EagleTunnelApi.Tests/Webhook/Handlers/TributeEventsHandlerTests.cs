using System.Net;
using System.Text.Json;
using EagleTunnelApi.Tests.Helpers;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using EagleTunnelApi.Webhook.Handlers;
using EagleTunnelApi.Webhook.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EagleTunnelApi.Tests.Webhook.Handlers;

public class TributeEventsHandlerTests
{
    private const string BaseUri = "https://panel.test";
    private const long TelegramId = 12345;

    private static readonly DateTime ExpiresAt = new(2026, 1, 28, 10, 0, 0, DateTimeKind.Utc);

    private static readonly long ExpectedExpiryMs =
        new DateTimeOffset(ExpiresAt.AddHours(1)).ToUnixTimeMilliseconds();

    private static NewSubscription NewSubscriptionEvent(long telegramUserId = TelegramId) => new(
        SubscriptionName: "Standard",
        SubscriptionId: 1,
        PeriodId: 2,
        Period: "monthly",
        Type: "new",
        Price: 9.99m,
        Amount: 9.99m,
        Currency: "USD",
        UserId: 7,
        TelegramUserId: telegramUserId,
        ChannelId: 3,
        ChannelName: "channel",
        ExpiresAt: ExpiresAt
    );

    private static RenewedSubscription RenewedSubscriptionEvent(long telegramUserId = TelegramId) => new(
        SubscriptionName: "Standard",
        SubscriptionId: 1,
        PeriodId: 2,
        Period: "monthly",
        Price: 9.99m,
        Amount: 9.99m,
        Currency: "USD",
        UserId: 7,
        TelegramUserId: telegramUserId,
        Email: "user@example.com",
        WebAppLink: "https://t.me/app",
        ChannelId: 3,
        ChannelName: "channel",
        ExpiresAt: ExpiresAt,
        Type: "renewed"
    );

    private static PanelClient SampleClient(string email = "user@example.com", long tgId = TelegramId) => new(
        Email: email,
        Enable: false,
        ExpiryTime: 1,
        TgId: tgId,
        TotalGB: 100L * 1024 * 1024 * 1024,
        Comment: "some comment",
        LimitIp: 2,
        Reset: 0,
        Security: "auto",
        SubId: "sub123",
        Flow: "xtls-rprx-vision",
        Id: 42
    );

    private static string ClientListJson(params PanelClient[] clients)
    {
        var apiResponse = new PanelApiResponse<List<PanelClientResponse>>(true, "ok",
            clients.Select(c => new PanelClientResponse(c, null, new List<int> { 1 }, 0)).ToList());

        return JsonSerializer.Serialize(apiResponse);
    }

    private static string InboundsJson(params int[] inboundIds)
    {
        var apiResponse = new PanelApiResponse<List<PanelInbound>>(true, "ok",
            inboundIds.Select(id => new PanelInbound(id)).ToList());

        return JsonSerializer.Serialize(apiResponse);
    }

    private static string SuccessJson() => JsonSerializer.Serialize(new PanelApiResponse<object>(true, "ok", null));

    private static string FailureJson(string message) =>
        JsonSerializer.Serialize(new PanelApiResponse<object>(false, message, null));

    private static TributeEventsHandler CreateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder,
        List<HttpRequestMessage>? requests = null)
    {
        var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests?.Add(request);
            return responder(request);
        }))
        {
            BaseAddress = new Uri(BaseUri)
        };

        return new TributeEventsHandler(NullLogger<TributeEventsHandler>.Instance, client);
    }

    private static async Task<JsonElement> ReadBodyJson(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static void AssertUpdateBody(JsonElement body, string email, long expiryMs, long tgId = TelegramId)
    {
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("enable").GetBoolean());
        Assert.Equal(expiryMs, body.GetProperty("expiryTime").GetInt64());
        Assert.Equal(tgId, body.GetProperty("tgId").GetInt64());
    }

    [Fact]
    public async Task HandleRenewedSubscription_ExistingClient_UpdatesExpiryAndEnables()
    {
        var requests = new List<HttpRequestMessage>();
        var client = SampleClient();

        var handler = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return StubHttpMessageHandler.Json(ClientListJson(client));
            }

            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/update/{client.Email}")
            {
                return StubHttpMessageHandler.Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        var body = await ReadBodyJson(updateRequest);

        AssertUpdateBody(body, client.Email, ExpectedExpiryMs);
        Assert.Equal(client.TotalGB, body.GetProperty("totalGB").GetInt64());
        Assert.Equal(client.Comment, body.GetProperty("comment").GetString());
        Assert.Equal(client.LimitIp, body.GetProperty("limitIp").GetInt32());
        Assert.Equal(client.Reset, body.GetProperty("reset").GetInt32());
        Assert.Equal(client.Security, body.GetProperty("security").GetString());
        Assert.Equal(client.SubId, body.GetProperty("subId").GetString());
        Assert.Equal(client.Flow, body.GetProperty("flow").GetString());
        Assert.DoesNotContain(requests, r => r.RequestUri!.AbsolutePath.EndsWith("/clients/add"));
    }

    [Fact]
    public async Task HandleNewSubscription_ExistingClient_UpdatesExpiry()
    {
        var requests = new List<HttpRequestMessage>();
        var client = SampleClient();

        var handler = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return StubHttpMessageHandler.Json(ClientListJson(client));
            }

            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/update/{client.Email}")
            {
                return StubHttpMessageHandler.Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleNewSubscription(NewSubscriptionEvent(), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        AssertUpdateBody(await ReadBodyJson(updateRequest), client.Email, ExpectedExpiryMs);
    }

    [Fact]
    public async Task HandleRenewedSubscription_MissingClient_CreatesWithDeterministicEmail()
    {
        var requests = new List<HttpRequestMessage>();

        var handler = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return StubHttpMessageHandler.Json(ClientListJson());
            }

            if (path == "/admin/panel/api/inbounds/list")
            {
                return StubHttpMessageHandler.Json(InboundsJson(1, 2));
            }

            if (path == "/admin/panel/api/clients/add")
            {
                return StubHttpMessageHandler.Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None);

        var createRequest = Assert.Single(requests, r => r.RequestUri!.AbsolutePath.EndsWith("/clients/add"));
        var body = await ReadBodyJson(createRequest);

        var client = body.GetProperty("client");
        Assert.Equal($"tg{TelegramId}", client.GetProperty("email").GetString());
        Assert.True(client.GetProperty("enable").GetBoolean());
        Assert.Equal(ExpectedExpiryMs, client.GetProperty("expiryTime").GetInt64());
        Assert.Equal(0, client.GetProperty("totalGB").GetInt64());
        Assert.Equal(TelegramId, client.GetProperty("tgId").GetInt64());
        Assert.Equal(0, client.GetProperty("limitIp").GetInt32());
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("subId").GetString()!);

        Assert.Equal(new[] { 1, 2 }, body.GetProperty("inboundIds").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray());

        Assert.DoesNotContain(requests, r => r.Method == HttpMethod.Post
                                             && r.RequestUri!.AbsolutePath.Contains("/clients/update/"));
    }

    [Fact]
    public async Task HandleNewSubscription_MissingClient_CreatesClient()
    {
        var requests = new List<HttpRequestMessage>();

        var handler = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return StubHttpMessageHandler.Json(ClientListJson());
            }

            if (path == "/admin/panel/api/inbounds/list")
            {
                return StubHttpMessageHandler.Json(InboundsJson(1));
            }

            if (path == "/admin/panel/api/clients/add")
            {
                return StubHttpMessageHandler.Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleNewSubscription(NewSubscriptionEvent(), CancellationToken.None);

        var createRequest = Assert.Single(requests, r => r.RequestUri!.AbsolutePath.EndsWith("/clients/add"));
        var client = (await ReadBodyJson(createRequest)).GetProperty("client");

        Assert.Equal($"tg{TelegramId}", client.GetProperty("email").GetString());
        Assert.Equal(ExpectedExpiryMs, client.GetProperty("expiryTime").GetInt64());
        Assert.Equal(TelegramId, client.GetProperty("tgId").GetInt64());
    }

    [Fact]
    public async Task HandleRenewedSubscription_MissingClientAndNoInbounds_ThrowsNotFoundException()
    {
        var handler = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return StubHttpMessageHandler.Json(ClientListJson());
            }

            if (path == "/admin/panel/api/inbounds/list")
            {
                return StubHttpMessageHandler.Json(InboundsJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleRenewedSubscription_InboundsFetchFails_ThrowsPanelApiException()
    {
        var handler = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return StubHttpMessageHandler.Json(ClientListJson());
            }

            if (path == "/admin/panel/api/inbounds/list")
            {
                return StubHttpMessageHandler.Json(FailureJson("inbounds exploded"));
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await Assert.ThrowsAsync<PanelApiException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleRenewedSubscription_CreateFailsButConcurrentlyCreated_FallsBackToUpdate()
    {
        var requests = new List<HttpRequestMessage>();
        var getRequests = 0;

        var handler = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getRequests++;

                if (getRequests == 1)
                {
                    return StubHttpMessageHandler.Json(ClientListJson());
                }

                return StubHttpMessageHandler.Json(ClientListJson(new PanelClient($"tg{TelegramId}", true,
                    ExpectedExpiryMs, TelegramId, 0, "Created from subscription: Standard", 0, 0, null, "sub456",
                    null, 99)));
            }

            if (path == "/admin/panel/api/inbounds/list")
            {
                return StubHttpMessageHandler.Json(InboundsJson(1));
            }

            if (path == "/admin/panel/api/clients/add")
            {
                return StubHttpMessageHandler.Json(FailureJson("Duplicate email"));
            }

            if (path == "/admin/panel/api/clients/update/tg12345")
            {
                return StubHttpMessageHandler.Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None);

        Assert.Equal(2, getRequests);
        var updateRequest = Assert.Single(requests, r => r.RequestUri!.AbsolutePath.Contains("/clients/update/"));
        AssertUpdateBody(await ReadBodyJson(updateRequest), $"tg{TelegramId}", ExpectedExpiryMs);
    }

    [Fact]
    public async Task HandleRenewedSubscription_CreateFailsAndStillMissing_ThrowsPanelApiException()
    {
        var getRequests = 0;

        var handler = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getRequests++;
                return StubHttpMessageHandler.Json(ClientListJson());
            }

            if (path == "/admin/panel/api/inbounds/list")
            {
                return StubHttpMessageHandler.Json(InboundsJson(1));
            }

            if (path == "/admin/panel/api/clients/add")
            {
                return StubHttpMessageHandler.Json(FailureJson("something broke"));
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await Assert.ThrowsAsync<PanelApiException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));

        Assert.Equal(2, getRequests);
    }

    [Fact]
    public async Task HandleRenewedSubscription_MultipleMatches_UpdatesFirstMatch()
    {
        var requests = new List<HttpRequestMessage>();
        var first = SampleClient("first@example.com");
        var second = SampleClient("second@example.com");

        var handler = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return StubHttpMessageHandler.Json(ClientListJson(first, second));
            }

            if (path == $"/admin/panel/api/clients/update/{first.Email}")
            {
                return StubHttpMessageHandler.Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        AssertUpdateBody(await ReadBodyJson(updateRequest), first.Email, ExpectedExpiryMs);
    }

    [Fact]
    public async Task HandleRenewedSubscription_PanelFetchReturnsSuccessFalse_ThrowsPanelApiException()
    {
        var handler = CreateHandler(_ => StubHttpMessageHandler.Json(FailureJson("boom")));

        await Assert.ThrowsAsync<PanelApiException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleRenewedSubscription_PanelFetchReturnsNullBody_ThrowsPanelApiException()
    {
        var handler = CreateHandler(_ => StubHttpMessageHandler.Json("null"));

        await Assert.ThrowsAsync<PanelApiException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleRenewedSubscription_UpdateReturnsSuccessFalse_ThrowsPanelApiException()
    {
        var client = SampleClient();

        var handler = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return StubHttpMessageHandler.Json(ClientListJson(client));
            }

            if (path == $"/admin/panel/api/clients/update/{client.Email}")
            {
                return StubHttpMessageHandler.Json(FailureJson("update rejected"));
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await Assert.ThrowsAsync<PanelApiException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleRenewedSubscription_UpdateReturnsNonJsonError_ThrowsPanelApiException()
    {
        var client = SampleClient();

        var handler = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                return StubHttpMessageHandler.Json(ClientListJson(client));
            }

            if (path == $"/admin/panel/api/clients/update/{client.Email}")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("internal server error")
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await Assert.ThrowsAsync<PanelApiException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleNewSubscription_TelegramUserIdZero_ThrowsInvalidPayloadException()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"), requests);

        await Assert.ThrowsAsync<InvalidPayloadException>(() =>
            handler.HandleNewSubscription(NewSubscriptionEvent(telegramUserId: 0), CancellationToken.None));

        Assert.Empty(requests);
    }

    [Fact]
    public async Task HandleRenewedSubscription_NegativeTelegramUserId_ThrowsInvalidPayloadException()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"), requests);

        await Assert.ThrowsAsync<InvalidPayloadException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(telegramUserId: -5), CancellationToken.None));

        Assert.Empty(requests);
    }
}
