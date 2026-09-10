using System.Net;
using System.Text.Json;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Telegram;
using EagleTunnelApi.Tests.Helpers;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using EagleTunnelApi.Webhook.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EagleTunnelApi.Tests.Webhook.Handlers;

public class TributeEventsHandlerTests
{
    private const string BaseUri = "https://panel.test";
    private const long TelegramId = 12345;

    private const long TotalGigabytes = 300L * 1024 * 1024 * 1024;

    private static readonly DateTime ExpiresAt = new(2026, 1, 28, 10, 0, 0, DateTimeKind.Utc);

    private static readonly long ExpectedExpiryMs =
        new DateTimeOffset(ExpiresAt.AddHours(1)).ToUnixTimeMilliseconds();

    private static NewSubscription NewSubscriptionEvent(long telegramUserId = TelegramId)
    {
        return new NewSubscription(
            "Standard",
            1,
            2,
            "monthly",
            "new",
            9.99m,
            9.99m,
            "USD",
            7,
            telegramUserId,
            3,
            "channel",
            ExpiresAt
        );
    }

    private static RenewedSubscription RenewedSubscriptionEvent(long telegramUserId = TelegramId)
    {
        return new RenewedSubscription(
            "Standard",
            1,
            2,
            "monthly",
            9.99m,
            9.99m,
            "USD",
            7,
            telegramUserId,
            "user@example.com",
            "https://t.me/app",
            3,
            "channel",
            ExpiresAt,
            "renewed"
        );
    }

    private static PanelClient SampleClient(string email = "user@example.com", long tgId = TelegramId)
    {
        return new PanelClient(
            "uuid-1234",
            email,
            false,
            1,
            tgId,
            100L * 1024 * 1024 * 1024,
            "some comment",
            2,
            2,
            "monthly",
            1,
            0,
            "auto",
            "sub123",
            "xtls-rprx-vision",
            42,
            null
        );
    }

    private static string ClientListJson(params PanelClient[] clients)
    {
        var apiResponse = new PanelApiResponse<List<PanelClientResponse>>(true, "ok",
            clients.Select(c => new PanelClientResponse(c, null, new List<int> { 1 }, 0)).ToList());

        return JsonSerializer.Serialize(apiResponse);
    }

    private static string ClientByEmailJson(PanelClient client)
    {
        return JsonSerializer.Serialize(new PanelApiResponse<PanelClientResponse>(true, "ok",
            new PanelClientResponse(client, null, new List<int> { 1 }, 0)));
    }

    private static string SuccessJson()
    {
        return JsonSerializer.Serialize(new PanelApiResponse<object>(true, "ok", null));
    }

    private static string FailureJson(string message)
    {
        return JsonSerializer.Serialize(new PanelApiResponse<object>(false, message, null));
    }

    private static (TributeEventsHandler Handler, FakeTelegramBotClient Bot) CreateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        List<HttpRequestMessage>? requests = null, int[]? inboundIds = null, int referralBonusDays = 30)
    {
        var bot = new FakeTelegramBotClient();

        var options = Options.Create(new TelegramOptions
        {
            BotToken = "token",
            SupportUrl = "https://t.me/support",
            TributeSubscriptionUrl = "https://tribute.test/sub",
            BotUsername = "TestBot",
            ReferralBonusDays = referralBonusDays,
            DefaultInboundIds = inboundIds ?? new[] { 1, 2 },
            WebhookPath = "/webhooks/telegram"
        });

        var panelClient = new PanelApiClient(new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests?.Add(request);
            return responder(request);
        }))
        {
            BaseAddress = new Uri(BaseUri)
        }, NullLogger<PanelApiClient>.Instance);

        var adminPanelService = new AdminPanelService(NullLogger<AdminPanelService>.Instance, panelClient);

        return (new TributeEventsHandler(NullLogger<TributeEventsHandler>.Instance,
            new SubscriptionProvisioner(NullLogger<SubscriptionProvisioner>.Instance, panelClient, options),
            panelClient, adminPanelService, bot, options), bot);
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

        var (handler, _) = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(client));

            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/update/{client.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        var body = await ReadBodyJson(updateRequest);

        AssertUpdateBody(body, client.Email, ExpectedExpiryMs);
        Assert.Equal(client.TotalGB, body.GetProperty("totalGB").GetInt64());
        Assert.Equal(client.Comment, body.GetProperty("comment").GetString());
        Assert.Equal(client.LimitIp, body.GetProperty("limitIp").GetInt32());
        Assert.Equal(client.LimitHwid, body.GetProperty("limitHwid").GetInt32());
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

        var (handler, _) = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(client));

            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/update/{client.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

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

        var (handler, _) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson());

            if (path == "/admin/panel/api/clients/add") return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None);

        var createRequest = Assert.Single(requests, r => r.RequestUri!.AbsolutePath.EndsWith("/clients/add"));
        var body = await ReadBodyJson(createRequest);

        var client = body.GetProperty("client");
        Assert.Equal($"tg{TelegramId}", client.GetProperty("email").GetString());
        Assert.True(client.GetProperty("enable").GetBoolean());
        Assert.Equal(ExpectedExpiryMs, client.GetProperty("expiryTime").GetInt64());
        Assert.Equal(TotalGigabytes, client.GetProperty("totalGB").GetInt64());
        Assert.Equal(TelegramId, client.GetProperty("tgId").GetInt64());
        Assert.Equal(2, client.GetProperty("limitHwid").GetInt32());
        Assert.Equal("monthly", client.GetProperty("trafficReset").GetString());
        Assert.Equal(1, client.GetProperty("trafficResetDay").GetInt32());
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("subId").GetString()!);
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("password").GetString()!);
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("auth").GetString()!);
        Assert.Equal("xtls-rprx-vision", client.GetProperty("flow").GetString());

        Assert.Equal(new[] { 1, 2 }, body.GetProperty("inboundIds").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray());

        Assert.DoesNotContain(requests, r => r.Method == HttpMethod.Post
                                             && r.RequestUri!.AbsolutePath.Contains("/clients/update/"));
    }

    [Fact]
    public async Task HandleNewSubscription_MissingClient_CreatesClient_WithConfiguredInbounds()
    {
        var requests = new List<HttpRequestMessage>();

        var (handler, _) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson());

            if (path == "/admin/panel/api/clients/add") return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests, new[] { 5 });

        await handler.HandleNewSubscription(NewSubscriptionEvent(), CancellationToken.None);

        var createRequest = Assert.Single(requests, r => r.RequestUri!.AbsolutePath.EndsWith("/clients/add"));
        var body = await ReadBodyJson(createRequest);

        var client = body.GetProperty("client");
        Assert.Equal($"tg{TelegramId}", client.GetProperty("email").GetString());
        Assert.Equal(ExpectedExpiryMs, client.GetProperty("expiryTime").GetInt64());
        Assert.Equal(TelegramId, client.GetProperty("tgId").GetInt64());

        Assert.Equal(new[] { 5 }, body.GetProperty("inboundIds").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public async Task HandleRenewedSubscription_CreateFailsButConcurrentlyCreated_FallsBackToUpdate()
    {
        var requests = new List<HttpRequestMessage>();
        var getRequests = 0;

        var (handler, _) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getRequests++;

                if (getRequests == 1) return StubHttpMessageHandler.Json(ClientListJson());

                return StubHttpMessageHandler.Json(ClientListJson(new PanelClient(
                    "uuid-5678",
                    $"tg{TelegramId}",
                    true,
                    ExpectedExpiryMs,
                    TelegramId,
                    0,
                    "Created from subscription: Standard",
                    0,
                    2,
                    "monthly",
                    1,
                    0,
                    null,
                    "sub456",
                    null,
                    99,
                    null)));
            }

            if (path == "/admin/panel/api/clients/add")
                return StubHttpMessageHandler.Json(FailureJson("Duplicate email"));

            if (path == "/admin/panel/api/clients/update/tg12345") return StubHttpMessageHandler.Json(SuccessJson());

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

        var (handler, _) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getRequests++;
                return StubHttpMessageHandler.Json(ClientListJson());
            }

            if (path == "/admin/panel/api/clients/add")
                return StubHttpMessageHandler.Json(FailureJson("something broke"));

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

        var (handler, _) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(first, second));

            if (path == $"/admin/panel/api/clients/update/{first.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        AssertUpdateBody(await ReadBodyJson(updateRequest), first.Email, ExpectedExpiryMs);
    }

    [Fact]
    public async Task HandleRenewedSubscription_PanelFetchReturnsSuccessFalse_ThrowsPanelApiException()
    {
        var (handler, _) = CreateHandler(_ => StubHttpMessageHandler.Json(FailureJson("boom")));

        await Assert.ThrowsAsync<PanelApiException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleRenewedSubscription_PanelFetchReturnsNullBody_ThrowsPanelApiException()
    {
        var (handler, _) = CreateHandler(_ => StubHttpMessageHandler.Json("null"));

        await Assert.ThrowsAsync<PanelApiException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleRenewedSubscription_UpdateReturnsSuccessFalse_ThrowsPanelApiException()
    {
        var client = SampleClient();

        var (handler, _) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(client));

            if (path == $"/admin/panel/api/clients/update/{client.Email}")
                return StubHttpMessageHandler.Json(FailureJson("update rejected"));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await Assert.ThrowsAsync<PanelApiException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleRenewedSubscription_UpdateReturnsNonJsonError_ThrowsPanelApiException()
    {
        var client = SampleClient();

        var (handler, _) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(client));

            if (path == $"/admin/panel/api/clients/update/{client.Email}")
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("internal server error")
                };

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await Assert.ThrowsAsync<PanelApiException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleNewSubscription_TelegramUserIdZero_ThrowsInvalidPayloadException()
    {
        var requests = new List<HttpRequestMessage>();
        var (handler, _) = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"), requests);

        await Assert.ThrowsAsync<InvalidPayloadException>(() =>
            handler.HandleNewSubscription(NewSubscriptionEvent(0), CancellationToken.None));

        Assert.Empty(requests);
    }

    [Fact]
    public async Task HandleRenewedSubscription_NegativeTelegramUserId_ThrowsInvalidPayloadException()
    {
        var requests = new List<HttpRequestMessage>();
        var (handler, _) = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"), requests);

        await Assert.ThrowsAsync<InvalidPayloadException>(() =>
            handler.HandleRenewedSubscription(RenewedSubscriptionEvent(-5), CancellationToken.None));

        Assert.Empty(requests);
    }

    private static PanelClient TaggedRefereeClient()
    {
        return SampleClient("newbie@example.com") with
        {
            Enable = false,
            ExpiryTime = DateTimeOffset.UtcNow.AddYears(100).ToUnixTimeMilliseconds(),
            Comment = "Telegram ID: 12345 · Referrer tgId: 777 · Referred by: friend@example.com"
        };
    }

    private static PanelClient ReferrerClient()
    {
        return SampleClient("friend@example.com", 777) with
        {
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds(),
            Comment = "referrer comment"
        };
    }

    [Fact]
    public async Task HandleNewSubscription_WithReferrerTag_GrantsBonusAndMarksPaid()
    {
        var requests = new List<HttpRequestMessage>();
        var referee = TaggedRefereeClient();
        var referrer = ReferrerClient();

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path == $"/admin/panel/api/clients/update/{referee.Email}" ||
                path == $"/admin/panel/api/clients/update/{referrer.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleNewSubscription(NewSubscriptionEvent(), CancellationToken.None);

        var bodies = new List<JsonElement>();
        foreach (var r in requests.Where(r => r.Method == HttpMethod.Post)) bodies.Add(await ReadBodyJson(r));

        var grantBody = bodies.First(b =>
            b.GetProperty("email").GetString() == referrer.Email &&
            !b.GetProperty("comment").GetString()!.Contains("Referral credit:"));
        Assert.True(grantBody.GetProperty("enable").GetBoolean());
        Assert.InRange(grantBody.GetProperty("expiryTime").GetInt64(),
            DateTimeOffset.UtcNow.AddDays(40).AddMinutes(-2).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.AddDays(40).AddMinutes(2).ToUnixTimeMilliseconds());

        var creditBody = bodies.First(b =>
            b.GetProperty("comment").GetString()!.Contains("Referral credit: 30d"));
        Assert.Equal(referrer.Email, creditBody.GetProperty("email").GetString());

        var paidBody = bodies.First(b =>
            b.GetProperty("comment").GetString()!.Contains("Referral bonus paid"));
        Assert.Equal(referee.Email, paidBody.GetProperty("email").GetString());

        var dm = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("777", dm.Body);
        Assert.Contains("30 days", dm.Body);
    }

    [Fact]
    public async Task HandleNewSubscription_PendingUnknownReferrer_NoGrant()
    {
        var requests = new List<HttpRequestMessage>();
        var referee = TaggedRefereeClient() with
        {
            Comment = "Telegram ID: 12345 · Referred by (pending): ghost@example.com"
        };

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/ghost@example.com")
                return StubHttpMessageHandler.Json(FailureJson("not found"));

            if (path == $"/admin/panel/api/clients/update/{referee.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleNewSubscription(NewSubscriptionEvent(), CancellationToken.None);

        Assert.DoesNotContain(requests,
            r => r.RequestUri!.AbsolutePath.Contains("/clients/update/ghost"));
        Assert.Empty(bot.Requests);

        foreach (var r in requests.Where(r => r.Method == HttpMethod.Post))
        {
            var body = await ReadBodyJson(r);
            Assert.DoesNotContain("Referral bonus paid", body.GetProperty("comment").GetString());
        }
    }

    [Fact]
    public async Task HandleNewSubscription_ProvisionedRefereeWithTag_NoGrant()
    {
        var requests = new List<HttpRequestMessage>();
        var referee = TaggedRefereeClient() with
        {
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds()
        };

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == $"/admin/panel/api/clients/update/{referee.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleNewSubscription(NewSubscriptionEvent(), CancellationToken.None);

        Assert.DoesNotContain(requests,
            r => r.RequestUri!.AbsolutePath.Contains("friend@example.com"));
        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task HandleNewSubscription_AlreadyPaidMarker_NoGrant()
    {
        var requests = new List<HttpRequestMessage>();
        var referee = TaggedRefereeClient() with
        {
            Comment = TaggedRefereeClient().Comment + " · Referral bonus paid"
        };

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == $"/admin/panel/api/clients/update/{referee.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleNewSubscription(NewSubscriptionEvent(), CancellationToken.None);

        Assert.DoesNotContain(requests,
            r => r.RequestUri!.AbsolutePath.Contains("friend@example.com"));
        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task HandleRenewedSubscription_WithReferrerTag_NoGrant()
    {
        var requests = new List<HttpRequestMessage>();
        var referee = TaggedRefereeClient();

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == $"/admin/panel/api/clients/update/{referee.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleRenewedSubscription(RenewedSubscriptionEvent(), CancellationToken.None);

        Assert.DoesNotContain(requests,
            r => r.RequestUri!.AbsolutePath.Contains("friend@example.com"));
        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task HandleNewSubscription_BonusDisabled_NoGrant()
    {
        var requests = new List<HttpRequestMessage>();
        var referee = TaggedRefereeClient();

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == $"/admin/panel/api/clients/update/{referee.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests, referralBonusDays: 0);

        await handler.HandleNewSubscription(NewSubscriptionEvent(), CancellationToken.None);

        Assert.DoesNotContain(requests,
            r => r.RequestUri!.AbsolutePath.Contains("friend@example.com"));
        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task HandleNewSubscription_TrialRefereeWithTag_GrantsBonus()
    {
        var requests = new List<HttpRequestMessage>();
        var referee = TaggedRefereeClient() with
        {
            Enable = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(),
            Comment = TaggedRefereeClient().Comment + " · Trial until 2099-01-01 00:00 UTC"
        };
        var referrer = ReferrerClient();

        var (handler, bot) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(referee));

            if (path == "/admin/panel/api/clients/get/tgId/777")
                return StubHttpMessageHandler.Json(ClientListJson(referrer));

            if (path == "/admin/panel/api/clients/get/friend@example.com")
                return StubHttpMessageHandler.Json(ClientByEmailJson(referrer));

            if (path.StartsWith("/admin/panel/api/clients/update/", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleNewSubscription(NewSubscriptionEvent(), CancellationToken.None);

        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post &&
                r.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/update/{referrer.Email}");
        Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
    }

    [Fact]
    public async Task HandleNewSubscription_Activation_StripsTrialTag()
    {
        var requests = new List<HttpRequestMessage>();
        var trialing = SampleClient() with { Comment = "some comment · Trial until 2099-01-01 00:00 UTC" };

        var (handler, _) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(trialing));

            if (path == $"/admin/panel/api/clients/update/{trialing.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleNewSubscription(NewSubscriptionEvent(), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        var comment = (await ReadBodyJson(updateRequest)).GetProperty("comment").GetString()!;

        Assert.DoesNotContain("Trial until", comment);
        Assert.Contains("some comment", comment);
    }

    private static CancelledSubscription CancelledSubscriptionEvent(long telegramUserId = TelegramId)
    {
        return new CancelledSubscription(
            "Standard",
            1,
            2,
            "monthly",
            "regular",
            9.99m,
            9.99m,
            "USD",
            "T-31326",
            telegramUserId,
            "durov",
            3,
            "channel",
            "",
            ExpiresAt
        );
    }

    [Fact]
    public void CancelledSubscription_DocumentedShape_Deserializes()
    {
        const string json = """
                            {
                              "name": "cancelled_subscription",
                              "created_at": "2025-03-21T11:20:44.013969Z",
                              "sent_at": "2025-03-21T11:20:44.527657077Z",
                              "payload": {
                                "subscription_name": "Join the private club 🎉",
                                "subscription_id": 1646,
                                "period_id": 1549,
                                "period": "monthly",
                                "type": "regular",
                                "price": 1000,
                                "amount": 1000,
                                "currency": "eur",
                                "trb_user_id": "T-31326",
                                "telegram_user_id": 12321321,
                                "telegram_username": "durov",
                                "channel_id": 614,
                                "channel_name": "lbs",
                                "cancel_reason": "",
                                "expires_at": "2025-03-20T11:13:44.737Z"
                              }
                            }
                            """;

        var webhookEvent = JsonSerializer.Deserialize<WebhookEvent>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(webhookEvent);
        Assert.Equal("cancelled_subscription", webhookEvent.Name);

        var payload = webhookEvent.Payload.Deserialize<CancelledSubscription>();
        Assert.NotNull(payload);
        Assert.Equal(1646, payload.SubscriptionId);
        Assert.Equal(12321321, payload.TelegramUserId);
        Assert.Equal("durov", payload.TelegramUsername);
        Assert.Equal("T-31326", payload.TrbUserId);
        Assert.Equal(1000, payload.Price);
    }

    [Fact]
    public void CancelledSubscription_MissingOptionalFields_DeserializesToNull()
    {
        const string json = """
                            {
                              "payload": {
                                "subscription_name": "Club",
                                "subscription_id": 1,
                                "period_id": 2,
                                "period": "monthly",
                                "type": "regular",
                                "price": 1000,
                                "amount": 1000,
                                "currency": "eur",
                                "telegram_user_id": 42,
                                "channel_id": 7,
                                "channel_name": "lbs",
                                "expires_at": "2025-03-20T11:13:44.737Z"
                              }
                            }
                            """;

        var payload = JsonDocument.Parse(json).RootElement
            .GetProperty("payload")
            .Deserialize<CancelledSubscription>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(payload);
        Assert.Equal(42, payload.TelegramUserId);
        Assert.Null(payload.TelegramUsername);
        Assert.Null(payload.CancelReason);
        Assert.Null(payload.TrbUserId);
    }

    [Fact]
    public async Task HandleCancelledSubscription_TagsClient()
    {
        var requests = new List<HttpRequestMessage>();
        var client = SampleClient();

        var (handler, _) = CreateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(client));

            if (path == $"/admin/panel/api/clients/update/{client.Email}")
                return StubHttpMessageHandler.Json(SuccessJson());

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleCancelledSubscription(CancelledSubscriptionEvent(), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        var body = await ReadBodyJson(updateRequest);
        Assert.Contains("Cancelled ", body.GetProperty("comment").GetString());
        Assert.Contains("some comment", body.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task HandleCancelledSubscription_AlreadyTagged_NoUpdate()
    {
        var requests = new List<HttpRequestMessage>();
        var client = SampleClient() with { Comment = "some comment · Cancelled 2026-09-01" };

        var (handler, _) = CreateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                return StubHttpMessageHandler.Json(ClientListJson(client));

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }, requests);

        await handler.HandleCancelledSubscription(CancelledSubscriptionEvent(), CancellationToken.None);

        Assert.DoesNotContain(requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task HandleCancelledSubscription_InvalidId_AcksWithoutCalls()
    {
        var requests = new List<HttpRequestMessage>();
        var (handler, _) = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"), requests);

        await handler.HandleCancelledSubscription(CancelledSubscriptionEvent(0),
            CancellationToken.None);

        Assert.Empty(requests);
    }
}