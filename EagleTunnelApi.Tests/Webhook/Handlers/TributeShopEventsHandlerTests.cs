using System.Text.Json;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Telegram;
using EagleTunnelApi.Tests.Helpers;
using EagleTunnelApi.TributeShop;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using EagleTunnelApi.Webhook.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EagleTunnelApi.Tests.Webhook.Handlers;

public class TributeShopEventsHandlerTests
{
    private const string BaseUri = "https://panel.test";
    private const long TelegramId = 12345;

    private static readonly DateTime MemberExpiresAt = new(2026, 2, 28, 10, 0, 0, DateTimeKind.Utc);

    private static readonly long ExpectedExpiryMs =
        new DateTimeOffset(MemberExpiresAt.AddHours(1)).ToUnixTimeMilliseconds();

    private static ShopOrderEventPayload PaymentPayload(
        string? customerId = null,
        long? amount = null,
        string? period = "monthly",
        bool? isRecurrent = true,
        DateTime? memberExpiresAt = null) => new(
        Uuid: "order-uuid-1",
        ShopId: 1,
        Amount: amount,
        Currency: "rub",
        Period: period,
        Status: "paid",
        CustomerId: customerId ?? TelegramId.ToString(),
        IsRecurrent: isRecurrent,
        MemberStatus: "active",
        MemberExpiresAt: memberExpiresAt,
        CancelReason: null,
        ChargeRetries: null,
        TransactionId: null,
        RefundedAt: null,
        StarsAmount: null,
        OnlyStars: false
    );

    private static PanelClient SampleClient(string email = "user@example.com", long tgId = TelegramId) => new(
        Uuid: "uuid-1234",
        Email: email,
        Enable: true,
        ExpiryTime: ExpectedExpiryMs,
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

    private static string SuccessJson() => JsonSerializer.Serialize(new PanelApiResponse<object>(true, "ok", null));

    private static SubscriptionProvisioner CreateProvisioner(PanelClient client,
        List<HttpRequestMessage>? requests = null)
    {
        requests ??= [];

        var options = Options.Create(new TelegramOptions
        {
            BotToken = "token",
            SupportUrl = "https://t.me/support",
            DefaultInboundIds = new[] { 1, 2 },
            WebhookPath = "/webhook/telegram"
        });

        return new SubscriptionProvisioner(NullLogger<SubscriptionProvisioner>.Instance,
            new PanelApiClient(new HttpClient(new StubHttpMessageHandler(request =>
            {
                requests.Add(request);
                var path = request.RequestUri!.AbsolutePath;

                if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
                {
                    return StubHttpMessageHandler.Json(ClientListJson(client));
                }

                if (path == $"/admin/panel/api/clients/update/{client.Email}")
                {
                    return StubHttpMessageHandler.Json(SuccessJson());
                }

                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            }))
            {
                BaseAddress = new Uri(BaseUri)
            }, NullLogger<PanelApiClient>.Instance),
            options);
    }

    private static (TributeShopEventsHandler Handler, List<HttpRequestMessage> Requests) CreateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var requests = new List<HttpRequestMessage>();

        var options = Options.Create(new TelegramOptions
        {
            BotToken = "token",
            SupportUrl = "https://t.me/support",
            DefaultInboundIds = new[] { 1, 2 },
            WebhookPath = "/webhook/telegram"
        });

        var provisioner = new SubscriptionProvisioner(NullLogger<SubscriptionProvisioner>.Instance,
            new PanelApiClient(new HttpClient(new StubHttpMessageHandler(request =>
            {
                requests.Add(request);
                return responder(request);
            }))
            {
                BaseAddress = new Uri(BaseUri)
            }, NullLogger<PanelApiClient>.Instance),
            options);

        return (new TributeShopEventsHandler(NullLogger<TributeShopEventsHandler>.Instance, provisioner), requests);
    }

    private static async Task<JsonElement> ReadBodyJson(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static TributeShopEventsHandler CreateExistingClientHandler(
        PanelClient client, List<HttpRequestMessage>? requests = null)
    {
        requests ??= [];
        var provisioner = CreateProvisioner(client, requests);

        return new TributeShopEventsHandler(NullLogger<TributeShopEventsHandler>.Instance, provisioner);
    }

    [Fact]
    public async Task HandlePaymentReceived_Recurring_UsesMemberExpiresAt()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateExistingClientHandler(SampleClient(), requests);

        await handler.HandlePaymentReceived(PaymentPayload(memberExpiresAt: MemberExpiresAt), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        var body = await ReadBodyJson(updateRequest);

        Assert.True(body.GetProperty("enable").GetBoolean());
        Assert.Equal(ExpectedExpiryMs, body.GetProperty("expiryTime").GetInt64());
    }

    [Fact]
    public async Task HandlePaymentReceived_OneTime_ResolvesDurationByAmount()
    {
        var requests = new List<HttpRequestMessage>();
        var before = DateTime.UtcNow;
        var handler = CreateExistingClientHandler(SampleClient(), requests);

        await handler.HandlePaymentReceived(
            PaymentPayload(period: "onetime", isRecurrent: false, amount: 30000, memberExpiresAt: null),
            CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        var body = await ReadBodyJson(updateRequest);

        Assert.True(body.GetProperty("enable").GetBoolean());
        var expiryMs = body.GetProperty("expiryTime").GetInt64();
        var expiry = DateTimeOffset.FromUnixTimeMilliseconds(expiryMs).UtcDateTime;

        Assert.InRange(expiry, before.AddDays(29), DateTime.UtcNow.AddDays(31));
    }

    [Fact]
    public async Task HandlePaymentReceived_RecurringMissingMemberExpiry_FallsBackToPeriodDuration()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateExistingClientHandler(SampleClient(), requests);

        await handler.HandlePaymentReceived(
            PaymentPayload(period: "monthly", memberExpiresAt: null), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        var body = await ReadBodyJson(updateRequest);

        Assert.True(body.GetProperty("enable").GetBoolean());
        var expiryMs = body.GetProperty("expiryTime").GetInt64();
        var expiry = DateTimeOffset.FromUnixTimeMilliseconds(expiryMs).UtcDateTime;

        Assert.InRange(expiry, DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));
    }

    [Fact]
    public async Task HandlePaymentReceived_InvalidCustomerId_ThrowsInvalidPayloadException()
    {
        var (handler, requests) = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"));

        await Assert.ThrowsAsync<InvalidPayloadException>(() =>
            handler.HandlePaymentReceived(PaymentPayload(customerId: "not-a-number"), CancellationToken.None));

        Assert.Empty(requests);
    }

    [Fact]
    public async Task HandlePaymentReceived_UnknownAmount_ThrowsInvalidPayloadException()
    {
        var (handler, requests) = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"));

        await Assert.ThrowsAsync<InvalidPayloadException>(() =>
            handler.HandlePaymentReceived(
                PaymentPayload(period: "onetime", isRecurrent: false, amount: 12345, memberExpiresAt: null),
                CancellationToken.None));

        Assert.Empty(requests);
    }

    [Fact]
    public async Task HandleChargeSuccess_ExtendsToMemberExpiry()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateExistingClientHandler(SampleClient(), requests);

        await handler.HandleChargeSuccess(PaymentPayload(memberExpiresAt: MemberExpiresAt), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        var body = await ReadBodyJson(updateRequest);

        Assert.True(body.GetProperty("enable").GetBoolean());
        Assert.Equal(ExpectedExpiryMs, body.GetProperty("expiryTime").GetInt64());
    }

    [Fact]
    public async Task HandleChargeSuccess_MissingMemberExpiry_ThrowsInvalidPayloadException()
    {
        var (handler, requests) = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"));

        await Assert.ThrowsAsync<InvalidPayloadException>(() =>
            handler.HandleChargeSuccess(PaymentPayload(memberExpiresAt: null), CancellationToken.None));

        Assert.Empty(requests);
    }

    [Fact]
    public async Task HandleRefunded_DisablesClient()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateExistingClientHandler(SampleClient(), requests);

        await handler.HandleRefunded(PaymentPayload(), CancellationToken.None);

        var updateRequest = Assert.Single(requests, r => r.Method == HttpMethod.Post);
        var body = await ReadBodyJson(updateRequest);

        Assert.False(body.GetProperty("enable").GetBoolean());
        Assert.Equal(ExpectedExpiryMs, body.GetProperty("expiryTime").GetInt64());
    }

    [Fact]
    public async Task HandleRefunded_MissingClient_DoesNotThrow()
    {
        var (handler, requests) = CreateHandler(_ =>
            StubHttpMessageHandler.Json(ClientListJson()));

        await handler.HandleRefunded(PaymentPayload(), CancellationToken.None);

        Assert.DoesNotContain(requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task HandleChargeFailed_MakesNoPanelCalls()
    {
        var (handler, requests) = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"));

        await handler.HandleChargeFailed(PaymentPayload(), CancellationToken.None);

        Assert.Empty(requests);
    }

    [Fact]
    public async Task HandleSubscriptionCancelled_MakesNoPanelCalls()
    {
        var (handler, requests) = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"));

        await handler.HandleSubscriptionCancelled(PaymentPayload(), CancellationToken.None);

        Assert.Empty(requests);
    }

    [Fact]
    public async Task HandlePaymentFailed_MakesNoPanelCalls()
    {
        var (handler, requests) = CreateHandler(_ => throw new InvalidOperationException("No HTTP calls expected"));

        await handler.HandlePaymentFailed(PaymentPayload(), CancellationToken.None);

        Assert.Empty(requests);
    }
}