using System.Net;
using System.Text;
using System.Text.Json;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Telegram;
using EagleTunnelApi.Tests.Helpers;
using EagleTunnelApi.TributeShop;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using BotChat = Telegram.Bot.Types.Chat;
using BotMessage = Telegram.Bot.Types.Message;
using BotUpdate = Telegram.Bot.Types.Update;
using BotUser = Telegram.Bot.Types.User;
using BotCallbackQuery = Telegram.Bot.Types.CallbackQuery;
using ChatType = Telegram.Bot.Types.Enums.ChatType;

namespace EagleTunnelApi.Tests.Telegram;

public class TelegramHandlersTests
{
    private const string PanelBaseUri = "https://panel.test";
    private const long TelegramId = 12345;

    private static PanelClient ActiveClient() => new(
        Uuid: "uuid-1",
        Email: "user@example.com",
        Enable: true,
        ExpiryTime: DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds(),
        TgId: TelegramId,
        TotalGB: 100L * 1024 * 1024 * 1024,
        Comment: "comment",
        LimitIp: 2,
        Reset: 0,
        Security: "auto",
        SubId: "sub123",
        Flow: "xtls-rprx-vision",
        Id: 1
    );

    private static PanelClient TestClient(bool enable) => ActiveClient() with { Enable = enable };

    private static string ClientListJson(PanelClient client) =>
        JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientResponse>>(true, "ok",
            new List<PanelClientResponse> { new(client, null, new List<int> { 1 }, 10L * 1024 * 1024 * 1024) }));

    private static string EmptyListJson() =>
        JsonSerializer.Serialize(new PanelApiResponse<List<PanelClientResponse>>(true, "ok",
            new List<PanelClientResponse>()));

    private static string SuccessJson() =>
        JsonSerializer.Serialize(new PanelApiResponse<object>(true, "ok", null));

    private static HttpResponseMessage Json(string rawJson) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(rawJson, Encoding.UTF8, "application/json")
    };

    private static (TelegramHandlers Handler, FakeTelegramBotClient Bot) CreateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> panelResponder,
        Func<HttpRequestMessage, HttpResponseMessage>? shopResponder = null)
    {
        var bot = new FakeTelegramBotClient();

        var telegramOptions = Options.Create(new TelegramOptions
        {
            BotToken = "token",
            SupportUrl = "https://t.me/support",
            DefaultInboundIds = new[] { 1, 2 },
            WebhookPath = "/webhook/telegram"
        });

        var tributeOptions = Options.Create(new TributeOptions
        {
            ApiKey = "tribute-key",
            BaseUri = "https://tribute.test/api/v1"
        });

        var panelClient = new PanelApiClient(
            new HttpClient(new StubHttpMessageHandler(panelResponder)) { BaseAddress = new Uri(PanelBaseUri) },
            NullLogger<PanelApiClient>.Instance);

        var tributeShopClient = new TributeShopClient(
            new HttpClient(new StubHttpMessageHandler(shopResponder ??
                (_ => throw new InvalidOperationException("No shop responder configured"))))
            {
                BaseAddress = new Uri("https://tribute.test/api/v1")
            }, NullLogger<TributeShopClient>.Instance);

        var handler = new TelegramHandlers(new SessionStore(), panelClient, tributeShopClient, telegramOptions,
            tributeOptions, Options.Create(new PanelOptions { BaseUri = PanelBaseUri, ApiKey = "key" }),
            NullLogger<TelegramHandlers>.Instance);

        return (handler, bot);
    }

    private static BotUpdate TextUpdate(string text, BotUser? from = null) => new()
    {
        Message = new BotMessage
        {
            Id = 1,
            Text = text,
            Chat = new BotChat { Id = TelegramId, Type = ChatType.Private },
            From = from ?? new BotUser { Id = TelegramId }
        }
    };

    private static BotUpdate CallbackUpdate(string data) => new()
    {
        CallbackQuery = new BotCallbackQuery
        {
            Id = "cb-1",
            Data = data,
            From = new BotUser { Id = TelegramId },
            Message = new BotMessage
            {
                Id = 10,
                Text = "menu",
                Chat = new BotChat { Id = TelegramId, Type = ChatType.Private }
            }
        }
    };

    [Fact]
    public async Task Start_NewUser_AutoRegisters_WithSanitizedUsernameAndComment()
    {
        var requests = new List<HttpRequestMessage>();
        var getCount = 0;

        var (handler, bot) = CreateHandler(request =>
        {
            requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;

            if (path == $"/admin/panel/api/clients/get/tgId/{TelegramId}")
            {
                getCount++;
                return getCount == 1 ? Json(EmptyListJson()) : Json(ClientListJson(TestClient(enable: false)));
            }

            if (path == "/admin/panel/api/clients/add")
            {
                return Json(SuccessJson());
            }

            if (path == "/admin/panel/api/clients/bulkDisable")
            {
                return Json(SuccessJson());
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var from = new BotUser
        {
            Id = TelegramId,
            FirstName = "John 😀 Doe",
            LastName = "Smith",
            Username = "john_smith"
        };
        await handler.HandleUpdateAsync(bot, TextUpdate("/start", from), CancellationToken.None);

        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        Assert.Contains(requests,
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/bulkDisable");

        var addRequest = requests.Single(r => r.RequestUri!.AbsolutePath == "/admin/panel/api/clients/add");
        var payload = await ReadJson(addRequest);

        var client = payload.GetProperty("client");
        Assert.Equal("JohnDoeSmith", client.GetProperty("email").GetString());
        Assert.False(client.GetProperty("enable").GetBoolean());
        Assert.Equal(300L * 1024 * 1024 * 1024, client.GetProperty("totalGB").GetInt64());
        Assert.Equal(TelegramId, client.GetProperty("tgId").GetInt64());
        Assert.Equal("xtls-rprx-vision", client.GetProperty("flow").GetString());
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("password").GetString()!);
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("subId").GetString()!);
        Assert.Matches("^[a-z0-9]{16}$", client.GetProperty("auth").GetString()!);

        var comment = client.GetProperty("comment").GetString()!;
        Assert.Contains("John 😀 Doe Smith", comment);
        Assert.Contains("@john_smith", comment);
        Assert.Contains($"Telegram ID: {TelegramId}", comment);

        Assert.Equal(new[] { 1, 2 }, payload.GetProperty("inboundIds").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray());

        var menu = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Welcome to Eagle Tunnel Network", menu.Body);
        Assert.Contains("Get VPN", menu.Body);
        Assert.DoesNotContain("start_registration", menu.Body);
    }

    [Fact]
    public async Task Start_ExistingActiveUser_RendersStatusAndMainMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Subscription: Active", send.Body);
        Assert.Contains("Copy VPN Link", send.Body);
        Assert.Contains("How to Connect", send.Body);
    }

    [Fact]
    public async Task Start_ExistingActiveUser_ManageSubscriptionOpensNewPlanMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("\"subscribe\"", send.Body);
        Assert.DoesNotContain("https://tribute", send.Body);

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Subscribe), CancellationToken.None);

        var edit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("plan:monthly"));
        Assert.Contains("plan:onetime", edit.Body);
    }

    [Fact]
    public async Task Start_ExistingDisabledUser_ShowsGetVpnWithoutConnect()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(TestClient(enable: false))));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);

        var send = Assert.Single(bot.Requests, r => r.MethodName == "sendMessage");
        Assert.Contains("Subscription: Not Active", send.Body);
        Assert.Contains("Get VPN", send.Body);
        Assert.DoesNotContain("Copy VPN Link", send.Body);
    }

    [Fact]
    public async Task Support_IncludesPrefillTemplate_WithUsernameAndTelegramId()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Support), CancellationToken.None);

        var edit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Message Support"));
        Assert.Contains("My%20username%3A%20user%40example.com", edit.Body);
        Assert.Contains($"My%20Telegram%20ID%3A%20{TelegramId}", edit.Body);
        Assert.Contains("text=", edit.Body);
    }

    [Fact]
    public async Task Connect_ShowsGuide()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Connect), CancellationToken.None);

        var edit = Assert.Single(bot.Requests, r => r.MethodName == "editMessageText");
        Assert.Contains("How to Connect", edit.Body);
        Assert.Contains("INCY", edit.Body);
        Assert.Contains("Copy VPN Link", edit.Body);

        Assert.Contains(bot.Requests, r => r.MethodName == "answerCallbackQuery");
    }

    [Fact]
    public async Task Subscribe_OpensPlanMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()));

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Subscribe), CancellationToken.None);

        var edit = Assert.Single(bot.Requests, r => r.MethodName == "editMessageText");
        Assert.Contains("plan:weekly", edit.Body);
        Assert.Contains("plan:monthly", edit.Body);
        Assert.Contains("plan:quarterly", edit.Body);
        Assert.Contains("plan:halfyearly", edit.Body);
        Assert.Contains("plan:yearly", edit.Body);
        Assert.Contains("plan:onetime", edit.Body);
    }

    [Fact]
    public async Task PlanPurchase_CreatesOrder_AndShowsPaymentLink()
    {
        var shopRequests = new List<HttpRequestMessage>();

        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()),
            shopResponder: request =>
            {
                shopRequests.Add(request);
                return Json(OrderJson("https://pay.test/order-1"));
            });

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Subscribe), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate("plan:monthly"), CancellationToken.None);

        var orderRequest = Assert.Single(shopRequests);
        var payload = await ReadJson(orderRequest);

        Assert.Equal(30000, payload.GetProperty("amount").GetInt64());
        Assert.Equal("rub", payload.GetProperty("currency").GetString());
        Assert.Equal("monthly", payload.GetProperty("period").GetString());
        Assert.Equal(TelegramId.ToString(), payload.GetProperty("customerId").GetString());
        Assert.Equal(TelegramId.ToString(), payload.GetProperty("comment").GetString());

        var paymentText = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("https://pay.test/order-1"));
        Assert.Contains("Monthly", paymentText.Body);
        Assert.Contains("payment link is ready", paymentText.Body);
    }

    [Fact]
    public async Task PlanPurchase_ShopOrderFails_ShowsErrorMessage()
    {
        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()),
            shopResponder: _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Subscribe), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate("plan:weekly"), CancellationToken.None);

        var errorText = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Failed to create a payment link"));
        Assert.Contains("plan:weekly", errorText.Body);
    }

    [Fact]
    public async Task PlanPurchase_UnknownPlan_ShowsError()
    {
        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()));

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Subscribe), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate("plan:nonsense"), CancellationToken.None);

        var errorText = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains("Unknown plan"));
        Assert.Contains("plan:monthly", errorText.Body);
    }

    [Fact]
    public async Task Back_RestoresMainMenuText_NotJustKeyboard()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Subscribe), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Back), CancellationToken.None);

        var mainMenuEdits = bot.Requests.Where(r => r.MethodName == "editMessageText").ToList();

        var backEdit = mainMenuEdits.Last(r => r.Body.Contains("Subscription: Active"));
        Assert.Contains("Welcome to Eagle Tunnel Network", backEdit.Body);
        Assert.Contains("\"subscribe\"", backEdit.Body);
        Assert.DoesNotContain("Choose a subscription plan", backEdit.Body);
    }

    [Fact]
    public async Task Back_AfterConnectGuide_RestoresMainMenuText()
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Connect), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Back), CancellationToken.None);

        var edits = bot.Requests.Where(r => r.MethodName == "editMessageText").ToList();

        Assert.Contains(edits, r => r.Body.Contains("INCY"));

        var backEdit = edits.Last(r => r.Body.Contains("Subscription: Active"));
        Assert.Contains("Welcome to Eagle Tunnel Network", backEdit.Body);
        Assert.DoesNotContain("INCY", backEdit.Body);
    }

    [Theory]
    [InlineData(MenuService.Subscribe, "\"plan:monthly\"")]
    [InlineData(MenuService.Connect, "INCY")]
    [InlineData(MenuService.Support, "Message Support")]
    public async Task Back_FromAnySubMenu_RestoresMainMenuText(string menuCallback, string subMenuMarker)
    {
        var (handler, bot) = CreateHandler(_ => Json(ClientListJson(ActiveClient())));

        await handler.HandleUpdateAsync(bot, TextUpdate("/start"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(menuCallback), CancellationToken.None);

        var subMenuEdit = Assert.Single(bot.Requests,
            r => r.MethodName == "editMessageText" && r.Body.Contains(subMenuMarker));

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Back), CancellationToken.None);

        var backEdits = bot.Requests.Where(r => r.MethodName == "editMessageText")
            .Where(r => r != subMenuEdit).ToList();

        var backEdit = Assert.Single(backEdits);
        Assert.Contains("Welcome to Eagle Tunnel Network", backEdit.Body);
        Assert.Contains("Subscription: Active", backEdit.Body);
        Assert.DoesNotContain(subMenuMarker, backEdit.Body);
    }

    [Fact]
    public async Task Back_FromPaymentMenu_ReturnsToPlanMenu_NotMainMenu()
    {
        var (handler, bot) = CreateHandler(_ => Json(EmptyListJson()),
            shopResponder: _ => Json(OrderJson("https://pay.test/order-1")));

        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Subscribe), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate("plan:monthly"), CancellationToken.None);
        await handler.HandleUpdateAsync(bot, CallbackUpdate(MenuService.Subscribe), CancellationToken.None);

        var paymentEdits = bot.Requests.Where(r => r.MethodName == "editMessageText").ToList();

        Assert.Contains(paymentEdits, r => r.Body.Contains("payment link is ready"));

        var lastEdit = paymentEdits[^1];
        Assert.Contains("Choose a subscription plan", lastEdit.Body);
        Assert.Contains("\"plan:monthly\"", lastEdit.Body);
        Assert.DoesNotContain("payment link is ready", lastEdit.Body);
    }

    private static string OrderJson(string paymentUrl) => JsonSerializer.Serialize(new ShopOrderResponse(
        Uuid: "order-1",
        ShopId: 1,
        Amount: 30000,
        Currency: "rub",
        Title: "Eagle Tunnel — Monthly",
        Description: "Eagle Tunnel Network VPN · Monthly",
        Status: "paid",
        SuccessUrl: null,
        FailUrl: null,
        PaymentUrl: paymentUrl,
        WebappPaymentUrl: null,
        CreatedAt: DateTime.UtcNow,
        Period: "monthly"
    ));

    private static async Task<JsonElement> ReadJson(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}