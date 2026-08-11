using System.Net;
using System.Text.Json;
using EagleTunnelApi.Tests.Helpers;
using EagleTunnelApi.TributeShop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EagleTunnelApi.Tests.TributeShop;

public class TributeShopClientTests
{
    private const string ApiKey = "shop-secret";

    private static (TributeShopClient Client, List<HttpRequestMessage> Requests) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var requests = new List<HttpRequestMessage>();

        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return responder(request);
        }))
        {
            BaseAddress = new Uri("https://tribute.test/api/v1")
        };

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Api-Key", ApiKey);

        return (new TributeShopClient(httpClient, NullLogger<TributeShopClient>.Instance), requests);
    }

    private static CreateShopOrderRequest SampleRequest(long? shopId = 7) => new(
        ShopId: shopId,
        Amount: 30000,
        Currency: "rub",
        Title: "Eagle Tunnel — Monthly",
        Description: "Eagle Tunnel Network VPN · Monthly",
        SuccessUrl: "https://t.me/success",
        FailUrl: null,
        Comment: "12345",
        CustomerId: "12345",
        Period: "monthly"
    );

    private static string OrderJson(string paymentUrl) => JsonSerializer.Serialize(new ShopOrderResponse(
        Uuid: "order-1",
        ShopId: 7,
        Amount: 30000,
        Currency: "rub",
        Title: "Eagle Tunnel — Monthly",
        Description: "desc",
        Status: "paid",
        SuccessUrl: null,
        FailUrl: null,
        PaymentUrl: paymentUrl,
        WebappPaymentUrl: null,
        CreatedAt: DateTime.UtcNow,
        Period: "monthly"
    ));

    [Fact]
    public async Task CreateOrderAsync_PostsToShopOrders_WithApiKeyAndSnakeCaseBody()
    {
        var (client, requests) = CreateClient(_ =>
            StubHttpMessageHandler.Json(OrderJson("https://pay.test/order-1")));

        var order = await client.CreateOrderAsync(SampleRequest(), CancellationToken.None);

        Assert.NotNull(order);
        Assert.Equal("order-1", order!.Uuid);
        Assert.Equal("https://pay.test/order-1", order.PaymentUrl);

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/shop/orders", request.RequestUri!.AbsolutePath);
        Assert.Equal("tribute.test", request.RequestUri.Host);
        Assert.Equal(ApiKey, Assert.Single(request.Headers.GetValues("Api-Key")));

        var response = await request.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.Equal(7, root.GetProperty("shopId").GetInt64());
        Assert.Equal(30000, root.GetProperty("amount").GetInt64());
        Assert.Equal("rub", root.GetProperty("currency").GetString());
        Assert.Equal("monthly", root.GetProperty("period").GetString());
        Assert.Equal("12345", root.GetProperty("customerId").GetString());
        Assert.Equal("12345", root.GetProperty("comment").GetString());
        Assert.Equal("https://t.me/success", root.GetProperty("successUrl").GetString());
        Assert.False(root.TryGetProperty("failUrl", out _));
    }

    [Fact]
    public async Task CreateOrderAsync_OmitsShopIdWhenNull()
    {
        var (client, requests) = CreateClient(_ =>
            StubHttpMessageHandler.Json(OrderJson("https://pay.test/order-1")));

        await client.CreateOrderAsync(SampleRequest(shopId: null), CancellationToken.None);

        var request = Assert.Single(requests);
        var response = await request.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(response);

        Assert.False(doc.RootElement.TryGetProperty("shopId", out _));
    }

    [Fact]
    public async Task CreateOrderAsync_NonSuccess_ThrowsTributeShopException()
    {
        var (client, _) = CreateClient(_ => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"error_bad_request","message":"bad things"}"""));

        await Assert.ThrowsAsync<TributeShopException>(() =>
            client.CreateOrderAsync(SampleRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateOrderAsync_EmptyResponse_ThrowsTributeShopException()
    {
        var (client, _) = CreateClient(_ => StubHttpMessageHandler.Json("null"));

        await Assert.ThrowsAsync<TributeShopException>(() =>
            client.CreateOrderAsync(SampleRequest(), CancellationToken.None));
    }
}